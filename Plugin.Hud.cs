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
                        Emphasis: true, Width: W, Align: TextAlign.Center, Shadow: true, FontSize: 72, ShadowDistance: 6)
                    { DynamicFontSize = () => (int)(72f * RwScale) },
                }, Gap: 8f)),
            new ConditionalElement(
                () => _running,
                new ColumnElement(new HudElement[]
                {
                    new SpacerElement(Width: W, Height: 0f),
                    new TextElement(
                        () => "COUNTDOWN",
                        Color: () => (ColorRgba?)_services.Theme.Colors.HudText,
                        Width: W, Align: TextAlign.Center, Shadow: true, FontSize: 56, ShadowDistance: 4)
                    { DynamicFontSize = () => (int)(56f * Scale) },
                    new TextElement(
                        () => FormatRemaining(),
                        Color: () => _remaining <= 5d
                            ? new ColorRgba(1f, 0.15f, 0.15f, 1f)
                            : (ColorRgba?)_services.Theme.Colors.HudAccent,
                        Emphasis: true, Width: W, Align: TextAlign.Center, Shadow: true, FontSize: 120, ShadowDistance: 8)
                    { DynamicFontSize = () => (int)(120f * Scale) },
                }, Gap: 10f)),
        }, Gap: 0f);
    }
}
