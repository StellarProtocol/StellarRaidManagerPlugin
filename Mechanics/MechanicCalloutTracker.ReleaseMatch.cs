using System;
using System.Collections.Generic;
using static Stellar.RaidManager.MechanicCalloutData;

namespace Stellar.RaidManager;

// Raid "Mirage" Share / Decay / Spread buffs (829305 / 829307 / 829309) are TWO different mechanics in the logs
// (user + Experiment log 2026-10-07, raid 13023 P3):
//   • REAL (later P3 rounds, the floor-placed dodgeable Decay the players see as a normal Decay): the boss gains its
//     release buff (829310 / 829311 / 829312) and ~0.3 s later the "mirage" buff lands on the players. The game reuses
//     the mirage buff id for it, so the id alone mislabels it "Mirage Decay".
//   • MIRAGE (the delayed replay): 829305 / 829307 / 829309 land ~3 s apart with NO boss release buff.
// Rule: an occurrence whose create time is within ±ReleaseMatchMs of a matching release buff's create time is REAL →
// shown in the normal "Share / Decay / Spread" group with the normal label/colour (829304 / 829306 / 829308 defs).
// Otherwise it stays Mirage. The row key gets a ":r" suffix (a real and a mirage occurrence must never merge); the
// leading digits stay the buff id, so the hit-offset key (b829307 …) and Release.cs matching are unchanged.
// Release creates are REMEMBERED (the release lives only ~2 s on the boss; the player buff 7-17 s), so the verdict
// holds for the whole row. Usually the release arrives first; if the player buff is seen first the row starts as
// Mirage and switches on the scan the release appears. (The Experiment copy also logs each verdict.)
internal sealed partial class MechanicCalloutTracker
{
    // mirage id → (boss release id, normal-kind buff id whose table def the real occurrence borrows)
    private static readonly Dictionary<int, (int Release, int Normal)> MirageRelease = new()
    {
        [829305] = (829310, 829304),   // Share
        [829307] = (829311, 829306),   // Decay
        [829309] = (829312, 829308),   // Spread
    };
    private const long ReleaseMatchMs = 1500;    // |player buff create − release create|
    private const long ReleaseKeepMs  = 60_000;  // remembered release creates (server ms)

    private readonly Dictionary<int, List<long>> _releaseCreates = new();   // release id → recent create ms

    // Each scan: remember every release buff's create time (any carrier — the boss), drop old ones.
    private void RecordReleases()
    {
        long newest = 0;
        foreach (var b in _buffs)
        {
            if (b.Create <= 0 || !ReleaseBuffs.ContainsKey(b.BaseId)) continue;
            if (!_releaseCreates.TryGetValue(b.BaseId, out var list)) _releaseCreates[b.BaseId] = list = new List<long>();
            if (!list.Contains(b.Create)) list.Add(b.Create);
            if (b.Create > newest) newest = b.Create;
        }
        long srv = ServerNowMs();
        long cut = (srv > 0 ? srv : newest) - ReleaseKeepMs;
        if (cut <= 0) return;
        foreach (var list in _releaseCreates.Values) list.RemoveAll(c => c < cut);
    }

    // True = this mirage-id buff instance is the REAL mechanic; `normal` = the table def to show it with.
    private bool IsReleaseBacked(SceneDef def, in McBuff b, out CalloutDef normal)
    {
        normal = null!;
        if (!MirageRelease.TryGetValue(b.BaseId, out var m) || !def.Buffs.TryGetValue(m.Normal, out var nd)) return false;
        long delta = long.MaxValue;
        if (b.Create > 0 && _releaseCreates.TryGetValue(m.Release, out var list))
            foreach (long rc in list)
                if (Math.Abs(rc - b.Create) <= ReleaseMatchMs && Math.Abs(rc - b.Create) < Math.Abs(delta)) delta = rc - b.Create;
        bool real = delta != long.MaxValue;
        if (real) normal = nd;
        return real;
    }

    private void ResetReleaseMatch() => _releaseCreates.Clear();
}
