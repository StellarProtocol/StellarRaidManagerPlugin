using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// The Mechanic Callouts HUD list (data: MechanicCalloutTracker) — approved redesign 2026-11-17, prototype A "grouped"
// (_scratch/prototypes/raidmanager-callouts-hud-APPROVED.md + raidmanager-callouts-hud.html §1–4; port of Experiment
// fd828ea). ONE compact rounded card over a FIXED pool of line slots (the element tree is fixed at registration —
// WindowBuilder-Patterns.md "pre-allocate slots"); a slot is either a small bold UPPERCASE group header (normal text colour) or a row:
//   line 1   [■] <label>                         <timer>      timer = fixed-width, right-aligned
//   line 2       [YOU] ×2 (out), Alice, Bob …  +N             only when the row has targets, at most 2 lines
// and the row's BACKGROUND is a drain fill (remaining / current phase total) always in the mechanic colour, plus a red
// ring (theme Danger) ≤ 3 s — the only time cue (user 2026-11-17; Plugin.MechanicCalloutsHud.State.cs). Renders only
// in-world, when enabled AND there are rows; in layout-edit mode (enabled) it shows the sample card to position (the only preview path). No
// BringToFront: it opens from hidden via ShouldRender and lands on top within its ZCat naturally (CLAUDE.md rule 5).
//
// ⭐ Fill BEHIND a 1–3 line row = a LayerStackElement (fw 2.33) whose overlays are drawn in order over the base:
//   base     a height STRUT (spacers of the exact pinned line heights; Conditionals add the name lines) — it alone sizes
//            the row, so the overlays can be stretched over it
//   layer 1  BarElement (2.33 window bar: CornerRadius + FillColorFn + transparent TrackColorFn), Height set far above
//            the row so the layer's stretched VLG hands the track the WHOLE row rect (the bar row clamps the track to
//            min(row, Height)) → a rounded fill the size of the row, anchor-clipped to the fraction
//   layer 2  a transparent BubbleElement used only for its OutlineColorFn ring (null > 3 s = no ring)
//   layer 3  the text lines, every text with a pinned TextElement.Height equal to the strut's
// Nothing toggled inside an overlay re-lays-out the base (the overlays host has no LayoutGroup — LayerStack doc).
//
// ⭐ Menu surface (not HudOverlay as before): the card, the rounded bar and the outline ring only exist there. Plain Menu
// text ignores FontSize (WindowBuilder-Patterns.md), so every text is Bold (the TMP styled path reads FontSize — once,
// at build), and Menu text clips/ellipsizes itself (the old manual "…" / name-wrap estimate for HudOverlay is gone).
// Text size therefore rebuilds the window (Scale.cs, debounced re-register under the same id).
//
// ⭐ Perf: the card is a BubbleElement around the whole list, and a bubble re-measures (forced layout rebuild of its
// subtree) whenever a text it OWNS changes. Every per-tick text (the timers) therefore sits in its own fixed-width
// transparent inner bubble — the innermost bubble owns it, re-measures one short string, keeps the same width and never
// dirties the card (WindowBuilder.BubbleOwner.cs). Bars and layers are not owner-tracked at all. Labels, names, headers
// change only when the row set / phase / language changes.
public sealed partial class Plugin
{
    private IWindowControl? _mechHudWindow;          // null while removed (list toggle off — ApplyMechHudWindows)
    private const int MechHudSlots = 16;   // headers + rows; upstream panels rarely list more than a handful

    // ── geometry @ Text size 1.0× (canvas units; Scale.cs multiplies) ─────────────────────────────────────────────
    private const float MechFontLabel = 14f, MechFontHeader = 11f, MechFontNames = 12f, MechFontYou = 10f, MechFontMore = 11f;
    private const float MechLine1H = 18f, MechNameH = 16f, MechHeaderH = 16f, MechMoreH = 15f;
    private const float MechRowPad = 3f;          // content inset inside a row (all sides)
    private const float MechRowRadius = 5f;       // row drain fill + ring
    private const float MechRowGap = 5f;          // gap between the swatch / label / timer
    private const float MechSwatch = 8f;
    // Timer column: 66 (Experiment 40) — sized for the widest localized NOW ("SEKARANG", id, ≈ 64 px bold 14) besides
    // "88s" / "8.8s", so the label edge never shifts when a row hits.
    private const float MechTimerW = 66f;
    private const float MechSlotGap = 2f;         // between lines in the card
    private const float MechCardPadX = 6f, MechCardPadY = 5f, MechCardRadius = 10f;
    private const float MechRingW = 1.5f;
    private const float MechYouPadX = 4f, MechYouMaxW = 64f;   // YOU tag ("あなた" / "IKAW" / "KAMU" …)
    // Name line indent = swatch + row gap → the names start under the label's first glyph.
    private const float MechNameIndent = MechSwatch;
    private const float MechHudMinW = 200f, MechHudDefW = 300f, MechHudMaxW = 1200f;

