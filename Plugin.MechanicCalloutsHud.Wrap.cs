using System;
using System.Collections.Generic;
using System.Text;

namespace Stellar.RaidManager;

// Manual wrap / truncate for the callout list (port of Experiment's Plugin.MechanicCalloutsHud.Scale.cs, b515ca4).
// The list draws on SurfaceStyle.HudOverlay (live "Text size"), and HudOverlay text NEVER wraps and is NOT clipped —
// the framework's MakeShadowedTextHud forces HorizontalWrapMode.Overflow even with NoWrap=false, and there is no
// RectMask2D. Unlike the Menu text this list used before, a long player-name list would run past the window edge and a
// long mechanic name into the timer. So:
//   • names are broken BETWEEN names (",\n") against the names column's estimated width (window width − padding −
//     fixed columns − row gaps; re-evaluated per refresh, so a window resize or Text size change re-wraps);
//   • an over-long mechanic label is shortened with "…" to the label column.
// Width is an ESTIMATE (no font metrics from a plugin): ≈0.58 em per Latin/Cyrillic glyph, 1 em from U+0E00 up
// (Thai, CJK, kana — this list is localized into Thai and Japanese). It errs wide, so a line breaks a little early
// rather than spilling. Rich-text tags (local-name accent, (safe)/(out) colour) are skipped when measuring.
// Row stride vs text height: rows are laid out by the VLG from the texts' preferred heights (≈1.15–1.2 em: 14 px body
// → ~17, 15 px header → ~18 at 1.0×), all scaled with Text size, inside a 21 px × scale stride budget; wrapped name
// lines draw from the 6-line wrap reserve (18 px × scale each) in the locked height, so rows don't overlap.
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

    // Names cell width: the window's live width minus the column padding, the fixed columns (swatch, label, two breaks,
    // timer) and the row's five gaps, all at the registered scale. Floored so a tiny window still wraps sensibly.
    private float MechNamesWidth()
    {
        float sc = _mechHudBuiltScale;
        float w = 0f;
        try { w = _mechHudWindow?.Rect.Width ?? 0f; } catch { }
        if (w <= 0f) w = MechHudMinWidth(sc);
        float fixedW = (MechHudSwatchCell + MechHudLabelW + 2 * MechHudBreak + MechHudTimerW + 5 * MechHudGap) * sc;
        return MathF.Max(60f * sc, w - 2 * MechHudPad - fixedW);
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
    private readonly float[] _mechLabelPx = new float[MechHudSlots];

    // The row label, shortened with "…" to the label column (cached per slot while label + font size are unchanged).
    private string MechLabel(int i)
    {
        string s = MechLineAt(i)?.Row?.Label ?? "";
        float px = MechFont(MechHudFont);
        if (_mechLabelText[i] != null && _mechLabelSrc[i] == s && _mechLabelPx[i] == px) return _mechLabelText[i]!;
        float max = MechHudLabelW * _mechHudBuiltScale;
        string t = s;
        if (EstWidth(t, px) > max)
        {
            while (t.Length > 1 && EstWidth(t + "…", px) > max) t = t.Substring(0, t.Length - 1);
            t = t.TrimEnd() + "…";
        }
        _mechLabelSrc[i] = s; _mechLabelPx[i] = px;
        return _mechLabelText[i] = t;
    }
}
