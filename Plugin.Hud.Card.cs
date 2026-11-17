using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// The two HUD cards (option A): a rounded dark translucent BubbleElement with a thick ring (fw 2.33 OutlineWidth), a
// small bold caption, a big bold line and — countdown only — a thin rounded draining bar (fw 2.33 window bar:
// CornerRadius + FillColorFn + TrackColorFn). Geometry below is at size 1.0 in canvas units and scales with the
// Countdown size / Raid warning size step the variant was built for (Plugin.Hud.cs).
//
// Per-refresh cost (the Funcs are polled ~10 Hz while a card is up): no allocation — number strings come from a static
// table, captions are cached per language (+ sender), colours are struct maths on theme fields.
public sealed partial class Plugin
{
    // ── geometry @ size 1.0 ─────────────────────────────────────────────────────────────────────────────────────
    private const float CtCardW     = 420f;   // FIXED width: "12" / "3" / "GO!" never resize the card
    private const float RwCardW     = 720f;   // raid-warning card (message wraps inside it)
    private const float CardPadX    = 36f;    // > CardRing so no content sits under the ring
    private const float CardPadY    = 20f;
    private const float CardRadius  = 40f;
    private const float CardRing    = 5f;     // snapped to 0.5 px + capped at the radius by the framework
    private const float CardGap     = 4f;
    private const int   CapFont     = 30;     // caption (COUNTDOWN / RAID WARNING · name)
    private const int   NumFont     = 140;    // whole seconds / GO!
    private const int   RwMsgFont   = 60;     // raid-warning message
    private const float CtBarH      = 14f;

    // ── colours ───────────────────────────────────────────────────────────────────────────────────────────────────
    private static readonly ColorRgba Amber = new(1f, 0.710f, 0.278f);   // #FFB547 (5–4 s)
    private static readonly ColorRgba Red   = new(1f, 0.294f, 0.294f);   // #FF4B4B (3–1 s, raid warning)
    private static readonly ColorRgba Black = new(0f, 0f, 0f);
    private const float CardFillAlpha  = 0.72f;   // dark translucent card
    private const float CardDarken     = 0.45f;   // theme background pulled 45 % toward black
    private const float CardTrackAlpha = 0.14f;   // bar track = HUD text @14 %

    private IThemeColors Hud => _services.Theme.Colors;

    private ColorRgba CardFill(float alpha) => Mix(Hud.MenuBackground, Black, CardDarken) with { A = CardFillAlpha * alpha };

    // ── text caches ─────────────────────────────────────────────────────────────────────────────────────────────
    // "0".."99" built once — the number Func returns a cached string (never int.ToString per poll).
    private static readonly string[] SecText = BuildSecText();
    private static string[] BuildSecText()
    {
        var a = new string[100];
        for (int i = 0; i < a.Length; i++) a[i] = i.ToString();
        return a;
    }

    private string? _hudLang;
    private string _capCountdown = string.Empty, _capGo = string.Empty;
    private string _rwCaption = string.Empty;
    private string? _rwCaptionFor;               // sender the caption was built for (null = rebuild)

    private void RefreshHudLang()
    {
        var lang = _loc.Language;
        if (string.Equals(lang, _hudLang, StringComparison.Ordinal)) return;
        _hudLang = lang;
        _capCountdown = _loc.T("rm.hud.countdown");
        _capGo        = _loc.T("rm.hud.go");
        _rwCaptionFor = null;
    }

    // ── countdown card state ─────────────────────────────────────────────────────────────────────────────────────
    private bool CtGo => _goTimer > 0d && !_running;

    // Whole seconds rounded UP (12.3 s left → "12"); the sample shows 10.
    private int CtSec => _running ? ShownSec() : 10;

    private string CtCaption() { RefreshHudLang(); return CtGo ? string.Empty : _capCountdown; }
    private string CtNumber()  { RefreshHudLang(); return CtGo ? _capGo : SecText[Math.Clamp(CtSec, 0, 99)]; }

    private ColorRgba? CtNumberColor()
    {
        if (CtGo) return Hud.HudAccent;
        int s = CtSec;
        return s <= 3 ? Red : s <= 5 ? Amber : Hud.HudText;
    }

    private float CtFraction() => CtGo ? 0f : _running ? (float)Math.Clamp(_remaining / _total, 0d, 1d) : 1f;

    private ColorRgba? CtBarFill()
    {
        int s = CtSec;
        return s <= 3 ? Red : s <= 5 ? Amber : Hud.HudAccent;
    }

