using System;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — inline rename ──────────────────────────────────────────────────────────────────────
//
// A preset's name is a LABEL by default; CLICKING the name turns THAT row into an input field (house redesign
// 2026-11-12 — the pencil / check chips are gone), and Enter commits it back to a label. Clicking another name moves
// the edit there without saving. The row cells themselves are built in Plugin.Marks.Ui.cs; this partial owns the edit
// state + commit logic.
//
// The active preset is tracked by INDEX at runtime and persisted by NAME (SavePresetsToConfig writes
// ActivePreset()?.Name). A rename mutates the name in place without moving the preset, so _activeIndex still points
// at it and the save right after the rename writes the NEW name as "active" — it stays active across a relaunch.
public sealed partial class Plugin
{
    // _editBuffer is kept live by the input's OnChange (the framework's InputElement submits on ENTER ONLY — losing
    // focus does NOT submit).
    // _editingIdx is a LIST INDEX, so anything that shifts indices (DeletePreset / CreatePreset) resets it to -1.
    private int _editingIdx = -1;
    private string _editBuffer = "";

    private bool IsEditing(int idx) => _editingIdx == idx;

    // Turn the row's name into an editable field, seeded with the current name.
    private void EnterEdit(int idx)
    {
        if (idx < 0 || idx >= _presets.Count) return;
        _editingIdx = idx;
        _editBuffer = _presets[idx].Name;
        _renameFocusSeq++;   // focus the field as it appears (InputElement.FocusRequest, Plugin.Marks.Ui.cs)
        _marksWindow?.MarkDirty();
    }

    // Commit the edited name (from the live _editBuffer) — the input's Enter Submit. Empty / unchanged → just leave
    // edit mode. A name clash with ANOTHER preset (case-insensitive, matching CreatePreset's rule) shows the dupName
    // status and STAYS in edit mode so the user can fix the text instead of losing it.
    private void CommitRename(int idx)
    {
        if (idx < 0 || idx >= _presets.Count) { _editingIdx = -1; _marksWindow?.MarkDirty(); return; }

        string name = _editBuffer?.Trim() ?? "";
        string old = _presets[idx].Name;
        if (name.Length > 0 && !string.Equals(name, old, StringComparison.Ordinal))
        {
            for (int i = 0; i < _presets.Count; i++)
            {
                if (i != idx && string.Equals(_presets[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    SetMarksStatus(_loc.TFormat("rm.marks.dupName", name), ok: false);
                    _marksWindow?.MarkDirty();
                    return;
                }
            }

            _presets[idx].Name = name;
            if (idx == _exportPresetIdx) ClearExportCode();   // the code embeds the old name
            SavePresetsToConfig();  // also re-writes the active name if this preset is the active one
            SetMarksStatus(_loc.TFormat("rm.marks.renamed", old, name));
            _services.Log.Info($"[MarkPresets] renamed '{old}' -> '{name}'");
        }

        _editingIdx = -1;
        _marksWindow?.MarkDirty();
    }
}
