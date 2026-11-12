using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — window (house redesign, approved 2026-11-12) ──────────────────────────────────────────
//
// 480 wide, CONSTANT height in every state (rule 14): ToolHeader ("“X” active · N marks on the field"), the PRESETS
// panel (a 5-row list that scrolls past 5 — cap 24 unchanged — + the create row), the STEPS panel (always present: the
// active preset's step navigator, or the "No preset active" empty state, or the delete confirm — all in one fixed slot,
// Plugin.Marks.Ui.Steps.cs), and a fixed-height footer feedback line (Plugin.Marks.Edit.cs).
//
// Preset row = [name (bold; accent + accent ring when active; CLICK = rename)] ["N steps"] [Activate (Primary) /
// Stop (Caution — stops only my part, rule 8)] [✕ → delete confirm]. Rows reorder by drag (ListElement.OnReorder).
// While a row is renamed it swaps WHOLE for a 28 px row with the input (InputElement's minimum height is 28, so it
// can't sit in the 26 px tile — WindowBuilder-Patterns 2026-11-10); that row drops its 2 px gap so the pitch stays 28.
// State/logic: Plugin.Marks.cs, .Rename.cs, .Edit.cs; share code: .Share.cs / .ImportWindow.cs / .ExportWindow.cs.
public sealed partial class Plugin
{
    private const float MarksWidth   = 480f;
    private const float MarksInnerW  = MarksWidth - 24f - 20f;   // panel content width: window pad 2×12, panel pad 2×10
    private const int   ListRows     = 5;
    private const float RowTileH     = 26f;
    private const float RowPitch     = 28f;                       // 26 px tile + 2 px gap (rule 15)
    private const float RowPillW     = 72f;
    private const float RowPillPadX  = 6f;
    private const float StepCountW   = 64f;
    private const float CreatePillLabelW = 64f;

    private void RegisterMarksWindow()
    {
        _marksWindow = _services.Windows.Register(new WindowRegistration(
            // Prebuild: 24 pooled preset rows (each with its rename branch) are built in the background at the title
            // screen instead of in the frame the window first opens (Plugin-Performance-Rules §7).
            Spec: ToolSpec("stellar-raid-manager.marks", "rm.marks.title", new WindowRect(500f, 300f, MarksWidth, 0f),
                prebuild: true),
            Root: BuildMarksRoot(),
            OnClose: () => _marksWindow.SetVisible(false)));
    }

    private HudElement BuildMarksRoot()
    {
        var rows = new HudElement[MaxPresets];
        for (int i = 0; i < MaxPresets; i++) rows[i] = PresetRow(i);

        return new ColumnElement(new HudElement[]
        {
            ToolHeader("marks", () => _loc.T("rm.marks.title"), MarksHeaderStatus, () => _marksWindow.SetVisible(false)),

            SectionPanel(() => _loc.T("rm.marks.presetsHeader"), () => _loc.T("rm.marks.presetsHint"),
                new ScrollElement(new ColumnElement(new HudElement[]
                {
                    new ConditionalElement(() => _presets.Count == 0,
                        new TextElement(() => _loc.T("rm.marks.empty"), Color: () => MutedColor())),
                    new ListElement(() => _presets.Count, rows)
                    {
                        Gap = 0.01f,   // the 2 px row gap lives in each slot (rule 15) so a 28 px rename row can eat it
                        OnReorder = MovePreset, CanReorder = CanReorderPresets,
                    },
                }, Gap: 0.01f), Height: ListRows * RowPitch - 2f) { FixedHeight = true },
                new RowElement(new HudElement[]
                {
                    new CellElement(new InputElement(
                        Get:      () => _newPresetName,
                        Submit:   s => { _newPresetName = s; CreatePreset(); },
                        Width:    MarksInnerW - 2f * (CreatePillLabelW + 2f * PillPadX) - 12f,
                        OnChange: s => _newPresetName = s) { SingleLine = true, Placeholder = NewPresetPlaceholder },
                        Weight: 1f),
                    PrimaryButton(() => _loc.T("rm.marks.create"), CreatePreset, CreatePillLabelW),
                    NeutralButtonAt(() => _loc.T("rm.marks.importConfirm"), OpenImportWindowAt, CreatePillLabelW),
                }, Gap: 6f)),

            BuildStepsPanel(),   // Plugin.Marks.Ui.Steps.cs

            FixedLine(() => _marksFooter,
                () => _marksFooterOk ? SuccessText()
                                     : CautionText(), bold: true),
        }, Gap: 10f);
    }

