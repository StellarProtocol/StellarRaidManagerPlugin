using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Stellar.RaidManager;

// ── Raid: electromagnetic ring sequence ("Safe: Middle → Middle → Inner") ───────────────────────────────────────
// Upstream's ELECTROMAGNETIC_RING_SKILLS (10310062/63/64) are the SAME ids as the ring BODY dummies (DummyTable:
// inner / middle / outer EMP ring) — its "cast" is the ring body appearing, not a boss skill.
// Mechanic (log + user, 2026-10-08): each wave spawns TWO ring bodies at the same instant (same scan) at the arena
// centre — the two spawned rings are the DANGER, the ring NOT in the pair is the SAFE ring. Difficulty decides the rest
// (DungeonsTable: scene 13021 = "Clash!", 13022 = "Brutal!", 13023 = "Purge!"; RaidDungeonTable Difficult 1/2/3):
//   • Clash / Brutal = LIVE: each wave explodes right after it shows. Normal resets (10 s gap / 3 s after all bodies gone).
//   • Purge = PREVIEW: 3 waves shown first, players memorise the 3 safe rings, THEN the rings explode in that order —
//     the row must survive the preview bodies despawning (hold), max 3 waves per sequence.
//   • unknown scene → PREVIEW rules for the row (the safe superset), live shading on the minimap. So:
//   • waves: a fresh ring-body uuid joins the latest wave if first seen ≤ 1.5 s after that wave's first body; else it
//     opens a new wave. Decided at 2 distinct ids (safe = the third), or after 1.5 s with 1 id (safe "?"). A lone-body
//     wave still accepts a different id up to 2.5 s ("late join" — non-scanned-type bodies arrive via the 1 s wide pass)
//     if that body came alone in its scan (a pair in one scan is always a new wave). 3 ids → "?".
//   • the old per-body append + secondary cast append counted every ring twice (garbage order) — casts are no source.
//   • a uuid counts once; one that disappears and re-appears ≥ 3 s later counts as new (pooled-uuid caution, like the
//     pinball balls). Preview: at most 3 waves per sequence; bodies after a full preview are not counted.
//   • reset (preview): a fresh body ≥ 10 s after the last one (= a NEW preview), or 40 s hold after the last wave. NOT
//     on despawn: the preview bodies vanish right before the explode phase, exactly when players need the row.
//     Reset (live): no fresh body for 10 s, or none present and none fresh for 3 s.
//   • row: "Safe: <last 3 safe rings>" (localized, McText), colour of the latest wave's safe ring (cyan when "?" — a zone, not a player highlight).
//     Purge: the current step (next to explode) is shown "<b>[Outer]</b>".
//   • minimap (confident ring arena + ring centre at world (0,0) only): while the latest wave's bodies are present, its
//     two DANGER bands in the danger style + the safe band outlined. Purge (13023): the CURRENT step's bands instead,
//     and each decided wave's step number (1/2/3) inside its safe band with the current step highlighted
//     (MechanicCalloutTracker.Rules.RingPurge.cs — each wave explodes 10 s after its own spawn, user-confirmed).
// Experiment's copy (diagnostics lab) logs every wave / decision / ring-id cast; this shipped copy has no diagnostics.
internal sealed partial class MechanicCalloutTracker
{
    private static readonly Dictionary<int, (string ShortKey, int Color, float RIn, float ROut)> RaidRings = new()
    {
        [10310062] = ("rm.mech.ring.inner", 0, 0f, 12.5f), [10310063] = ("rm.mech.ring.middle", 1, 12.5f, 17.5f),
        [10310064] = ("rm.mech.ring.outer", 2, 18.5f, 30f),
    };
    // Manual constants (Rule 12 — no auto-learning).
    private const long RingGapMs = 10_000, RingHoldMs = 40_000, RingWaveWindowMs = 1500, RingLateJoinMs = 2500,
                       RingReappearMs = 3000, RingDespawnGraceMs = 3000;
    private const int  RingPreviewWaves = 3, RingUnknownColor = MechanicCalloutData.CyanSlot;

    private sealed class RingWave
    {
        public int  N;
        public long StartTick;
        public readonly List<int>     Ids = new();     // ring id per body, first-seen order
        public readonly HashSet<long> Uuids = new();
        public bool Decided;
        public int  Safe;                              // safe ring id, 0 = unknown ("?")
    }

