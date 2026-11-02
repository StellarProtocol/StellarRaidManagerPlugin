using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// Raid "monster aims at a buffed player" lines — ONE shared rule, configured per mechanic (AimLineDef); port of
// Experiment's Rules.AimLine.cs minus its [MechGolem] / [MechCannon] lock/fire diagnostics.
//   • Golem Line (P2 red carpet, Mechanic-Callouts.md "Raid P2 golem 'red carpet' line"): two Dragonbane Golem
//     Illusions (103203) cast 10320029; target = buff 829262 (5 s) on the PLAYER whose FireUuid = the golem. Grid arena.
//   • Cannon Laser (Mechanic-Callouts.md "Raid corner-cannon lasers"): four corner cannons (103110 Right / 103111
//     Left); target = buff 829136 (5 s) on one PLAYER per cannon, FireUuid = the cannon. 150-band (ring) arena.
// Shared behaviour: only PLAYER carriers fired by a scanned caster of the mechanic count (fire = self / monster carrier
// → nothing: no row, corridor or alert); several casters may pick the same player → one name with " ×N" (RowAcc.Counts).
// Timing is anchored on T0 = the buff's server create time: the caster turns to track its target until ~T0+Lock, then
// its facing freezes; it fires (golem: dashes) at ~T0+Fire. The buff ends BEFORE that → each target is LATCHED per
// caster uuid and lives until T0+Fire regardless of the buff (dropped early only when the caster despawns). Row
// countdown = "Locks N.Ns" then "<Phase2> N.Ns" (McRow.PhaseSplitSec, localized format keys). Corridor = the caster's
// LIVE rendered facing from its position to the def's arena edge, falling back to caster → target player when the
// facing is unreadable. CasterMarker: the golem gets a red triangle while aiming; the cannons are hidden in game, so
// they get NO marker and are suppressed from the monster-dot layer altogether (AddDots).
internal sealed partial class MechanicCalloutTracker
{
    private const int GolemId = 103203;
    // Manual estimates (no auto-learning; tuned from the Experiment copy's lock/fire logs, then baked here).
    // Golem Line — ONE recorded wave (recorder 2026-11-02, scene 13023):
    internal const float GolemLineLockSec = 4.2f;   // T0 → golem facing locks (carpet stops moving)
    internal const float GolemLineDashSec = 6.2f;   // T0 → golem dashes (≈ ground warning 6.41 s, warn ~0.2 s before buff)
    // Corridor width (world m). ESTIMATE: no width in the 3.7 tables (SkillTable 10320029 / SkillEffectTable 1032002901
    // EffectRange all 0; the warn shape lives in skill assets) — 5 m ≈ the approved prototype's corridor. Manual constant.
    private const float GolemLineWidthM = 5f;
    // Cannon Laser — two recorded waves (recorder 2026-11-02 20:01, scene 13023): lock ~T0+3.9–4.0 s; the laser fires at
    // ~warn+6.0 s ≈ T0+5.85 s (inferred from the telegraph — no cast/bullet event marks the shot).
    internal const float CannonLaserLockSec = 4.0f;
    internal const float CannonLaserFireSec = 5.85f;
    private const float CannonLaserWidthM = 6f;     // user-chosen (2026-11-02); no width in the tables either
    // Corridor edges (half-extents around world 0,0): raid grid floor lines ±30 × ±22.5 (RaidArena.Layout); the 150-band
    // arena's cannons stand at x −27.47/32.53, z −27.5/32.5 → a ±33 square encloses them (the corridor runs to its far side).
    private const float GridEdgeX = 30f, GridEdgeZ = 22.5f, CannonEdge = 33f;

    private sealed class AimLineDef
    {
        public string Key = "", Group = "", Label = "Target", Phase2Key = "";
        public int    Buff, Order;
        public int[]  Casters = Array.Empty<int>();
        public float  LockSec, FireSec, WidthM, EdgeX, EdgeZ;
        public bool   CasterMarker;                     // draw the aiming caster (red triangle); false = never draw it
        public readonly Dictionary<long, AimLatch> Latches = new();   // caster uuid → its current target
        public long   WaveT0;                           // row anchor: first T0 of the live wave
        public long   FireMs => (long)(FireSec * 1000f);
        public bool   IsCaster(int monsterId) => Array.IndexOf(Casters, monsterId) >= 0;
    }

    private sealed class AimLatch
    {
        public long Caster, Target, Create, T0;  // T0 = local tick of the buff's server create (bias-corrected)
    }

    private readonly AimLineDef _golemLine = new()
    {
        Key = "raid:golemline", Group = "Golem Line", Phase2Key = "rm.mech.fmt.golemDash",
        Buff = 829262, Order = 110, Casters = new[] { GolemId }, LockSec = GolemLineLockSec, FireSec = GolemLineDashSec,
        WidthM = GolemLineWidthM, EdgeX = GridEdgeX, EdgeZ = GridEdgeZ, CasterMarker = true,
    };
    private readonly AimLineDef _cannonLaser = new()
    {
        Key = "raid:cannonlaser", Group = "Cannon Laser", Phase2Key = "rm.mech.fmt.fire",
        Buff = 829136, Order = 112, Casters = new[] { 103110, 103111 }, LockSec = CannonLaserLockSec, FireSec = CannonLaserFireSec,
        WidthM = CannonLaserWidthM, EdgeX = CannonEdge, EdgeZ = CannonEdge, CasterMarker = false,
    };
    private AimLineDef[]? _aimLines;
    private AimLineDef[] AimLines => _aimLines ??= new[] { _golemLine, _cannonLaser };
    private readonly List<long> _aimDrop = new();

