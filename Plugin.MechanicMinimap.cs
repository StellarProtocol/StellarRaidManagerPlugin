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
// No BringToFront (CLAUDE.md rule 5). Config keys: mech_map_enabled / _markers / _hidenormal / _floor.
public sealed partial class Plugin
{
    private const int MechMapPx = 300;                     // texture AND on-screen size (1:1 texels → crisp)
    private IWindowControl         _mechMapWindow = null!;
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
        _mechMapPainter = new MechanicMinimapPainter(MechMapPx);
        ApplyMechMapOptions();

        _mechMapWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "raidmanager.mech.map",
                Title:       _loc.T("rm.mech.hud.map"),
                // Constant 1440p-calibrated rect, to the right of the callout list's default spot.
                DefaultRect: new WindowRect(540f, 420f, MechMapPx + 16f, MechMapPx + 16f),
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
                new GameTextureElement(MechMapTexture, MechMapPx, MechMapPx) { Fill = true },
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
