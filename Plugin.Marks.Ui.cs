using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — window + launcher tile ─────────────────────────────────────────────────────────────
//
// A dedicated "Mark Presets" window (its own launcher tile, plus an opener button in the Raid Manager settings
// window — see Plugin.Settings.cs). Gated to World phase like the other windows. State/logic live in
// Plugin.Marks.cs; interop in Plugin.Marks.Interop.cs. Every label is routed through _loc.T/_loc.TFormat
// ("rm.marks.*") — RaidManager is localized across Lang/{en,ja,th,id,fil}.json (Rule 10).
public sealed partial class Plugin
{
    private void RegisterMarksWindow()
    {
        _marksWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id: "stellar-raid-manager.marks",
                Title: _loc.T("rm.marks.title"),
                DefaultRect: new WindowRect(500f, 300f, 340f, 0f),
                Category: WindowCategory.Tools,
                Style: WindowPanelStyle.GlassMenu)
            {
                Draggable = true, Closable = true, StartVisible = false,
                // Placement/read only make sense in world; hide on loading screens too.
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            },
            Root: BuildMarksRoot(),
            OnClose: () => _marksWindow.SetVisible(false)));

        _marksLauncher = _services.Launcher.Register(new LauncherEntry(
            Title: _loc.T("rm.marks.title"),
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen: () => _marksWindow.SetVisible(true))
        { Group = LauncherGroup.Plugin,
          // Re-localize the tile title live on a language change (Title alone is a captured string).
          TitleProvider = () => _loc.T("rm.marks.title"),
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });
    }

    private HudElement BuildMarksRoot()
    {
        var rows = new HudElement[MaxPresets];
        for (int i = 0; i < MaxPresets; i++)
        {
            var idx = i;
            rows[i] = new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(
                    () => idx < _presets.Count ? _presets[idx].Name : "",
                    Emphasis: true), Weight: 1f),
                new CellElement(new TextElement(
                    () => idx < _presets.Count ? $"{_presets[idx].Marks.Count}" : "",
                    Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 24f),
                new CellElement(new ButtonElement(() => _loc.T("rm.marks.load"),   OnClick: () => LoadPreset(idx)),   Width: 60f),
                new CellElement(new ButtonElement(() => _loc.T("rm.marks.delete"), OnClick: () => DeletePreset(idx)), Width: 66f),
            }, Gap: 4f);
        }

        return new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T("rm.marks.header"), Emphasis: true),
            new TextElement(
                () => _loc.T("rm.marks.intro"),
                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new TextElement(
                () => _loc.T("rm.marks.hint"),
                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),

            new SeparatorElement(),
            new TextElement(
                () => _placedCount < 0
                    ? _loc.T("rm.marks.placed.unknown")
                    : _loc.TFormat("rm.marks.placed", _placedCount),
                Emphasis: true),

            new SeparatorElement(),
            new TextElement(() => _loc.T("rm.marks.saveHeader"), Emphasis: true),
            new RowElement(new HudElement[]
            {
                new CellElement(new InputElement(
                    Get:      () => _newPresetName,
                    Submit:   s => { _newPresetName = s; SaveCurrentAsPreset(); },
                    OnChange: s => _newPresetName = s), Weight: 1f),
                new CellElement(new ButtonElement(() => _loc.T("rm.marks.save"), OnClick: SaveCurrentAsPreset), Width: 100f),
            }, Gap: 4f),
            new TextElement(
                () => _loc.T("rm.marks.saveHint"),
                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),

            new SeparatorElement(),
            new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(() => _loc.T("rm.marks.savedHeader"), Emphasis: true), Weight: 1f),
                new CellElement(new ButtonElement(() => _loc.T("rm.marks.clear"), OnClick: ClearMarksNow), Width: 100f),
            }, Gap: 4f),
            new ConditionalElement(() => _presets.Count == 0,
                new TextElement(() => _loc.T("rm.marks.empty"),
                    Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                new ScrollElement(new ListElement(() => _presets.Count, rows), 180f)),

            new SeparatorElement(),
            new ConditionalElement(() => _marksStatus.Length > 0,
                new TextElement(() => _marksStatus,
                    Color: () => (ColorRgba?)_services.Theme.Colors.HudAccent)),
        }, Gap: 6f);
    }
}