    // Ring: none > 3 s, red 3–1 s, accent during GO!. Null hides the ring (OutlineColorFn contract).
    private ColorRgba? CtRing() => CtGo ? Hud.HudAccent : _running && ShownSec() <= 3 ? Red : null;

    private HudElement CountdownCard(float s)
    {
        float w = MathF.Round(CtCardW * s), padX = MathF.Round(CardPadX * s), inner = w - 2f * padX;
        int capFont = (int)MathF.Round(CapFont * s), numFont = (int)MathF.Round(NumFont * s);
        float barH = MathF.Max(4f, MathF.Round(CtBarH * s));
        var card = new BubbleElement(new ColumnElement(new HudElement[]
        {
            // Fixed Height on both lines: the blank caption during GO! (and any glyph mix) keeps the card's height.
            new TextElement(CtCaption, Color: () => Hud.MenuMuted, Width: inner, Align: TextAlign.Center,
                NoWrap: true, FontSize: capFont) { Bold = true, Height = MathF.Round(capFont * 1.4f) },
            new TextElement(CtNumber, Color: CtNumberColor, Width: inner, Align: TextAlign.Center,
                NoWrap: true, FontSize: numFont) { Bold = true, Height = MathF.Round(numFont * 1.15f) },
            // Default-style WINDOW bar (fw 2.33): radius = half the height → a capsule track AND fill.
            new BarElement(CtFraction, Hud.HudAccent)
            {
                Height = barH, Width = inner, CornerRadius = barH / 2f,
                FillColorFn = CtBarFill, TrackColorFn = () => Hud.HudText with { A = CardTrackAlpha },
            },
        }, Gap: MathF.Max(1f, MathF.Round(CardGap * s))))
        {
            FillColorFn = () => CardFill(1f), OutlineColorFn = CtRing, OutlineWidth = CardRing * s,
            CornerRadius = MathF.Round(CardRadius * s), PaddingX = padX, PaddingY = MathF.Round(CardPadY * s),
            MaxWidth = w,
        };
        return new RowElement(new HudElement[] { card }, Gap: 0.01f, Justify: RowJustify.Center);
    }

    // ── raid-warning card (Red overlay style) ────────────────────────────────────────────────────────────────────
    // Same card, red ring always, red caption "RAID WARNING · <sender>", message in HUD text colour. The existing 5 s
    // timer's last second fades the fill, ring and both texts together.
    private float RwAlpha => _rwVisible ? (float)Math.Clamp(_rwTimer, 0d, 1d) : 1f;

    private string RwCaption()
    {
        RefreshHudLang();
        string sender = _rwVisible ? _rwSender : _loc.T("rm.hud.sample.sender");
        if (_rwCaptionFor != null && string.Equals(sender, _rwCaptionFor, StringComparison.Ordinal)) return _rwCaption;
        _rwCaptionFor = sender;
        _rwCaption = sender.Length == 0 ? _loc.T("rm.hud.raidWarning") : _loc.TFormat("rm.hud.raidWarningFrom", sender);
        return _rwCaption;
    }

    private string RwMessage() => _rwVisible ? _rwText : _loc.T("rm.hud.sample.rw");

    private HudElement RaidWarningCard(float s)
    {
        float w = MathF.Round(RwCardW * s), padX = MathF.Round(CardPadX * s), inner = w - 2f * padX;
        int capFont = (int)MathF.Round(CapFont * s), msgFont = (int)MathF.Round(RwMsgFont * s);
        var card = new BubbleElement(new ColumnElement(new HudElement[]
        {
            new TextElement(RwCaption, Color: () => Red with { A = RwAlpha }, Width: inner, Align: TextAlign.Center,
                NoWrap: true, FontSize: capFont) { Bold = true, Ellipsize = true, Height = MathF.Round(capFont * 1.4f) },
            // Wraps inside the fixed card width (a long /rw grows the card downward, never wider).
            new TextElement(RwMessage, Color: () => Hud.HudText with { A = RwAlpha }, Width: inner,
                Align: TextAlign.Center, FontSize: msgFont) { Bold = true, MaxLines = 3 },
        }, Gap: MathF.Max(1f, MathF.Round(CardGap * s))))
        {
            FillColorFn = () => CardFill(RwAlpha), OutlineColorFn = () => Red with { A = RwAlpha },
            OutlineWidth = CardRing * s, CornerRadius = MathF.Round(CardRadius * s), PaddingX = padX,
            PaddingY = MathF.Round(CardPadY * s), MaxWidth = w,
        };
        return new RowElement(new HudElement[] { card }, Gap: 0.01f, Justify: RowJustify.Center);
    }
}
