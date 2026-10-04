using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — import (paste) window ────────────────────────────────────────────────────────────────
//
// Opened by the "Import code…" button in the Mark Presets window. Instead of silently reading whatever is on the
// clipboard, the user pastes (Ctrl+V, or the "Paste from clipboard" button) or types the code into an input field,
// then presses Import / Enter. Decoding + preset creation live in ImportFromBuffer (Plugin.Marks.Share.cs).
//
// InputElement drops newlines on submit, and here runs in SingleLine mode (fixed-height, horizontally-scrolling)
// so a long pasted code neither wraps nor grows this auto-height window. MarkPresetCode.TryDecode strips all
// whitespace anyway, so a code wrapped across lines by a chat client still decodes.
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

    // Opening from hidden remounts with a fresh ZSeq, so it lands on top on its own — no BringToFront (Rule 5).
    private void OpenImportWindow()
    {
        SetImportStatus("", ok: true);
        _importWindow.SetVisible(true);
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
                // Weight 1 fills the 420 window's content width (≈396).
                // SingleLine: the field renders one fixed-height line — a long pasted code scrolls
                // horizontally instead of wrapping and growing this auto-height window (framework opt-in).
                new CellElement(new InputElement(
                    Get:      () => _importBuffer,
                    Submit:   _ => ImportFromBuffer(),
                    OnChange: s => _importBuffer = s) { SingleLine = true }, Weight: 1f),
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
