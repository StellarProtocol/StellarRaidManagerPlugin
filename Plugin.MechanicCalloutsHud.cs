using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// The Mechanic Callouts HUD list (data: MechanicCalloutTracker). A borderless HUD-category window over a FIXED pool of
// line slots (the element tree is fixed at registration — WindowBuilder-Patterns.md "pre-allocate slots"); each slot
// shows either a group header or a row `[■] <label>  <countdown>  <you>, <others>` from the tracker's flattened Lines.
// Renders only in-world, when enabled AND there are rows; in layout-edit mode (enabled) it shows sample rows to
// position (the only preview path). No BringToFront: it opens from hidden via ShouldRender and lands on top within its ZCat
// naturally (CLAUDE.md rule 5).
//
// TEXT SIZE ("Text size" slider, config mech_textscale, 0.75–2.0×, default 1.0×) scales everything in the list: header /
// label / timer / names fonts, the colour swatch, row stride, column widths (label 300, timer, gaps) and the window's
// width, min width and locked height. Two halves:
//   • fonts: Surface = HudOverlay — the only surface that honours FontSize/DynamicFontSize (a Menu-surface TextElement
//     ignores FontSize, WindowBuilder-Patterns.md) — with DynamicFontSize re-read every refresh, so text follows the
//     slider LIVE. No Emphasis on these changing texts (it clobbers DynamicFontSize on HudOverlay); HudOverlay also
//     ignores Bold, so the group header is bold via an inline <b> rich-text tag.
//   • geometry: column widths, swatch size and the Resizable window's MinHeight==MaxHeight lock are baked at
//     registration and can't change at runtime → the window is REMOVED and RE-REGISTERED with the same id (the
//     framework restores its saved position) once the slider has been still for 300 ms (debounced, not per drag tick).
public sealed partial class Plugin
{
    private IWindowControl? _mechHudWindow;          // null while removed (list toggle off — ApplyMechHudWindows)
    private const int MechHudSlots = 16;   // headers + rows; upstream panels rarely list more than a handful

    // Width-resizable: Resizable turns OFF the Borderless content-fit on BOTH axes and fixes the height (no width-only
    // option in WindowSpec), so the height is locked (MinHeight==MaxHeight) at a worst-case budget instead: padding +
    // every slot at ~21f stride + room for ~6 wrapped name lines. It must not be too SHORT — under-height, the root
    // VLG squeezes children toward their minHeight (Text min = 0) and rows overlap; extra height is just empty space
    // below the top-stacked rows (root VLG is UpperLeft, childForceExpandHeight off).
    // Sizes at Text size 1.0× (canvas units); every one but the outer padding is multiplied by the scale.
    private const float MechHudPad = 20f /* column only — Passive root has none */, MechHudStride = 21f, MechHudWrapReserve = 6 * 18f;
    private const float MechHudLabelW = 300f;   // mechanic-name column (was 230; long th/fil names)
    private const float MechHudTimerW = 48f, MechHudSwatchCell = 14f, MechHudSwatch = 10f, MechHudBreak = 4f, MechHudGap = 6f;
    private const float MechHudMinW = 550f, MechHudMaxW = 1200f;   // +70 with the label column: timer + names keep their width
    private const int   MechHudFont = 14, MechHudHeaderFont = 15;  // the Menu surface's own body / emphasis sizes

    private float _mechHudScale = 1f;          // live "Text size" (fonts follow it at once)
    private float _mechHudBuiltScale = 1f;     // scale the window geometry was registered with
    private long  _mechHudRebuildAt;           // debounce deadline for the geometry re-register (0 = none pending)
    private IDisposable? _mechHudRebuildTick;

    private float MechHudH(float s) => 2 * MechHudPad + (MechHudSlots * MechHudStride + MechHudWrapReserve) * s;
    private float MechHudMinWidth(float s) => 2 * MechHudPad + (MechHudMinW - 2 * MechHudPad) * s;
    private int   MechFont(int basePx) => Math.Max(8, (int)MathF.Round(basePx * _mechHudScale));

