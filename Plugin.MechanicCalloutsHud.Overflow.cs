using System;
using Stellar.Abstractions.Domain;

namespace Stellar.RaidManager;

// Callout list height + "+N" overflow (port of Experiment's Plugin.MechanicCalloutsHud.Overflow.cs). The window is
// height-resizable between MechHudMinH (padding + 2 rows with a name line and gap + the "+N" line, × Text size) and the
// worst-case budget MechHudH; the framework persists the size like the width and, on a Text-size re-register, its
// restore (WindowRenderer.SetRect) clamps the saved size into the new Min/Max. The list has no layout feedback (the
// plugin can't read uGUI preferred heights), so what fits is ESTIMATED with the same per-line strides the height budget
// uses — header/row 21, first name line 19, each extra wrapped name line 18, name gap 6 (× scale) — which are all a
// little taller than the real lines (≈20/17/16), so an estimate never lets a row be half-cut (an over-full column would
// squeeze the VLG and rows overlap).
// When everything doesn't fit: show as many COMPLETE lines as fit with room left for the "+N" line (N = hidden mechanic
// ROWS, headers not counted), drop a group header left dangling at the bottom, and hide the rest. The tracker sorts
// most urgent first, so the hidden ones are the least urgent. Lines past the 16-slot pool count as hidden too.
// "+N" is numerals only — no localization key needed.
public sealed partial class Plugin
{
    // "+N" overflow line: bold green, centred. Green as the TextElement base Color (no inline <color> — HudOverlay's
    // shadow twin blurs inline colour spans, same as MechNameBlue); bold via inline <b>, which is safe.
    private static readonly ColorRgba MechOverflowGreen = new(0x7C / 255f, 0xE3 / 255f, 0x8B / 255f, 1f);   // #7CE38B

    private float MechHudMinH(float s) =>
        2 * MechHudPad + (2 * (MechHudStride + MechHudNameStride + MechHudNameGap) + MechHudStride) * s;

    private long _mechFitTick = -1;
    private int _mechFitVisible, _mechFitHidden;

    // Lines shown (slots 0..Visible-1) and hidden mechanic rows ("+N"). Recomputed at most once per ms — every slot's
    // conditional polls it on the same refresh.
    private (int Visible, int Hidden) MechFit()
    {
        long now = Environment.TickCount64;
        if (now == _mechFitTick) return (_mechFitVisible, _mechFitHidden);
        _mechFitTick = now;
        // Geometry is at the registered scale, but fonts follow the slider live during the 300 ms re-register
        // debounce — estimate at the larger of the two so a growing font never overfills.
        float sc = MathF.Max(_mechHudBuiltScale, _mechHudScale);
        var lines = MechHudLines();
        int n = Math.Min(lines.Count, MechHudSlots);
        float avail = 0f;
        try { avail = _mechHudWindow?.Rect.Height ?? 0f; } catch { }
        if (avail <= 0f) avail = MechHudH(_mechHudBuiltScale);
        avail -= 2 * MechHudPad;

        float all = 0f;
        for (int k = 0; k < n; k++) all += MechLineH(k, sc) + (k < n - 1 ? MechGapH(k, sc) : 0f);
        if (n == lines.Count && all <= avail) return Fit(n, 0);

        // Overflow: complete lines + the gap below the last one (the "+N" line follows it) + the "+N" line itself.
        float used = MechHudStride * sc;
        int v = 0;
        while (v < n && used + MechLineH(v, sc) + MechGapH(v, sc) <= avail) { used += MechLineH(v, sc) + MechGapH(v, sc); v++; }
        if (v > 0 && lines[v - 1].Header != null) v--;   // no header with none of its rows under it
        int hidden = 0;
        for (int k = v; k < lines.Count; k++) if (lines[k].Row != null) hidden++;
        return Fit(v, hidden);
    }

    private (int, int) Fit(int visible, int hidden)
    {
        _mechFitVisible = visible; _mechFitHidden = hidden;
        return (visible, hidden);
    }

    // Estimated height of line k (no trailing name gap): a header / row stride, plus the name line and its wraps.
    private float MechLineH(int k, float sc)
    {
        float hgt = MechHudStride * sc;
        if (MechLineAt(k)?.Header == null && MechHasNames(k))
        {
            int extra = 0;
            foreach (char c in MechNames(k)) if (c == '\n') extra++;
            hgt += (MechHudNameStride + extra * 18f) * sc;
        }
        return hgt;
    }

    private float MechGapH(int k, float sc) => MechLineAt(k)?.Header == null && MechHasNames(k) ? MechHudNameGap * sc : 0f;

    private int _mechOverflowN = -1;
    private string _mechOverflowText = "";

    private string MechOverflowText()   // cached — the text poll runs every refresh
    {
        int hidden = MechFit().Hidden;
        if (hidden != _mechOverflowN) { _mechOverflowN = hidden; _mechOverflowText = $"<b>+{hidden}</b>"; }
        return _mechOverflowText;
    }
}
