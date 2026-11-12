using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — import (paste) window ────────────────────────────────────────────────────────────────
//
// Opened by the "Import" pill in the Mark Presets window, centred on that pill and in front (WindowCenterer.OpenCentred,
// house redesign 2026-11-12). House header (32 px "share" icon tile, "Import preset", "Paste a code someone shared"),
// the multi-line text area, then [footer error line | Paste | Import]. Instead of silently reading the clipboard, the
// user pastes (Ctrl+V, or Paste) or types the code, then presses Import. Decoding + preset creation live in
// ImportFromBuffer (Plugin.Marks.Share.cs).
//
// TextAreaElement is a fixed-height box (Lines rows) that wraps a long pasted code and scrolls past that, so it never
// grows this window. It has NO submit (Enter inserts a newline) and NO placeholder (toolkit limit — the prototype's
// "Paste a preset code (RMP1.…)" hint lives in the header subtitle instead). The raw buffer — newlines and all — goes to
// MarkPresetCode.TryDecode, which strips all whitespace, so Enter-typed or chat-wrapped line breaks still decode.
//
// Status: failures (invalid code / preset limit) show in the footer line HERE (amber) and keep the buffer so the user can
// fix it; success closes this window and reports in the marks window's footer. Opening clears the old status but keeps
// the buffer (a half-pasted code survives an accidental close).
public sealed partial class Plugin
{
    private IWindowControl _importWindow = null!;
    private WindowCenterer _importCenter = null!;
    private string _importBuffer = "";
    private string _importStatus = "";
    private bool _importStatusOk = true;

    private const float PopupWidth   = 420f;
    private const float PopupInnerW  = PopupWidth - 24f;   // window padding 2×12
    private const float PopupPillLabelW = 60f;

    private void RegisterImportWindow()
    {
        _importWindow = _services.Windows.Register(new WindowRegistration(
            Spec: ToolSpec("stellar-raid-manager.marks.import", "rm.marks.importTitle", new WindowRect(560f, 340f, PopupWidth, 0f)),
            Root: BuildImportRoot(),
            OnClose: () => _importWindow.SetVisible(false)));
        _importCenter = new WindowCenterer(_services.Framework, _importWindow, WindowCenterer.CanvasCentre);
    }

    // "Import" pill → centred on the pill (`btn` = its SCREEN-pixel rect from OnClickWithRect → canvas units first).
    private void OpenImportWindowAt(WindowRect btn)
    {
        SetImportStatus("", ok: true);   // keep the buffer — a half-pasted code survives an accidental close
        _importCenter.OpenCentred(CentreOfButton(btn));
    }

    // The centre of a clicked control, in canvas units (OnClickWithRect reports screen pixels; SetRect wants canvas
    // units — WindowBuilder-Patterns.md, click-to-open tooltip).
    private Func<float, float, (float X, float Y)?> CentreOfButton(WindowRect btnPx)
    {
        var (_, _, k) = CanvasMetrics();
        return WindowCenterer.Point((btnPx.X + btnPx.Width / 2f) * k, (btnPx.Y + btnPx.Height / 2f) * k);
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

    private HudElement BuildImportRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader("share", () => _loc.T("rm.marks.importTitle"), () => _loc.T("rm.marks.importSub"),
            () => _importWindow.SetVisible(false), SmallIconSize, SmallGlyph),
        // Width = the window's content width (the TextArea pins its preferredWidth at layout priority 2). Lines 5: a pasted
        // code wraps across ~5 visible rows; longer text scrolls inside the fixed box.
        new TextAreaElement(Get: () => _importBuffer, OnChange: s => _importBuffer = s, Width: PopupInnerW) { Lines = 5 },
        new RowElement(new HudElement[]
        {
            new CellElement(FixedLine(() => _importStatus,
                () => _importStatusOk ? SuccessText()
                                      : CautionText(), bold: true), Weight: 1f),
            NeutralButton(() => _loc.T("rm.marks.importPaste"), PasteIntoImportBuffer, PopupPillLabelW),
            PrimaryButton(() => _loc.T("rm.marks.importConfirm"), ImportFromBuffer, PopupPillLabelW),
        }, Gap: 6f),
    }, Gap: 10f);
}
