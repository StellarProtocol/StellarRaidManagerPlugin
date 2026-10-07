using System;
using System.Collections.Generic;
using UnityEngine;

namespace Stellar.RaidManager;

// ── Raid: pinball "Ball" (dummy 10330051) — one 6 s row per ball, timed from the ball's SPAWN ────────────────────────
// Ported from Experiment c2fa028. Why the Ball never showed: 10330051 is NOT spawned by the 829314 "Pinball Cast".
//   • DummyTable 10330051 = "因果折跃" (Causal Jump field marker), WalkSpeed 0; BuffTable 829314 = "交互后倒计时"
//     (countdown after interaction, on a scene object) — a different mechanic.
//   • Raid log: the ball dummy appears ~5-10 s before each Causal Jump Ricochet 829316 and ~30-50 s before the next
//     829314. Arming balls only while 829314 was recent never coincided with a ball, and the "moving" fallback never
//     fires for a 0-speed dummy → no row, no orange dot, ever.
// Upstream (resonance-logs-cn addPinballRows) times the ball from entity first-seen + 6 s — its spawn. Same here:
//   • the spawn tick is taken ONCE per uuid (re-entering the AOI is not a new spawn); a ball already present on the
//     scene's first raid scan is NOT a spawn (start unknown → no row until it moves);
//   • "moving" — a ball that starts moving after ≥ 1.5 s still (XZ delta > 0.5 m) re-arms from that moment;
//   • the row (and the ball's orange dot) exists only while its 6 s run — it hides at expiry (no 0.0 s rows).
internal sealed partial class MechanicCalloutTracker
{
    private const long  BallDurMs = 6000, BallStillMs = 1500;
    private const float BallMoveM = 0.5f;

    private readonly Dictionary<long, long> _ballSpawn = new();     // uuid → spawn tick (0 = present at scene entry)
    private readonly Dictionary<long, (Vector3 Pos, long MovedTick, long MoveStart)> _ballMotion = new();
    private readonly HashSet<long> _ballArmed = new();
    private bool _ballSceneSeeded;

    private void RaidPinballBallRows()
    {
        long now = Environment.TickCount64;
        bool entry = !_ballSceneSeeded;                  // first raid scan of the scene: present balls aren't spawns
        _ballSceneSeeded = true;

        _ballArmed.Clear();
        foreach (var e in _ents.Values)
        {
            if (e.MonsterId != PinballBallId) continue;
            if (!_ballSpawn.TryGetValue(e.Uuid, out long spawn)) _ballSpawn[e.Uuid] = spawn = entry ? 0 : now;
            long start = Math.Max(spawn, BallMoveStart(e, now));
            if (start <= 0 || now - start >= BallDurMs) continue;   // unknown start / expired: hide (no 0.0 s row)
            _ballArmed.Add(e.Uuid);
            Upsert($"raid:pinball:ball:{e.Uuid}", "Pinball", "Ball", 5, 100, 0, BallDurMs, start);
        }
    }

    // Local tick when the ball began its current movement; 0 = never moved (this scene).
    private long BallMoveStart(McEnt e, long now)
    {
        if (!e.HasPos) return _ballMotion.TryGetValue(e.Uuid, out var m0) ? m0.MoveStart : 0;
        if (!_ballMotion.TryGetValue(e.Uuid, out var m))
        {
            _ballMotion[e.Uuid] = (e.Pos, 0, 0);
            return 0;
        }
        float dx = e.Pos.x - m.Pos.x, dz = e.Pos.z - m.Pos.z;
        if (dx * dx + dz * dz > BallMoveM * BallMoveM)
        {
            if (m.MovedTick == 0 || now - m.MovedTick > BallStillMs) m.MoveStart = now;   // started moving after a rest
            m.MovedTick = now;
        }
        m.Pos = e.Pos;
        _ballMotion[e.Uuid] = m;
        return m.MoveStart;
    }

    // Minimap: the ball's orange slot only while armed.
    private bool BallArmed(long uuid) => _ballArmed.Contains(uuid);

    private void ResetPinballBalls()
    {
        _ballSpawn.Clear(); _ballMotion.Clear(); _ballArmed.Clear(); _ballSceneSeeded = false;
    }
}
