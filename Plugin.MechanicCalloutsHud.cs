using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// The Mechanic Callouts HUD list (data: MechanicCalloutTracker). A borderless HUD-category window over a FIXED pool of
// line slots (the element tree is fixed at registration — WindowBuilder-Patterns.md "pre-allocate slots"); each slot
// shows either a group header or a row `[■] <label>  <countdown>` with its targets `<you>, <others>` on an indented
// line directly under it (no extra line when the row has no targets), from the tracker's flattened Lines.
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
    // every slot at ~21f stride + a ~19f target-name line under every slot (worst case: all 16 slots are rows with
    // targets) + room for ~4 wrapped name lines (the name line spans the full width, so it wraps less than the old
    // right-hand column did) + the after-names gap under every slot. It must not be too SHORT — under-height, the root
    // VLG squeezes children toward their minHeight (Text min = 0) and rows overlap; extra height is just empty space
    // below the top-stacked rows (root VLG is UpperLeft, childForceExpandHeight off) and Passive, so it never blocks the game.
    // Sizes at Text size 1.0× (canvas units); every one but the outer padding is multiplied by the scale.
    private const float MechHudPad = 20f /* column only — Passive root has none */, MechHudStride = 21f,
                        MechHudNameStride = 19f, MechHudWrapReserve = 4 * 18f;
    // Extra space BELOW a target-name line (user: rows with names ran into the next mechanic). Only rows that have a
    // name line get it, and not the last visible line (the backdrop hugs the column, so a trailing gap would be blank).
    private const float MechHudNameGap = 6f;
    private const float MechHudLabelW = 300f;   // mechanic-name column (was 230; long th/fil names)
    private const float MechHudTimerW = 48f, MechHudSwatchCell = 14f, MechHudSwatch = 10f, MechHudBreak = 4f, MechHudGap = 6f;
    private const float MechHudMinW = 550f, MechHudMaxW = 1200f;   // +70 with the label column
    // Target-name line indent: swatch cell + row gap puts it under the label's first glyph, +12 (~2 spaces at 14 px) so
    // it reads as belonging to the mechanic above rather than as another label.
    private const float MechHudNameIndent = MechHudSwatchCell + MechHudGap + 12f;
    // Target names (everyone but the local player) in a light blue that stays readable on the dark HUD. Applied as the
    // TextElement Color, NOT an inline <color> tag: on HudOverlay the shadow twin copies inline tags and the span blurs
    // (WindowBuilder-Patterns.md, framework fix `fix/hud-shadow-color-tags` not in 2.19.1). "You" (and any tagged
    // (safe)/(out) name) keeps its own inline span, which overrides this base colour for just that name.
    private static readonly ColorRgba MechNameBlue = new(0x6E / 255f, 0xC6 / 255f, 1f, 1f);   // #6EC6FF
    private const int   MechHudFont = 14, MechHudHeaderFont = 15;  // the Menu surface's own body / emphasis sizes

    private float _mechHudScale = 1f;          // live "Text size" (fonts follow it at once)
    private float _mechHudBuiltScale = 1f;     // scale the window geometry was registered with
    private long  _mechHudRebuildAt;           // debounce deadline for the geometry re-register (0 = none pending)
    private IDisposable? _mechHudRebuildTick;

    private float MechHudH(float s) =>
        2 * MechHudPad + (MechHudSlots * (MechHudStride + MechHudNameStride + MechHudNameGap) + MechHudWrapReserve) * s;
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
                // Extra width goes only to the target-name line (its one Weight cell); swatch/label/timer are fixed.
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

    // "Background opacity" (config mech_bg_opacity, 0–100 %, step 5 %, default 0 = off): a black backdrop behind just
    // the visible rows. Poll-diffed by the framework, so the slider is live with no re-register. Forced to 0 whenever
    // there are no lines → never a blank box.
    private float _mechBgOpacity;

    private float MechBackdropOpacity() => MechHudLines().Count > 0 ? _mechBgOpacity : 0f;

    private void SetMechBgOpacity(float v)
    {
        _mechBgOpacity = Math.Clamp(MathF.Round(v * 20f) / 20f, 0f, 1f);   // 5 % steps
        _cfg.Set<float>("mech_bg_opacity", _mechBgOpacity);
        _cfg.Save();
    }

    private HudElement BuildMechHudRoot(float sc)
    {
        // Element 0 = the "Background opacity" backdrop. It stretches over THIS column only, and the column is
        // content-sized (root VLG childForceExpandHeight off; a null slot collapses its whole no-Else Cond container),
        // so it covers exactly the visible rows + Padding — never the locked-tall window rect, which is what
        // WindowSpec.BackgroundOpacity would fill (a big blank box below the rows).
        var slots = new HudElement[MechHudSlots + 1];
        slots[0] = new BackdropElement(MechBackdropOpacity);
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
                // read as one run of text in game).
                new SpacerElement(MechHudBreak * sc),
                // Timer right after the fixed-width label, so its position doesn't depend on the targets.
                new CellElement(new TextElement(() => MechTimer(i),
                    Color: () => (ColorRgba?)_services.Theme.Colors.MenuText, Shadow: true, NoWrap: true, FontSize: MechHudFont)
                    { DynamicFontSize = () => MechFont(MechHudFont) }, Width: MechHudTimerW * sc),
            }, Gap: MechHudGap * sc);
            // Targets on their own indented line UNDER the mechanic, filling the rest of the width. HudOverlay text
            // never wraps by itself, so MechNames breaks the list between names (",\n") against the line's estimated
            // width (Plugin.MechanicCalloutsHud.Wrap.cs). Others are blue via the base Color; the local name stays
            // inline rich text (accent + bold) — two sibling Texts would both be squeezed. A no-Else Conditional
            // collapses entirely when there are no targets → no blank line, and the backdrop (which hugs the
            // content-sized column) still covers exactly the visible lines.
            var names = new RowElement(new HudElement[]
            {
                new SpacerElement(MechHudNameIndent * sc),
                new CellElement(new TextElement(() => MechNames(i),
                    Color: () => (ColorRgba?)MechNameBlue, Shadow: true, FontSize: MechHudFont)
                    { DynamicFontSize = () => MechFont(MechHudFont) }, Weight: 1f),
            });
            // Name line + the MechHudNameGap spacer under it (Gap 0 → the spacer adds exactly its height). The spacer is
            // skipped when this is the last visible line (nothing below to separate from) — i+1 past the slot pool
            // counts as last too, since those lines aren't shown.
            var namesWithGap = new ColumnElement(new HudElement[]
            {
                names,
                new ConditionalElement(() => i + 1 < MechHudSlots && MechLineAt(i + 1) != null,
                    new SpacerElement(Height: MechHudNameGap * sc)),
            }, Gap: 0f);
            var rowWithNames = new ColumnElement(new HudElement[]
            {
                row,
                new ConditionalElement(() => MechHasNames(i), namesWithGap),
            }, Gap: 1f * sc);

            slots[s + 1] = new ConditionalElement(() => MechLineAt(i) != null,
                new ConditionalElement(() => MechLineAt(i)?.Header != null, header, Else: rowWithNames));
        }
        return new ColumnElement(slots, Gap: 3f * sc) { Padding = (int)MechHudPad };   // Passive root has 0 padding
    }

    private ColorRgba MechRowColor(int i)
    {
        var row = MechLineAt(i)?.Row;
        return row != null ? MechanicCalloutData.SlotColor(row.Color) : MechanicCalloutData.SlotColor(0);
    }

    private bool MechHasNames(int i) => MechLineAt(i)?.Row is { } r && (r.LocalName.Length > 0 || r.OtherNames.Length > 0);

    // "<b><color=#accent>You</color></b>, Alice, Bob" — local player first in the theme accent, bold. uGUI Text has
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
            ? $"<b><color=#{(int)(c.R * 255f):X2}{(int)(c.G * 255f):X2}{(int)(c.B * 255f):X2}>{row.LocalName}</color></b>" : "";
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

    // A small sample panel (two groups, local-first targets, one untimed row, one target-less row). Each row's targets
    // render on the indented blue line under it; the target-less row shows no second line. Timers loop so it looks
    // alive. Rebuilt on a language switch (labels/headers are localized when built).
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
                // Long target list — exercises the name-line wrap (must not run past the window edge).
                new(MechTestRow(McText.T("Spread"), 0, 12000, you, "Alice, Bob, Carol, Dave, Erin, Frank, Grace, Heidi")),
                new(McText.T("Execution Sentence")),
                new(MechTestRow(McText.T("Execution Sentence") + " x2", 1, 15000, "", "Dave")),
                new(MechTestRow(McText.T("Divine Trick - Execution Sentence"), 0, 0, you, "")),
                // No targets → no name line (an existing localized mechanic name — no new text key).
                new(MechTestRow(McText.T("Phase Mapping"), 1, 6000, "", "")),
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
