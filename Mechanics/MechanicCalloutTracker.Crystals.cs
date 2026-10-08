using System;
using System.Collections.Generic;
using UnityEngine;
using static Stellar.RaidManager.MechanicCalloutData;

namespace Stellar.RaidManager;

// Raid "Divine Scale - Preset Return" CRYSTALS (Mechanic-Callouts.md "Preset Return crystals"): SceneObjects
// (EntSceneObject 3) 3540-3543 "时空之晶" appear on the grid when Preset Return starts; touching one runs the server's
// teleport_to_pointA..D + AddBuffToTarget 829314 (15 s "交互后倒计时") ON THE CRYSTAL. The pressed crystal STAYS.
// Minimap: a crystal glyph at each present crystal, and its tile marked PRESSED (grey + check) once it was touched —
// EVERY crystal, independent of the Preset Return rows (a crystal off the 1F/2F/3F tiles can be pressed too).
//   • Discovery: type 3 is only walked on the 1 s wide pass / pinball probe — and, while a crystal is present or a
//     Preset Return buff is up, on EVERY scan (CrystalScanHot), so "gone" is judged per 200 ms pass, not per second.
//   • Pressed = 829314 seen on the crystal, and ONLY that (its fire uuid is the crystal itself, so the presser is
//     unknown). Log (2 rounds): a pressed crystal stays in the scan; all 4 leave TOGETHER at round end with the Preset
//     Return buffs — so "gone" is NOT a press (inferring it marked the never-pressed ones). Gone = round over for that
//     crystal → its pressed mark clears (pinball gone rule: ≥ 3 missed walking passes AND ≥ 1 s absent).
//   • Pooled like the balls: a gone uuid that re-appears is a NEW round → unpressed again. An 829314 instance that
//     already pressed it (same create) never re-presses the next appearance.
//   • Round end = no Preset Return count/link buff (829372/3/4, 829318) on anyone for 2 s → every pressed flag
//     resets. A press outside a round (no buffs seen) shows at most 15 s (829314's own lifetime).
// The [MechCrystal] diagnostics live in the Experiment copy only.
internal sealed partial class MechanicCalloutTracker
{
    private static readonly HashSet<int> CrystalIds = new() { 3540, 3541, 3542, 3543 };
    private static readonly HashSet<int> PresetRoundBuffs = new() { 829372, 829373, 829374, 829318 };
    private const int  CrystalPressBuff = 829314;
    private const long CrystalGoneMs = 1000, PresetRoundEndMs = 2000, CrystalPressShowMs = 15000;
    private const int  CrystalGoneMisses = 3;

    private sealed class Crystal
    {
        public int Cell = -1, Misses;
        public long LastSeen, PressTick, PressCreate = long.MinValue;
        public bool Gone, Pressed, HasPos;
        public Vector3 Pos;
    }

    private readonly Dictionary<long, Crystal> _crystals = new();
    private readonly Dictionary<long, int> _crystalIdOf = new();     // type-3 uuid → crystal base id, -1 = not a crystal
    private readonly HashSet<long> _crystalSeenPass = new();
    private long _presetLastSeen;
    private bool _presetRoundActive;

    private static bool CrystalTracking(SceneDef def, bool mapEnabled) => def.Kind == SceneKind.Raid && mapEnabled;

    // Walk every entity type this scan? Only while it matters: a crystal is (still) present, or a round is running.
    private bool CrystalScanHot()
    {
        if (_presetRoundActive) return true;
        foreach (var c in _crystals.Values) if (!c.Gone) return true;
        return false;
    }

