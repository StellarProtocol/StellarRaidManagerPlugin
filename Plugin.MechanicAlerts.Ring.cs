using System;

namespace Stellar.RaidManager;

// Raid Electromagnetic Ring (Clash / Brutal): "MOVE → <safe ring>" banner while the LOCAL player stands in one of the
// current wave's two danger rings during its 5 s warning (tracker LocalRing*, Mechanics/MechanicCalloutTracker.Rules.RingDanger.cs).
// Same model as the danger-tile MOVE OFF banner: polled from tracker state (slot right below MOVE OFF, above the
// on-me banners), so it disappears by itself when the player reaches the safe ring or the wave explodes. Countdown =
// a synthetic timed row (remaining of the 5 s) so DetailLine shows "<Electromagnetic Ring>   3.2s". Keyed per wave:
// a new wave builds a new banner; chime when it starts (≤ every 3 s, shared with MOVE OFF). Red (slot colour 3).
// Text localized: rm.mech.ring.move ("MOVE → {0}") + the rm.mech.ring.{inner,middle,outer} names, rm.mech.ring.name.
public sealed partial class Plugin
{
    private Banner? _ringBanner;
    private int _ringBannerWave;                 // wave the cached banner was built for (0 = none)

    private Banner? RingBanner()
    {
        if (!_alertOn) return null;
        int wave = _mechTracker.LocalRingWave;
        if (wave == 0) { _ringBannerWave = 0; return null; }
        if (wave != _ringBannerWave || _ringBanner == null)
        {
            bool starting = _ringBannerWave == 0;
            _ringBannerWave = wave;
            long explode = _mechTracker.LocalRingExplodeTick, now = Environment.TickCount64;
            var row = new McRow { Key = MechanicCalloutTracker.RingDangerKey, Color = 3, DurationMs = 5000,
                                  SnapTick = now, SnapRemain = MathF.Max(0f, (explode - now) / 1000f) };
            _ringBanner = new Banner
            {
                Occ = new McOccurrence { Key = MechanicCalloutTracker.RingDangerKey, Color = 3, Row = row },
                Headline = _loc.TFormat("rm.mech.ring.move", _mechTracker.LocalRingSafeName),
                Detail = _loc.T("rm.mech.ring.name"),
            };
            if (starting && _alertSound && now - _dangerSoundAt > 3000) { _dangerSoundAt = now; PlayAlertSound(); }
        }
        return _ringBanner;
    }
}
