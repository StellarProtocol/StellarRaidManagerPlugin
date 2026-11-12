using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

public sealed partial class Plugin
{
    // HUD layout editor (Shift+`): with nothing live, the overlay shows a static sample in its real look so it can be
    // placed — "COUNTDOWN" over "10.00" at the user's Countdown size, plus (Raid warning style = Red overlay) a sample
    // warning line at the overlay size, the biggest thing this HUD can draw. A running countdown / live warning always
    // wins; the sample is gone the moment the editor closes. IsLayoutEditing is polled each refresh (~10 Hz) like the
    // other text Funcs here, so no state or tick of our own.
    private const string SampleRemaining = "10.00";
    private bool HudSample => !_running && !_rwVisible && _services.Windows.IsLayoutEditing;

    private HudElement BuildHudRoot()
    {
        const float W = 460f;

        return new ColumnElement(new HudElement[]
        {
            new ConditionalElement(
                () => _rwVisible || (HudSample && !_rwUseIngameWarning),
                new ColumnElement(new HudElement[]
                {
                    new SpacerElement(Width: W, Height: 0f),
                    new TextElement(
                        () => _rwVisible ? _rwText : _loc.T("rm.hud.sample.rw"),
                        Color: () => new ColorRgba(1f, 0.1f, 0.1f, _rwVisible ? (float)Math.Clamp(_rwTimer, 0.0, 1.0) : 1f),
                        // No Emphasis: on HudOverlay, Emphasis re-clobbers fontSize to EmphSize (~15) whenever the
                        // text changes, defeating DynamicFontSize (see WindowBuilder.Bindings TextBinding.Apply).
                        Width: W, Align: TextAlign.Center, Shadow: true, FontSize: 96, ShadowDistance: 6)
                    { DynamicFontSize = () => (int)(96f * RwScale) },
                }, Gap: 8f)),
            new ConditionalElement(
                () => _running || HudSample,
                new ColumnElement(new HudElement[]
                {
                    new SpacerElement(Width: W, Height: 0f),
                    new TextElement(
                        () => _loc.T("rm.hud.countdown"),
                        Color: () => (ColorRgba?)_services.Theme.Colors.HudText,
                        Width: W, Align: TextAlign.Center, Shadow: true, FontSize: 75, ShadowDistance: 4)
                    { DynamicFontSize = () => (int)(75f * Scale) },
                    new TextElement(
                        () => _running ? FormatRemaining() : SampleRemaining,
                        Color: () => _running && _remaining <= 5d
                            ? new ColorRgba(1f, 0.15f, 0.15f, 1f)
                            : (ColorRgba?)_services.Theme.Colors.HudAccent,
                        // No Emphasis (see note above): keeps DynamicFontSize=160 instead of being clobbered to ~15.
                        Width: W, Align: TextAlign.Center, Shadow: true, FontSize: 160, ShadowDistance: 8)
                    { DynamicFontSize = () => (int)(160f * Scale) },
                }, Gap: 10f)),
        }, Gap: 0f);
    }
}
