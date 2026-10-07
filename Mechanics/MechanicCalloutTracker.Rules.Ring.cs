using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Stellar.RaidManager;

// ── Raid: electromagnetic ring sequence ("Inner Ring → Middle Ring → Outer Ring") ────────────────────────────────
// Upstream's ELECTROMAGNETIC_RING_SKILLS (10310062/63/64) are the SAME ids as the ring BODY dummies (DummyTable:
// inner / middle / outer EMP ring) — its "cast" is the ring body appearing, not a boss skill. Polling monsters'
// curSkillId_ for those ids never matched in-game (no ring row, no ring log lines). Now:
//   • source = ring-body SPAWNS: each NEW entity uuid with id 10310062/63/64 appends Inner/Middle/Outer (a uuid already
//     seen this scene never re-appends). The old cast path stays as a secondary source if it ever matches.
//   • bodies of a non-scanned entity type are discovered on the 1 s wide pass (id resolved once per uuid) and then
//     re-added every scan while they exist (RingAddOthers) — monster/dummy-typed bodies come through the normal scan.
//   • reset: no new body for 10 s ("gap"), or no body present AND none new for 3 s ("despawn" — the short grace keeps a
//     sequence whose bodies despawn between rings). The row (last 3, colour of the latest, untimed) goes with it.
//   • minimap (confident ring arena only): the latest ring's band as a low-alpha annulus (inner 0-12.5 / middle
//     12.5-17.5 / outer 18.5-30) around the arena origin — ONLY once a ring body was seen within 5 m of world (0,0),
//     i.e. the raid's unlocalized origin really is the ring centre; otherwise skipped.
internal sealed partial class MechanicCalloutTracker
{
    private static readonly Dictionary<int, (string Label, int Color, float RIn, float ROut)> RaidRings = new()
    {
        [10310062] = ("Inner Ring", 0, 0f, 12.5f), [10310063] = ("Middle Ring", 1, 12.5f, 17.5f),
        [10310064] = ("Outer Ring", 2, 18.5f, 30f),
    };
    private const long RingGapMs = 10_000, RingDespawnGraceMs = 3000;

    private readonly HashSet<long> _ringSeenUuids = new();
    private readonly HashSet<(long, long)> _ringSeenCasts = new();
    private readonly List<int> _ringSeq = new();
    private long _ringLastNewTick;
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
        foreach (var e in _ents.Values)
        {
            if (!RaidRings.ContainsKey(e.MonsterId)) continue;
            present = true;
            if (!_ringSeenUuids.Add(e.Uuid)) continue;
            RingAppend(e.MonsterId, now);
            if (e.HasPos && MathF.Abs(e.Pos.x) <= 5f && MathF.Abs(e.Pos.z) <= 5f) _ringCentreOk = true;
        }
        // Secondary: a real cast of a ring id (never observed so far).
        foreach (var c in _casts)
        {
            if (!RaidRings.ContainsKey(c.SkillId) || now - c.Tick > RingGapMs || !_ringSeenCasts.Add((c.Caster, c.Tick))) continue;
            RingAppend(c.SkillId, now);
        }

        if (_ringSeq.Count > 0)
        {
            long idle = now - _ringLastNewTick;
            // "gap" (no new body for 10 s) or "despawn" (none present and none new for 3 s).
            if (idle > RingGapMs || (!present && idle > RingDespawnGraceMs)) _ringSeq.Clear();
        }
        if (_ringSeq.Count == 0) return;
        var last = _ringSeq.Skip(Math.Max(0, _ringSeq.Count - 3)).ToList();
        Upsert($"raid:ring:{string.Join("-", last)}", "Electromagnetic Ring Sequence",
               string.Join(" → ", last.Select(id => McText.T(RaidRings[id].Label))), RaidRings[last[^1]].Color, 101, 0, 0);
    }

    private void RingAppend(int id, long now)
    {
        _ringSeq.Add(id);
        if (_ringSeq.Count > 3) _ringSeq.RemoveAt(0);
        _ringLastNewTick = now;
    }

    // Minimap band of the latest ring (BuildRaidMap, confident ring arena).
    private void AddRingBand()
    {
        if (_ringSeq.Count == 0) return;
        if (!_ringCentreOk) return;   // no ring body near world (0,0) — ring centre unknown in our coords
        var r = RaidRings[_ringSeq[^1]];
        _map.Regions.Add(MinimapRegion.Ring(r.RIn, r.ROut, r.Color));
    }

    private void ResetRing()
    {
        _ringSeenUuids.Clear(); _ringSeenCasts.Clear(); _ringSeq.Clear(); _ringLastNewTick = 0;
        _ringCentreOk = false; _ringOther.Clear(); _ringIdTries.Clear();
    }
}
