using System;
using System.Collections.Generic;
using System.Text.Json;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — orchestration + state + persistence ────────────────────────────────────────────────
//
// A preset is a STEPPED SEQUENCE: an ordered list of mark layouts ("steps"). Each step is the whole set of
// punctuate marks the player had placed when it was captured. During a fight you activate one preset, then walk
// its steps forward/back (Prev / Next / Reset) — each move clears the board and re-places that step's marks.
//
// Steps[0] is ALWAYS a permanent blank "Start" anchor (0 marks, undeletable); real saved layouts are steps 1..N.
// Applying Step 0 clears the board and places nothing (Reset, or stepping back to it). New presets seed Steps with
// one empty step; on load, any preset whose Steps[0] has marks (or is empty) gets a blank step prepended (migration
// for presets saved before the anchor existed).
//
// Multiple presets are stored; exactly one is active at a time, and the user activates it by hand (no scene/dungeon
// auto-select — marks are absolute world coords, so a step only lines up in the dungeon it was captured in, and the
// UI just says so). Runtime state = the active preset + a current-step cursor (0 = the blank Start anchor). Persisted:
// the presets list AND the active preset's name; the step cursor is deliberately NOT persisted (resets to 0 on load).
//
// The IL2CPP place/read/clear plumbing is in Plugin.Marks.Interop.cs (reused verbatim); the window is in
// Plugin.Marks.Ui.cs. Wired from Plugin.cs (InitMarks/DisposeMarks) and pumped from OnUpdate (TickMarks).
//
// All player-visible status text goes through _loc.T/_loc.TFormat("rm.marks.*") (Rule 10 — RaidManager is
// localized across Lang/{en,ja,th,id,fil}.json); only the developer-facing _services.Log lines stay English.
public sealed partial class Plugin
{
    // Persisted shapes (System.Text.Json — public props). MarkPos.Slot is the 1..6 marker slot.
    private sealed class MarkPos
    {
        public int Slot { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    // One saved layout in a sequence: the full set of marks captured together.
    private sealed class MarkStep
    {
        public List<MarkPos> Marks { get; set; } = new();
        // Optional free-text note (Plugin.Marks.StepComment.cs). Absent in old configs → stays "".
        public string Comment { get; set; } = "";
    }

    private sealed class MarkPreset
    {
        public string Name { get; set; } = "";
        public List<MarkStep> Steps { get; set; } = new();
    }

    private const int MaxPresets = 24;   // safety cap on config growth; not a user-facing feature

    private IConfigSection _marksCfg = null!;
    private readonly List<MarkPreset> _presets = new();

    // Active preset (index into _presets, -1 = none) + step cursor (-1 = nothing applied yet). Neither the cursor
    // nor _activeIndex directly is persisted — the active preset's NAME is, and we resolve it back on load.
    private int _activeIndex = -1;
    private int _currentStep = -1;

    // Load queue: one mark placed per frame (drained in TickMarks) to avoid same-frame multi-cast issues.
    private readonly Queue<MarkPos> _loadQueue = new();

    // Live placed-count readout (FlagSkillState popcount), refreshed on a throttle while in world. -1 = unknown.
    private int _placedCount = -1;
    private double _placedPollTimer;
    private const double PlacedPollInterval = 0.5;

    // Name buffer for the "create preset" input field.
    private string _newPresetName = "";
    // Transient status line shown in the window.
    private string _marksStatus = "";

    private IWindowControl _marksWindow = null!;

    // Step-navigation hotkeys (fire regardless of window visibility). Declared UNBOUND (SuggestedDefault=null) so
    // they surface in the hotkey UI with their labels but claim no key until the user assigns one themselves.
    private IHotkeyAction _prevHotkey = null!;
    private IHotkeyAction _resetHotkey = null!;
    private IHotkeyAction _nextHotkey = null!;

    private MarkPreset? ActivePreset()
        => _activeIndex >= 0 && _activeIndex < _presets.Count ? _presets[_activeIndex] : null;

    private void InitMarks()
    {
        _marksCfg = _services.Config.GetSection("marks");
        _trashPng = BuildTrashIconPng();   // Plugin.TrashIcon.cs — per-row Delete button icon
        LoadPresetsFromConfig();
        RegisterMarksWindow();   // Plugin.Marks.Ui.cs

        // Step navigation from the keyboard — usable mid-fight without opening the window. Each op guards on
        // "no active preset" itself (see PrevStep/ResetSteps/NextStep), so a stray press just shows an error tip.
        _prevHotkey = _services.Hotkeys.DeclareAction(
            new HotkeyAction("raidmanager.marks.prev", "Mark Presets: Previous step",
                null), PrevStep);
        _resetHotkey = _services.Hotkeys.DeclareAction(
            new HotkeyAction("raidmanager.marks.reset", "Mark Presets: Reset to Start",
                null), ResetSteps);
        _nextHotkey = _services.Hotkeys.DeclareAction(
            new HotkeyAction("raidmanager.marks.next", "Mark Presets: Next step",
                null), NextStep);

        _services.Log.Info($"[MarkPresets] initialized ({_presets.Count} preset(s) loaded, active={_activeIndex})");
    }

    private void DisposeMarks()
    {
        _prevHotkey?.Dispose();
        _resetHotkey?.Dispose();
        _nextHotkey?.Dispose();
        _marksWindow?.Remove();
    }

    // ── NoticeTip feedback (subtle, on-screen — mirrors the window status line) ────────────────────────────────────
    // These fire often (Activate + every Prev/Next/Reset), so keep them short and SILENT — deliberately NOT the
    // Special banner + DungeonVictory sound the /rw path uses. Fully guarded: a notice failure must never break
    // navigation or crash the game.
    private void ShowMarksNotice(string content)
    {
        try
        {
            _services.NoticeTips
                .Create(NoticeTipType.PopTip)       // neutral center pop-tip (not the /rw Special banner)
                .WithContent(content)
                .WithAudio(NoticeTipAudio.Silent)   // silent — nav tips fire too often to sound
                .WithDuration(2.0f)
                .Show();
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] notice tip failed: {ex.Message}"); }
    }

    // Error variant — used when a step hotkey is pressed with no preset active (a rare misuse, so a short center
    // pop-tip with a light error cue is warranted). Still fully guarded.
    private void ShowMarksError(string content)
    {
        try
        {
            _services.NoticeTips
                .Create(NoticeTipType.PopTip)       // same neutral pop-tip; audible error cue kept below
                .WithContent(content)
                .WithAudio(NoticeTipAudio.ErrorTip)
                .WithDuration(2.5f)
                .Show();
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] notice tip failed: {ex.Message}"); }
    }

    // Localized "resulting step" line for a nav op. Step 0 (the blank anchor) reads "Start"; a real step k reads
    // "Step k / N" where N == the real step count (Steps[0] is the anchor, so N = Steps.Count - 1).
    // A non-empty step comment is appended (notice.stepComment) so it shows mid-fight from the hotkeys too.
    private string MarksStepNotice(MarkPreset p)
    {
        if (_currentStep <= 0) return _loc.TFormat("rm.marks.notice.start", p.Name);
        string c = _currentStep < p.Steps.Count ? p.Steps[_currentStep].Comment ?? "" : "";
        return c.Length > 0
            ? _loc.TFormat("rm.marks.notice.stepComment", p.Name, _currentStep, p.Steps.Count - 1, c)
            : _loc.TFormat("rm.marks.notice.step", p.Name, _currentStep, p.Steps.Count - 1);
    }

    // Pumped from OnUpdate (Plugin.Logic.cs). Drains the load queue one mark per frame — Framework.Update runs on
    // the main thread before LateUpdate, which is exactly the timing the indicatorPos_ write needs.
    private void TickMarks(float deltaTime)
    {
        if (_loadQueue.Count > 0)
        {
            var m = _loadQueue.Dequeue();
            var target = new UnityEngine.Vector3(m.X, m.Y, m.Z);
            MkPlaceAt(m.Slot, target);
            if (_loadQueue.Count == 0)
            {
                _marksStatus = _loc.T("rm.marks.stepPlaced");
                _marksWindow?.MarkDirty();
            }
        }

        // Throttled live placed-count poll (cheap Lua bitmask read) while in world.
        if (_services.ClientState.Phase == GamePhase.World)
        {
            _placedPollTimer += deltaTime;
            if (_placedPollTimer >= PlacedPollInterval)
            {
                _placedPollTimer = 0;
                long flag = MkReadFlagStateViaLua();
                int c = flag > 0 ? MkPopCount(flag) : (flag == 0 ? 0 : -1);
                if (c != _placedCount) { _placedCount = c; _marksWindow?.MarkDirty(); }
            }
        }
    }

    // ── Preset-level operations ──────────────────────────────────────────────────
    private void CreatePreset()
    {
        string name = _newPresetName.Trim();
        if (name.Length == 0) name = _loc.TFormat("rm.marks.autoName", _presets.Count + 1);

        if (_presets.Exists(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _marksStatus = _loc.TFormat("rm.marks.dupName", name);
            _marksWindow?.MarkDirty();
            return;
        }
        if (_presets.Count >= MaxPresets)
        {
            _marksStatus = _loc.TFormat("rm.marks.limit", MaxPresets);
            _marksWindow?.MarkDirty();
            return;
        }

        // Every preset owns a permanent blank Step 0 (the "Start" anchor); real layouts are appended as steps 1..N.
        _presets.Add(new MarkPreset { Name = name, Steps = { new MarkStep() } });
        _editingIdx = -1;   // list changed — drop any in-progress rename (it is keyed by index)
        CancelStepCommentEdit();
        _newPresetName = "";
        SavePresetsToConfig();
        _marksStatus = _loc.TFormat("rm.marks.created", name);
        _marksWindow?.MarkDirty();
        _services.Log.Info($"[MarkPresets] created '{name}'");
    }

    // Activate = make this the working preset; reset the cursor to "nothing applied yet" (do NOT auto-place).
    private void ActivatePreset(int index)
    {
        if (index < 0 || index >= _presets.Count) return;
        CancelStepCommentEdit();   // preset switch — a step-comment edit belongs to the old preset
        ClearExportCode();         // share code belongs to the old preset (Plugin.Marks.Share.cs)
        _activeIndex = index;
        _currentStep = 0;   // land on the blank Start anchor; pure selection — do NOT apply/place anything
        SavePresetsToConfig();   // also persists the active-preset name
        _marksStatus = _loc.TFormat("rm.marks.activated", _presets[index].Name);
        _marksWindow?.MarkDirty();
        ShowMarksNotice(_loc.TFormat("rm.marks.notice.activated", _presets[index].Name));
    }

    // Deactivate = clear the active selection entirely (no preset active). Mirror of ActivatePreset: reset the
    // cursor to "nothing applied yet" and persist the cleared active name so it stays deactivated across relaunch
    // (SavePresetsToConfig writes ActivePreset()?.Name ?? "" → "" once _activeIndex is -1). Deliberately does NOT
    // touch the board — marks placed in-world stay put; this only drops the step cursor.
    private void DeactivatePreset()
    {
        var p = ActivePreset();
        if (p == null) return;          // nothing active — guard (button only shows "Deactivate" when one is active)
        string name = p.Name;
        CancelStepCommentEdit();
        ClearExportCode();
        _activeIndex = -1;
        _currentStep = -1;              // nothing applied yet (matches the no-active-preset load state)
        SavePresetsToConfig();          // persists the cleared active-preset name (stays deactivated on relaunch)
        _marksStatus = _loc.TFormat("rm.marks.notice.deactivated", name);
        _marksWindow?.MarkDirty();
        ShowMarksNotice(_loc.TFormat("rm.marks.notice.deactivated", name));
    }

    private void DeletePreset(int index)
    {
        if (index < 0 || index >= _presets.Count) return;
        string name = _presets[index].Name;
        _presets.RemoveAt(index);
        _editingIdx = -1;   // indices shifted — an in-progress rename would now point at the wrong row
        CancelStepCommentEdit();
        ClearExportCode();  // _exportPresetIdx is an index too — shifted/removed along with the list

        // Keep _activeIndex pointing at the same preset it did before (or clear it if that one was removed).
        if (_activeIndex == index) { _activeIndex = -1; _currentStep = -1; }
        else if (_activeIndex > index) _activeIndex--;

        SavePresetsToConfig();
        _marksStatus = _loc.TFormat("rm.marks.deleted", name);
        _marksWindow?.MarkDirty();
    }

    // ── Step operations (within the ACTIVE preset) ───────────────────────────────
    // Save Step = capture whatever marks are placed right now and APPEND as a new step (option A: always append).
    private void SaveStep()
    {
        var p = ActivePreset();
        if (p == null) return;

        var marks = MkReadOwnMarks();
        if (marks.Count == 0)
        {
            _marksStatus = _loc.T("rm.marks.noMarks");
            _marksWindow?.MarkDirty();
            return;
        }

        CancelStepCommentEdit();   // cursor is about to jump to the new step
        ClearExportCode();         // new step → any exported code is stale
        // Always append (never overwrite Step 0). New index >= 1 == the real step number (Steps[0] is the anchor).
        p.Steps.Add(new MarkStep { Marks = marks });
        _currentStep = p.Steps.Count - 1;   // cursor lands on the freshly-added step
        SavePresetsToConfig();
        _marksStatus = _loc.TFormat("rm.marks.stepSaved", _currentStep, marks.Count);
        _marksWindow?.MarkDirty();
        _services.Log.Info($"[MarkPresets] '{p.Name}' saved step {_currentStep} with {marks.Count} marks");
    }

    private void NextStep()
    {
        var p = ActivePreset();
        if (p == null) { ShowMarksError(_loc.T("rm.marks.notice.noActive")); return; }
        CancelStepCommentEdit();
        // Advance 0→1→…→N (N == last real step); Steps[0] is the anchor so Steps is never empty.
        if (_currentStep < p.Steps.Count - 1) { _currentStep++; ApplyStep(_currentStep); }
        // Tip reflects the RESULTING step even when clamped at the end (just re-shows the current step).
        ShowMarksNotice(MarksStepNotice(p));
    }

    private void PrevStep()
    {
        var p = ActivePreset();
        if (p == null) { ShowMarksError(_loc.T("rm.marks.notice.noActive")); return; }
        CancelStepCommentEdit();
        // Step back toward 0; landing on Step 0 (the blank anchor) clears the board.
        if (_currentStep > 0) { _currentStep--; ApplyStep(_currentStep); }
        // Tip reflects the RESULTING step even when clamped at Start (just re-shows Step 0).
        ShowMarksNotice(MarksStepNotice(p));
    }

    private void ResetSteps()
    {
        var p = ActivePreset();
        if (p == null) { ShowMarksError(_loc.T("rm.marks.notice.noActive")); return; }
        CancelStepCommentEdit();
        // Reset = go to the blank Start anchor and apply it → clears the board (Steps[0] is always empty).
        _currentStep = 0;
        ApplyStep(0);
        ShowMarksNotice(MarksStepNotice(p));
    }

    private void DeleteCurrentStep()
    {
        var p = ActivePreset();
        if (p == null) return;
        if (_currentStep <= 0)                       // Step 0 is the permanent blank anchor — never deletable
        {
            _marksStatus = _loc.T("rm.marks.stepZeroProtected");
            _marksWindow?.MarkDirty();
            return;
        }
        if (_currentStep >= p.Steps.Count) return;   // out of range (shouldn't happen) — nothing to delete

        CancelStepCommentEdit();
        ClearExportCode();                           // step removed → any exported code is stale
        int shown = _currentStep;                   // real step number == index (Steps[0] is the anchor)
        p.Steps.RemoveAt(_currentStep);
        if (_currentStep >= p.Steps.Count) _currentStep = p.Steps.Count - 1;   // clamp; falls back to 0 (anchor)

        SavePresetsToConfig();
        _marksStatus = _loc.TFormat("rm.marks.stepDeleted", shown);            // do NOT auto-apply after a delete
        _marksWindow?.MarkDirty();
    }

    // Apply a step = clear the whole board, then queue this step's marks (one placed per frame — the validated path).
    private void ApplyStep(int i)
    {
        var p = ActivePreset();
        if (p == null || i < 0 || i >= p.Steps.Count) return;
        var step = p.Steps[i];

        // Applying always clears the board first. Step 0 is the blank anchor → clears and places nothing.
        MkClearMarks();
        _loadQueue.Clear();
        foreach (var m in step.Marks) _loadQueue.Enqueue(m);

        if (i == 0)
        {
            _marksStatus = _loc.T("rm.marks.clearedToStart");
            _marksWindow?.MarkDirty();
            _services.Log.Info($"[MarkPresets] '{p.Name}' cleared to Start (step 0)");
            return;
        }

        _marksStatus = _loc.TFormat("rm.marks.applying", i, step.Marks.Count);   // real step number == index
        _marksWindow?.MarkDirty();
        _services.Log.Info($"[MarkPresets] applying '{p.Name}' step {i} ({step.Marks.Count} marks)");
    }

    // ── Persistence (presets JSON + active-preset name, both in the 'marks' config section) ───────────────────────
    private void LoadPresetsFromConfig()
    {
        _presets.Clear();
        _activeIndex = -1;
        _currentStep = -1;
        try
        {
            string json = _marksCfg.Get<string>("presets", "") ?? "";
            if (json.Length > 0)
            {
                var list = JsonSerializer.Deserialize<List<MarkPreset>>(json);
                if (list != null) _presets.AddRange(list);
            }

            // Migration: every preset must own a blank Step 0 anchor. Old presets (saved before this feature) have
            // real layouts starting at Steps[0] — prepend an empty step so their first layout isn't mistaken for it.
            foreach (var p in _presets)
                if (p.Steps.Count == 0 || p.Steps[0].Marks.Count > 0)
                    p.Steps.Insert(0, new MarkStep());

            // A step with an explicit JSON null comment would otherwise carry null past the initializer.
            foreach (var p in _presets)
                foreach (var st in p.Steps)
                    st.Comment ??= "";

            // Resolve the persisted active name back to an index; the cursor lands on Step 0 (the blank anchor).
            string active = _marksCfg.Get<string>("active", "") ?? "";
            if (active.Length > 0)
                _activeIndex = _presets.FindIndex(p => string.Equals(p.Name, active, StringComparison.OrdinalIgnoreCase));
            _currentStep = _activeIndex >= 0 ? 0 : -1;
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] preset load failed: {ex.Message}"); }
    }

    private void SavePresetsToConfig()
    {
        try
        {
            _marksCfg.Set<string>("presets", JsonSerializer.Serialize(_presets));
            _marksCfg.Set<string>("active", ActivePreset()?.Name ?? "");
            _marksCfg.Save();
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] preset save failed: {ex.Message}"); }
    }
}
