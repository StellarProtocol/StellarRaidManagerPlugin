using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// Tool-window plumbing shared by every house-style window (hub, Countdown & Raid Warning, Mark Presets, Import / Export,
// Mechanic Callouts, the help popover): the in-world gate and the common WindowSpec.
public sealed partial class Plugin
{
    // Tool windows draw in-world only, never on a loading screen (and are closed on logout — Plugin.cs).
    private bool ToolWindowsAllowed =>
        _services.ClientState.Phase == GamePhase.World && (_services.ClientState.UiState & GameUIState.Loading) == 0;

    // The shared spec of every house-style tool window: GlassMenu, no framework title bar (the ToolHeader carries the
    // title and the round ✕; the whole frame drags — presses on controls / inputs / scrolling lists don't, WindowDragRule),
    // in-world only. Title is the localized window name (layout editor / diagnostics), live via TitleProvider.
    private WindowSpec ToolSpec(string id, string titleKey, WindowRect rect, bool prebuild = false) => new(
        Id: id, Title: _loc.T(titleKey), DefaultRect: rect, Category: WindowCategory.Tools, Style: WindowPanelStyle.GlassMenu)
    {
        TitleProvider = () => _loc.T(titleKey),
        Draggable = true, Closable = true, StartVisible = false, ShowTitleBar = false,
        ShouldRender = () => ToolWindowsAllowed,
        // Prebuild (heavy windows): the framework builds the tree at the title screen and fills it behind the loading
        // screen, so the first open doesn't pay it in a game frame (Plugin-Performance-Rules §7, fw 2.30.0).
        Prebuild = prebuild,
    };
}
