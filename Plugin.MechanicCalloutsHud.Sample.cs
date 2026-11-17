using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// Layout-edit sample of the callout list card — the approved mockup's states (§1–3): local-first targets with the YOU
// tag, a long target list (2 name lines + "+N"), a target-less row, two two-phase rows (Spin Fan "Stops" → "Fire",
// Golem Line "Locks" → "Dash" — the drain restarts at the phase change) and an untimed row ("—", steady fill). Timers
// loop so every colour step (mechanic colour → Caution ≤ 5 s → Danger + ring ≤ 3 s) shows. Fictional names only; every
// label / header is an existing localized mechanic name (McText) or phase key — no new text. Rebuilt on a language switch.
public sealed partial class Plugin
{
    private List<McLine>? _mechTestLines;
    private string _mechTestLang = "";

    private IReadOnlyList<McLine> MechTestLines()
    {
        if (_mechTestLines == null || _mechTestLang != _loc.Language)
        {
            _mechTestLang = _loc.Language;
            string you = _loc.T("rm.mech.test.you");
            _mechTestLines = new List<McLine>
            {
                new(McText.T("Execution Sentence")),
                new(MechTestRow(McText.T("Execution Sentence") + " x2", 5, 9000, you, "Alice, Bob, Carol")),
                new(MechTestRow(McText.T("Phase Mapping"), 1, 15000, "", "")),
                new(McText.T("Spin Fan")),
                new(MechTestRow(McText.T("Spin Fan"), 0, 8000, "", "", "rm.mech.fmt.spinStops", "rm.mech.fmt.fire", 1.5f)),
                new(MechTestRow(McText.T("Golem Line"), 1, 20000, "", "Erin", "rm.mech.fmt.golemLocks", "rm.mech.fmt.golemDash", 3f)),
                // Long target list — exercises the 2-line cap and the "+N".
                new(MechTestRow(McText.T("Spread"), 0, 12000, you,
                    "Alice, Bob, Carol, Dave, Erin, Frank, Grace, Heidi, Ivan, Judy, Mallory, Niaj, Olivia, Peggy")),
                new(MechTestRow(McText.T("Divine Trick - Execution Sentence"), 3, 0, you, "")),   // untimed
            };
        }
        long now = Environment.TickCount64;
        foreach (var l in _mechTestLines)
            if (l.Row is { HasTimer: true } r && r.RemainSec <= 0f) { r.SnapTick = now; }
        return _mechTestLines;
    }

    private static McRow MechTestRow(string label, int color, long durMs, string local, string others,
        string phase1 = "", string phase2 = "", float splitSec = -1f) => new()
    {
        Key = label, Label = label, Color = color, DurationMs = durMs,
        SnapTick = Environment.TickCount64, SnapRemain = durMs > 0 ? durMs / 1000f : -1f,
        LocalName = local, OtherNames = others,
        Phase1 = phase1, Phase2 = phase2, PhaseSplitSec = splitSec,
    };
}