    // Target names: a light blue that reads on the dark card (mockup --blue). The local player is the red YOU tag.
    private static readonly ColorRgba MechNameBlue = new(0x9C / 255f, 0xC8 / 255f, 1f, 1f);   // #9CC8FF
    private static readonly ColorRgba MechWhite = new(1f, 1f, 1f, 1f);

    private void RegisterMechCalloutHud()
    {
        _mechHudBuiltScale = _mechHudScale;
        _mechHudWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "raidmanager.mech.hud",
                Title:       _loc.T("rm.mech.hud.list"),
                // Constant 1440p-calibrated rect (never ScreenWidth-derived — "reset all HUD" reads ScreenWidth 0).
                DefaultRect: new WindowRect(40f, 420f, MechHudDefWidth, MechHudHeight),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false,
                // Width: extra goes to the label (timer stays pinned right) and the name lines. Height: user-sized; what
                // doesn't fit collapses into "+N more" (Overflow.cs). The card hugs the rows at the top of the rect.
                Resizable = true, MinWidth = MechHudMinWidth, MaxWidth = MechHudMaxWidth, MinHeight = MechHudMinHeight, MaxHeight = MechHudHeight,
                // Passive: the Borderless root carries a full-rect invisible raycast blocker, and with the tall rect most
                // of it is EMPTY — it would eat game clicks/camera drags mid-fight. Pure info HUD, nothing clickable;
                // edit-mode drag/resize hit-test rects geometrically, so layout editing still works. (Root padding 0.)
                Passive = true,
                ShouldRender = () => _mechEnabled && MechInWorld
                                  && (_services.ClientState.UiState & GameUIState.Blocking) == 0
                                  && (_services.Windows.IsLayoutEditing || _mechTracker.RowCount > 0),
            },
            Root:    BuildMechHudRoot(),
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

    private HudElement BuildMechHudRoot()
    {
        var items = new HudElement[MechHudSlots + 1];   // line slots + "+N more"
        for (int s = 0; s < MechHudSlots; s++)
        {
            int i = s;
            // Header = normal theme text (HudText, same as the row labels), NOT muted: it names the mechanic a player reads
            // mid-fight — important HUD text is never muted (user 2026-11-17). Muted stays only for the "+N more" line below.
            var header = new TextElement(() => MechHeaderText(i), Color: () => Hud.HudText,
                NoWrap: true, FontSize: MF(MechFontHeader)) { Bold = true, Ellipsize = true, Height = MS(MechHeaderH) };
            // Only the lines that fit the current height (Overflow.cs) — never a half-cut row.
            items[s] = new ConditionalElement(() => i < MechVisible,
                new ConditionalElement(() => MechIsHeader(i), header, Else: MechRowStack(i)));
        }
        items[MechHudSlots] = new ConditionalElement(() => MechHidden > 0,
            new TextElement(MechOverflowText, Color: () => Hud.MenuMuted, Align: TextAlign.Center,
                NoWrap: true, FontSize: MF(MechFontMore)) { Bold = true, Height = MS(MechMoreH) });

        // The card: one bubble around the list (fills the window width — a Column force-expands it — and hugs the rows'
        // height). "Background" = its opacity (live, FillColorFn). See the header note on why it stays cheap.
        var card = new BubbleElement(new ColumnElement(items, Gap: MechGap(MechSlotGap)))
        {
            FillColorFn = () => MechCardFill(MechBackdropOpacity()),
            CornerRadius = MathF.Round(MS(MechCardRadius)), PaddingX = MathF.Round(MS(MechCardPadX)),
            PaddingY = MathF.Round(MS(MechCardPadY)),
        };
        return new ColumnElement(new HudElement[] { card }, Gap: 0.01f);   // Passive root has no padding
    }

