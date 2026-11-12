using System;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — live status strings (hub card, window header, STEPS caption / title) ───────────────────
//
// Every Func here is polled on each window refresh, so each keeps its last answer and re-formats ONLY when one of its
// inputs (active preset, its name, step cursor / count, placed-mark count, language) changes — no per-poll strings
// (Plugin-Performance-Rules §7). The hub card and the window header read the same state; the card shows the step
// ("“Boss 2” active · Step 2 / 5"), the header the board ("… · 6 marks on the field"), per the approved spec.
public sealed partial class Plugin
{
    private struct StatusKey
    {
        public int Active, Step, Steps, Presets, Placed;
        public string? Name, Lang;

        public bool Same(in StatusKey o) => Active == o.Active && Step == o.Step && Steps == o.Steps
            && Presets == o.Presets && Placed == o.Placed && ReferenceEquals(Name, o.Name)
            && string.Equals(Lang, o.Lang, StringComparison.Ordinal);
    }

    private StatusKey CurrentStatusKey()
    {
        var p = ActivePreset();
        return new StatusKey
        {
            Active = _activeIndex, Step = _currentStep, Steps = p != null ? p.Steps.Count - 1 : -1,
            Presets = _presets.Count, Placed = _placedCount, Name = p?.Name, Lang = _loc.Language,
        };
    }

    private StatusKey _cardKey = new() { Active = int.MinValue }, _headKey = new() { Active = int.MinValue };
    private string _cardStatus = string.Empty, _headStatus = string.Empty;

    // Hub card: "“X” active · Step 2 / 5" / "“X” active · Start" / "No preset active · 4 presets".
    private string MarksCardStatus()
    {
        var k = CurrentStatusKey();
        k.Placed = 0;   // the card doesn't show the board count — don't re-format on it
        if (k.Same(_cardKey)) return _cardStatus;
        _cardKey = k;
        _cardStatus = k.Name == null ? _loc.TFormat("rm.marks.status.none", k.Presets)
            : k.Step >= 1 ? _loc.TFormat("rm.marks.status.activeStep", k.Name, k.Step, k.Steps)
            : _loc.TFormat("rm.marks.status.activeStart", k.Name);
        return _cardStatus;
    }

    // Window header: "“X” active · 6 marks on the field" (count unknown outside a dungeon → just "“X” active").
    private string MarksHeaderStatus()
    {
        var k = CurrentStatusKey();
        k.Step = 0; k.Steps = 0;
        if (k.Same(_headKey)) return _headStatus;
        _headKey = k;
        _headStatus = k.Name == null ? _loc.TFormat("rm.marks.status.none", k.Presets)
            : k.Placed >= 0 ? _loc.TFormat("rm.marks.status.activeMarks", k.Name, k.Placed)
            : _loc.TFormat("rm.marks.status.active", k.Name);
        return _headStatus;
    }

    // Big step line: "Step 2 / 5" or "Start".
    private StatusKey _titleKey = new() { Active = int.MinValue };
    private string _stepTitle = string.Empty;

    private string StepTitle()
    {
        var k = CurrentStatusKey();
        k.Placed = 0; k.Presets = 0; k.Name = null; k.Active = 0;
        if (k.Same(_titleKey)) return _stepTitle;
        _titleKey = k;
        _stepTitle = k.Step >= 1 ? _loc.TFormat("rm.marks.step", k.Step, k.Steps) : _loc.T("rm.marks.start");
        return _stepTitle;
    }

    // Panel caption: "Steps · <preset>" / "Steps" / "Delete preset" (UpperCached upper-cases it once per change).
    private StatusKey _capKey = new() { Active = int.MinValue };
    private string _stepsCaption = string.Empty;

    private string StepsCaption()
    {
        var k = CurrentStatusKey();
        k.Placed = 0; k.Step = 0; k.Steps = ConfirmingDelete ? 1 : 0; k.Presets = 0;
        if (k.Same(_capKey)) return _stepsCaption;
        _capKey = k;
        _stepsCaption = ConfirmingDelete ? _loc.T("rm.marks.confirm.caption")
            : k.Name != null ? _loc.TFormat("rm.marks.stepsCaptionNamed", k.Name)
            : _loc.T("rm.marks.stepsCaption");
        return _stepsCaption;
    }
}
