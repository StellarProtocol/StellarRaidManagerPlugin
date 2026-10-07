using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Stellar.RaidManager.MechanicCalloutData;

namespace Stellar.RaidManager;

// Raid GRID arena floor damage (not in upstream; Mechanic-Callouts.md "Raid floor grid — cracked / destroyed tiles",
// validated from Experiment raid logs).
//   Tiles   = scene objects (EntSceneObject 3) with BaseId 3531 (floor) / 3532 (floor, damaged), found on the 1 s wide
//             pass (the only pass that walks type 3), mapped to the nearest cell (≤ 8 m, same floor); their buffs are
//             read every 200 ms scan. Crack = 829209 layer 1, destroyed = 829209 layer 2 (validated).
//   Dummies = EntDummy 11 floor helpers. In game (Purge 13023) the removal dummies 2002394 / 2002383 are PERSISTENT
//             helpers parked on the removed cells — so dummies are EVENTS: only a dummy uuid's FIRST sighting acts
//             (2002383/2002384/2002394 → Destroyed, 2002382 → Cracked, 2002388 → pending regen); staying put afterwards
//             never re-asserts anything.
// Per cell, evaluated every scan (first match wins):
//   1. Pending (after a regen dummy or a reset buff 829216/829233/829234/829235): shown as nothing until the next
//      wide pass (forced ≤ 300 ms after the reset) has re-read tile presence.
//   2. Tile present: damage buffs (829209 layer ≥ 2 / 829239 / 829204 → Destroyed; 829209 layer 1 / 829208 → Cracked);
//      BaseId 3532 → Cracked (damaged); else 3531 → INTACT, overriding any earlier Destroyed (the tile is back).
//   3. No tile: the cell had one (or a removal dummy spawned on it) → Destroyed; otherwise Unknown.
// (The per-change / census logs stay in Experiment.)
internal sealed partial class MechanicCalloutTracker
{
    public bool ShowFloor { get; set; } = true;

    public enum FloorState { Unknown, Intact, Cracked, Destroyed }

    private sealed class FloorCell
    {
        public long TileUuid; public int TileBase; public object? TileObj;
        public bool HadTile, DummyDestroyed, DummyCracked;
        public bool Pending; public int PendingPass;
        public FloorState State = FloorState.Unknown;
    }

    private static readonly HashSet<int> FloorTileIds = new() { 3531, 3532 };
    private static readonly HashSet<int> FloorResetBuffs = new() { 829233, 829216, 829234, 829235 };
    private const int DummyCrack = 2002382, DummyRegen = 2002388;
    private static readonly HashSet<int> DummyDestroy = new() { 2002383, 2002384, 2002394 };
    private const float FloorCellRange = 8f;

    private readonly FloorCell[] _floor = Enumerable.Range(0, 9).Select(_ => new FloorCell()).ToArray();
    private readonly Dictionary<long, int> _floorTileCell = new();          // tile uuid → cell (-1 = not a floor tile)
    private readonly HashSet<string> _floorResetSeen = new();
    private readonly HashSet<long> _floorDummySeen = new();                 // dummy uuids whose spawn was handled
    private readonly HashSet<long> _floorSeenThisPass = new();
    private int _floorPasses;

    private bool FloorTracking(SceneDef def) =>
        def.Kind == SceneKind.Raid && MapEnabled && ShowFloor && !(_raidArena == RaidArena.Kind.Ring && _arenaConfident);

    private void ResetFloor()
    {
        foreach (var c in _floor)
        {
            c.TileUuid = 0; c.TileObj = null; c.HadTile = c.DummyDestroyed = c.DummyCracked = c.Pending = false;
            c.State = FloorState.Unknown;
        }
        _floorTileCell.Clear(); _floorResetSeen.Clear(); _floorDummySeen.Clear();
        _floorPasses = 0;
    }

    private int CellOf(Vector3 p)
    {
        int i = RaidArena.NearestCell(p.x, p.z);
        if (i < 0) return -1;
        float dx = p.x - RaidArena.Cells[i].X, dz = p.z - RaidArena.Cells[i].Z;
        return dx * dx + dz * dz <= FloorCellRange * FloorCellRange && SameFloor(p.y) ? i : -1;
    }

    // ── Wide pass (1 s, or ≤ 300 ms after a reset): type-3 floor tiles ───────────────────────────────────────
    private void FloorBeginPass() => _floorSeenThisPass.Clear();

    private void FloorDiscover(long uuid, long type)
    {
        if (type != 3) return;
        if (_floorTileCell.TryGetValue(uuid, out int known))
        {
            if (known >= 0) { _floorSeenThisPass.Add(uuid); var kc = _floor[known]; if (kc.TileUuid != uuid) AttachTile(kc, uuid, null); }
            return;
        }
        var obj = GetEntity(uuid);
        if (obj == null) return;
        int id = ReadMonsterId(obj);
        if (id == 0) return;                                          // not readable yet — retry next pass
        if (!FloorTileIds.Contains(id) || !TryReadPos(obj, out var p, out _)) { _floorTileCell[uuid] = -1; return; }
        int cell = CellOf(p);
        _floorTileCell[uuid] = cell;
        if (cell < 0) return;
        _floorSeenThisPass.Add(uuid);
        AttachTile(_floor[cell], uuid, obj, id);
    }

