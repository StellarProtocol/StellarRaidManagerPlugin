using System;
using Stellar.Abstractions.Domain;

namespace Stellar.RaidManager;

// Shared by the callout list card (Plugin.MechanicCalloutsHud*.cs) and the on-me alert cards (Plugin.MechanicAlerts.Banner*.cs):
// the countdown text, its colour step and the card fill (approved redesign 2026-11-17,
// _scratch/prototypes/raidmanager-callouts-hud-APPROVED.md; port of Experiment fd828ea / 877eed8).
//
// Countdown format: ≥ 10 s whole seconds "12s", < 10 s one decimal "4.6s". Both are looked up in tables built once, so
// the ~10 Hz poll of every row / card never formats or allocates a string (Plugin-Performance-Rules.md §7). Invariant
// "." on purpose — the cards are a fixed-format readout, not prose (the old list used the current culture).
public sealed partial class Plugin
{
    private static readonly string[] MechTenths = BuildMechTenths();   // [k] = "k/10 s" for 0.0 .. 9.9
    private static readonly string[] MechWhole  = BuildMechWhole();    // [n] = "n s" for 0 .. 999
    private const string MechNoTimer = "—";      // untimed row
    private static readonly ColorRgba MechClear = new(0f, 0f, 0f, 0f);

    // The hit-offset window / release buff (McRow.IsHitNow). Localized; _loc.T hands back the catalog string (no alloc).
    private string MechNowText => _loc.T("rm.mech.now");

    private static string[] BuildMechTenths()
    {
        var a = new string[100];
        for (int i = 0; i < a.Length; i++) a[i] = (i / 10).ToString() + "." + (i % 10).ToString() + "s";
        return a;
    }

    private static string[] BuildMechWhole()
    {
        var a = new string[1000];
        for (int i = 0; i < a.Length; i++) a[i] = i.ToString() + "s";
        return a;
    }

    // 9.95 and up would round to "10.0" → switch to whole seconds there, never show a 3-digit decimal.
    private static string MechSecText(float v)
    {
        if (!(v > 0f)) v = 0f;   // also catches NaN
        if (v < 9.95f) return MechTenths[(int)MathF.Round(v * 10f)];
        int n = (int)v;
        return MechWhole[n < 10 ? 10 : n > 999 ? 999 : n];
    }

    // ON-ME ALERT CARDS only — colour step of a countdown: 0 = normal (> 5 s), 1 = theme Caution (≤ 5 s), 2 = theme
    // Danger (≤ 3 s, + red ring). Keyed on the DISPLAYED tenth (+0.05) so "3.0s" is already red and "5.0s" already amber.
    // The callout LIST rows no longer step their fill / timer colour (user 2026-11-17): they only use MechRingDue.
    private static int MechStep(float v) => v < 3.05f ? 2 : v < 5.05f ? 1 : 0;

    // List rows' red ring (row outline): same ≤ 3 s threshold as MechStep's step 2, so list ring and card ring agree.
    private static bool MechRingDue(float v) => v < 3.05f;

    private ColorRgba MechStepColor(int step, ColorRgba normal) => step == 2 ? Danger : step == 1 ? Caution : normal;

    // The card behind the list / an alert: the theme's menu background pulled 45 % toward black (the countdown card
    // recipe, Plugin.Hud.Card.cs), alpha as given. Struct maths on theme fields → theme-live with no allocation.
    private ColorRgba MechCardFill(float alpha) => Mix(Hud.MenuBackground, Black, CardDarken) with { A = alpha };

    // Shown countdown of a row and the drain fraction of the CURRENT phase (a phase change restarts the drain).
    // Returns false for an untimed row. NOW (hit window) = 0 s / empty drain.
    private static bool MechRowTime(McRow row, out float shown, out float frac)
    {
        shown = 0f; frac = 1f;
        if (!row.HasTimer) return false;
        if (row.IsHitNow) { frac = 0f; return true; }
        float total;
        if (row.IsPhased)
        {
            shown = row.PhaseRemainSec;
            total = row.InFirstPhase ? row.DurationMs / 1000f - row.OffsetSec - row.PhaseSplitSec : row.PhaseSplitSec;
        }
        else
        {
            shown = row.ShownRemainSec;
            total = row.DurationMs / 1000f - row.OffsetSec;
        }
        frac = total > 0.05f ? Math.Clamp(shown / total, 0f, 1f) : 0f;
        return true;
    }

    // Phase WORD of a two-phase row ("Locks" / "Dash", "Stops" / "Fire"). RM's phases are catalog FORMAT keys with the
    // seconds in them ("Locks {0}s" — the old list showed "Locks 3.1s" in the timer column); the card shows the word in
    // the label and only the seconds in the timer, so the word is the template minus its "{0}s" (every locale puts the
    // placeholder last: "固定 {0}s", "ล็อก {0}s" …). Cached per (key, language): built once, then a lookup per refresh.
    private readonly System.Collections.Generic.Dictionary<string, string> _mechPhaseWords = new();
    private string? _mechPhaseLang;

    private string MechPhaseWord(string key)
    {
        if (key.Length == 0) return "";
        var lang = _loc.Language;
        if (!string.Equals(lang, _mechPhaseLang, StringComparison.Ordinal)) { _mechPhaseWords.Clear(); _mechPhaseLang = lang; }
        if (_mechPhaseWords.TryGetValue(key, out var w)) return w;
        w = _loc.T(key).Replace("{0}s", "").Replace("{0}", "").Trim();
        _mechPhaseWords[key] = w;
        return w;
    }
}
