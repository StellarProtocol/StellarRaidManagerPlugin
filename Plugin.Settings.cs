using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

public sealed partial class Plugin
{
    private HudElement BuildSettingsRoot()
    {
        return new ColumnElement(new HudElement[]
        {
            // ── Countdown ──────────────────────────────────────
            new TextElement(() => "Countdown", Emphasis: true),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _ctEnabled, Set: v => { _ctEnabled = v; _cfg.Set<bool>("ct_enabled", v); _cfg.Save(); }),
                new TextElement(() => "Enable Countdown Command"),
            }, Gap: 6f),
            new TextElement(
                () => "Use /ct <seconds> or /countdown <seconds> in chat. Example: /ct 10",
                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new SeparatorElement(),
            new TextElement(() => "Display Size"),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => "Small",  OnClick: () => SetSize("small"),  Active: () => _sizeMult == 0.5f),
                new ButtonElement(() => "Medium", OnClick: () => SetSize("medium"), Active: () => _sizeMult == 0.75f),
                new ButtonElement(() => "Large",  OnClick: () => SetSize("large"),  Active: () => _sizeMult == 1.0f),
            }, Gap: 8f),
            new SeparatorElement(),
            new TextElement(() => "Countdown Command Channels"),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _ctParty, Set: v => { _ctParty = v; _cfg.Set<bool>("ct_channel_party", v); _cfg.Save(); }),
                new TextElement(() => "Party"),
            }, Gap: 6f),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _ctGuild, Set: v => { _ctGuild = v; _cfg.Set<bool>("ct_channel_guild", v); _cfg.Save(); }),
                new TextElement(() => "Guild"),
            }, Gap: 6f),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _ctLocal, Set: v => { _ctLocal = v; _cfg.Set<bool>("ct_channel_local", v); _cfg.Save(); }),
                new TextElement(() => "Local"),
            }, Gap: 6f),

            // ── Raid Warning ────────────────────────────────────
            new SpacerElement(Height: 4f),
            new TextElement(() => "Raid Warning", Emphasis: true),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _rwEnabled, Set: v => { _rwEnabled = v; _cfg.Set<bool>("rw_enabled", v); _cfg.Save(); }),
                new TextElement(() => "Enable Raid Warning"),
            }, Gap: 6f),
            new TextElement(
                () => "Use /rw <text> in chat to display a red warning overlay for 5 seconds. Example: /rw Stack now!",
                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new SeparatorElement(),
            new TextElement(() => "Display Size"),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => "Small",  OnClick: () => SetRwSize("small"),  Active: () => _rwSizeMult == 0.5f),
                new ButtonElement(() => "Medium", OnClick: () => SetRwSize("medium"), Active: () => _rwSizeMult == 0.75f),
                new ButtonElement(() => "Large",  OnClick: () => SetRwSize("large"),  Active: () => _rwSizeMult == 1.0f),
            }, Gap: 8f),
            new SeparatorElement(),
            new TextElement(() => "Raid Warning Channels"),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _rwParty, Set: v => { _rwParty = v; _cfg.Set<bool>("rw_channel_party", v); _cfg.Save(); }),
                new TextElement(() => "Party"),
            }, Gap: 6f),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _rwGuild, Set: v => { _rwGuild = v; _cfg.Set<bool>("rw_channel_guild", v); _cfg.Save(); }),
                new TextElement(() => "Guild"),
            }, Gap: 6f),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _rwLocal, Set: v => { _rwLocal = v; _cfg.Set<bool>("rw_channel_local", v); _cfg.Save(); }),
                new TextElement(() => "Local"),
            }, Gap: 6f),

            // ── Preview ─────────────────────────────────────────
            new SpacerElement(Height: 4f),
            new TextElement(() => "Preview", Emphasis: true),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => "Test Countdown", OnClick: () => StartCountdown(10)),
                new ButtonElement(() => "Test Raid Warning", OnClick: () => { _rwText = "Test Raid Warning"; _rwVisible = true; _rwTimer = 5.0; _hud.MarkDirty(); }),
            }, Gap: 8f),
        }, Gap: 8f);
    }
}
