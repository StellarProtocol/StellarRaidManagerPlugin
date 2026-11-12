using System;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — list editing: footer feedback, delete confirm, drag-to-reorder ─────────────────────────
//
// FOOTER (house rule 13): every action reports in ONE fixed-height line at the bottom of the window for ~3 s — green
// "✓ <message>" on success, amber on an error (the existing rm.marks.* messages). The line is composed once when set
// (no per-poll string building) and cleared by TickMarksFooter.
//
// DELETE CONFIRM (new in the 2026-11-12 redesign): a row's ✕ no longer deletes at once — it opens the confirm panel in
// the STEPS slot ("Delete “X”? Its N steps are removed. Export it first to keep a copy." + Danger Delete / Neutral
// Cancel), because it removes a whole preset. Keyed by LIST INDEX like the rename, so anything that shifts indices
// (create / delete / reorder) resets or remaps it.
//
// REORDER (new): drag a row (ListElement.OnReorder, fw 2.30.0 — remove-then-insert indices); the new order is saved
// with the presets JSON. Every index-keyed state follows its preset (active, export, confirm); a rename in progress is
// dropped instead (CanReorder blocks the drag while renaming or confirming anyway).
public sealed partial class Plugin
{
    private const float FooterSeconds = 3f;

    private string _marksFooter = "";
    private bool _marksFooterOk = true;
    private float _marksFooterUntil;

    private void SetMarksStatus(string text, bool ok = true)
    {
        _marksFooter = ok ? "✓ " + text : text;
        _marksFooterOk = ok;
        _marksFooterUntil = _services.Framework.TimeNow + FooterSeconds;
        _marksWindow?.MarkDirty();
    }

    // From TickMarks. O(1); a single compare while nothing is showing.
    private void TickMarksFooter()
    {
        if (_marksFooter.Length == 0 || _services.Framework.TimeNow < _marksFooterUntil) return;
        _marksFooter = "";
        _marksWindow?.MarkDirty();
    }

    // ── delete confirm ────────────────────────────────────────────────────────────────────────────────────────────
    private int _pendingDeleteIdx = -1;

    private bool ConfirmingDelete => _pendingDeleteIdx >= 0 && _pendingDeleteIdx < _presets.Count;

    private void AskDeletePreset(int idx)
    {
        if (idx < 0 || idx >= _presets.Count) return;
        _pendingDeleteIdx = idx;
        _marksWindow?.MarkDirty();
    }

    private void ConfirmDeletePreset()
    {
        int idx = _pendingDeleteIdx;
        _pendingDeleteIdx = -1;
        DeletePreset(idx);    // resets the confirm + reports in the footer
        _marksWindow?.MarkDirty();
    }

    private void CancelDeletePreset()
    {
        _pendingDeleteIdx = -1;
        _marksWindow?.MarkDirty();
    }

    // "Delete “X”?" / "Its N steps are removed. …" — re-made only when the preset, its step count or the language
    // changes (polled every refresh while the panel is up).
    private object? _confirmFor;
    private string? _confirmName;
    private int _confirmSteps = -1;
    private string? _confirmLang;
    private string _confirmTitle = "", _confirmBody = "";

    private void RefreshConfirmText()
    {
        if (!ConfirmingDelete) return;
        var p = _presets[_pendingDeleteIdx];
        int steps = p.Steps.Count - 1;   // Steps[0] is the blank Start anchor
        var lang = _loc.Language;
        if (ReferenceEquals(p, _confirmFor) && ReferenceEquals(p.Name, _confirmName) && steps == _confirmSteps
            && string.Equals(lang, _confirmLang, StringComparison.Ordinal)) return;
        _confirmFor = p; _confirmName = p.Name; _confirmSteps = steps; _confirmLang = lang;
        _confirmTitle = _loc.TFormat("rm.marks.confirm.title", p.Name);
        _confirmBody = _loc.TFormat("rm.marks.confirm.body", steps);
    }

    private string ConfirmTitle() { RefreshConfirmText(); return _confirmTitle; }
    private string ConfirmBody()  { RefreshConfirmText(); return _confirmBody; }

    // ── drag-to-reorder ───────────────────────────────────────────────────────────────────────────────────────────
    private bool CanReorderPresets(int _) => _editingIdx < 0 && !ConfirmingDelete;

    private void MovePreset(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= _presets.Count || to >= _presets.Count) return;
        var p = _presets[from];
        _presets.RemoveAt(from);
        _presets.Insert(to, p);

        _activeIndex      = Remap(_activeIndex, from, to);
        _exportPresetIdx  = Remap(_exportPresetIdx, from, to);
        _pendingDeleteIdx = Remap(_pendingDeleteIdx, from, to);
        _editingIdx = -1;

        SavePresetsToConfig();   // the order IS the JSON list order; the active name is unchanged
        _marksWindow?.MarkDirty();
        _services.Log.Info($"[MarkPresets] moved '{p.Name}' {from} -> {to}");
    }

    // Where index `i` lands after a remove-at-`from` / insert-at-`to` move (−1 stays −1).
    private static int Remap(int i, int from, int to)
    {
        if (i < 0) return i;
        if (i == from) return to;
        if (from < to && i > from && i <= to) return i - 1;
        if (to < from && i >= to && i < from) return i + 1;
        return i;
    }
}
