using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// "Countdown & Raid Warning" window (approved redesign 2026-11-12 — the COUNTDOWN / RAID WARNING sections that used to
// fill the single settings window, now behind the hub's third card). 460 wide, constant height: ToolHeader (live status
// shared with the hub card) + two SectionPanels of setting rows, each row ending in "?" (the old description sentences
// live in the help popover), then the two Test pills. "Overlay size" is RECESSED (not hidden) while Style = Game notice.
// Same config keys, defaults and behaviour as before (size / rw_size / ct_* / rw_*); /ct and /rw parsing is in
// Plugin.Logic.cs.
public sealed partial class Plugin
{
    private IWindowControl _ctWindow = null!;

    private const float CtWidth   = 460f;
    private const float CtLabelW  = 118f;
    private const string CtSection = "rm.countdown";
    private const string RwSection = "rm.raidWarning";

    private void RegisterCtWindow()
    {
        _ctWindow = _services.Windows.Register(new WindowRegistration(
            Spec: ToolSpec("stellar-raid-manager.countdown", "rm.ct.title", new WindowRect(860f, 420f, CtWidth, 0f)),
            Root: BuildCtRoot(),
            OnClose: () => _ctWindow.SetVisible(false)));
    }

    private HelpTip CtTip(string titleKey, string section, Func<string> body) => new(titleKey, section, body, () => _ctWindow);

    private HudElement BuildCtRoot()
    {
        float half = (CtWidth - 24f - 8f) / 2f;   // two equal Test pills (window content = width − 2×12 padding)
        return new ColumnElement(new HudElement[]
        {
            ToolHeader("countdown", () => _loc.T("rm.ct.title"), CtStatus, () => _ctWindow.SetVisible(false)),

            SectionPanel(() => _loc.T(CtSection), () => _loc.T("rm.ct.hint"),
                SettingRow("rm.ct.command", CtLabelW, CtTip("rm.ct.command", CtSection, HelpText("rm.countdown.help")),
                    null, false,
                    OnOffChip(() => _ctEnabled, v => { _ctEnabled = v; SaveCtBool("ct_enabled", v); })),
                SettingRow("rm.ct.size", CtLabelW, CtTip("rm.ct.size", CtSection, HelpText("rm.ct.help.size")), null, false,
                    OptionChip(() => _loc.T("rm.size.small"),  () => _sizeMult == 0.5f,  () => SetSize("small")),
                    OptionChip(() => _loc.T("rm.size.medium"), () => _sizeMult == 0.75f, () => SetSize("medium")),
                    OptionChip(() => _loc.T("rm.size.large"),  () => _sizeMult == 1.0f,  () => SetSize("large"))),
                SettingRow("rm.ct.listenIn", CtLabelW, CtTip("rm.ct.listenIn", CtSection, HelpText("rm.ct.help.listen")),
                    null, false,
                    OptionChip(() => _loc.T("rm.channel.party"), () => _ctParty, () => { _ctParty = !_ctParty; SaveCtBool("ct_channel_party", _ctParty); }),
                    OptionChip(() => _loc.T("rm.channel.guild"), () => _ctGuild, () => { _ctGuild = !_ctGuild; SaveCtBool("ct_channel_guild", _ctGuild); }),
                    OptionChip(() => _loc.T("rm.channel.local"), () => _ctLocal, () => { _ctLocal = !_ctLocal; SaveCtBool("ct_channel_local", _ctLocal); }))),

            SectionPanel(() => _loc.T(RwSection), () => _loc.T("rm.rw.hint"),
                SettingRow("rm.rw.command", CtLabelW, CtTip("rm.rw.command", RwSection, HelpText("rm.raidWarning.help")),
                    null, false,
                    OnOffChip(() => _rwEnabled, v => { _rwEnabled = v; SaveCtBool("rw_enabled", v); })),
                SettingRow("rm.rw.style", CtLabelW, CtTip("rm.rw.style", RwSection, HelpText("rm.rw.help.style")), null, false,
                    OptionChip(() => _loc.T("rm.rw.style.notice"),  () => _rwUseIngameWarning,  () => SetRwStyle(true),  StyleChipW),
                    OptionChip(() => _loc.T("rm.rw.style.overlay"), () => !_rwUseIngameWarning, () => SetRwStyle(false), StyleChipW)),
                SettingRow("rm.rw.overlaySize", CtLabelW,
                    CtTip("rm.rw.overlaySize", RwSection, HelpText("rm.rw.help.overlaySize")), RwOverlayOn, false,
                    OptionChip(() => _loc.T("rm.size.small"),  () => _rwSizeMult == 0.5f,  () => SetRwSize("small"),  enabled: RwOverlayOn),
                    OptionChip(() => _loc.T("rm.size.medium"), () => _rwSizeMult == 0.75f, () => SetRwSize("medium"), enabled: RwOverlayOn),
                    OptionChip(() => _loc.T("rm.size.large"),  () => _rwSizeMult == 1.0f,  () => SetRwSize("large"),  enabled: RwOverlayOn)),
                SettingRow("rm.ct.listenIn", CtLabelW, CtTip("rm.ct.listenIn", RwSection, HelpText("rm.rw.help.listen")),
                    null, false,
                    OptionChip(() => _loc.T("rm.channel.party"), () => _rwParty, () => { _rwParty = !_rwParty; SaveCtBool("rw_channel_party", _rwParty); }),
                    OptionChip(() => _loc.T("rm.channel.guild"), () => _rwGuild, () => { _rwGuild = !_rwGuild; SaveCtBool("rw_channel_guild", _rwGuild); }),
                    OptionChip(() => _loc.T("rm.channel.local"), () => _rwLocal, () => { _rwLocal = !_rwLocal; SaveCtBool("rw_channel_local", _rwLocal); }))),

            new RowElement(new HudElement[]
            {
                FixedCell(half, NeutralButton(() => _loc.T("rm.test.countdown"), () => StartCountdown(10), LabelIn(half))),
                FixedCell(half, NeutralButton(() => _loc.T("rm.test.raidWarning"), TestRaidWarning, LabelIn(half))),
            }, Gap: 8f),
        }, Gap: 10f);
    }

