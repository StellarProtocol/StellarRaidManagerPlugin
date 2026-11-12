using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — the STEPS panel (always present) ───────────────────────────────────────────────────────
//
// One fixed-height slot (FixedSlot, rule 14) shows exactly one of three states, so the window never changes height:
//   • active preset — big "Step 2 / 5" (or "Start" + muted "blank board") + step dots + "?" (the old "as leader /
//     absolute world coords" note), the comment tile + round pencil (recessed on Start), ‹ Prev | Reset to Start |
//     Next › (Prev recessed on Start), Save marks as new step | Delete step | Export;
//   • no active preset — the empty state "No preset active / Press Activate on a preset …";
//   • a row's ✕ was pressed — the delete confirm (bold question, muted consequence, Danger Delete / Neutral Cancel).
// The step ops themselves are unchanged (Plugin.Marks.cs); the comment edit is Plugin.Marks.StepComment.cs.
public sealed partial class Plugin
{
    private const float StepsBodyH = 132f;   // tallest state (active navigator: 26 + 3 × 28 + 3 × 6 gaps ≈ 128)
    private const int   MaxDots    = 16;     // more steps than this show the first 16 dots
    private const float DotW = 18f, DotH = 6f;

    private bool HasActive => _activeIndex >= 0 && _activeIndex < _presets.Count;

    private HudElement BuildStepsPanel() => SectionPanel(StepsCaption, () => HasActive && !ConfirmingDelete ? _loc.T("rm.marks.stepsHint") : string.Empty,
        FixedSlot(StepsBodyH, new ConditionalElement(() => ConfirmingDelete, BuildDeleteConfirm(),
            new ConditionalElement(() => HasActive, BuildStepNavigator(), BuildStepsEmpty()))));

    private HelpTip MarksTip(string titleKey, string section, Func<string> body) => new(titleKey, section, body, () => _marksWindow);

