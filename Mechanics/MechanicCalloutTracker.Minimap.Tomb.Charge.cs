using System;
using System.Collections.Generic;
using System.Linq;

namespace Stellar.RaidManager;

// Cursed Tomb charge-clone danger halves — selection + dedupe. In game (Experiment) the map sometimes went WHOLLY red
// while the real mechanic always leaves a safe strip. Code-review suspects, in order:
//   1. FACING (most likely): the half-plane is "left/right of the clone's charge line", so a wrong heading flips or
//      rotates it. The cast now captures the caster's MODEL ROTATION at detection (Casts.cs), not the scan's AttrDir
//      (which in Tina read one identical default value for every dummy).
//   2. STACKING: every charge cast in the 10 s window painted its own half — a clone that charges again (or the
//      "instant" variant right after the normal one) added a second half instead of replacing the first.
//   3. DUPLICATES: one cast detected twice (no cast uuid → AttrSkillId fallback).
// Rules here: (a) a cast is accepted once (same caster + cast uuid; without a uuid, same caster + skill within
// 1.5 s); (b) each clone keeps only its NEWEST charge; (c) the L and R clones' halves are both kept (the intended
// pair — their union leaves the strip between the two lines). (The coverage/charge logs stay in Experiment.)
internal sealed partial class MechanicCalloutTracker
{
    private readonly struct ChargeHalf
    {
        public readonly McCast Cast;
        public readonly float[] Poly;
        public ChargeHalf(McCast c, float[] poly) { Cast = c; Poly = poly; }
    }

    private List<ChargeHalf> ActiveChargeHalves(MinimapArenaSpec spec)
    {
        long now = Environment.TickCount64;
        var newestPerCaster = new Dictionary<long, McCast>();
        var accepted = new List<McCast>();
        foreach (var c in _casts)
        {
            if (!TombChargeSkills.ContainsKey(c.SkillId)) continue;
            long age = now - c.Tick;
            if (age < -500 || age > 10_000 || !c.HasPos || float.IsNaN(c.Facing)) continue;
            // (a) duplicate detection of the same cast.
            bool dup = accepted.Any(a => a.Caster == c.Caster &&
                (c.CastUuid != 0 ? a.CastUuid == c.CastUuid : a.SkillId == c.SkillId && Math.Abs(a.Tick - c.Tick) < 1500));
            if (dup) continue;
            accepted.Add(c);
            // (b) a clone's new charge replaces its previous half.
            if (!newestPerCaster.TryGetValue(c.Caster, out var prev) || c.Tick > prev.Tick) newestPerCaster[c.Caster] = c;
        }

        var halves = new List<ChargeHalf>();
        foreach (var c in newestPerCaster.Values.OrderBy(c => c.Tick))
        {
            // English skill label ("Left-Hand Clone" / "Right-Hand Clone") — the table value, not the localized row.
            bool left = TombChargeSkills[c.SkillId].StartsWith("Left", StringComparison.Ordinal);
            var poly = ChargeHalfPoly(spec, c.X, c.Z, c.Facing, left);
            if (poly != null) halves.Add(new ChargeHalf(c, poly));
        }
        return halves;
    }
}
