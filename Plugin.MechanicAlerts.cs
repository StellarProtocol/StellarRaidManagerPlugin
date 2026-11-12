using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// "Mechanic Alerts" — reacts to the tracker's new-occurrence event (Mechanics/MechanicCalloutTracker.Occurrence.cs).
// "On me": when the LOCAL player is among a new occurrence's targets (or joins an existing row's targets), a big
// banner "<MECHANIC> — YOU" in the mechanic colour with a countdown appears on a HUD window (up to 3 stacked, newest
// on top) and stays until the row ends (untimed mechanics: 5 s); optionally the built-in chime plays through
// MechAudioPlayer (local speakers, winmm). Raid danger tiles (Phase Mapping, Edge-Mid / Corner Explosion) get a
// "MOVE OFF" banner instead (Plugin.MechanicAlerts.Banner.cs). Settings are the ON-ME ALERT section of the Mechanic
// Callouts window (Plugin.MechanicCallouts.Ui.cs), whose header "Test alert" works even with the alert off: the banner
// HUD is registered for the test and removed again once it ends (TestOnMeAlert / EndAlertTest).
// Config keys: mech_alert_on / _sound / _volume / _scale. (Experiment's custom sound file, untimed-duration slider
// and voice callouts are deliberately not ported.)
// The tracker must be polling for occurrences to fire — it runs while the list OR the minimap OR alerts are enabled.
public sealed partial class Plugin
{
    private IWindowControl? _mechAlertHud;          // null while removed (alert toggle off)
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
        if (_alertOn) RegisterMechAlertHud();   // Plugin.MechanicAlerts.Banner.cs; off ⇒ never registered
    }

    private void DisposeMechanicAlerts()
    {
        if (_mechTracker != null) _mechTracker.Occurrence -= OnMechOccurrence;
        _alertTestTick?.Dispose(); _alertTestTick = null;
        _mechAudio?.Dispose();
    }

    // "Test alert" with the alert OFF: the banner HUD only exists while the alert is on (ApplyMechHudWindows), so the test
    // registers it for the test's 8 s (ShouldRender lets it draw while _alertTestUntil is ahead) and a 1 Hz check removes
    // it again afterwards — unless the alert was switched on meanwhile, which keeps it.
    private const long AlertTestMs = 8000;
    private long _alertTestUntil;
    private IDisposable? _alertTestTick;

    private bool AlertTestRunning => Environment.TickCount64 < _alertTestUntil;

    private void EndAlertTest()
    {
        if (AlertTestRunning) return;
        _alertTestTick?.Dispose(); _alertTestTick = null;
        if (!_alertOn) RemoveMechHud(ref _mechAlertHud);
    }

    private void OnMechOccurrence(McOccurrence o)
    {
        // Danger tiles (Phase Mapping, Edge-Mid / Corner Explosion) mark the floor, never a player — no on-me alert
        // (the MOVE OFF banner covers them instead). Causal Jump Ricochet 829316 lands as the ball bounces — too
        // late; the pinball round alert (lead ball row, flagged local by the tracker) already warned when the balls appeared.
        if (_alertOn && o.IsLocalTarget && !MechanicCalloutTracker.IsDangerTileKey(o.Key)
            && !MechanicCalloutTracker.IsPinballRicochetKey(o.Key)) ShowOnMeAlert(o, testMs: 0);
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
        _alertTestUntil = Environment.TickCount64 + AlertTestMs;
        if (!_alertOn && _mechAlertHud == null)
        {
            RegisterMechAlertHud();   // temporary — EndAlertTest removes it once the test banner is done
            _alertTestTick ??= _services.Framework.Every(TimeSpan.FromSeconds(1), EndAlertTest);
        }
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
}
