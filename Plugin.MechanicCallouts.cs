using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Mechanic Callouts — orchestration + the "Mechanic Callouts" sub-menu ────────────────────────────────────────
//
// Ported from StellarExperimentPlugin (user-facing parts only; its diagnostics, recorder and voice callouts stay
// there). In the supported dungeons/raids (Mechanics/MechanicCallouts.Data.cs) the tracker (Mechanics/
// MechanicCalloutTracker*.cs) reads who carries which mechanic debuff and feeds three HUD windows:
//   • the callout LIST  `● <mechanic>  <countdown>  <you>, <others>`        — Plugin.MechanicCalloutsHud.cs
//   • the MINIMAP (arena, regions, team/boss dots, party markers)           — Plugin.MechanicMinimap.cs
//   • the ON-ME banner ("<mechanic> — YOU", Phase Mapping MOVE OFF) + chime — Plugin.MechanicAlerts*.cs
// Countdowns end at the HIT via a hardcoded per-mechanic offset table (Mechanics/MechanicCalloutTracker.HitOffset.cs;
// tuned in the Experiment plugin, no user adjustment here).
//
// The sub-menu is opened from a button in the Raid Manager settings window (Plugin.Settings.cs); it has no launcher
// tile and no hotkeys. Config lives in the plugin's "settings" section, keys prefixed `mech_`. Every label goes
// through _loc.T("rm.mech.*") (Rule 10); mechanic/arena names through McText (rm.mech.n.*).
public sealed partial class Plugin
{
    private IWindowControl         _mechWindow = null!;
    private MechanicCalloutTracker _mechTracker = null!;
    private readonly List<IWindowControl> _mechWindows = new();   // every mechanic window, Remove()d on dispose

    private bool _mechEnabled;      // callout list — off (and map + alert off) ⇒ no poll at all

    // Mechanic windows render in-world only, never on a loading screen.
    private bool MechInWorld =>
        _services.ClientState.Phase == GamePhase.World && (_services.ClientState.UiState & GameUIState.Loading) == 0;

