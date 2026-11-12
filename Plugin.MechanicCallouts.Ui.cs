using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Mechanic Callouts — the settings window (house redesign, approved 2026-11-12) ──────────────────────────────────
//
// 800 wide, two balanced, top-aligned columns (rule 17), CONSTANT height: left = CALLOUT LIST + ON-ME ALERT, right =
// MINIMAP. Header = ToolHeader (status "List on · Minimap on · Alert off", the SAME builder as the hub card) + a
// Neutral "Test alert" pill (reachable even with the alert off) + a "?" holding the "move them in the HUD layout
// editor" note. Toggles are 64×24
// ON/OFF chips, sliders round-knob, dropdowns Pill. A section's sub-options are RECESSED — shown, dimmed, not clickable —
// while its main switch is off (they used to disappear and the window jumped). Every row ends in "?" (help popover).
// Settings, config keys (mech_*), defaults and behaviour are unchanged; the setters are the ones the old rows used.
public sealed partial class Plugin
{
    // Label columns = the longest label in ANY locale (R10; 190 cut "Only teammates with a me…" in English). Every ON/OFF
    // chip in a column lines up: main switches and indented chip sub-options both put the chip at x = 244 (14 px indent
    // + 230). Sliders / dropdowns keep their own narrower column (112) so the slider still gets its room.
    //   chip sub-options: "Only teammates with a mechanic" (en, ≈222 px bold-13), "Kakampi lang na may mekaniko" (fil)
    //   main switches:    "Alerto kapag ikaw ang tinamaan" (fil, ≈219 px)
    //   sliders/dropdown: "Lakas ng tunog" / "Ukuran banner" (≈105 px)
    // Width 800 (was 760, R18): the 364 px column interior holds a dropdown row exactly (8 + 112 + 196 DropdownW + 24 "?"
    // + 4 × 6 gaps); a chip row is 8 + 230 + 64 + 24 + 4 × 6 = 350.
    private const float MechWidth   = 800f;
    private const float MechColGap  = 8f;
    private const float MechColW    = (MechWidth - 24f - MechColGap) / 2f;   // 384: window pad 2×12, interior 364
    private const float MechSubLabelW  = 112f;                               // sliders / dropdowns (after the 14 px indent)
    private const float MechChipLabelW = 230f;                               // ON/OFF sub-options (after the indent)
    private const float MechMainLabelW = MechChipLabelW + RowIndent + RowGap; // a section's main switch: 244, chips aligned
    private const float MechTestAlertW = 140f;   // "Subukan ang alerto" (fil) ≈136 px bold-13

    private const string ListSection  = "rm.mech.list.title";
    private const string AlertSection = "rm.mech.alert.title";
    private const string MapSection   = "rm.mech.map.title";

    private HelpTip MechTip(string titleKey, string section, string bodyKey)
        => new(titleKey, section, HelpText(bodyKey), () => _mechWindow);

