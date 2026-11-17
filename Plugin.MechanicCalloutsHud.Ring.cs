using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// Raid Electromagnetic Ring sequence row = STEP PILLS (approved 2026-11-17; port of Experiment c2dca65). The row
// text used to join the steps with " → ", and the list card is on the Menu surface, whose game font draws "→" BLANK
// ("Safe: Middle [Middle] Inner").
//   [■] (1 Middle) (2 Middle) (3 Inner)                  4.2s
// swatch = the CURRENT step's safe-ring colour; up to 3 fixed pills (unused slots hidden — the pool never changes, so
// nothing jumps inside a sequence), each a small rounded BubbleElement "<n> <ring>" ("?" = undecided):
//   done (already exploded)  dark backing (black α.45), muted text
//   CURRENT                  filled with its ring colour, dark bold text
//   upcoming                 dark backing (black α.45), normal theme text
// The dark backing keeps done/upcoming readable over ANY drain colour (the old neutral α.06/.14 fills with muted text
// vanished into the row drain fill in-game, 2026-11-17).
// Timer + drain fill = time until the CURRENT step explodes, from the tracker's existing manual timing (McRingSeq:
// Purge 10 s after each wave's spawn, Clash/Brutal 5 s after the latest wave's spawn); same format / colour steps as
// every other row. No current step (all exploded, or an unknown scene without timing) → "—", steady fill.
// Perf: every pill is its own (innermost) bubble, so a pill text change re-measures that pill only; the texts come
// from a per-language table (no per-refresh string building) and only change when the sequence does.
public sealed partial class Plugin
{
    private const int MechPills = McRingSeq.Max;
    private const float MechFontPill = 11f, MechPillPadX = 5f, MechPillGap = 3f;
    private static readonly ColorRgba MechPillDark = new(0.07f, 0.07f, 0.09f, 1f);   // text on a lit ring colour
    private static readonly ColorRgba MechPillBack = new(0f, 0f, 0f, 0.45f);         // done / upcoming pill backing

    private readonly bool[]      _mechIsSeq     = new bool[MechHudSlots];
    private readonly ColorRgba[] _mechSeqSwatch = new ColorRgba[MechHudSlots];
    private readonly bool[]      _mechPillOn    = new bool[MechHudSlots * MechPills];
    private readonly string[]    _mechPillText  = new string[MechHudSlots * MechPills];
    private readonly ColorRgba[] _mechPillFill  = new ColorRgba[MechHudSlots * MechPills];
    private readonly ColorRgba[] _mechPillInk   = new ColorRgba[MechHudSlots * MechPills];

    // "<n> <ring>" for step n (1..3) × ring (0 "?", 1 Inner, 2 Middle, 3 Outer) — ring names from the existing
    // rm.mech.ring.inner/middle/outer keys; rebuilt only on a language switch.
    private static readonly string[] MechRingKeys = { "", "rm.mech.ring.inner", "rm.mech.ring.middle", "rm.mech.ring.outer" };
    private readonly string[] _mechPillNames = new string[MechPills * 4];
    private string? _mechPillLang;

    private string[] MechPillNames()
    {
        var lang = _loc.Language;
        if (_mechPillLang != null && string.Equals(lang, _mechPillLang, StringComparison.Ordinal)) return _mechPillNames;
        _mechPillLang = lang ?? "";
        for (int r = 0; r < 4; r++)
        {
            string name = r == 0 ? "?" : _loc.T(MechRingKeys[r]);
            for (int s = 0; s < MechPills; s++) _mechPillNames[s * 4 + r] = (s + 1).ToString() + " " + name;
        }
        return _mechPillNames;
    }

    private bool MechIsSeq(int i)            { MechHudFrame(); return i < _mechVisible && _mechIsSeq[i]; }
    private bool MechPillOn(int i, int p)    { MechHudFrame(); return _mechPillOn[i * MechPills + p]; }
    private string MechPillText(int i, int p) { MechHudFrame(); return _mechPillText[i * MechPills + p] ?? ""; }
    private ColorRgba? MechPillFill(int i, int p) { MechHudFrame(); return _mechPillFill[i * MechPills + p]; }
    private ColorRgba? MechPillInk(int i, int p)  { MechHudFrame(); return _mechPillInk[i * MechPills + p]; }

    // The pills, left-aligned in the label cell (the trailing weighted cell takes the rest of the width).
    private HudElement MechPillRow(int i, float l1)
    {
        var items = new HudElement[MechPills + 1];
        for (int p = 0; p < MechPills; p++) items[p] = MechPill(i, p, MathF.Round(l1 - 2f));
        items[MechPills] = new CellElement(new SpacerElement(0f, 0f), Weight: 1f);
        return new RowElement(items, Gap: MechGap(MechPillGap));
    }

    private HudElement MechPill(int i, int p, float h)
    {
        var text = new TextElement(() => MechPillText(i, p), Color: () => MechPillInk(i, p), NoWrap: true,
            FontSize: MF(MechFontPill)) { Bold = true, Height = h };
        // Round ends: radius side/2 − 1 (a 9-slice bubble's borders are a min size — WindowBuilder-Patterns.md BubbleElement).
        return new ConditionalElement(() => MechPillOn(i, p), new BubbleElement(text)
        {
            FillColorFn = () => MechPillFill(i, p), CornerRadius = MathF.Max(1f, MathF.Floor(h / 2f) - 1f),
            PaddingX = MathF.Round(MS(MechPillPadX)), PaddingY = 0f,
        });
    }

    // MechHudFrame, ring sequence row k: pills + swatch + the current step's timer / drain fill.
    private void MechSeqState(int k, McRow row, McRingSeq seq, float barA)
    {
        long now = Environment.TickCount64;
        var c = Hud;
        var names = MechPillNames();
        int cur = seq.CurrentAt(now);
        _mechIsSeq[k] = true;
        for (int p = 0; p < MechPills; p++)
        {
            int j = k * MechPills + p;
            _mechPillOn[j] = p < seq.Count;
            if (p >= seq.Count) continue;
            _mechPillText[j] = names[p * 4 + Math.Clamp(seq.Ring[p], 0, 3)];
            if (p == cur) { _mechPillFill[j] = MechanicCalloutData.SlotColor(seq.Color[p]) with { A = 1f }; _mechPillInk[j] = MechPillDark; }
            else if (seq.IsDone(p, now)) { _mechPillFill[j] = MechPillBack; _mechPillInk[j] = c.MenuMuted; }
            else { _mechPillFill[j] = MechPillBack; _mechPillInk[j] = c.HudText; }
        }
        var mc = MechanicCalloutData.SlotColor(cur >= 0 ? seq.Color[cur] : row.Color);
        _mechSeqSwatch[k] = mc;
        if (cur < 0)
        {
            // No reliable explode time (all exploded / unknown scene): steady quieter fill and "—", never a guess.
            _mechFrac[k] = 1f; _mechFill[k] = mc with { A = barA * 0.6f }; _mechRing[k] = false;
            _mechTimer[k] = MechNoTimer; _mechTimerCol[k] = c.MenuMuted;
            return;
        }
        float total = seq.ExplodeMs / 1000f;
        float shown = Math.Clamp((seq.Start[cur] + seq.ExplodeMs - now) / 1000f, 0f, total);
        int step = MechStep(shown);
        _mechFrac[k] = total > 0.05f ? shown / total : 0f;
        _mechFill[k] = MechStepColor(step, mc) with { A = barA };
        _mechRing[k] = step == 2;
        _mechTimer[k] = MechSecText(shown);
        _mechTimerCol[k] = MechStepColor(step, c.HudText);
    }
}
