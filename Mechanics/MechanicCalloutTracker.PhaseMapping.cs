using System;
using System.Collections.Generic;
using System.Linq;

namespace Stellar.RaidManager;

// Raid Phase Mapping (829327-829332) = DANGER floor tiles you must NOT stand on (user correction; consistent with
// dummy 2002387 "Phase Mapping single-floor explosion" / skill 10320054, and the buffs riding monsters, not players).
// Upstream drew each mapped cell in its own palette colour and listed one row per cell — misleading (looked like an
// assignment) and clashing with Preset Return's 1F/2F/3F colours. Now:
//   • ONE merged row: group "Phase Mapping — Danger", label "Avoid: Top Left, Middle Right", red, countdown = the
//     longest buff duration. Targets only when the carrier is a PLAYER (names kept), never colouring dots.
//   • Map: every marked cell in one red no-go style (MechanicMinimapPainter Style 3), above floor damage.
//   • PhaseDangerCell = the (localized) marked cell the LOCAL player stands in ("" if none) — drives the on-me
//     "Phase Mapping — MOVE OFF <tile>" banner (Plugin.MechanicAlerts.Banner.cs). The on-me alert itself skips these
//     rows (they are never "on you").
internal sealed partial class MechanicCalloutTracker
{
    public const string PhaseDangerKey = "raid:phasemap:danger";
    private static readonly Dictionary<int, string> RaidPhaseMapping = new()
    {
        [829327] = "topLeft", [829328] = "midLeft", [829329] = "bottomLeft",
        [829330] = "topRight", [829331] = "midRight", [829332] = "bottomRight",
    };

    private readonly SortedSet<int> _phaseDanger = new();     // marked cell indices (RaidArena.Cells order)
    public string PhaseDangerCell { get; private set; } = "";

    private void RaidPhaseMappingRows()
    {
        _phaseDanger.Clear();
        long create = 0, dur = 0;
        var players = new List<long>();
        foreach (var b in _buffs)
        {
            if (!RaidPhaseMapping.TryGetValue(b.BaseId, out var cellId)) continue;
            int i = RaidArena.CellIndex(cellId);
            if (i < 0) continue;
            _phaseDanger.Add(i);
            if (b.Create > 0 && (create == 0 || b.Create < create)) create = b.Create;
            if (b.Dur > dur) dur = b.Dur;
            if (Ent(b.Target)?.IsPlayer == true) players.Add(b.Target);
        }
        PhaseDangerCell = "";
        if (_phaseDanger.Count == 0) return;

        string label = McText.F("rm.mech.fmt.avoid", string.Join(", ", _phaseDanger.Select(i => McText.T(RaidArena.Cells[i].Name))));
        var a = Upsert(PhaseDangerKey, "Phase Mapping — Danger", label, 3, 1, create, dur);
        foreach (long u in players) AddTarget(a, u, colourEntity: false);

        // Is the local player standing in a marked cell? (cell rect = centre ± 10 × 7.5)
        if (_hasLocalPos)
            foreach (int i in _phaseDanger)
            {
                var c = RaidArena.Cells[i];
                if (MathF.Abs(_localPos.x - c.X) <= RaidArena.CellHalfX && MathF.Abs(_localPos.z - c.Z) <= RaidArena.CellHalfZ)
                { PhaseDangerCell = McText.T(c.Name); break; }
            }
    }
}
