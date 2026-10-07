using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// Raid "release" buffs = the HIT moment of Share / Decay / Spread (user raid logs, Purge 13023): the boss entity that
// applies 829304-829309 gains 829310 "释放分摊buff" (release Share), 829311 "释放衰减buff" (release Decay),
// 829312 "释放分散buff" (release Spread). On each NEW release instance:
//   • the matching active row is picked — normal or Mirage kind (829304/829305 Share, 829306/829307 Decay,
//     829308/829309 Spread), whichever active row is closest to expiry at the release (smallest non-negative remaining);
//   • the row shows "NOW" for 1.5 s (McRow.HitNowUntil, direct signal). (The Experiment build also logs the raw
//     remaining at the release as hit-offset evidence; that diagnostic stays there — no auto-learning, HitOffset.cs.)
internal sealed partial class MechanicCalloutTracker
{
    private static readonly Dictionary<int, (int Normal, int Mirage)> ReleaseBuffs = new()
    {
        [829310] = (829304, 829305),   // Share
        [829311] = (829306, 829307),   // Decay
        [829312] = (829308, 829309),   // Spread
    };
    private const long HitNowMs = 1500;

    private readonly HashSet<string> _releaseSeen = new();

    private void RaidReleaseCheck()
    {
        foreach (var b in _buffs)
        {
            if (!ReleaseBuffs.TryGetValue(b.BaseId, out var kinds)) continue;
            if (!_releaseSeen.Add($"{b.Target}:{b.BuffUuid}:{b.Create}")) continue;
            if (_releaseSeen.Count > 512) _releaseSeen.Clear();

            McRow? best = null;
            float bestRaw = 0f;
            foreach (var r in _rows.Values)
            {
                if (!r.HasTimer) continue;
                int id = RowBuffId(r);
                if (id != kinds.Normal && id != kinds.Mirage) continue;
                float raw = RemainAtRelease(r, b.Create);
                if (raw < 0f) continue;                       // already expired at the release → not this wave
                if (best == null || raw < bestRaw) { best = r; bestRaw = raw; }
            }
            if (best != null) best.HitNowUntil = Environment.TickCount64 + HitNowMs;
        }
    }

    // Row remaining (s) at the release moment. Both create times are SERVER ms, so (row expiry − release create)
    // is exact — no scan latency, no clock bias. Fallback (either create unknown): the row's live remaining now.
    private static float RemainAtRelease(McRow r, long releaseCreateMs) =>
        r.CreateMs > 0 && releaseCreateMs > 0 ? (r.CreateMs + r.DurationMs - releaseCreateMs) / 1000f : r.RemainSec;

    private static int RowBuffId(McRow r)
    {
        int n = 0;
        while (n < r.Key.Length && char.IsDigit(r.Key[n])) n++;
        return n > 0 && int.TryParse(r.Key.AsSpan(0, n), out int id) ? id : 0;
    }
}
