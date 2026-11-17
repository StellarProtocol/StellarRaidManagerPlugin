using System;
using System.Text;

namespace Stellar.RaidManager;

// The ring sequence's STEP PILLS (approved 2026-11-17; port of Experiment c2dca65). The list card used to show
// "Safe: Middle → Middle → Inner", but it moved onto the Menu surface, whose game font draws "→" BLANK — the row read
// "Safe: Middle [Middle] Inner". Now the
// row carries a McRingSeq (≤ 3 steps: safe ring, its colour, its wave's spawn tick) and the HUD draws one pill per step
// (Plugin.MechanicCalloutsHud.Ring.cs): done = dimmed, CURRENT = filled in its ring colour, upcoming = neutral, plus
// the current step's countdown in the timer column.
//   • Timing per difficulty — the SAME manual constants the danger warning already uses (Rule 12, nothing new):
//       Purge (13023)        every step explodes RingPurgeExplodeMs (10 s) after its own wave spawned; current = the
//                            earliest step still before that (RingPurge.cs _rpCur, recomputed at the HUD's refresh).
//       Clash / Brutal       LIVE: only the LATEST wave can be current, exploding RingDangerMs (5 s) after it spawned
//       (13021 / 13022)      (RingDanger.cs); earlier waves have exploded.
//       unknown scene        no explode timing → no current step, "—" in the timer (don't invent timing).
//   • The ROW stays exactly as before for everything else: same key (waves' N.Safe — a moving current step never makes
//     a new row / occurrence / voice cue), same group, same colour, untimed. Only the label lost its " → " / "<b>[x]</b>"
//     markup ("Safe: Middle, Middle, Inner", rm.mech.ring.safe — kept for occurrences / the Table sort, not shown by the list).
//   • No per-scan string building: the key + label are rebuilt only when the shown waves' (N, safe) or the language
//     change.
internal sealed class McRingSeq
{
    public const int Max = 3;
    public int Count;                               // steps shown (1..3)
    public readonly int[]  Ring  = new int[Max];    // safe ring per step: 0 = "?", 1 Inner, 2 Middle, 3 Outer
    public readonly int[]  Color = new int[Max];    // slot colour of that safe ring (cyan for "?")
    public readonly long[] Start = new long[Max];   // TickCount64 the step's wave spawned
    public long ExplodeMs;                          // spawn → explosion: 10 000 Purge, 5 000 Clash/Brutal, 0 = unknown
    public bool Live;                               // Clash/Brutal: only the LATEST step can be current

    /// <summary>Index of the step that explodes next at <paramref name="now"/>; -1 = none (all exploded / no timing).</summary>
    public int CurrentAt(long now)
    {
        if (ExplodeMs <= 0 || Count == 0) return -1;
        if (Live) return now - Start[Count - 1] < ExplodeMs ? Count - 1 : -1;
        for (int i = 0; i < Count; i++) if (now - Start[i] < ExplodeMs) return i;
        return -1;
    }

    /// <summary>Step i has exploded (live: every step before the latest; Purge: its own 10 s are over).</summary>
    public bool IsDone(int i, long now) =>
        ExplodeMs > 0 && ((Live && i < Count - 1) || now - Start[i] >= ExplodeMs);
}

internal sealed partial class MechanicCalloutTracker
{
    // List group caption (Order.cs Flatten) — "Electromagnetic Ring · Safe"; Loc.T hands back the catalog string.
    private static string RingSeqHeader => McText.L("rm.mech.ring.safeHeader");
    private readonly McRingSeq _ringSeq = new();
    private readonly int[] _ringSigN = new int[McRingSeq.Max], _ringSigSafe = new int[McRingSeq.Max];
    private int    _ringSigCount = -1;
    private string _ringRowKey = "", _ringRowLabel = "";
    private string? _ringRowLang;                      // the label is localized: rebuild on a language switch

    // RaidRingRows tail (≥ 1 wave): fill the pill data from the last 3 waves, upsert the row with the cached key/label.
    private void RingSeqRow()
    {
        int first = Math.Max(0, _ringWaves.Count - McRingSeq.Max), n = _ringWaves.Count - first;
        var latest = _ringWaves[^1];
        bool changed = n != _ringSigCount || McText.Loc?.Language != _ringRowLang;
        _ringSeq.Count = n;
        for (int i = 0; i < n; i++)
        {
            var w = _ringWaves[first + i];
            if (_ringSigN[i] != w.N || _ringSigSafe[i] != w.Safe) changed = true;
            _ringSigN[i] = w.N; _ringSigSafe[i] = w.Safe;
            _ringSeq.Ring[i]  = w.Safe == 0 ? 0 : w.Safe - RingInnerId + 1;   // 10310062/63/64 → 1/2/3
            _ringSeq.Color[i] = w.Safe == 0 ? RingUnknownColor : RaidRings[w.Safe].Color;
            _ringSeq.Start[i] = w.StartTick;
        }
        _ringSigCount = n; _ringRowLang = McText.Loc?.Language;
        _ringSeq.Live = !RingPreviewMode();
        _ringSeq.ExplodeMs = SceneId switch { 13023 => RingPurgeExplodeMs, 13021 or 13022 => RingDangerMs, _ => 0 };
        if (changed)
        {
            var key = new StringBuilder("raid:ring:");
            var steps = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                var w = _ringWaves[first + i];
                if (i > 0) { key.Append('-'); steps.Append(", "); }
                key.Append(w.N).Append('.').Append(w.Safe);
                steps.Append(w.Safe == 0 ? "?" : McText.L(RaidRings[w.Safe].ShortKey));
            }
            _ringRowKey = key.ToString(); _ringRowLabel = McText.F("rm.mech.ring.safe", steps.ToString());
        }
        var a = Upsert(_ringRowKey, "Electromagnetic Ring Sequence", _ringRowLabel,
                       latest.Safe == 0 ? RingUnknownColor : RaidRings[latest.Safe].Color, 101, 0, 0);
        a.Ring = _ringSeq;
    }
}