    // One type-3 entity on a walking pass.
    private void CrystalDiscover(long uuid, long now)
    {
        if (!_crystalIdOf.TryGetValue(uuid, out int id))
        {
            var o = GetEntity(uuid);
            if (o == null) return;
            id = ReadMonsterId(o);
            if (id == 0) return;                                      // not readable yet — retry next pass
            _crystalIdOf[uuid] = id = CrystalIds.Contains(id) ? id : -1;
        }
        if (id < 0) return;
        var obj = GetEntity(uuid);
        if (obj == null) return;
        _crystalSeenPass.Add(uuid);
        if (!_crystals.TryGetValue(uuid, out var c)) _crystals[uuid] = c = new Crystal { Gone = true };
        if (c.Gone) { c.Gone = false; c.Pressed = false; }           // new uuid, or a pooled one back = a new round
        c.Misses = 0; c.LastSeen = now;
        if (TryReadPos(obj, out var p, out _) && !(p.x == 0f && p.y == 0f && p.z == 0f))
        {
            c.Pos = p; c.HasPos = true; c.Cell = RaidArena.NearestCell(p.x, p.z);
        }
        if (!c.Pressed && CrystalPressBuffOn(obj, out long create) && create != c.PressCreate)
        {
            c.Pressed = true; c.PressTick = now; c.PressCreate = create;
        }
    }

    // After a walking pass: misses + the gone rule. Gone = the round ended (all 4 vanish together), NOT a press — the
    // pressed mark goes with the crystal.
    private void CrystalEndPass(long now)
    {
        foreach (var kv in _crystals)
        {
            var c = kv.Value;
            if (c.Gone || _crystalSeenPass.Contains(kv.Key)) continue;
            c.Misses++;
            if (c.Misses < CrystalGoneMisses || now - c.LastSeen < CrystalGoneMs) continue;
            c.Gone = true;
            c.Pressed = false;
        }
        _crystalSeenPass.Clear();
    }

    // Every scan, after the wide merge: Preset Return round state; the active → idle edge resets every pressed flag.
    private void CrystalRoundUpdate(long now)
    {
        foreach (var b in _buffs) if (PresetRoundBuffs.Contains(b.BaseId)) { _presetLastSeen = now; break; }
        bool active = _presetLastSeen != 0 && now - _presetLastSeen <= PresetRoundEndMs;
        if (_presetRoundActive && !active)
            foreach (var c in _crystals.Values) c.Pressed = false;
        _presetRoundActive = active;
    }

    // Reads 829314 off the crystal's own full buff list (not via _buffs: the wide scan's copy is up to 1 s old).
    private bool CrystalPressBuffOn(object obj, out long create)
    {
        create = 0;
        try
        {
            var comp = _piBuffComp?.GetValue(obj);
            if (comp == null) return false;
            if (!_listsResolved) ResolveLists(comp);
            var list = _piFullList?.GetValue(comp);
            if (list == null) return false;
            int n = ListCount(list);
            for (int i = 0; i < n; i++)
            {
                var item = ListItem(list, i);
                if (item == null) continue;
                if (!_itemResolved) ResolveItemFields(item);
                if (_piItemBaseId == null) return false;
                if (ReadInt(_piItemBaseId, item) != CrystalPressBuff) continue;
                create = ReadLong(_piItemCreate, item);
                return true;
            }
        }
        catch { }
        return false;
    }

    // Minimap (inside BuildRaidMap's grid block, after the Preset Return cells): pressed tiles, then the crystal glyphs
    // on top. Local floor only (SameFloor vs the crystal's Y).
    private void AddCrystalRegions()
    {
        long now = Environment.TickCount64;
        int done = 0;                                                 // cell bitmask: one pressed overlay per tile
        foreach (var c in _crystals.Values)
        {
            if (!c.Pressed || c.Cell < 0 || !c.HasPos || !SameFloor(c.Pos.y) || (done & (1 << c.Cell)) != 0) continue;
            if (!_presetRoundActive && now - c.PressTick >= CrystalPressShowMs) continue;
            done |= 1 << c.Cell;
            var r = RaidArena.CellRect(c.Cell, 0);
            r.Style = 4;
            _map.Regions.Add(r);
        }
        foreach (var c in _crystals.Values)
            if (!c.Gone && c.HasPos && SameFloor(c.Pos.y)) _map.Regions.Add(MinimapRegion.Crystal(c.Pos.x, c.Pos.z, c.Pressed));
    }

    private void ResetCrystals()
    {
        _crystals.Clear(); _crystalIdOf.Clear(); _crystalSeenPass.Clear();
        _presetLastSeen = 0; _presetRoundActive = false;
    }
}