    private readonly Dictionary<long, long> _ringLastPresent = new();   // uuid → last scan tick it was present
    private readonly List<RingWave> _ringWaves = new();                 // the current sequence
    private readonly List<McEnt> _ringNew = new();                      // scratch: fresh bodies of this scan
    private int  _ringWaveNo;
    private long _ringLastNewTick, _ringLastWaveTick;
    private bool _ringCentreOk;
    private readonly Dictionary<long, int> _ringOther = new();      // non-scanned-type ring bodies: uuid → id
    private readonly Dictionary<long, int> _ringIdTries = new();    // id-resolution attempts per other-type uuid

    // Wide pass, non-scanned entity type: resolve its id (≤ 3 tries per uuid) and remember ring bodies.
    private void RingDiscover(long uuid)
    {
        if (_ringOther.ContainsKey(uuid)) return;
        _ringIdTries.TryGetValue(uuid, out int tries);
        if (tries >= 3) return;
        if (_ringIdTries.Count > 4096) _ringIdTries.Clear();       // bullets churn uuids
        _ringIdTries[uuid] = tries + 1;
        var obj = GetEntity(uuid);
        if (obj == null) return;
        int id = ReadMonsterId(obj);
        if (RaidRings.ContainsKey(id)) _ringOther[uuid] = id;
        else if (id != 0) _ringIdTries[uuid] = 3;                   // resolved, not a ring: stop trying
    }

    // Every scan: known other-type ring bodies join _ents (dropped once the entity is gone).
    private void RingAddOthers(long now)
    {
        List<long>? gone = null;
        foreach (var (uuid, id) in _ringOther)
        {
            if (_ents.ContainsKey(uuid)) continue;
            var obj = GetEntity(uuid);
            if (obj == null) { (gone ??= new()).Add(uuid); continue; }
            if (!_firstSeen.TryGetValue(uuid, out long seen)) _firstSeen[uuid] = seen = now;
            var e = new McEnt { Uuid = uuid, MonsterId = id, FirstSeenTick = seen };
            e.HasPos = TryReadPos(obj, out e.Pos, out e.GoComp);
            _ents[uuid] = e;
        }
        if (gone != null) foreach (long u in gone) _ringOther.Remove(u);
    }

    private void RaidRingRows()
    {
        long now = Environment.TickCount64;
        bool present = false;
        _ringNew.Clear();
        foreach (var e in _ents.Values)
        {
            if (!RaidRings.ContainsKey(e.MonsterId)) continue;
            present = true;
            // Fresh = never seen, or absent ≥ 3 s and back (a pooled uuid re-used for a later wave).
            bool fresh = !_ringLastPresent.TryGetValue(e.Uuid, out long last) || now - last >= RingReappearMs;
            _ringLastPresent[e.Uuid] = now;
            if (fresh) _ringNew.Add(e);
        }
        if (_ringLastPresent.Count > 512)
            foreach (var u in _ringLastPresent.Where(kv => now - kv.Value > 60_000).Select(kv => kv.Key).ToList())
                _ringLastPresent.Remove(u);

        bool preview = RingPreviewMode();
        if (_ringWaves.Count > 0)
        {
            long idle = now - _ringLastNewTick;
            bool reset = preview
                ? now - _ringLastWaveTick > RingHoldMs || (_ringNew.Count > 0 && idle > RingGapMs)   // hold / new preview
                : idle > RingGapMs || (!present && idle > RingDespawnGraceMs);                       // gap / despawn
            if (reset) _ringWaves.Clear();
        }

        foreach (var e in _ringNew) RingJoin(e, now, preview);
        if (_ringWaves.Count > 6) _ringWaves.RemoveRange(0, _ringWaves.Count - 6);   // live mode: history only
        if (_ringNew.Count > 0) _ringLastNewTick = now;
        foreach (var w in _ringWaves) if (!w.Decided) RingDecide(w, now);
        RingDangerCheck(now);                                          // MOVE warning: Clash/Brutal + Purge steps (RingDanger.cs)

        if (_ringWaves.Count == 0) return;
        var shown = _ringWaves.Skip(Math.Max(0, _ringWaves.Count - 3)).ToList();
        var latest = shown[^1];
        // Purge: the step that explodes next is marked "<b>[Outer]</b>" (RingPurgeCurrentWave — set by RingDangerCheck
        // above, this same scan). The KEY stays the waves' N.Safe only, so the highlight moving 1 → 2 → 3 just rewrites
        // the label of the SAME row: no new row, no new occurrence (instance key = row key + timing + epoch).
        int curN = RingPurgeCurrentWave();
        Upsert($"raid:ring:{string.Join("-", shown.Select(w => $"{w.N}.{w.Safe}"))}", "Electromagnetic Ring Sequence",
               McText.F("rm.mech.ring.safe", string.Join(" → ", shown.Select(w => RingStepText(w.Safe, w.N == curN)))),
               latest.Safe == 0 ? RingUnknownColor : RaidRings[latest.Safe].Color, 101, 0, 0);
    }

