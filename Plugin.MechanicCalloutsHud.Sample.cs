using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// Layout-edit sample of the callout list card — the approved mockup's states (§1–3): local-first targets with the YOU
// tag, a long target list (2 name lines + "+N"), a target-less row, two two-phase rows (Spin Fan "Stops" → "Fire",
// Golem Line "Locks" → "Dash" — the drain restarts at the phase change) and an untimed row ("—", steady fill). Timers
// loop so the red ring (≤ 3 s; the fill stays the mechanic colour) shows. Fictional names only; every
// label / header is an existing localized mechanic name (McText) or phase key — no new text. Rebuilt on a language switch.
// Plus a ring sequence row (step pills Middle / Middle (current) / Inner, Purge timing — Plugin.MechanicCalloutsHud.Ring.cs)
// under its rm.mech.ring.safeHeader caption.
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
                new(_loc.T("rm.mech.ring.safeHeader")),
                new(MechTestRingRow()),
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
        // Ring sample: step 1 exploded, step 2 counting down its 10 s, step 3 waiting — restarts when step 2 explodes.
        if (now - _mechTestSeq.Start[1] >= _mechTestSeq.ExplodeMs) MechTestSeqRestart(now);
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

    // Ring sequence sample: safe Middle / Middle / Inner (ring 2/2/1, slot colours 1/1/0 as the tracker's RaidRings).
    private readonly McRingSeq _mechTestSeq = new() { Count = 3, ExplodeMs = 10_000 };

    private McRow MechTestRingRow()
    {
        _mechTestSeq.Ring[0] = 2; _mechTestSeq.Ring[1] = 2; _mechTestSeq.Ring[2] = 1;
        _mechTestSeq.Color[0] = 1; _mechTestSeq.Color[1] = 1; _mechTestSeq.Color[2] = 0;
        MechTestSeqRestart(Environment.TickCount64);
        return new McRow { Key = "raid:ring:sample", Label = "", Color = 0, Ring = _mechTestSeq };
    }

    private void MechTestSeqRestart(long now)
    {
        _mechTestSeq.Start[0] = now - 11_000;   // exploded
        _mechTestSeq.Start[1] = now;            // current
        _mechTestSeq.Start[2] = now + 3_000;    // upcoming (Purge waves spawn ~3 s apart)
    }
}