    private void InitMechanicCallouts()
    {
        McText.Loc = _loc;
        _mechEnabled  = _cfg.Get<bool>("mech_enabled", false);

        _mechTracker = new MechanicCalloutTracker(_services);
        _mechWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id: "raidmanager.mech.settings",
                Title: _loc.T("rm.mech.title"),
                DefaultRect: new WindowRect(960f, 240f, 480f, 0f),
                Category: WindowCategory.Tools,
                Style: WindowPanelStyle.GlassMenu)
            {
                Draggable = true, Closable = true, StartVisible = false,
                ShouldRender = () => MechInWorld,
            },
            Root: BuildMechCalloutRoot(),
            OnClose: () => _mechWindow.SetVisible(false)));
        _mechWindows.Add(_mechWindow);

        RegisterMechCalloutHud();   // Plugin.MechanicCalloutsHud.cs
        InitMechanicMinimap();      // Plugin.MechanicMinimap.cs (own toggles + HUD window)
        InitMechanicAlerts();       // Plugin.MechanicAlerts.cs (on-me banner + chime)

        // Persisted-on ⇒ start the poll now (it self-gates to in-world + supported scene each tick).
        ApplyMechTrackerEnabled();
    }

    private void DisposeMechanicCallouts()
    {
        DisposeMechanicAlerts();
        _mechTracker?.Dispose();
        DisposeMechanicMinimap();
        foreach (var w in _mechWindows) w.Remove();
        _mechWindows.Clear();
        McText.Loc = null;
    }

    // One tracker feeds the list, the minimap and the on-me alert: it polls while ANY of them is enabled; each window
    // gates its own rendering on its own toggle.
    private void ApplyMechTrackerEnabled()
    {
        _mechTracker.MapEnabled = _mechMapEnabled;
        _mechTracker.SetEnabled(_mechEnabled || _mechMapEnabled || _alertOn);
        SetMechMapTick(_mechMapEnabled);   // per-frame smooth-dot tick only while the map is on
    }

    private void SetMechBool(string key, bool v)
    {
        _cfg.Set<bool>(key, v);
        _cfg.Save();
    }

    // Three sections in the Mark Presets style (Emphasis header + separator between sections). Each section's master
    // toggle is indented one level under its header; sub-options sit one level deeper and are HIDDEN (not greyed)
    // while the master is off — ConditionalElement drops them and the auto-height GlassMenu shrinks to fit.
    private HudElement BuildMechCalloutRoot() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => _loc.T("rm.mech.title"), Emphasis: true),
        new TextElement(() => _loc.T("rm.mech.intro"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        new TextElement(() => _loc.T("rm.mech.help"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        new SeparatorElement(),

        // ── Callout List ──
        new TextElement(() => _loc.T("rm.mech.list.title"), Emphasis: true),
        MechIndent(MechToggleRow("rm.mech.list.enable", () => _mechEnabled, v =>
        {
            _mechEnabled = v;
            ApplyMechTrackerEnabled();
            SetMechBool("mech_enabled", v);
        })),
        new SeparatorElement(),

        // ── Minimap ──
        new TextElement(() => _loc.T("rm.mech.map.title"), Emphasis: true),
        MechIndent(
            MechToggleRow("rm.mech.map.enable", () => _mechMapEnabled, v =>
            {
                _mechMapEnabled = v;
                ApplyMechTrackerEnabled();
                SetMechBool("mech_map_enabled", v);
            }),
            new ConditionalElement(() => _mechMapEnabled, MechIndent(
                MechToggleRow("rm.mech.map.markers", () => _mechMapMarkers, v =>
                {
                    _mechMapMarkers = v;
                    ApplyMechMapOptions();
                    SetMechBool("mech_map_markers", v);
                }),
                MechToggleRow("rm.mech.map.hideNormal", () => _mechMapHideNormal, v =>
                {
                    _mechMapHideNormal = v;
                    ApplyMechMapOptions();
                    SetMechBool("mech_map_hidenormal", v);
                }),
                MechToggleRow("rm.mech.map.floor", () => _mechMapFloor, v =>
                {
                    _mechMapFloor = v;
                    ApplyMechMapOptions();
                    SetMechBool("mech_map_floor", v);
                })))),
        new SeparatorElement(),

        // ── Mechanic Alerts ──
        BuildMechAlertSection(),    // Plugin.MechanicAlerts.cs — "Mechanic Alerts" in this same menu
    }, Gap: 8f);

    // One indentation level: a fixed spacer (12 + the 6 px row gap = 18 px) before a stretching column, so nested
    // rows (incl. sliders, which need the leftover width) still fill the window.
    private static HudElement MechIndent(params HudElement[] children) => new RowElement(new HudElement[]
    {
        new SpacerElement(Width: 12f, Height: 0f),
        new CellElement(new ColumnElement(children, Gap: 8f), Weight: 1f),
    }, Gap: 6f);

    // Capsule toggle + sibling label (ToggleElement is capsule-only — same row shape as Plugin.Settings.cs).
    private HudElement MechToggleRow(string labelKey, Func<bool> get, Action<bool> set) =>
        new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", Get: get, Set: set),
            new TextElement(() => _loc.T(labelKey)),
        }, Gap: 6f);

    // Label | slider (takes the leftover width — a slider squeezed by fixed cells collapses to a pill,
    // WindowBuilder-Patterns.md) | value readout.
    private HudElement MechSliderRow(string labelKey, Func<float> get, Action<float> set, float min, float max, Func<string> value) =>
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T(labelKey), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 110f),
            new CellElement(new SliderElement(Get: get, Set: v => set(Math.Clamp(v, min, max)), Min: min, Max: max), Weight: 1f),
            new CellElement(new TextElement(value), Width: 56f),
        }, Gap: 6f);
}
