using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// The on-me banner HUD (Plugin.MechanicAlerts.cs raises it). Up to 3 stacked banners, newest first, each two lines:
//   headline  "<MECHANIC> — YOU"   in the mechanic colour   (28 px × Banner size; default 1.5× = 42 px)
//   detail    "<what about it>  <countdown>"                 (20 px × Banner size)
//
// SIZE: the window uses Surface = HudOverlay, the only surface whose TextElement honours DynamicFontSize (re-read
// each refresh, no upper clamp — WindowBuilder.HudOverlay BuildTextHud / TextBinding.Apply). The default Menu surface
// bakes FontSize at build time (WindowBuilder.StyledText), so a live slider there would need a window rebuild.
// No Emphasis on these texts: on a CHANGING text Emphasis re-asserts its fixed ~15-20 px size every change
// (WindowBuilder-Patterns.md "Emphasis CLOBBERS DynamicFontSize") — the countdown would collapse. HudOverlay text
// is shadowed for legibility instead. Each line is a fixed-width (BannerW) centre-aligned cell, so the text stays
// horizontally centred at the default spot at ANY size; the borderless window is click-through (Passive), so its
// wide box is invisible.
//
// TEXT: always names the mechanic, localized. Headline = the row GROUP (e.g. "Half-Field Charge Target"); for a
// scene-generic group ("Tina · Mechanic") the label's first part is the headline instead ("Wudi-Slash Mark" — every
// locale keeps the " · " separator in those labels). Detail = the row LABEL minus anything the headline already says
// ("Left Charge", "Slash 3", "1F · Top Left", "x2"); dropped when it would only repeat the headline. Both empty →
// "#<buffId>" from the row key. The group check runs on the ENGLISH group; the text uses the localized group.
public sealed partial class Plugin
{
    private const float BannerW = 2400f;                   // fixed centred cell width (1440p canvas units)
    private float _alertScale = 1.5f;                      // "Banner size" (1–6×, 0.5 steps; default 1.5×)

    private sealed class Banner
    {
        public McOccurrence Occ = null!;
        public long TestUntil;
        public long UntimedUntil;          // untimed occurrence (no countdown): banner expiry tick, 0 = timed
        public string Headline = "", Detail = "";
    }
    private readonly List<Banner> _banners = new();

    // Groups that only name the scene, not the mechanic (upstream Tina's single "buffGroup").
    private static readonly HashSet<string> GenericGroups = new(StringComparer.OrdinalIgnoreCase) { "Tina · Mechanic" };

    private Banner MakeBanner(McOccurrence o, long testMs)
    {
        var (head, detail) = BannerText(o.Group ?? "", o.Label ?? "", o.Key ?? "");
        return new Banner
        {
            Occ = o, TestUntil = testMs > 0 ? Environment.TickCount64 + testMs : 0,
            // Untimed mechanic (no duration — e.g. Reef Duet colour): banner for AlertUntimedMs only, else it would
            // stay as long as the row (forever). A fresh occurrence (new instance) shows again.
            UntimedUntil = testMs <= 0 && !o.Row.HasTimer ? Environment.TickCount64 + AlertUntimedMs : 0,
            Headline = _loc.TFormat("rm.mech.banner.you", head), Detail = detail,
        };
    }

