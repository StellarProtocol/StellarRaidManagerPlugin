using System;
using System.Collections.Generic;
using static Stellar.RaidManager.MechanicCalloutData;

namespace Stellar.RaidManager;

// Per-scene rules that need more than "buff on a player ⇒ label": buffs on MONSTER entities and buff casters
// (FireUuid). Ported from resonance-logs-cn src/routes/minimap-overlay/scenes/<scene>/mechanics.ts; labels are its
// en-US strings, colours its colorSlot numbers, keys its row keys (prefixed per scene). Minimap-only output
// (regions, entity colour rings) has no text form and is dropped. Rules read the poll's snapshot (_ents/_buffs) and
// write through Upsert/AddTarget. Group order: table groups use 0..N, rule groups 100+.
internal sealed partial class MechanicCalloutTracker
{
    private void ApplyRules(SceneDef def)
    {
        switch (def.Kind)
        {
            case SceneKind.CursedTomb:     TombTowerRows(); TombChargeCloneRows(); break;
            case SceneKind.GiantTower:     GiantPortalRow(); GiantGravityRows(); break;
            case SceneKind.Tina:           TinaWudiRows(); TinaPizzaRows(); break;
            case SceneKind.Raid:
                if (!RaidRingGate(def)) { RaidPresetReturnRows(); RaidPhaseMappingRows(); }
                else { _raidPresetRegions.Clear(); _phaseDanger.Clear(); ClearLocalDanger(); }
                RaidPinballCastRows(); RaidPinballBallRows(); RaidRingRows();
                break;
            case SceneKind.SeaReef:        ReefMatrixCalloutRows(); ReefWaveRows(); ReefPizzaRows(); break;
            case SceneKind.WastelandCourt: WastelandBuffRows(); break;
        }
    }

    // ── Cursed Tomb: blue tower activating (addTowerRows) ────────────────────────────────────────────────────
    // A tower monster with neither the gold (884103) nor blue (884102) "complete" buff is ACTIVATING; 40 s countdown.
    // Towers PERSIST all fight (audit: same class as the pinball ball), so entity first-seen is the wrong start — it
    // is long past by the 2nd activation and never re-arms. Start, per tower, best first:
    //   "buff-create" — its earliest activating buff's (884101/884106/884122) server create, converted ONCE
    //                   (StableTickFromServer — a per-scan conversion jitters and re-fires the occurrence);
    //   "transition"  — the tick we saw it go complete → not complete (a new activation without a buff);
    //   "first-seen"  — last resort (fight start); it now simply expires and hides (Expiry.cs), never sits at 0.0 s.
    private static readonly HashSet<int> TombTowers = new() { 33904, 33905 };
    private static readonly HashSet<int> TombTowerActivating = new() { 884101, 884106, 884122 };
    private readonly Dictionary<long, long> _towerSince = new();     // tower uuid → tick it LEFT the complete state
    private readonly Dictionary<long, bool> _towerComplete = new();  // tower uuid → complete at the last pass

    private void TombTowerRows()
    {
        long start = 0, now = Environment.TickCount64;
        foreach (var e in _ents.Values)
        {
            if (!TombTowers.Contains(e.MonsterId)) continue;
            bool complete = HasBuff(e.Uuid, 884103) || HasBuff(e.Uuid, 884102);   // gold / blue complete
            if (_towerComplete.TryGetValue(e.Uuid, out bool was) && was && !complete) _towerSince[e.Uuid] = now;
            _towerComplete[e.Uuid] = complete;
            if (complete) continue;
            long t = 0;
            foreach (var b in _buffs)
            {
                if (b.Target != e.Uuid || !TombTowerActivating.Contains(b.BaseId)) continue;
                long bt = StableTickFromServer(b.Create);
                if (bt > 0 && (t == 0 || bt < t)) t = bt;
            }
            if (t == 0 && _towerSince.TryGetValue(e.Uuid, out long since)) t = since;   // "transition"
            if (t == 0) t = e.FirstSeenTick;                                              // "first-seen"
            if (start == 0 || t > start) start = t;    // the NEWEST activation is the live one
        }
        if (start > 0) Upsert("tomb:tower:activating", "Blue Tower", "Blue Tower Activating", 7, 100, 0, 40_000, start);
    }

    // ── Raid: pinball cast (addPinballRows, buff half) — 829314 on any entity (usually the boss), no targets ─
    private void RaidPinballCastRows()
    {
        foreach (var b in _buffs)
            if (b.BaseId == 829314)
                Upsert("raid:pinball:cast", "Pinball", "Pinball Cast", 5, 100, b.Create, b.Dur);
    }

    // ── Sea-Ringed Reef: matrix callout (buildMatrixMechanicView) ────────────────────────────────────────────
    // 522602 on a player, cast BY a matrix monster (4639) — the row takes the colour of that matrix's rune buff
    // (883707-883710 = A/B/C/D on the matrix itself), one row per (source matrix, colour).
    private static readonly Dictionary<int, int> ReefRuneColor = new()
    { [883707] = 1, [883708] = 7, [883709] = 6, [883710] = 0 };

    private void ReefMatrixCalloutRows()
    {
        foreach (var b in _buffs)
        {
            if (b.BaseId != 522602) continue;
            var src = Ent(b.Fire);
            int color = 0;
            if (src != null && src.MonsterId == 4639)
                foreach (var r in _buffs)
                    if (r.Target == src.Uuid && ReefRuneColor.TryGetValue(r.BaseId, out int c)) { color = c; break; }
            var a = Upsert($"reef:matrix:{src?.Uuid ?? 0}:{color}", "Nabo Matrix Callout", "Mechanism Callout",
                           color, 100, b.Create, b.Dur);
            AddTarget(a, b.Target);
        }
    }

