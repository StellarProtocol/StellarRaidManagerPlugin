using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// The on-me alert HUD (Plugin.MechanicAlerts.cs raises it) — approved redesign 2026-11-17 (raidmanager-callouts-hud.html
// §5; port of Experiment 877eed8): up to 3 stacked RINGED CARDS, newest on top, same family as the callout list card.
// Each card:
//   small caption (mechanic colour)        "ON YOU · <GROUP>"  /  mechanic name (MOVE)  /  danger-tile name (MOVE OFF, red)
//   large action text                       detail ("Left Charge")  /  "MOVE"  /  "MOVE OFF"
//   large right-aligned timer               same format + colour steps as the list; none when untimed (MOVE OFF)
// Ring = the mechanic colour, theme Danger at ≤ 3 s and always for MOVE OFF. The card itself is in
// Plugin.MechanicAlerts.Banner.Card.cs.
//
// TEXT: always names the mechanic, localized. Head = the row GROUP (e.g. "Half-Field Charge Target"); for a
// scene-generic group ("Tina · Mechanic") the label's first part is the head instead ("Wudi-Slash Mark" — every locale
// keeps the " · " separator in those labels). Detail = the row LABEL minus anything the head already says ("Left
// Charge", "Slash 3", "1F · Top Left", "x2"); the head when that leaves nothing. Both empty → "#<buffId>" from the row
// key. The group check runs on the ENGLISH group; the text uses the localized group. Captions / actions are built once
// per banner (a language switch applies to the next banner).
public sealed partial class Plugin
{
    private const float BannerW = 2400f;                   // window width (1440p canvas units); cards centre in it
    private float _alertScale = 1.5f;                      // "Banner size" (1–6×, 0.5 steps; default 1.5×)

    private sealed class Banner
    {
        public McOccurrence Occ = null!;
        public long TestUntil;
        public long UntimedUntil;          // untimed occurrence (no countdown): banner expiry tick, 0 = timed
        public string Caption = "", Action = "";
        // Two-phase on-me rows (Golem Line): the action carries the phase word, picked per refresh (cached strings).
        public string ActionP1 = "", ActionP2 = "";
        public int  Color;                 // MechanicCalloutData slot of the caption + ring
        public bool DangerTile;            // MOVE OFF: Danger caption + ring, never a timer
        public bool Sample;                // layout-edit sample: fixed "8.0s"
        public string ActionNow => ActionP1.Length > 0 && Occ.Row.IsPhased ? (Occ.Row.InFirstPhase ? ActionP1 : ActionP2) : Action;
    }
    private readonly List<Banner> _banners = new();

    // Groups that only name the scene, not the mechanic (upstream Tina's single "buffGroup").
    private static readonly HashSet<string> GenericGroups = new(StringComparer.OrdinalIgnoreCase) { "Tina · Mechanic" };

    private Banner MakeBanner(McOccurrence o, long testMs)
    {
        var (head, detail) = BannerText(o.Group ?? "", o.Label ?? "", o.Key ?? "");
        string action = detail.Length > 0 ? detail : head;
        var b = new Banner
        {
            Occ = o, TestUntil = testMs > 0 ? Environment.TickCount64 + testMs : 0,
            // Untimed mechanic (no duration — e.g. Reef Duet colour): banner for AlertUntimedMs only, else it would
            // stay as long as the row (forever). A fresh occurrence (new instance) shows again.
            UntimedUntil = testMs <= 0 && !o.Row.HasTimer ? Environment.TickCount64 + AlertUntimedMs : 0,
            Caption = _loc.TFormat("rm.mech.alert.onYou", head).ToUpperInvariant(), Action = action, Color = o.Color,
        };
        if (o.Row.IsPhased)
        {
            b.ActionP1 = action + " · " + MechPhaseWord(o.Row.Phase1);
            b.ActionP2 = action + " · " + MechPhaseWord(o.Row.Phase2);
        }
        return b;
    }

    // groupEn = the row's ENGLISH group (GenericGroups check); label = the already-localized row label.
    private (string Head, string Detail) BannerText(string groupEn, string label, string key)
    {
        groupEn = groupEn.Trim(); label = label.Trim();
        string group = McText.T(groupEn);
        string head = group, detail = label;
        if (groupEn.Length == 0 || GenericGroups.Contains(groupEn))
        {
            // Head from the label: "Wudi-Slash Mark · Slash 3" → "Wudi-Slash Mark" + "Slash 3".
            int dot = label.IndexOf(" · ", StringComparison.Ordinal);
            head = dot > 0 ? label.Substring(0, dot) : label;
            detail = dot > 0 ? label.Substring(dot + 3) : "";
        }
        else if (label.StartsWith(group, StringComparison.OrdinalIgnoreCase))
            detail = label.Substring(group.Length).Trim(' ', '-', '·');   // "Execution Sentence x2" → "x2"
        if (string.Equals(detail, head, StringComparison.OrdinalIgnoreCase)) detail = "";
        if (head.Length == 0)
        {
            int n = 0;
            while (n < key.Length && char.IsDigit(key[n])) n++;
            head = n > 0 ? "#" + key.Substring(0, n) : group.Length > 0 ? group : _loc.T("rm.mech.banner.mechanic");
        }
        return (head, detail);
    }