    // groupEn = the row's ENGLISH group (GenericGroups check); label = the already-localized row label.
    private (string Head, string Detail) BannerText(string groupEn, string label, string key)
    {
        groupEn = groupEn.Trim(); label = label.Trim();
        string group = McText.T(groupEn);
        string head = group, detail = label;
        if (groupEn.Length == 0 || GenericGroups.Contains(groupEn))
        {
            // Headline from the label: "Wudi-Slash Mark · Slash 3" → "Wudi-Slash Mark" + "Slash 3".
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
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false, Passive = true,
                Surface = SurfaceStyle.HudOverlay,
                ShouldRender = () => _alertOn && MechInWorld
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
        if (DangerBanner() != null) return true;
        foreach (var b in _banners) if (BannerLive(b)) return true;
        return false;
    }

    // Slot 0 = the danger-tile MOVE OFF banner while it applies, then the on-me banners below it.
    private Banner? BannerAt(int i)
    {
        var d = DangerBanner();
        if (d != null) { if (i == 0) return d; i--; }
        int k = 0;
        foreach (var b in _banners)
            if (BannerLive(b) && k++ == i) return b;
        return null;
    }

    // "<mechanic> — MOVE OFF <tile>": shown while the local player stands in a marked danger tile — Phase Mapping,
    // Edge-Mid Explosion or Corner Explosion (tracker LocalDanger* — cheap: computed in the 200 ms scan; precedence
    // Phase Mapping → Edge-Mid → Corner when tiles overlap). Plays the alert sound when it starts, at most every 3 s.
    private Banner? _dangerBanner;
    private string _dangerSig = "";              // key|cell the cached banner was built for ("" = none)
    private long _dangerSoundAt;

    private Banner? DangerBanner()
    {
        if (!_alertOn) return null;
        string key = _mechTracker.LocalDangerKey, cell = _mechTracker.LocalDangerCell;
        if (key.Length == 0) { _dangerSig = ""; return null; }
        string sig = key + "|" + cell;
        if (sig != _dangerSig || _dangerBanner == null)
        {
            bool starting = _dangerSig.Length == 0;
            _dangerSig = sig;
            _dangerBanner = new Banner
            {
                Occ = new McOccurrence { Key = key, Color = 3, Row = new McRow() },
                // Mechanic name via the shared rm.mech.n.* keys (McText.Names: phaseMapping / edgeMid / corner).
                Headline = _loc.TFormat("rm.mech.banner.moveOff", McText.T(_mechTracker.LocalDangerName)), Detail = cell,
            };
            long now = Environment.TickCount64;
            if (starting && _alertSound && now - _dangerSoundAt > 3000) { _dangerSoundAt = now; PlayAlertSound(); }
        }
        return _dangerBanner;
    }

    // Layout-edit sample; its text is composed live (localized) in SampleHeadline / DetailLine.
    private static readonly Banner SampleBanner = new();
    private string SampleHeadline => _loc.TFormat("rm.mech.banner.you", McText.T("Half-Field Charge Target"));

    // Live banner, or (slot 0 in layout edit with nothing live) a sample at the current size for positioning.
    private Banner? ShownAt(int i) =>
        BannerAt(i) ?? (i == 0 && _services.Windows.IsLayoutEditing ? SampleBanner : null);

    private string DetailLine(Banner b)
    {
        if (b == SampleBanner) return McText.T("Left Charge") + "  8.0s";
        string t = !b.Occ.Row.HasTimer ? "" : b.Occ.Row.IsHitNow ? _loc.T("rm.mech.now")   // release buff marked the hit
            : $"{b.Occ.Row.ShownRemainSec:0.0}s";                                      // hit-offset adjusted
        return b.Detail.Length == 0 ? t : t.Length == 0 ? b.Detail : $"{b.Detail}   {t}";
    }

    // Headline in bold via an inline rich-text tag — NOT Emphasis, which on the HudOverlay surface resets the
    // DynamicFontSize (WindowBuilder-Patterns.md). The tag wraps the FINAL string (after localization + formatting),
    // so a translation can never split or drop it. Cached per slot: the text lambda runs every refresh.
    private readonly string?[] _bannerHeadSrc = new string?[AlertSlots], _bannerHeadBold = new string?[AlertSlots];

    private string BoldHeadline(int slot, string text)
    {
        if (text.Length == 0) return "";
        if (!ReferenceEquals(_bannerHeadSrc[slot], text) && _bannerHeadSrc[slot] != text)
        {
            _bannerHeadSrc[slot] = text;
            _bannerHeadBold[slot] = "<b>" + text + "</b>";
        }
        return _bannerHeadBold[slot]!;
    }

    private HudElement BuildAlertHudRoot()
    {
        var slots = new HudElement[AlertSlots];
        for (int s = 0; s < AlertSlots; s++)
        {
            int i = s;
            slots[s] = new ConditionalElement(() => ShownAt(i) != null,
                new ColumnElement(new HudElement[]
                {
                    new TextElement(() => ShownAt(i) is { } h ? BoldHeadline(i, h == SampleBanner ? SampleHeadline : h.Headline) : "",
                        Color: () => (ColorRgba?)MechanicCalloutData.SlotColor(ShownAt(i) is { } b && b != SampleBanner ? b.Occ.Color : 3),
                        Width: BannerW, Align: TextAlign.Center, Shadow: true, NoWrap: true, ShadowDistance: 3)
                    { DynamicFontSize = () => (int)MathF.Round(28f * _alertScale) },
                    new TextElement(() => ShownAt(i) is { } b ? DetailLine(b) : "",
                        Color: () => (ColorRgba?)_services.Theme.Colors.HudText,
                        Width: BannerW, Align: TextAlign.Center, Shadow: true, NoWrap: true, ShadowDistance: 2)
                    { DynamicFontSize = () => (int)MathF.Round(20f * _alertScale) },
                }, Gap: 2f));
        }
        return new ColumnElement(slots, Gap: 12f) { Padding = 10 };
    }
}
