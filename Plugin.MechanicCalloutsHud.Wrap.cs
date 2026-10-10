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
// wrap reserve (18 px × scale each) in the max height (smaller heights collapse into "+N" — Overflow.cs).
public sealed partial class Plugin
{
    private static float EstWidth(string s, float px)
    {
        float em = 0f;
        for (int i = 0; i < s.Length; i++)
        {
            int tag = RichTagLen(s, i);
            if (tag > 0) { i += tag - 1; continue; }
            em += s[i] < 0x0E00 ? 0.58f : 1f;   // Latin/Cyrillic-ish vs Thai/CJK/kana
        }
        return em * px;
    }

    // Length of the uGUI rich-text tag starting at s[i] (b / i / color / size, open or close), else 0. Only these
    // names count, so a literal "<" in a player name is still measured as text.
    private static int RichTagLen(string s, int i)
    {
        if (s[i] != '<') return 0;
        int end = s.IndexOf('>', i + 1);
        if (end < 0) return 0;
        var name = s.AsSpan(i + 1, end - i - 1).TrimStart('/');
        int eq = name.IndexOf('=');
        if (eq >= 0) name = name[..eq];
        return name is "b" or "i" or "color" or "size" ? end - i + 1 : 0;
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

    // Shorten to fit `max` px with a trailing "…" (no-op when it already fits). Tag-aware (a label may carry inline <b>
    // — Purge ring current step): a cut never lands inside a tag, a tag right before the cut is dropped, and any tag
    // the cut leaves open is closed after the "…".
    private static string Ellipsize(string s, float max, float px)
    {
        if (EstWidth(s, px) <= max) return s;
        float room = max - EstWidth("…", px), w = 0f;
        var sb = new StringBuilder();
        var open = new Stack<string>();
        for (int i = 0; i < s.Length; i++)
        {
            int tag = RichTagLen(s, i);
            if (tag > 0)
            {
                string t = s.Substring(i, tag);
                if (t[1] == '/') { if (open.Count > 0) open.Pop(); }
                else
                {
                    string name = t.Substring(1, tag - 2);             // "b" / "color=#fff" → "color"
                    int eq = name.IndexOf('=');
                    open.Push(eq >= 0 ? name.Substring(0, eq) : name);
                }
                sb.Append(t);
                i += tag - 1;
                continue;
            }
            float cw = (s[i] < 0x0E00 ? 0.58f : 1f) * px;
            if (w + cw > room && sb.Length > 0) break;
            sb.Append(s[i]);
            w += cw;
        }
        string head = sb.ToString().TrimEnd();
        // Drop tags left dangling at the cut ("…<b>" opens nothing visible) — then close what is still open.
        while (head.Length > 0 && head[^1] == '>')
        {
            int lt = head.LastIndexOf('<');
            if (lt < 0 || RichTagLen(head, lt) != head.Length - lt || head[lt + 1] == '/') break;
            head = head.Substring(0, lt).TrimEnd();
            if (open.Count > 0) open.Pop();
        }
        var res = new StringBuilder(head).Append('…');
        while (open.Count > 0) res.Append("</").Append(open.Pop()).Append('>');
        return res.ToString();
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
