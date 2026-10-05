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
// The window has three stacked sections: the PRESETS list (activate / rename / delete, active row tinted), a CREATE-preset
// row, and — only when a preset is active — the STEP panel (Prev / Reset / Next, Save Step, Delete Step, the live
// placed-count readout and the dungeon hint). The share-code Import / Export buttons are built in
// Plugin.Marks.Share.cs and slotted in below. Every label is routed through _loc.T/_loc.TFormat("rm.marks.*") —
// RaidManager is localized across Lang/{en,ja,th,id,fil}.json (Rule 10).
public sealed partial class Plugin
{
    private void RegisterMarksWindow()
    {
        _marksWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id: "stellar-raid-manager.marks",
                Title: _loc.T("rm.marks.title"),
                DefaultRect: new WindowRect(500f, 300f, 440f, 0f),
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
                // Name: a LABEL by default; the row being renamed swaps to an input field in the same cell (inline
                // rename, Plugin.Marks.Rename.cs). Enter submits; the check chip below commits the live _editBuffer.
                new CellElement(new ConditionalElement(() => IsEditing(idx),
                    new InputElement(
                        Get:      () => _editBuffer,
                        Submit:   _ => CommitRename(idx),
                        OnChange: s => _editBuffer = s),
                    new TextElement(
                        () => idx < _presets.Count ? _presets[idx].Name : "",
                        Emphasis: true,
                        // Tint the active row so it reads at a glance. NON-active rows must return the explicit
                        // default color, NOT null: a Color lambda that returns null is "leave the color untouched"
                        // (the framework binding only applies ColorFn() when non-null), so a row painted HudAccent
                        // while active would STAY yellow after you activate another. MenuText re-paints the default.
                        Color: () => idx == _activeIndex
                            ? (ColorRgba?)_services.Theme.Colors.HudAccent
                            : (ColorRgba?)_services.Theme.Colors.MenuText)), Weight: 1f),
                new CellElement(new TextElement(
                    // Steps[0] is the blank Start anchor — count only the real steps (1..N).
                    () => idx < _presets.Count ? _loc.TFormat("rm.marks.stepCount", _presets[idx].Steps.Count - 1) : "",
                    Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 64f),
                // Toggle: the active preset's button reads "Deactivate" and clears the selection; all others read
                // "Activate" and switch to that preset. Highlight the button (Active tint) while this preset is the
                // active one — so the "Deactivate" state reads at a glance alongside the yellow row name.
                new CellElement(new ButtonElement(
                    () => idx == _activeIndex ? _loc.T("rm.marks.deactivate") : _loc.T("rm.marks.activate"),
                    OnClick: () => { if (idx == _activeIndex) DeactivatePreset(); else ActivatePreset(idx); },
                    // Disabled on the row being renamed so an activate/delete can't race the in-progress edit.
                    Enabled: () => !IsEditing(idx),
                    Active: () => idx == _activeIndex), Width: 74f),
                // Icon-only rename chip: pencil enters edit mode; while editing, the same cell shows a check that
                // commits (Wardrobe's edit↔save swap). Embedded PNGs, see Plugin.Marks.Rename.cs.
                new CellElement(new ConditionalElement(() => IsEditing(idx),
                    new ButtonElement(() => "", OnClick: () => CommitRename(idx), Icon: () => SaveIconPng),
                    new ButtonElement(() => "", OnClick: () => EnterEdit(idx), Icon: () => EditIconPng)), Width: 36f),
                // Icon-only trash-can button (procedural PNG, see Plugin.TrashIcon.cs) — no text label.
                new CellElement(new ButtonElement(() => "",
                    OnClick: () => DeletePreset(idx), Enabled: () => !IsEditing(idx), Icon: () => _trashPng), Width: 36f),
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
            BuildMarksImportButton(),   // share-code import (Plugin.Marks.Share.cs)

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

                    // Per-step comment (Plugin.Marks.StepComment.cs). Same label↔input + pencil↔check swap as the
                    // preset rename above. The row is ALWAYS present while a preset is active — gating it on
                    // _currentStep >= 1 added/removed it crossing Start↔Step 1, changing the window height and making
                    // the whole UI jump. On the blank Start anchor it's read-only instead: CurrentStepComment() is ""
                    // there (so the muted placeholder shows) and the pencil is disabled — same cells, same size.
                    new RowElement(new HudElement[]
                    {
                        new CellElement(new ConditionalElement(IsEditingStepComment,
                            new InputElement(
                                Get:      () => _commentBuffer,
                                Submit:   _ => CommitStepComment(),
                                OnChange: s => _commentBuffer = s),
                            new TextElement(
                                () => CurrentStepComment() is { Length: > 0 } c ? c : _loc.T("rm.marks.commentPlaceholder"),
                                // Explicit color on BOTH branches — a null would leave the placeholder's muted
                                // tint stuck on once a comment is set (Color lambda null = "untouched").
                                Color: () => CurrentStepComment().Length > 0
                                    ? (ColorRgba?)_services.Theme.Colors.MenuText
                                    : (ColorRgba?)_services.Theme.Colors.TextMuted)), Weight: 1f),
                        new CellElement(new ConditionalElement(IsEditingStepComment,
                            new ButtonElement(() => "", OnClick: CommitStepComment, Icon: () => SaveIconPng),
                            // Disabled (not hidden) on Start so the cell keeps its footprint; EnterStepCommentEdit
                            // also refuses step < 1 on its own.
                            new ButtonElement(() => "", OnClick: EnterStepCommentEdit,
                                Enabled: () => _currentStep >= 1, Icon: () => EditIconPng)), Width: 36f),
                    }, Gap: 4f),

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
                    BuildMarksExportButton(),  // Export Code → copy window (Plugin.Marks.Share.cs / .ExportWindow.cs)

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
