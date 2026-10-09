using System;
using Stellar.Abstractions.Domain;

namespace Stellar.RaidManager;

// Rasterises a MinimapView — port of upstream minimap-canvas.svelte's draw(): same projection (rotationQuarters,
// PADDING 10, scale = min fit of the rotated half-extents, canvas x = cx − z·scale, canvas y = cy − x·scale, i.e.
// world +X points UP and world +Z points LEFT at rotation 0), same layer order (cached arena base → regions →
// entities) and the same colours/alphas/sizes as its defaults (settings-store.ts minimap defaults). Departures:
// the canvas is square (upstream sizes its canvas to the view aspect — here the view is letterboxed), and the soft
// shadow glow on mechanic-coloured dots is a translucent halo disc.
internal sealed class MechanicMinimapPainter : IDisposable
{
    private static readonly ColorRgba Bg       = Rgba(15, 23, 42, 1f);
    private static readonly ColorRgba Border   = Rgba(203, 213, 225, 1f);
    private static readonly ColorRgba SquareC  = Rgba(148, 163, 184, 1f);
    private static readonly ColorRgba LayoutC  = Rgba(226, 232, 240, 1f);
    private static readonly ColorRgba LocalC   = Rgba(248, 250, 252, 1f);   // #f8fafc
    private static readonly ColorRgba MateC    = Rgba(56, 189, 248, 1f);    // #38bdf8
    private static readonly ColorRgba BossC    = Rgba(239, 68, 68, 1f);     // #ef4444
    private static readonly ColorRgba White    = Rgba(255, 255, 255, 1f);
    private const float DeadAlpha = 0.35f;
    // Dot sizes, scaled with the canvas ("Minimap size"): the base values below are px at the 300-px base canvas.
    private readonly float LocalR, MateR, MonsterR, LocalRingW;

    private readonly MinimapRaster _r;
    private readonly float _k;            // canvas size / MinimapProjector.BasePx — every px size in here is × _k
    private string _baseKey = "";
    private MinimapProjector _p;
    private float _scale;

    // "Minimap size" re-creates the painter at the new size (a fresh raster rendered 1:1 — never a stretched 300-px
    // texture), so all drawn sizes — dot radii, ring/arrow, marker badges, line/stroke widths, hatching step, insets,
    // bitmap-text scale — are base px × _k and the whole picture grows proportionally with the map.
    public MechanicMinimapPainter(int size)
    {
        _r = new MinimapRaster(size);
        _k = size / (float)MinimapProjector.BasePx;
        LocalR = 4f * _k; MateR = 4f * _k; MonsterR = 10f * _k; LocalRingW = 2f * _k;
    }

    private float K(float basePx) => basePx * _k;

    public object? Texture => _r.Texture;

    // Display options (settings toggles), applied at paint time so live and test views behave the same.
    public bool ShowMarkers = true;
    // hideNormalTeammates: teammates with no mechanic colour (incl. safe-zone status) aren't drawn — dead ones too; the
    // local player is always drawn.
    public bool HideNormalTeammates;

    // Party markers 1..6 as BADGES ("pins", not text — so they can't be confused with the white "1F" tile labels):
    // a filled disc (r 8 ≈ 16 px) in the marker colour (upstream markerColors m1..m6), dark outline, soft drop shadow,
    // the number inside in black or white by contrast. A mark outside the canvas is CLAMPED to a 10 px inset edge so
    // you still see which way it lies (upstream just lets it fall off-canvas).
    private static readonly ColorRgba[] MarkerC =
    {
        Rgba(250, 204, 21, 1f), Rgba(251, 146, 60, 1f), Rgba(74, 222, 128, 1f),
        Rgba(103, 232, 249, 1f), Rgba(192, 132, 252, 1f), Rgba(37, 99, 235, 1f),
    };

    private void DrawMarker(int slot, float x, float z)
    {
        if (slot < 1 || slot > 6) return;
        var (px, py) = P(x, z);
        float lo = K(10f), hi = _r.Size - K(10f);
        px = MathF.Min(hi, MathF.Max(lo, px)); py = MathF.Min(hi, MathF.Max(lo, py));
        var c = MarkerC[slot - 1];
        float R = K(8f);
        _r.FillCircle(px + K(1.5f), py + K(2f), R + K(0.5f), Rgba(0, 0, 0, 1f), 0.45f);       // drop shadow
        _r.FillCircle(px, py, R, c, 1f);
        _r.StrokeCircle(px, py, R, K(1.5f), Rgba(15, 15, 20, 1f), 0.95f);
        float luma = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
        _r.Text(slot.ToString(), px, py + K(0.5f), K(2f), luma > 0.55f ? Rgba(10, 10, 12, 1f) : White, outline: false);
    }
    public void Dispose() => _r.Dispose();

