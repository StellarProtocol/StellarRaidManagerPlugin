using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// Raid "Spin Fan" (centre spinning beam) — Mechanic-Callouts.md "Simultaneous AREA mechanic = centre spinning beam"
// (port of Experiment's Rules.SpinFan.cs, minus its [MechSpin] diagnostics). UNTARGETED: a pinwheel of blue danger
// wedges around the centre caster dummy (BaseId 10310066, arena centre, 150-band arena); whoever stands in a wedge
// when it fires is hit.
//   • Wave start T0 = a buff on the caster: 829134 → yaw INCREASES, 829135 → yaw DECREASES. The same id returns every
//     wave as a NEW instance → a wave is keyed on (buff uuid, create), never the id alone. Waves 8.0 s apart.
//   • The caster spins ~30°/s from ~T0+0.4 s and turns ~150° per wave → PREDICTED stop yaw = yaw at T0 ± TurnDeg.
//   • Stop = a base-1000 dummy spawns AT THE CENTRE (≤ SpinStopRadiusM) with the FINAL yaw (its spawn facing) and casts
//     10310067. Base-1000 dummies also spawn at players for the puddle mechanic (skill 10310061) → only a centre one
//     casting 10310067 counts (SpinStopCandidate, from the scan's non-listed-dummy branch). Fire = stop + 0.8 s.
//   • Drawn: ONLY the predicted final pinwheel (never the live spin), replaced by the stop dummy's real yaw on stop,
//     removed at fire. Without a seen stop, fire = T0 + StopSec + FireDelay; the wave is dropped at T0 + 8 s (safety),
//     so a late stop inside that window still revives it.
//   • Row "Spin Fan · Clockwise/Counter-clockwise", countdown "Stops N.Ns" → "Fire N.Ns" (McRow.PhaseSplitSec).
//     Direction WORD = how it turns on OUR minimap: the projector puts world +X up and +Z left (MinimapProjector,
//     rot 0), so yaw 0° (+Z) is screen-left and yaw 90° (+X) screen-up → increasing yaw turns left→up = CLOCKWISE on
//     screen. Arena / user map rotations are rotations (never mirrors), so the handedness holds at every setting.
//   • On-me MOVE: every scan, the LOCAL player's angle around the caster vs the CURRENT shape's wedges (angle only —
//     a beam from the centre spans the arena; dead → never) → LocalSpinDanger (Plugin.MechanicAlerts.Spin.cs).
internal sealed partial class MechanicCalloutTracker
{
    public const string SpinFanKey = "raid:spinfan";
    private const int SpinCwBuff = 829134, SpinCcwBuff = 829135;
    private const int SpinStopDummyId = 1000, SpinStopSkill = 10310067;
    // Manual estimates (no auto-learning; recorder 2026-11-02 scene 13023 — 4 waves), tuned via Experiment's logs.
    internal const int   SpinFanBlades = 4;           // wedges in the pinwheel, evenly spaced every 360/Blades
    internal const float SpinFanBladeDeg = 45f;       // width of each blue wedge (estimate; no width in the tables)
    internal const float SpinFanOffsetDeg = 0f;       // a wedge is CENTRED on the caster yaw (estimate)
    internal const float SpinFanTurnDeg = 150f;       // turn per wave (recorded 147–151°)
    internal const float SpinFanStopSec = 5.5f;       // fallback T0 → stop until the real stop is seen (5.2–5.6 s)
    internal const float SpinFanFireDelaySec = 0.8f;  // stop → fire (warn 1031006701 dur 0.8 s)
    private const float SpinFanRadiusM = 30f;         // wedge radius drawn = the ring arena's outer edge (r 30)
    private const float SpinStopRadiusM = 1.5f;       // "at the centre": stop dummy within this of the caster
    private const long  SpinSafetyMs = 8000;          // wave dropped at T0 + 8 s (= the wave cycle) whatever happened

    private sealed class SpinWave
    {
        public int   BuffUuid;
        public long  Create, T0;
        public int   Dir;                              // +1 yaw increasing (829134), −1 decreasing (829135)
        public float Cx, Cz, PredYaw = float.NaN;
        public bool  Stopped;
        public long  StopTick;
        public float StopYaw = float.NaN;
        public long  StopTickOrFallback => Stopped ? StopTick : T0 + (long)(SpinFanStopSec * 1000f);
        public long  FireTick => StopTickOrFallback + (long)(SpinFanFireDelaySec * 1000f);
        public float ShapeYaw => Stopped && !float.IsNaN(StopYaw) ? StopYaw : PredYaw;
    }
    private SpinWave? _spin;
    private readonly HashSet<long> _spinStopSeen = new();   // stop dummies already used (a lingering one never re-stops)

    /// <summary>True while the LOCAL player stands in a wedge of the current Spin Fan shape before it fires.</summary>
    public bool  LocalSpinDanger { get; private set; }
    /// <summary>The live Spin Fan row (phase countdown) while a wave runs, else null.</summary>
    public McRow? SpinRow => _rows.TryGetValue(SpinFanKey, out var r) ? r : null;

    private void ResetSpinFan() { _spin = null; _spinStopSeen.Clear(); LocalSpinDanger = false; }

