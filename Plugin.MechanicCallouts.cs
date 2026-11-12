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
//   • the ON-ME banner ("<mechanic> — YOU", danger-tile MOVE OFF) + chime — Plugin.MechanicAlerts*.cs
// Countdowns end at the HIT via a hardcoded per-mechanic offset table (Mechanics/MechanicCalloutTracker.HitOffset.cs;
// tuned in the Experiment plugin, no user adjustment here).
//
// The settings window (Plugin.MechanicCallouts.Ui.cs) is opened from the hub's "Mechanic Callouts" card (Plugin.Hub.cs);
// it has no launcher tile and no hotkeys. Config lives in the plugin's "settings" section, keys prefixed `mech_`. Every label goes
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
            // House-style tool window (Plugin.cs ToolSpec), prebuilt: ~20 setting rows, two columns.
            Spec: ToolSpec("raidmanager.mech.settings", "rm.mech.title", new WindowRect(760f, 240f, MechWidth, 0f),
                prebuild: true),
            Root: BuildMechCalloutRoot(),   // Plugin.MechanicCallouts.Ui.cs
            OnClose: () => _mechWindow.SetVisible(false)));
        _mechWindows.Add(_mechWindow);

        _mechHudScale = Math.Clamp(_cfg.Get<float>("mech_textscale", 1f), 0.75f, 2f);   // callout list "Text size"
        _mechBgOpacity = Math.Clamp(_cfg.Get<float>("mech_bg_opacity", 0f), 0f, 1f);    // callout list "Background opacity"
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

    // "List order" options, index = MechanicCalloutTracker.ListOrderMode.
    private static readonly string[] MechOrderKeys =
        { "rm.mech.list.order.arrival", "rm.mech.list.order.urgent", "rm.mech.list.order.table" };
}