    private static ColorRgba Rgba(int r, int g, int b, float a) => new(r / 255f, g / 255f, b / 255f, a);

    public void Paint(MinimapView v)
    {
        SetProjector(v);
        string key = $"{v.LayoutKey}|{v.RotationQuarters}|{v.HalfX}|{v.HalfZ}";
        if (key != _baseKey) { DrawBase(v); _r.SaveBase(); _baseKey = key; }
        else _r.RestoreBase();

        foreach (var reg in v.Regions) DrawRegion(reg);
        // Monsters under the team, local player last (on top).
        foreach (var d in v.Dots) if (d.Kind == MinimapDotKind.Monster)  DrawDot(d);
        foreach (var d in v.Dots)
            if (d.Kind == MinimapDotKind.Teammate && !(HideNormalTeammates && d.Slot < 0)) DrawDot(d);
        foreach (var d in v.Dots) if (d.Kind == MinimapDotKind.Local)    DrawDot(d);
        if (ShowMarkers) foreach (var m in v.Markers) DrawMarker(m.Slot, m.X, m.Z);
        _r.Upload();
    }

    // ── Projection (MinimapProjector below) ─────────────────────────────────────────────────────────────────
    private void SetProjector(MinimapView v)
    {
        _p = MinimapProjector.For(v, _r.Size);
        _scale = _p.Scale;
    }

    private (float X, float Y) P(float x, float z) => _p.Project(x, z);        // world
    private (float X, float Y) L(float x, float z) => _p.ProjectLocal(x, z);   // arena-local

    // ── Layers ───────────────────────────────────────────────────────────────────────────────────────────────
    private void DrawBase(MinimapView v)
    {
        _r.Clear();
        float s = _r.Size;
        _r.FillRect(0, 0, s, s, Bg, 0.68f);
        _r.StrokeRect(K(1f), K(1f), s - K(1f), s - K(1f), K(2f), Border, 0.72f);
        var (ox, oy) = L(0, 0);
        foreach (float half in v.Squares)
        {
            float h = half * _scale;
            _r.StrokeRect(ox - h, oy - h, ox + h, oy + h, K(1f), SquareC, 0.45f);
        }
        foreach (float r in v.Circles) _r.StrokeCircle(ox, oy, r * _scale, K(1.5f), LayoutC, 0.85f);
        foreach (var l in v.Lines)
        {
            var (sx, sy) = L(l.X1, l.Z1); var (ex, ey) = L(l.X2, l.Z2);
            _r.Line(sx, sy, ex, ey, K(2f), LayoutC, 0.9f);
        }
    }

