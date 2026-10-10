using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// Wasteland Court minimap — port of scenes/s4-wasteland-court/{arena.ts, index.ts, mechanics.ts}: swap orbs, star
// pools, energy balls with their target lines, near/far chain monsters, boss / clone markers. Void-Mark pair players,
// wheel and energy-orb targets colour through their rows.
internal sealed partial class MechanicCalloutTracker
{
    private const int WlBossA = 4701, WlBossB = 4711, WlClone = 4702, WlSwapOrb = 470131, WlStarPool = 884642;

    private void BuildWastelandMap()
    {
        ClearMap();
        var spec = MinimapArenas.Wasteland;
        spec.Apply(_map);
        var monsterColor = new Dictionary<long, int>();

        // Swap orbs (addOrbRegions): r 2.5 disc, green 1 while it carries the swap buff 884664, else orange 5.
        foreach (var e in _ents.Values)
        {
            if (e.MonsterId != WlSwapOrb || !e.HasPos) continue;
            int c = HasBuff(e.Uuid, 884664) ? 1 : 5;
            monsterColor[e.Uuid] = c;
            _map.Regions.Add(MinimapRegion.Disc(e.Pos.x, e.Pos.z, 2.5f, c));
        }

        // Shadow (addShadowRows): star pools r 1.5 green; energy balls r 1.2 coloured by their newest target
        // assignment (884641 cast by the ball; colour = the target player's team slot) else amber 11, plus a 3 px line
        // ball → target.
        var slots = TeamColorSlots(WlEnergyColors);
        var latestByBall = new Dictionary<long, McBuff>();
        foreach (var b in _buffs)
        {
            if (b.BaseId != 884641) continue;
            var ball = Ent(b.Fire);
            if (ball == null || !WlEnergyBalls.Contains(ball.MonsterId) || Ent(b.Target) == null) continue;
            if (!latestByBall.TryGetValue(ball.Uuid, out var prev) || b.Create >= prev.Create) latestByBall[ball.Uuid] = b;
        }
        foreach (var e in _ents.Values)
        {
            if (!e.HasPos) continue;
            if (e.MonsterId == WlStarPool)
            {
                monsterColor[e.Uuid] = 1;
                _map.Regions.Add(MinimapRegion.Disc(e.Pos.x, e.Pos.z, 1.5f, 1));
                continue;
            }
            if (!WlEnergyBalls.Contains(e.MonsterId)) continue;
            int c = 11;
            McEnt? target = null;
            if (latestByBall.TryGetValue(e.Uuid, out var a))
            {
                c = slots.TryGetValue(a.Target, out int sc) ? sc : WlEnergyColors[0];
                target = Ent(a.Target);
            }
            monsterColor[e.Uuid] = c;
            _map.Regions.Add(MinimapRegion.Disc(e.Pos.x, e.Pos.z, 1.2f, c));
            if (target is { HasPos: true })
                _map.Regions.Add(MinimapRegion.Line(e.Pos.x, e.Pos.z, target.Pos.x, target.Pos.z, c, 3f));
        }

        // Near/far chain (addChainRows): chain monsters marked near (884609) orange 5 / far (884610) cyan (upstream 4 —
        // monsters, not a player highlight, so cyan is allowed); a chain monster casting its hit in the last 4 s
        // takes that hit's colour.
        foreach (var b in _buffs)
        {
            if (!WlChainMonsters.Contains(Ent(b.Target)?.MonsterId ?? 0)) continue;
            if (b.BaseId == 884609) monsterColor[b.Target] = 5;
            else if (b.BaseId == 884610) monsterColor[b.Target] = MechanicCalloutData.CyanSlot;
        }
        long now = Environment.TickCount64;
        foreach (var c in _casts)
        {
            if ((c.SkillId != 470112 && c.SkillId != 470113) || !WlChainMonsters.Contains(Ent(c.Caster)?.MonsterId ?? 0)) continue;
            long age = now - c.Tick;
            if (age >= -500 && age <= 4000) monsterColor[c.Caster] = c.SkillId == 470112 ? 5 : MechanicCalloutData.CyanSlot;
        }

        AddDots(e => InSpecArena(spec, e), e =>
            e.MonsterId == WlBossA || e.MonsterId == WlBossB ? 3
            : e.MonsterId == WlClone ? 5
            : monsterColor.TryGetValue(e.Uuid, out int c) ? c : -1);
    }
}
