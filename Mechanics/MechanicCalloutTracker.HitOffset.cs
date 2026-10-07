using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Stellar.Abstractions.Services;
using static Stellar.RaidManager.MechanicCalloutData;

namespace Stellar.RaidManager;

// Countdown accuracy (from real Experiment [MechTimer] logs, raid 13023):
//   • CLOCK BIAS — at first sight every buff's CreateTime was ~300-600 ms AHEAD of CombatSnapshot.ServerNowMs. Running
//     median (last 15) of (create − serverNow) for buffs first seen within ~1 s of creation, clamped 0..1500 ms, is
//     added to "now" in the remain calc (Rows.ComputeSnapRemain).
//   • HIT OFFSET per MECHANIC — some mechanics land before their debuff expires (Share 829304 ended with ~1.3 s left)
//     while siblings in the same row group run to expiry (Mirage Decay 829307 → ~0 ms), so offsets are keyed per
//     mechanic, not per group:
//       key = "b<baseId>" for buff rows (row key starts with the buff id), "g_<group slug>" for rule rows.
//     Shown countdown = remain − offset (≥ 0); offset = the user's manual value if set (0 = no offset), else the
//     built-in default. The row still lives as long as the buff.
//     Defaults: 2.0 s for Share 829304 / Spread 829308 / Mirage Share 829305 / Mirage Spread 829309; 0 elsewhere
//     (Decay 829306 / Mirage Decay 829307 run to expiry in the logs).
//   ⚠ NO auto-learning (user decision): learned medians silently moved the countdown. The user tunes offsets by eye
//     in the Hit Offsets window; good values get baked into DefaultHit.
// Config (RaidManager "settings" section): mechhit_<key> manual seconds (e.g. mechhit_b829304,
// mechhit_g_charge_clone); missing/negative = default.
internal sealed partial class MechanicCalloutTracker
{
    public IConfigSection? Cfg { get; set; }

    private static readonly Dictionary<string, float> DefaultHit = new()
    {
        ["b829304"] = 2f, ["b829308"] = 2f, ["b829305"] = 2f, ["b829309"] = 2f,
    };

    private readonly Dictionary<string, float> _hitManual = new();
    // Mechanics seen live with / without a countdown (Hit Offsets window hides never-timed ones — offset meaningless).
    private readonly HashSet<string> _hitSeenTimed = new(), _hitSeenUntimed = new();
    // Rule groups that never show a countdown (untimed rows by construction).
    private static readonly HashSet<string> UntimedRuleGroups = new()
    {
        "Electromagnetic Ring Sequence", "Portal", "Pizza Danger Zone", "Ice/Sea Wave Safe Zone",
    };

    // False when the mechanic is known never to show a countdown: an untimed rule group, or only ever seen untimed.
    public bool HitMechanicTimed(string key, string group) =>
        !(key.StartsWith("g_", StringComparison.Ordinal) && UntimedRuleGroups.Contains(group))
        && !(_hitSeenUntimed.Contains(key) && !_hitSeenTimed.Contains(key));
    private readonly List<long> _biasSamples = new();
    public long ClockBiasMs { get; private set; }

    public static string HitSlug(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return sb.ToString();
    }

    public static string GroupHitKey(string group) => "g_" + HitSlug(group);

    // Mechanic key of a live row: buff rows → b<baseId>; rule rows → g_<group>.
    private static string MechKey(McRow r)
    {
        int n = 0;
        while (n < r.Key.Length && char.IsDigit(r.Key[n])) n++;
        return n > 0 ? "b" + r.Key.Substring(0, n) : GroupHitKey(r.Group);
    }

    public static float DefaultHitOffset(string key) => DefaultHit.TryGetValue(key, out float d) ? d : 0f;

    // The offset in effect: the manual value when set (0 = no offset), else the built-in default.
    public float HitOffset(string key)
    {
        if (_hitManual.TryGetValue(key, out float v)) return v;
        v = Cfg?.Get<float>("mechhit_" + key, -1f) ?? -1f;
        return _hitManual[key] = v < 0f ? DefaultHitOffset(key) : v;
    }

    public bool HitOffsetIsDefault(string key) => (Cfg?.Get<float>("mechhit_" + key, -1f) ?? -1f) < 0f;

    public void SetManualHitOffset(string key, float sec)
    {
        _hitManual[key] = sec;
        if (Cfg == null) return;
        Cfg.Set<float>("mechhit_" + key, sec);
        Cfg.Save();
    }

    // Back to the built-in default: −1 = "not set" (the config API has no key removal).
    public void ResetHitOffset(string key)
    {
        _hitManual.Remove(key);
        if (Cfg == null) return;
        Cfg.Set<float>("mechhit_" + key, -1f);
        Cfg.Save();
    }

    private float RowHitOffset(McRow r)
    {
        string key = MechKey(r);
        _hitSeenTimed.Add(key);
        return HitOffset(key);
    }

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
