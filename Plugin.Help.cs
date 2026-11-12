using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

/// <summary>
/// The round "?" at the end of every setting row and the ONE help popover they share, house style (Maestro
/// Plugin.Help.cs / ChatTools Plugin.SettingsHelp.cs): a 24 px Neutral circle (lighter on hover, accent-tinted while its
/// tip is open) whose click opens a title-bar-less GlassMenu popover anchored under it — 32 px accent "?" tile + bold
/// setting name + muted "SECTION · HELP" + round ✕, then the help text in one Tile box, 360 wide. Re-clicking the same
/// "?" closes it; another "?" swaps the text and re-anchors it. The long description sentences that used to sit in the
/// windows live here now. A tip belongs to the window its "?" is in: it closes when that window closes (TickHelp).
///
/// Z-order: every Raid Manager open fronts (WindowCenterer.OpenCentred — once a plugin fronts one window it must front
/// them all, ZFront is sticky up to fw 2.29), so the tip fronts on every show too, or a fronted owner would cover it.
/// </summary>
public sealed partial class Plugin
{
    private const float HelpSize    = 24f;    // the round "?" — exactly 24 × 24 (RoundRadius)
    private const float TipWidth    = 360f;
    private const float TipIconSize = 32f;
    private const float HelpOpenAccentAlpha = 0.35f;

    // One "?": the setting's title + section loc keys, its body text Func and the window it lives in.
    private sealed class HelpTip
    {
        public readonly string TitleKey, SectionKey;
        public readonly Func<string> Body;
        public readonly Func<IWindowControl?> Owner;
        public readonly HoverFlag Hover = new();
        public HelpTip(string titleKey, string sectionKey, Func<string> body, Func<IWindowControl?> owner)
        { TitleKey = titleKey; SectionKey = sectionKey; Body = body; Owner = owner; }
    }

    private IWindowControl _helpWindow = null!;
    private HelpTip? _openTip;
    private WindowRect _helpRect;              // where the popover should sit (re-applied for a few ticks after opening)
    private int _helpRepositionTicks;
    private readonly HoverFlag _helpCloseHover = new();

    private bool TipOpen(HelpTip tip) => ReferenceEquals(_openTip, tip) && _helpWindow.IsShown;

    // A help body straight from one catalog key.
    private Func<string> HelpText(string key) => () => _loc.T(key);

