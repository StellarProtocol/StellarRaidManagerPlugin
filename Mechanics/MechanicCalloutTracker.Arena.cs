using System.Collections.Generic;
using UnityEngine;
using static Stellar.RaidManager.MechanicCalloutData;

namespace Stellar.RaidManager;

// Local position + raid arena selection (the behaviour half of Experiment's MechanicCalloutTracker.Minimap.Diag.cs;
// its readout/log half stays in Experiment).
//
// ARENA: upstream picks ring/grid purely by the local player's world Y (~150 vs ~400). Our Y comes from the client
// model position, whose scale/offset vs upstream's server coordinates was unverified (first in-game report: grid
// highlights never showed). So mechanics decide first — a grid-only buff in the scan ⇒ grid, a ring-only signal
// ⇒ ring — and Y is only a low-confidence fallback. Rows are hidden for the ring arena ONLY when that verdict came
// from a mechanic signal (never from Y, never when unknown).
internal sealed partial class MechanicCalloutTracker
{
    private RaidArena.Kind _raidArena = RaidArena.Kind.Unknown;
    private bool   _arenaConfident;
    private bool   _hasLocalPos, _localDead;
    private Vector3 _localPos;

    // Grid-only (upstream builds these only outside the ring arena): Phase, Phase-Mapping, Preset Return link/count.
    private static readonly HashSet<int> RaidGridSignals = new()
    { 829214, 829215, 829327, 829328, 829329, 829330, 829331, 829332, 829318, 829372, 829373, 829374 };
    // Ring-only: the electromagnetic ring virtual bodies / their casts. (Pinball is NOT a ring signal — upstream
    // runs it "in any phase", outside the non-ring block.)
    private static readonly int[] RaidRingBodyIds = { 10310062, 10310063, 10310064 };

    // Upstream builds Phase / Phase-Mapping / Preset-Return / callout rows only OUTSIDE the ring arena — applied only
    // when the ring verdict is mechanic-backed.
    private bool RaidRingGate(SceneDef def) =>
        def.Kind == SceneKind.Raid && _raidArena == RaidArena.Kind.Ring && _arenaConfident;

    // Local player's world position this scan (every scene: arena filters, raid arena-by-Y, danger-tile MOVE OFF).
    private void UpdateLocalPos()
    {
        long local = _services.CombatSnapshot.LocalEntityId.Value;
        _hasLocalPos = false; _localDead = false;
        foreach (var e in _ents.Values)
            if (e.IsPlayer && e.HasPos && local != 0 && (e.Uuid >> 16) == (local >> 16)) { _localPos = e.Pos; _hasLocalPos = true; _localDead = e.IsDead; break; }
    }

    private void UpdateRaidArena()
    {
        foreach (var b in _buffs)
            if (RaidGridSignals.Contains(b.BaseId)) { SetArena(RaidArena.Kind.Grid, true); return; }
        foreach (var e in _ents.Values)
            foreach (int id in RaidRingBodyIds)
                if (e.MonsterId == id) { SetArena(RaidArena.Kind.Ring, true); return; }
        long now = System.Environment.TickCount64;
        foreach (var c in _casts)
            foreach (int id in RaidRingBodyIds)
                if (c.SkillId == id && now - c.Tick <= 30_000) { SetArena(RaidArena.Kind.Ring, true); return; }
        SetArena(RaidArena.ByY(_hasLocalPos ? _localPos.y : null), false);
    }

    private void SetArena(RaidArena.Kind kind, bool confident)
    {
        _raidArena = kind; _arenaConfident = confident;
    }

    // Dot visibility: same floor as the local player (|ΔY| ≤ 50) — scale-independent, unlike upstream's absolute
    // ~150/~400 bands. No local position → no filter.
    private bool SameFloor(float y) => !_hasLocalPos || Mathf.Abs(y - _localPos.y) <= 50f;
}
