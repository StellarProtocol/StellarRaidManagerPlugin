using System;
using System.Collections.Generic;
using System.Linq;

namespace Stellar.RaidManager;

// Skill-cast-driven rules (casts sampled in MechanicCalloutTracker.Casts.cs). Timed rows start at the LOCAL tick the
// cast was observed (StartTick), with the fixed windows upstream uses. Geometry upstream attaches to these casts
// (charge half-plane polygon, pizza sectors) is minimap-only and dropped — the row labels don't depend on it.
internal sealed partial class MechanicCalloutTracker
{
    // ── Cursed Tomb: charge clones (CHARGE_SKILLS, 10 s danger window per cast) ──────────────────────────────
    private static readonly Dictionary<int, string> TombChargeSkills = new()
    {
        [3390117] = "Left-Hand Clone", [3390123] = "Left-Hand Clone",
        [3390118] = "Right-Hand Clone", [3390124] = "Right-Hand Clone",
    };

    private void TombChargeCloneRows()
    {
        foreach (var kv in TombChargeSkills)
            foreach (var c in RecentCasts(kv.Key, 10_000))
                Upsert($"tomb:clone:{c.Caster}:{c.SkillId}:{c.Tick}", "Charge Clone", kv.Value, 3, 101, 0, 10_000, c.Tick);
    }

    // ── Towering Ruin: gravity blast (boss skill 111103, 8.5 s) ──────────────────────────────────────────────
    // Upstream requires a BOSS caster; here: any scanned relevant monster that isn't one of the two portals.
    private void GiantGravityRows()
    {
        foreach (var c in RecentCasts(111103, 8500))
        {
            int mid = Ent(c.Caster)?.MonsterId ?? 0;
            if (mid == 2106 || mid == 2107) continue;
            Upsert($"giant:gravity:{c.Caster}:{c.Tick}", "Gravity Blast", "Blast Countdown", 3, 101, 0, 8500, c.Tick);
        }
    }

    // ── Sea-Ringed Reef: pizza (indicator skill 3340245 cast by a live dummy) ────────────────────────────────
    // Colour/label from the round's marker buff on the boss/indicator: purple 883634 wins over orange 883633.
    private void ReefPizzaRows()
    {
        bool live = false;
        foreach (var c in _casts) if (c.SkillId == 3340245 && Ent(c.Caster) != null) { live = true; break; }
        if (!live) return;
        if (AnyBuff(883634, out _))      Upsert("reef:pizza:indicator", "Pizza Danger Zone", "Purple Pizza", 9, 102, 0, 0);
        else if (AnyBuff(883633, out _)) Upsert("reef:pizza:indicator", "Pizza Danger Zone", "Orange Pizza", 5, 102, 0, 0);
        else                             Upsert("reef:pizza:indicator", "Pizza Danger Zone", "Pizza Danger Zone", 3, 102, 0, 0);
    }

    // (Raid electromagnetic ring sequence: Rules.Ring.cs — built from ring-body spawns.)

    // ── Wasteland Court ──────────────────────────────────────────────────────────────────────────────────────
    // Shadow of Heluga phase: newest 470119 cast within 15 s.
    private void WastelandShadowPhaseRow()
    {
        McCast? newest = null;
        foreach (var c in RecentCasts(470119, 15_000)) if (newest == null || c.Tick > newest.Value.Tick) newest = c;
        if (newest is { } n) Upsert("wl:shadow:phase", "Shadow of Heluga", "Shadow of Heluga", 1, 102, 0, 15_000, n.Tick);
    }

    // Near/far chain hits: 470112 (near) / 470113 (far) cast by a chain monster; numbered by their order in the cast
    // log, each shown for 4 s.
    private void WastelandChainCastRows()
    {
        var chain = _casts.Where(c => (c.SkillId == 470112 || c.SkillId == 470113)
                                      && WlChainMonsters.Contains(Ent(c.Caster)?.MonsterId ?? 0))
                          .OrderBy(c => c.Tick).ToList();
        long now = Environment.TickCount64;
        for (int i = 0; i < chain.Count; i++)
        {
            var c = chain[i];
            long age = now - c.Tick;
            if (age < -500 || age > 4000) continue;
            bool near = c.SkillId == 470112;
            Upsert($"wl:chain:cast:{c.Caster}:{c.Tick}", "Near/Far Chain",
                   McText.F("rm.mech.fmt.chainHit", i + 1, McText.T(near ? "Near" : "Far")), near ? 5 : 4, 104, 0, 4000, c.Tick);
        }
    }

    // Void-Mark resolve, cast half (addSettleRow fallback): resolve skill 470132 within 5 s, when no resolve buff.
    private void WastelandSettleCastRow()
    {
        foreach (var c in RecentCasts(470132, 5000))
            Upsert("wl:pair:settle", "Void-Mark Swap", "Void-Mark Resolve", 1, 100, 0, 5000, c.Tick);
    }

    // pairWindowClosed, cast half: a resolve cast (470132) observed since the marks went out.
    private bool PairResolveCastSeen(long earliestServerMs)
    {
        // No synced clock → can't place the marks in local time; fall back to "within the last 25 s window".
        long since = TickFromServer(earliestServerMs), now = Environment.TickCount64;
        foreach (var c in _casts)
            if (c.SkillId == 470132 && (since != 0 ? c.Tick >= since : now - c.Tick <= 25_000)) return true;
        return false;
    }
}
