using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// Scene-agnostic minimap view — the C# shape of upstream resonance-logs-cn `SceneView` (scene-types.ts): world
// half-extents + rotation, static arena layout (lines / circles / squares around the arena origin), mechanic regions
// and the entity dots to draw. Per-scene view builders (MechanicCalloutTracker.Minimap*.cs) fill it each scan; the
// painter (MechanicMinimapPainter.cs) rasterises it.
//
// Localization: upstream maps every entity/region through toArenaLocal (x − centre.x, z − centre.z) for all scenes
// except the raid. Here regions and dots stay in WORLD x/z and the view carries the arena origin (OriginX/OriginZ);
// the projector subtracts it. Layout shapes are already arena-local (around the origin), exactly as upstream.
internal sealed class MinimapView
{
    public float HalfX = 30f, HalfZ = 27f;
    public float OriginX, OriginZ;                        // world point drawn at the arena centre (0,0 for the raid)
    public int   RotationQuarters;
    public string LayoutKey = "";                         // identity of the static layout (painter caches its base layer)
    public readonly List<(float X1, float Z1, float X2, float Z2)> Lines = new();
    public readonly List<float> Circles = new();          // radii around the origin
    public readonly List<float> Squares = new();          // half-sizes around the origin
    public readonly List<MinimapRegion> Regions = new();
    public readonly List<MinimapDot>    Dots = new();
    // Placed party markers 1..6 (punctuate field marks), world x/z; not spatially filtered (upstream drawMarkers).
    public readonly List<(int Slot, float X, float Z)> Markers = new();

    public void Clear()
    {
        Lines.Clear(); Circles.Clear(); Squares.Clear(); Regions.Clear(); Dots.Clear(); Markers.Clear();
        RotationQuarters = 0; LayoutKey = ""; OriginX = OriginZ = 0f;
    }
}

// Upstream MechanicRegion kinds: rect / ring / sector / polygon / line.
internal enum MinimapRegionKind { Rect, Ring, Sector, Polygon, Line, Crystal, Text }   // Crystal = raid Preset Return crystal glyph (point); Text = label at an ARENA-LOCAL point

internal struct MinimapRegion
{
    public MinimapRegionKind Kind;
    public float X, Z, HalfX, HalfZ;   // Rect: world centre + half extents. Sector/Line: X,Z = centre / start point
    public float RInner, ROuter;       // Ring (around the arena origin); Sector: ROuter = radius
    public float StartDeg, EndDeg;     // Sector (world yaw degrees; point = (x + sin·r, z + cos·r))
    public float X2, Z2, WidthPx;      // Line end + stroke width in px (0 → 2)
    public float[]? Points;            // Polygon: world x,z pairs
    public int   Color;                // palette slot (MechanicCalloutData.SlotColor)
    public string? Label;              // digits + 'F' / '?' / '#' only (the painter's bitmap font), e.g. "2F"; Style 4 = press order "N"
    public int   Style;                // Rect: 0 = mechanic cell, 1 = cracked floor (hatched), 2 = destroyed floor (dark + X),
                                       //       3 = DANGER no-go tile (red fill + hazard stripes + heavy outline; Phase Mapping / Explosions)
                                       //       4 = Preset Return crystal PRESSED on this tile (grey tint + check mark, label-safe)
                                       // Crystal: 1 = pressed but still present (dimmed glyph)
                                       // Text:    0 = plain white, 1 = dimmed (not the current Purge step), 2 = CURRENT Purge step (bright, larger)
                                       // Ring:    3 = raid ring DANGER band (red fill + heavy edges), 1 = SAFE band (calm outline only)

    public static MinimapRegion Ring(float rInner, float rOuter, int color) =>
        new() { Kind = MinimapRegionKind.Ring, RInner = rInner, ROuter = rOuter, Color = color };

    public static MinimapRegion Sector(float x, float z, float radius, float startDeg, float endDeg, int color) =>
        new() { Kind = MinimapRegionKind.Sector, X = x, Z = z, ROuter = radius, StartDeg = startDeg, EndDeg = endDeg, Color = color };

    // A full disc = upstream's `sector` with startDeg 0 / endDeg 360 (how it draws orbs / pools / balls).
    public static MinimapRegion Disc(float x, float z, float radius, int color) => Sector(x, z, radius, 0f, 360f, color);

    public static MinimapRegion Line(float x1, float z1, float x2, float z2, int color, float widthPx = 2f) =>
        new() { Kind = MinimapRegionKind.Line, X = x1, Z = z1, X2 = x2, Z2 = z2, WidthPx = widthPx, Color = color };

    public static MinimapRegion Polygon(float[] xz, int color) =>
        new() { Kind = MinimapRegionKind.Polygon, Points = xz, Color = color };

    // Raid Preset Return crystal at world x/z: a fixed-px painter glyph (size scales with the canvas, not the world).
    public static MinimapRegion Crystal(float x, float z, bool pressed) =>
        new() { Kind = MinimapRegionKind.Crystal, X = x, Z = z, Style = pressed ? 1 : 0 };

    // A painter-font label (digits / 'F' / '?') at an ARENA-LOCAL x/z (around the origin, like Ring) — e.g. the raid
    // ring step numbers drawn inside each preview wave's safe band.
    public static MinimapRegion Text(float localX, float localZ, string label) =>
        new() { Kind = MinimapRegionKind.Text, X = localX, Z = localZ, Label = label };
}

