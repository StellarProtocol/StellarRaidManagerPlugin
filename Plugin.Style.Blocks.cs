using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// More house-kit building blocks (Stellar Design Language §4; reference Maestro / ChatTools Plugin.Style.Blocks.cs):
// embedded art + the per-size icon picker, the accent icon tile, the ToolHeader with its round ✕, the hub card, the
// icon-only ✕ of a list row, the round icon button and a fixed-height text line.
public sealed partial class Plugin
{
    // ── embedded PNGs ─────────────────────────────────────────────────────────────────────────────────────────────
    // Read once per resource and kept: ImageElement Funcs are polled. A missing resource caches null (the element then
    // draws nothing) instead of re-reading every poll. `logical` = the csproj LogicalName minus "Stellar.RaidManager.".
    private static readonly Dictionary<string, byte[]?> _pngCache = new();

    private static byte[]? CachedPng(string logical)
    {
        if (_pngCache.TryGetValue(logical, out var png)) return png;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.RaidManager." + logical);
            if (s != null) { using var ms = new System.IO.MemoryStream(); s.CopyTo(ms); png = ms.ToArray(); }
        }
        catch { png = null; }
        _pngCache[logical] = png;
        return png;
    }

    // ── icon art per size (KB Stellar-Design-Language §3 "PRE-DRAW every icon per size") ───────────────────────────
    // Icons are flat WHITE glyphs embedded as Resources\icons\<name>-<px>.png (csproj glob → LogicalName
    // "Stellar.RaidManager.icons.<name>-<px>.png"). The sizes that exist are DISCOVERED from the manifest, so swapping in
    // the final art = replacing / adding files, no code change. The picker wants the glyph's real SCREEN size (canvas
    // units × ScreenWidth / CanvasWidth — 24 units draw at 18 px at 1080p): an exact match (±0.05 px) is drawn 1:1,
    // otherwise the smallest larger bitmap (scaled down by the renderer), else the largest there is.
    private const string IconPrefix = "Stellar.RaidManager.icons.";
    private static Dictionary<string, int[]>? _iconSizes;   // name → ascending pixel sizes

    private static int[] IconSizesOf(string name)
    {
        if (_iconSizes == null)
        {
            var found = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var res in typeof(Plugin).Assembly.GetManifestResourceNames())
            {
                if (!res.StartsWith(IconPrefix, StringComparison.Ordinal) || !res.EndsWith(".png", StringComparison.Ordinal)) continue;
                string stem = res.Substring(IconPrefix.Length, res.Length - IconPrefix.Length - 4);   // "<name>-<px>"
                int dash = stem.LastIndexOf('-');
                if (dash <= 0 || !int.TryParse(stem.Substring(dash + 1), out int px) || px <= 0) continue;
                string n = stem.Substring(0, dash);
                if (!found.TryGetValue(n, out var list)) found[n] = list = new List<int>();
                list.Add(px);
            }
            var map = new Dictionary<string, int[]>(StringComparer.Ordinal);
            foreach (var kv in found) { kv.Value.Sort(); map[kv.Key] = kv.Value.ToArray(); }
            _iconSizes = map;
        }
        return _iconSizes.TryGetValue(name, out var sizes) ? sizes : Array.Empty<int>();
    }

    private static int PickIconSize(int[] sizes, float px)
    {
        if (sizes.Length == 0) return 0;
        foreach (int s in sizes) if (MathF.Abs(s - px) <= 0.05f) return s;   // exact → drawn 1:1
        foreach (int s in sizes) if (s > px) return s;                        // smallest larger
        return sizes[sizes.Length - 1];                                       // nothing larger: the biggest
    }

    // One live glyph image: re-picks only when the screen/canvas ratio changes (resolution / UI scale), and returns the
    // SAME cached array otherwise (ImageElement.Live compares by reference — no swap, no allocation per poll).
    private Func<byte[]?> IconPng(string name, float units)
    {
        float lastScale = -1f;
        byte[]? last = null;
        return () =>
        {
            var fw = _services.Framework;
            float scale = fw.CanvasWidth > 1f && fw.ScreenWidth > 0 ? fw.ScreenWidth / fw.CanvasWidth : 1f;
            if (scale == lastScale) return last;
            lastScale = scale;
            int px = PickIconSize(IconSizesOf(name), units * scale);
            last = px > 0 ? CachedPng("icons." + name + "-" + px + ".png") : null;
            return last;
        };
    }

    // ── icon tile ─────────────────────────────────────────────────────────────────────────────────────────────────
    // A rounded tile in the LIVE theme accent with the flat white glyph centred on it (art can't follow the theme, so the
    // colour lives in the tile — user-approved option A). 40 px tile → 24 px glyph, 32 px tile → 16 px glyph (the
    // framework's snapped glyph sizes, so the pre-drawn art is used 1:1 at 1440p). Radius ≈ 22 % of the side. The
    // fixed-width Cell + centred Row stop the image being stretched (the icon-squish gotcha).
    private const float ToolIconSize  = 40f;
    private const float ToolGlyphSize = 24f;
    private const float SmallIconSize = 32f;
    private const float SmallGlyph    = 16f;

    private HudElement IconBox(string icon, float size, float glyph)
    {
        float pad = (size - glyph) / 2f;
        return new CellElement(new BubbleElement(
            new RowElement(new HudElement[] { new ImageElement(IconPng(icon, glyph), (int)glyph, (int)glyph) { Live = true } },
                Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(size * 0.22f), PaddingX = pad, PaddingY = pad,
        }, Width: size);
    }

    // Window title block: icon tile + bold title over a live muted status line, optional trailing controls (e.g. the
    // Mechanic Callouts "Test alert" pill), then the round ✕ at the far right. The windows have no framework title bar
    // (ShowTitleBar = false: the whole frame drags), so this ✕ is THE close.
    private HudElement ToolHeader(string icon, Func<string> title, Func<string> status, Action onClose,
        float iconSize = ToolIconSize, float glyph = ToolGlyphSize, HudElement? trailing = null)
    {
        var items = new List<HudElement>(4)
        {
            IconBox(icon, iconSize, glyph),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(title, Color: () => TextColor(), Emphasis: true, NoWrap: true) { Ellipsize = true },
                new TextElement(status, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true },
            }, Gap: 2f), Weight: 1f),
        };
        if (trailing != null) items.Add(trailing);
        items.Add(CloseIcon(onClose, IconButtonSize));
        return new RowElement(items, Gap: 10f);
    }

    // ── round icon-only ✕ (window close) ──────────────────────────────────────────────────────────────────────────
    // House "remove hover": a muted glyph at rest; on hover a red glyph on a soft round red wash. Exactly `side` square
    // (one Row[strut of the side, a side-wide glyph box], house rule 4). Red = the theme's Danger colour lifted 35 %
    // toward white (#ff4d4f → #ff8a8b on the dark presets).
    private const float IconButtonSize  = 28f;
    private const float RemoveWashAlpha = 0.22f;
    private const float RemoveGlyphLift = 0.35f;

    private HudElement CloseIcon(Action onClick, float side)
    {
        var hover = new HoverFlag();
        var glyph = new ContrastCache();
        ColorRgba Wash() => Over(Menu.Danger with { A = RemoveWashAlpha }, Menu.MenuBackground);
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, side),
                new TextElement(() => "✕", Width: side, Align: TextAlign.Center, NoWrap: true,
                    Color: () => hover.On ? glyph.Get(Mix(Menu.Danger, White, RemoveGlyphLift), Wash(), SolidMinContrast)
                                          : MutedColor()),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            // Transparent at rest — explicit, NOT null (null = the themed accent default, WindowBuilder-Patterns.md).
            FillColorFn = () => hover.On ? Fade(Wash()) : Transparent,
            CornerRadius = RoundRadius(side), PaddingX = 0f, PaddingY = 0f,
        };
    }

    // ── row ✕ (delete) ────────────────────────────────────────────────────────────────────────────────────────────
    // The list-row form of the icon-only ✕ (22 px, round): muted at rest, red on a soft red wash on hover. The preset
    // list is a plain ListElement (slot i = item i), so a ✕ click that removes a row slides the next preset under the
    // pointer: the hover flag is keyed by the item it was set for and drops when the slot shows another item.
    private const float RowIconSize = 22f;

    private sealed class RowHover
    {
        public bool On;
        public object? Item;
        public bool Get(object? item) { if (!ReferenceEquals(item, Item)) { Item = item; On = false; } return On; }
    }

    private HudElement RowRemoveIcon(Func<object?> item, Action onClick)
    {
        var hover = new RowHover();
        var glyph = new ContrastCache();
        ColorRgba Wash() => Over(Menu.Danger with { A = RemoveWashAlpha }, TileColor());
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, RowIconSize),
                new TextElement(() => "✕", Width: RowIconSize, Align: TextAlign.Center, NoWrap: true,
                    Color: () => hover.Get(item()) ? glyph.Get(Mix(Menu.Danger, White, RemoveGlyphLift), Wash(), SolidMinContrast)
                                                   : MutedColor()),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => { hover.Item = item(); hover.On = over; } })
        {
            FillColorFn = () => hover.Get(item()) ? Fade(Wash()) : Transparent,
            CornerRadius = RoundRadius(RowIconSize), PaddingX = 0f, PaddingY = 0f,
        };
    }

    // ── round icon button ─────────────────────────────────────────────────────────────────────────────────────────
    // 28 px round Neutral button with a 16 px white glyph (the step comment's pencil / check). Recessed while `enabled`
    // is false (rule 22), same size. `png` is an embedded logical name (read once).
    private HudElement RoundIconButton(string png, Action onClick, Func<bool>? enabled = null)
    {
        var hover = new HoverFlag();
        const float glyph = 16f, side = IconButtonSize;
        float pad = (side - glyph) / 2f;
        bool En() => enabled == null || enabled();
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(pad, side),
                new ImageElement(() => CachedPng(png), (int)glyph, (int)glyph)
                    { Tint = () => En() ? (ColorRgba?)null : Menu.MenuText with { A = 0.32f } },
                new SpacerElement(pad, side),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (En()) onClick(); }) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => !En() ? DisabledShade : Fade(hover.On ? HoverFill(NeutralFill()) : NeutralFill()),
            CornerRadius = RoundRadius(side), PaddingX = 0f, PaddingY = 0f,
        };
    }

    // ── hub card ──────────────────────────────────────────────────────────────────────────────────────────────────
    // Raised card (CardColor — it sits on the window, R6) → Bare Selectable → [40 px icon | bold title over the live
    // status | › ], 56 px (a 56 px strut). Bubble OUTSIDE the selectable on purpose (a bubble wrapped IN a selectable gets
    // a full-width chip behind it); the hover is the card's own fill, so it keeps the rounded corners. The text column
    // sits in a weighted Cell so a long status ellipsizes instead of pushing the chevron out.
    private const float HubCardHeight = 56f;

    private HudElement HubCard(string icon, Func<string> title, Func<string> status, Action onOpen)
    {
        var hover = new HoverFlag();
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, HubCardHeight),   // + the 8 px row gap = the left inset; also the height strut
                IconBox(icon, ToolIconSize, ToolGlyphSize),
                new CellElement(new ColumnElement(new HudElement[]
                {
                    new TextElement(title, Color: () => TextColor(), NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                    new TextElement(status, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true },
                }, Gap: 1f), Weight: 1f),
                // Bold (styled/TMP path) so FontSize applies and the glyph can't fall back to a taller OS font.
                new TextElement(() => "›", Color: () => MutedColor(), Width: 24f, Align: TextAlign.Center, NoWrap: true,
                    FontSize: 18) { Bold = true },
                new SpacerElement(0f, 1f),   // + the row gap = the right margin
            }, Gap: 8f),
            OnClick: onOpen) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(hover.On ? HoverFill(CardColor()) : CardColor()),
            CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
    }

    // ── fixed-height line ─────────────────────────────────────────────────────────────────────────────────────────
    // One line of text in a box of exactly LineH (TextElement.Height, fw 2.30.0): bold / empty / longer text can't change
    // the window height; NoWrap + Ellipsize keeps it on one line.
    private const float LineH = 20f;

    private static TextElement FixedLine(Func<string> text, Func<ColorRgba?> color, bool bold = false)
        => new(text, color, NoWrap: true, FontSize: bold ? 12 : 0) { Ellipsize = true, Height = LineH, Bold = bold };

    // A fixed-width, TOP-aligned column (a Row centres its children vertically): an always-true Conditional with Fill
    // makes the column take the row's height and lay out from the top (WindowBuilder-Patterns.md "Two-column settings
    // window", ✅ in-game in ChatTools Settings).
    private static HudElement TopColumn(float width, float gap, params HudElement[] children) => new CellElement(
        new ConditionalElement(() => true, new ColumnElement(children, Gap: gap), Fill: true), Width: width);

    // A slot of exactly `height` whose content lays out from the top: Row[0-wide strut, weighted top-aligned column].
    // Every state of a panel goes into the same slot, so switching states never resizes the window (rule 14).
    private static HudElement FixedSlot(float height, HudElement content) => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, height),
        new CellElement(new ConditionalElement(() => true, new ColumnElement(new[] { content }, Gap: 0.01f), Fill: true),
            Weight: 1f),
    }, Gap: 0.01f);
}