    private void AttachTile(FloorCell c, long uuid, object? obj, int id = 0)
    {
        c.TileUuid = uuid; c.TileObj = obj ?? GetEntity(uuid); c.HadTile = true;
        if (id != 0) c.TileBase = id;
        else if (c.TileObj != null) c.TileBase = ReadMonsterId(c.TileObj);
    }

    private void FloorEndPass()
    {
        _floorPasses++;
        foreach (var c in _floor)
            if (c.TileUuid != 0 && !_floorSeenThisPass.Contains(c.TileUuid)) { c.TileUuid = 0; c.TileObj = null; }
    }

    // ── Dummies: events on FIRST sighting only ───────────────────────────────────────────────────────────────
    private void FloorDummy(long uuid, int id, object obj)
    {
        bool crack = id == DummyCrack, destroy = DummyDestroy.Contains(id), regen = id == DummyRegen;
        if ((!crack && !destroy && !regen) || _floorDummySeen.Contains(uuid)) return;
        if (!TryReadPos(obj, out var p, out _)) return;
        int cell = CellOf(p);
        if (cell < 0) return;                                         // retry next scan (e.g. floor filter not ready)
        _floorDummySeen.Add(uuid);
        var c = _floor[cell];
        if (regen) SetPending(c);
        else if (destroy) { c.DummyDestroyed = true; c.DummyCracked = false; c.Pending = false; }
        else c.DummyCracked = true;
    }

    private void SetPending(FloorCell c)
    {
        c.Pending = true; c.PendingPass = _floorPasses;
        c.DummyDestroyed = c.DummyCracked = false;
        RequestWideSoon();                                            // re-read tile presence within ≤ 300 ms
    }

    // ── Every scan, after the wide merge ─────────────────────────────────────────────────────────────────────
    private void FloorUpdate()
    {
        foreach (var b in _buffs)
            if (FloorResetBuffs.Contains(b.BaseId) && _floorResetSeen.Add($"{b.Target}:{b.BuffUuid}:{b.Create}"))
                foreach (var c in _floor) SetPending(c);

        foreach (var c in _floor) c.State = Evaluate(c);
    }

    private FloorState Evaluate(FloorCell c)
    {
        if (c.Pending)
        {
            if (_floorPasses <= c.PendingPass) return FloorState.Unknown;     // pending: wait for a wide pass
            c.Pending = false;                                        // a wide pass ran since → decide from presence
            if (c.TileUuid != 0 && c.TileObj != null && c.TileBase == 3531) return FloorState.Intact;
        }
        if (c.TileUuid != 0 && c.TileObj != null)
        {
            var buffs = TileBuffs(c.TileObj);
            int crackLayer = buffs.Where(b => b.Id == 829209).Select(b => b.Layer).DefaultIfEmpty(0).Max();
            if (crackLayer >= 2) return FloorState.Destroyed;
            if (buffs.Any(b => b.Id == 829239 || b.Id == 829204)) return FloorState.Destroyed;
            if (crackLayer == 1 || buffs.Any(b => b.Id == 829208)) return FloorState.Cracked;
            c.DummyDestroyed = false;                                 // a present tile beats an earlier removal event
            if (c.TileBase == 3532 || c.DummyCracked) return FloorState.Cracked;
            return FloorState.Intact;
        }
        if (c.DummyDestroyed || c.HadTile) return FloorState.Destroyed;   // removal dummy (spawn) / tile gone
        return FloorState.Unknown;
    }

    private readonly List<(int Id, int Layer)> _tileBuffScratch = new();

    private List<(int Id, int Layer)> TileBuffs(object tile)
    {
        _tileBuffScratch.Clear();
        try
        {
            var comp = _piBuffComp?.GetValue(tile);
            if (comp == null) return _tileBuffScratch;
            if (!_listsResolved) ResolveLists(comp);
            var list = _piFullList?.GetValue(comp);
            if (list == null) return _tileBuffScratch;
            int n = ListCount(list);
            for (int i = 0; i < n; i++)
            {
                var item = ListItem(list, i);
                if (item == null) continue;
                if (!_itemResolved) ResolveItemFields(item);
                if (_piItemBaseId == null) break;
                _tileBuffScratch.Add((ReadInt(_piItemBaseId, item), _piItemLayer != null ? ReadInt(_piItemLayer, item) : 1));
            }
        }
        catch { }
        return _tileBuffScratch;
    }

    // Minimap regions (drawn FIRST in BuildRaidMap, so Phase / Preset cells and dots stay on top).
    private void AddFloorRegions()
    {
        if (!ShowFloor) return;
        for (int i = 0; i < _floor.Length; i++)
        {
            var st = _floor[i].State;
            if (st != FloorState.Cracked && st != FloorState.Destroyed) continue;
            var r = RaidArena.CellRect(i, 5);
            r.Style = st == FloorState.Cracked ? 1 : 2;
            _map.Regions.Add(r);
        }
    }
}
