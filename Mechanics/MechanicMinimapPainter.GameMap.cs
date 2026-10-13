namespace Stellar.RaidManager;

// "Show game map" (default OFF): the game's own radar floor-plan art (MechMapArtSource) baked into the STATIC layer as
// its bottom-most layer — over the background fill, under the border / arena grid / regions / dots / camera cone.
// Baked only when the layer cache key changes (art version / layout / rotation / extents / origin / background
// opacity), never per frame. Per output pixel: canvas px → world X/Z through the inverse of the SAME projector the
// shapes use (origin, rotationQuarters, scale), → art pixel via the art's affine GetImgPos map, bilinear sample, blend.
// Outside the art / transparent art = nothing (today's look).
//
// "Background opacity" (BgOpacity) = the alpha of the BACKGROUND only — the dark fill AND the game-map art on it; the
// border, grid, regions, dots and markers keep their own alphas. Default 0.68 = the upstream fill alpha (today's look);
// 1.0 = solid fill + full-strength art. User 2026-10-12: the slider is for the background, not the whole picture.
internal sealed partial class MechanicMinimapPainter
{
    public const float DefaultBgOpacity = 0.68f;

    public MinimapArt? Art;   // null = no game-map layer (option off, no art for this place, or a test view)

    public float BgOpacity = DefaultBgOpacity;

    private void DrawGameMap()
    {
        var art = Art;
        if (art == null || _p.Scale <= 0f || BgOpacity <= 0f) return;
        int n = _r.Size;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var (wx, wz) = _p.Unproject(x + 0.5f, y + 0.5f);
                if (art.Sample(wx, wz, out var c)) _r.BlendPixel(x, y, c, c.A * BgOpacity);
            }
    }
}
