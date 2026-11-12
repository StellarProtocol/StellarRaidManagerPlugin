using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// House-kit chips (Stellar Design Language §4 "Clickable on/off chip", ChatTools / MinimalNameplate recipe): the 64×24
// ON/OFF toggle, the option chips (Size Small/Medium/Large, Listen in Party/Guild/Local, Style) and the RECESSED chip a
// sub-option shows while its section's main switch is off (rule 22 — shown, dimmed, not clickable; never hidden, so the
// window keeps its height).
public sealed partial class Plugin
{
    private const float OnOffChipW  = 64f;
    private const float OptionChipW = 64f;
    private const float StyleChipW  = 96f;   // "Game notice" / "Red overlay"

    // A clickable 24 px chip in a fixed-width cell: Cell → round Bubble (fill + hover) → Bare Selectable → Row[24 px
    // strut, label]. The caller swaps complete variants with a ConditionalElement (bold can't toggle live), all in the
    // same width, so a swap never moves the row. Pass the SAME HoverFlag to every variant of one chip.
    private HudElement ChipButton(HudElement label, Func<ColorRgba> fill, HoverFlag hover, Action onClick, float width)
        => new CellElement(new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipH),   // height strut = the side (house rule 4: no stacked spacers)
                label,
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(hover.On ? HoverFill(fill()) : fill()),
            CornerRadius = RoundRadius(ChipH), PaddingX = 0f, PaddingY = 0f,
        }, Width: width);

    // The recessed (not clickable) form of a chip: sunken fill + faint regular label, same size.
    private HudElement RecessedChip(Func<string> label, float width)
    {
        var disabled = new DisabledLabelCache();
        return new CellElement(new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipH),
                new TextElement(label, Color: () => DisabledLabel(PanelColor(), disabled), Align: TextAlign.Center, NoWrap: true),
            }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => DisabledShade, CornerRadius = RoundRadius(ChipH), PaddingX = 0f, PaddingY = 0f,
        }, Width: width);
    }

    // A two-state chip: ON = bold `onText` colour on `onFill`, OFF = regular `offText` on `offFill`; same width, swapped
    // whole by `isOn`. `enabled` (optional): while false the chip is recessed instead (rule 22).
    private HudElement TwoStateChip(Func<string> label, Func<bool> isOn, Action onClick, float width,
        Func<ColorRgba> onFill, Func<ColorRgba> onText, Func<ColorRgba> offFill, Func<ColorRgba> offText,
        Func<bool>? enabled = null)
    {
        var hover = new HoverFlag();
        HudElement live = new ConditionalElement(isOn,
            ChipButton(DropBold(new TextElement(label, Color: () => onText(), Align: TextAlign.Center, NoWrap: true,
                    FontSize: 12) { Bold = true }), onFill, hover, onClick, width),
            ChipButton(new TextElement(label, Color: () => offText(), Align: TextAlign.Center, NoWrap: true),
                offFill, hover, onClick, width));
        return enabled == null ? live : new ConditionalElement(enabled, live, RecessedChip(label, width));
    }

    // The house ON/OFF toggle: ON = bold Success text on a 22% Success wash, OFF = muted on a faint muted wash; 64 × 24
    // both ways. The label is the state ("ON" / "OFF"), so it flips with it.
    private HudElement OnOffChip(Func<bool> isOn, Action<bool> set, Func<bool>? enabled = null)
    {
        var onText = new ContrastCache();
        var offText = new ContrastCache();
        ColorRgba OnFill()  => Over(Menu.Success with { A = ChipWashAlpha }, PanelColor());
        ColorRgba OffFill() => Over(Menu.MenuMuted with { A = ChipOffAlpha }, PanelColor());
        return TwoStateChip(() => isOn() ? _loc.T("rm.ui.on") : _loc.T("rm.ui.off"), isOn, () => set(!isOn()), OnOffChipW,
            OnFill, () => onText.Get(Menu.Success, OnFill(), PillMinContrast),
            OffFill, () => offText.Get(MutedColor(), OffFill(), SolidMinContrast), enabled);
    }

    // An option chip (single- or multi-select — the caller decides what a click does): selected = bold accent on a 25%
    // accent wash, unselected = muted on a faint muted wash.
    private HudElement OptionChip(Func<string> label, Func<bool> selected, Action onClick, float width = OptionChipW,
        Func<bool>? enabled = null)
    {
        var onText = new ContrastCache();
        var offText = new ContrastCache();
        ColorRgba SelFill()   => Over(Menu.MenuAccent with { A = SelectWashAlpha }, PanelColor());
        ColorRgba UnselFill() => Over(Menu.MenuMuted with { A = UnselectAlpha }, PanelColor());
        return TwoStateChip(label, selected, onClick, width,
            SelFill, () => onText.Get(AccentColor(), SelFill(), PillMinContrast),
            UnselFill, () => offText.Get(MutedColor(), UnselFill(), SolidMinContrast), enabled);
    }
}