    private HudElement BuildMechCalloutRoot()
    {
        Func<bool> listOn = () => _mechEnabled, alertOn = () => _alertOn, mapOn = () => _mechMapEnabled;
        var orderOptions = LocOptions(MechOrderKeys);
        var rotationOptions = LocOptions(MechMapRotKeys);

        return new ColumnElement(new HudElement[]
        {
            ToolHeader("callouts", () => _loc.T("rm.mech.title"), MechStatus, () => _mechWindow.SetVisible(false),
                trailing: new RowElement(new HudElement[]
                {
                    NeutralButton(() => _loc.T("rm.mech.alert.test"), TestOnMeAlert, MechTestAlertW),
                    // "Where do the overlays go?" — the HUD layout editor note (moved out of the status line).
                    HelpDot(MechTip("rm.mech.help.layoutTitle", "rm.mech.title", "rm.mech.help.layout")),
                }, Gap: RowGap)),
            new RowElement(new HudElement[]
            {
                TopColumn(MechColW, 8f,
                    SectionPanel(() => _loc.T(ListSection), () => _loc.T("rm.mech.list.hint"),
                        SettingRow("rm.mech.list.enable", MechMainLabelW, MechTip("rm.mech.list.enable", ListSection, "rm.mech.help.list"),
                            null, false, OnOffChip(listOn, SetMechListOn)),
                        SettingRow("rm.mech.list.order", MechSubLabelW, MechTip("rm.mech.list.order", ListSection, "rm.mech.help.order"),
                            listOn, true, PillDropdown(() => (int)_mechTracker.ListOrder, orderOptions, SetMechListOrder, DropdownW, listOn)),
                        SliderRow("rm.mech.list.textSize", MechSubLabelW, MechTip("rm.mech.list.textSize", ListSection, "rm.mech.help.textSize"),
                            () => _mechHudScale, SetMechHudScale, 0.75f, 2f, ValueText(() => _mechHudScale, v => $"{v:0.00}x"), listOn),
                        SliderRow("rm.mech.list.bgOpacity", MechSubLabelW, MechTip("rm.mech.list.bgOpacity", ListSection, "rm.mech.help.listBg"),
                            () => _mechBgOpacity, SetMechBgOpacity, 0f, 1f, ValueText(() => _mechBgOpacity, v => $"{v * 100f:0}%"), listOn)),
                    SectionPanel(() => _loc.T(AlertSection), () => _loc.T("rm.mech.alert.hint"),
                        SettingRow("rm.mech.alert.enable", MechMainLabelW, MechTip("rm.mech.alert.enable", AlertSection, "rm.mech.alert.help"),
                            null, false, OnOffChip(alertOn, SetMechAlertOn)),
                        SettingRow("rm.mech.alert.sound", MechChipLabelW, MechTip("rm.mech.alert.sound", AlertSection, "rm.mech.help.sound"),
                            alertOn, true, OnOffChip(() => _alertSound, v => { _alertSound = v; SetMechBool("mech_alert_sound", v); }, alertOn)),
                        SliderRow("rm.mech.alert.volume", MechSubLabelW, MechTip("rm.mech.alert.volume", AlertSection, "rm.mech.help.volume"),
                            () => _alertVolume, SetAlertVolume, 0f, 1f, ValueText(() => _alertVolume, v => $"{v * 100f:0}%"), alertOn),
                        SliderRow("rm.mech.alert.size", MechSubLabelW, MechTip("rm.mech.alert.size", AlertSection, "rm.mech.help.bannerSize"),
                            () => _alertScale, SetAlertScale, 1f, 6f, ValueText(() => _alertScale, v => $"{v:0.0}x"), alertOn))),
                TopColumn(MechColW, 8f,
                    SectionPanel(() => _loc.T(MapSection), () => _loc.T("rm.mech.map.hint"),
                        SettingRow("rm.mech.map.enable", MechMainLabelW, MechTip("rm.mech.map.enable", MapSection, "rm.mech.help.map"),
                            null, false, OnOffChip(mapOn, SetMechMapOn)),
                        SliderRow("rm.mech.map.size", MechSubLabelW, MechTip("rm.mech.map.size", MapSection, "rm.mech.help.mapSize"),
                            () => _mechMapScale, SetMechMapScale, MechMapScaleMin, MechMapScaleMax,
                            ValueText(() => _mechMapScale, v => $"{v:0.0}x"), mapOn),   // Plugin.MechanicMinimap.cs
                        SliderRow("rm.mech.map.bgOpacity", MechSubLabelW, MechTip("rm.mech.map.bgOpacity", MapSection, "rm.mech.help.mapBg"),
                            () => _mechMapBgOpacity, SetMechMapBgOpacity, MechMapBgOpacityMin, MechMapBgOpacityMax,
                            ValueText(() => _mechMapBgOpacity, v => $"{v * 100f:0}%"), mapOn),   // .GameMap.cs
                        SettingRow("rm.mech.map.rotation", MechSubLabelW, MechTip("rm.mech.map.rotation", MapSection, "rm.mech.help.rotation"),
                            mapOn, true, PillDropdown(() => _mechMapRotation, rotationOptions, SetMechMapRotation, DropdownW, mapOn)),
                        MapChipRow("rm.mech.map.markers", "rm.mech.help.markers", () => _mechMapMarkers, v =>
                        {
                            _mechMapMarkers = v; ApplyMechMapOptions(); SetMechBool("mech_map_markers", v);
                        }),
                        MapChipRow("rm.mech.map.hideNormal", "rm.mech.help.hideNormal", () => _mechMapHideNormal, v =>
                        {
                            _mechMapHideNormal = v; ApplyMechMapOptions(); SetMechBool("mech_map_hidenormal", v);
                        }),
                        MapChipRow("rm.mech.map.camCone", "rm.mech.help.camCone", () => _mechMapCamCone, v =>
                        {
                            _mechMapCamCone = v; ApplyMechMapOptions(); SetMechBool("mech_map_camcone", v);
                        }),
                        MapChipRow("rm.mech.map.gameMap", "rm.mech.help.gameMap", () => _mechMapGameMap, SetMechMapGameMap),
                        MapChipRow("rm.mech.map.floor", "rm.mech.help.floor", () => _mechMapFloor, v =>
                        {
                            _mechMapFloor = v; ApplyMechMapOptions(); SetMechBool("mech_map_floor", v);
                        }))),
            }, Gap: MechColGap),
        }, Gap: 10f);
    }

