using System;
using Stellar.Abstractions.Domain;

namespace Stellar.RaidManager;

// Per-refresh state of the callout list card (port of Experiment fd828ea). The window polls ~10 bindings per visible
// slot every refresh (~10 Hz); instead of each one re-deriving its row, the FIRST poll of a refresh runs MechHudFrame
// once — name-line layout, fit (Overflow.cs), then drain fraction / fill colour / ring / timer text / label for every
// visible row — and every binding just reads its slot's arrays. Keyed on the TickCount64 millisecond (one refresh = one
// tick). Timed into the [MechPerf] "hud" column (McPerf.Hud). No allocation in steady state: timer strings come from
// tables (Plugin.MechanicHudText.cs), colours are structs, and the label / header / name / "+N more" strings are rebuilt
// only when their source (or the language) changes.
public sealed partial class Plugin
{
    private long _mechFrameTick = -1;
    private int _mechVisible, _mechHidden;

    private readonly float[]     _mechFrac      = new float[MechHudSlots];
    private readonly ColorRgba[] _mechFill      = new ColorRgba[MechHudSlots];
    private readonly ColorRgba[] _mechTimerCol  = new ColorRgba[MechHudSlots];
    private readonly bool[]      _mechRing      = new bool[MechHudSlots];
    private readonly string[]    _mechTimer     = new string[MechHudSlots];

    // label (phased rows add " · <phase word>"), cached on (source label, phase word — itself cached per language)
    private readonly string?[] _mechLabelSrc = new string?[MechHudSlots], _mechLabelPhase = new string?[MechHudSlots];
    private readonly string[]  _mechLabel    = new string[MechHudSlots];
    // header in UPPERCASE, cached on the source string
    private readonly string?[] _mechHeadSrc = new string?[MechHudSlots];
    private readonly string[]  _mechHead    = new string[MechHudSlots];

    private int  MechVisible { get { MechHudFrame(); return _mechVisible; } }
    private int  MechHidden  { get { MechHudFrame(); return _mechHidden; } }
    private int  MechNameLines(int i)  { MechHudFrame(); return i < _mechVisible ? _mechNameLines[i] : 0; }
    private float MechSlotFrac(int i)  { MechHudFrame(); return _mechFrac[i]; }
    private ColorRgba? MechSlotFill(int i) { MechHudFrame(); return _mechFill[i]; }
    private bool MechSlotRing(int i)   { MechHudFrame(); return _mechRing[i]; }
    private string MechSlotTimer(int i) { MechHudFrame(); return _mechTimer[i] ?? ""; }
    private ColorRgba? MechSlotTimerColor(int i) { MechHudFrame(); return _mechTimerCol[i]; }
    private string MechSlotLabel(int i) { MechHudFrame(); return _mechLabel[i] ?? ""; }
    private string MechHeaderText(int i) { MechHudFrame(); return _mechHead[i] ?? ""; }
    private bool MechSlotYou(int i)    { MechHudFrame(); return _mechNameYou[i]; }
    private string MechSlotNamesA(int i) { MechHudFrame(); return _mechNameA[i] ?? ""; }
    private string MechSlotNamesB(int i) { MechHudFrame(); return _mechNameB[i] ?? ""; }

    private void MechHudFrame()
    {
        long now = Environment.TickCount64;
        if (now == _mechFrameTick) return;
        _mechFrameTick = now;
        var perf = McPerf.Begin();

        var lines = MechHudLines();
        int n = Math.Min(lines.Count, MechHudSlots);
        for (int k = 0; k < n; k++)
        {
            if (lines[k].Row is { } row) MechLayoutNames(k, row);   // Names.cs — cached, re-split only on change
            else _mechNameLines[k] = 0;
        }
        MechComputeFit(lines, n);                                  // Overflow.cs → _mechVisible / _mechHidden

        float barA = _mechBarOpacity;
        var c = Hud;
        for (int k = 0; k < _mechVisible; k++)
        {
            var line = lines[k];
            if (line.Header != null) { MechHeaderState(k, line.Header); continue; }
            var row = line.Row!;
            var mc = MechanicCalloutData.SlotColor(row.Color);
            MechLabelState(k, row);
            if (!MechRowTime(row, out float shown, out float frac))
            {
                // Untimed: full, steady, quieter fill and "—" (mockup §2 state 4).
                _mechFrac[k] = 1f; _mechFill[k] = mc with { A = barA * 0.6f }; _mechRing[k] = false;
                _mechTimer[k] = MechNoTimer; _mechTimerCol[k] = c.MenuMuted;
                continue;
            }
            int step = row.IsHitNow ? 2 : MechStep(shown);
            _mechFrac[k] = frac;
            _mechFill[k] = MechStepColor(step, mc) with { A = barA };
            _mechRing[k] = step == 2;
            _mechTimer[k] = row.IsHitNow ? MechNowText : MechSecText(shown);
            _mechTimerCol[k] = MechStepColor(step, c.HudText);
        }
        McPerf.End(McPerf.Hud, perf);
    }

    private void MechHeaderState(int k, string h)
    {
        if (_mechHeadSrc[k] == h && _mechHead[k] != null) return;   // value compare: cheap, survives rebuilt strings
        _mechHeadSrc[k] = h;
        _mechHead[k] = h.ToUpperInvariant();
    }

    // Phased rows (Golem Line "Locks" → "Dash", Spin Fan "Stops" → "Fire"): the phase word joins the label, the timer
    // shows the phase's own countdown (mockup "Golem Line · Locks  17.5s"). The old list showed "Locks 3.1s" in the timer.
    private void MechLabelState(int k, McRow row)
    {
        string? phase = row.IsPhased ? MechPhaseWord(row.InFirstPhase ? row.Phase1 : row.Phase2) : null;
        if (_mechLabelSrc[k] == row.Label && _mechLabelPhase[k] == phase && _mechLabel[k] != null)
            return;
        _mechLabelSrc[k] = row.Label; _mechLabelPhase[k] = phase;
        _mechLabel[k] = string.IsNullOrEmpty(phase) ? row.Label : row.Label + " · " + phase;
    }

    // "+N more" (rm.mech.more) — cached on (N, language): the text poll runs every refresh.
    private int _mechOverflowN = -1;
    private string? _mechOverflowLang;
    private string _mechOverflowText = "";

    private string MechOverflowText()
    {
        int hidden = MechHidden;
        var lang = _loc.Language;
        if (hidden != _mechOverflowN || !string.Equals(lang, _mechOverflowLang, StringComparison.Ordinal))
        {
            _mechOverflowN = hidden; _mechOverflowLang = lang;
            _mechOverflowText = _loc.TFormat("rm.mech.more", hidden);
        }
        return _mechOverflowText;
    }

    // The red YOU tag (rm.mech.you) — _loc.T hands back the catalog string, no allocation.
    private string MechYouText() => _loc.T("rm.mech.you");
}
