namespace Stellar.RaidManager;

// Layout-editor preview for the Mechanic Minimap outside the dungeons: the raid grid arena with a sample of every
// raid region style (floor damage, danger tiles, a labelled tile), team dots, a boss and party markers — built from
// the real arena data (RaidArena), so it shows what the live raid map looks like while positioning the window.
internal static class MinimapTestViews
{
    private static void Team(MinimapView v, float ox, float oz, float spread)
    {
        v.Dots.Add(new MinimapDot { X = ox + 0.1f * spread, Z = oz - 0.15f * spread, Kind = MinimapDotKind.Local,    Slot = -1, Facing = 30f, HasFacing = true });
        v.Dots.Add(new MinimapDot { X = ox + 0.5f * spread, Z = oz + 0.2f * spread,  Kind = MinimapDotKind.Teammate, Slot = 1 });
        v.Dots.Add(new MinimapDot { X = ox - 0.3f * spread, Z = oz + 0.4f * spread,  Kind = MinimapDotKind.Teammate, Slot = -1 });
        v.Dots.Add(new MinimapDot { X = ox - 0.6f * spread, Z = oz - 0.35f * spread, Kind = MinimapDotKind.Teammate, Slot = -1, Dead = true });   // dead teammate → ×
        v.Markers.Add((1, ox + 0.7f * spread, oz + 0.6f * spread));
        v.Markers.Add((3, ox - 0.7f * spread, oz - 0.1f * spread));
        v.Markers.Add((5, ox + 3f * spread, oz));   // far off-canvas → drawn clamped to the edge
    }

    public static MinimapView Raid()
    {
        var v = new MinimapView();
        RaidArena.Layout(RaidArena.Kind.Grid, v);
        v.LayoutKey = "test:" + v.LayoutKey;
        // Floor damage first (under the mechanic cells): bottom-middle cracked, center destroyed.
        var cracked = RaidArena.CellRect(RaidArena.CellIndex("bottomMid"), 5); cracked.Style = 1; v.Regions.Add(cracked);
        var hole = RaidArena.CellRect(RaidArena.CellIndex("center"), 5); hole.Style = 2; v.Regions.Add(hole);
        // Corner Explosion = DANGER tiles (Style 3), same no-go look as Phase Mapping (live: Minimap.cs DangerCell).
        foreach (var c in RaidArena.CornerCells) { var ex = RaidArena.CellRect(RaidArena.CellIndex(c), 3); ex.Style = 3; v.Regions.Add(ex); }
        var danger = RaidArena.CellRect(RaidArena.CellIndex("midLeft"), 3); danger.Style = 3; v.Regions.Add(danger);   // Phase Mapping danger
        v.Regions.Add(RaidArena.CellRect(RaidArena.CellIndex("topMid"), 1, "2F"));   // tile label beside a badge
        v.Markers.Add((2, 0f, 8f));   // party-marker badge "2" right beside the "2F" tile label, for comparison
        Team(v, 0f, 0f, 30f);
        v.Dots.Add(new MinimapDot { X = 0f, Z = 0f, Kind = MinimapDotKind.Monster, Slot = -1 });
        return v;
    }
}
