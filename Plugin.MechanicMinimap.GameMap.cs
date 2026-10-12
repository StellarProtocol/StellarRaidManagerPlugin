using System;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// Mechanic Minimap — "Show game map" (rm.mech.map.gameMap) + "Minimap opacity" (rm.mech.map.opacity). Port of Experiment c844dd4.
//
// SHOW GAME MAP (mech_map_gamemap, default OFF): the game's radar floor-plan art under our arena drawing
// (MechMapArtSource → painter static layer, MechanicMinimapPainter.GameMap.cs). Polled from the texture getter, so
// only while the map is on screen, and only with a LIVE arena view (supported scene) — a test view never gets art.
// Off ⇒ no radar access at all and the art buffer is freed. No diagnostics here (Experiment logs [MechMapBg]); only
// unexpected failures Warn. The art is freed again once no live view has existed for ArtDropAfterMs (left the arena).
//
// MINIMAP OPACITY (mech_map_opacity, 0.2–1.0, 5 % steps, default 1.0 = today's look): alpha of the WHOLE
// minimap picture. GameTextureElement exposes no colour/alpha and the window has no CanvasGroup knob, so the raster
// scales its alpha bytes at upload (MinimapRaster.Opacity) — a repaint, not a re-bake of the static layer.
public sealed partial class Plugin
{
    private const float MechMapOpacityMin = 0.2f, MechMapOpacityMax = 1f;
    private const long ArtDropAfterMs = 5000;
    private bool  _mechMapGameMap;                         // "Show game map" (default OFF)
    private float _mechMapOpacity = 1f;                    // "Minimap opacity"
    private MechMapArtSource? _mechMapArt;
    private long _mechMapNoLiveSince;

    private void InitMechMapGameMap()
    {
        _mechMapGameMap = _cfg.Get<bool>("mech_map_gamemap", false);
        _mechMapOpacity = Math.Clamp(_cfg.Get<float>("mech_map_opacity", 1f), MechMapOpacityMin, MechMapOpacityMax);
        _mechMapArt = new MechMapArtSource(m => _services.Log.Warning(m), diag: null);
    }

    private void SetMechMapGameMap(bool v)
    {
        _mechMapGameMap = v;
        SetMechBool("mech_map_gamemap", v);
        if (!v) _mechMapArt?.Drop();   // free the buffer; painter.Art is cleared by the next poll
        _mechMapPaintedVersion = int.MinValue;
    }

    private HudElement MechMapOpacityRow() => MechSliderRow("rm.mech.map.opacity", () => _mechMapOpacity, v =>
    {
        _mechMapOpacity = Math.Clamp(MathF.Round(v * 20f) / 20f, MechMapOpacityMin, MechMapOpacityMax);   // 5 % steps
        _cfg.Set<float>("mech_map_opacity", _mechMapOpacity); _cfg.Save();
        _mechMapPainter.Opacity = _mechMapOpacity;   // live while dragging: repaint only, no re-bake
        _mechMapPaintedVersion = int.MinValue;
    }, MechMapOpacityMin, MechMapOpacityMax, () => $"{_mechMapOpacity * 100f:0}%");

    // Texture getter hook (before the paint): keep painter.Art = the art for the LIVE view (or null), forcing a repaint
    // whenever it changes so the static layer re-bakes.
    private void PollMechMapArt(MinimapView? live)
    {
        MinimapArt? want = null;
        if (_mechMapGameMap && _mechMapArt != null)
        {
            long now = Environment.TickCount64;
            if (live != null)
            {
                _mechMapNoLiveSince = 0;
                _mechMapArt.Poll(_mechTracker.SceneId, live.OriginX, live.OriginZ);
                want = _mechMapArt.Art;
            }
            else if (_mechMapNoLiveSince == 0) _mechMapNoLiveSince = now;
            else if (now - _mechMapNoLiveSince > ArtDropAfterMs) _mechMapArt.Drop();
        }
        if (!ReferenceEquals(want, _mechMapPainter.Art))
        {
            _mechMapPainter.Art = want;
            _mechMapPaintedVersion = int.MinValue;
        }
    }

    private void DisposeMechMapGameMap() { _mechMapArt?.Dispose(); _mechMapArt = null; }
}