    // ── preset row ────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement PresetRow(int idx)
    {
        bool Active() => idx == _activeIndex;
        object? Item() => idx < _presets.Count ? _presets[idx] : null;
        var count = new StepCountCache();

        var normal = new ColumnElement(new HudElement[]
        {
            new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(2f, RowTileH),   // + the row gap = the 8 px left inset; also the 26 px height strut
                // Click the name to rename (house redesign: no pencil). A press that turns into a drag reorders instead
                // (ListElement swallows the click after a real drag).
                new CellElement(new SelectableElement(
                    new TextElement(() => idx < _presets.Count ? _presets[idx].Name : string.Empty,
                        Color: () => Active() ? AccentColor() : TextColor(), NoWrap: true, FontSize: 13)
                        { Bold = true, Ellipsize = true },
                    OnClick: () => EnterEdit(idx)) { Bare = true }, Weight: 1f),
                new TextElement(() => count.Get(this, idx), Color: () => MutedColor(), Width: StepCountW,
                    Align: TextAlign.Right, NoWrap: true),
                new ConditionalElement(Active,
                    RowPill(() => _loc.T("rm.marks.stop"), CautionTileFill, CautionTileText, DeactivatePreset),
                    RowPill(() => _loc.T("rm.marks.activate"), AccentColor, () => SolidLabel(AccentColor()),
                        () => ActivatePreset(idx))),
                RowRemoveIcon(Item, () => AskDeletePreset(idx)),
            }, Gap: 6f))
            {
                FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
                // The active preset carries a 1 px accent ring (the Bubble outline; the prototype's 2 px ring isn't
                // drawable — BubbleElement's outline is a fixed 1 px sprite).
                OutlineColorFn = () => Active() ? Fade(AccentColor()) : null,
            },
            new SpacerElement(0f, RowPitch - RowTileH),
        }, Gap: 0.01f);

        // Rename: the whole row swaps for a 28 px one holding the input (accent ring around it) — no 2 px gap below.
        var editing = new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0.01f, RowPitch),
            new CellElement(new BubbleElement(new InputElement(
                    Get:      () => _editBuffer,
                    Submit:   _ => CommitRename(idx),
                    Width:    MarksInnerW - RowPillW - RowIconSize - 18f,
                    OnChange: s => _editBuffer = s) { SingleLine = true, FocusRequest = () => _renameFocusSeq })
                {
                    FillColor = Transparent, CornerRadius = 7f, PaddingX = 0f, PaddingY = 0f,
                    OutlineColorFn = () => AccentColor(),
                }, Weight: 1f),
            RowPill(() => _loc.T("rm.marks.activate"), AccentColor, () => SolidLabel(AccentColor()), NoClick, () => false),
            RowRemoveIcon(Item, () => AskDeletePreset(idx)),
        }, Gap: 6f))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
        };

        return new ConditionalElement(() => IsEditing(idx), editing, normal);
    }

    // The rows' 22 px Activate / Stop pill (fixed 72 wide, so Activate ↔ Stop never moves the row).
    private HudElement RowPill(Func<string> label, Func<ColorRgba> fill, Func<ColorRgba> text, Action onClick,
        Func<bool>? enabled = null)
        => new CellElement(PillButton(label, fill, text, onClick, RowPillW - 2f * RowPillPadX, enabled, SmallPillH, 12,
            RowPillPadX, TileColor), Width: RowPillW);

    // Caution on a list tile: the amber wash composed over the TILE (not the panel), label pushed to 4.5:1 on it.
    private readonly ContrastCache _cautionTileText = new();
    private ColorRgba CautionTileFill() => Over(Menu.Caution with { A = ChipWashAlpha }, TileColor());
    private ColorRgba CautionTileText() => _cautionTileText.Get(Menu.Caution, CautionTileFill(), PillMinContrast);

    // Bumped on every EnterEdit → the rename field takes the keyboard focus as it appears (InputElement.FocusRequest).
    private int _renameFocusSeq;

    // "N steps" per row, re-made only when the count or the language changes (polled every refresh).
    private sealed class StepCountCache
    {
        private int _n = -1;
        private string? _lang;
        private string _text = string.Empty;

        public string Get(Plugin p, int idx)
        {
            if (idx >= p._presets.Count) return string.Empty;
            int n = p._presets[idx].Steps.Count - 1;   // Steps[0] is the blank Start anchor
            var lang = p._loc.Language;
            if (n != _n || !string.Equals(lang, _lang, StringComparison.Ordinal))
            {
                _n = n; _lang = lang; _text = p._loc.TFormat("rm.marks.stepCount", n);
            }
            return _text;
        }
    }

    // "New preset name (blank = Preset 5)" — re-made when the preset count or the language changes.
    private int _phCount = -1;
    private string? _phLang;
    private string _ph = string.Empty;

    private string NewPresetPlaceholder()
    {
        var lang = _loc.Language;
        if (_presets.Count != _phCount || !string.Equals(lang, _phLang, StringComparison.Ordinal))
        {
            _phCount = _presets.Count; _phLang = lang;
            _ph = _loc.TFormat("rm.marks.newPlaceholder", _loc.TFormat("rm.marks.autoName", _presets.Count + 1));
        }
        return _ph;
    }
}
