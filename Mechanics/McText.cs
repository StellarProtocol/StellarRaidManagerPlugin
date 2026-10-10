using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// Localization bridge for the mechanic engine (RaidManager is localized — Lang/{en,ja,th,id,fil}.json, CLAUDE.md
// Rule 10). The tracker/minimap classes are plain internal classes without the plugin's _loc, so the plugin hands its
// ILocalization in once (Plugin.MechanicCallouts.cs InitMechanicCallouts).
//   T(english) — a known English mechanic / scene / arena name (McText.Names.cs) → the active language; anything else
//                passes through unchanged (labels composed from already-localized parts, single letters "A"/"B"/"C").
//   L(key)     — a plain catalog key ("rm.mech.safe").
//   F(key, …)  — a catalog template with positional {0}/{1} placeholders.
// Resolved at compose time each scan (≈ 5 Hz), so a live language switch shows on the next scan.
internal static partial class McText
{
    public static ILocalization? Loc;

    public static string T(string english) =>
        Loc != null && Names.TryGetValue(english, out var key) ? Loc.T(key) : english;

    public static string L(string key) => Loc != null ? Loc.T(key) : key;

    public static string F(string key, params object[] args) => Loc != null ? Loc.TFormat(key, args) : key;
}