    private void DrawRegion(in MinimapRegion reg)
    {
        var c = MechanicCalloutData.SlotColor(reg.Color);
        if (reg.Kind == MinimapRegionKind.Ring)
        {
            var (ox, oy) = L(0, 0);
            float ri = reg.RInner * _scale, ro = reg.ROuter * _scale;
            if (reg.Style == 3)
            {
                // Raid ring DANGER band (the two rings spawned in a wave): strong red fill + heavy red edges.
                var red = MechanicCalloutData.SlotColor(3);
                _r.FillAnnulus(ox, oy, ri, ro, red, 0.35f);
                if (ri > 0f) _r.StrokeCircle(ox, oy, ri, K(2.5f), red, 1f);
                _r.StrokeCircle(ox, oy, ro, K(2.5f), red, 1f);
                return;
            }
            if (reg.Style == 1)
            {
                // Raid ring SAFE band: calm pale-mint outline only (no fill), so it never reads as a hazard.
                if (ri > 0f) _r.StrokeCircle(ox, oy, ri, K(1.5f), CheckC, 0.95f);
                _r.StrokeCircle(ox, oy, ro, K(1.5f), CheckC, 0.95f);
                return;
            }
            _r.FillAnnulus(ox, oy, ri, ro, c, 0.18f);
            _r.StrokeCircle(ox, oy, ro, K(1.5f), c, 0.9f);
            return;
        }
        if (reg.Kind == MinimapRegionKind.Sector)  { DrawSector(reg, c);  return; }
        if (reg.Kind == MinimapRegionKind.Polygon) { DrawPolygon(reg, c); return; }
        if (reg.Kind == MinimapRegionKind.Line)    { DrawLineRegion(reg, c); return; }
        if (reg.Kind == MinimapRegionKind.Crystal) { DrawCrystal(reg); return; }
        if (reg.Kind == MinimapRegionKind.Text)
        {
            // Arena-local label (raid ring step numbers): white, dark-outlined, same size as the tile labels. Purge step
            // highlight: the CURRENT step is palette yellow and one size up, the other steps slate (dimmed).
            var (lx, ly) = L(reg.X, reg.Z);
            if (!string.IsNullOrEmpty(reg.Label))
                _r.Text(reg.Label!, lx, ly, K(reg.Style == 2 ? 5f : 4f),
                        reg.Style == 2 ? MechanicCalloutData.SlotColor(0) : reg.Style == 1 ? PressedC : White, outline: true);
            return;
        }
        var (ax, ay) = P(reg.X - reg.HalfX, reg.Z - reg.HalfZ);
        var (bx, by) = P(reg.X + reg.HalfX, reg.Z + reg.HalfZ);
        if (reg.Style != 0) { DrawFloorCell(reg.Style, ax, ay, bx, by, reg.Label); return; }
        _r.FillRect(ax, ay, bx, by, c, 0.22f);
        _r.StrokeRect(MathF.Min(ax, bx), MathF.Min(ay, by), MathF.Max(ax, bx), MathF.Max(ay, by), K(1.5f), c, 0.9f);
        if (!string.IsNullOrEmpty(reg.Label))
        {
            var (tx, ty) = P(reg.X, reg.Z);
            // Tile labels ("1F".."3F"): plain white, dark-outlined, larger than the marker badges' numbers.
            _r.Text(reg.Label!, tx, ty, K(4f), White, outline: true);
        }
    }

    // Dead team member (upstream dead style, shape "x"): a dimmed × in the dead colour (#ef4444, opacity 0.35, stroke
    // max(1, r·0.5)), overriding mechanic colour / halo. The local player keeps its white ring and its arrow (dead colour).
    private void DrawDead(float x, float y, in MinimapDot d)
    {
        float r = d.Kind == MinimapDotKind.Local ? LocalR : MateR, half = r * 0.866f, w = MathF.Max(1f, r * 0.5f);
        _r.Line(x - half, y - half, x + half, y + half, w, BossC, DeadAlpha);
        _r.Line(x + half, y - half, x - half, y + half, w, BossC, DeadAlpha);
        if (d.Kind != MinimapDotKind.Local) return;
        _r.StrokeCircle(x, y, r + LocalRingW, LocalRingW, White, 1f);
        if (d.HasFacing) DrawFacing(x, y, d, BossC, r);
    }