    private void RaidSpinFanRows()
    {
        long now = Environment.TickCount64;
        // 1) Wave start: a NEW instance of 829134/829135 on the caster.
        foreach (var b in _buffs)
        {
            if (b.BaseId != SpinCwBuff && b.BaseId != SpinCcwBuff) continue;
            if (_spin != null && _spin.BuffUuid == b.BuffUuid && _spin.Create == b.Create) continue;
            if (_spin != null && b.Create != 0 && b.Create <= _spin.Create) continue;   // an older instance still listed
            long t0 = StableTickFromServer(b.Create);
            t0 = t0 > 0 ? t0 - ClockBiasMs : now;        // + bias: server timestamps lead our clock (HitOffset.cs)
            var w = new SpinWave { BuffUuid = b.BuffUuid, Create = b.Create, T0 = t0, Dir = b.BaseId == SpinCwBuff ? 1 : -1 };
            if (TryEntPose(b.Target, out float cx, out float cz, out float yaw))
            {
                w.Cx = cx; w.Cz = cz;
                if (!float.IsNaN(yaw)) w.PredYaw = Norm180(yaw + w.Dir * SpinFanTurnDeg);
            }
            _spin = w;
        }
        var s = _spin;
        if (s == null) { LocalSpinDanger = false; return; }
        if (now - s.T0 >= SpinSafetyMs) { _spin = null; LocalSpinDanger = false; return; }
        // Row / shape / MOVE live from the wave start until the fire.
        if (now >= s.FireTick || float.IsNaN(s.ShapeYaw)) { LocalSpinDanger = false; return; }

        // Row: "Stops" until the stop (real, else fallback), then "Fire" (format keys, localized at display time).
        var a = Upsert(SpinFanKey, "Spin Fan", s.Dir > 0 ? "Clockwise" : "Counter-clockwise", MechanicCalloutData.BlueSlot,
                       111, 0, s.FireTick - s.T0, s.T0);
        a.PhaseSplitSec = SpinFanFireDelaySec;
        a.Phase1 = "rm.mech.fmt.spinStops"; a.Phase2 = "rm.mech.fmt.fire";

        LocalSpinDanger = LocalInSpinWedge(s);       // re-evaluated every scan
    }

    // Scan hook (non-listed raid dummy, BaseId 1000): the centre stop dummy of the running wave.
    private void SpinStopCandidate(long uuid, object obj, long now)
    {
        var s = _spin;
        if (s == null || s.Stopped || now - s.T0 < 1000 || _spinStopSeen.Contains(uuid)) return;
        if (!TryReadPos(obj, out var p, out var go)) return;
        float dx = p.x - s.Cx, dz = p.z - s.Cz;
        if (dx * dx + dz * dz > SpinStopRadiusM * SpinStopRadiusM) return;     // a player-position puddle dummy
        if (ReadCurrentSkill(obj).Id != SpinStopSkill) return;                 // not casting yet → retried next scan
        if (!TryGoYaw(go, out float yaw)) return;
        _spinStopSeen.Add(uuid);
        if (_spinStopSeen.Count > 64) _spinStopSeen.Clear();
        s.Stopped = true; s.StopTick = now; s.StopYaw = Norm180(yaw);
    }

    // Minimap (ring arena): the current shape's wedges as blue fill-only sectors (Sector Style 3).
    private void AddSpinFanRegions()
    {
        var s = _spin;
        if (s == null || float.IsNaN(s.ShapeYaw) || Environment.TickCount64 >= s.FireTick) return;
        for (int k = 0; k < SpinFanBlades; k++)
        {
            float c = s.ShapeYaw + SpinFanOffsetDeg + k * 360f / SpinFanBlades;
            var r = MinimapRegion.Sector(s.Cx, s.Cz, SpinFanRadiusM, c - SpinFanBladeDeg / 2f, c + SpinFanBladeDeg / 2f,
                                         MechanicCalloutData.BlueSlot);
            r.Style = 3;
            _map.Regions.Add(r);
        }
    }

    private bool LocalInSpinWedge(SpinWave s)
    {
        if (!_hasLocalPos || _localDead || float.IsNaN(s.ShapeYaw)) return false;
        float angle = MathF.Atan2(_localPos.x - s.Cx, _localPos.z - s.Cz) * 180f / MathF.PI;
        for (int k = 0; k < SpinFanBlades; k++)
            if (MathF.Abs(SpinDeltaDeg(angle, s.ShapeYaw + SpinFanOffsetDeg + k * 360f / SpinFanBlades)) <= SpinFanBladeDeg / 2f)
                return true;
        return false;
    }

    // Position + rendered yaw of an entity: the scan snapshot when it is there, else a direct read (the caster may
    // be an entity type the scan doesn't list).
    private bool TryEntPose(long uuid, out float x, out float z, out float yaw)
    {
        x = z = 0f; yaw = float.NaN;
        object? go = null;
        if (Ent(uuid) is { HasPos: true } e) { x = e.Pos.x; z = e.Pos.z; go = e.GoComp; }
        else if (GetEntity(uuid) is { } obj && TryReadPos(obj, out var p, out go)) { x = p.x; z = p.z; }
        else return false;
        if (TryGoYaw(go, out float y)) yaw = y;
        return true;
    }

    private static float Norm180(float d) => ((d % 360f) + 540f) % 360f - 180f;
    private static float SpinDeltaDeg(float a, float b) => ((a - b) % 360f + 540f) % 360f - 180f;
}
