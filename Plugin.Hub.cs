using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// The hub window "Raid Manager" (approved redesign 2026-11-12, option B): ToolHeader + one 56 px card per tool —
// Mark Presets, Mechanic Callouts, Countdown & Raid Warning. Each card shows the tool's live status from the SAME
// builder its window header uses (MarksCardStatus / MechStatus / CtStatus), and opens that window centred on the hub
// and in front (WindowCenterer.OpenCentred). The launcher tile opens the hub.
//
// Window id stays "stellar-raid-manager.settings" (the old single settings window), so a saved position carries over.
public sealed partial class Plugin
{
    // 480 (was 400, R18): the card status column (480 − 24 pad − 40 tile − 24 › − 4 × 8 gaps = 360) holds the longest
    // status in any locale — "Listahan naka-off · Minimap naka-off · Alerto naka-off" (fil, ≈340 px) cut at 400.
    private const float HubWidth = 480f;
    private const float HubCardGap = 8f;
    private const int   HubToolCount = 3;

    private WindowCenterer _ctCenter = null!, _marksCenter = null!, _mechCenter = null!;

    private void RegisterHubWindow()
    {
        _hubWindow = _services.Windows.Register(new WindowRegistration(
            Spec: ToolSpec("stellar-raid-manager.settings", "rm.title", new WindowRect(810f, 480f, HubWidth, 0f)),
            Root: BuildHubRoot(),
            OnClose: () => _hubWindow.SetVisible(false)));
    }

    // Child windows open centred on the hub's CURRENT rect (or the screen if the hub isn't laid out).
    private void CreateHubCenterers()
    {
        var onHub = WindowCenterer.CentreOf(_hubWindow);
        _ctCenter    = new WindowCenterer(_services.Framework, _ctWindow, onHub);
        _marksCenter = new WindowCenterer(_services.Framework, _marksWindow, onHub);
        _mechCenter  = new WindowCenterer(_services.Framework, _mechWindow, onHub);
    }

    // Launcher tile: show + front (every Raid Manager open fronts — WindowCenterer.OpenCentred); the hub keeps the
    // position the user left it at.
    private void OpenHub()
    {
        _hubWindow.SetVisible(true);
        _hubWindow.BringToFront();
    }

    private HudElement BuildHubRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader("raidmanager", () => _loc.T("rm.title"), HubStatus, () => _hubWindow.SetVisible(false)),
        new ColumnElement(new HudElement[]
        {
            HubCard("marks",     () => _loc.T("rm.marks.title"), MarksCardStatus, () => _marksCenter.OpenCentred()),
            HubCard("callouts",  () => _loc.T("rm.mech.title"),  MechStatus,      () => _mechCenter.OpenCentred()),
            HubCard("countdown", () => _loc.T("rm.ct.title"),    CtStatus,        () => _ctCenter.OpenCentred()),
        }, Gap: HubCardGap),
    }, Gap: 10f);

    // "3 tools" — formatted once per language.
    private string? _hubStatusLang;
    private string _hubStatus = string.Empty;

    private string HubStatus()
    {
        var lang = _loc.Language;
        if (!string.Equals(lang, _hubStatusLang, StringComparison.Ordinal))
        {
            _hubStatusLang = lang;
            _hubStatus = _loc.TFormat("rm.hub.status", HubToolCount);
        }
        return _hubStatus;
    }
}
