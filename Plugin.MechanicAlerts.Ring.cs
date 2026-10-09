using System;

namespace Stellar.RaidManager;

// Raid Electromagnetic Ring: "MOVE → <safe ring>" banner while the LOCAL player stands in one of the current wave's two
// danger rings before it explodes (tracker LocalRing*, Mechanics/MechanicCalloutTracker.Rules.RingDanger.cs / .RingPurge.cs)
// — Clash / Brutal: the latest wave's 5 s; Purge: the current STEP's 10 s, detail "<Electromagnetic Ring · Step 2/3>".
// Same model as the danger-tile MOVE OFF banner: polled from tracker state (slot right below MOVE OFF, above the
// on-me banners), so it disappears by itself when the player reaches the safe ring or the wave explodes. Countdown =
// a synthetic timed row (remaining of the window) so DetailLine shows "<Electromagnetic Ring>   3.2s". Keyed per wave
// (+ safe ring, which a late Purge body can re-decide): a new wave builds a new banner; chime when it starts (≤ every
// 3 s, shared with MOVE OFF). Red (slot colour 3).
// Text localized: rm.mech.ring.move ("MOVE → {0}") + the rm.mech.ring.{inner,middle,outer} names, rm.mech.ring.name,
// rm.mech.ring.step ("Electromagnetic Ring · Step {0}/{1}", Purge).
public sealed partial class Plugin
{
    private Banner? _ringBanner;
    private int _ringBannerWave, _ringBannerSafe;   // wave / safe ring the cached banner was built for (0 = none)

    private Banner? RingBanner()
    {
        if (!_alertOn) return null;
        int wave = _mechTracker.LocalRingWave, safe = _mechTracker.LocalRingSafeId;
        if (wave == 0) { _ringBannerWave = 0; return null; }
        if (wave != _ringBannerWave || safe != _ringBannerSafe || _ringBanner == null)
        {
            bool starting = _ringBannerWave == 0;
            _ringBannerWave = wave; _ringBannerSafe = safe;
            long explode = _mechTracker.LocalRingExplodeTick, now = Environment.TickCount64;
            var row = new McRow { Key = MechanicCalloutTracker.RingDangerKey, Color = 3, DurationMs = _mechTracker.LocalRingWindowMs,
                                  SnapTick = now, SnapRemain = MathF.Max(0f, (explode - now) / 1000f) };
            _ringBanner = new Banner
            {
                Occ = new McOccurrence { Key = MechanicCalloutTracker.RingDangerKey, Color = 3, Row = row },
                Headline = _loc.TFormat("rm.mech.ring.move", _mechTracker.LocalRingSafeName),
                Detail = _mechTracker.LocalRingStep > 0
                    ? _loc.TFormat("rm.mech.ring.step", _mechTracker.LocalRingStep, _mechTracker.LocalRingSteps)
                    : _loc.T("rm.mech.ring.name"),
            };
            if (starting && _alertSound && now - _dangerSoundAt > 3000) { _dangerSoundAt = now; PlayAlertSound(); }
        }
        return _ringBanner;
    }
}
