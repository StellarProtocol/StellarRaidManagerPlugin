using System;
using System.Collections.Generic;
using System.Text;

namespace Stellar.RaidManager;

// Manual wrap / truncate for the callout list (port of Experiment's Plugin.MechanicCalloutsHud.Scale.cs, b515ca4).
// The list draws on SurfaceStyle.HudOverlay (live "Text size"), and HudOverlay text NEVER wraps and is NOT clipped —
// the framework's MakeShadowedTextHud forces HorizontalWrapMode.Overflow even with NoWrap=false, and there is no
// RectMask2D. Unlike the Menu text this list used before, a long player-name list would run past the window edge and a
// long mechanic name into the timer. So:
//   • names are broken BETWEEN names (",\n") against the target-name line's estimated width (window width − padding −
//     name-line indent; re-evaluated per refresh, so a window resize or Text size change re-wraps);
//   • an over-long mechanic label is shortened with "…" to the space left of the right-pinned timer (window width −
//     padding − fixed columns), and a group header to the full inner width.
// Width is an ESTIMATE (no font metrics from a plugin): ≈0.58 em per Latin/Cyrillic glyph, 1 em from U+0E00 up
// (Thai, CJK, kana — this list is localized into Thai and Japanese). It errs wide, so a line breaks a little early
// rather than spilling. Rich-text tags (local-name accent, (safe)/(out) colour) are skipped when measuring.
// Row stride vs text height: rows are laid out by the VLG from the texts' preferred heights (≈1.15–1.2 em: 14 px body
// → ~17, 15 px header → ~18 at 1.0×), all scaled with Text size, inside a 21 px × scale stride budget;
// each row's target-name line draws from a 19 px × scale per-slot budget and extra wrapped name lines from the 4-line
// wrap reserve (18 px × scale each) in the locked height, so rows don't overlap.
public sealed partial class Plugin
{
    private static float EstWidth(string s, float px)
    {
        float em = 0f;
        bool tag = false;
        foreach (char c in s)
        {
            if (c == '<') { tag = true; continue; }
            if (tag) { if (c == '>') tag = false; continue; }
            em += c < 0x0E00 ? 0.58f : 1f;   // Latin/Cyrillic-ish vs Thai/CJK/kana
        }
        return em * px;
    }

    // The column's inner width: the window's live width minus the (unscaled) column padding.
    private float MechInnerWidth()
    {
        float w = 0f;
        try { w = _mechHudWindow?.Rect.Width ?? 0f; } catch { }
        if (w <= 0f) w = MechHudMinWidth(_mechHudBuiltScale);
        return w - 2 * MechHudPad;
    }

    // Target-name line width: inner width minus the line's left indent (the names sit on their own line under the
    // mechanic, so the swatch/label/timer columns don't take from it), at the registered scale. Floored so a tiny
    // window still wraps sensibly.
    private float MechNamesWidth() => MathF.Max(60f * _mechHudBuiltScale, MechInnerWidth() - MechHudNameIndent * _mechHudBuiltScale);

    // Mechanic label width: inner width minus the fixed swatch / gaps / break / timer columns (the label is the row's
    // Weight cell, so it gets exactly this).
    private float MechLabelWidth() => MathF.Max(60f * _mechHudBuiltScale, MechInnerWidth() - MechHudRowFixedW * _mechHudBuiltScale);

    // Shorten to fit `max` px with a trailing "…" (no-op when it already fits).
    private static string Ellipsize(string s, float max, float px)
    {
        if (EstWidth(s, px) <= max) return s;
        string t = s;
        while (t.Length > 1 && EstWidth(t + "…", px) > max) t = t.Substring(0, t.Length - 1);
        return t.TrimEnd() + "…";
    }

    // Greedy wrap between names: "<rich local>, Alice, Bob,\nCarol". Names are joined by ", " in the tracker.
    private static string WrapNames(string localRich, string others, float maxW, float px)
    {
        var parts = new List<string>();
        if (localRich.Length > 0) parts.Add(localRich);
        if (others.Length > 0) parts.AddRange(others.Split(", "));
        var sb = new StringBuilder();
        float line = 0f, sepW = EstWidth(", ", px);
        for (int k = 0; k < parts.Count; k++)
        {
            float w = EstWidth(parts[k], px);
            if (k > 0)
            {
                if (line + sepW + w > maxW) { sb.Append(",\n"); line = w; }
                else { sb.Append(", "); line += sepW + w; }
            }
            else line = w;
            sb.Append(parts[k]);
        }
        return sb.ToString();
    }

    private readonly string?[] _mechLabelSrc = new string?[MechHudSlots], _mechLabelText = new string?[MechHudSlots];
    private readonly float[] _mechLabelPx = new float[MechHudSlots], _mechLabelWidth = new float[MechHudSlots];

    // The row label, shortened with "…" to the space left of the timer (cached per slot while label + font size +
    // window width are unchanged).
    private string MechLabel(int i)
    {
        string s = MechLineAt(i)?.Row?.Label ?? "";
        float px = MechFont(MechHudFont), max = MathF.Round(MechLabelWidth());
        if (_mechLabelText[i] != null && _mechLabelSrc[i] == s && _mechLabelPx[i] == px && _mechLabelWidth[i] == max)
            return _mechLabelText[i]!;
        _mechLabelSrc[i] = s; _mechLabelPx[i] = px; _mechLabelWidth[i] = max;
        return _mechLabelText[i] = Ellipsize(s, max, px);
    }

    private readonly string?[] _mechHeadSrc = new string?[MechHudSlots], _mechHeadText = new string?[MechHudSlots];
    private readonly float[] _mechHeadPx = new float[MechHudSlots], _mechHeadWidth = new float[MechHudSlots];

    // Group header as "<b>Header</b>" (bold via rich text — HudOverlay ignores Emphasis/Bold), shortened with "…" to the
    // inner width (the min width is narrower than it was). Bold glyphs run ~5 % wider than the estimate's regular ones.
    private string MechHeader(int i)
    {
        string h = MechLineAt(i)?.Header ?? "";
        if (h.Length == 0) return "";
        float px = MechFont(MechHudHeaderFont), max = MathF.Round(MechInnerWidth());
        if (_mechHeadText[i] != null && _mechHeadSrc[i] == h && _mechHeadPx[i] == px && _mechHeadWidth[i] == max)
            return _mechHeadText[i]!;
        _mechHeadSrc[i] = h; _mechHeadPx[i] = px; _mechHeadWidth[i] = max;
        return _mechHeadText[i] = "<b>" + Ellipsize(h, max / 1.05f, px) + "</b>";
    }
}
