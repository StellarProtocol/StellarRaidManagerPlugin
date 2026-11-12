using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// House style (Stellar Design Language, Knowledge Base\Stellar-Design-Language.md) — the palette and the pill / panel /
// chip kit every Raid Manager TOOL window builds from (hub, Countdown & Raid Warning, Mark Presets, Import / Export,
// Mechanic Callouts, the help popover). Ported from the reference kits (StellarMaestroPlugin / StellarChatToolsPlugin
// Plugin.Style.cs) and trimmed to what Raid Manager uses. The HUD overlays (countdown text, callout list, minimap,
// on-me banner) do NOT use it — they keep their own overlay look. Needs SDK >= 2.30.0 (theme status colours,
// DropdownElement.FillColorFn, ListElement.OnReorder/Gap, TextElement.Height, ImageElement.Live/Tint).
//
// Toolkit limits behind some choices (WindowBuilder-Patterns.md):
//   • Plain TextElement ignores FontSize on Menu windows (fixed 14px) — only the styled path (Bold/Emphasis) honours it,
//     so small captions are Bold+FontSize while hints stay at the fixed plain size.
//   • Row/Column Gap 0 means the DEFAULT gap (8/12), never zero — every container here passes an explicit Gap.
//   • Bold can't be toggled live → a bold/regular variant is two complete elements swapped by a ConditionalElement.
public sealed partial class Plugin
{
    // ── palette: LIVE from the current framework theme ────────────────────────────────────────────────────────────
    // Derived from _services.Theme.Colors on every call and handed to elements as Funcs, so a theme switch repaints on
    // the next refresh. Status green / amber / red come from the theme too (IThemeStatusColors, fw 2.30.0).
    private static readonly ColorRgba PanelShade = new(0f, 0f, 0f, 0.20f);   // panel = background darkened
    private const float TileTextMix      = 0.08f;   // raised tile = panel + 8% MenuText …
    private const float TileAccentMix    = 0.06f;   // … + a 6% accent tint (teal on teal, pink on pink)
    private const float CardTextMix      = 0.06f;   // hub card = WINDOW background + 6% text …
    private const float CardAccentMix    = 0.08f;   // … + 8% accent (cards sit on the window, not in a panel — R6)
    private const float NeutralMix       = 0.22f;   // neutral pill = panel + 22% MenuMuted …
    private const float NeutralAccentMix = 0.08f;   // … + an 8% accent tint, like the tiles
    private const float PillMinContrast  = 4.5f;    // tinted fills (Neutral, Caution, chips) keep 4.5:1 labels
    private const float SolidMinContrast = 3.0f;    // white on a SOLID fill (Primary, Danger) keeps white to 3:1
    private const float ChipWashAlpha    = 0.22f;   // "on" chip = bold coloured text on a 22% wash of its colour
    private const float ChipOffAlpha     = 0.15f;   // "off" chip = muted text on a faint muted wash
    private const float SelectWashAlpha  = 0.25f;   // selected option chip = accent text on a 25% accent wash
    private const float UnselectAlpha    = 0.12f;   // unselected option chip = muted text on a 12% muted wash
    private const float DangerFillAlpha  = 0.85f;   // the house Danger pill (#ff4d4f @ 0.85 on the dark presets)
    private const float HoverMix         = 0.10f;   // hover = 10% MenuText over the fill
    private static readonly ColorRgba SolidDarkLabel = new(0.12f, 0.12f, 0.12f);   // #1F1F1F: label when white < 3:1
    private static readonly ColorRgba White       = new(1f, 1f, 1f);
    private static readonly ColorRgba Transparent = new(0f, 0f, 0f, 0f);

    private IThemeColors Menu => _services.Theme.Colors;

