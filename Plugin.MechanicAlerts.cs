using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// "Mechanic Alerts" — reacts to the tracker's new-occurrence event (Mechanics/MechanicCalloutTracker.Occurrence.cs).
// "On me": when the LOCAL player is among a new occurrence's targets (or joins an existing row's targets), a big
// banner "<MECHANIC> — YOU" in the mechanic colour with a countdown appears on a HUD window (up to 3 stacked, newest
// on top) and stays until the row ends (untimed mechanics: 5 s); optionally the built-in chime plays through
// MechAudioPlayer (local speakers, winmm). Phase Mapping danger tiles get a "MOVE OFF" banner instead
// (Plugin.MechanicAlerts.Banner.cs). Settings are the "Mechanic Alerts" section of the Mechanic Callouts window.
// Config keys: mech_alert_on / _sound / _volume / _scale. (Experiment's custom sound file, untimed-duration slider
// and voice callouts are deliberately not ported.)
// The tracker must be polling for occurrences to fire — it runs while the list OR the minimap OR alerts are enabled.
public sealed partial class Plugin
{
    private IWindowControl  _mechAlertHud = null!;
    private MechAudioPlayer? _mechAudio;           // created on the first chime (its thread costs nothing until then)

    private bool  _alertOn, _alertSound = true;     // on-me alert default OFF (opt-in, like the list and minimap)
    private float _alertVolume = 0.8f;
    private const long AlertUntimedMs = 5000;      // banner time for mechanics without a countdown (fixed)

    private const int AlertSlots = 3;

    private void InitMechanicAlerts()
    {
        _alertOn     = _cfg.Get<bool>("mech_alert_on", false);
        _alertSound  = _cfg.Get<bool>("mech_alert_sound", true);
        _alertVolume = Math.Clamp(_cfg.Get<float>("mech_alert_volume", 0.8f), 0f, 1f);
        _alertScale  = Math.Clamp(_cfg.Get<float>("mech_alert_scale", 1.5f), 1f, 6f);

        _mechTracker.Occurrence += OnMechOccurrence;
        RegisterMechAlertHud();   // Plugin.MechanicAlerts.Banner.cs
    }

    private void DisposeMechanicAlerts()
    {
        if (_mechTracker != null) _mechTracker.Occurrence -= OnMechOccurrence;
        _mechAudio?.Dispose();
    }

    private void OnMechOccurrence(McOccurrence o)
    {
        // Phase Mapping marks floor tiles, never a player — no on-me alert (the MOVE OFF banner covers it instead).
        if (_alertOn && o.IsLocalTarget && o.Key != MechanicCalloutTracker.PhaseDangerKey) ShowOnMeAlert(o, testMs: 0);
    }

    private void ShowOnMeAlert(McOccurrence o, long testMs)
    {
        PruneBanners();
        _banners.Insert(0, MakeBanner(o, testMs));
        if (_banners.Count > AlertSlots) _banners.RemoveRange(AlertSlots, _banners.Count - AlertSlots);
        if (_alertSound) PlayAlertSound();
    }

    private void PlayAlertSound() => (_mechAudio ??= new MechAudioPlayer()).PlayChime(_alertVolume);

    // Banner liveness: test by time; timed while its row is still active (same row object, timer > 0); untimed for
    // AlertUntimedMs (or until the row ends, if sooner).
    private bool BannerLive(Banner b) =>
        b.TestUntil > 0 ? Environment.TickCount64 < b.TestUntil
        : _mechTracker.IsRowActive(b.Occ) && (b.UntimedUntil == 0 || Environment.TickCount64 < b.UntimedUntil);

    private void PruneBanners() => _banners.RemoveAll(b => !BannerLive(b));

    private void TestOnMeAlert()
    {
        string label = McText.T("Execution Sentence") + " x2";
        var row = new McRow
        {
            Key = "test", Label = label, Color = 3, DurationMs = 8000,
            SnapTick = Environment.TickCount64, SnapRemain = 8f, LocalName = _loc.T("rm.mech.test.you"),
        };
        ShowOnMeAlert(new McOccurrence { Key = "test", Group = "Execution Sentence", Label = label, Color = 3, DurationMs = 8000,
                                         Row = row, IsLocalTarget = true },
                      testMs: 8000);
    }

    // "Mechanic Alerts" section of the Mechanic Callouts window (Plugin.MechanicCallouts.cs).
    private HudElement BuildMechAlertSection() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => _loc.T("rm.mech.alert.title"), Emphasis: true),
        new TextElement(() => _loc.T("rm.mech.alert.help"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        MechIndent(
            MechToggleRow("rm.mech.alert.enable", () => _alertOn, v =>
            {
                _alertOn = v; SetMechBool("mech_alert_on", v); ApplyMechTrackerEnabled();
            }),
            // Sound / volume / size / test only while the on-me alert is on (hidden otherwise).
            new ConditionalElement(() => _alertOn, MechIndent(
                MechToggleRow("rm.mech.alert.sound", () => _alertSound, v => { _alertSound = v; SetMechBool("mech_alert_sound", v); }),
                MechSliderRow("rm.mech.alert.volume", () => _alertVolume, v =>
                {
                    _alertVolume = v;
                    _cfg.Set<float>("mech_alert_volume", _alertVolume); _cfg.Save();
                }, 0f, 1f, () => $"{_alertVolume * 100f:0}%"),
                MechSliderRow("rm.mech.alert.size", () => _alertScale, v =>
                {
                    _alertScale = Math.Clamp(MathF.Round(v * 2f) / 2f, 1f, 6f);   // 0.5 steps
                    _cfg.Set<float>("mech_alert_scale", _alertScale); _cfg.Save();
                }, 1f, 6f, () => $"{_alertScale:0.0}x"),
                new RowElement(new HudElement[]
                {
                    new ButtonElement(() => _loc.T("rm.mech.alert.test"), OnClick: TestOnMeAlert),
                }, Gap: 8f)))),
    }, Gap: 8f);
}