    private void ResetAimLines() { foreach (var d in AimLines) { d.Latches.Clear(); d.WaveT0 = 0; } }

    private void RaidAimLineRows() { foreach (var d in AimLines) AimLineRows(d); }

    private void AimLineRows(AimLineDef d)
    {
        long now = Environment.TickCount64;
        // 1) Latch new targets: the buff on a PLAYER, cast by a scanned caster (fire = self / monster carrier → skipped).
        foreach (var b in _buffs)
        {
            if (b.BaseId != d.Buff || b.Fire == 0 || b.Fire == b.Target) continue;
            if (Ent(b.Target) is not { IsPlayer: true } || Ent(b.Fire) is not { } g || !d.IsCaster(g.MonsterId)) continue;
            if (d.Latches.TryGetValue(g.Uuid, out var have) && have.Create == b.Create && have.Target == b.Target) continue;
            long t0 = StableTickFromServer(b.Create);
            t0 = t0 > 0 ? t0 - ClockBiasMs : now;        // + bias: server timestamps lead our clock (HitOffset.cs)
            d.Latches[g.Uuid] = new AimLatch { Caster = g.Uuid, Target = b.Target, Create = b.Create, T0 = t0 };
        }

        // 2) Drop latches past the fire or whose caster despawned.
        _aimDrop.Clear();
        foreach (var l in d.Latches.Values)
            if (Ent(l.Caster) == null || now - l.T0 >= d.FireMs) _aimDrop.Add(l.Caster);
        foreach (var k in _aimDrop) d.Latches.Remove(k);

        // 3) One row for the live wave: targets with a per-player count (×N when several casters picked the same player).
        RowAcc? a = null;
        foreach (var l in d.Latches.Values)
        {
            if (a == null)
            {
                if (d.WaveT0 == 0 || now - d.WaveT0 >= d.FireMs) d.WaveT0 = l.T0;
                a = Upsert(d.Key, d.Group, d.Label, 3, d.Order, 0, d.FireMs, d.WaveT0);
                a.PhaseSplitSec = d.FireSec - d.LockSec;
                a.Phase1 = "rm.mech.fmt.golemLocks"; a.Phase2 = d.Phase2Key;   // format keys ("Locks {0}s")
            }
            AddTarget(a, l.Target);
            a.Counts ??= new Dictionary<long, int>();
            a.Counts[l.Target] = a.Counts.TryGetValue(l.Target, out int n) ? n + 1 : 1;
        }
        if (a == null) d.WaveT0 = 0;
    }

    private static bool AimActive(AimLineDef d, long caster) =>
        d.Latches.TryGetValue(caster, out var l) && Environment.TickCount64 - l.T0 < d.FireMs;

    // Monster-dot layer hooks (BuildRaidMap / AddDots): red marker for an aiming caster that has one (golem), and the
    // marker-less casters (cannons — hidden in game) are never drawn at all, aiming or not.
    private int AimCasterSlot(McEnt e)
    {
        foreach (var d in AimLines) if (d.CasterMarker && d.IsCaster(e.MonsterId) && AimActive(d, e.Uuid)) return 3;
        return -1;
    }

    private bool AimCasterHidden(int monsterId)
    {
        foreach (var d in AimLines) if (!d.CasterMarker && d.IsCaster(monsterId)) return true;
        return false;
    }

    // Minimap corridors: red Style-3 polygon, caster → the def's arena edge along the live facing. Golem Line in the
    // grid branch of BuildRaidMap, Cannon Laser in the ring (150-band) branch.
    private void AddAimLineRegions(AimLineDef d)
    {
        foreach (var l in d.Latches.Values)
        {
            if (!AimActive(d, l.Caster) || Ent(l.Caster) is not { HasPos: true } g) continue;
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
            var poly = CorridorPoly(d, g.Pos.x, g.Pos.z, dx, dz);
            _map.Regions.Add(new MinimapRegion { Kind = MinimapRegionKind.Polygon, Points = poly, Color = 3, Style = 3 });
        }
    }

    // Rectangle of d.WidthM from (x, z) along unit (dx, dz) to where the ray leaves the def's edge rect (≥ 1 m).
    private static float[] CorridorPoly(AimLineDef d, float x, float z, float dx, float dz)
    {
        float t = float.MaxValue;
        if (dx > 1e-4f) t = MathF.Min(t, (d.EdgeX - x) / dx); else if (dx < -1e-4f) t = MathF.Min(t, (-d.EdgeX - x) / dx);
        if (dz > 1e-4f) t = MathF.Min(t, (d.EdgeZ - z) / dz); else if (dz < -1e-4f) t = MathF.Min(t, (-d.EdgeZ - z) / dz);
        if (t == float.MaxValue || t < 1f) t = 1f;
        float hw = d.WidthM / 2f, nx = dz * hw, nz = -dx * hw;
        float ex = x + dx * t, ez = z + dz * t;
        return new[] { x + nx, z + nz, ex + nx, ez + nz, ex - nx, ez - nz, x - nx, z - nz };
    }
}