    // ── Wasteland Court (buff-sourced parts) ─────────────────────────────────────────────────────────────────
    private static readonly HashSet<int> WlEnergyBalls = new() { 884640, 884668, 884669, 884670, 884671 };
    private static readonly HashSet<int> WlChainMonsters = new() { 884606, 884607 };
    private static readonly int[] WlEnergyColors = { 0, 2, 6, 8, 10 };

    private void WastelandBuffRows()
    {
        WastelandPairRows();
        if (!WastelandSettleBuffRow()) WastelandSettleCastRow();
        WastelandShadowPhaseRow();
        WastelandChainCastRows();
        WastelandOrbRow();
        WastelandEnergyRows();
        WastelandChainOverview();
    }

    // Void-Mark resolve (addSettleRow, buff half): 884660 anywhere → untargeted row, 5 s if the buff has no duration.
    private bool WastelandSettleBuffRow()
    {
        if (!AnyBuff(884660, out var s)) return false;
        Upsert("wl:pair:settle", "Void-Mark Swap", "Void-Mark Resolve", 1, 100, s.Create, s.Dur > 0 ? s.Dur : 5000);
        return true;
    }

    // Swap orbs (addOrbRegions): any orb monster 470131 carrying 884664 → "Swapping" (3 s fallback duration).
    private void WastelandOrbRow()
    {
        long create = 0, dur = 0; bool any = false;
        foreach (var b in _buffs)
        {
            if (b.BaseId != 884664 || Ent(b.Target)?.MonsterId != 470131) continue;
            any = true;
            if (b.Create > 0 && (create == 0 || b.Create < create)) create = b.Create;
            if (b.Dur > dur) dur = b.Dur;
        }
        if (any) Upsert("wl:orb:active", "Swap Orbs", "Swapping", 1, 101, create, dur > 0 ? dur : 3000);
    }

    // Energy orb tracking (addShadowRows, buff half): 884641 on a player whose CASTER is a live energy-ball monster;
    // newest assignment per ball, then newest per player; colour = the player's slot (local first, then uuid order).
    private void WastelandEnergyRows()
    {
        var latestByBall = new Dictionary<long, McBuff>();
        foreach (var b in _buffs)
        {
            if (b.BaseId != 884641) continue;
            var ball = Ent(b.Fire);
            if (ball == null || !WlEnergyBalls.Contains(ball.MonsterId) || Ent(b.Target) == null) continue;
            if (!latestByBall.TryGetValue(ball.Uuid, out var prev) || b.Create >= prev.Create) latestByBall[ball.Uuid] = b;
        }
        if (latestByBall.Count == 0) return;
        var latestByTarget = new Dictionary<long, McBuff>();
        foreach (var b in latestByBall.Values)
            if (!latestByTarget.TryGetValue(b.Target, out var prev) || b.Create >= prev.Create) latestByTarget[b.Target] = b;

        var slots = TeamColorSlots(WlEnergyColors);
        foreach (var b in latestByTarget.Values)
        {
            int color = slots.TryGetValue(b.Target, out int c) ? c : WlEnergyColors[0];
            var a = Upsert($"wl:shadow:target:{b.Target}", "Shadow of Heluga", "Energy Orb Tracking", color, 102,
                           b.Create, b.Dur > 0 ? b.Dur : 5000);
            AddTarget(a, b.Target);
        }
    }

    // Near/far chain overview (addChainRows, buff half): count near (884609) / far (884610) marks on chain monsters.
    private void WastelandChainOverview()
    {
        int near = 0, far = 0;
        foreach (var b in _buffs)
        {
            if (b.BaseId != 884609 && b.BaseId != 884610) continue;
            if (!WlChainMonsters.Contains(Ent(b.Target)?.MonsterId ?? 0)) continue;
            if (b.BaseId == 884609) near++; else far++;
        }
        if (near + far > 0) Upsert("wl:chain:overview", "Near/Far Chain", McText.F("rm.mech.fmt.chainOverview", near, far), 4, 104, 0, 0);
    }

    // ── Team helpers ─────────────────────────────────────────────────────────────────────────────────────────
    // Upstream "local or teammate": the local player plus PartyRoster members (by roleId) among the scanned players.
    private bool IsTeam(long uuid)
    {
        long local = _services.CombatSnapshot.LocalEntityId.Value;
        if (local != 0 && (uuid == local || (uuid >> 16) == (local >> 16))) return true;
        try
        {
            var members = _services.PartyRoster?.Members;
            if (members != null)
                foreach (var m in members) if (m.CharId == (uuid >> 16)) return true;
        }
        catch { }
        return false;
    }

    // energyPlayerColorSlots: team players sorted local-first then by uuid string, cycling through `palette`.
    private Dictionary<long, int> TeamColorSlots(int[] palette)
    {
        long local = _services.CombatSnapshot.LocalEntityId.Value;
        var team = new List<long>();
        foreach (var e in _ents.Values) if (e.IsPlayer && IsTeam(e.Uuid)) team.Add(e.Uuid);
        team.Sort((x, y) =>
        {
            bool lx = local != 0 && (x >> 16) == (local >> 16), ly = local != 0 && (y >> 16) == (local >> 16);
            if (lx != ly) return lx ? -1 : 1;
            return string.CompareOrdinal(x.ToString(), y.ToString());
        });
        var map = new Dictionary<long, int>();
        for (int i = 0; i < team.Count; i++) map[team[i]] = palette[i % palette.Length];
        return map;
    }
}
