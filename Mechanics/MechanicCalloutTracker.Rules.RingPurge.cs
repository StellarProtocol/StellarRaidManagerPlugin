using System;
using System.Collections.Generic;
using System.Linq;

namespace Stellar.RaidManager;

// ── Raid: electromagnetic ring — Purge (13023) step warning ───────────────────────────────────────────────────────
// Purge shows a 3-wave PREVIEW (2 ring bodies per wave, the missing ring = safe), then the rings explode in preview
// order. User-confirmed 2026-10-09 (recorder: 9/9 pairs despawn 9.9–10.1 s after spawning): EACH wave explodes
// RingPurgeExplodeMs (10 s) after ITS OWN bodies spawned. Waves are ~3 s apart but NOT fixed (3.9 s then 2.1 s seen),
// so every step uses its own wave's StartTick — never a fixed spacing.
//   • Steps = COPIES of the current sequence's waves (_rpSteps, index = step − 1), refreshed from _ringWaves while the
//     wave is there (a late body can re-decide it). A sequence reset (hold / new preview) clears _ringWaves but not the
//     copies, so it can't cut a running step; a NEW sequence (first wave N differs) replaces them.
//   • Current step = the EARLIEST step whose explode tick (StartTick + 10 s) is still in the future. With a known safe
//     ring it is armed as the danger window (RingDanger.cs: same band test / radii / LocalRing* / banner as Clash/Brutal,
//     plus LocalRingStep). Step 1 is current from its own spawn — i.e. already during the rest of the preview.
//   • After the last step explodes there is no current step: no window, no minimap bands/highlight. Once no further
//     wave can join (3 steps, or RingGapMs without a fresh body) the sequence ENDS: row + step numbers cleared
//     (RingPurgeEnd). Fewer than 3 waves / "?" steps: ends after the last KNOWN step, once that gap has passed.
//   • Minimap (AddRingBand → AddPurgeRingMap): the CURRENT step's two danger bands + safe outline (not the latest
//     preview wave), and its step number highlighted among the 1/2/3 labels (others dimmed).
// Experiment's copy logs each step / enter / leave / explode; this shipped copy has no diagnostics.
internal sealed partial class MechanicCalloutTracker
{
    private const long RingPurgeExplodeMs = 10_000;    // manual (Rule 12): Purge wave spawn → explosion (user-confirmed)

    private sealed class RingStep
    {
        public int   N, IdCount;                       // wave number / body count the copy was taken at
        public long  StartTick;
        public bool  Decided;
        public int   Safe;
        public int[] Danger = Array.Empty<int>();
    }

    private readonly List<RingStep> _rpSteps = new();  // the current Purge sequence (≤ 3 steps), copied out of _ringWaves
    private int _rpCur = -1;                           // index of the current step in _rpSteps; -1 = none

    // RingDangerCheck (Purge scene), every raid scan.
    private void RingPurgeCheck(long now, bool arena)
    {
        RingPurgeSync();
        // The armed step's explosion first, so the next step can arm in this same scan.
        if (_rdWave != 0 && now - _rdStart >= _rdDelay) RingDangerEnd();
        _rpCur = _rpSteps.FindIndex(s => now - s.StartTick < RingPurgeExplodeMs);
        if (_rpCur < 0 && _rpSteps.Count > 0 && RingPurgeNoMoreWaves(now)) RingPurgeEnd();

        if (!arena) { if (_rdWave != 0) RingDangerEnd(); return; }
        if (_rpCur >= 0 && _rpSteps[_rpCur] is { Decided: true, Safe: not 0 } step)
        {
            if (_rdWave != step.N) RingDangerArm(step.N, _rpCur + 1, step.StartTick, RingPurgeExplodeMs, step.Safe, step.Danger);
            else { _rdSafe = step.Safe; _rdDanger = step.Danger; }   // re-decided by a late body: follow the copy
        }
        RingDangerTick(now);
    }

    // Copy the live sequence's waves into the step list (see header). Empty _ringWaves (reset) → keep the copies.
    private void RingPurgeSync()
    {
        if (_ringWaves.Count == 0) return;
        if (_rpSteps.Count > 0 && _rpSteps[0].N != _ringWaves[0].N) { _rpSteps.Clear(); _rpCur = -1; }
        for (int i = 0; i < _ringWaves.Count && i < RingPreviewWaves; i++)
        {
            var w = _ringWaves[i];
            if (i == _rpSteps.Count) _rpSteps.Add(new RingStep { N = w.N, StartTick = w.StartTick });
            var s = _rpSteps[i];
            if (s.Decided == w.Decided && s.Safe == w.Safe && s.IdCount == w.Ids.Count) continue;
            s.Decided = w.Decided; s.Safe = w.Safe; s.IdCount = w.Ids.Count;
            s.Danger = w.Ids.Distinct().ToArray();
        }
    }

    // Minimap (AddRingBand, Purge scene, ring centre known): the current step's bands + step numbers (current
    // highlighted, others dimmed). Nothing running and the sequence reset → nothing drawn (clears as before).
    private void AddPurgeRingMap()
    {
        if (_rpCur >= 0 && _rpCur < _rpSteps.Count)
        {
            var s = _rpSteps[_rpCur];
            foreach (int id in s.Danger)
            {
                var d = RaidRings[id];
                var reg = MinimapRegion.Ring(d.RIn, d.ROut, 3);
                reg.Style = 3;                                         // danger band
                _map.Regions.Add(reg);
            }
            if (s.Safe != 0)
            {
                var sr = RaidRings[s.Safe];
                var safe = MinimapRegion.Ring(sr.RIn, sr.ROut, sr.Color);
                safe.Style = 1;                                        // safe band outline
                _map.Regions.Add(safe);
            }
        }
        if (_ringWaves.Count == 0 && _rpCur < 0) return;
        for (int i = 0; i < _rpSteps.Count; i++)
        {
            var s = _rpSteps[i];
            if (!s.Decided || s.Safe == 0) continue;
            AddRingStepLabel(i + 1, s.Safe, _rpCur < 0 ? 0 : i == _rpCur ? 2 : 1);
        }
    }

    // Wave number N of the current Purge step (callout row highlight, RaidRingRows); 0 = none / not Purge. Matched by N,
    // not index, so it can never mark a wave of a different sequence.
    private int RingPurgeCurrentWave() =>
        SceneId == 13023 && _rpCur >= 0 && _rpCur < _rpSteps.Count ? _rpSteps[_rpCur].N : 0;

    // No further wave can join this sequence: it is full (3 waves), or no fresh ring body for RingGapMs — past that
    // RaidRingRows treats the next body as a NEW preview (reset), so it could never become step N+1 here anyway.
    private bool RingPurgeNoMoreWaves(long now) => _rpSteps.Count >= RingPreviewWaves || now - _ringLastNewTick > RingGapMs;

    // The sequence ENDS when its last step has exploded and no further wave can join (user 2026-10-09): clear the row
    // (RaidRingRows stops upserting once _ringWaves is empty), the step numbers and the bands. The 40 s hold /
    // new-preview resets stay as fallbacks.
    private void RingPurgeEnd()
    {
        _ringWaves.Clear();
        _rpSteps.Clear(); _rpCur = -1;
    }

    private void ClearRingPurge()
    {
        _rpSteps.Clear(); _rpCur = -1;
    }
}