    // A minimap on/off sub-option: [indent][label 230][chip][flex][?], recessed while the minimap is off.
    private HudElement MapChipRow(string labelKey, string helpKey, Func<bool> get, Action<bool> set)
    {
        Func<bool> mapOn = () => _mechMapEnabled;
        return SettingRow(labelKey, MechChipLabelW, MechTip(labelKey, MapSection, helpKey), mapOn, true,
            OnOffChip(get, set, mapOn));
    }

    // A slider readout re-formatted only when the value changes (polled every refresh; dragging changes it per frame).
    private static Func<string> ValueText(Func<float> get, Func<float, string> format)
    {
        float last = float.NaN;
        string text = string.Empty;
        return () =>
        {
            float v = get();
            if (v != last) { last = v; text = format(v); }
            return text;
        };
    }

    // ── setters (the same work the old rows did) ──────────────────────────────────────────────────────────────────
    private void SetMechListOn(bool v)
    {
        _mechEnabled = v;
        ApplyMechHudWindows();
        ApplyMechTrackerEnabled();
        SetMechBool("mech_enabled", v);
    }

    private void SetMechMapOn(bool v)
    {
        _mechMapEnabled = v;
        ApplyMechHudWindows();
        ApplyMechTrackerEnabled();
        SetMechBool("mech_map_enabled", v);
    }

    private void SetMechAlertOn(bool v)
    {
        _alertOn = v;
        SetMechBool("mech_alert_on", v);
        ApplyMechHudWindows();
        ApplyMechTrackerEnabled();
    }

    private void SetMechListOrder(int v)
    {
        _mechTracker.ListOrder = (MechanicCalloutTracker.ListOrderMode)Math.Clamp(v, 0, 2);
        _cfg.Set<int>("mech_order", (int)_mechTracker.ListOrder);
        _cfg.Save();
    }

    private void SetAlertVolume(float v)
    {
        _alertVolume = v;
        _cfg.Set<float>("mech_alert_volume", _alertVolume); _cfg.Save();
    }

    private void SetAlertScale(float v)
    {
        _alertScale = Math.Clamp(MathF.Round(v * 2f) / 2f, 1f, 6f);   // 0.5 steps
        _cfg.Set<float>("mech_alert_scale", _alertScale); _cfg.Save();
    }

    // ── status (window header AND hub card) ───────────────────────────────────────────────────────────────────────
    // "List on · Minimap on · Alert off", re-made only when a switch or the language changes. The header used to append
    // " — move them in the HUD layout editor", but that ellipsized in fil / id (in-game 2026-11-12); the sentence now
    // lives in the header "?" next to Test alert, so header and hub card show the same string.
    private int _mechStatusKey = -1;
    private string? _mechStatusLang;
    private string _mechStatus = string.Empty;

    private void RefreshMechStatus()
    {
        int key = (_mechEnabled ? 1 : 0) | (_mechMapEnabled ? 2 : 0) | (_alertOn ? 4 : 0);
        var lang = _loc.Language;
        if (key == _mechStatusKey && string.Equals(lang, _mechStatusLang, StringComparison.Ordinal)) return;
        _mechStatusKey = key; _mechStatusLang = lang;
        _mechStatus = _loc.T(_mechEnabled ? "rm.mech.status.listOn" : "rm.mech.status.listOff")
            + " · " + _loc.T(_mechMapEnabled ? "rm.mech.status.mapOn" : "rm.mech.status.mapOff")
            + " · " + _loc.T(_alertOn ? "rm.mech.status.alertOn" : "rm.mech.status.alertOff");
    }

    private string MechStatus() { RefreshMechStatus(); return _mechStatus; }
}
