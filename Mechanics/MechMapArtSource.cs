using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.RaidManager;

/// <summary>
/// The game's OWN radar map art as a minimap background ("Show game map", default OFF). Adapted from
/// EntityInspector's <c>LocationMapSource</c> (plugins don't share code): finds the live radar widget
/// <c>Panda.ZUi.MiniMap</c> (<c>node_map_comp</c>), reads its <c>mapBgImg_</c> texture and copies it into a managed
/// RGBA32 buffer ONCE per texture / layer / scene change via a GPU readback (Blit → ReadPixels — the art is not
/// CPU-readable; log-confirmed 4096² non-readable, Social-Location-Probe.md). Projection = the game's own
/// <c>MiniMapManager.GetImgPos(world, readbackSize)</c> (validated: bottom-left-origin region pixels, world X→u, Z→v,
/// Y ignored — Minimap-World-Projection.md), sampled at three world points to get an affine world→art-pixel map, so
/// the painter never reflects per pixel. Reflection only, no Harmony; Unity main thread only.
/// </summary>
internal sealed class MechMapArtSource : IDisposable
{
    // Readback cap: 4096² art → 2048² (16 MB as RGBA32). GetImgPos is called with the readback size, so the mapping
    // stays exact; the arena only spans a few hundred art px, so the downscale costs nothing visible.
    internal const int MaxReadback = 2048;
    // The texture/layer check is cheap but reflective — once a second is plenty (the art changes on a floor/region
    // change, never mid-fight). A failed widget scan (full Resources.FindObjectsOfTypeAll) retries every 3 s.
    private const long CheckEveryMs = 1000, RescanEveryMs = 3000;

    private readonly Action<string> _warn;
    private readonly Action<string>? _diag;   // diagnostics sink; null = silent (RaidManager)
    public MechMapArtSource(Action<string> warn, Action<string>? diag) { _warn = warn; _diag = diag; }

    public MinimapArt? Art { get; private set; }

    private bool _resolved, _resolveFailed;
    private Type? _widgetType, _widgetBaseType, _mgrType;
    private PropertyInfo? _piMapBgSize, _piFlagParent, _piMapBgImg, _piCurLayerId;
    private MethodInfo? _miGetImgPos, _miGetAddr, _miRegionSize, _miRegionCenter;
    private UnityEngine.Object? _widget;   // Unity-overloaded null: goes "null" when the scene rebuilds the radar
    private object? _mgr;
    private long _nextCheck, _nextScan;
    private string? _key, _failedKey, _missingLogged;

    /// <summary>Keep <see cref="Art"/> in step with the radar (throttled to <see cref="CheckEveryMs"/>). Call only
    /// while the option is on and a live arena view exists. Returns true when <see cref="Art"/> changed.</summary>
    public bool Poll(int sceneId, float originX, float originZ)
    {
        long now = Environment.TickCount64;
        if (now < _nextCheck) return false;
        _nextCheck = now + CheckEveryMs;
        var prev = Art;
        try { Refresh(sceneId, originX, originZ, now); }
        catch (Exception ex) { WarnOnce("poll", $"game map refresh failed: {ex.Message}"); }
        return !ReferenceEquals(prev, Art);
    }

    /// <summary>Free the art (option off / left the arena / dispose). Returns true when there was art.</summary>
    public bool Drop()
    {
        bool had = Art != null;
        Art = null; _key = null; _failedKey = null; _missingLogged = null;
        return had;
    }

    public void Dispose() { Drop(); _widget = null; _mgr = null; }

    private void Refresh(int sceneId, float ox, float oz, long now)
    {
        if (Art != null && Art.SceneId != sceneId) Drop();   // never show one scene's art in another
        if (!Resolve()) { Missing(sceneId, "radar types not resolvable"); return; }
        var tex = CurrentTexture(now, out var img);
        // Radar gone / not built / no art: keep this scene's art if we already have it (the widget is rebuilt across
        // UI transitions), otherwise just today's look.
        if (tex == null) { Missing(sceneId, _widget == null ? "no live radar widget" : "radar has no map texture"); return; }
        int layer = ReadLayer();
        string key = $"{sceneId}|{tex.GetInstanceID()}|{tex.width}x{tex.height}|L{layer}";
        if (key == _key && Art != null) return;
        if (key == _failedKey) return;   // a failed readback is not retried until the texture/layer changes

        Art = null; _key = null;
        if (!Readback(tex, MaxReadback, m => WarnOnce("readback", m), out var buf, out int w, out int h)
            || !Mapping(ox, oz, w, h, out var m))
        {
            _failedKey = key;
            _diag?.Invoke($"[MechMapBg] load FAILED scene={sceneId} tex={tex.name} {tex.width}x{tex.height} layer={layer} (see warnings)");
            return;
        }
        Art = new MinimapArt(buf, w, h, sceneId, ox, oz, m);
        _key = key; _failedKey = null; _missingLogged = null;
        if (_diag != null) LogLoad(tex, img, layer, sceneId);
    }

