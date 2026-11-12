using System;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — on-screen NoticeTip feedback for activate / step navigation (split out of
// Plugin.Marks.cs to keep it under the 500-line cap; logic unchanged).
public sealed partial class Plugin
{
    // ── NoticeTip feedback (subtle, on-screen — mirrors the window status line) ────────────────────────────────────
    // These fire often (Activate + every Prev/Next/Reset), so keep them short and SILENT — deliberately NOT the
    // Special banner + DungeonVictory sound the /rw path uses. Fully guarded: a notice failure must never break
    // navigation or crash the game.
    private void ShowMarksNotice(string content)
    {
        try
        {
            _services.NoticeTips
                .Create(NoticeTipType.PopTip)       // neutral center pop-tip (not the /rw Special banner)
                .WithContent(content)
                .WithAudio(NoticeTipAudio.Silent)   // silent — nav tips fire too often to sound
                .WithDuration(2.0f)
                .Show();
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] notice tip failed: {ex.Message}"); }
    }

    // Error variant — used when a step hotkey is pressed with no preset active (a rare misuse, so a short center
    // pop-tip with a light error cue is warranted). Still fully guarded.
    private void ShowMarksError(string content)
    {
        try
        {
            _services.NoticeTips
                .Create(NoticeTipType.PopTip)       // same neutral pop-tip; audible error cue kept below
                .WithContent(content)
                .WithAudio(NoticeTipAudio.ErrorTip)
                .WithDuration(2.5f)
                .Show();
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] notice tip failed: {ex.Message}"); }
    }

    // Localized "resulting step" line for a nav op. Step 0 (the blank anchor) reads "Start"; a real step k reads
    // "Step k / N" where N == the real step count (Steps[0] is the anchor, so N = Steps.Count - 1).
    // A non-empty step comment is appended (notice.stepComment) so it shows mid-fight from the hotkeys too.
    private string MarksStepNotice(MarkPreset p)
    {
        if (_currentStep <= 0) return _loc.TFormat("rm.marks.notice.start", p.Name);
        string c = _currentStep < p.Steps.Count ? p.Steps[_currentStep].Comment ?? "" : "";
        return c.Length > 0
            ? _loc.TFormat("rm.marks.notice.stepComment", p.Name, _currentStep, p.Steps.Count - 1, c)
            : _loc.TFormat("rm.marks.notice.step", p.Name, _currentStep, p.Steps.Count - 1);
    }
}
