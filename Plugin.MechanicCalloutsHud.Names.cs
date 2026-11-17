using System;
using System.Text;

namespace Stellar.RaidManager;

// The target-name lines under a callout row (approved design: small light-blue names, the red YOU tag first, at most 2
// lines, then "+N"; port of Experiment fd828ea — replaces the HudOverlay-era manual wrap/"…" of Wrap.cs). The framework
// doesn't report how many lines a text would wrap to, and a wrapping text inside the row would change the row height
// behind the fill strut — so the names are split HERE into two one-line texts against an ESTIMATED width (≈0.58 em per
// Latin glyph, 1 em from U+0E00 up — Thai, CJK, kana, this list is localized; errs wide so a line breaks a little early
// rather than spilling; rich-text tags of the "(safe)"/"(out)" status are skipped). Each line is also Ellipsize'd by
// the framework as a safety net.
//   line A = [YOU] + the local player's own suffix (" ×2", "(out)" — McRow.LocalTag) + as many names as fit
//   line B = the rest, as many as fit with room for " +N" kept, then " +N" for what didn't
// Re-split only when the names, the YOU state, the language or the width change (cached per slot; the tracker hands the
// names as one comma-joined string per row, so a split allocates only on a change).
public sealed partial class Plugin
{
    private readonly int[]     _mechNameLines = new int[MechHudSlots];
    private readonly bool[]    _mechNameYou   = new bool[MechHudSlots];
    private readonly string?[] _mechNameA = new string?[MechHudSlots], _mechNameB = new string?[MechHudSlots];
    private readonly string?[] _mechNameSrc = new string?[MechHudSlots];   // OtherNames the split was built from
    private readonly string?[] _mechNameTag = new string?[MechHudSlots];   // LocalTag the split was built from
    private readonly float[]   _mechNameW   = new float[MechHudSlots];     // line-A width it was built for
    private readonly StringBuilder _mechNameSb = new(128);

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

    // Length of the rich-text tag starting at s[i] (b / i / color / size, open or close), else 0. Only these names count,
    // so a literal "<" in a player name is still measured as text.
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

    // Inner width of a row's text column: window width minus the card and row insets (re-read per refresh → a resize
    // re-splits).
    private float MechRowInnerWidth()
    {
        float w = 0f;
        try { w = _mechHudWindow?.Rect.Width ?? 0f; } catch { }
        if (w <= 0f) w = MechHudDefWidth;
        return w - 2f * MathF.Round(MS(MechCardPadX)) - 2f * MathF.Round(MS(MechRowPad));
    }

    private void MechLayoutNames(int k, McRow row)
    {
        bool you = row.LocalName.Length > 0;
        string others = row.OtherNames, tag = you ? row.LocalTag : "";
        float px = MF(MechFontNames), gap = MS(MechRowGap);
        float lineB = MechRowInnerWidth() - MS(MechNameIndent) - gap;
        // The YOU tag text is localized; its width rides in lineA, so a language switch re-splits through it.
        float youW = you ? MathF.Min(EstWidth(MechYouText(), MF(MechFontYou)) + 2f * MathF.Round(MS(MechYouPadX)),
                                     MathF.Round(MS(MechYouMaxW))) + gap : 0f;
        float lineA = MathF.Round(lineB - youW);
        if (_mechNameA[k] != null && _mechNameYou[k] == you && _mechNameSrc[k] == others && _mechNameTag[k] == tag
            && _mechNameW[k] == lineA) return;
        _mechNameYou[k] = you; _mechNameSrc[k] = others; _mechNameTag[k] = tag; _mechNameW[k] = lineA;

        if (others.Length == 0)
        {
            _mechNameA[k] = tag; _mechNameB[k] = "";
            _mechNameLines[k] = you ? 1 : 0;
            return;
        }
        var names = others.Split(", ");
        float sep = EstWidth(", ", px);
        var sb = _mechNameSb.Clear();

        // Line A: the local suffix, then the first name always goes here (cut by Ellipsize if longer than the line).
        float used = 0f;
        if (tag.Length > 0) { sb.Append(tag); used = EstWidth(tag, px); }
        int j = 0;
        while (j < names.Length)
        {
            bool lead = sb.Length > 0;
            float w = EstWidth(names[j], px) + (lead ? sep : 0f);
            if (j > 0 && used + w > lineA) break;
            if (lead) sb.Append(", ");
            sb.Append(names[j]);
            used += w; j++;
        }
        _mechNameA[k] = sb.ToString();
        if (j >= names.Length) { _mechNameB[k] = ""; _mechNameLines[k] = 1; return; }

        // Line B: keep room for " +N" unless the name is the last one.
        float more = EstWidth(" +99", px);
        sb.Clear(); used = 0f;
        int first = j;
        while (j < names.Length)
        {
            float w = EstWidth(names[j], px) + (j > first ? sep : 0f);
            float reserve = j < names.Length - 1 ? more : 0f;
            if (used + w + reserve > lineB) break;
            if (j > first) sb.Append(", ");
            sb.Append(names[j]);
            used += w; j++;
        }
        if (j < names.Length) sb.Append(sb.Length > 0 ? " +" : "+").Append(names.Length - j);
        _mechNameB[k] = sb.ToString();
        _mechNameLines[k] = 2;
    }
}
