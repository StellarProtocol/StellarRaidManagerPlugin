using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — window ─────────────────────────────────────────────────────────────────────────────
//
// A dedicated "Mark Presets" window, opened from the opener button at the top of the Raid Manager settings
// window (see Plugin.Settings.cs) — it has no launcher tile of its own. Gated to World phase like the other
// windows. State/logic live in Plugin.Marks.cs; interop in Plugin.Marks.Interop.cs.
//
// The window has three stacked sections: the PRESETS list (activate / delete, active row tinted), a CREATE-preset
// row, and — only when a preset is active — the STEP panel (Prev / Reset / Next, Save Step, Delete Step, the live
// placed-count readout and the dungeon hint). Every label is routed through _loc.T/_loc.TFormat("rm.marks.*") —
// RaidManager is localized across Lang/{en,ja,th,id,fil}.json (Rule 10).
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
    }

    private HudElement BuildMarksRoot()
    {
        // Preset rows: fixed row templates, the ListElement count gates how many are shown (see ListElement below).
        var rows = new HudElement[MaxPresets];
        for (int i = 0; i < MaxPresets; i++)
        {
            var idx = i;
            rows[i] = new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(
                    () => idx < _presets.Count ? _presets[idx].Name : "",
                    Emphasis: true,
                    // Tint the active row so it reads at a glance. NON-active rows must return the explicit default
                    // color, NOT null: a Color lambda that returns null is "leave the color untouched" (the framework
                    // binding only applies ColorFn() when non-null), so a row painted HudAccent while active would
                    // STAY yellow after you activate another. Returning MenuText re-paints it back to default.
                    Color: () => idx == _activeIndex
                        ? (ColorRgba?)_services.Theme.Colors.HudAccent
                        : (ColorRgba?)_services.Theme.Colors.MenuText), Weight: 1f),
                new CellElement(new TextElement(
                    // Steps[0] is the blank Start anchor — count only the real steps (1..N).
                    () => idx < _presets.Count ? _loc.TFormat("rm.marks.stepCount", _presets[idx].Steps.Count - 1) : "",
                    Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 64f),
                new CellElement(new ButtonElement(() => _loc.T("rm.marks.activate"),
                    OnClick: () => ActivatePreset(idx), Active: () => idx == _activeIndex), Width: 74f),
                // Icon-only trash-can button (procedural PNG, see Plugin.TrashIcon.cs) — no text label.
                new CellElement(new ButtonElement(() => "",
                    OnClick: () => DeletePreset(idx), Icon: () => _trashPng), Width: 36f),
            }, Gap: 4f);
        }

        return new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T("rm.marks.header"), Emphasis: true),
            new TextElement(
                () => _loc.T("rm.marks.intro"),
                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),

            // ── Presets list ────────────────────────────────────
            new SeparatorElement(),
            new TextElement(() => _loc.T("rm.marks.presetsHeader"), Emphasis: true),
            new ConditionalElement(() => _presets.Count == 0,
                new TextElement(() => _loc.T("rm.marks.empty"),
                    Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                new ScrollElement(new ListElement(() => _presets.Count, rows), 160f)),

            // ── Create preset ───────────────────────────────────
            new SeparatorElement(),
            new TextElement(() => _loc.T("rm.marks.createHeader"), Emphasis: true),
            new RowElement(new HudElement[]
            {
                new CellElement(new InputElement(
                    Get:      () => _newPresetName,
                    Submit:   s => { _newPresetName = s; CreatePreset(); },
                    OnChange: s => _newPresetName = s), Weight: 1f),
                new CellElement(new ButtonElement(() => _loc.T("rm.marks.create"), OnClick: CreatePreset), Width: 90f),
            }, Gap: 4f),
            new TextElement(
                () => _loc.T("rm.marks.createHint"),
                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),

            // ── Active-preset step panel (only when one is active) ──
            new ConditionalElement(() => _activeIndex >= 0 && _activeIndex < _presets.Count,
                new ColumnElement(new HudElement[]
                {
                    new SeparatorElement(),
                    new TextElement(() => _loc.TFormat("rm.marks.activeHeader", ActivePreset()?.Name ?? ""),
                        Emphasis: true),

                    // Step 0 = the blank "Start" anchor; real steps are 1..N (N == Steps.Count - 1).
                    new TextElement(() =>
                    {
                        int n = (ActivePreset()?.Steps.Count ?? 1) - 1;
                        return _currentStep <= 0
                            ? _loc.TFormat("rm.marks.stepStart", n)
                            : _loc.TFormat("rm.marks.step", _currentStep, n);
                    }, Emphasis: true),

                    new RowElement(new HudElement[]
                    {
                        new ButtonElement(() => _loc.T("rm.marks.prev"),  OnClick: PrevStep),
                        new ButtonElement(() => _loc.T("rm.marks.reset"), OnClick: ResetSteps),
                        new ButtonElement(() => _loc.T("rm.marks.next"),  OnClick: NextStep),
                    }, Gap: 6f),
                    new RowElement(new HudElement[]
                    {
                        new ButtonElement(() => _loc.T("rm.marks.saveStep"),   OnClick: SaveStep),
                        new ButtonElement(() => _loc.T("rm.marks.deleteStep"), OnClick: DeleteCurrentStep),
                    }, Gap: 6f),

                    new ConditionalElement(() => (ActivePreset()?.Steps.Count ?? 0) <= 1,
                        new TextElement(() => _loc.T("rm.marks.noSteps"),
                            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted)),

                    new TextElement(
                        () => _placedCount < 0
                            ? _loc.T("rm.marks.placed.unknown")
                            : _loc.TFormat("rm.marks.placed", _placedCount),
                        Emphasis: true),
                    new TextElement(
                        () => _loc.T("rm.marks.hint"),
                        Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
                }, Gap: 6f)),

            // ── Status line ─────────────────────────────────────
            new SeparatorElement(),
            new ConditionalElement(() => _marksStatus.Length > 0,
                new TextElement(() => _marksStatus,
                    Color: () => (ColorRgba?)_services.Theme.Colors.HudAccent)),
        }, Gap: 6f);
    }
}