    // One mechanic row: drain fill / ring / text over a height strut (header note).
    private HudElement MechRowStack(int i)
    {
        float pad = MathF.Round(MS(MechRowPad)), radius = MathF.Round(MS(MechRowRadius));
        float l1 = MS(MechLine1H), nh = MS(MechNameH), gap = MS(MechRowGap);
        int padI = (int)pad;

        var strut = new ColumnElement(new HudElement[]
        {
            new SpacerElement(0f, l1),
            new ConditionalElement(() => MechNameLines(i) >= 1, new SpacerElement(0f, nh)),
            new ConditionalElement(() => MechNameLines(i) >= 2, new SpacerElement(0f, nh)),
        }, Gap: 0.01f) { Padding = padI };

        var fill = new BarElement(() => MechSlotFrac(i), MechClear)
        {
            FillWidth = true, Height = 999f, CornerRadius = radius,
            FillColorFn = () => MechSlotFill(i), TrackColorFn = () => MechClear,
        };
        var ring = new BubbleElement(new SpacerElement(0f, 0f))
        {
            FillColor = MechClear, OutlineColorFn = () => MechSlotRing(i) ? Danger : null,
            OutlineWidth = MS(MechRingW), CornerRadius = radius, PaddingX = 0f, PaddingY = 0f,
        };

        // Timer: own fixed-width transparent bubble (perf — header note), right-aligned in a fixed cell.
        float tw = MathF.Round(MS(MechTimerW));
        var timer = new BubbleElement(new TextElement(() => MechSlotTimer(i), Color: () => MechSlotTimerColor(i),
            Width: tw, Align: TextAlign.Right, NoWrap: true, FontSize: MF(MechFontLabel)) { Bold = true, Height = l1 })
        { FillColor = MechClear, PaddingX = 0f, PaddingY = 0f, MaxWidth = tw };
        var line1 = new RowElement(new HudElement[]
        {
            new SwatchElement(() => MechRowColor(i), MS(MechSwatch)),
            // Ring sequence row: step pills instead of the label (Plugin.MechanicCalloutsHud.Ring.cs).
            new CellElement(new ConditionalElement(() => MechIsSeq(i), MechPillRow(i, l1),
                Else: new TextElement(() => MechSlotLabel(i), Color: () => Hud.HudText,
                    NoWrap: true, FontSize: MF(MechFontLabel)) { Bold = true, Ellipsize = true, Height = l1 }), Weight: 1f),
            new CellElement(timer, Width: tw),
        }, Gap: gap);

        // Names: line A = [YOU] + the first names, line B = the rest + "+N" (Names.cs splits them by estimated width).
        var you = new BubbleElement(new TextElement(MechYouText, Color: () => MechWhite, NoWrap: true,
            FontSize: MF(MechFontYou)) { Bold = true, Height = MathF.Round(nh - 2f) })
        {
            FillColorFn = () => Danger, CornerRadius = MathF.Max(1f, MathF.Floor((nh - 2f) / 2f) - 1f),
            PaddingX = MathF.Round(MS(MechYouPadX)), PaddingY = 0f, MaxWidth = MathF.Round(MS(MechYouMaxW)),
        };
        var lineA = new RowElement(new HudElement[]
        {
            new SpacerElement(MS(MechNameIndent)),
            new ConditionalElement(() => MechSlotYou(i), you),   // no Else: hidden → out of the layout, gap included
            new CellElement(MechNameText(() => MechSlotNamesA(i), nh), Weight: 1f),
        }, Gap: gap);
        var lineB = new RowElement(new HudElement[]
        {
            new SpacerElement(MS(MechNameIndent)),
            new CellElement(MechNameText(() => MechSlotNamesB(i), nh), Weight: 1f),
        }, Gap: gap);
        var text = new ColumnElement(new HudElement[]
        {
            line1,
            new ConditionalElement(() => MechNameLines(i) >= 1, lineA),
            new ConditionalElement(() => MechNameLines(i) >= 2, lineB),
        }, Gap: 0.01f) { Padding = padI };

        return new LayerStackElement(strut, new StackLayer(fill), new StackLayer(ring), new StackLayer(text));
    }

    // Names are rich text: a "(safe)"/"(out)" status keeps its inline colour span (MechanicCalloutTracker.Rows.cs).
    private TextElement MechNameText(Func<string> text, float h) =>
        new(text, Color: () => MechNameBlue, NoWrap: true, FontSize: MF(MechFontNames))
        { Bold = true, Ellipsize = true, Height = h };   // Ellipsize = safety net under the width estimate

    // Column/Row Gap 0 means "the default section gap" to the framework → never pass a true 0.
    private float MechGap(float basePx) => MathF.Max(0.01f, MathF.Round(MS(basePx)));

    private bool MechIsHeader(int i) => MechLineAt(i)?.Header != null;

    private ColorRgba MechRowColor(int i)
    {
        var row = MechLineAt(i)?.Row;
        if (row?.Ring != null) { MechHudFrame(); return _mechSeqSwatch[i]; }   // the CURRENT step's ring (Ring.cs)
        return row != null ? MechanicCalloutData.SlotColor(row.Color) : MechanicCalloutData.SlotColor(0);
    }
}
