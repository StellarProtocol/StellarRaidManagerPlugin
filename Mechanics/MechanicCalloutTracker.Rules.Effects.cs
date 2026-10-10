using System;
using System.Collections.Generic;
using System.Linq;

namespace Stellar.RaidManager;

// Rules that need a buff instance's PlayEffect / customize ids (McBuffEffectPatch): Tina's Wudi-Slash order and the
// Wasteland Court Void-Mark pairing. Ported from s3-tina-mindrealm/mechanics.ts addWudiSlashRows and
// s4-wasteland-court/mechanics.ts parsePairMark / groupComplementaryPairs / addPairRows.
internal sealed partial class MechanicCalloutTracker
{
    private int[] EffectIds(in McBuff b) =>
        b.BuffUuid != 0 && McBuffEffectPatch.TryGet(b.Target, b.BuffUuid, out var ids) ? ids : Array.Empty<int>();

    // ── Tina: Wudi-Slash mark with slash order ───────────────────────────────────────────────────────────────
    // 841509's FIRST PlayEffect effect id is the player's 0-indexed slash order; colour = order % 12 so the in-group
    // colour sort renders rows in slash order. No ids (yet) → "?" in amber, like upstream's null order.
    private void TinaWudiRows()
    {
        foreach (var b in _buffs)
        {
            if (b.BaseId != 841509) continue;
            var ids = EffectIds(b);
            int? order = ids.Length > 0 ? ids[0] : null;
            string n = order.HasValue ? (order.Value + 1).ToString() : "?";
            // Localized "Wudi-Slash Mark · Slash N" — every locale keeps the " · " separator (the on-me banner splits
            // a scene-generic group's label there: Plugin.MechanicAlerts.Banner.cs).
            var a = Upsert($"tina:wudi:{b.Target}", "Tina · Mechanic", McText.F("rm.mech.fmt.wudiSlash", n),
                           order.HasValue ? ((order.Value % 12) + 12) % 12 : 11, 2, b.Create, b.Dur);
            AddTarget(a, b.Target);
        }
    }

    // ── Wasteland Court: Void-Mark pairing (884659) ──────────────────────────────────────────────────────────
    private enum PairColor { White, Black }

    private sealed class PairMark
    {
        public PairColor[] Slots = new PairColor[3];
        public int LockIndex;              // 1..3
        public PairColor Target;
        public bool AlreadyMatched;
    }

    private const int PairLockOffset = 6;
    private static readonly int[] PairColorSlots = { 0, 2, 6, 8 };

    // Appearance code → (slot, colour): 1-3 = black in slot 1-3, 4-6 = white in slot 1-3.
    private static bool PairAppearance(int code, out int slot, out PairColor color)
    {
        slot = code is >= 1 and <= 3 ? code : code is >= 4 and <= 6 ? code - 3 : 0;
        color = code <= 3 ? PairColor.Black : PairColor.White;
        return slot != 0;
    }

    private static PairMark? ParsePairMark(int[] ids)
    {
        if (ids.Length < 4) return null;
        var m = new PairMark();
        for (int i = 0; i < 3; i++)
        {
            if (!PairAppearance(ids[i], out int slot, out var c) || slot != i + 1) return null;
            m.Slots[i] = c;
        }
        m.LockIndex = ids[3] - PairLockOffset;
        if (m.LockIndex < 1 || m.LockIndex > 3) return null;
        m.Target = m.Slots[m.LockIndex - 1];
        m.AlreadyMatched = m.Slots.All(s => s == m.Target);
        return m;
    }

    // Partner test: every UNLOCKED slot of `other` already shows `self`'s target colour, and vice versa.
    private static bool CopiesTargetFrom(PairMark self, PairMark other)
    {
        bool any = false;
        for (int i = 0; i < 3; i++)
        {
            if (i + 1 == self.LockIndex) continue;
            any = true;
            if (other.Slots[i] != self.Target) return false;
        }
        return any;
    }

    // "White [Black] White" — locked slot bracketed, colour words localized. (Upstream joins single CJK glyphs with no
    // separator; the words are space-separated here for readability.)
    private static string FormatPattern(PairMark m) => string.Join(" ",
        m.Slots.Select((c, i) => i + 1 == m.LockIndex ? $"[{McText.T(c.ToString())}]" : McText.T(c.ToString())));

    private void WastelandPairRows()
    {
        var marks = new Dictionary<long, PairMark>();
        long earliest = 0, dur = 25_000;
        foreach (var b in _buffs)
        {
            if (b.BaseId != 884659) continue;
            if (b.Create > 0 && (earliest == 0 || b.Create < earliest)) earliest = b.Create;
            if (b.Dur > dur) dur = b.Dur;
            var m = ParsePairMark(EffectIds(b));
            if (m != null) marks[b.Target] = m;
        }
        if (marks.Count == 0 || PairWindowClosed(earliest, dur)) return;

        // groupComplementaryPairs: walk unmatched marks in uuid-string order, pair each with the first free partner.
        var remaining = marks.Where(kv => !kv.Value.AlreadyMatched).Select(kv => kv.Key)
                             .OrderBy(u => u.ToString(), StringComparer.Ordinal).ToList();
        var used = new HashSet<long>();
        int pairIdx = 0;
        foreach (long u in remaining)
        {
            if (used.Contains(u)) continue;
            long partner = remaining.FirstOrDefault(o => o != u && !used.Contains(o)
                && !marks[u].AlreadyMatched && !marks[o].AlreadyMatched
                && CopiesTargetFrom(marks[u], marks[o]) && CopiesTargetFrom(marks[o], marks[u]));
            if (partner == 0) continue;
            used.Add(u); used.Add(partner);
            bool uFirst = string.CompareOrdinal(u.ToString(), partner.ToString()) < 0;
            long l = uFirst ? u : partner, r = uFirst ? partner : u;
            var a = Upsert($"wl:pair:{l}:{r}", "Void-Mark Swap",
                           $"{FormatPattern(marks[l])} ↔ {FormatPattern(marks[r])}",
                           PairColorSlots[pairIdx++ % PairColorSlots.Length], 100, earliest, dur);
            AddTarget(a, l); AddTarget(a, r);
        }
        foreach (var kv in marks)
        {
            if (used.Contains(kv.Key)) continue;
            var m = kv.Value;
            int color = m.AlreadyMatched ? 1 : m.Target == PairColor.White ? 4 : 7;
            string label = m.AlreadyMatched ? "Already matched"
                : McText.F("rm.mech.fmt.pairTarget", FormatPattern(m), McText.T(m.Target.ToString()));
            var a = Upsert($"wl:pair:{kv.Key}", "Void-Mark Swap", label, color, 100, earliest, dur);
            AddTarget(a, kv.Key);
        }
    }

    // pairWindowClosed: a resolve (884660) or penalty (884661) buff is out, or the window (earliest create + duration)
    // has elapsed, or the resolve skill was cast (Rules.Casts.cs).
    private bool PairWindowClosed(long earliest, long dur)
    {
        if (AnyBuff(884660, out _) || AnyBuff(884661, out _)) return true;
        long now = ServerNowMs();
        if (earliest > 0 && now > 0 && now > earliest + dur) return true;
        return PairResolveCastSeen(earliest);
    }
}
