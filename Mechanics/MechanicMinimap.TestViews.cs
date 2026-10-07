using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// Positioning / preview views for the Mechanic Minimap outside the dungeons: one per scene layout, cycled from the
// Mechanic Callouts window ("Test map: <name>"). Names are English source strings, localized at display (McText.T). Each uses the real arena data + a few sample regions and dots placed
// relative to the arena origin, so every shape kind and rotation can be eyeballed without being in the instance.
internal static class MinimapTestViews
{
    public static readonly (string Name, Func<MinimapView> Build)[] All =
    {
        ("Raid (grid)",      Raid),
        ("Reef (boss)",      ReefBoss),
        ("Reef (matrix)",    ReefMatrix),
        ("Tina's Mindrealm", Tina),
        ("Cursed Radiant Tomb", Tomb),
        ("Wasteland Court",  Wasteland),
        ("Towering Ruin",    Giant),
    };

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

    private static MinimapView Spec(MinimapArenaSpec s)
    {
        var v = new MinimapView();
        s.Apply(v);
        v.LayoutKey = "test:" + v.LayoutKey;
        return v;
    }

    private static MinimapView Raid()
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

    private static MinimapView ReefBoss()
    {
        var s = MinimapArenas.ReefBoss; var v = Spec(s);
        v.Regions.Add(new MinimapRegion { Kind = MinimapRegionKind.Rect, X = s.Cx + 8f, Z = s.Cz, HalfX = 2f, HalfZ = s.HalfZ, Color = 1 });
        v.Regions.Add(MinimapRegion.Sector(s.Cx - 6f, s.Cz + 4f, 20f, 15f, 105f, 9));
        v.Regions.Add(MinimapRegion.Sector(s.Cx - 6f, s.Cz + 4f, 20f, 195f, 285f, 9));
        Team(v, s.Cx, s.Cz, 25f);
        v.Dots.Add(new MinimapDot { X = s.Cx, Z = s.Cz - 10f, Kind = MinimapDotKind.Monster, Slot = -1 });
        v.Dots.Add(new MinimapDot { X = s.Cx + 15f, Z = s.Cz + 12f, Kind = MinimapDotKind.Monster, Slot = 7 });
        return v;
    }

    private static MinimapView Tina()
    {
        var s = MinimapArenas.Tina; var v = Spec(s);
        v.Regions.Add(MinimapRegion.Sector(s.Cx, s.Cz, 16f, 37.5f, 82.5f, 3));
        v.Regions.Add(MinimapRegion.Sector(s.Cx, s.Cz, 16f, 217.5f, 262.5f, 5));
        Team(v, s.Cx, s.Cz, 12f);
        v.Dots.Add(new MinimapDot { X = s.Cx, Z = s.Cz, Kind = MinimapDotKind.Monster, Slot = 3 });
        return v;
    }

    private static MinimapView Tomb()
    {
        var s = MinimapArenas.Tomb; var v = Spec(s);
        // A charge half: the arena half left of a line through the centre heading +X.
        v.Regions.Add(MinimapRegion.Polygon(new[] { s.Cx - s.HalfX, s.Cz, s.Cx + s.HalfX, s.Cz, s.Cx + s.HalfX, s.Cz + s.HalfZ, s.Cx - s.HalfX, s.Cz + s.HalfZ }, 3));
        Team(v, s.Cx, s.Cz, 25f);
        v.Dots.Add(new MinimapDot { X = s.Cx + 5f,  Z = s.Cz - 4f,  Kind = MinimapDotKind.Monster, Slot = 3 });
        v.Dots.Add(new MinimapDot { X = s.Cx - 20f, Z = s.Cz - 20f, Kind = MinimapDotKind.Monster, Slot = 7 });
        v.Dots.Add(new MinimapDot { X = s.Cx + 20f, Z = s.Cz - 20f, Kind = MinimapDotKind.Monster, Slot = 4 });
        return v;
    }

    private static MinimapView Wasteland()
    {
        var s = MinimapArenas.Wasteland; var v = Spec(s);
        v.Regions.Add(MinimapRegion.Disc(s.Cx + 8f, s.Cz - 8f, 2.5f, 1));
        v.Regions.Add(MinimapRegion.Disc(s.Cx - 8f, s.Cz + 8f, 2.5f, 5));
        v.Regions.Add(MinimapRegion.Disc(s.Cx + 12f, s.Cz + 10f, 1.2f, 2));
        v.Regions.Add(MinimapRegion.Line(s.Cx + 12f, s.Cz + 10f, s.Cx + 2f, s.Cz - 2f, 2, 3f));
        v.Regions.Add(MinimapRegion.Disc(s.Cx - 14f, s.Cz - 12f, 1.5f, 1));
        Team(v, s.Cx, s.Cz, 18f);
        v.Dots.Add(new MinimapDot { X = s.Cx, Z = s.Cz + 4f, Kind = MinimapDotKind.Monster, Slot = 3 });
        return v;
    }

    private static MinimapView Giant()
    {
        var s = MinimapArenas.Giant; var v = Spec(s);
        Team(v, s.Cx, s.Cz, 20f);
        v.Dots.Add(new MinimapDot { X = s.Cx, Z = s.Cz, Kind = MinimapDotKind.Monster, Slot = -1 });
        v.Dots.Add(new MinimapDot { X = s.Cx + 20f, Z = s.Cz, Kind = MinimapDotKind.Monster, Slot = 6 });
        v.Dots.Add(new MinimapDot { X = s.Cx - 20f, Z = s.Cz, Kind = MinimapDotKind.Monster, Slot = 7 });
        return v;
    }

    private static MinimapView ReefMatrix()
    {
        var s = MinimapArenas.ReefMatrix; var v = Spec(s);
        float mx = s.Cx + 12f, mz = s.Cz + 5f;
        v.Regions.Add(MinimapRegion.Line(mx, mz, mx - 40f, mz - 20f, 7, 10f));
        Team(v, s.Cx, s.Cz, 14f);
        v.Dots.Add(new MinimapDot { X = mx, Z = mz, Kind = MinimapDotKind.Monster, Slot = 7 });
        return v;
    }
}
