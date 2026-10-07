using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Stellar.RaidManager;

// Countdown accuracy (from real Experiment [MechTimer] logs, raid 13023):
//   • CLOCK BIAS — at first sight every buff's CreateTime was ~300-600 ms AHEAD of CombatSnapshot.ServerNowMs. Running
//     median (last 15) of (create − serverNow) for buffs first seen within ~1 s of creation, clamped 0..1500 ms, is
//     added to "now" in the remain calc (Rows.ComputeSnapRemain).
//   • HIT OFFSET per MECHANIC — some mechanics land before their debuff expires (Share 829304 ended with ~1.3 s left)
//     while siblings in the same row group run to expiry (Mirage Decay 829307 → ~0 ms), so offsets are keyed per
//     mechanic, not per group:
//       key = "b<baseId>" for buff rows (row key starts with the buff id), "g_<group slug>" for rule rows.
//     Shown countdown = remain − offset (≥ 0); the row still lives as long as the buff, and shows "NOW" while the
//     offset window runs (McRow.IsHitNow). In RaidManager the offsets are HARDCODED (HitOffsets below) — no user
//     adjustment, no config; tuning happens in the Experiment plugin's Hit Offsets window.
internal sealed partial class MechanicCalloutTracker
{
    // ⭐ THE hit-offset table (seconds before debuff expiry that the mechanic actually lands). Values are tuned in the
    // StellarExperimentPlugin (Hit Offsets window, by eye in real raids) and COPIED here — change them there first.
    // Keys: "b<buff base id>" for buff rows, "g_<group slug>" (GroupHitKey) for rule rows. Missing key = 0 (no offset).
    private static readonly Dictionary<string, float> HitOffsets = new()
    {
        ["b829304"] = 2f,   // Share
        ["b829308"] = 2f,   // Spread
        ["b829305"] = 2f,   // Mirage Share
        ["b829309"] = 2f,   // Mirage Spread
        ["b829307"] = 2f,   // Mirage Decay (user tuned by eye 2026-10-07)
        // Decay 829306 runs to expiry in the logs → 0; P3 floor Decay 829325 → 0 too (default).
    };

    private readonly List<long> _biasSamples = new();
    public long ClockBiasMs { get; private set; }

    private static string HitSlug(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return sb.ToString();
    }

    private static string GroupHitKey(string group) => "g_" + HitSlug(group);

    // Mechanic key of a live row: buff rows → b<baseId>; rule rows → g_<group> (English group, language-independent).
    private static string MechKey(McRow r)
    {
        int n = 0;
        while (n < r.Key.Length && char.IsDigit(r.Key[n])) n++;
        return n > 0 ? "b" + r.Key.Substring(0, n) : GroupHitKey(r.Group);
    }

    private static float RowHitOffset(McRow r) => HitOffsets.TryGetValue(MechKey(r), out float s) ? s : 0f;

    // First sight of a buff row: (create − serverNow) if it was created within ~1 s → bias sample.
    private void SampleClockBias(long createMs)
    {
        long now = _services.CombatSnapshot.ServerNowMs;
        if (now < 1_600_000_000_000L || createMs <= 0) return;
        long d = createMs - now;
        if (d < -1000 || d > 1500) return;
        _biasSamples.Add(Math.Clamp(d, 0, 1500));
        if (_biasSamples.Count > 15) _biasSamples.RemoveAt(0);
        ClockBiasMs = _biasSamples.OrderBy(x => x).ElementAt(_biasSamples.Count / 2);
    }
}