    // World → art pixel is affine (GetImgPos = (world − BL)/RegionSize × size on X/Z): sample it at the arena origin
    // and 100 u along +X / +Z, keeping the cross terms so an axis swap in the game's mapping would still be honoured.
    private bool Mapping(float ox, float oz, int w, int h, out (float U0, float UX, float UZ, float V0, float VX, float VZ) m)
    {
        m = default;
        if (!ImgPos(new Vector3(ox, 0f, oz), w, h, out var p0)
            || !ImgPos(new Vector3(ox + 100f, 0f, oz), w, h, out var px)
            || !ImgPos(new Vector3(ox, 0f, oz + 100f), w, h, out var pz)) return false;
        m = (p0.x, (px.x - p0.x) / 100f, (pz.x - p0.x) / 100f, p0.y, (px.y - p0.y) / 100f, (pz.y - p0.y) / 100f);
        float det = m.UX * m.VZ - m.UZ * m.VX;
        if (MathF.Abs(det) < 1e-8f) { WarnOnce("mapping", "GetImgPos mapping is degenerate"); return false; }
        return true;
    }

    private bool ImgPos(Vector3 world, int w, int h, out Vector2 px)
    {
        px = default;
        var mgr = Manager;
        if (mgr == null || _miGetImgPos == null) return false;
        try
        {
            if (_miGetImgPos.Invoke(mgr, new object[] { world, new Vector2(w, h) }) is not Vector2 p) return false;
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.x) || float.IsInfinity(p.y)) return false;
            px = p; return true;
        }
        catch (Exception ex) { WarnOnce("imgpos", $"GetImgPos failed: {ex.InnerException?.Message ?? ex.Message}"); return false; }
    }

    // ---- resolve (LocationMapSource recipe) ----

    private bool Resolve()
    {
        if (_resolved) return !_resolveFailed;
        try
        {
            _widgetType     = StellarInterop.FindType("Panda.ZUi.MiniMap");
            _widgetBaseType = StellarInterop.FindType("Panda.ZUi.MiniMapBase");
            _mgrType        = StellarInterop.FindType("Panda.ZGame.MiniMapManager");
            if (_widgetType == null || _widgetBaseType == null || _mgrType == null) return false;   // not loaded yet — retry

            // mapBgImg_/mapBgSize_/flagParent_ are declared on MiniMapBase — FindPropertyUp walks the base chain.
            _piMapBgImg     = StellarInterop.FindPropertyUp(_widgetType, "mapBgImg_");
            _piMapBgSize    = StellarInterop.FindPropertyUp(_widgetType, "mapBgSize_");
            _piFlagParent   = StellarInterop.FindPropertyUp(_widgetType, "flagParent_");
            _piCurLayerId   = StellarInterop.FindPropertyUp(_mgrType, "CurLayerId");
            _miGetImgPos    = FindMethod(_mgrType, "GetImgPos", 2);
            _miGetAddr      = FindMethod(_mgrType, "GetCurRegionMapAddress", 0);   // diag only
            _miRegionSize   = FindMethod(_mgrType, "GetCurRegionSize", 0);         // diag only
            _miRegionCenter = FindMethod(_mgrType, "GetCurRegionCenter", 0);       // diag only

            _resolved = true;
            _resolveFailed = _piMapBgImg == null || _miGetImgPos == null;   // the two essentials
            if (_resolveFailed)
                WarnOnce("resolve", $"resolve incomplete mapBgImg={_piMapBgImg != null} getImgPos={_miGetImgPos != null}");
            return !_resolveFailed;
        }
        catch (Exception ex)
        {
            WarnOnce("resolve", $"resolve error: {ex.Message}");
            _resolved = true; _resolveFailed = true; return false;
        }
    }

    private object? Manager
    {
        get
        {
            if (_mgr != null) return _mgr;
            try { _mgr = StellarInterop.GetSingleton(_mgrType!); } catch { _mgr = null; }
            return _mgr;
        }
    }

    private Texture? CurrentTexture(long now, out RawImage? img)
    {
        img = null;
        if (_widget == null)
        {
            if (now < _nextScan) return null;
            _nextScan = now + RescanEveryMs;
            _widget = FindWidget() as UnityEngine.Object;
            if (_widget == null) return null;
        }
        try
        {
            img = _piMapBgImg!.GetValue(_widget) as RawImage;   // ZRawImage : RawImage — the interop wrapper is typed
            var tex = img != null ? img.texture : null;
            return tex != null && tex.width > 1 && tex.height > 1 ? tex : null;
        }
        catch (Exception ex) { WarnOnce("tex", $"mapBgImg_ read failed: {ex.Message}"); _widget = null; return null; }
    }

    // Scan the BASE type (a scan of the concrete MiniMap returns 0 in-game) and TryCast each hit — FindObjectsOfTypeAll
    // hands back base UnityEngine.Object wrappers. Two MiniMap instances exist (live radar + inactive template): take
    // the active, initialised one (mapBgSize_ > 1, flagParent_ set).
    private object? FindWidget()
    {
        try
        {
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(_widgetBaseType!));
            if (arr == null) return null;
            foreach (var o in arr)
            {
                if (o == null) continue;
                var w = TryCast(o, _widgetType!);
                if (w == null) continue;
                bool inited = false, active = false;
                try { inited = _piMapBgSize?.GetValue(w) is Vector2 v && v.x > 1f && v.y > 1f
                               && _piFlagParent?.GetValue(w) != null; } catch { }
                try { active = ((Component)w).gameObject.activeInHierarchy; } catch { }
                if (active && inited) return w;
            }
            return null;
        }
        catch (Exception ex) { WarnOnce("scan", $"radar scan error: {ex.Message}"); return null; }
    }

    private static MethodInfo? _tryCast;
    private static object? TryCast(object o, Type t)
    {
        try
        {
            if (_tryCast == null)
            {
                var b = o.GetType();
                while (b != null && b.Name != "Il2CppObjectBase") b = b.BaseType;
                _tryCast = b?.GetMethod("TryCast", BindingFlags.Public | BindingFlags.Instance);
            }
            return _tryCast?.MakeGenericMethod(t).Invoke(o, null);
        }
        catch { return null; }
    }

    // Name + arity hand-scan (Framework-Interop-Foundation.md: FindMethod matches by count only — same thing here).
    private static MethodInfo? FindMethod(Type t, string name, int argc)
    {
        for (Type? cur = t; cur != null; cur = cur.BaseType)
            foreach (var m in cur.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (m.Name == name && m.GetParameters().Length == argc) return m;
        return null;
    }

    private int ReadLayer()
    {
        try { return _piCurLayerId?.GetValue(Manager) is int l ? l : -1; } catch { return -1; }
    }

    // ---- readback (once per art load — never per frame) ----

    // Blit the (non-readable, possibly compressed) art into a temp sRGB RT, ReadPixels into a throwaway RGBA32
    // Texture2D, copy the bytes out, release both. ReadPixels row 0 = bottom = GetImgPos's bottom-left origin (no flip).
    private static bool Readback(Texture src, int max, Action<string> warn, out byte[] buf, out int w, out int h)
    {
        buf = Array.Empty<byte>();
        w = Math.Min(src.width, max); h = Math.Min(src.height, max);
        RenderTexture? rt = null; Texture2D? rb = null;
        var prev = RenderTexture.active;
        try
        {
            rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            rb = new Texture2D(w, h, TextureFormat.RGBA32, false);
            rb.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);   // CPU copy only — no Apply()
            var px = rb.GetPixels32();
            var bytes = MemoryMarshal.AsBytes(px.AsSpan());       // Color32 = 4 blittable bytes, RGBA order
            if (bytes.Length != w * h * 4) { warn($"readback size mismatch {bytes.Length} != {w * h * 4}"); return false; }
            buf = bytes.ToArray();
            return true;
        }
        catch (Exception ex) { warn($"map readback failed: {ex.Message}"); return false; }
        finally
        {
            RenderTexture.active = prev;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            if (rb != null) UnityEngine.Object.Destroy(rb);
        }
    }

    // ---- diagnostics (Experiment passes a sink, RaidManager null; one line per load / per "missing" reason per scene — never per frame) ----

    private void Missing(int sceneId, string why)
    {
        if (_diag == null) return;
        string k = $"{sceneId}|{why}";
        if (_missingLogged == k) return;
        _missingLogged = k;
        _diag($"[MechMapBg] no game map for scene {sceneId}: {why}{(Art != null ? " (keeping the loaded art)" : "")}");
    }

    private void LogLoad(Texture tex, RawImage? img, int layer, int sceneId)
    {
        var a = Art!;
        string uv = "?", bl = "?", size = "?", addr = "?";
        try { if (img != null) uv = img.uvRect.ToString(); } catch { }
        try
        {
            var s = Manager is { } m1 && _miRegionSize != null ? _miRegionSize.Invoke(m1, null) : null;
            var c = Manager is { } m2 && _miRegionCenter != null ? _miRegionCenter.Invoke(m2, null) : null;
            if (s is Vector2 sv) size = $"({sv.x:F1},{sv.y:F1})";
            if (s is Vector2 sz && c is Vector3 cv) bl = $"({cv.x - sz.x / 2f:F1},{cv.z - sz.y / 2f:F1})";   // BL = centre − size/2
        }
        catch { }
        try { addr = AddressString(Manager is { } m3 && _miGetAddr != null ? _miGetAddr.Invoke(m3, null) : null); } catch { }
        bool inArt = a.U0 >= 0 && a.U0 <= a.W && a.V0 >= 0 && a.V0 <= a.H;
        _diag!($"[MechMapBg] load scene={sceneId} addr='{addr}' tex={tex.name} {tex.width}x{tex.height} readable={tex.isReadable} " +
               $"read={a.W}x{a.H} layer={layer} uvRect={uv} regionBL={bl} regionSize={size} " +
               $"pxPerWorld u/x={a.UX:F3} v/z={a.VZ:F3} cross u/z={a.UZ:F3} v/x={a.VX:F3} " +
               $"arenaCentre=({a.Ox:F1},{a.Oz:F1})→px({a.U0:F1},{a.V0:F1}) inArt={inArt}");
    }

    // ZResAddress → its path (explicit string conversion first, then the backing field) — LocationMapSource helper.
    private static string AddressString(object? addr)
    {
        try
        {
            if (addr == null) return "?";
            var t = addr.GetType();
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "op_Explicit" && m.ReturnType == typeof(string) && m.GetParameters().Length == 1)
                    if (m.Invoke(null, new[] { addr }) is string s1) return s1;
            if (StellarInterop.FindPropertyUp(t, "_addressString")?.GetValue(addr) is string s2) return s2;
            return addr.ToString() ?? "?";
        }
        catch { return "?"; }
    }

    private readonly HashSet<string> _warned = new();
    private void WarnOnce(string key, string msg)
    {
        if (_warned.Add(key)) _warn($"[MechMap] {msg}");
    }
}

