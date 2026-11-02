namespace Stellar.RaidManager;

// Raid Spin Fan: "Spin Fan — MOVE" banner while the LOCAL player stands in a wedge of the current (predicted, then
// actual) pinwheel before it fires (tracker LocalSpinDanger, Mechanics/MechanicCalloutTracker.Rules.SpinFan.cs). Same
// model as the ring MOVE banner: polled from tracker state (slot after the ring MOVE banner, above the on-me banners),
// so it clears by itself when the player leaves the wedges or the wave fires. Detail = the live row's phase text
// ("Stops 2.1s" / "Fire 0.6s" — DetailLine reads Occ.Row.PhaseText). One chime per ENTRY (a re-entry builds a new
// banner and may chime again). Red (slot colour 3), like the other MOVE banners.
public sealed partial class Plugin
{
    private Banner? _spinBanner;

    private Banner? SpinBanner()
    {
        if (!_alertOn || !_mechTracker.LocalSpinDanger || _mechTracker.SpinRow is not { } row) { _spinBanner = null; return null; }
        if (_spinBanner == null || !ReferenceEquals(_spinBanner.Occ.Row, row))
        {
            bool entering = _spinBanner == null;
            _spinBanner = new Banner
            {
                Occ = new McOccurrence { Key = MechanicCalloutTracker.SpinFanKey, Color = 3, Row = row },
                Headline = _loc.TFormat("rm.mech.banner.move", McText.T("Spin Fan")),
            };
            if (entering && _alertSound) PlayAlertSound();
        }
        return _spinBanner;
    }
}
