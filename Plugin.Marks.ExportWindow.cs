using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — export (copy) window ─────────────────────────────────────────────────────────────────
//
// Opened by the "Export" pill in the STEPS panel, centred on that pill and in front (WindowCenterer.OpenCentred, house
// redesign 2026-11-12) — the mirror of the import window (Plugin.Marks.ImportWindow.cs, which also owns CentreOfButton).
// Opening GENERATES the code for the active preset (ExportActivePreset, Plugin.Marks.Share.cs); if encoding fails the
// export-failed status shows in the marks window's footer and this window does not open.
//
// House header (32 px "share" tile, "Share “X”", "N steps · N characters"), the full code in a READ-ONLY TextAreaElement
// (selectable / copyable by hand, not editable), then [footer "✓ Copied" | Copy].
//
// Staleness: the code is a snapshot. Everything that would change it (switching / deactivating / deleting / renaming
// the preset, saving or deleting a step, editing a comment) calls ClearExportCode(), which also CLOSES this window —
// the user re-opens it to get a fresh code. This window only ever shows a code for the preset it was opened on.
public sealed partial class Plugin
{
    private IWindowControl _exportWindow = null!;
    private WindowCenterer _exportCenter = null!;
    private string _exportStatus = "";
    private bool _exportStatusOk = true;

    private void RegisterExportWindow()
    {
        _exportWindow = _services.Windows.Register(new WindowRegistration(
            Spec: ToolSpec("stellar-raid-manager.marks.export", "rm.marks.exportTitle", new WindowRect(560f, 340f, PopupWidth, 0f)),
            Root: BuildExportRoot(),
            OnClose: () => _exportWindow.SetVisible(false)));
        _exportCenter = new WindowCenterer(_services.Framework, _exportWindow, WindowCenterer.CanvasCentre);
    }

    // "Export" → OnClickWithRect (`btn` in screen pixels). Re-clicking while open regenerates the code for the (same,
    // still-active) preset and re-centres it.
    private void OpenExportWindowAt(WindowRect btn)
    {
        ExportActivePreset();
        if (!HasExportCode()) return;   // no active preset, or encode failed (status already in the marks footer)

        SetExportStatus("", ok: true);
        _exportCenter.OpenCentred(CentreOfButton(btn));
        _exportWindow.MarkDirty();
    }

    private void CloseExportWindow()
    {
        if (_exportWindow != null && _exportWindow.IsShown) _exportWindow.SetVisible(false);
    }

    private void SetExportStatus(string text, bool ok)
    {
        _exportStatus = ok && text.Length > 0 ? "✓ " + text : text;
        _exportStatusOk = ok;
        _exportWindow?.MarkDirty();
    }

    private string ExportPresetName()
        => _exportPresetIdx >= 0 && _exportPresetIdx < _presets.Count ? _presets[_exportPresetIdx].Name ?? "" : "";

    // "Share “X”" + "5 steps · 412 characters" — re-made only when the code, the name or the language changes.
    private string? _shareCode, _shareName, _shareLang;
    private string _shareTitle = "", _shareStatus = "";

    private void RefreshShareText()
    {
        string name = ExportPresetName();
        var lang = _loc.Language;
        if (ReferenceEquals(_exportCode, _shareCode) && ReferenceEquals(name, _shareName)
            && string.Equals(lang, _shareLang, StringComparison.Ordinal)) return;
        _shareCode = _exportCode; _shareName = name; _shareLang = lang;
        int steps = _exportPresetIdx >= 0 && _exportPresetIdx < _presets.Count ? _presets[_exportPresetIdx].Steps.Count - 1 : 0;
        _shareTitle = _loc.TFormat("rm.marks.shareTitle", name);
        _shareStatus = _loc.TFormat("rm.marks.shareStatus", steps, _exportCode.Length);
    }

    private HudElement BuildExportRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader("share", () => { RefreshShareText(); return _shareTitle; }, () => { RefreshShareText(); return _shareStatus; },
            () => _exportWindow.SetVisible(false), SmallIconSize, SmallGlyph),
        // Same sizing as the import text area. ReadOnly: selectable / copyable, not editable.
        new TextAreaElement(Get: () => _exportCode, OnChange: _ => { }, Width: PopupInnerW) { Lines = 5, ReadOnly = true },
        new RowElement(new HudElement[]
        {
            new CellElement(FixedLine(() => _exportStatus,
                () => _exportStatusOk ? SuccessText()
                                      : CautionText(), bold: true), Weight: 1f),
            PrimaryButton(() => _loc.T("rm.marks.copy"), CopyExportCode, PopupPillLabelW),
        }, Gap: 6f),
    }, Gap: 10f);
}
