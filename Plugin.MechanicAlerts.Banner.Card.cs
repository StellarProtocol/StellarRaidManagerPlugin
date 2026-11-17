using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// The on-me alert CARD (approved mockup §5; port of Experiment 877eed8): a rounded dark translucent BubbleElement with a
// thick ring (fw 2.33 OutlineWidth), a small bold caption over a large bold action on the left, a large bold timer
// right-aligned in a fixed column. Same family as the countdown card (Plugin.Hud.Card.cs).
//
// SIZE: fonts, ring width, radius and padding are fixed at BUILD on the Menu surface, and "Banner size" moves in 11
// discrete 0.5 steps (1–6). So every slot is built once PER STEP, each variant a whole-card ConditionalElement gated on
// "this slot shows AND this step is selected" (WindowBuilder-Patterns.md "Size setting on a Menu-surface overlay"): a
// size change just shows another branch next refresh — no re-register, saved position safe — and a branch builds lazily
// the first time it shows, so unused steps cost nothing. The Conditional wraps the WHOLE card (never inside the bubble).
// Card scale = Banner size × 1.0 (Experiment × 0.75 — its default size is 4, Raid Manager's 1.5): the default 1.5 →
// 1.5× the mockup's card (caption 15 / action 32 / timer 42 px, 510 px wide) — the old 42 px headline's height, a
// moderate card, not a screen-wide strip. 6× = 2040 px, still inside the 2400 window.
//
// Fixed card width (the text column and the timer column both have fixed widths, the texts Ellipsize), so the bubble
// never resizes with its text. The per-tick timer sits in its own fixed-width inner bubble, so the card bubble
// re-measures only when a different banner takes the slot (WindowBuilder.BubbleOwner.cs).
//
// Per-refresh state (AlertFrame): the first poll of a refresh resolves the 3 slots once (ShownAt walks the MOVE OFF /
// ring / spin / on-me lists) and computes timer text + colours; every binding reads the arrays. No allocation: timer
// strings from the shared tables (Plugin.MechanicHudText.cs), colours are structs, captions are built once per banner.
public sealed partial class Plugin
{
    private static readonly float[] AlertSizes = { 1f, 1.5f, 2f, 2.5f, 3f, 3.5f, 4f, 4.5f, 5f, 5.5f, 6f };
    private const float AlertCardScale = 1f;
    private const float AlertCardFillA = 0.82f;

    private long _alertFrameTick = -1;
    private readonly Banner?[]   _alertShown    = new Banner?[AlertSlots];
    private readonly string[]    _alertTimer    = new string[AlertSlots];
    private readonly ColorRgba[] _alertTimerCol = new ColorRgba[AlertSlots];
    private readonly ColorRgba[] _alertRing     = new ColorRgba[AlertSlots];
    private readonly ColorRgba[] _alertCapCol   = new ColorRgba[AlertSlots];

    private void AlertFrame()
    {
        long now = Environment.TickCount64;
        if (now == _alertFrameTick) return;
        _alertFrameTick = now;
        var c = Hud;
        for (int i = 0; i < AlertSlots; i++)
        {
            var b = _alertShown[i] = ShownAt(i);
            if (b == null) continue;
            var mc = b.DangerTile ? c.Danger : MechanicCalloutData.SlotColor(b.Color);
            _alertCapCol[i] = mc;
            if (b.Sample) { _alertTimer[i] = MechTenths[80]; _alertTimerCol[i] = c.HudText; _alertRing[i] = mc; continue; }
            var row = b.Occ.Row;
            if (b.DangerTile || !MechRowTime(row, out float shown, out _))
            {
                _alertTimer[i] = ""; _alertTimerCol[i] = c.HudText; _alertRing[i] = mc;
                continue;
            }
            int step = row.IsHitNow ? 2 : MechStep(shown);
            _alertTimer[i] = row.IsHitNow ? AlertNowText() : MechSecText(shown);
            _alertTimerCol[i] = MechStepColor(step, c.HudText);
            _alertRing[i] = step == 2 ? c.Danger : mc;
        }
    }