    // One step of the row text. Current Purge step = bold + brackets (language-neutral), NOT the minimap's yellow: an
    // inline <color> blurs on the HudOverlay list (shadow twin copies the tag — WindowBuilder-Patterns.md
    // "shadow-twin"); inline <b> is safe.
    // TODO: once the framework's fix/hud-shadow-color-tags ships, the current step can use the yellow accent instead.
    private static string RingStepText(int safe, bool current)
    {
        string s = safe == 0 ? "?" : McText.L(RaidRings[safe].ShortKey);
        return current ? $"<b>[{s}]</b>" : s;
    }

    // Assign one fresh ring body to the latest wave (window / late join) or open a new wave.
    private void RingJoin(McEnt e, long now, bool preview)
    {
        if (e.HasPos && MathF.Abs(e.Pos.x) <= 5f && MathF.Abs(e.Pos.z) <= 5f) _ringCentreOk = true;
        var w = _ringWaves.Count > 0 ? _ringWaves[^1] : null;
        long age = w == null ? long.MaxValue : now - w.StartTick;
        bool lateJoin = w != null && age > RingWaveWindowMs && age <= RingLateJoinMs && _ringNew.Count == 1
                        && w.Ids.Distinct().Count() == 1 && !w.Ids.Contains(e.MonsterId);
        if (w == null || (age > RingWaveWindowMs && !lateJoin))
        {
            if (preview && _ringWaves.Count >= RingPreviewWaves) return;   // after a full preview (explode phase?)
            w = new RingWave { N = ++_ringWaveNo, StartTick = now };
            _ringWaves.Add(w);
            _ringLastWaveTick = now;
        }
        w.Ids.Add(e.MonsterId);
        w.Uuids.Add(e.Uuid);
        if (w.Decided) { w.Decided = false; w.Safe = 0; }            // re-decide with the extra body
    }

    private void RingDecide(RingWave w, long now)
    {
        var ids = w.Ids.Distinct().ToList();
        if (ids.Count == 2) w.Safe = RaidRings.Keys.First(id => !ids.Contains(id));   // the missing ring is safe
        else if (ids.Count >= 3 || now - w.StartTick > RingWaveWindowMs) w.Safe = 0;  // all 3 / lone body → "?"
        else return;
        w.Decided = true;
    }

    // Purge (13023) = preview-then-explode; Clash (13021) / Brutal (13022) = each wave live. Scene id ↔ difficulty is
    // 1:1 (DungeonsTable SceneID; RaidDungeonTable Difficult 1/2/3). Unknown → preview rules (the safe superset).
    private bool RingPreviewMode() => SceneId is not (13021 or 13022);

