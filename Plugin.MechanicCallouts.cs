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

        _mechTracker = new MechanicCalloutTracker(_services)
        {
            // "List order" (callout list only): 0 Arrival (default) / 1 Most urgent first / 2 Table order.
            ListOrder = (MechanicCalloutTracker.ListOrderMode)Math.Clamp(_cfg.Get<int>("mech_order", 0), 0, 2),
        };
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

        _mechHudScale = Math.Clamp(_cfg.Get<float>("mech_textscale", 1f), 0.75f, 2f);   // callout list "Text size"
        if (_mechEnabled) RegisterMechCalloutHud();   // Plugin.MechanicCalloutsHud.cs; off ⇒ never registered
        InitMechanicMinimap();      // Plugin.MechanicMinimap.cs (own toggles + HUD window)
        InitMechanicAlerts();       // Plugin.MechanicAlerts.cs (on-me banner + chime)

        // Persisted-on ⇒ start the poll now (it self-gates to in-world + supported scene each tick).
        ApplyMechTrackerEnabled();
    }

    private void DisposeMechanicCallouts()
    {
        DisposeMechanicAlerts();
        _mechHudRebuildTick?.Dispose(); _mechHudRebuildTick = null;
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

    // A HUD window EXISTS only while its own toggle is on; off ⇒ REMOVED, not hidden: the layout editor lists every
    // EditModeDragOnly window that isn't Removed — hidden ones too, as a dimmed "re-enable" outline (framework
    // WindowService.EditableElements) — so ShouldRender=false alone still showed a turned-off HUD there. Back on ⇒
    // re-registered with the SAME id, so its saved layout position comes back. Removed windows leave _mechWindows, so
    // dispose never removes them twice. Called from the three HUD toggles (after init only).
    private void ApplyMechHudWindows()
    {
        if (!_mechEnabled)    RemoveMechHud(ref _mechHudWindow); else if (_mechHudWindow == null) RegisterMechCalloutHud();
        if (!_mechMapEnabled) RemoveMechHud(ref _mechMapWindow); else if (_mechMapWindow == null) RegisterMechMapHud();
        if (!_alertOn)        RemoveMechHud(ref _mechAlertHud);  else if (_mechAlertHud == null)  RegisterMechAlertHud();
    }

    private void RemoveMechHud(ref IWindowControl? w)
    {
        if (w == null) return;
        _mechWindows.Remove(w);
        w.Remove();
        w = null;
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
        MechIndent(
            MechToggleRow("rm.mech.list.enable", () => _mechEnabled, v =>
            {
                _mechEnabled = v;
                ApplyMechHudWindows();
                ApplyMechTrackerEnabled();
                SetMechBool("mech_enabled", v);
            }),
            new ConditionalElement(() => _mechEnabled, MechIndent(
                new RowElement(new HudElement[]
                {
                    new TextElement(() => _loc.T("rm.mech.list.order")),
                    new DropdownElement(
                        Selected: () => (int)_mechTracker.ListOrder,
                        Options:  () => Array.ConvertAll(MechOrderKeys, _loc.T),
                        OnSelect: v =>
                        {
                            _mechTracker.ListOrder = (MechanicCalloutTracker.ListOrderMode)Math.Clamp(v, 0, 2);
                            _cfg.Set<int>("mech_order", (int)_mechTracker.ListOrder);
                            _cfg.Save();
                        },
                        Width: 220f),
                }, Gap: 6f),
                MechSliderRow("rm.mech.list.textSize", () => _mechHudScale, SetMechHudScale, 0.75f, 2f,
                    () => $"{_mechHudScale:0.00}x")))),
        new SeparatorElement(),

        // ── Minimap ──
        new TextElement(() => _loc.T("rm.mech.map.title"), Emphasis: true),
        MechIndent(
            MechToggleRow("rm.mech.map.enable", () => _mechMapEnabled, v =>
            {
                _mechMapEnabled = v;
                ApplyMechHudWindows();
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

    // "List order" options, index = MechanicCalloutTracker.ListOrderMode.
    private static readonly string[] MechOrderKeys =
        { "rm.mech.list.order.arrival", "rm.mech.list.order.urgent", "rm.mech.list.order.table" };

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