    private void RegisterMechCalloutHud()
    {
        float s = _mechHudBuiltScale = _mechHudScale;
        float minW = MechHudMinWidth(s), h = MechHudH(s);
        _mechHudWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "raidmanager.mech.hud",
                Title:       _loc.T("rm.mech.hud.list"),
                // Constant 1440p-calibrated rect (never ScreenWidth-derived — "reset all HUD" reads ScreenWidth 0).
                DefaultRect: new WindowRect(40f, 420f, minW, h),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false,
                Surface = SurfaceStyle.HudOverlay,   // live DynamicFontSize (Text size)
                // Extra width goes only to the names column (the one Weight cell); swatch/label/timer are fixed.
                Resizable = true, MinWidth = minW, MaxWidth = MathF.Max(MechHudMaxW, minW), MinHeight = h, MaxHeight = h,
                // Passive: the Borderless root carries a full-rect invisible raycast blocker, and with the locked tall
                // height most of it is EMPTY — it would eat game clicks/camera drags over a big blank area mid-fight.
                // Passive drops that blocker (pure info HUD, nothing clickable). Edit-mode drag/resize hit-test rects
                // geometrically (WindowInteractionTicker), not via raycasts, so layout editing still works. Passive
                // also zeroes the root's 12f padding — the column's Padding (MechHudPad = 20) restores the old 12+8.
                // Needs Stellar.Abstractions ≥ 2.16.0 (WindowSpec.Passive).
                Passive = true,
                ShouldRender = () => _mechEnabled && MechInWorld
                                  && (_services.ClientState.UiState & GameUIState.Blocking) == 0
                                  && (_services.Windows.IsLayoutEditing || _mechTracker.RowCount > 0),
            },
            Root:    BuildMechHudRoot(s),
            OnClose: () => { }));
        _mechWindows.Add(_mechHudWindow);   // DisposeMechanicCallouts Remove()s each
        _mechHudWindow.SetVisible(true);   // always "shown"; ShouldRender does the real gating
    }

    // Live rows when there are any; otherwise the sample list while the layout editor wants something to place.
    private IReadOnlyList<McLine> MechHudLines()
    {
        if (_mechTracker.RowCount > 0) return _mechTracker.Lines;
        return _services.Windows.IsLayoutEditing ? MechTestLines() : _mechTracker.Lines;
    }

    private McLine? MechLineAt(int i)
    {
        var lines = MechHudLines();
        return i < lines.Count ? lines[i] : null;
    }

    // Text size changed: fonts follow at once; the geometry re-register waits until the slider is still for 300 ms.
    private void SetMechHudScale(float v)
    {
        _mechHudScale = Math.Clamp(MathF.Round(v * 20f) / 20f, 0.75f, 2f);   // 0.05 steps
        _cfg.Set<float>("mech_textscale", _mechHudScale);
        _cfg.Save();
        _mechHudRebuildAt = Environment.TickCount64 + 300;
        _mechHudRebuildTick ??= _services.Framework.Every(TimeSpan.FromMilliseconds(100), MechHudRebuildTick);
    }

    private void MechHudRebuildTick()
    {
        if (Environment.TickCount64 < _mechHudRebuildAt) return;
        _mechHudRebuildTick?.Dispose(); _mechHudRebuildTick = null;
        if (MathF.Abs(_mechHudScale - _mechHudBuiltScale) < 0.001f) return;
        // List turned off inside the debounce ⇒ the window is already removed; don't resurrect it (it is rebuilt at the
        // current scale when the toggle comes back on).
        if (_mechHudWindow == null) return;
        RemoveMechHud(ref _mechHudWindow);
        RegisterMechCalloutHud();   // same id → saved position restored
    }

    private HudElement BuildMechHudRoot(float sc)
    {
        var slots = new HudElement[MechHudSlots];
        for (int s = 0; s < MechHudSlots; s++)
        {
            int i = s;
            // Bold via rich text: HudOverlay ignores Emphasis/Bold styling, and Emphasis would clobber the dynamic size.
            var header = new TextElement(() => MechLineAt(i)?.Header is { } h ? "<b>" + h + "</b>" : "",
                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted, Shadow: true, NoWrap: true, FontSize: MechHudHeaderFont)
                { DynamicFontSize = () => MechFont(MechHudHeaderFont) };
            var row = new RowElement(new HudElement[]
            {
                new CellElement(new SwatchElement(() => MechRowColor(i), MechHudSwatch * sc), Width: MechHudSwatchCell * sc),
                // Label shortened with "…" to its column (HudOverlay text doesn't clip — Plugin.MechanicCalloutsHud.Wrap.cs).
                new CellElement(new TextElement(() => MechLabel(i),
                    Color: () => (ColorRgba?)_services.Theme.Colors.MenuText, Shadow: true, NoWrap: true, FontSize: MechHudFont)
                    { DynamicFontSize = () => MechFont(MechHudFont) }, Width: MechHudLabelW * sc),
                // Fixed 4f spacer + the 6f row gap on each side = a 16f break (at a bare 6f gap neighbouring columns
                // read as one run of text in game). Same break again between the timer and the names.
                new SpacerElement(MechHudBreak * sc),
                // Timer sits between label and names (names last) so a long name list can never run over it.
                new CellElement(new TextElement(() => MechTimer(i),
                    Color: () => (ColorRgba?)_services.Theme.Colors.MenuText, Shadow: true, NoWrap: true, FontSize: MechHudFont)
                    { DynamicFontSize = () => MechFont(MechHudFont) }, Width: MechHudTimerW * sc),
                new SpacerElement(MechHudBreak * sc),
                // Names LAST, filling the rest of the row. HudOverlay text never wraps by itself, so MechNames breaks
                // the list between names (",\n") against the cell's estimated width (Plugin.MechanicCalloutsHud.Wrap.cs);
                // the local name stays inline rich text (accent + bold) — two sibling Texts would both be squeezed.
                new CellElement(new TextElement(() => MechNames(i),
                    Color: () => (ColorRgba?)_services.Theme.Colors.MenuText, Shadow: true, FontSize: MechHudFont)
                    { DynamicFontSize = () => MechFont(MechHudFont) }, Weight: 1f),
            }, Gap: MechHudGap * sc);

            slots[s] = new ConditionalElement(() => MechLineAt(i) != null,
                new ConditionalElement(() => MechLineAt(i)?.Header != null, header, Else: row));
        }
        return new ColumnElement(slots, Gap: 3f * sc) { Padding = (int)MechHudPad };   // Passive root has 0 padding
    }

    private ColorRgba MechRowColor(int i)
    {
        var row = MechLineAt(i)?.Row;
        return row != null ? MechanicCalloutData.SlotColor(row.Color) : MechanicCalloutData.SlotColor(0);
    }

    // "<color=#accent>You</color>, Alice, Bob" — local player first in the theme accent. NOT bold: the font has no bold
    // face, so <b> is faux-bold (smeared copies) and with HudOverlay's drop shadow it looked blurry. uGUI Text has
    // supportRichText on by default (the framework only turns it off for text INPUTS). Cached per slot so the
    // per-refresh text poll doesn't allocate a new string every tick while nothing changed.
    private readonly string?[] _mechNameLocal = new string?[MechHudSlots], _mechNameOthers = new string?[MechHudSlots],
                               _mechNameText = new string?[MechHudSlots];
    private readonly ColorRgba[] _mechNameAccent = new ColorRgba[MechHudSlots];   // theme switch → re-colour
    private readonly float[] _mechNameWidth = new float[MechHudSlots], _mechNamePx = new float[MechHudSlots];   // re-wrap on resize / Text size

    private string MechNames(int i)
    {
        var row = MechLineAt(i)?.Row;
        if (row == null) return "";
        var c = _services.Theme.Colors.HudAccent;
        float width = MathF.Round(MechNamesWidth()), px = MechFont(MechHudFont);
        if (_mechNameText[i] != null && _mechNameLocal[i] == row.LocalName && _mechNameOthers[i] == row.OtherNames
            && _mechNameAccent[i] == c && _mechNameWidth[i] == width && _mechNamePx[i] == px) return _mechNameText[i]!;
        string local = row.LocalName.Length > 0
            ? $"<color=#{(int)(c.R * 255f):X2}{(int)(c.G * 255f):X2}{(int)(c.B * 255f):X2}>{row.LocalName}</color>" : "";
        _mechNameLocal[i] = row.LocalName; _mechNameOthers[i] = row.OtherNames; _mechNameAccent[i] = c;
        _mechNameWidth[i] = width; _mechNamePx[i] = px;
        return _mechNameText[i] = WrapNames(local, row.OtherNames, width, px);
    }

    // Countdown: hidden when the row has no duration (upstream hideTimer / durationMs ≤ 0).
    private string MechTimer(int i)
    {
        var row = MechLineAt(i)?.Row;
        if (row == null || !row.HasTimer) return "";
        if (row.IsHitNow) return _loc.T("rm.mech.now");   // boss release buff just marked the hit (tracker Release.cs)
        float r = row.ShownRemainSec;   // hit-offset adjusted (0 = the mechanic lands now)
        return r < 10f ? $"{r:0.0}s" : $"{(int)r}s";
    }

    // ── Test rows ────────────────────────────────────────────────────────────────────────────────────────────
    private List<McLine>? _mechTestLines;
    private string _mechTestLang = "";

    // A small sample panel (two groups, local-first targets, one untimed row). Timers loop so it looks alive.
    // Rebuilt on a language switch (labels/headers are localized when built).
    private IReadOnlyList<McLine> MechTestLines()
    {
        if (_mechTestLines == null || _mechTestLang != _loc.Language)
        {
            _mechTestLang = _loc.Language;
            string you = _loc.T("rm.mech.test.you");
            _mechTestLines = new List<McLine>
            {
                new(McText.T("Electromagnetic Pulse")),
                new(MechTestRow("A", 0, 8000, you, "Alice")),
                new(MechTestRow("B", 1, 8000, "", "Bob, Carol")),
                // Long target list — exercises the names-cell wrap (must not run over the timer / window edge).
                new(MechTestRow(McText.T("Spread"), 0, 12000, you, "Alice, Bob, Carol, Dave, Erin, Frank, Grace, Heidi")),
                new(McText.T("Execution Sentence")),
                new(MechTestRow(McText.T("Execution Sentence") + " x2", 1, 15000, "", "Dave")),
                new(MechTestRow(McText.T("Divine Trick - Execution Sentence"), 0, 0, you, "")),
            };
        }
        long now = Environment.TickCount64;
        foreach (var l in _mechTestLines)
            if (l.Row is { HasTimer: true } r && r.RemainSec <= 0f) { r.SnapTick = now; }
        return _mechTestLines;
    }

    private static McRow MechTestRow(string label, int color, long durMs, string local, string others) => new()
    {
        Key = label, Label = label, Color = color, DurationMs = durMs,
        SnapTick = Environment.TickCount64, SnapRemain = durMs > 0 ? durMs / 1000f : -1f,
        LocalName = local, OtherNames = others,
    };
}
