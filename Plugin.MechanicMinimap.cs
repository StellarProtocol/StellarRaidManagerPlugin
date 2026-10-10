using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// "Mechanic Minimap" — port of the resonance-logs-cn minimap overlay for the scenes that have a view builder (see
// MechanicCalloutTracker.Minimap*.cs). The tracker builds a MinimapView each scan; this window shows
// it through a GameTextureElement fed by MechanicMinimapPainter's plugin-owned Texture2D (CPU raster, uploaded
// in place — the framework rebinds a texture only when the REFERENCE changes, and ours never does, so an in-place
// upload is all a redraw needs). The paint happens lazily inside the texture getter, which the framework only polls
// while the window is active, and only when the tracker's MapVersion moved (≈ 5 Hz in the raid) — idle otherwise.
// Window style mirrors the callout HUD: HUD category, Borderless, EditModeDragOnly, Passive (pure info, no clicks).
// No BringToFront (CLAUDE.md rule 5). Config keys: mech_map_enabled / _markers / _hidenormal / _floor / _scale.
//
// MINIMAP SIZE (slider 0.5–2.0×, step 0.1, config mech_map_scale, default 1.0×; port of Experiment dbcc76f): the
// texture AND the on-screen size are round(300 × scale) px — still 1:1 texels, so a bigger map is re-RENDERED at the
// new size (crisp), never a stretched 300-px texture. The painter scales every drawn px size with the canvas
// (Mechanics/MechanicMinimapPainter.cs). The GameTextureElement size and the window rect are baked at registration, so
// a change re-creates the painter (old Texture2D destroyed) and RE-REGISTERS the window under the same id (saved
// position kept) once the slider has been still for 300 ms — like the callout list's Text size. A map whose toggle
// is off stays removed.
public sealed partial class Plugin
{
    private const int MechMapBasePx = 300;                 // canvas at 1.0× (MinimapProjector.BasePx)
    private const float MechMapScaleMin = 0.5f, MechMapScaleMax = 2f;
    private float _mechMapScale = 1f;                      // live "Minimap size"
    private int   _mechMapBuiltPx = MechMapBasePx;         // px the current painter (and the window) were built at
    private long  _mechMapRebuildAt;                       // debounce deadline for the re-create
    private IDisposable? _mechMapRebuildTick;
    private int MechMapPx => (int)MathF.Round(MechMapBasePx * _mechMapScale);   // texture AND on-screen size (1:1 → crisp)
    private IWindowControl?        _mechMapWindow;       // null while removed (map toggle off)
    private MechanicMinimapPainter _mechMapPainter = null!;
    private bool _mechMapEnabled;
    private bool _mechMapMarkers = true;                   // "Show party markers" (default ON)
    private bool _mechMapFloor = true;                     // "Show floor damage" (raid grid, default ON)
    private bool _mechMapHideNormal;                       // "Hide teammates without a mechanic" (default OFF)
    private int  _mechMapPaintedVersion = -1;              // tracker MapVersion last painted; -2 = test view painted

    private void InitMechanicMinimap()
    {
        _mechMapEnabled = _cfg.Get<bool>("mech_map_enabled", false);
        _mechMapMarkers = _cfg.Get<bool>("mech_map_markers", true);
        _mechMapHideNormal = _cfg.Get<bool>("mech_map_hidenormal", false);
        _mechMapFloor = _cfg.Get<bool>("mech_map_floor", true);
        _mechMapScale = Math.Clamp(_cfg.Get<float>("mech_map_scale", 1f), MechMapScaleMin, MechMapScaleMax);
        CreateMechMapPainter();
        if (_mechMapEnabled) RegisterMechMapHud();   // off ⇒ never registered (not even in the layout editor)
    }

    // (Re)create the painter at the current "Minimap size" — disposing the old one destroys its Texture2D, so the
    // caller removes a window still bound to it first.
    private void CreateMechMapPainter()
    {
        _mechMapPainter?.Dispose();
        _mechMapBuiltPx = MechMapPx;
        _mechMapPainter = new MechanicMinimapPainter(_mechMapBuiltPx);
        ApplyMechMapOptions();                  // carries the display options over + forces a repaint
    }

    private void SetMechMapScale(float v)
    {
        _mechMapScale = Math.Clamp(MathF.Round(v * 10f) / 10f, MechMapScaleMin, MechMapScaleMax);   // 0.1 steps
        _cfg.Set<float>("mech_map_scale", _mechMapScale);
        _cfg.Save();
        _mechMapRebuildAt = Environment.TickCount64 + 300;
        _mechMapRebuildTick ??= _services.Framework.Every(TimeSpan.FromMilliseconds(100), MechMapRebuildTick);
    }

    private void MechMapRebuildTick()
    {
        if (Environment.TickCount64 < _mechMapRebuildAt) return;
        _mechMapRebuildTick?.Dispose(); _mechMapRebuildTick = null;
        if (MechMapPx == _mechMapBuiltPx) return;
        // Map toggled off inside the debounce ⇒ the window is already removed: only the painter is re-made; don't
        // resurrect the window (it is registered at the new size when the toggle comes back on).
        bool had = _mechMapWindow != null;
        RemoveMechHud(ref _mechMapWindow);   // before the painter dispose: the window still shows the old texture
        CreateMechMapPainter();
        if (had) RegisterMechMapHud();       // same id → saved position restored
    }