/// <summary>One loaded radar art buffer + its world → art-pixel affine map (u = U0 + UX·(x−Ox) + UZ·(z−Oz), v likewise;
/// bottom-left origin, row 0 = bottom). Immutable; <see cref="Version"/> is unique per load so the painter's static
/// layer cache key changes whenever the art does.</summary>
internal sealed class MinimapArt
{
    private static int _seq;
    public readonly byte[] Px;
    public readonly int W, H, SceneId, Version;
    public readonly float Ox, Oz, U0, UX, UZ, V0, VX, VZ;

    public MinimapArt(byte[] px, int w, int h, int sceneId, float ox, float oz,
                      (float U0, float UX, float UZ, float V0, float VX, float VZ) m)
    {
        Px = px; W = w; H = h; SceneId = sceneId; Ox = ox; Oz = oz; Version = ++_seq;
        (U0, UX, UZ, V0, VX, VZ) = m;
    }

    /// <summary>Bilinear sample at a world X/Z. False outside the art (→ draw nothing there). Pixel centres sit at
    /// +0.5 in GetImgPos space, so the sample point is shifted by half a texel.</summary>
    public bool Sample(float x, float z, out ColorRgba c)
    {
        c = default;
        float dx = x - Ox, dz = z - Oz;
        float u = U0 + UX * dx + UZ * dz - 0.5f, v = V0 + VX * dx + VZ * dz - 0.5f;
        if (u < -0.5f || v < -0.5f || u > W - 0.5f || v > H - 0.5f) return false;
        int u0 = Math.Clamp((int)MathF.Floor(u), 0, W - 1), v0 = Math.Clamp((int)MathF.Floor(v), 0, H - 1);
        int u1 = Math.Min(u0 + 1, W - 1), v1 = Math.Min(v0 + 1, H - 1);
        float fu = Math.Clamp(u - u0, 0f, 1f), fv = Math.Clamp(v - v0, 0f, 1f);
        int i00 = (v0 * W + u0) * 4, i10 = (v0 * W + u1) * 4, i01 = (v1 * W + u0) * 4, i11 = (v1 * W + u1) * 4;
        float w00 = (1 - fu) * (1 - fv), w10 = fu * (1 - fv), w01 = (1 - fu) * fv, w11 = fu * fv;
        float r = Px[i00] * w00 + Px[i10] * w10 + Px[i01] * w01 + Px[i11] * w11;
        float g = Px[i00 + 1] * w00 + Px[i10 + 1] * w10 + Px[i01 + 1] * w01 + Px[i11 + 1] * w11;
        float b = Px[i00 + 2] * w00 + Px[i10 + 2] * w10 + Px[i01 + 2] * w01 + Px[i11 + 2] * w11;
        float a = Px[i00 + 3] * w00 + Px[i10 + 3] * w10 + Px[i01 + 3] * w01 + Px[i11 + 3] * w11;
        if (a < 1f) return false;   // fully transparent art (outside the floor plan) = no art
        c = new ColorRgba(r / 255f, g / 255f, b / 255f, a / 255f);
        return true;
    }
}
