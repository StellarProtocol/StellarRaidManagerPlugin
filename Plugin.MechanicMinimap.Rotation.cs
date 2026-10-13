using System;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// Mechanic Minimap — "Map rotation" dropdown (mech_map_rotation, option index, default 0 = the arena's own orientation,
// e.g. Tomb/Tina's built-in 180°). Port of Experiment 8507aa5. One GLOBAL setting: a fixed extra turn on top of every
// arena's rotation, NOT a camera-follow map. The painter adds it in the projector, so the whole picture turns together,
// and it is in the static layer key → a change re-bakes once, then nothing per frame.
public sealed partial class Plugin
{
    private static readonly string[] MechMapRotKeys =
    {
        "rm.mech.map.rotation.default", "rm.mech.map.rotation.cw90", "rm.mech.map.rotation.r180", "rm.mech.map.rotation.ccw90",
    };
    // Option index → projector quarters. +1 quarter = 90° COUNTER-clockwise on screen (MinimapProjector.For), so
    // "90° clockwise" is 3 quarters.
    private static readonly int[] MechMapRotQuarters = { 0, 3, 2, 1 };
    private int _mechMapRotation;                          // option index into MechMapRotKeys

    private void LoadMechMapRotation() =>
        _mechMapRotation = Math.Clamp(_cfg.Get<int>("mech_map_rotation", 0), 0, MechMapRotQuarters.Length - 1);

    private int MechMapUserQuarters => MechMapRotQuarters[_mechMapRotation];

    private HudElement MechMapRotationRow() => new RowElement(new HudElement[]
    {
        new TextElement(() => _loc.T("rm.mech.map.rotation")),
        new DropdownElement(
            Selected: () => _mechMapRotation,
            Options:  () => Array.ConvertAll(MechMapRotKeys, _loc.T),
            OnSelect: v =>
            {
                _mechMapRotation = Math.Clamp(v, 0, MechMapRotQuarters.Length - 1);
                _cfg.Set<int>("mech_map_rotation", _mechMapRotation);
                _cfg.Save();
                ApplyMechMapOptions();   // painter quarters + forced repaint (re-bake via the layer key)
            },
            Width: 220f),
    }, Gap: 6f);
}
