using System;
using System.Linq;

namespace Stellar.RaidManager;

// ── Raid: electromagnetic ring — "you are standing in a danger ring" ─────────────────────────────────────────────
// LIVE mode (13021 Clash / 13022 Brutal) explodes each wave right after it shows: the game's own ground warning on
// every ring body lasts exactly 5.0 s → the explosion lands 5 s after the bodies appear (waves ~6 s apart). So a
// DANGER WINDOW = the latest decided wave's StartTick + RingDangerMs, only when its safe ring is known. Every scan in
// the window the LOCAL player's horizontal distance from the arena centre (world 0,0 — only when _ringCentreOk) picks
// its band; standing in one of the wave's two danger rings → LocalRing* state, which drives the on-me
// "MOVE → <safe>" banner (Plugin.MechanicAlerts.Ring.cs). The banner is polled from that state, so it clears by itself
// when the player reaches the safe ring or the window ends.
//   • Bands (RaidRings radii, from upstream, NOT validated in game): Inner r < 12.5, Middle 12.5–17.5, Outer 18.5–30;
//     the 17.5–18.5 gap counts as BOTH Middle and Outer (danger if either is a danger ring); r > 30 = outside.
//   • The window is a COPY of the wave, not a reference into _ringWaves: a live "despawn" reset can clear the
//     sequence before the 5 s are up and the warning must still run to the explosion.
//   • Purge (13023): same window / band test, but the armed wave is the current STEP of the preview sequence and the
//     delay is RingPurgeExplodeMs (10 s) — MechanicCalloutTracker.Rules.RingPurge.cs. Unknown scenes: no warning.
// Experiment's copy logs enter / leave / explode with the local radius; this shipped copy has no diagnostics.
internal sealed partial class MechanicCalloutTracker
{
    public const string RingDangerKey = "raid:ring:danger";
    private const long RingDangerMs = 5000;            // manual (Rule 12): the game's ring ground-warning duration
    private const int  RingInnerId = 10310062, RingMiddleId = 10310063, RingOuterId = 10310064;

    /// <summary>Wave number whose danger ring the LOCAL player stands in; 0 = not in danger (no banner).</summary>
    public int  LocalRingWave { get; private set; }
    /// <summary>Safe ring id (10310062/63/64) of that wave; 0 when not in danger.</summary>
    public int  LocalRingSafeId => _localRingSafe;
    /// <summary>Environment.TickCount64 at which that wave explodes (window end).</summary>
    public long LocalRingExplodeTick { get; private set; }
    /// <summary>Length of that window (wave start → explosion): 5 s Clash/Brutal, 10 s Purge; 0 when not in danger.</summary>
    public long LocalRingWindowMs { get; private set; }
    /// <summary>Purge only: 1-based step of that wave in the preview sequence; 0 = Clash/Brutal (no step).</summary>
    public int  LocalRingStep { get; private set; }
    /// <summary>Purge only: steps per sequence (RingPreviewWaves = 3); 0 = Clash/Brutal.</summary>
    public int  LocalRingSteps { get; private set; }
    /// <summary>Localized short name of the safe ring ("Inner"/"Middle"/"Outer"), "" when not in danger.</summary>
    public string LocalRingSafeName => _localRingSafe != 0 ? McText.L(RaidRings[_localRingSafe].ShortKey) : "";

    private int   _localRingSafe;
    private int   _rdWave, _rdSafe, _rdLastArmed;      // armed window's wave (0 = none) / safe id / last wave armed (live)
    private int   _rdStep;                             // Purge step of the armed window (1-based); 0 = live
    private long  _rdStart, _rdDelay;                  // armed window: wave start tick / explode delay
    private int[] _rdDanger = Array.Empty<int>();

    // End of RaidRingRows, every raid scan.
    private void RingDangerCheck(long now)
    {
        bool arena = _raidArena == RaidArena.Kind.Ring && _arenaConfident && _ringCentreOk;
        if (SceneId == 13023) { RingPurgeCheck(now, arena); return; }          // RingPurge.cs
        bool live = (SceneId == 13021 || SceneId == 13022) && arena;
        if (!live) { if (_rdWave != 0) RingDangerEnd(); return; }

        // Arm the newest decided wave with a known safe ring while its 5 s are still running.
        var w = _ringWaves.LastOrDefault(x => x.Decided);
        if (w != null && w.Safe != 0 && w.N > _rdLastArmed && now - w.StartTick < RingDangerMs)
        {
            _rdLastArmed = w.N;
            RingDangerArm(w.N, 0, w.StartTick, RingDangerMs, w.Safe, w.Ids.Distinct().ToArray());
        }
        RingDangerTick(now);
    }

    private void RingDangerArm(int wave, int step, long start, long delay, int safe, int[] danger)
    {
        _rdWave = wave; _rdStep = step; _rdStart = start; _rdDelay = delay; _rdSafe = safe; _rdDanger = danger;
    }

    // Armed window: explosion at start + delay, else the local band test → LocalRing* state.
    private void RingDangerTick(long now)
    {
        if (_rdWave == 0) return;
        if (now - _rdStart >= _rdDelay) { RingDangerEnd(); return; }

        if (RingLocalInDanger())
        {
            LocalRingWave = _rdWave; _localRingSafe = _rdSafe; LocalRingExplodeTick = _rdStart + _rdDelay;
            LocalRingWindowMs = _rdDelay; LocalRingStep = _rdStep; LocalRingSteps = _rdStep > 0 ? RingPreviewWaves : 0;
        }
        else SetNoRingDanger();
    }

    private void RingDangerEnd() { _rdWave = 0; _rdStep = 0; _rdSafe = 0; _rdDanger = Array.Empty<int>(); SetNoRingDanger(); }

    private void SetNoRingDanger()
    {
        LocalRingWave = 0; _localRingSafe = 0; LocalRingExplodeTick = 0; LocalRingWindowMs = 0; LocalRingStep = 0; LocalRingSteps = 0;
    }

    // Local horizontal distance from the arena centre (world 0,0). Dead / no position → never "in danger".
    private bool RingLocalInDanger()
    {
        if (!_hasLocalPos || _localDead) return false;
        float r = MathF.Sqrt(_localPos.x * _localPos.x + _localPos.z * _localPos.z);
        foreach (int id in _rdDanger) if (InRingBand(id, r)) return true;
        return false;
    }

    // Middle/Outer both include the 17.5–18.5 gap between them (counts as both).
    private static bool InRingBand(int id, float r) => id switch
    {
        RingInnerId  => r < RaidRings[RingInnerId].ROut,
        RingMiddleId => r >= RaidRings[RingMiddleId].RIn && r < RaidRings[RingOuterId].RIn,
        RingOuterId  => r > RaidRings[RingMiddleId].ROut && r <= RaidRings[RingOuterId].ROut,
        _ => false,
    };

    // Scene reset / left the raid / loading: no stale MOVE banner.
    private void ClearRingDanger()
    {
        _rdWave = 0; _rdStep = 0; _rdSafe = 0; _rdLastArmed = 0; _rdStart = 0; _rdDelay = 0; _rdDanger = Array.Empty<int>();
        ClearRingPurge();
        SetNoRingDanger();
    }
}