    // Minimap (BuildRaidMap, confident ring arena): latest wave's danger bands while its bodies are present (Purge:
    // the current step's) + step numbers. A running Purge step survives a sequence reset (copied steps).
    private void AddRingBand()
    {
        if ((_ringWaves.Count == 0 && _rpCur < 0) || !_ringCentreOk) return;   // no ring body near world (0,0) — centre unknown
        if (SceneId == 13023) { AddPurgeRingMap(); return; }        // Purge: current STEP's bands + highlight (RingPurge.cs)
        var w = _ringWaves[^1];
        // Latest wave's bodies present = live danger (Clash/Brutal) or the wave being previewed (unknown scene): shade
        // its two danger bands + outline the safe band. Bodies gone: live = wave over; unknown scene = nothing extra
        // (no explode timing known there — don't guess).
        if (w.Uuids.Any(u => _ents.TryGetValue(u, out var e) && RaidRings.ContainsKey(e.MonsterId)))
        {
            foreach (int id in w.Ids.Distinct())
            {
                var d = RaidRings[id];
                var reg = MinimapRegion.Ring(d.RIn, d.ROut, 3);
                reg.Style = 3;                                         // danger band (painter: red fill + heavy edges)
                _map.Regions.Add(reg);
            }
            if (w.Safe != 0)
            {
                var s = RaidRings[w.Safe];
                var safe = MinimapRegion.Ring(s.RIn, s.ROut, s.Color);
                safe.Style = 1;                                        // safe band: calm outline only
                _map.Regions.Add(safe);
            }
        }
        if (!RingPreviewMode()) return;
        // Unknown scene (preview rules): each decided wave's STEP NUMBER (1/2/3) inside its SAFE band, kept through the
        // explode phase until the sequence resets. All equal brightness (the step timing is only known for Purge).
        for (int i = 0; i < _ringWaves.Count; i++)
        {
            var wi = _ringWaves[i];
            if (wi.Decided && wi.Safe != 0) AddRingStepLabel(i + 1, wi.Safe, 0);
        }
    }

    // Step number k inside safe ring `safe`'s band; style = Text region style (0 plain / 1 dim / 2 current). The steps
    // must NEVER overlap (user 2026-10-09 — the old 40°-apart slots stacked in the Inner band, Inner → Outer → Inner).
    // Rule: step k ALWAYS takes slot k of its band, slots running left → right on screen, so any 2 or 3 steps sharing a
    // band sit apart and a label never moves when a later wave decides. Sizes at the 300-px base canvas: plain/dim label
    // 14×22 px (text scale 4 + 1 px rim), current 17.5×27.5 px (scale 5); ring view = 280 px / 110 u ≈ 2.55 px/u. Text
    // and projection both scale with the canvas (× _k, view half-extents fixed 55), so this holds at every map size.
    //   • Inner (disc r < 12.5): a horizontal row through the centre, z = +7.5 / 0 / −7.5 (≈ 19 px apart > 15.75 px, the
    //     current + dim half-width sum); the outer corners stay inside r ≈ 12.2.
    //   • Middle (12.5–17.5, mid r 15): left / top / right. The band is only ≈ 13 px wide, so the sides (label WIDTH
    //     radial) fit it best; the top slot is used only when all three steps share the band.
    //   • Outer (18.5–30, mid r 24.25): upper-left / bottom / upper-right (150° / 270° / 30°) — offset from the Middle
    //     slots so a Middle label never touches an Outer one (worst case ≥ 3 px apart, all combinations checked).
    // Screen angle θ (0 = right, 90 = up) → arena-local x = r·sinθ, z = −r·cosθ: the ring view has rotation 0
    // (px = cx − z·s, py = cy − x·s).
    private static readonly float[] RingMiddleDeg = { 180f, 90f, 0f }, RingOuterDeg = { 150f, 270f, 30f };
    private const float RingInnerStepDz = 7.5f;

    private void AddRingStepLabel(int step, int safe, int style)
    {
        int k = Math.Clamp(step, 1, RingPreviewWaves);
        float x = 0f, z = (2 - k) * RingInnerStepDz;                   // Inner: left / centre / right row
        if (safe != RingInnerId)
        {
            var s = RaidRings[safe];
            float r = (s.RIn + s.ROut) / 2f;
            float a = (safe == RingMiddleId ? RingMiddleDeg : RingOuterDeg)[k - 1] * MathF.PI / 180f;
            x = MathF.Sin(a) * r; z = -MathF.Cos(a) * r;
        }
        var reg = MinimapRegion.Text(x, z, step.ToString());
        reg.Style = style;
        _map.Regions.Add(reg);
    }

    private void ResetRing()
    {
        _ringLastPresent.Clear(); _ringWaves.Clear(); _ringNew.Clear();
        _ringWaveNo = 0; _ringLastNewTick = 0; _ringLastWaveTick = 0;
        _ringCentreOk = false; _ringOther.Clear(); _ringIdTries.Clear();
        ClearRingDanger();
    }
}