    private void RegisterMechAlertHud()
    {
        _mechAlertHud = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "raidmanager.mech.alert",
                Title:       _loc.T("rm.mech.hud.alert"),
                // Constant 1440p-calibrated rect: a 2400-wide strip centred on the 2560 canvas, upper third.
                DefaultRect: new WindowRect(80f, 150f, BannerW, 0f),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                // Menu surface (the default): the card, its ring and Bold sized text only exist there (Banner.Card.cs).
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false, Passive = true,
                ShouldRender = () => (_alertOn || AlertTestRunning) && MechInWorld
                                  && (_services.ClientState.UiState & GameUIState.Blocking) == 0
                                  && (_services.Windows.IsLayoutEditing || AnyBanner()),
            },
            Root:    BuildAlertHudRoot(),
            OnClose: () => { }));
        _mechWindows.Add(_mechAlertHud);   // DisposeMechanicCallouts Remove()s each
        _mechAlertHud.SetVisible(true);
    }

    private bool AnyBanner()
    {
        if (DangerBanner() != null || RingBanner() != null || SpinBanner() != null) return true;
        foreach (var b in _banners) if (BannerLive(b)) return true;
        return false;
    }

    // Slot 0 = the danger-tile MOVE OFF banner while it applies, then the ring MOVE banner, then the on-me banners.
    private Banner? BannerAt(int i)
    {
        var d = DangerBanner();
        if (d != null) { if (i == 0) return d; i--; }
        var r = RingBanner();                                   // ring MOVE banner next (Plugin.MechanicAlerts.Ring.cs)
        if (r != null) { if (i == 0) return r; i--; }
        var sp = SpinBanner();                                  // Spin Fan MOVE banner next (Plugin.MechanicAlerts.Spin.cs)
        if (sp != null) { if (i == 0) return sp; i--; }
        int k = 0;
        foreach (var b in _banners)
            if (BannerLive(b) && k++ == i) return b;
        return null;
    }

    // "MOVE OFF" card: shown while the local player stands in a marked danger tile — Phase Mapping, Edge-Mid Explosion
    // or Corner Explosion (tracker LocalDanger* — cheap: computed in the 200 ms scan; precedence Phase Mapping →
    // Edge-Mid → Corner when tiles overlap). Rebuilt when the tile key or cell changes (compared field by field — the
    // poll runs every refresh, so no per-poll signature string). Plays the alert sound when it starts, at most every 3 s.
    private Banner? _dangerBanner;
    private string _dangerKey = "", _dangerCell = "";   // what the cached banner was built for ("" = none)
    private long _dangerSoundAt;

    private Banner? DangerBanner()
    {
        if (!_alertOn) return null;
        string key = _mechTracker.LocalDangerKey, cell = _mechTracker.LocalDangerCell;
        if (key.Length == 0) { _dangerKey = ""; return null; }
        if (key != _dangerKey || cell != _dangerCell || _dangerBanner == null)
        {
            bool starting = _dangerKey.Length == 0;
            _dangerKey = key; _dangerCell = cell;
            _dangerBanner = new Banner
            {
                Occ = new McOccurrence { Key = key, Color = 3, Row = new McRow() },
                // Mechanic name via the shared rm.mech.n.* keys (McText.Names: phaseMapping / edgeMid / corner).
                Caption = McText.T(_mechTracker.LocalDangerName).ToUpperInvariant(), Action = _loc.T("rm.mech.alert.moveOff"),
                Color = 3, DangerTile = true,
            };
            long now = Environment.TickCount64;
            if (starting && _alertSound && now - _dangerSoundAt > 3000) { _dangerSoundAt = now; PlayAlertSound(); }
        }
        return _dangerBanner;
    }

    // Layout-edit sample at the current size, for positioning — rebuilt on a language switch.
    private Banner? _sampleBanner;
    private string? _sampleLang;

    private Banner SampleBanner()
    {
        var lang = _loc.Language;
        if (_sampleBanner == null || !string.Equals(lang, _sampleLang, StringComparison.Ordinal))
        {
            _sampleLang = lang;
            _sampleBanner = new Banner
            {
                Occ = new McOccurrence { Row = new McRow() },
                Caption = _loc.TFormat("rm.mech.alert.onYou", McText.T("Half-Field Charge Target")).ToUpperInvariant(),
                Action = McText.T("Left Charge"), Color = 5, Sample = true,
            };
        }
        return _sampleBanner;
    }

    // Live banner, or (slot 0 in layout edit with nothing live) a sample at the current size for positioning.
    private Banner? ShownAt(int i) =>
        BannerAt(i) ?? (i == 0 && _services.Windows.IsLayoutEditing ? SampleBanner() : null);
}
