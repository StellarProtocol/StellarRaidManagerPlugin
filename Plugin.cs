using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "Raid Manager";

    private readonly IPluginServices _services;
    private readonly ILocalization _loc;
    private readonly IWindowControl _hud;
    private IWindowControl _hubWindow = null!;   // Plugin.Hub.cs
    private readonly IDisposable _launcherEntry;
    private readonly IConfigSection _cfg;

    // Countdown state
    private bool _running;
    private double _remaining;
    private float _sizeMult;
    private bool _ctEnabled;
    private bool _ctParty;
    private bool _ctGuild;
    private bool _ctLocal;

    // Raid warning state
    private bool _rwEnabled;
    private bool _rwUseIngameWarning;
    private float _rwSizeMult;
    private bool _rwParty;
    private bool _rwGuild;
    private bool _rwLocal;
    private string _rwText = "";
    private bool _rwVisible;
    private double _rwTimer;

    private readonly Action<ChatMessage> _onMessage;
    private readonly Action<float> _onUpdate;
    private readonly Action _onLogout;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc = services.Localization;
        _cfg = _services.Config.GetSection("settings");

        _sizeMult  = SizeToMult(_cfg.Get<string>("size", "large"));
        _ctEnabled = _cfg.Get<bool>("ct_enabled", true);
        _ctParty   = _cfg.Get<bool>("ct_channel_party", true);
        _ctGuild   = _cfg.Get<bool>("ct_channel_guild", true);
        _ctLocal   = _cfg.Get<bool>("ct_channel_local", false);
        _rwEnabled            = _cfg.Get<bool>("rw_enabled", true);
        _rwUseIngameWarning   = _cfg.Get<bool>("rw_ingame_warning", true);
        _rwSizeMult           = SizeToMult(_cfg.Get<string>("rw_size", "large"));
        _rwParty     = _cfg.Get<bool>("rw_channel_party", true);
        _rwGuild     = _cfg.Get<bool>("rw_channel_guild", true);
        _rwLocal     = _cfg.Get<bool>("rw_channel_local", false);

        _onMessage = OnMessage;
        _onUpdate  = OnUpdate;
        _onLogout  = OnLogoutCloseWindows;

        _services.Chat.MessageReceived += _onMessage;
        _services.Framework.Update += _onUpdate;

        _hud = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                "countdown-timer.hud",
                _loc.T("rm.hud.title"),
                new WindowRect(0f, 72f, 460f, 0f),
                WindowCategory.HUD,
                WindowPanelStyle.Borderless)
            {
                Surface          = SurfaceStyle.HudOverlay,
                Anchor           = WindowAnchor.Top,
                Draggable        = true,
                EditModeDragOnly = true,
                // Gameplay overlay (countdown / raid warning): draw only while in-world and not on a loading screen.
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            },
            BuildHudRoot()));

        // Tool windows (house style, Plugin.Style*.cs). The help popover first: the prebuilt windows' "?" poll it.
        RegisterHelpWindow();     // Plugin.Help.cs
        RegisterCtWindow();       // Plugin.Countdown.Ui.cs — Countdown & Raid Warning

        // Punctuate Mark Presets — save/reload dungeon marker layouts (Plugin.Marks*.cs).
        InitMarks();

        // Mechanic Callouts — who got which boss mechanic: list, minimap, on-me alert (Plugin.MechanicCallouts.cs).
        InitMechanicCallouts();

        RegisterHubWindow();      // Plugin.Hub.cs — the launcher tile's window, one card per tool
        CreateHubCenterers();

        // Title stays the fixed literal "Raid Manager" — the stable pin-identity key (ILauncher.cs:49-50) —
        // so a pinned tile survives a language change; TitleProvider carries the live-localized display.
        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title: "Raid Manager",
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen: OpenHub)
        { Group = LauncherGroup.Plugin,
          // Re-localize the tile DISPLAY on a language change; Title above never changes (pin identity).
          TitleProvider = () => _loc.T("rm.title"),
          // Launcher tile: only surface the plugin while in-world.
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });

        // In-world only + closed on logout (Stellar-Design-Language §5): the phase stays World through the logout
        // transition, so ShouldRender alone would leave the tool windows up on the loading screen. Synchronous — Update
        // is world-gated, so a Post could run too late.
        _services.ClientState.Logout += _onLogout;
    }

    public void Dispose()
    {
        _services.Chat.MessageReceived -= _onMessage;
        _services.Framework.Update -= _onUpdate;
        try { _services.ClientState.Logout -= _onLogout; } catch { }
        StopCenterers();
        DisposeMarks();
        DisposeMechanicCallouts();
        _launcherEntry.Dispose();
        _hubWindow.Remove();
        _ctWindow.Remove();
        _helpWindow.Remove();
        _hud.Remove();
    }

    private void StopCenterers()
    {
        _ctCenter?.Stop(); _marksCenter?.Stop(); _mechCenter?.Stop();
        _importCenter?.Stop(); _exportCenter?.Stop();
    }

    // Logout → close every tool window so none lingers through the logout transition or reappears at the next login.
    private void OnLogoutCloseWindows()
    {
        try
        {
            StopCenterers();
            CloseHelp();
            _hubWindow.SetVisible(false);
            _ctWindow.SetVisible(false);
            _marksWindow.SetVisible(false);
            _importWindow.SetVisible(false);
            _exportWindow.SetVisible(false);
            _mechWindow.SetVisible(false);
        }
        catch (Exception ex) { _services.Log.Warning($"[RaidManager] logout window close failed: {ex.Message}"); }
    }

    private void SetSize(string size)
    {
        _sizeMult = SizeToMult(size);
        _cfg.Set<string>("size", size);
        _cfg.Save();
        _hud.MarkDirty();
        _ctWindow.MarkDirty();
    }

    private void SetRwSize(string size)
    {
        _rwSizeMult = SizeToMult(size);
        _cfg.Set<string>("rw_size", size);
        _cfg.Save();
        _hud.MarkDirty();
        _ctWindow.MarkDirty();
    }

    private static float SizeToMult(string? size) => size switch
    {
        "small"  => 0.5f,
        "medium" => 0.75f,
        _        => 1.0f,
    };

    // Font sizes below are CANVAS UNITS (v2 design space 2560×1440); the CanvasScaler makes them
    // resolution-independent. Do NOT fold ScreenHeight in here — that double-scales against the CanvasScaler
    // (quadratic shrink; text too small at <1440p). See WindowBuilder-Patterns.md (CanvasScaler / UI Scale).
    private float Scale   => _sizeMult;
    private float RwScale => _rwSizeMult;

    // Launcher tile art — stays the old raid-manager-icon.png until the new "raidmanager" art is chosen.
    private static byte[]? LoadIconPng() => CachedPng("raid-manager-icon.png");
}
