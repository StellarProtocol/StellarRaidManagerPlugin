using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// A localized single arena — the shared shape of upstream's per-scene arena.ts (every scene but the raid):
// ARENA_CENTER (x, y, z), WORLD_HALF_X/Z, rotationQuarters, layout shapes (arena-local), Y_HALF_RANGE (yInArena) and
// BOSS_AREA_MARGIN (inBossArea). Apply() writes the layout + origin into a MinimapView; the view builders use
// YIn/InBossArea for upstream's visibleEntities filter.
internal sealed class MinimapArenaSpec
{
    public string Key = "";
    public float  Cx, Cy, Cz;              // world centre
    public float  HalfX, HalfZ;            // world half-extents (view fit)
    public int    Rot;                     // rotationQuarters
    public float  YRange = 18f;            // |y − Cy| ≤ YRange ⇒ on this arena's floor
    public float  Margin = 4f;             // inBossArea margin beyond the half-extents
    public readonly List<(float X1, float Z1, float X2, float Z2)> Lines = new();
    public readonly List<float> Circles = new();
    public readonly List<float> Squares = new();

    public bool YIn(float y) => MathF.Abs(y - Cy) <= YRange;

    public bool InBossArea(float x, float z) =>
        MathF.Abs(x - Cx) <= HalfX + Margin && MathF.Abs(z - Cz) <= HalfZ + Margin;

    public void Apply(MinimapView v)
    {
        v.HalfX = HalfX; v.HalfZ = HalfZ; v.RotationQuarters = Rot;
        v.OriginX = Cx; v.OriginZ = Cz; v.LayoutKey = Key;
        v.Lines.AddRange(Lines); v.Circles.AddRange(Circles); v.Squares.AddRange(Squares);
    }

    // Layout helpers (arena-local).
    public MinimapArenaSpec Rect(float hx, float hz)
    {
        Lines.Add((-hx, -hz, hx, -hz)); Lines.Add((hx, -hz, hx, hz));
        Lines.Add((hx, hz, -hx, hz));   Lines.Add((-hx, hz, -hx, -hz));
        return this;
    }

    public MinimapArenaSpec Radial(float radius, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float rad = MathF.PI * 2f * i / count;
            Lines.Add((0f, 0f, MathF.Cos(rad) * radius, MathF.Sin(rad) * radius));
        }
        return this;
    }
}

// Arena data per scene (ported constants; comments name the upstream file).
internal static class MinimapArenas
{
    // ── s3-sea-ringed-reef/arena.ts: two arenas told apart by Y (nearest centre Y) ───────────────────────────
    // matrix: centre (−74, 75, 12), 24×24, rotation 2, ring r16 + 8 radial spokes.
    // boss:   centre (−330, 27, 101), 33×27 (walls cropped 5/6), rotation 3, outline rect. Unknown → boss, rotation 0.
    // Upstream reef visibleEntities filters by arena Y only (no boss-area box) → effectively unlimited Margin.
    public static readonly MinimapArenaSpec ReefMatrix = new MinimapArenaSpec
    { Key = "reef:matrix", Cx = -74f, Cy = 75f, Cz = 12f, HalfX = 24f, HalfZ = 24f, Rot = 2, YRange = 24f, Margin = 1e4f }
        .Radial(16f, 8);
    public static readonly MinimapArenaSpec ReefBoss = new MinimapArenaSpec
    { Key = "reef:boss", Cx = -330f, Cy = 27f, Cz = 101f, HalfX = 33f, HalfZ = 27f, Rot = 3, YRange = 24f, Margin = 1e4f }
        .Rect(33f, 27f);

    // ── s3-tina-mindrealm/arena.ts: centre (0, 110, 0), view 20×20 (cabinet ring r16 + buffer), rotation 1,
    // Y band ±15 (excludes the layer-1 sub-bosses at Y≈79), boss-area margin 6.
    public static readonly MinimapArenaSpec Tina = new MinimapArenaSpec
    { Key = "tina", Cx = 0f, Cy = 110f, Cz = 0f, HalfX = 20f, HalfZ = 20f, Rot = 1, YRange = 15f, Margin = 6f };

    // ── s3-cursed-tomb/arena.ts: centre (69, 62.2, −307), 32×30, rotation 1, Y ±18, margin 4; outline rect + a 3×3
    // floor grid of 13.5-unit cells (edges ±20.25, ±6.75).
    public static readonly MinimapArenaSpec Tomb = TombSpec();

    private static MinimapArenaSpec TombSpec()
    {
        var s = new MinimapArenaSpec { Key = "tomb", Cx = 69f, Cy = 62.2f, Cz = -307f, HalfX = 32f, HalfZ = 30f, Rot = 1, YRange = 18f, Margin = 4f }
            .Rect(32f, 30f);
        const float sp = 13.5f, half = sp * 1.5f;
        foreach (float e in new[] { -half, -sp / 2f, sp / 2f, half })
        {
            s.Lines.Add((e, -half, e, half));
            s.Lines.Add((-half, e, half, e));
        }
        return s;
    }

    // ── s4-wasteland-court/arena.ts: centre (−25.05, 129.06, 39.86), 22×22, rotation 0, Y ±18, margin 4, outline rect.
    public static readonly MinimapArenaSpec Wasteland = new MinimapArenaSpec
    { Key = "wasteland", Cx = -25.05f, Cy = 129.06f, Cz = 39.86f, HalfX = 22f, HalfZ = 22f, Rot = 0, YRange = 18f, Margin = 4f }
        .Rect(22f, 22f);

    // ── s3-giant-tower/arena.ts: centre (−187.5, 89, −385), 28×28, rotation 1, Y ±22, margin 4; outline rect + portal
    // ring r20. Upstream draws NO regions in this scene (portals / gravity are dot colours and rows only).
    public static readonly MinimapArenaSpec Giant = new MinimapArenaSpec
    { Key = "giant", Cx = -187.5f, Cy = 89f, Cz = -385f, HalfX = 28f, HalfZ = 28f, Rot = 1, YRange = 22f, Margin = 4f }
        .Rect(28f, 28f);

    static MinimapArenas() { ReefMatrix.Circles.Add(16f); Tina.Circles.Add(16f); Giant.Circles.Add(20f); }
}
