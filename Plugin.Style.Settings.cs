using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// Settings-row building blocks shared by the Countdown & Raid Warning and Mechanic Callouts windows (house rule 17:
// every setting row ends in a "?", so the "?" column lines up): [indent?] [bold label, fixed width] [controls…]
// [flexible spacer] [?]. A row whose section switch is off is RECESSED, not hidden (rule 22 + rule 14 — the window
// never changes height): faint label, recessed chips, a recessed stand-in for the dropdown.
//
// ⚠️ Toolkit limit: SliderElement has no colour hooks (WindowBuilder.Widgets BuildSlider hard-codes the accent fill and
// the light knob; Enabled only sets Slider.interactable, which dims the knob). A disabled slider therefore can't look
// recessed — the label and the value readout fade and the slider stops taking input (KB WindowBuilder-Patterns
// 2026-11-12 "House-style kit into an existing plugin"). Needs a framework opt-in to match the prototype exactly.
public sealed partial class Plugin
{
    private const float RowIndent = 8f;    // + the 6 px row gap = the prototype's 14 px sub-option indent
    private const float RowGap    = 6f;
    private const float ValueW    = 44f;   // slider readout ("1.00x", "100%")

    // [indent?][label][controls…][flex][?]. `enabled` (optional) fades the label while the row is recessed; the controls
    // take their own `enabled`.
    private HudElement SettingRow(string labelKey, float labelW, HelpTip tip, Func<bool>? enabled, bool indent,
        params HudElement[] controls)
    {
        var items = new List<HudElement>(controls.Length + 4);
        if (indent) items.Add(new SpacerElement(RowIndent, 1f));
        items.Add(RowLabel(labelKey, labelW, enabled));
        items.AddRange(controls);
        items.Add(new SpacerElement(0f));   // flexible: pushes the "?" to the right edge
        items.Add(HelpDot(tip));
        return new RowElement(items, Gap: RowGap);
    }

    private HudElement RowLabel(string labelKey, float width, Func<bool>? enabled)
    {
        var disabled = new DisabledLabelCache();
        return new TextElement(() => _loc.T(labelKey),
            Color: () => enabled == null || enabled() ? TextColor() : DisabledLabel(PanelColor(), disabled),
            Width: width, NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true };
    }

    // [indent?][label][round-knob slider (fills the row)][value][?]. The slider sits in a weighted cell — a slider
    // squeezed by fixed cells collapses (WindowBuilder-Patterns.md), so it takes the leftover width.
    private HudElement SliderRow(string labelKey, float labelW, HelpTip tip, Func<float> get, Action<float> set,
        float min, float max, Func<string> value, Func<bool>? enabled = null, bool indent = true)
    {
        var disabled = new DisabledLabelCache();
        var items = new List<HudElement>(6);
        if (indent) items.Add(new SpacerElement(RowIndent, 1f));
        items.Add(RowLabel(labelKey, labelW, enabled));
        items.Add(new CellElement(new SliderElement(get, v => set(Math.Clamp(v, min, max)), min, max, enabled)
            { SquareHandle = true, HandleSize = 14f }, Weight: 1f));
        items.Add(new TextElement(value,
            Color: () => enabled == null || enabled() ? TextColor() : DisabledLabel(PanelColor(), disabled),
            Width: ValueW, Align: TextAlign.Right, NoWrap: true, FontSize: 12) { Bold = true });
        items.Add(HelpDot(tip));
        return new RowElement(items, Gap: RowGap);
    }

    // ── pill dropdown ─────────────────────────────────────────────────────────────────────────────────────────────
    // The framework Pill dropdown (fw ≥ 2.29 capsule at CornerRadius 13), its fill faded with the window. DropdownElement
    // has NO disabled state, so while `enabled` is false a RECESSED stand-in of the same geometry is swapped in: the
    // framework trigger's own composition (WindowBuilder.Dropdown.Pill.cs: 12 px insets, bold-13 caption, 6 px gap, 8×5
    // ▾ image, 28 tall) on the sunken fill, not clickable.
    private const float DropdownW = 176f;

    private HudElement PillDropdown(Func<int> selected, Func<IReadOnlyList<string>> options, Action<int> onSelect,
        float width, Func<bool>? enabled = null)
    {
        var live = new DropdownElement(selected, options, onSelect, Width: width)
        {
            Style = DropdownStyle.Pill, CornerRadius = RoundRadius(PillH), FillColorFn = () => Fade(NeutralFill()),
        };
        if (enabled == null) return live;

        var disabled = new DisabledLabelCache();
        string Caption()
        {
            var o = options(); int i = selected();
            return o != null && i >= 0 && i < o.Count ? o[i] : string.Empty;
        }
        var group = new RowElement(new HudElement[]
        {
            DropBold(new TextElement(Caption, Color: () => DisabledLabel(PanelColor(), disabled), NoWrap: true, FontSize: 13)
                { Bold = true, Ellipsize = true }),
            new SpacerElement(6f, 0f),
            new ImageElement(() => CachedPng("icons.chevron-down.png"), 8, 5)
                { Tint = () => DisabledLabel(PanelColor(), disabled) },
        }, Gap: 0.01f, Justify: RowJustify.Center);
        var standIn = new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(PillPadX, PillH),
            new CellElement(group, Width: MathF.Max(0f, width - 2f * PillPadX)),
            new SpacerElement(PillPadX, PillH),
        }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => DisabledShade, CornerRadius = RoundRadius(PillH), PaddingX = 0f, PaddingY = 0f,
        };
        return new CellElement(new ConditionalElement(enabled, live, standIn), Width: width);
    }

    // Localized option captions, rebuilt only when the language changes (DropdownElement re-reads Options every refresh
    // — Array.ConvertAll per poll would allocate every frame the window is open).
    private Func<IReadOnlyList<string>> LocOptions(string[] keys)
    {
        string? lang = null;
        var cache = new string[keys.Length];
        return () =>
        {
            var l = _loc.Language;
            if (!string.Equals(l, lang, StringComparison.Ordinal))
            {
                lang = l;
                for (int i = 0; i < keys.Length; i++) cache[i] = _loc.T(keys[i]);
            }
            return cache;
        };
    }
}
