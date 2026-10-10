using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// Row timing hygiene (audit after the pinball-ball bug, 1ed7792 / 1b52d66). Bug classes guarded here, for EVERY rule:
//   (b) a timed row sitting at 0.0 s — PruneExpired drops any accumulated row whose countdown has run out, before
//       Reconcile, and keeps it dropped while its timing signature (create, start tick, duration) is unchanged. A new
//       signature (buff re-applied, new cast, new state transition) brings it back as a NEW occurrence (the key left
//       _rows, so the occurrence epoch bumps). Covers buff rows whose buff outlives its duration, rule rows with a
//       fallback duration (swap orbs 3 s, energy orb 5 s, resolve 5 s) and local-tick rows (casts / first-seen).
//   (d) a StartTick re-derived from a server timestamp every scan (TickFromServer = now − (serverNow − ms)) jitters by
//       a few ms per pass → the row resnaps and the occurrence key (which includes StartTick) changes → alert re-fires
//       every scan. StableTickFromServer converts each server timestamp ONCE.
// Untimed rows (no countdown) can't expire; their lifetime is logged instead ([MechTimer] untimed … appeared / gone
// after) so a row that lives the whole fight (pooled / persistent presence entity) shows up in the logs.
internal sealed partial class MechanicCalloutTracker
{
    private readonly Dictionary<string, (long Create, long Start, long Dur)> _expired = new();
    private readonly List<string> _expiredDrop = new();
    private readonly Dictionary<long, long> _stableTick = new();

    private void PruneExpired()
    {
        long now = Environment.TickCount64, srv = ServerNowMs();
        _expiredDrop.Clear();
        foreach (var kv in _acc)
        {
            var a = kv.Value;
            if (a.DurMs <= 0) continue;
            var sig = (a.CreateMs, a.StartTick, a.DurMs);
            bool expired;
            if (_expired.TryGetValue(kv.Key, out var old) && old == sig) expired = true;
            else if (a.StartTick > 0) expired = now - a.StartTick >= a.DurMs;
            else if (a.CreateMs > 0 && srv > 0) expired = a.CreateMs + a.DurMs <= srv + ClockBiasMs;
            else expired = _rows.TryGetValue(kv.Key, out var r) && r.CreateMs == a.CreateMs && r.StartTick == a.StartTick
                           && r.DurationMs == a.DurMs && r.RemainSec <= 0f;     // no create: the live row's own countdown
            if (!expired) { _expired.Remove(kv.Key); continue; }
            _expired[kv.Key] = sig;
            _expiredDrop.Add(kv.Key);
        }
        // Forget remembered keys the rules no longer produce at all (bounded; a later return is a fresh signature).
        _expiredGone.Clear();
        foreach (var k in _expired.Keys) if (!_acc.ContainsKey(k)) _expiredGone.Add(k);
        foreach (var k in _expiredGone) _expired.Remove(k);

        foreach (var k in _expiredDrop)
        {
            // An expired row must not keep colouring its targets' minimap dots either.
            var a = _acc[k];
            foreach (var t in a.Targets)
                if (_entColor.TryGetValue(t.Uuid, out int c) && c == a.Color) _entColor.Remove(t.Uuid);
            _acc.Remove(k);
        }
    }
    private readonly List<string> _expiredGone = new();

    // A server-epoch timestamp → local TickCount64, converted ONCE per distinct timestamp (stable across scans).
    private long StableTickFromServer(long serverMs)
    {
        if (serverMs <= 0) return 0;
        if (_stableTick.TryGetValue(serverMs, out long t)) return t;
        t = TickFromServer(serverMs);
        if (t == 0) return 0;                     // clock not synced yet — retry next pass
        if (_stableTick.Count > 256) _stableTick.Clear();
        return _stableTick[serverMs] = t;
    }

    private void ResetExpiry() { _expired.Clear(); _stableTick.Clear(); _towerSince.Clear(); _towerComplete.Clear(); }
}
