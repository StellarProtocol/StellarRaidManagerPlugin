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
    private readonly IWindowControl _settingsWindow;
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

        _settingsWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id: "stellar-raid-manager.settings",
                Title: _loc.T("rm.title"),
                DefaultRect: new WindowRect(810f, 480f, 300f, 0f),
                Category: WindowCategory.Tools,
                Style: WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              // Settings window: available whenever the world is active and not on a loading screen.
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                   && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: BuildSettingsRoot(),
            OnClose: () => _settingsWindow.SetVisible(false)));

        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title: _loc.T("rm.title"),
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen: () => _settingsWindow.SetVisible(true))
        { Group = LauncherGroup.Plugin,
          // Re-localize the tile title live on a language change (Title alone is a captured string).
          TitleProvider = () => _loc.T("rm.title"),
          // Launcher tile: only surface the plugin while in-world.
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });

        // Punctuate Mark Presets — save/reload dungeon marker layouts (Plugin.Marks*.cs).
        InitMarks();
    }

    public void Dispose()
    {
        _services.Chat.MessageReceived -= _onMessage;
        _services.Framework.Update -= _onUpdate;
        DisposeMarks();
        _launcherEntry.Dispose();
        _settingsWindow.Remove();
        _hud.Remove();
    }

    private void SetSize(string size)
    {
        _sizeMult = SizeToMult(size);
        _cfg.Set<string>("size", size);
        _cfg.Save();
        _hud.MarkDirty();
        _settingsWindow.MarkDirty();
    }

    private void SetRwSize(string size)
    {
        _rwSizeMult = SizeToMult(size);
        _cfg.Set<string>("rw_size", size);
        _cfg.Save();
        _hud.MarkDirty();
        _settingsWindow.MarkDirty();
    }

    private static float SizeToMult(string? size) => size switch
    {
        "small"  => 0.5f,
        "medium" => 0.75f,
        _        => 1.0f,
    };

    private float Scale   => (_services.Framework.ScreenHeight / 1080f) * _sizeMult;
    private float RwScale => (_services.Framework.ScreenHeight / 1080f) * _rwSizeMult;

    private static byte[]? LoadIconPng()
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.RaidManager.raid-manager-icon.png");
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
