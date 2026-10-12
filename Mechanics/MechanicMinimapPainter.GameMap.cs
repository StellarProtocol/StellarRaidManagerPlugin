namespace Stellar.RaidManager;

// "Show game map" (default OFF): the game's own radar floor-plan art (MechMapArtSource) baked into the STATIC layer as
// its bottom-most layer — over the background fill, under the border / arena grid / regions / dots / camera cone.
// Baked only when the layer cache key changes (art version / layout / rotation / extents / origin), never per frame.
// Per output pixel: canvas px → world X/Z through the inverse of the SAME projector the shapes use (origin,
// rotationQuarters, scale), → art pixel via the art's affine GetImgPos map, bilinear sample, blend at GameMapAlpha so
// the grid and danger colours stay readable. Outside the art / transparent art = nothing (today's look).
// "Minimap opacity" (Opacity) is applied by the raster at upload, over the whole picture.
internal sealed partial class MechanicMinimapPainter
{
    private const float GameMapAlpha = 0.55f;   // fixed dim of the game art under our layers

    public MinimapArt? Art;   // null = no game-map layer (option off, no art for this place, or a test view)

    public float Opacity { get => _r.Opacity; set => _r.Opacity = value; }

    private void DrawGameMap()
    {
        var art = Art;
        if (art == null || _p.Scale <= 0f) return;
        int n = _r.Size;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var (wx, wz) = _p.Unproject(x + 0.5f, y + 0.5f);
                if (art.Sample(wx, wz, out var c)) _r.BlendPixel(x, y, c, c.A * GameMapAlpha);
            }
    }
}
