using System;

namespace Stellar.RaidManager;

// Callout list settings that shape the card: "Text size", "Background" (card opacity) and "Bar opacity" (row drain fill)
// — the slider rows themselves are in the settings window (Plugin.MechanicCallouts.Ui.cs).
//
// Text size (config mech_textscale, 0.75–2.0×, step 0.05, default 1.0×) scales EVERYTHING in the card — fonts, line
// heights, row padding, swatch, timer column, drain-fill radius, ring width, card padding/radius — and the window's
// default/min width + min/max height. On the Menu surface all of that is fixed at BUILD (styled FontSize, bubble
// radius/padding/OutlineWidth, bar radius, cell widths, Resizable min/max), so a change RE-REGISTERS the window under the
// same id (saved position kept) — debounced: only once the slider has been still for 300 ms (≈ on release), never per
// drag tick. (The HudOverlay list before this followed the font live and rebuilt only the geometry.)
public sealed partial class Plugin
{
    private float _mechHudScale = 1f;          // "Text size"
    private float _mechHudBuiltScale = 1f;     // scale the window was registered with
    private long  _mechHudRebuildAt;           // debounce deadline for the re-register (0 = none pending)
    private IDisposable? _mechHudRebuildTick;

    private float MS(float v) => v * _mechHudBuiltScale;
    private int MF(float basePx) => Math.Max(7, (int)MathF.Round(basePx * _mechHudBuiltScale));

    // Worst-case budget = the default height: every slot a row with a name line, plus room for a few second name lines.
    private float MechHudHeight =>
        2f * MathF.Round(MS(MechCardPadY)) + MechHudSlots * (MechRowH(1) + MechGap(MechSlotGap)) + 6f * MS(MechNameH);
    private float MechHudMinWidth => MS(MechHudMinW);
    private float MechHudDefWidth => MS(MechHudDefW);
    private float MechHudMaxWidth => MathF.Max(MechHudMaxW, MechHudDefWidth);

    private void LoadMechHudSettings()
    {
        _mechHudScale   = Math.Clamp(_cfg.Get<float>("mech_textscale", 1f), 0.75f, 2f);
        _mechBgOpacity  = Math.Clamp(_cfg.Get<float>("mech_bg_opacity", 0.7f), 0f, 1f);
        _mechBarOpacity = Math.Clamp(_cfg.Get<float>("mech_bar_opacity", 0.3f), 0f, 1f);
    }

    // Text size changed: the re-register waits until the slider is still for 300 ms.
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
        // List turned off inside the debounce ⇒ the window is already removed; don't resurrect it (it is built at the
        // current scale when the toggle comes back on).
        if (_mechHudWindow == null) return;
        RemoveMechHud(ref _mechHudWindow);
        Array.Clear(_mechNameA);    // re-split the name lines at the new font size
        RegisterMechCalloutHud();   // same id → saved position restored
    }

    // ── "Background" (config mech_bg_opacity, 0–100 %, step 5 %, default 70 %; 0 = off) = the card's opacity ───────────
    // Poll-diffed by the framework (card FillColorFn), so the slider is live with no re-register. 0 whenever there are
    // no lines → never an empty card. Default kept at 0 (Experiment moved to 70 % with this redesign): a saved value is
    // kept either way.
    private float _mechBgOpacity;

    private float MechBackdropOpacity() => MechHudLines().Count > 0 ? _mechBgOpacity : 0f;

    private void SetMechBgOpacity(float v)
    {
        _mechBgOpacity = Math.Clamp(MathF.Round(v * 20f) / 20f, 0f, 1f);   // 5 % steps
        _cfg.Set<float>("mech_bg_opacity", _mechBgOpacity);
        _cfg.Save();
    }

    // ── "Bar opacity" (config mech_bar_opacity, 0–100 %, step 5 %, default 30 %) = the row drain fill ─────────────
    // Read by the per-refresh state pass (State.cs) into each fill colour's alpha → live, no re-register. Untimed rows
    // use 60 % of it (a quieter steady fill).
    private float _mechBarOpacity = 0.3f;

    private void SetMechBarOpacity(float v)
    {
        _mechBarOpacity = Math.Clamp(MathF.Round(v * 20f) / 20f, 0f, 1f);   // 5 % steps
        _cfg.Set<float>("mech_bar_opacity", _mechBarOpacity);
        _cfg.Save();
    }
}