    private void RegisterMechMapHud()
    {
        _mechMapWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "raidmanager.mech.map",
                Title:       _loc.T("rm.mech.hud.map"),
                // Constant 1440p-calibrated rect, to the right of the callout list's default spot.
                DefaultRect: new WindowRect(540f, 420f, _mechMapBuiltPx + 16f, _mechMapBuiltPx + 16f),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false, Passive = true,
                ShouldRender = () => _mechMapEnabled && MechInWorld
                                  && (_services.ClientState.UiState & GameUIState.Blocking) == 0
                                  && (_services.Windows.IsLayoutEditing || _mechTracker.Map != null),
            },
            Root: new ColumnElement(new HudElement[]
            {
                new GameTextureElement(MechMapTexture, _mechMapBuiltPx, _mechMapBuiltPx) { Fill = true },
            }) { Padding = 8 },
            OnClose: () => { }));
        _mechWindows.Add(_mechMapWindow);  // DisposeMechanicCallouts Remove()s each
        _mechMapWindow.SetVisible(true);   // always "shown"; ShouldRender does the real gating
    }

    // Display options → tracker (what to collect) + painter (what to draw); repaint so the test view follows too.
    private void ApplyMechMapOptions()
    {
        _mechTracker.ShowMarkers = _mechMapMarkers;
        _mechMapPainter.ShowMarkers = _mechMapMarkers;
        _mechMapPainter.HideNormalTeammates = _mechMapHideNormal;
        _mechTracker.ShowFloor = _mechMapFloor;
        _mechMapPaintedVersion = int.MinValue;
    }

    private void DisposeMechanicMinimap()
    {
        SetMechMapTick(false);
        _mechMapRebuildTick?.Dispose(); _mechMapRebuildTick = null;
        _mechMapPainter?.Dispose();
    }

    // Texture getter (polled by the framework only while the window is active): marks the map as on-screen for the
    // per-frame tick, paints the first live frame / new scan immediately, or the test view when there's no live view.
    private object? MechMapTexture()
    {
        _mechMapShownAt = Environment.TickCount64;
        try
        {
            var live = _mechTracker.Map;
            if (live != null)
            {
                if (_mechTracker.MapVersion != _mechMapPaintedVersion) PaintMechMap(live, _mechTracker.MapVersion);
            }
            else if (_mechMapPaintedVersion != -2) PaintMechMap(MechMapTestView(), -2);
        }
        catch (Exception ex) { LogMapErrOnce(ex); }
        return _mechMapPainter.Texture;
    }

    // ── Smooth dots: per-frame tick ──────────────────────────────────────────────────────────────────────────
    // IFramework.Update (per frame; inside the framework's IsWorldActive gate — fine, the map is world-only,
    // Game-Phases-and-Tick-Gating.md) throttled to ~30 Hz. Subscribed only while the map toggle is on, and does
    // nothing unless the texture getter ran within the last 250 ms (= the window is actually on screen) and a live
    // view exists. Re-reads only the current dots' positions (tracker fast path) and repaints when one moved
    // > 0.05 world units or a scan produced a new view.
    private const long MechMapFrameMs = 33;
    private bool _mechMapTickOn;
    private long _mechMapShownAt = -10_000, _mechMapLastTick;

    private void SetMechMapTick(bool on)
    {
        if (on == _mechMapTickOn) return;
        _mechMapTickOn = on;
        if (on) _services.Framework.Update += OnMechMapFrame;
        else    _services.Framework.Update -= OnMechMapFrame;
    }

    private void OnMechMapFrame(float dt)
    {
        long now = Environment.TickCount64;
        if (now - _mechMapShownAt > 250 || now - _mechMapLastTick < MechMapFrameMs) return;
        _mechMapLastTick = now;
        try
        {
            var live = _mechTracker.Map;
            if (live == null) return;
            bool moved = _mechTracker.RefreshDotPositions();
            if (moved || _mechTracker.MapVersion != _mechMapPaintedVersion) PaintMechMap(live, _mechTracker.MapVersion);
        }
        catch (Exception ex) { LogMapErrOnce(ex); }
    }

    private void PaintMechMap(MinimapView v, int version)
    {
        _mechMapPainter.Paint(v);
        _mechMapPaintedVersion = version;
    }

    private bool _mechMapErrLogged;
    private void LogMapErrOnce(Exception ex)
    {
        if (_mechMapErrLogged) return;
        _mechMapErrLogged = true;
        _services.Log.Warning($"[MechMap] paint failed: {ex.InnerException?.Message ?? ex.Message}");
    }

    // Layout-edit preview (no live view): always the raid grid-arena sample.
    private static MinimapView MechMapTestView() => MinimapTestViews.Raid();
}