internal enum MinimapDotKind { Local, Teammate, Monster }

internal struct MinimapDot
{
    public float X, Z;
    public MinimapDotKind Kind;
    public int   Slot;                 // mechanic colour slot, -1 = none (team default / boss red)
    public bool  Hidden;               // fast-path read failed (stale/destroyed entity) — skip until the next scan
    public float Facing;               // yaw degrees (upstream convention), valid when HasFacing — local player arrow
    public bool  HasFacing;
    public bool  Dead;                 // team member dead → dimmed × (overrides mechanic colour)
}

// ── S3 raid "Forgotten Dreamwild" arena (port of scenes/s3-raid/arena.ts) ────────────────────────────────────
// Two arenas in one scene, told apart by the local player's world Y: the ring arena (boss 1, ~Y 150) and the 3×3
// floor-grid arena (~Y 400). Everything is in RAW world x/z around the origin (upstream renders the raid unlocalized).
internal static class RaidArena
{
    public enum Kind { Unknown, Ring, Grid }

    private const float RingY = 150f, GridY = 400f;
    public const float CellHalfX = 10f, CellHalfZ = 7.5f;

    // Floor cells: (upstream id, English name, centre x, centre z). Names = upstream en-US `minimap.s3Raid.floor.*`
    // where it has them; the three middle-column cells have no upstream string (named here).
    public static readonly (string Id, string Name, float X, float Z)[] Cells =
    {
        ("topLeft",    "Top Left",      -20f,  15f), ("topMid",    "Top Middle",     0f,  15f), ("topRight",    "Top Right",     20f,  15f),
        ("midLeft",    "Middle Left",   -20f,   0f), ("center",    "Center",         0f,   0f), ("midRight",    "Middle Right",  20f,   0f),
        ("bottomLeft", "Bottom Left",   -20f, -15f), ("bottomMid", "Bottom Middle",  0f, -15f), ("bottomRight", "Bottom Right",  20f, -15f),
    };
    public static readonly string[] CornerCells  = { "topLeft", "topRight", "bottomLeft", "bottomRight" };
    public static readonly string[] EdgeMidCells = { "topMid", "bottomMid", "midLeft", "midRight" };

    public static Kind ByY(float? y) =>
        y is not { } v ? Kind.Unknown : MathF.Abs(v - RingY) <= MathF.Abs(v - GridY) ? Kind.Ring : Kind.Grid;

    public static bool YInArena(float y, Kind arena) => arena switch
    {
        Kind.Ring => MathF.Abs(y - RingY) <= MathF.Abs(y - GridY),
        Kind.Grid => MathF.Abs(y - GridY) <  MathF.Abs(y - RingY),
        _         => true,
    };

    public static int CellIndex(string id)
    {
        for (int i = 0; i < Cells.Length; i++) if (Cells[i].Id == id) return i;
        return -1;
    }

    public static int NearestCell(float x, float z)
    {
        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < Cells.Length; i++)
        {
            float dx = x - Cells[i].X, dz = z - Cells[i].Z, d = dx * dx + dz * dz;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    public static MinimapRegion CellRect(int cell, int color, string? label = null) => new()
    {
        Kind = MinimapRegionKind.Rect, X = Cells[cell].X, Z = Cells[cell].Z,
        HalfX = CellHalfX, HalfZ = CellHalfZ, Color = color, Label = label,
    };

    // arenaLayout(): grid = 4×4 floor lines (±30 × ±22.5), half-extents 30×27; ring = 5 circles, 2 squares and four
    // diamond "sector" outlines, half-extents 55×55; unknown = empty 30×27.
    public static void Layout(Kind arena, MinimapView v)
    {
        switch (arena)
        {
            case Kind.Grid:
                v.HalfX = 30f; v.HalfZ = 27f; v.LayoutKey = "raid:grid";
                foreach (float x in new[] { -30f, -10f, 10f, 30f }) v.Lines.Add((x, -22.5f, x, 22.5f));
                foreach (float z in new[] { -22.5f, -7.5f, 7.5f, 22.5f }) v.Lines.Add((-30f, z, 30f, z));
                break;
            case Kind.Ring:
                v.HalfX = 55f; v.HalfZ = 55f; v.LayoutKey = "raid:ring";
                v.Circles.AddRange(new[] { 11.5f, 12.5f, 17.5f, 18.5f, 30f });
                v.Squares.AddRange(new[] { 30f, 50f });
                AddPoly(v, (-21f, 21.21f), (0f, 0f), (21f, 21.21f), (0f, 42.21f));
                AddPoly(v, (21f, 21.21f), (0f, 0f), (21f, -21.21f), (42.21f, 0f));
                AddPoly(v, (21f, -21.21f), (0f, 0f), (-21f, -21.21f), (0f, -42.21f));
                AddPoly(v, (-21f, -21.21f), (0f, 0f), (-21f, 21.21f), (-42.21f, 0f));
                break;
            default:
                v.HalfX = 30f; v.HalfZ = 27f; v.LayoutKey = "raid:unknown";
                break;
        }
    }

    private static void AddPoly(MinimapView v, params (float X, float Z)[] pts)
    {
        for (int i = 0; i < pts.Length; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Length];
            v.Lines.Add((a.X, a.Z, b.X, b.Z));
        }
    }
}
