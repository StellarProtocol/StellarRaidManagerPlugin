using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

public sealed partial class Plugin
{
    private HudElement BuildHudRoot()
    {
        const float W = 460f;

        return new ColumnElement(new HudElement[]
        {
            new ConditionalElement(
                () => _rwVisible,
                new ColumnElement(new HudElement[]
                {
                    new SpacerElement(Width: W, Height: 0f),
                    new TextElement(
                        () => _rwText,
                        Color: () => new ColorRgba(1f, 0.1f, 0.1f, (float)Math.Clamp(_rwTimer, 0.0, 1.0)),
                        // No Emphasis: on HudOverlay, Emphasis re-clobbers fontSize to EmphSize (~15) whenever the
                        // text changes, defeating DynamicFontSize (see WindowBuilder.Bindings TextBinding.Apply).
                        Width: W, Align: TextAlign.Center, Shadow: true, FontSize: 96, ShadowDistance: 6)
                    { DynamicFontSize = () => (int)(96f * RwScale) },
                }, Gap: 8f)),
            new ConditionalElement(
                () => _running,
                new ColumnElement(new HudElement[]
                {
                    new SpacerElement(Width: W, Height: 0f),
                    new TextElement(
                        () => _loc.T("rm.hud.countdown"),
                        Color: () => (ColorRgba?)_services.Theme.Colors.HudText,
                        Width: W, Align: TextAlign.Center, Shadow: true, FontSize: 75, ShadowDistance: 4)
                    { DynamicFontSize = () => (int)(75f * Scale) },
                    new TextElement(
                        () => FormatRemaining(),
                        Color: () => _remaining <= 5d
                            ? new ColorRgba(1f, 0.15f, 0.15f, 1f)
                            : (ColorRgba?)_services.Theme.Colors.HudAccent,
                        // No Emphasis (see note above): keeps DynamicFontSize=160 instead of being clobbered to ~15.
                        Width: W, Align: TextAlign.Center, Shadow: true, FontSize: 160, ShadowDistance: 8)
                    { DynamicFontSize = () => (int)(160f * Scale) },
                }, Gap: 10f)),
        }, Gap: 0f);
    }
}