    // The hit moment (McRow.IsHitNow) in the card's 74-wide timer column: the localized NOW when it fits ("NOW", "今！"),
    // else "0.0s" ("SEKARANG" / "NGAYON" would ellipsize; the red ring + Danger colour already say "now"). Same
    // estimate as the name lines (+5 % for bold), cached per language.
    private string? _alertNowLang;
    private string _alertNow = "";

    private string AlertNowText()
    {
        var lang = _loc.Language;
        if (!string.Equals(lang, _alertNowLang, StringComparison.Ordinal))
        {
            _alertNowLang = lang;
            string now = MechNowText;
            _alertNow = EstWidth(now, 28f) * 1.05f <= 74f ? now : MechTenths[0];
        }
        return _alertNow;
    }

    private bool AlertSlotShown(int i) { AlertFrame(); return _alertShown[i] != null; }

    private HudElement BuildAlertHudRoot()
    {
        var items = new HudElement[AlertSlots * AlertSizes.Length];
        int n = 0;
        for (int s = 0; s < AlertSlots; s++)
        {
            int i = s;
            foreach (float step in AlertSizes)
            {
                float st = step;   // compared with == on purpose: the setting only ever stores these exact values
                items[n++] = new ConditionalElement(() => AlertSlotShown(i) && _alertScale == st, AlertCard(i, st * AlertCardScale));
            }
        }
        // Hidden no-else Conditionals leave the layout entirely, so the gap only sits between cards actually on screen.
        return new ColumnElement(items, Gap: 12f) { Padding = 10 };
    }

    private HudElement AlertCard(int i, float f)
    {
        // Timer column 74 (mockup) holds "8.8s" / "88s" / "NOW" at 28 px (a longer localized NOW → AlertNowText).
        float w = MathF.Round(340f * f), padX = MathF.Round(14f * f), padY = MathF.Round(7f * f);
        float gap = MathF.Round(14f * f), tw = MathF.Round(74f * f);
        float textW = w - 2f * padX - gap - tw;
        int capFont = (int)MathF.Round(10f * f), actFont = (int)MathF.Round(21f * f), numFont = (int)MathF.Round(28f * f);

        var text = new ColumnElement(new HudElement[]
        {
            new TextElement(() => { AlertFrame(); return _alertShown[i]?.Caption ?? ""; }, Color: () => { AlertFrame(); return _alertCapCol[i]; },
                Width: textW, NoWrap: true, FontSize: capFont) { Bold = true, Ellipsize = true, Height = MathF.Round(capFont * 1.35f) },
            new TextElement(() => { AlertFrame(); return _alertShown[i]?.ActionNow ?? ""; }, Color: () => Hud.HudText,
                Width: textW, NoWrap: true, FontSize: actFont) { Bold = true, Ellipsize = true, Height = MathF.Round(actFont * 1.2f) },
        }, Gap: 0.01f);
        var timer = new BubbleElement(new TextElement(() => { AlertFrame(); return _alertTimer[i] ?? ""; },
            Color: () => { AlertFrame(); return _alertTimerCol[i]; }, Width: tw, Align: TextAlign.Right, NoWrap: true,
            FontSize: numFont) { Bold = true, Ellipsize = true, Height = MathF.Round(numFont * 1.15f) })
        { FillColor = MechClear, PaddingX = 0f, PaddingY = 0f, MaxWidth = tw };

        var card = new BubbleElement(new RowElement(new HudElement[]
        {
            new CellElement(text, Width: textW),
            new CellElement(timer, Width: tw),
        }, Gap: gap))
        {
            FillColorFn = () => MechCardFill(AlertCardFillA),
            OutlineColorFn = () => { AlertFrame(); return _alertRing[i]; }, OutlineWidth = 2.5f * f,
            CornerRadius = MathF.Round(12f * f), PaddingX = padX, PaddingY = padY, MaxWidth = w,
        };
        return new RowElement(new HudElement[] { card }, Gap: 0.01f, Justify: RowJustify.Center);
    }
}
