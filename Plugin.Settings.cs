using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

public sealed partial class Plugin
{
    private HudElement BuildSettingsRoot()
    {
        return new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T("rm.countdown"), Emphasis: true),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _ctEnabled, Set: v => { _ctEnabled = v; _cfg.Set<bool>("ct_enabled", v); _cfg.Save(); }),
                new TextElement(() => _loc.T("rm.countdown.enable")),
            }, Gap: 6f),
            new TextElement(
                () => _loc.T("rm.countdown.help"),
                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new SeparatorElement(),
            new TextElement(() => _loc.T("rm.displaySize")),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => _loc.T("rm.size.small"),  OnClick: () => SetSize("small"),  Active: () => _sizeMult == 0.5f),
                new ButtonElement(() => _loc.T("rm.size.medium"), OnClick: () => SetSize("medium"), Active: () => _sizeMult == 0.75f),
                new ButtonElement(() => _loc.T("rm.size.large"),  OnClick: () => SetSize("large"),  Active: () => _sizeMult == 1.0f),
            }, Gap: 8f),
            new SeparatorElement(),
            new TextElement(() => _loc.T("rm.countdown.channels")),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _ctParty, Set: v => { _ctParty = v; _cfg.Set<bool>("ct_channel_party", v); _cfg.Save(); }),
                new TextElement(() => _loc.T("rm.channel.party")),
            }, Gap: 6f),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _ctGuild, Set: v => { _ctGuild = v; _cfg.Set<bool>("ct_channel_guild", v); _cfg.Save(); }),
                new TextElement(() => _loc.T("rm.channel.guild")),
            }, Gap: 6f),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _ctLocal, Set: v => { _ctLocal = v; _cfg.Set<bool>("ct_channel_local", v); _cfg.Save(); }),
                new TextElement(() => _loc.T("rm.channel.local")),
            }, Gap: 6f),

            new SpacerElement(Height: 4f),
            new TextElement(() => _loc.T("rm.raidWarning"), Emphasis: true),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _rwEnabled, Set: v => { _rwEnabled = v; _cfg.Set<bool>("rw_enabled", v); _cfg.Save(); }),
                new TextElement(() => _loc.T("rm.raidWarning.enable")),
            }, Gap: 6f),
            new TextElement(
                () => _loc.T("rm.raidWarning.help"),
                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _rwUseIngameWarning, Set: v => { _rwUseIngameWarning = v; _cfg.Set<bool>("rw_ingame_warning", v); _cfg.Save(); }),
                new TextElement(() => _loc.T("rm.raidWarning.useIngame")),
            }, Gap: 6f),
            new TextElement(
                () => _rwUseIngameWarning
                    ? _loc.T("rm.raidWarning.ingameDesc")
                    : _loc.T("rm.raidWarning.overlayDesc"),
                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new SeparatorElement(),
            new ConditionalElement(
                () => !_rwUseIngameWarning,
                new ColumnElement(new HudElement[]
                {
                    new TextElement(() => _loc.T("rm.displaySize")),
                    new RowElement(new HudElement[]
                    {
                        new ButtonElement(() => _loc.T("rm.size.small"),  OnClick: () => SetRwSize("small"),  Active: () => _rwSizeMult == 0.5f),
                        new ButtonElement(() => _loc.T("rm.size.medium"), OnClick: () => SetRwSize("medium"), Active: () => _rwSizeMult == 0.75f),
                        new ButtonElement(() => _loc.T("rm.size.large"),  OnClick: () => SetRwSize("large"),  Active: () => _rwSizeMult == 1.0f),
                    }, Gap: 8f),
                    new SeparatorElement(),
                }, Gap: 8f)),
            new TextElement(() => _loc.T("rm.raidWarning.channels")),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _rwParty, Set: v => { _rwParty = v; _cfg.Set<bool>("rw_channel_party", v); _cfg.Save(); }),
                new TextElement(() => _loc.T("rm.channel.party")),
            }, Gap: 6f),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _rwGuild, Set: v => { _rwGuild = v; _cfg.Set<bool>("rw_channel_guild", v); _cfg.Save(); }),
                new TextElement(() => _loc.T("rm.channel.guild")),
            }, Gap: 6f),
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _rwLocal, Set: v => { _rwLocal = v; _cfg.Set<bool>("rw_channel_local", v); _cfg.Save(); }),
                new TextElement(() => _loc.T("rm.channel.local")),
            }, Gap: 6f),

            new SpacerElement(Height: 4f),
            new TextElement(() => _loc.T("rm.preview"), Emphasis: true),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => _loc.T("rm.test.countdown"), OnClick: () => StartCountdown(10)),
                new ButtonElement(() => _loc.T("rm.test.raidWarning"), OnClick: () =>
                {
                    if (_rwUseIngameWarning)
                        _services.NoticeTips.Create(NoticeTipType.Special).WithContent(_loc.T("rm.test.raidWarning")).WithAudio(NoticeTipAudio.DungeonVictory).WithDuration(5f).Show();
                    else { _rwText = _loc.T("rm.test.raidWarning"); _rwVisible = true; _rwTimer = 5.0; _hud.MarkDirty(); }
                }),
            }, Gap: 8f),
        }, Gap: 8f);
    }
}
