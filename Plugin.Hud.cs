using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// Countdown / raid-warning HUD (approved redesign 2026-11-17, option A "Card" — _scratch/prototypes/
// raidmanager-countdown-hud-APPROVED.md). The cards themselves are in Plugin.Hud.Card.cs; this file is the window root.
//
// ⭐ Fonts and the ring width are FIXED AT BUILD on the Menu surface (a styled TextElement reads FontSize once; there is
// no DynamicFontSize on that path, and BubbleElement.OutlineWidth is build-time). The size settings are three discrete
// steps (0.5 / 0.75 / 1.0), so each card is built once PER SIZE, each variant a whole-card ConditionalElement gated on
// "shown AND this size": a size change is just a different branch on the next refresh — no window re-register (which
// would risk the saved position) and no live rebuild. Branches build lazily on first show, so an unused size costs
// nothing. The Conditional wraps the WHOLE card row (never inside the bubble — WindowBuilder-Patterns.md "Never put a
// ConditionalElement inside the bubble's measured child").
public sealed partial class Plugin
{
    // HUD layout editor (Shift+`): with nothing live, the overlay shows a static sample in its real look so it can be
    // placed — the countdown card at "10" with a full bar, plus (Raid warning style = Red overlay) a sample raid-warning
    // card, both at the user's sizes. A running countdown / live warning always wins; the sample is gone the moment the
    // editor closes. IsLayoutEditing is polled each refresh (~10 Hz), so no state or tick of our own.
    private bool HudSample => !_running && _goTimer <= 0d && !_rwVisible && _services.Windows.IsLayoutEditing;

    private bool CtCardShown => _running || _goTimer > 0d || HudSample;
    private bool RwCardShown => _rwVisible || (HudSample && !_rwUseIngameWarning);

    // The three Size steps (SizeToMult). Compared with == on purpose: the settings only ever store these exact values.
    private static readonly float[] HudSizes = { 0.5f, 0.75f, 1.0f };

    private HudElement BuildHudRoot()
    {
        var items = new List<HudElement>(HudSizes.Length * 2);
        foreach (var s in HudSizes)
            items.Add(new ConditionalElement(() => RwCardShown && RwScale == s, RaidWarningCard(s)));
        foreach (var s in HudSizes)
            items.Add(new ConditionalElement(() => CtCardShown && Scale == s, CountdownCard(s)));
        // Hidden no-else Conditionals leave the layout entirely (BuildConditional), so the gap only sits between two
        // cards actually on screen.
        return new ColumnElement(items, Gap: 12f);
    }
}