    private HudElement BuildStepNavigator()
    {
        float third = MathF.Floor((MarksInnerW - 12f) / 3f);
        // R10: Delete step / Export are fixed cells sized to the longest locale ("Hapus langkah" (id) ≈94 px bold-13,
        // "書き出し" (ja) ≈56); Save takes the rest — 292 at the 560 window, label 268 ≥ "I-save ang mark bilang bagong
        // hakbang" (fil, ≈256) and "Simpan mark sebagai langkah baru" (id, ≈227). At 480 all three cut in fil / id.
        const float deleteW = 124f, exportW = 88f;
        float saveW = MarksInnerW - deleteW - exportW - 12f;
        return new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new TextElement(StepTitle, Color: () => TextColor(), NoWrap: true, FontSize: 18) { Bold = true },
                new TextElement(() => _currentStep <= 0 ? _loc.T("rm.marks.blankBoard") : string.Empty,
                    Color: () => MutedColor(), NoWrap: true),
                new SpacerElement(0f),
                BuildDots(),
                HelpDot(MarksTip("rm.marks.help.title", "rm.marks.stepsCaption", HelpText("rm.marks.hint"))),
            }, Gap: 8f),

            new RowElement(new HudElement[]
            {
                new CellElement(new BubbleElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0.01f, PillH),
                    new CellElement(new ConditionalElement(IsEditingStepComment,
                        new InputElement(
                            Get:      () => _commentBuffer,
                            Submit:   _ => CommitStepComment(),
                            Width:    MarksInnerW - IconButtonSize - 6f,
                            OnChange: s => _commentBuffer = s) { SingleLine = true, FocusRequest = () => _commentFocusSeq },
                        new TextElement(
                            () => CurrentStepComment() is { Length: > 0 } c ? c
                                : _currentStep <= 0 ? _loc.T("rm.marks.commentStart") : _loc.T("rm.marks.commentPlaceholder"),
                            // Explicit colour on BOTH branches — a null leaves the placeholder's muted tint stuck on.
                            Color: () => CurrentStepComment().Length > 0 ? TextColor() : MutedColor(), NoWrap: true)
                            { Ellipsize = true }), Weight: 1f),
                }, Gap: 0.01f))
                {
                    FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 8f, PaddingY = 0f,
                }, Weight: 1f),
                new ConditionalElement(IsEditingStepComment,
                    RoundIconButton("Icons.save.png", CommitStepComment),
                    RoundIconButton("Icons.edit.png", EnterStepCommentEdit, () => _currentStep >= 1)),
            }, Gap: 6f),

            new RowElement(new HudElement[]
            {
                FixedCell(third, NeutralButton(() => _loc.T("rm.marks.prev"), PrevStep, LabelIn(third), () => _currentStep > 0)),
                FixedCell(third, NeutralButton(() => _loc.T("rm.marks.reset"), ResetSteps, LabelIn(third))),
                FixedCell(third, PrimaryButton(() => _loc.T("rm.marks.next"), NextStep, LabelIn(third))),
            }, Gap: 6f),

            new RowElement(new HudElement[]
            {
                FixedCell(saveW, NeutralButton(() => _loc.T("rm.marks.saveStep"), SaveStep, LabelIn(saveW))),
                FixedCell(deleteW, NeutralButton(() => _loc.T("rm.marks.deleteStep"), DeleteCurrentStep, LabelIn(deleteW),
                    CanDeleteStep)),
                FixedCell(exportW, NeutralButtonAt(() => _loc.T("rm.marks.export"), OpenExportWindowAt, LabelIn(exportW))),
            }, Gap: 6f),
        }, Gap: 6f);
    }

    // "Delete step" is recessed + inert on the Start anchor (Steps[0] can never be deleted) and when there are no real steps.
    private bool CanDeleteStep() => _currentStep >= 1 && _currentStep < (ActivePreset()?.Steps.Count ?? 0);

    // Real steps (Steps[0] is the blank Start anchor), capped at MaxDots.
    private int DotCount() => Math.Min(MaxDots, Math.Max(0, (ActivePreset()?.Steps.Count ?? 1) - 1));

    // Step dots: small FIXED 18×6 pills, 4 px apart, hugging the right of the title row. done = accent @45 %, current =
    // accent, the rest = a dark track tone (Text @15 % over the panel).
    // ⚠️ Not a horizontal ListElement: its List and Slot containers are column layouts with childForceExpandWidth, which
    // bubbles flexibleWidth = 1 up — the list took the row's slack and every slot stretched its dot into a long bar
    // (seen in-game 2026-11-12). A Row of no-else Conditionals instead: each Cond container clamps its flexibleWidth to 0
    // and a hidden one is deactivated whole (no gap left behind), so the row hugs exactly count × 18 + gaps.
    private HudElement BuildDots()
    {
        var dots = new HudElement[MaxDots];
        for (int i = 0; i < MaxDots; i++)
        {
            int step = i + 1;   // dot i = real step i + 1 (Steps[0] is the blank Start anchor)
            dots[i] = new ConditionalElement(() => step <= DotCount(), new BubbleElement(new SpacerElement(DotW, DotH))
            {
                FillColorFn = () => Fade(step < _currentStep ? Over(Menu.MenuAccent with { A = 0.45f }, PanelColor())
                                       : step == _currentStep ? Menu.MenuAccent
                                       : Over(Menu.MenuText with { A = 0.15f }, PanelColor())),
                CornerRadius = RoundRadius(DotH), PaddingX = 0f, PaddingY = 0f,
            });
        }
        return new RowElement(dots, Gap: 4f);
    }

    // No active preset: a faint outlined box filling the slot, text centred. (The prototype's DASHED edge isn't drawable —
    // BubbleElement only has a solid 1 px outline.)
    private HudElement BuildStepsEmpty() => new BubbleElement(new RowElement(new HudElement[]
    {
        new SpacerElement(0.01f, StepsBodyH - 2f),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T("rm.marks.emptyTitle"), Color: () => TextColor(), Align: TextAlign.Center,
                NoWrap: true, FontSize: 13) { Bold = true },
            new TextElement(() => _loc.T("rm.marks.emptyBody"), Color: () => MutedColor(), Align: TextAlign.Center),
        }, Gap: 4f), Weight: 1f),
    }, Gap: 0.01f))
    {
        FillColor = Transparent, CornerRadius = TileRadius, PaddingX = 24f, PaddingY = 0f,
        OutlineColorFn = () => Fade(Over(Menu.MenuText with { A = 0.15f }, PanelColor())),
    };

    // The delete confirm (house "Confirmations": bold question + muted consequence + Danger / Neutral pair).
    private HudElement BuildDeleteConfirm()
    {
        float half = MathF.Floor((MarksInnerW - 20f - 8f) / 2f);   // inside the tile's 2×10 padding
        return new BubbleElement(new ColumnElement(new HudElement[]
        {
            new TextElement(ConfirmTitle, Color: () => TextColor(), NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true },
            new TextElement(ConfirmBody, Color: () => MutedColor()),
            new RowElement(new HudElement[]
            {
                FixedCell(half, DangerButton(() => _loc.T("rm.marks.confirm.delete"), ConfirmDeletePreset, LabelIn(half))),
                FixedCell(half, NeutralButton(() => _loc.T("rm.marks.confirm.cancel"), CancelDeletePreset, LabelIn(half))),
            }, Gap: 8f),
        }, Gap: 6f))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        };
    }
}
