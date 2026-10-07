using System;
using System.Collections.Generic;
using UnityEngine;

namespace Stellar.RaidManager;

// ── Raid: pinball ball (10330051) — one 6 s "Ball" row per ball, timed from the CAST, not entity first-seen ───────
// In-game (Experiment c1313c9): the ball entity exists long before the mechanic (idle / pooled, ~91 s lag), so a
// first-seen start had always expired → the row sat at 0.0 s. Start source, per ball:
//   • "cast"   — the Pinball Cast buff 829314's server create time, while a cast is active or recent (≤ 15 s). Each NEW
//                829314 instance re-arms every ball. The server create is converted ONCE per cast into a local tick
//                (StartTick must stay stable: the occurrence dedupe keys on it, a jittering value re-fires alerts).
//   • "moving" — otherwise, the moment the ball starts moving (XZ delta > 0.5 m between scans after ≥ 1.5 s still).
// The row (and the ball's orange dot colour) exists only while the 6 s countdown runs — it hides the moment it expires,
// even mid-cast (user: no rows sitting at 0.0 s); a new 829314 instance re-arms.
// Idle balls before a cast get NO row and NO dot (pooled balls park at arbitrary spots — a dot there is misleading).
internal sealed partial class MechanicCalloutTracker
{
    private const long  BallDurMs = 6000, CastRecentMs = 15000, BallStillMs = 1500;
    private const float BallMoveM = 0.5f;

    private long _pinCastCreate;                 // server ms of the latest 829314 instance seen (0 = none this scene)
    private long _pinCastStartTick;              // that create as a local TickCount64 (computed once per instance)
    private readonly Dictionary<long, (Vector3 Pos, long MovedTick, long MoveStart)> _ballMotion = new();
    private readonly HashSet<long> _ballArmed = new();

    private void RaidPinballBallRows()
    {
        long now = Environment.TickCount64;
        long serverNow = ServerNowMs();
        foreach (var b in _buffs)
        {
            if (b.BaseId != PinballCastBuff) continue;
            if (b.Create > _pinCastCreate && serverNow > 0)
            {
                _pinCastCreate = b.Create;
                _pinCastStartTick = now - Math.Max(0, serverNow + ClockBiasMs - b.Create);
            }
        }
        bool castRecent = _pinCastStartTick > 0 && now - _pinCastStartTick <= CastRecentMs;

        _ballArmed.Clear();
        foreach (var e in _ents.Values)
        {
            if (e.MonsterId != PinballBallId) continue;
            long moveStart = BallMoveStart(e, now);
            long start;
            if (castRecent) start = _pinCastStartTick;                  // "cast"
            else if (moveStart > 0) start = moveStart;                   // "moving"
            else continue;                                       // idle ball, no cast: no row, no dot
            if (now - start >= BallDurMs) continue;                  // expired: hide at once (no 0.0 s row)
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
        _pinCastCreate = 0; _pinCastStartTick = 0;
        _ballMotion.Clear(); _ballArmed.Clear();
    }
}