    // drawLocalFacing: an arrowhead just outside the local ring pointing along the world facing. The screen heading
    // comes from projecting a point 1 world unit ahead (x + sin f, z + cos f) through the same projector, so the
    // scene's rotationQuarters / axis convention applies exactly as for positions. Base = r + ring + 3, length 7,
    // half-width 4 px, dot colour.
    private void DrawFacing(float x, float y, in MinimapDot d, ColorRgba c, float rr)
    {
        float rad = d.Facing * MathF.PI / 180f;
        var (ax, ay) = P(d.X + MathF.Sin(rad), d.Z + MathF.Cos(rad));
        float dx = ax - x, dy = ay - y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-4f) return;
        dx /= len; dy /= len;
        float b = rr + LocalRingW + K(3f), tip = b + K(7f), hw = K(4f);
        _r.FillTriangle(x + dx * tip, y + dy * tip,
                        x + dx * b - dy * hw, y + dy * b + dx * hw,
                        x + dx * b + dy * hw, y + dy * b - dx * hw, c, 1f);
    }

    // Raid floor damage (Floor.cs): cracked = amber fill with diagonal crack hatching; destroyed = near-black fill with
    // an X outline. Drawn before the mechanic regions, so Phase / Preset cells stay on top. Style 3 = Phase Mapping
    // danger tile (PhaseMapping.cs), drawn after floor damage.
    private static readonly ColorRgba CrackC = Rgba(245, 158, 11, 1f), HoleC = Rgba(8, 8, 10, 1f), HoleEdge = Rgba(120, 120, 130, 1f);

    private void DrawFloorCell(int style, float ax, float ay, float bx, float by, string? label)
    {
        float x0 = MathF.Min(ax, bx), y0 = MathF.Min(ay, by), x1 = MathF.Max(ax, bx), y1 = MathF.Max(ay, by);
        if (style == 3)
        {
            // Phase Mapping DANGER tile: strong red fill + 45° hazard stripes + heavy red outline (no-go).
            var red = MechanicCalloutData.SlotColor(3);
            _r.FillRect(x0, y0, x1, y1, red, 0.35f);
            float h = y1 - y0;
            float step = K(9f), i1 = K(1f);
            for (float sx = x0 - h; sx < x1; sx += step)
            {
                // stripe from (sx, y1) to (sx + h, y0), clipped to the cell by its x range
                float ax0 = sx, ay0 = y1, ax1 = sx + h, ay1 = y0;
                if (ax0 < x0) { ay0 -= x0 - ax0; ax0 = x0; }
                if (ax1 > x1) { ay1 += ax1 - x1; ax1 = x1; }
                if (ax1 > ax0) _r.Line(ax0, ay0, ax1, ay1, K(2f), red, 0.75f);
            }
            _r.StrokeRect(x0 + i1, y0 + i1, x1 - i1, y1 - i1, K(2.5f), red, 1f);
            return;
        }
        if (style == 4) { DrawPressedCell(x0, y0, x1, y1, label); return; }
        float lw = K(1.5f);
        if (style == 1)
        {
            _r.FillRect(x0, y0, x1, y1, CrackC, 0.28f);
            float w = x1 - x0, h = y1 - y0;
            // Three jagged crack strokes across the cell.
            _r.Line(x0 + w * 0.10f, y0 + h * 0.20f, x0 + w * 0.45f, y0 + h * 0.55f, lw, CrackC, 0.95f);
            _r.Line(x0 + w * 0.45f, y0 + h * 0.55f, x0 + w * 0.35f, y0 + h * 0.90f, lw, CrackC, 0.95f);
            _r.Line(x0 + w * 0.45f, y0 + h * 0.55f, x0 + w * 0.85f, y0 + h * 0.35f, lw, CrackC, 0.95f);
            _r.Line(x0 + w * 0.85f, y0 + h * 0.35f, x0 + w * 0.95f, y0 + h * 0.75f, lw, CrackC, 0.95f);
            _r.StrokeRect(x0, y0, x1, y1, lw, CrackC, 0.9f);
            return;
        }
        float i3 = K(3f);
        _r.FillRect(x0, y0, x1, y1, HoleC, 0.78f);
        _r.Line(x0 + i3, y0 + i3, x1 - i3, y1 - i3, K(2f), HoleEdge, 0.9f);
        _r.Line(x1 - i3, y0 + i3, x0 + i3, y1 - i3, K(2f), HoleEdge, 0.9f);
        _r.StrokeRect(x0, y0, x1, y1, lw, HoleEdge, 0.9f);
    }

    // Preset Return crystals (Crystals.cs). Colours avoid the raid's other meanings: 1F/2F/3F cells are palette yellow /
    // GREEN / violet, danger is red, cracked amber, destroyed near-black — so "pressed" is a NEUTRAL slate tint with a
    // pale-mint check in the cell's top-right corner (clear of the centred "1F" label, which stays readable when the
    // pressed tile is also a Preset Return target: the overlay is drawn after it, light enough to keep its colour).
    // Crystal glyph = pale-cyan diamond with a white outline (circles = players, triangles = monsters); dimmed once
    // pressed while it is still present. Cyan is fine here: an object, not a player highlight (palette rule).
    private static readonly ColorRgba PressedC = Rgba(148, 163, 184, 1f), CheckC = Rgba(187, 247, 208, 1f),
                                      CrystalC = Rgba(165, 243, 252, 1f), Shadow = Rgba(10, 10, 12, 1f);

    private void DrawPressedCell(float x0, float y0, float x1, float y1, string? order)
    {
        _r.FillRect(x0, y0, x1, y1, PressedC, 0.22f);
        float i2 = K(2f);
        _r.StrokeRect(x0 + i2, y0 + i2, x1 - i2, y1 - i2, K(1.5f), PressedC, 0.85f);
        // ✓ in the top-right corner: short stroke down-right, long stroke up-right; dark under-stroke for contrast.
        float s = K(5f), cx = x1 - K(11f), cy = y0 + K(10f);
        float ax = cx - s, ay = cy, bx = cx - s * 0.3f, by = cy + s * 0.75f, ex = cx + s, ey = cy - s * 0.9f;
        _r.Line(ax, ay, bx, by, K(4f), Shadow, 0.7f); _r.Line(bx, by, ex, ey, K(4f), Shadow, 0.7f);
        _r.Line(ax, ay, bx, by, K(2.2f), CheckC, 1f); _r.Line(bx, by, ex, ey, K(2.2f), CheckC, 1f);
        if (string.IsNullOrEmpty(order)) return;
        // Press order "#N" on the check's row, just left of it (3 px gap): top band of the tile, so it never touches
        // the crystal glyph or the "1F" label (both centred in the cell). Text scale 2.5 (12.5 px tall at base) — the
        // raster's width formula (glyphs × 4 − 1) × scale gives its right edge exactly.
        float ts = K(2.5f), w = (order!.Length * 4 - 1) * ts;
        _r.Text(order, ax - K(3f) - w / 2f, cy, ts, CheckC, outline: true);
    }

    private void DrawCrystal(in MinimapRegion reg)
    {
        var (x, y) = P(reg.X, reg.Z);
        float hw = K(5f), hh = K(7.5f), a = reg.Style == 1 ? 0.35f : 1f;
        _r.FillTriangle(x, y - hh - K(1.5f), x + hw + K(1.5f), y, x - hw - K(1.5f), y, White, a * 0.9f);   // outline
        _r.FillTriangle(x, y + hh + K(1.5f), x + hw + K(1.5f), y, x - hw - K(1.5f), y, White, a * 0.9f);
        _r.FillTriangle(x, y - hh, x + hw, y, x - hw, y, CrystalC, a);
        _r.FillTriangle(x, y + hh, x + hw, y, x - hw, y, CrystalC, a);
        _r.Line(x - hw * 0.5f, y, x + hw * 0.5f, y, K(1f), White, a * 0.8f);                             // facet
    }

    // drawSectorRegion: centre + arc points every ≤ 8° (≥ 6 steps), point = (x + sin·r, z + cos·r); fill 0.24,
    // outline 0.92 @ 1.5 px. A full 360° sector (orbs/pools) closes back through the centre like upstream's path.
    private float[] _poly = new float[2 * 64];

    private void DrawSector(in MinimapRegion reg, ColorRgba c)
    {
        int steps = Math.Max(6, (int)MathF.Ceiling(MathF.Abs(reg.EndDeg - reg.StartDeg) / 8f));
        int n = steps + 2;
        if (_poly.Length < 2 * n) _poly = new float[2 * n];
        (_poly[0], _poly[1]) = P(reg.X, reg.Z);
        for (int i = 0; i <= steps; i++)
        {
            float rad = (reg.StartDeg + (reg.EndDeg - reg.StartDeg) * i / steps) * MathF.PI / 180f;
            (_poly[2 * (i + 1)], _poly[2 * (i + 1) + 1]) = P(reg.X + MathF.Sin(rad) * reg.ROuter, reg.Z + MathF.Cos(rad) * reg.ROuter);
        }
        FillStroke(n, c, 0.24f, 0.92f);
    }

    private void DrawPolygon(in MinimapRegion reg, ColorRgba c)
    {
        var pts = reg.Points;
        if (pts == null || pts.Length < 6) return;
        int n = pts.Length / 2;
        if (_poly.Length < 2 * n) _poly = new float[2 * n];
        for (int i = 0; i < n; i++) (_poly[2 * i], _poly[2 * i + 1]) = P(pts[2 * i], pts[2 * i + 1]);
        FillStroke(n, c, 0.24f, 0.92f);
    }

    private void FillStroke(int n, ColorRgba c, float fillA, float strokeA)
    {
        _r.FillPolygon(_poly, n, c, fillA);
        for (int i = 0, j = n - 1; i < n; j = i++)
            _r.Line(_poly[2 * j], _poly[2 * j + 1], _poly[2 * i], _poly[2 * i + 1], K(1.5f), c, strokeA);
    }

    // drawLineRegion: alpha 0.5, widthPx (default 2) — px at the base canvas, scaled like everything else.
    private void DrawLineRegion(in MinimapRegion reg, ColorRgba c)
    {
        var (sx, sy) = P(reg.X, reg.Z); var (ex, ey) = P(reg.X2, reg.Z2);
        _r.Line(sx, sy, ex, ey, K(reg.WidthPx > 0 ? reg.WidthPx : 2f), c, 0.5f);
    }

    // Team = circle (local white with a white ring, teammates sky-blue), mechanic colour overrides + halo;
    // everything else = upward triangle (boss red unless mechanic-coloured).
    private void DrawDot(in MinimapDot d)
    {
        if (d.Hidden) return;
        var (x, y) = P(d.X, d.Z);
        if (d.Dead && d.Kind != MinimapDotKind.Monster) { DrawDead(x, y, d); return; }
        bool mech = d.Slot >= 0;
        var c = mech ? MechanicCalloutData.SlotColor(d.Slot)
              : d.Kind == MinimapDotKind.Local ? LocalC : d.Kind == MinimapDotKind.Teammate ? MateC : BossC;
        if (d.Kind == MinimapDotKind.Monster)
        {
            float r = MonsterR;
            if (mech) _r.FillCircle(x, y, r + K(3f), c, 0.3f);
            _r.FillTriangle(x, y - r, x + r * 0.866f, y + r * 0.5f, x - r * 0.866f, y + r * 0.5f, c, 1f);
            return;
        }
        float rr = d.Kind == MinimapDotKind.Local ? LocalR : MateR;
        if (mech) _r.FillCircle(x, y, rr + K(4f), c, 0.35f);
        _r.FillCircle(x, y, rr, c, 1f);
        if (d.Kind == MinimapDotKind.Local)
        {
            _r.StrokeCircle(x, y, rr + LocalRingW, LocalRingW, White, 1f);
            if (d.HasFacing) DrawFacing(x, y, d, c, rr);
        }
    }
}