    // Content = [24 px strut, the "?" in a 24 px box] side by side in ONE Row, so the circle is exactly its side (a
    // stacked spacer drew Maestro's "?" 24 × 26). CenterGlyphs centres the drawn "?" in its box.
    private HudElement HelpDot(HelpTip tip)
    {
        var label = new ContrastCache();
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, HelpSize),
                new TextElement(() => "?", Color: () => label.Get(TextColor(), NeutralFill(), PillMinContrast), Width: HelpSize,
                    Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: NoClick)                         // the real work is in OnClickWithRect (needs the button's rect)
            {
                Bare = true,
                OnHover = over => tip.Hover.On = over,
                OnClickWithRect = r => ToggleHelp(r, tip),
            })
        {
            FillColorFn = () => Fade(TipOpen(tip) ? Over(AccentColor() with { A = HelpOpenAccentAlpha }, NeutralFill())
                                     : tip.Hover.On ? HoverFill(NeutralFill())
                                     : NeutralFill()),
            CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
        };
    }

    // Registered FIRST (Plugin.cs): the prebuilt windows' "?" fills poll TipOpen.
    private void RegisterHelpWindow() => _helpWindow = _services.Windows.Register(new WindowRegistration(
        new WindowSpec(
            Id:          "stellar-raid-manager.help",
            Title:       _loc.T("rm.ui.help"),
            DefaultRect: new WindowRect(800f, 80f, TipWidth, 0f),
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        {
            TitleProvider = () => _loc.T("rm.ui.help"),
            Draggable = true, Closable = true, StartVisible = false,
            // No title bar: the header carries the title and a close; the whole frame is the drag handle.
            ShowTitleBar = false,
            ShouldRender = () => ToolWindowsAllowed,
        },
        BuildHelpRoot(),
        OnClose: CloseHelp));

    private HudElement BuildHelpRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            HelpTipIcon(),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => _openTip != null ? _loc.T(_openTip.TitleKey) : string.Empty, Color: () => TextColor(),
                    NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                new TextElement(HelpSubtitle, Color: () => MutedColor(), NoWrap: true, FontSize: 11)
                    { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            HelpCloseButton(),
        }, Gap: 10f),
        // The help text: one raised tile (house Tile colour, theme-live), wrapping inside the 360 px popover.
        new BubbleElement(new TextElement(() => _openTip?.Body() ?? string.Empty, Color: () => TextColor()))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        },
    }, Gap: 10f);

    // "COUNTDOWN · HELP" — composed only when the section or the language changes.
    private string? _helpSubSection, _helpSubHelp;
    private string _helpSub = string.Empty;

    private string HelpSubtitle()
    {
        if (_openTip == null) return string.Empty;
        string section = _loc.T(_openTip.SectionKey), help = _loc.T("rm.ui.help");
        if (!string.Equals(section, _helpSubSection, StringComparison.Ordinal) || !string.Equals(help, _helpSubHelp, StringComparison.Ordinal))
        {
            _helpSubSection = section; _helpSubHelp = help;
            _helpSub = (section + " · " + help).ToUpperInvariant();
        }
        return _helpSub;
    }

    // 32 px accent tile with a white bold "?" (the icon-tile look, drawn with text instead of a PNG).
    private HudElement HelpTipIcon() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, TipIconSize),
            new TextElement(() => "?", Color: () => SolidLabel(AccentColor()),
                Width: TipIconSize, Align: TextAlign.Center, NoWrap: true, FontSize: 18) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(TipIconSize * 0.22f), PaddingX = 0f, PaddingY = 0f,
    }, Width: TipIconSize);

    // Round 24 px ✕ that closes the tip: muted glyph at rest, a soft round wash + text colour on hover.
    private HudElement HelpCloseButton() => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, HelpSize),
            new TextElement(() => "✕", Color: () => _helpCloseHover.On ? TextColor() : MutedColor(), Width: HelpSize,
                Align: TextAlign.Center, NoWrap: true),
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: CloseHelp) { Bare = true, OnHover = over => _helpCloseHover.On = over })
    {
        FillColorFn = () => _helpCloseHover.On ? Fade(HoverFill(PanelColor())) : Transparent,
        CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
    };

    // Re-clicking the same "?" closes it; otherwise (closed, or another "?") show this tip anchored under the "?".
    private void ToggleHelp(WindowRect r, HelpTip tip)
    {
        if (TipOpen(tip)) { CloseHelp(); return; }
        _openTip = tip;
        // OnClickWithRect reports SCREEN PIXELS; SetRect takes CANVAS units — scale first, or the tip drifts off its
        // "?" whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
        var (cw, ch, k) = CanvasMetrics();
        r = new WindowRect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
        // Below the "?", right-aligned to it, so the popover opens to the LEFT over the row instead of off the window.
        float x = Math.Clamp(r.X + r.Width - TipWidth, 0f, Math.Max(0f, cw - TipWidth));
        float y = Math.Min(r.Y + r.Height + 4f, ch - 40f);
        _helpRect = new WindowRect(x, y, TipWidth, 0f);
        _helpWindow.SetVisible(true);
        _helpWindow.BringToFront();   // see the class summary: every Raid Manager open fronts
        _helpWindow.SetRect(_helpRect);
        _helpWindow.MarkDirty();
        // The first-ever open mounts the window and applies its DefaultRect AFTER this SetRect → re-apply the anchored
        // rect for the next few ticks so it lands under the "?" on the first open too.
        _helpRepositionTicks = 4;
    }

    private void CloseHelp()
    {
        _helpRepositionTicks = 0;
        _openTip = null;
        // Unconditional: on the very first open IsShown is still false while the mount lands (WindowBuilder-Patterns.md).
        _helpWindow?.SetVisible(false);
    }

    // From OnUpdate (Plugin.Logic.cs): re-assert the tip position for a few ticks after an open, and close the tip once
    // the window its "?" belongs to has closed. O(1), nothing while no tip is open.
    private void TickHelp()
    {
        if (_openTip == null) return;
        if (_openTip.Owner() is { IsShown: false }) { CloseHelp(); return; }
        if (_helpRepositionTicks <= 0) return;
        _helpRepositionTicks--;
        if (_helpWindow.IsShown) _helpWindow.SetRect(_helpRect);
    }

    // Canvas size + the screen-px → canvas-unit factor (falls back to the screen size before the canvas is measured).
    private (float W, float H, float PxToCanvas) CanvasMetrics()
    {
        var fw = _services.Framework;
        float cw = fw.CanvasWidth > 1f ? fw.CanvasWidth : fw.ScreenWidth;
        float ch = fw.CanvasHeight > 1f ? fw.CanvasHeight : fw.ScreenHeight;
        float k = fw.ScreenWidth > 0 && cw > 1f ? cw / fw.ScreenWidth : 1f;
        return (cw, ch, k);
    }
}
