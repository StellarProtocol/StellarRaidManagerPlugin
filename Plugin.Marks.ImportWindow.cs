using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — import (paste) window ────────────────────────────────────────────────────────────────
//
// Opened by the "Import code…" button in the Mark Presets window, anchored just below it (above if no room).
// Instead of silently reading whatever is on the clipboard, the user pastes (Ctrl+V, or the "Paste from clipboard"
// button) or types the code into a multi-line text area, then presses Import. Decoding + preset creation live in ImportFromBuffer (Plugin.Marks.Share.cs).
//
// TextAreaElement is a fixed-height box (Lines rows) that wraps a long pasted code across several visible lines and
// scrolls past that, so it never grows this auto-height window. It has NO submit: Enter inserts a newline (and keeps
// focus), so the Import button is the only way to import. The raw buffer — newlines and all — goes straight to
// MarkPresetCode.TryDecode, which strips all whitespace, so Enter-typed or chat-wrapped line breaks still decode.
//
// Status: failures (invalid code / preset limit) show HERE and keep the buffer so the user can fix it; success closes
// this window and reports in the marks window's status line. Opening the window clears the old status but keeps the
// buffer (a half-pasted code survives an accidental close).
public sealed partial class Plugin
{
    private IWindowControl _importWindow = null!;
    private string _importBuffer = "";
    private string _importStatus = "";
    private bool _importStatusOk = true;

    private void RegisterImportWindow()
    {
        _importWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id: "stellar-raid-manager.marks.import",
                Title: _loc.T("rm.marks.importTitle"),
                // Constant default rect (KB rule) — just offset from the marks window (500,300) so it opens beside it.
                DefaultRect: new WindowRect(560f, 340f, 420f, 0f),
                Category: WindowCategory.Tools,
                Style: WindowPanelStyle.GlassMenu)
            {
                Draggable = true, Closable = true, StartVisible = false,
                // Same gate as the marks window: world only, hidden on loading screens.
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            },
            Root: BuildImportRoot(),
            OnClose: () => _importWindow.SetVisible(false)));
    }

    private const float ImportWidth = 420f;          // = DefaultRect width
    private const float ImportHeightEstimate = 220f; // auto-height window; used until it has been mounted once
    private const float ImportAnchorGap = 6f;
    private WindowRect _importRect;                  // anchored rect, re-applied for a few frames after opening
    private int _importRepositionTicks;

    // Opens the paste window anchored to the "Import code…" button (`btn` = OnClickWithRect's rect). Opening from
    // hidden remounts with a fresh ZSeq, so it lands on top on its own; if it is already visible it is only
    // repositioned. Both windows are WindowCategory.Tools → no cross-category conflict → no BringToFront (Rule 5).
    // DefaultRect stays constant (layout "reset" uses it).
    private void OpenImportWindowAt(WindowRect btn)
    {
        SetImportStatus("", ok: true);   // keep the buffer — a half-pasted code survives an accidental close
        _importRect = AnchorImportRect(btn);
        _importWindow.SetVisible(true);
        _importWindow.SetRect(_importRect);
        // First-ever open MOUNTS the window and the mount applies DefaultRect AFTER this SetRect, so it would land at
        // the default spot. Re-assert the anchored rect for the next few ticks (TickImportReposition) —
        // WindowBuilder-Patterns.md, "Click-to-open tooltip" first-open bug.
        _importRepositionTicks = 4;
    }

    // OnClickWithRect reports SCREEN PIXELS (top-left origin), but WindowRect / SetRect / IWindowControl.Rect are
    // CANVAS UNITS (anchoredPosition) — convert by the UI scale first, or the window drifts off the button whenever
    // the UI scale ≠ 1. Canvas size falls back to screen size if the framework hasn't measured the canvas yet.
    private WindowRect AnchorImportRect(WindowRect btn)
    {
        var fw = _services.Framework;
        float cw = fw.CanvasWidth  > 0 ? fw.CanvasWidth  : fw.ScreenWidth;
        float ch = fw.CanvasHeight > 0 ? fw.CanvasHeight : fw.ScreenHeight;
        float px2cu = fw.ScreenWidth > 0 && cw > 0 ? cw / fw.ScreenWidth : 1f;

        float bx = btn.X * px2cu, by = btn.Y * px2cu, bh = btn.Height * px2cu;
        float h  = _importWindow.Rect.Height > 0f ? _importWindow.Rect.Height : ImportHeightEstimate;

        // Just below the button, left-aligned to it; flip ABOVE the button when there is no room below.
        float y = by + bh + ImportAnchorGap;
        if (y + h > ch) y = by - ImportAnchorGap - h;
        // Keep it fully on screen (SetRect clamps too, but keep the stashed rect honest for the re-apply ticks).
        float x = Math.Max(0f, Math.Min(bx, cw - ImportWidth));
        y = Math.Max(0f, Math.Min(y, ch - h));
        return new WindowRect(x, y, ImportWidth, 0f);   // Height 0 = auto (not Resizable, so size is ignored anyway)
    }

    // Called every tick from TickMarks (Plugin.Marks.cs): re-assert the anchored rect for a few ticks after an open.
    private void TickImportReposition()
    {
        if (_importRepositionTicks <= 0) return;
        _importRepositionTicks--;
        if (_importWindow.IsShown) _importWindow.SetRect(_importRect);
    }

    private void SetImportStatus(string text, bool ok)
    {
        _importStatus = text;
        _importStatusOk = ok;
        _importWindow?.MarkDirty();
    }

    private void PasteIntoImportBuffer()
    {
        try
        {
            _importBuffer = UnityEngine.GUIUtility.systemCopyBuffer ?? "";   // IL2CPP-safe clipboard (see Share.cs)
            SetImportStatus("", ok: true);
        }
        catch (Exception ex)
        {
            SetImportStatus(_loc.T("rm.marks.clipboardFailed"), ok: false);
            _services.Log.Warning($"[MarkPresets] clipboard read failed: {ex.Message}");
        }
        _importWindow?.MarkDirty();
    }

    private HudElement BuildImportRoot()
        => new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T("rm.marks.importLabel"), Emphasis: true),
            new RowElement(new HudElement[]
            {
                // Weight 1 fills the 420 window's content width (≈396). Width is passed too: the TextArea pins its
                // preferredWidth (default 260) at layout priority 2, which also sets the cell's natural width —
                // 396 keeps it full-width rather than relying on the cell's force-expand alone.
                // Lines 5: a pasted code wraps across ~5 visible rows; longer text scrolls inside the fixed box.
                new CellElement(new TextAreaElement(
                    Get:      () => _importBuffer,
                    OnChange: s => _importBuffer = s,
                    Width:    396f) { Lines = 5 }, Weight: 1f),
            }, Gap: 4f),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => _loc.T("rm.marks.importPaste"),   OnClick: PasteIntoImportBuffer),
                new ButtonElement(() => _loc.T("rm.marks.importConfirm"), OnClick: ImportFromBuffer),
            }, Gap: 6f),
            new ConditionalElement(() => _importStatus.Length > 0,
                new TextElement(() => _importStatus,
                    // Explicit color on BOTH branches — a null Color lambda means "leave untouched", so an error tint
                    // would otherwise stick after a later success (WindowBuilder-Patterns.md, sticky color).
                    Color: () => _importStatusOk
                        ? (ColorRgba?)_services.Theme.Colors.HudAccent
                        : (ColorRgba?)_services.Theme.Colors.Warning)),
        }, Gap: 6f);
}
