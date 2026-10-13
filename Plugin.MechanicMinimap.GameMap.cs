using System;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// Mechanic Minimap — "Show game map" (rm.mech.map.gameMap) + background opacity (rm.mech.map.bgOpacity). Port of
// Experiment c844dd4 + 40e6527.
//
// SHOW GAME MAP (mech_map_gamemap, default OFF): the game's radar floor-plan art under our arena drawing
// (MechMapArtSource → painter static layer, MechanicMinimapPainter.GameMap.cs). Polled from the texture getter, so
// only while the map is on screen, and only with a LIVE arena view (supported scene) — a test view never gets art.
// Off ⇒ no radar access at all and the art buffer is freed. No diagnostics here (Experiment logs [MechMapBg]); only
// unexpected failures Warn. The art is freed again once no live view has existed for ArtDropAfterMs (left the arena).
//
// MAP BACKGROUND OPACITY (mech_map_bgopacity, 0–100 %, 5 % steps, default 68 % = today's fill): alpha of the
// background fill AND the game-map art only (painter BgOpacity); grid / regions / dots keep theirs. Baked into the
// static layer (it is in the layer key), so a drag re-bakes that layer — cheap without art, one art pass with it.
// (Replaced the first cut's whole-picture "Minimap opacity" — user: the background is what should go opaque.)
public sealed partial class Plugin
{
    private const float MechMapBgOpacityMin = 0f, MechMapBgOpacityMax = 1f;
    private const long ArtDropAfterMs = 5000;
    private bool  _mechMapGameMap;                         // "Show game map" (default OFF)
    private float _mechMapBgOpacity = MechanicMinimapPainter.DefaultBgOpacity;   // "Map background opacity"
    private MechMapArtSource? _mechMapArt;
    private long _mechMapNoLiveSince;

    private void InitMechMapGameMap()
    {
        _mechMapGameMap = _cfg.Get<bool>("mech_map_gamemap", false);
        _mechMapBgOpacity = Math.Clamp(_cfg.Get<float>("mech_map_bgopacity", MechanicMinimapPainter.DefaultBgOpacity),
            MechMapBgOpacityMin, MechMapBgOpacityMax);
        _mechMapArt = new MechMapArtSource(m => _services.Log.Warning(m), diag: null);
    }

    private void SetMechMapGameMap(bool v)
    {
        _mechMapGameMap = v;
        SetMechBool("mech_map_gamemap", v);
        if (!v) _mechMapArt?.Drop();   // free the buffer; painter.Art is cleared by the next poll
        _mechMapPaintedVersion = int.MinValue;
    }

    // Live while dragging: the value is in the painter's layer key, so the forced repaint re-bakes the background.
    private HudElement MechMapBgOpacityRow() => MechSliderRow("rm.mech.map.bgOpacity", () => _mechMapBgOpacity, v =>
    {
        _mechMapBgOpacity = Math.Clamp(MathF.Round(v * 20f) / 20f, MechMapBgOpacityMin, MechMapBgOpacityMax);   // 5 % steps
        _cfg.Set<float>("mech_map_bgopacity", _mechMapBgOpacity); _cfg.Save();
        _mechMapPainter.BgOpacity = _mechMapBgOpacity;
        _mechMapPaintedVersion = int.MinValue;
    }, MechMapBgOpacityMin, MechMapBgOpacityMax, () => $"{_mechMapBgOpacity * 100f:0}%");

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
