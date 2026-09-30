using System;
using System.Collections.Generic;
using System.Text.Json;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — orchestration + state + persistence ────────────────────────────────────────────────
//
// Save the dungeon "punctuate" marks the player currently has placed, then reload the whole layout in one click.
// The IL2CPP place/read/clear plumbing is in Plugin.Marks.Interop.cs; the window is in Plugin.Marks.Ui.cs. Wired
// from Plugin.cs (InitMarks/DisposeMarks) and pumped from OnUpdate (TickMarks). Marks are world-anchored absolute
// coords — a preset is only meaningful in the dungeon it was captured in, so each preset is tagged with the scene
// it was saved in (a cheap IClientState.CurrentSceneName read; see the "for dungeon" hint in the UI).
//
// All player-visible status text goes through _loc.T/_loc.TFormat("rm.marks.*") (Rule 10 — RaidManager is
// localized across Lang/{en,ja,th,id,fil}.json); only the developer-facing _services.Log lines stay English.
public sealed partial class Plugin
{
    // Persisted preset shape (System.Text.Json — public props). MarkPos.Slot is the 1..6 marker slot.
    private sealed class MarkPos
    {
        public int Slot { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    private sealed class MarkPreset
    {
        public string Name { get; set; } = "";
        public string Scene { get; set; } = "";              // IClientState.CurrentSceneName at save time ("" if unknown)
        public List<MarkPos> Marks { get; set; } = new();
    }

    private const int MaxPresets = 12;

    private IConfigSection _marksCfg = null!;
    private readonly List<MarkPreset> _presets = new();

    // Load queue: one mark placed per frame (drained in TickMarks) to avoid same-frame multi-cast issues.
    private readonly Queue<MarkPos> _loadQueue = new();

    // Live placed-count readout (FlagSkillState popcount), refreshed on a throttle while in world. -1 = unknown.
    private int _placedCount = -1;
    private double _placedPollTimer;
    private const double PlacedPollInterval = 0.5;

    // Name buffer for the "save as" input field.
    private string _newPresetName = "";
    // Transient status line shown in the window.
    private string _marksStatus = "";

    private IWindowControl _marksWindow = null!;
    private IDisposable _marksLauncher = null!;

    private void InitMarks()
    {
        _marksCfg = _services.Config.GetSection("marks");
        LoadPresetsFromConfig();
        RegisterMarksWindow();   // Plugin.Marks.Ui.cs
        _services.Log.Info($"[MarkPresets] initialized ({_presets.Count} preset(s) loaded)");
    }

    private void DisposeMarks()
    {
        _marksLauncher?.Dispose();
        _marksWindow?.Remove();
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
                _marksStatus = _loc.T("rm.marks.presetPlaced");
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

    // ── Preset operations ────────────────────────────────────────────────────────
    private void SaveCurrentAsPreset()
    {
        var marks = MkReadOwnMarks();
        if (marks.Count == 0)
        {
            _marksStatus = _loc.T("rm.marks.noMarks");
            _marksWindow?.MarkDirty();
            return;
        }

        string name = _newPresetName.Trim();
        if (name.Length == 0) name = _loc.TFormat("rm.marks.autoName", _presets.Count + 1);

        string scene = "";
        try { scene = _services.ClientState.CurrentSceneName ?? ""; } catch { }

        // Overwrite a same-named preset in place; otherwise append (capped).
        var existing = _presets.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var preset = new MarkPreset { Name = name, Scene = scene, Marks = marks };
        if (existing >= 0) _presets[existing] = preset;
        else
        {
            if (_presets.Count >= MaxPresets)
            {
                _marksStatus = _loc.TFormat("rm.marks.limit", MaxPresets);
                _marksWindow?.MarkDirty();
                return;
            }
            _presets.Add(preset);
        }

        _newPresetName = "";
        SavePresetsToConfig();
        _marksStatus = _loc.TFormat("rm.marks.saved", name, marks.Count);
        _marksWindow?.MarkDirty();
        _services.Log.Info($"[MarkPresets] saved '{name}' with {marks.Count} marks (scene='{scene}')");
    }

    private void LoadPreset(int index)
    {
        if (index < 0 || index >= _presets.Count) return;
        var preset = _presets[index];

        // Clear existing marks first, then queue the saved layout (one placed per frame).
        MkClearMarks();
        _loadQueue.Clear();
        foreach (var m in preset.Marks) _loadQueue.Enqueue(m);

        _marksStatus = _loc.TFormat("rm.marks.loading", preset.Name, preset.Marks.Count);
        _marksWindow?.MarkDirty();
        _services.Log.Info($"[MarkPresets] loading '{preset.Name}' ({preset.Marks.Count} marks)");
    }

    private void DeletePreset(int index)
    {
        if (index < 0 || index >= _presets.Count) return;
        string name = _presets[index].Name;
        _presets.RemoveAt(index);
        SavePresetsToConfig();
        _marksStatus = _loc.TFormat("rm.marks.deleted", name);
        _marksWindow?.MarkDirty();
    }

    private void ClearMarksNow()
    {
        MkClearMarks();
        _loadQueue.Clear();
        _marksStatus = _loc.T("rm.marks.cleared");
        _marksWindow?.MarkDirty();
    }

    // ── Persistence (presets serialized as a JSON string in the 'marks' config section) ──────────────────────────
    private void LoadPresetsFromConfig()
    {
        _presets.Clear();
        try
        {
            string json = _marksCfg.Get<string>("presets", "") ?? "";
            if (json.Length == 0) return;
            var list = JsonSerializer.Deserialize<List<MarkPreset>>(json);
            if (list != null) _presets.AddRange(list);
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] preset load failed: {ex.Message}"); }
    }

    private void SavePresetsToConfig()
    {
        try
        {
            _marksCfg.Set<string>("presets", JsonSerializer.Serialize(_presets));
            _marksCfg.Save();
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] preset save failed: {ex.Message}"); }
    }
}