    private bool RwOverlayOn() => !_rwUseIngameWarning;

    private void SaveCtBool(string key, bool v)
    {
        _cfg.Set<bool>(key, v);
        _cfg.Save();
        _ctWindow.MarkDirty();
    }

    private void SetRwStyle(bool gameNotice)
    {
        if (_rwUseIngameWarning == gameNotice) return;
        _rwUseIngameWarning = gameNotice;
        SaveCtBool("rw_ingame_warning", gameNotice);
    }

    // Same preview as before: the game notice (with its sound) or the red overlay for 5 s, per the chosen style.
    private void TestRaidWarning()
    {
        if (_rwUseIngameWarning)
            _services.NoticeTips.Create(NoticeTipType.Special).WithContent(_loc.T("rm.test.raidWarning"))
                .WithAudio(NoticeTipAudio.DungeonVictory).WithDuration(5f).Show();
        else { _rwText = _loc.T("rm.test.raidWarning"); _rwVisible = true; _rwTimer = 5.0; _hud.MarkDirty(); }
    }

    // ── status (window header AND hub card) ───────────────────────────────────────────────────────────────────────
    // "/ct on · /rw on (game notice)" — rebuilt only when an input changes (the Func is polled every refresh).
    private int _ctStatusKey = -1;
    private string? _ctStatusLang;
    private string _ctStatus = string.Empty;

    private string CtStatus()
    {
        int key = (_ctEnabled ? 1 : 0) | (_rwEnabled ? 2 : 0) | (_rwUseIngameWarning ? 4 : 0);
        var lang = _loc.Language;
        if (key == _ctStatusKey && string.Equals(lang, _ctStatusLang, StringComparison.Ordinal)) return _ctStatus;
        _ctStatusKey = key; _ctStatusLang = lang;
        string rw = !_rwEnabled ? "rm.ct.status.rwOff" : _rwUseIngameWarning ? "rm.ct.status.rwNotice" : "rm.ct.status.rwOverlay";
        _ctStatus = _loc.T(_ctEnabled ? "rm.ct.status.ctOn" : "rm.ct.status.ctOff") + " · " + _loc.T(rw);
        return _ctStatus;
    }
}
