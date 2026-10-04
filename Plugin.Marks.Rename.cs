using System;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — inline rename ──────────────────────────────────────────────────────────────────────
//
// Same UX as StellarWardrobeLoadout's outfit rename: a preset's name is a LABEL by default; the pencil chip turns
// THAT row's name cell into an input field, and the check chip (or Enter) commits it back to a label. The row cells
// themselves are built in Plugin.Marks.Ui.cs (BuildMarksRoot); this partial owns the edit state + commit logic.
//
// The active preset is tracked by INDEX at runtime and persisted by NAME (SavePresetsToConfig writes
// ActivePreset()?.Name). A rename mutates the name in place without moving the preset, so _activeIndex still points
// at it and the save right after the rename writes the NEW name as "active" — it stays active across a relaunch.
public sealed partial class Plugin
{
    // _editBuffer is kept live by the input's OnChange so the check chip can read the typed value without an Enter
    // first: the framework's InputElement submits on ENTER ONLY — losing focus does NOT submit.
    // _editingIdx is a LIST INDEX, so anything that shifts indices (DeletePreset / CreatePreset) resets it to -1.
    private int _editingIdx = -1;
    private string _editBuffer = "";

    // Pencil / check chip icons, copied from StellarWardrobeLoadout/Icons and embedded with an explicit LogicalName
    // (see Stellar.RaidManager.csproj). null if the resource is missing → the button just renders blank.
    private static readonly byte[]? EditIconPng = LoadEmbeddedIcon("Stellar.RaidManager.Icons.edit.png");
    private static readonly byte[]? SaveIconPng = LoadEmbeddedIcon("Stellar.RaidManager.Icons.save.png");

    private bool IsEditing(int idx) => _editingIdx == idx;

    // Turn the row's name into an editable field, seeded with the current name.
    private void EnterEdit(int idx)
    {
        if (idx < 0 || idx >= _presets.Count) return;
        _editingIdx = idx;
        _editBuffer = _presets[idx].Name;
        _marksWindow?.MarkDirty();
    }

    // Commit the edited name (from the live _editBuffer). Called by the check chip AND the input's Enter Submit —
    // both go through here so they can't disagree. Empty / unchanged → just leave edit mode. A name clash with
    // ANOTHER preset (case-insensitive, matching CreatePreset's rule) shows the dupName status and STAYS in edit
    // mode so the user can fix the text instead of losing it.
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
                    _marksStatus = _loc.TFormat("rm.marks.dupName", name);
                    _marksWindow?.MarkDirty();
                    return;
                }
            }

            _presets[idx].Name = name;
            SavePresetsToConfig();   // also re-writes the active name if this preset is the active one
            _marksStatus = _loc.TFormat("rm.marks.renamed", old, name);
            _services.Log.Info($"[MarkPresets] renamed '{old}' -> '{name}'");
        }

        _editingIdx = -1;
        _marksWindow?.MarkDirty();
    }

    private static byte[]? LoadEmbeddedIcon(string name)
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream(name);
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