    private ColorRgba TextColor()   => Menu.MenuText;
    private ColorRgba MutedColor()  => Menu.MenuMuted;
    private ColorRgba AccentColor() => Menu.MenuAccent;
    private ColorRgba PanelColor()  => Over(PanelShade, Menu.MenuBackground);
    private ColorRgba TileColor()   => Over(Menu.MenuAccent with { A = TileAccentMix },
                                            Over(Menu.MenuText with { A = TileTextMix }, PanelColor()));
    private ColorRgba CardColor()   => Over(Menu.MenuAccent with { A = CardAccentMix },
                                            Over(Menu.MenuText with { A = CardTextMix }, Menu.MenuBackground));
    private ColorRgba NeutralFill() => Over(Menu.MenuAccent with { A = NeutralAccentMix },
                                            Over(Menu.MenuMuted with { A = NeutralMix }, PanelColor()));
    // Status text on the window background (footer lines): the theme's Success / Caution pushed to 4.5:1, cached.
    private readonly ContrastCache _successText = new(), _cautionText = new();
    private ColorRgba SuccessText() => _successText.Get(Menu.Success, Menu.MenuBackground, PillMinContrast);
    private ColorRgba CautionText() => _cautionText.Get(Menu.Caution, Menu.MenuBackground, PillMinContrast);
    private ColorRgba HoverFill(ColorRgba fill) => Over(Menu.MenuText with { A = HoverMix }, fill);
    private ColorRgba CautionFill() => Over(Menu.Caution with { A = ChipWashAlpha }, PanelColor());
    private ColorRgba DangerFill()  => Over(Menu.Danger with { A = DangerFillAlpha }, PanelColor());

    // Solid-fill label rule (KB Stellar-Design-Language, fw 8df7f29): WHITE when white reaches 3:1 on the fill, otherwise
    // near-black — never a "darkened white" mid grey, which bold thin text can't carry.
    private static ColorRgba SolidLabel(ColorRgba fill)
        => ContrastRatio(1f, Luminance(fill)) >= SolidMinContrast ? White : SolidDarkLabel;

    // ── window opacity: EVERY coloured fill follows the window opacity, uniformly ──────────────────────────────────
    // The framework fades ONLY the GlassMenu frame Image (Themes → Window Opacity); every bubble fill here is an opaque
    // pre-blended colour, so at a low opacity the panels, chips and pills would stay solid on a see-through frame. Every
    // fill therefore goes through Fade with the frame's factor; only text and pictures stay solid (house rule
    // 2026-11-07). Composed OPAQUE first, alpha last. A cast + property read per poll, no allocation.
    private float WindowOpacity()
    {
        float? v;
        try { v = (_services.NamedTheme as IChromeStyle)?.WindowOpacity; } catch { v = null; }
        return v is { } f ? Math.Clamp(f, 0.3f, 1f) : 1f;
    }

    private ColorRgba Fade(ColorRgba c) => c with { A = c.A * WindowOpacity() };

    // ── colour maths (pure struct maths, allocation-free in per-refresh funcs) ────────────────────────────────────
    // Nudges fg toward white (dark bg) or black (light bg) in 15% steps until it reaches minRatio against the opaque bg.
    private static ColorRgba EnsureContrast(ColorRgba fg, ColorRgba bg, float minRatio)
    {
        float lb = Luminance(bg);
        var target = lb < 0.179f ? White : new ColorRgba(0f, 0f, 0f);
        for (int i = 0; i < 12 && ContrastRatio(Luminance(fg), lb) < minRatio; i++) fg = Mix(fg, target, 0.15f);
        return fg with { A = 1f };
    }

