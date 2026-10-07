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
//   • LocalDanger = which danger tile the LOCAL player stands in — drives the on-me "<mechanic> — MOVE OFF <tile>"
//     banner (Plugin.MechanicAlerts.Banner.cs). Covers ALL raid danger tiles: Phase Mapping plus Edge-Mid / Corner
//     Explosion (829214/829215, same red no-go tiles, Minimap.cs). The on-me alert itself skips every danger-tile
//     row (IsDangerTileKey — they mark the floor, they are never "on you").
internal sealed partial class MechanicCalloutTracker
{
    public const string PhaseDangerKey = "raid:phasemap:danger";
    // Edge-Mid / Corner Explosion rows are TableRows rows keyed ByBase (MechanicCallouts.Data.cs) → key = the base id.
    public const int EdgeMidExplosionId = 829214, CornerExplosionId = 829215;
    private static readonly Dictionary<int, string> RaidPhaseMapping = new()
    {
        [829327] = "topLeft", [829328] = "midLeft", [829329] = "bottomLeft",
        [829330] = "topRight", [829331] = "midRight", [829332] = "bottomRight",
    };

    /// <summary>Rows that mark FLOOR tiles (never a player): excluded from the on-me alert.</summary>
    public static bool IsDangerTileKey(string? key) =>
        key == PhaseDangerKey || key == "829214" || key == "829215";

    private readonly SortedSet<int> _phaseDanger = new();     // marked cell indices (RaidArena.Cells order)

    /// <summary>Danger-tile mechanic the LOCAL player stands in: Key = row key (PhaseDangerKey / "829214" /
    /// "829215"), Name = ENGLISH mechanic name (McText.T localizes it), Cell = localized cell name. All "" when none.</summary>
    public string LocalDangerKey { get; private set; } = "";
    public string LocalDangerName { get; private set; } = "";
    public string LocalDangerCell { get; private set; } = "";

    private void RaidPhaseMappingRows()
    {
        _phaseDanger.Clear();
        long create = 0, dur = 0;
        var players = new List<long>();
        bool edgeMid = false, corner = false;
        foreach (var b in _buffs)
        {
            if (b.BaseId == EdgeMidExplosionId) { edgeMid = true; continue; }
            if (b.BaseId == CornerExplosionId) { corner = true; continue; }
            if (!RaidPhaseMapping.TryGetValue(b.BaseId, out var cellId)) continue;
            int i = RaidArena.CellIndex(cellId);
            if (i < 0) continue;
            _phaseDanger.Add(i);
            if (b.Create > 0 && (create == 0 || b.Create < create)) create = b.Create;
            if (b.Dur > dur) dur = b.Dur;
            if (Ent(b.Target)?.IsPlayer == true) players.Add(b.Target);
        }
        if (_phaseDanger.Count > 0)
        {
            string label = McText.F("rm.mech.fmt.avoid", string.Join(", ", _phaseDanger.Select(i => McText.T(RaidArena.Cells[i].Name))));
            var a = Upsert(PhaseDangerKey, "Phase Mapping — Danger", label, 3, 1, create, dur);
            foreach (long u in players) AddTarget(a, u, colourEntity: false);
        }

        // Which danger tile is the local player standing in? Precedence when tiles overlap (Phase Mapping can mark
        // midLeft/midRight and the corners too): Phase Mapping → Edge-Mid → Corner — fixed and simple; the cell is
        // the same either way, only the headline differs. Cells exist only while their buff is in this scan.
        ClearLocalDanger();
        if (!_hasLocalPos) return;
        foreach (int i in _phaseDanger)
            if (LocalIn(i)) { SetLocalDanger(PhaseDangerKey, "Phase Mapping", i); return; }
        if (edgeMid && LocalInAny(RaidArena.EdgeMidCells, out int e)) { SetLocalDanger("829214", "Edge-Mid Explosion", e); return; }
        if (corner && LocalInAny(RaidArena.CornerCells, out int c)) SetLocalDanger("829215", "Corner Explosion", c);
    }

    // Cell rect = centre ± 10 × 7.5 (RaidArena.CellHalfX/Z) — the same containment the Phase Mapping banner always used.
    private bool LocalIn(int i)
    {
        var c = RaidArena.Cells[i];
        return MathF.Abs(_localPos.x - c.X) <= RaidArena.CellHalfX && MathF.Abs(_localPos.z - c.Z) <= RaidArena.CellHalfZ;
    }

    private bool LocalInAny(string[] cellIds, out int cell)
    {
        foreach (var id in cellIds)
        {
            cell = RaidArena.CellIndex(id);
            if (cell >= 0 && LocalIn(cell)) return true;
        }
        cell = -1;
        return false;
    }

    private void SetLocalDanger(string key, string name, int cell)
    {
        LocalDangerKey = key; LocalDangerName = name; LocalDangerCell = McText.T(RaidArena.Cells[cell].Name);
    }

    private void ClearLocalDanger() { LocalDangerKey = ""; LocalDangerName = ""; LocalDangerCell = ""; }
}
