namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — per-step comment (inline edit) ─────────────────────────────────────────────────────
//
// Each real step (1..N) of a preset can carry an optional free-text note (MarkStep.Comment, e.g. "P2 — stack west").
// Same label↔input swap as the preset rename (Plugin.Marks.Rename.cs): the step panel shows the CURRENT step's
// comment as a label; the pencil chip turns it into an input, the check chip (or Enter) commits. The row itself is
// built in Plugin.Marks.Ui.cs; this partial owns the edit state + commit. The blank Start anchor (Steps[0]) still
// shows the row (so the window height doesn't jump crossing Start↔Step 1) but read-only: CurrentStepComment() is ""
// there, the pencil is disabled, and EnterStepCommentEdit refuses — so it can't be annotated.
//
// Persistence rides on the existing presets JSON: Comment is a plain public prop on MarkStep, so System.Text.Json
// writes it with the rest of the step. Configs saved before this field existed simply lack it → the initializer's
// "" stands; an explicit JSON null is normalized to "" on load (LoadPresetsFromConfig).
public sealed partial class Plugin
{
    // _commentEditStep is a STEP INDEX into the active preset (-1 = not editing). Anything that moves the step cursor
    // or changes the step/preset lists cancels it (CancelStepCommentEdit), so a stale index can never write a comment
    // onto a different step than the one the user was looking at. _commentBuffer is kept live by OnChange because
    // InputElement submits on ENTER ONLY — the check chip has to read the typed text without an Enter.
    private int _commentEditStep = -1;
    private string _commentBuffer = "";

    // True only while the CURRENTLY SHOWN step is the one being edited (the cursor moving cancels, but belt-and-braces).
    private bool IsEditingStepComment() => _commentEditStep >= 1 && _commentEditStep == _currentStep;

    // Comment of the current step, "" for Start / no active preset / out of range.
    private string CurrentStepComment()
    {
        var p = ActivePreset();
        return p != null && _currentStep >= 1 && _currentStep < p.Steps.Count ? p.Steps[_currentStep].Comment ?? "" : "";
    }

    private void EnterStepCommentEdit()
    {
        var p = ActivePreset();
        if (p == null || _currentStep < 1 || _currentStep >= p.Steps.Count) return;   // Start anchor isn't annotatable
        _commentEditStep = _currentStep;
        _commentBuffer = p.Steps[_currentStep].Comment ?? "";
        _marksWindow?.MarkDirty();
    }

    // Commit the live _commentBuffer (check chip AND Enter both land here). Empty is allowed — it clears the comment.
    private void CommitStepComment()
    {
        var p = ActivePreset();
        int i = _commentEditStep;
        if (p != null && i >= 1 && i < p.Steps.Count)
        {
            p.Steps[i].Comment = _commentBuffer?.Trim() ?? "";
            ClearExportCode();   // comment changed → any exported code is stale
            SavePresetsToConfig();
            _marksStatus = _loc.TFormat("rm.marks.commentSaved", i);   // real step number == index (Steps[0] = anchor)
            _services.Log.Info($"[MarkPresets] '{p.Name}' step {i} comment set ({p.Steps[i].Comment.Length} chars)");
        }
        CancelStepCommentEdit();
    }

    // Drop any in-progress edit WITHOUT saving. Called from every cursor/list mutation in Plugin.Marks.cs.
    private void CancelStepCommentEdit()
    {
        if (_commentEditStep < 0) return;
        _commentEditStep = -1;
        _commentBuffer = "";
        _marksWindow?.MarkDirty();
    }
}