    private static float ContrastRatio(float la, float lb) => (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);
    private static float Luminance(ColorRgba c) => 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);
    private static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    // a → b by t (0 = a, 1 = b), opaque.
    private static ColorRgba Mix(ColorRgba a, ColorRgba b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    // Alpha-composite fg (its A = coverage) over an opaque bg.
    private static ColorRgba Over(ColorRgba fg, ColorRgba bg) => Mix(bg, fg with { A = 1f }, fg.A);

    // EnsureContrast walks up to 12 steps of pow() maths; label Funcs poll it every refresh with inputs that only change
    // with the theme, so each call site keeps its last answer.
    private sealed class ContrastCache
    {
        private ColorRgba _fg, _bg, _out;
        private bool _valid;

        public ColorRgba Get(ColorRgba fg, ColorRgba bg, float ratio)
        {
            if (_valid && fg.Equals(_fg) && bg.Equals(_bg)) return _out;
            _fg = fg; _bg = bg; _out = EnsureContrast(fg, bg, ratio); _valid = true;
            return _out;
        }
    }

    // ── recessed (disabled) look — house rule 22 ──────────────────────────────────────────────────────────────────
    // A disabled control is SUNKEN: black @28 % drawn translucent over whatever it sits on (so it also fades with the
    // window opacity like any see-through fill), a faint label (Text @32 %, raised in 0.04 steps until 2:1 on the sunken
    // fill — the framework's StellarStyle.DisabledLabel), no outline ring, same size. Copied from ChatTools e349c56.
    private static readonly ColorRgba DisabledShade = new(0f, 0f, 0f, 0.28f);
    private const float DisabledLabelAlpha = 0.32f;
    private const float DisabledLabelMinContrast = 2.0f;

    private sealed class DisabledLabelCache { public ColorRgba Text, Surface, Label; public bool Valid; }

    private ColorRgba DisabledLabel(ColorRgba surface, DisabledLabelCache cache)
    {
        var text = Menu.MenuText;
        if (cache.Valid && text.Equals(cache.Text) && surface.Equals(cache.Surface)) return cache.Label;
        var bg = Over(DisabledShade, surface);
        float lb = Luminance(bg), a = DisabledLabelAlpha;
        while (a < 1f && ContrastRatio(Luminance(Over(text with { A = a }, bg)), lb) < DisabledLabelMinContrast)
            a = MathF.Min(1f, a + 0.04f);
        cache.Text = text; cache.Surface = surface; cache.Label = text with { A = a }; cache.Valid = true;
        return cache.Label;
    }

    // ── shape ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // The corner radius for a ROUND (capsule / circle) bubble of side `side`: side / 2 − 1, NOT side / 2. The bubble's
    // 9-slice background reports a MIN size of its summed sprite borders (radius + 1 each side), so radius side/2 drew
    // every round control 2 px taller than its content (Maestro, measured in-game 2026-11-06).
    private static float RoundRadius(float side) => side / 2f - 1f;

    private const float PillH      = 28f;
    private const float PillPadX   = 12f;
    private const float SmallPillH = 22f;   // the preset rows' Activate / Stop
    private const float ChipH      = 24f;
    private const float TileRadius = 6f;    // panels, tiles, cards

    // Hover that follows the shape: a Bare selectable (no square wash of its own) flips this flag and the surrounding
    // round Bubble's FillColorFn shows the hover.
    private sealed class HoverFlag { public bool On; }

    private static readonly Action NoClick = () => { };

    // Bold (TMP) labels: centre the CAP-HEIGHT band — a centred bold-13 line sits ~1.5–2 px high (Maestro, measured on
    // "Rename"), so the label sits in a Column under a 2 px spacer, which moves the text down by 1 px.
    private static HudElement DropBold(HudElement label) => new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, 2f),
        label,
    }, Gap: 0.01f);

    // ── pill buttons ──────────────────────────────────────────────────────────────────────────────────────────────
    // ONE build for every labelled button: a fill-tinted Bubble around a Bare SelectableElement, `height` tall (struts
    // either side of a bold label). `labelW` > 0 = a FIXED label box (house rule 10: ≥ the longest label in any locale,
    // so a language switch or a label change never resizes the pill; Ellipsize as a last resort); 0 = hug the label
    // (only for pills next to a flexible neighbour). `enabled` (optional): a disabled pill keeps its geometry, swallows
    // the click and is RECESSED (rule 22). `surface` = what the pill sits on (for the recessed label contrast).
    private HudElement PillButton(Func<string> label, Func<ColorRgba> fill, Func<ColorRgba> text, Action onClick,
        float labelW, Func<bool>? enabled = null, float height = PillH, int fontSize = 13, float padX = PillPadX,
        Func<ColorRgba>? surface = null, Action<WindowRect>? onClickRect = null)
    {
        var hover = new HoverFlag();
        var disabled = new DisabledLabelCache();
        surface ??= PanelColor;
        bool En() => enabled == null || enabled();
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(padX, height),
                DropBold(new TextElement(label, Color: () => En() ? text() : DisabledLabel(surface(), disabled),
                    Width: labelW, Align: TextAlign.Center, NoWrap: true, FontSize: fontSize) { Bold = true, Ellipsize = labelW > 0f }),
                new SpacerElement(padX, height),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (En()) onClick(); })
            {
                Bare = true, OnHover = over => hover.On = over,
                // OnClick AND OnClickWithRect both fire on a click: a pill that needs its rect (to open a window centred
                // on it) passes NoClick as onClick and does the work here.
                OnClickWithRect = onClickRect == null ? null : r => { if (En()) onClickRect(r); },
            })
        {
            // The recessed fill is drawn translucent (the renderer blends it over the real surface, so it already fades
            // with the window); the raised fills are opaque and faded here.
            FillColorFn = () => !En() ? DisabledShade : Fade(hover.On ? HoverFill(fill()) : fill()),
            CornerRadius = RoundRadius(height), PaddingX = 0f, PaddingY = 0f,
        };
    }

    private HudElement PrimaryButton(Func<string> label, Action onClick, float labelW = 0f, Func<bool>? enabled = null)
        => PillButton(label, AccentColor, () => SolidLabel(AccentColor()), onClick, labelW, enabled);

    private HudElement NeutralButton(Func<string> label, Action onClick, float labelW = 0f, Func<bool>? enabled = null)
    {
        var lbl = new ContrastCache();
        return PillButton(label, NeutralFill, () => lbl.Get(TextColor(), NeutralFill(), PillMinContrast), onClick, labelW, enabled);
    }

    // A Neutral pill that opens something centred on itself (Import / Export): the click delivers the pill's screen rect.
    private HudElement NeutralButtonAt(Func<string> label, Action<WindowRect> onClickRect, float labelW = 0f,
        Func<bool>? enabled = null)
    {
        var lbl = new ContrastCache();
        return PillButton(label, NeutralFill, () => lbl.Get(TextColor(), NeutralFill(), PillMinContrast), NoClick, labelW,
            enabled, onClickRect: onClickRect);
    }

    private HudElement DangerButton(Func<string> label, Action onClick, float labelW = 0f)
        => PillButton(label, DangerFill, () => SolidLabel(DangerFill()), onClick, labelW);

    // A pill that fills a fixed-width cell (equal splits: Prev | Reset | Next, Delete | Cancel). The label box is the
    // cell minus the pill padding, so every pill in the row is exactly `cellW` wide whatever its label.
    private static HudElement FixedCell(float cellW, HudElement pill) => new CellElement(pill, Width: cellW);
    private static float LabelIn(float cellW) => MathF.Max(0f, cellW - 2f * PillPadX);

    // ── section panel ─────────────────────────────────────────────────────────────────────────────────────────────
    // A rounded panel (background darkened, live): small bold UPPERCASE caption + right-aligned muted hint, then the
    // children. Directly in a Column it FILLS the width (a Bubble only hugs inside a Row). No trailing-control overload
    // on purpose (a second params overload silently swallows the first child — KB Stellar-Design-Language §4).
    private HudElement SectionPanel(Func<string> caption, Func<string>? hint, params HudElement[] children)
    {
        var items = new HudElement[children.Length + 1];
        items[0] = new RowElement(new HudElement[]
        {
            new TextElement(UpperCached(caption), Color: () => MutedColor(), NoWrap: true, FontSize: 12) { Bold = true },
            new SpacerElement(0f),   // flexible: pushes the hint to the right edge
            new TextElement(hint ?? (() => string.Empty), Color: () => MutedColor(), NoWrap: true),
        }, Gap: 6f);
        Array.Copy(children, 0, items, 1, children.Length);
        return new BubbleElement(new ColumnElement(items, Gap: 6f))
        {
            FillColorFn = () => Fade(PanelColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        };
    }

    // The UPPERCASE form of a localized string, re-made only when the source changes (a language switch), not per poll.
    private static Func<string> UpperCached(Func<string> src)
    {
        string? last = null, upper = string.Empty;
        return () =>
        {
            var s = src();
            if (!string.Equals(s, last, StringComparison.Ordinal)) { last = s; upper = s.ToUpperInvariant(); }
            return upper;
        };
    }
}
