using UnityEngine;

namespace Stellar.RaidManager;

/// <summary>
/// Procedural flat-white trash-can icon for the per-preset Delete button in the Mark Presets list.
/// Mirrors the CombatMeter header-icon pattern (StellarCombatMeterPlugin/Plugin.LocationPinIcon.cs):
/// SDF triangle/segment fills, white with a soft-AA edge, baked at 2× the on-screen footprint so it
/// stays crisp after the framework's downsample.
///
/// Composition: a slightly-tapered bucket BODY (two triangles) carved with three vertical stripe gaps,
/// a LID bar across the top, and a small HANDLE bar above the lid.
/// </summary>
public sealed partial class Plugin
{
    private const int TrashIconTexPx = 64;

    private byte[]? _trashPng;

    private static byte[]? BuildTrashIconPng()
    {
        const int n = TrashIconTexPx;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, mipChain: false);
        var px  = new Color[n * n];
        const float aa = 1.5f;

        // Bucket body: tapered quad (wider at top than bottom), built from two triangles. y grows upward.
        var bTL = new Vector2(n * 0.22f, n * 0.68f);
        var bTR = new Vector2(n * 0.78f, n * 0.68f);
        var bBR = new Vector2(n * 0.72f, n * 0.10f);
        var bBL = new Vector2(n * 0.28f, n * 0.10f);

        // Three vertical stripe gaps carved out of the body.
        var s1Top = new Vector2(n * 0.38f, n * 0.60f); var s1Bot = new Vector2(n * 0.38f, n * 0.18f);
        var s2Top = new Vector2(n * 0.50f, n * 0.60f); var s2Bot = new Vector2(n * 0.50f, n * 0.18f);
        var s3Top = new Vector2(n * 0.62f, n * 0.60f); var s3Bot = new Vector2(n * 0.62f, n * 0.18f);
        float stripeHalfW = n * 0.035f;

        // Lid bar across the top — slightly wider than the body top so it reads as an overhanging rim.
        var lidL = new Vector2(n * 0.15f, n * 0.74f);
        var lidR = new Vector2(n * 0.85f, n * 0.74f);
        float lidHalfH = n * 0.05f;

        // Handle: short bar centred above the lid.
        var hndL = new Vector2(n * 0.42f, n * 0.85f);
        var hndR = new Vector2(n * 0.58f, n * 0.85f);
        float hndHalfH = n * 0.035f;

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);

            // Body = union of the two triangles; carve the stripe gaps via SDF subtraction (max(body, -stripes)).
            float dBody    = Mathf.Min(SdfTriangle(p, bTL, bTR, bBR), SdfTriangle(p, bTL, bBR, bBL));
            float dStripes = Mathf.Min(Mathf.Min(DistToSegment(p, s1Top, s1Bot),
                                                 DistToSegment(p, s2Top, s2Bot)),
                                                 DistToSegment(p, s3Top, s3Bot)) - stripeHalfW;
            float dCarved  = Mathf.Max(dBody, -dStripes);

            float dLid = DistToSegment(p, lidL, lidR) - lidHalfH;
            float dHnd = DistToSegment(p, hndL, hndR) - hndHalfH;

            float d = Mathf.Min(Mathf.Min(dCarved, dLid), dHnd);
            px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d / aa));
        }

        tex.SetPixels(px);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        try   { return ImageConversion.EncodeToPNG(tex); }
        finally { Object.Destroy(tex); }
    }

    // ── SDF helpers (self-contained, copied from the CombatMeter icon partials) ──────────────────────

    // Signed distance to a triangle (negative inside). Winding-agnostic: the sign is derived from the shape.
    private static float SdfTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var e0 = b - a; var e1 = c - b; var e2 = a - c;
        var v0 = p - a; var v1 = p - b; var v2 = p - c;

        var pq0 = v0 - e0 * Mathf.Clamp01(Vector2.Dot(v0, e0) / Vector2.Dot(e0, e0));
        var pq1 = v1 - e1 * Mathf.Clamp01(Vector2.Dot(v1, e1) / Vector2.Dot(e1, e1));
        var pq2 = v2 - e2 * Mathf.Clamp01(Vector2.Dot(v2, e2) / Vector2.Dot(e2, e2));

        float s = Mathf.Sign(e0.x * e2.y - e0.y * e2.x);
        var   d = new Vector2(
            Mathf.Min(Mathf.Min(pq0.sqrMagnitude, pq1.sqrMagnitude), pq2.sqrMagnitude),
            Mathf.Min(Mathf.Min(s * (v0.x * e0.y - v0.y * e0.x),
                                s * (v1.x * e1.y - v1.y * e1.x)),
                                s * (v2.x * e2.y - v2.y * e2.x)));

        return -Mathf.Sqrt(d.x) * Mathf.Sign(d.y);
    }

    // Shortest distance from point p to the segment [a,b].
    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.sqrMagnitude;
        float t = len2 <= 1e-4f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return (p - (a + ab * t)).magnitude;
    }
}
