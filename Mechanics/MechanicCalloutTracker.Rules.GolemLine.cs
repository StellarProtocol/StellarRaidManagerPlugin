using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// Raid P2 "Golem Line" (red carpet) — Mechanic-Callouts.md "Raid P2 golem 'red carpet' line" (port of Experiment's
// Rules.GolemLine.cs, minus its [MechGolem] lock/dash diagnostics). Two Dragonbane Golem Illusions (BaseId 103203)
// cast 10320029; the target is buff 829262 (5 s) on the PLAYER whose FireUuid = the golem. Some waves put 829262 on
// the golems themselves (fire = self) → no player target → nothing at all (no row/corridor/alert). Both golems may
// pick the same player → one name with " ×2" (RowAcc.Counts).
//
// Timing: anchored on T0 = the 829262 server create time. The golem turns to track its target until ~T0+Lock, then
// its facing freezes; it DASHES along the corridor at ~T0+Dash. The buff ends at ~T0+5 s, BEFORE the dash — so each
// target is LATCHED per golem uuid and lives until T0+Dash regardless of the buff (dropped early only when the golem
// despawns). Row countdown = "Locks N.Ns" then "Dash N.Ns" (McRow.PhaseSplitSec, localized format keys).
// Corridor = the golem's LIVE rendered facing from its position to the arena edge (follows the real carpet, freezes
// on lock), falling back to golem → target player when the facing is unreadable.
internal sealed partial class MechanicCalloutTracker
{
    private const int GolemId = 103203, GolemLineBuff = 829262;
    // Manual estimates from ONE recorded wave (recorder 2026-11-02, scene 13023) — no auto-learning; tuned from the
    // Experiment copy's [MechGolem] logs, then baked here.
    internal const float GolemLineLockSec = 4.2f;   // T0 → golem facing locks (carpet stops moving)
    internal const float GolemLineDashSec = 6.2f;   // T0 → golem dashes (≈ ground warning 6.41 s, warn ~0.2 s before buff)
    // Corridor width (world m). ESTIMATE: no width in the 3.7 tables (SkillTable 10320029 / SkillEffectTable 1032002901
    // EffectRange all 0; the warn shape lives in skill assets) — 5 m ≈ the approved prototype's corridor. Manual constant.
    private const float GolemLineWidthM = 5f;
    // Raid grid floor extent (RaidArena.Layout grid lines ±30 × ±22.5): the corridor runs to this edge.
    private const float GridEdgeX = 30f, GridEdgeZ = 22.5f;

    private sealed class GolemLatch
    {
        public long Golem, Target, Create, T0;   // T0 = local tick of the buff's server create (bias-corrected)
    }
    private readonly Dictionary<long, GolemLatch> _golemLines = new();   // golem uuid → its current target
    private long _golemWaveT0;                                           // row anchor: first T0 of the live wave
    private readonly List<long> _golemDrop = new();

    private void ResetGolemLine() { _golemLines.Clear(); _golemWaveT0 = 0; }

    private void RaidGolemLineRows()
    {
        long now = Environment.TickCount64, dashMs = (long)(GolemLineDashSec * 1000f);
        // 1) Latch new targets: 829262 on a PLAYER, cast by a scanned golem (fire = self / monster carrier → skipped).
        foreach (var b in _buffs)
        {
            if (b.BaseId != GolemLineBuff || b.Fire == 0 || b.Fire == b.Target) continue;
            if (Ent(b.Target) is not { IsPlayer: true } || Ent(b.Fire) is not { MonsterId: GolemId } g) continue;
            if (_golemLines.TryGetValue(g.Uuid, out var have) && have.Create == b.Create && have.Target == b.Target) continue;
            long t0 = StableTickFromServer(b.Create);
            t0 = t0 > 0 ? t0 - ClockBiasMs : now;        // + bias: server timestamps lead our clock (HitOffset.cs)
            _golemLines[g.Uuid] = new GolemLatch { Golem = g.Uuid, Target = b.Target, Create = b.Create, T0 = t0 };
        }

        // 2) Drop latches past the dash or whose golem despawned.
        _golemDrop.Clear();
        foreach (var l in _golemLines.Values)
            if (Ent(l.Golem) == null || now - l.T0 >= dashMs) _golemDrop.Add(l.Golem);
        foreach (var k in _golemDrop) _golemLines.Remove(k);

        // 3) One row for the live wave: targets with a per-player count (×2 when both golems picked the same player).
        RowAcc? a = null;
        foreach (var l in _golemLines.Values)
        {
            if (a == null)
            {
                if (_golemWaveT0 == 0 || now - _golemWaveT0 >= dashMs) _golemWaveT0 = l.T0;
                a = Upsert("raid:golemline", "Golem Line", "Target", 3, 110, 0, dashMs, _golemWaveT0);
                a.PhaseSplitSec = GolemLineDashSec - GolemLineLockSec;
                a.Phase1 = "rm.mech.fmt.golemLocks"; a.Phase2 = "rm.mech.fmt.golemDash";   // format keys ("Locks {0}s")
            }
            AddTarget(a, l.Target);
            a.Counts ??= new Dictionary<long, int>();
            a.Counts[l.Target] = a.Counts.TryGetValue(l.Target, out int n) ? n + 1 : 1;
        }
        if (a == null) _golemWaveT0 = 0;
    }

    private bool GolemLineActive(long golem) =>
        _golemLines.TryGetValue(golem, out var l) && Environment.TickCount64 - l.T0 < (long)(GolemLineDashSec * 1000f);

    // Minimap corridors (grid arena; BuildRaidMap): red Style-3 polygon, golem → arena edge along the live facing.
    private void AddGolemLineRegions()
    {
        foreach (var l in _golemLines.Values)
        {
            if (!GolemLineActive(l.Golem) || Ent(l.Golem) is not { HasPos: true } g) continue;
            float dx, dz;
            if (g.FacingFromRot && !float.IsNaN(g.Facing))
            {
                float rad = g.Facing * MathF.PI / 180f;
                dx = MathF.Sin(rad); dz = MathF.Cos(rad);
            }
            else if (Ent(l.Target) is { HasPos: true } t)
            {
                dx = t.Pos.x - g.Pos.x; dz = t.Pos.z - g.Pos.z;
                float len = MathF.Sqrt(dx * dx + dz * dz);
                if (len < 0.01f) continue;
                dx /= len; dz /= len;
            }
            else continue;
            var poly = CorridorPoly(g.Pos.x, g.Pos.z, dx, dz);
            _map.Regions.Add(new MinimapRegion { Kind = MinimapRegionKind.Polygon, Points = poly, Color = 3, Style = 3 });
        }
    }

    // Rectangle of GolemLineWidthM from (x, z) along unit (dx, dz) to where the ray leaves the grid floor (≥ 1 m).
    private static float[] CorridorPoly(float x, float z, float dx, float dz)
    {
        float t = float.MaxValue;
        if (dx > 1e-4f) t = MathF.Min(t, (GridEdgeX - x) / dx); else if (dx < -1e-4f) t = MathF.Min(t, (-GridEdgeX - x) / dx);
        if (dz > 1e-4f) t = MathF.Min(t, (GridEdgeZ - z) / dz); else if (dz < -1e-4f) t = MathF.Min(t, (-GridEdgeZ - z) / dz);
        if (t == float.MaxValue || t < 1f) t = 1f;
        float hw = GolemLineWidthM / 2f, nx = dz * hw, nz = -dx * hw;
        float ex = x + dx * t, ez = z + dz * t;
        return new[] { x + nx, z + nz, ex + nx, ez + nz, ex - nx, ez - nz, x - nx, z - nz };
    }
}
