using System;
using System.Collections.Generic;
using UnityEngine;

namespace Stellar.RaidManager;

// ── Raid: pinball "Ball" (dummy 10330051) — one 6 s row per ball APPEARANCE ──────────────────────────────────────────
// Ported from Experiment b871960. Why the Ball never showed: 10330051 is NOT spawned by the 829314 "Pinball Cast".
//   • DummyTable 10330051 = "因果折跃" (Causal Jump field marker), WalkSpeed 0; BuffTable 829314 = "交互后倒计时"
//     (countdown after interaction, on a scene object) — a different mechanic.
//   • Raid log: the ball dummy appears ~6.5 s before each Causal Jump Ricochet 829316 (fire = that ball uuid).
// Upstream (resonance-logs-cn addPinballRows) times the ball from entity first-seen + 6 s — its spawn.
// "Ball shows only once and never again": the balls are a POOLED set — the SAME uuids re-appear every round (appear →
// ~6.5 s → ricochet → leave the scan → re-appear ~30-40 s later). So the 6 s runs from each (RE-)APPEARANCE:
//   • a ball counts as gone only after ≥ BallGoneMisses consecutive scan passes without it AND ≥ BallGoneMs since last
//     seen (scan pass = 200 ms; a single missed GetEntity must not re-arm it; a paused poll runs no passes → no misses);
//   • a ball already present on the scene's first raid scan is NOT a spawn (start unknown → no row until it re-appears);
//   • each appearance converts to ONE stable start tick, and the row key carries it → alerts/dedupe never merge rounds;
//   • the run ends early once its own ricochet (829316, fire = this uuid, created after this appearance) lands;
//   • "moving" — a ball that starts moving after ≥ 1.5 s still (XZ delta > 0.5 m) re-arms from that moment;
//   • the row (and the ball's orange dot) exists only while its 6 s run — it hides at expiry (no 0.0 s rows).
internal sealed partial class MechanicCalloutTracker
{
    private const long  BallDurMs = 6000, BallStillMs = 1500, BallGoneMs = 1000;
    private const int   BallGoneMisses = 3, CausalJumpRicochetBuff = 829316;
    private const float BallMoveM = 0.5f;

    // uuid → current appearance: Start = spawn tick (0 = present at scene entry), LastSeen tick, Misses = consecutive
    // scan passes without it, Ended = its ricochet landed (run over early).
    private sealed class BallLife { public long Start, LastSeen; public int Misses; public bool Ended; }
    private readonly Dictionary<long, BallLife> _ballLife = new();
    private readonly Dictionary<long, (Vector3 Pos, long MovedTick, long MoveStart)> _ballMotion = new();
    private readonly HashSet<long> _ballArmed = new();
    private bool _ballSceneSeeded;

    private void RaidPinballBallRows()
    {
        long now = Environment.TickCount64;
        bool entry = !_ballSceneSeeded;                  // first raid scan of the scene: present balls aren't spawns
        _ballSceneSeeded = true;

        foreach (var kv in _ballLife) if (!_ents.ContainsKey(kv.Key)) kv.Value.Misses++;

        _ballArmed.Clear();
        foreach (var e in _ents.Values)
        {
            if (e.MonsterId != PinballBallId) continue;
            var life = BallSighting(e.Uuid, now, entry);
            if (life.Ended) continue;
            long moveStart = BallMoveStart(e, now);
            long start = Math.Max(life.Start, moveStart);
            if (start <= 0 || now - start >= BallDurMs) continue;   // unknown start / expired: hide (no 0.0 s row)
            if (BallRicocheted(e.Uuid, start)) { life.Ended = true; continue; }
            _ballArmed.Add(e.Uuid);
            Upsert(BallKey(e.Uuid, start), "Pinball", "Ball", 5, 100, 0, BallDurMs, start);
            if (moveStart <= life.Start) BallRoundCheck(e.Uuid, start);   // spawn-armed (not a "moving" re-arm)
        }
    }

    private static string BallKey(long uuid, long start) => $"raid:pinball:ball:{uuid}:{start}";

    // ── On-me alert per ball ROUND (the 829316 ricochet banner came too late) ────────────────────────────────────
    // There is no per-player pre-hit signal — the ricochet lands on 2-3 OR all 13 players — so every player is a
    // potential target from the moment the pair appears. The FIRST ball of a round that arms on a spawn becomes the
    // round's LEAD: its row's occurrence is flagged as a local target (DetectOccurrences), so the normal on-me path
    // (banner "Pinball — YOU | Ball  N.Ns" + chime) runs once per round. The pair appears in the same scan; a ball
    // starting within BallRoundJoinMs of the lead is the same round. The banner lives while the lead's row does: its
    // 6 s run, or until its own ricochet ends the run early. "moving" re-arms never lead.
    private const long BallRoundJoinMs = 1500;           // manual (Rule 12)
    private long   _ballRoundStart;
    private string _ballRoundLeadKey = "";

    private void BallRoundCheck(long uuid, long start)
    {
        if (_ballRoundLeadKey.Length > 0 && start <= _ballRoundStart + BallRoundJoinMs) return;   // same round
        _ballRoundStart = start;
        _ballRoundLeadKey = BallKey(uuid, start);
    }

    /// <summary>The ball row that carries this round's on-me alert (everyone is a potential ricochet target).</summary>
    private bool IsPinballRoundLead(string key) => _ballRoundLeadKey.Length > 0 && key == _ballRoundLeadKey;

    /// <summary>Causal Jump Ricochet 829316 rows: no on-me banner — the ball round alert already covered it.</summary>
    public static bool IsPinballRicochetKey(string? key) =>
        key != null && (key == "829316" || key.StartsWith("829316:", StringComparison.Ordinal));

    // Records a sighting; a (re-)appearance after a real absence starts a new run.
    private BallLife BallSighting(long uuid, long now, bool entry)
    {
        if (!_ballLife.TryGetValue(uuid, out var life))
        {
            _ballLife[uuid] = life = new BallLife { Start = entry ? 0 : now, LastSeen = now };
            return life;
        }
        if (life.Misses >= BallGoneMisses && now - life.LastSeen >= BallGoneMs)
        {
            life.Start = now; life.Ended = false;
            _ballMotion.Remove(uuid);                    // last round's position is not a movement
        }
        life.Misses = 0;
        life.LastSeen = now;
        return life;
    }

    // Its own Causal Jump Ricochet (fire = this ball) created during this appearance = the hit has landed.
    private bool BallRicocheted(long uuid, long start)
    {
        foreach (var b in _buffs)
        {
            if (b.BaseId != CausalJumpRicochetBuff || b.Fire != uuid) continue;
            long t = StableTickFromServer(b.Create);
            if (t > 0 && t >= start) return true;      // an older round's lingering ricochet doesn't count
        }
        return false;
    }

    // Local tick when the ball began its current movement; 0 = never moved (this appearance).
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
        _ballLife.Clear(); _ballMotion.Clear(); _ballArmed.Clear(); _ballSceneSeeded = false;
        _ballRoundStart = 0; _ballRoundLeadKey = "";
    }
}
