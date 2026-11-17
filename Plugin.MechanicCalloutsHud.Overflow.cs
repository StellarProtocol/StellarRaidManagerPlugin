using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// Callout list height + "+N more" overflow (port of Experiment fd828ea). The window is height-resizable between
// MechHudMinHeight (card padding + 2 two-name-line rows + the "+N more" line, × Text size) and the worst-case budget
// MechHudHeight; the framework persists the size like the width and, on a Text-size re-register, clamps the saved size
// into the new Min/Max.
// Every line's height is EXACT now (each text has a pinned TextElement.Height and a row is its strut — Hud.cs), so the
// fit uses the same numbers the layout does — no more over-estimated strides: header = MechHeaderH, row = 2·pad + line 1
// + name lines, plus the card's slot gap between lines. When everything doesn't fit: show as many COMPLETE lines as fit
// with room left for the "+N more" line (N = hidden mechanic ROWS, headers not counted), drop a group header left
// dangling at the bottom, hide the rest. The tracker's order decides which ones hide. Lines past the 16-slot pool count
// as hidden too.
public sealed partial class Plugin
{
    private float MechHudMinHeight =>
        2f * MathF.Round(MS(MechCardPadY)) + 2f * MechRowH(2) + MS(MechMoreH) + 2f * MechGap(MechSlotGap);

    private float MechRowH(int nameLines) =>
        2f * MathF.Round(MS(MechRowPad)) + MS(MechLine1H) + nameLines * MS(MechNameH);

    private float MechLineH(IReadOnlyList<McLine> lines, int k) =>
        lines[k].Header != null ? MS(MechHeaderH) : MechRowH(_mechNameLines[k]);

    private void MechComputeFit(IReadOnlyList<McLine> lines, int n)
    {
        float gap = MechGap(MechSlotGap);
        float avail = 0f;
        try { avail = _mechHudWindow?.Rect.Height ?? 0f; } catch { }
        if (avail <= 0f) avail = MechHudHeight;
        avail -= 2f * MathF.Round(MS(MechCardPadY));

        float all = 0f;
        for (int k = 0; k < n; k++) all += MechLineH(lines, k) + (k > 0 ? gap : 0f);
        if (n == lines.Count && all <= avail) { _mechVisible = n; _mechHidden = 0; return; }

        // Overflow: complete lines, each followed by a gap, then the "+N more" line itself.
        float used = MS(MechMoreH);
        int v = 0;
        while (v < n && used + MechLineH(lines, v) + gap <= avail) { used += MechLineH(lines, v) + gap; v++; }
        if (v > 0 && lines[v - 1].Header != null) v--;   // no header with none of its rows under it
        int hidden = 0;
        for (int k = v; k < lines.Count; k++) if (lines[k].Row != null) hidden++;
        _mechVisible = v; _mechHidden = hidden;
    }
}
