namespace Stellar.RaidManager;

// Raid Spin Fan: "SPIN FAN | MOVE" card while the LOCAL player stands in a wedge of the current (predicted, then
// actual) pinwheel before it fires (tracker LocalSpinDanger, Mechanics/MechanicCalloutTracker.Rules.SpinFan.cs). Same
// model as the ring MOVE card: polled from tracker state (slot after the ring MOVE card, above the on-me cards), so it
// clears by itself when the player leaves the wedges or the wave fires. Timer = the live row's phase countdown ("Stops"
// then "Fire" — the card reads Occ.Row). One chime per ENTRY (a re-entry builds a new banner and may chime again).
// Caption + ring in the Spin Fan row colour (the list's), ring red ≤ 3 s like every card. Text: rm.mech.n.spinFan +
// rm.mech.alert.move.
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
                Caption = McText.T("Spin Fan").ToUpperInvariant(), Action = _loc.T("rm.mech.alert.move"), Color = row.Color,
            };
            if (entering && _alertSound) PlayAlertSound();
        }
        return _spinBanner;
    }
}