// World → canvas pixels, exactly upstream minimap-canvas.svelte makeProjector / rotateMapPoint / rotatedHalfExtents:
// PADDING 10, scale = min fit of the rotated half-extents, px = cx − z·scale, py = cy − x·scale (rotation 0: world
// +X up, +Z left). Pixel y grows DOWN.
internal readonly struct MinimapProjector
{
    public const float Pad = 10f;      // at the BASE canvas; scaled with the canvas so a resized map is the same picture
    public const int BasePx = 300;     // base canvas size ("Minimap size" 1.0×)
    public readonly float Scale, Cx, Cy, Ox, Oz;
    public readonly int Rot, Size;

    private MinimapProjector(float scale, float cx, float cy, int rot, int size, float ox, float oz)
    { Scale = scale; Cx = cx; Cy = cy; Rot = rot; Size = size; Ox = ox; Oz = oz; }

    public static MinimapProjector For(MinimapView v, int size)
    {
        int rot = ((v.RotationQuarters % 4) + 4) % 4;
        bool quarter = rot % 2 == 1;
        float halfW = quarter ? v.HalfX : v.HalfZ, halfH = quarter ? v.HalfZ : v.HalfX;
        float pad = Pad * size / BasePx;
        float scale = MathF.Min((size - pad * 2) / (halfW * 2), (size - pad * 2) / (halfH * 2));
        return new MinimapProjector(scale, size / 2f, size / 2f, rot, size, v.OriginX, v.OriginZ);
    }

    // WORLD point (regions, dots) → pixels: localize against the arena origin first (upstream toArenaLocal).
    public (float X, float Y) Project(float x, float z) => ProjectLocal(x - Ox, z - Oz);

    // ARENA-LOCAL point (layout shapes, rings) → pixels.
    public (float X, float Y) ProjectLocal(float x, float z)
    {
        (x, z) = Rot switch { 1 => (-z, x), 2 => (-x, -z), 3 => (z, -x), _ => (x, z) };
        return (Cx - z * Scale, Cy - x * Scale);
    }

}
