using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — export (copy) window ─────────────────────────────────────────────────────────────────
//
// Opened by the "Export Code" button in the Mark Presets step panel, anchored just below it (above if no room) — the
// mirror of the import window (Plugin.Marks.ImportWindow.cs, which also owns the shared AnchorBelowButton /
// TickPopupReposition helpers). Opening GENERATES the code for the active preset (ExportActivePreset,
// Plugin.Marks.Share.cs); if encoding fails the export-failed status shows in the marks window and this window does
// not open.
//
// The full code sits in a READ-ONLY TextAreaElement: the user can select/copy it by hand (Ctrl+C) but not edit it,
// and the Copy button puts the whole thing on the clipboard. Copy feedback shows in this window's status line (and in
// the marks window's status + NoticeTip, as before).
//
// Staleness: the code is a snapshot. Everything that would change it (switching / deactivating / deleting / renaming
// the preset, saving or deleting a step, editing a comment) calls ClearExportCode(), which also CLOSES this window —
// the user re-opens it to get a fresh code. Closing (rather than silently regenerating) keeps one rule everywhere:
// this window only ever shows a code for the preset it was opened on.
public sealed partial class Plugin
{
    private IWindowControl _exportWindow = null!;
    private string _exportStatus = "";
    private bool _exportStatusOk = true;

    private const float ExportWidth = 420f;          // = DefaultRect width
    private const float ExportHeightEstimate = 240f; // auto-height window; used until it has been mounted once
    private WindowRect _exportRect;                  // anchored rect, re-applied for a few frames after opening
    private int _exportRepositionTicks;

    private void RegisterExportWindow()
    {
        _exportWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id: "stellar-raid-manager.marks.export",
                Title: _loc.T("rm.marks.exportTitle"),
                // Constant default rect (KB rule) — same spot as the import window; it is re-anchored on every open.
                DefaultRect: new WindowRect(560f, 340f, ExportWidth, 0f),
                Category: WindowCategory.Tools,
                Style: WindowPanelStyle.GlassMenu)
            {
                Draggable = true, Closable = true, StartVisible = false,
                // Same gate as the marks window: world only, hidden on loading screens.
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            },
            Root: BuildExportRoot(),
            OnClose: () => _exportWindow.SetVisible(false)));
    }

    // "Export Code" → OnClickWithRect (`btn` in screen pixels). Re-clicking while open regenerates the code for the
    // (same, still-active) preset and re-anchors. Both windows are WindowCategory.Tools and this one opens from hidden
    // (fresh ZSeq) → no BringToFront (Rule 5).
    private void OpenExportWindowAt(WindowRect btn)
    {
        ExportActivePreset();
        if (!HasExportCode()) return;   // no active preset, or encode failed (status already in the marks window)

        SetExportStatus("", ok: true);
        _exportRect = AnchorBelowButton(btn, ExportWidth, ExportHeightEstimate, _exportWindow.Rect.Height);
        _exportWindow.SetVisible(true);
        _exportWindow.SetRect(_exportRect);
        _exportRepositionTicks = 4;     // first-open mount applies DefaultRect after SetRect (see ImportWindow.cs)
        _exportWindow.MarkDirty();
    }

    private void CloseExportWindow()
    {
        if (_exportWindow != null && _exportWindow.IsShown) _exportWindow.SetVisible(false);
    }

    private void SetExportStatus(string text, bool ok)
    {
        _exportStatus = text;
        _exportStatusOk = ok;
        _exportWindow?.MarkDirty();
    }

    private string ExportPresetName()
        => _exportPresetIdx >= 0 && _exportPresetIdx < _presets.Count ? _presets[_exportPresetIdx].Name ?? "" : "";

    private HudElement BuildExportRoot()
        => new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.TFormat("rm.marks.exportLabel", ExportPresetName()), Emphasis: true),
            new RowElement(new HudElement[]
            {
                // Same sizing as the import text area (Weight 1 + Width 396 = the 420 window's content width).
                // ReadOnly: selectable/copyable, not editable — OnChange is never meaningfully called.
                new CellElement(new TextAreaElement(
                    Get:      () => _exportCode,
                    OnChange: _ => { },
                    Width:    396f) { Lines = 5, ReadOnly = true }, Weight: 1f),
            }, Gap: 4f),
            new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(
                    () => _loc.TFormat("rm.marks.codeLength", _exportCode.Length),
                    Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Weight: 1f),
                new CellElement(new ButtonElement(() => _loc.T("rm.marks.copy"), OnClick: CopyExportCode),
                    Width: 90f),
            }, Gap: 4f),
            new ConditionalElement(() => _exportStatus.Length > 0,
                new TextElement(() => _exportStatus,
                    // Explicit color on BOTH branches — a null Color lambda means "leave untouched", so an error tint
                    // would otherwise stick after a later success (WindowBuilder-Patterns.md, sticky color).
                    Color: () => _exportStatusOk
                        ? (ColorRgba?)_services.Theme.Colors.HudAccent
                        : (ColorRgba?)_services.Theme.Colors.Warning)),
        }, Gap: 6f);
}
