using System;
using System.Collections.Generic;
using UnityEngine;
using static Stellar.RaidManager.MechanicCalloutData;

namespace Stellar.RaidManager;

// Entity + buff scan for MechanicCalloutTracker. Players are always scanned; monsters/dummies only when their monster
// id (ZEntity.BaseId — MonsterId.cs; upstream's "monster id") is in the scene's relevant list (upstream
// relevant_monster_ids), so a trash pack costs one cached id read each. Each scanned entity's FULL buff list is read once and only the scene's
// mechanic buff ids are kept (_buffs) — the table and every rule read from that one snapshot.
internal sealed partial class MechanicCalloutTracker
{

    private void ScanEntities(SceneDef def)
    {
        _ents.Clear();
        _buffs.Clear();
        long localUuid = _services.CombatSnapshot.LocalEntityId.Value;
        long now = Environment.TickCount64;
        // Positions: the minimap (any scene), reef wave safe-zones, and the raid (arena gate, Preset Return cell).
        bool needPos = MapEnabled || def.Kind == SceneKind.SeaReef || def.Kind == SceneKind.Raid;

        // Once a second also check every OTHER entity (any type) for the scene's mechanic buff ids (WideScan.cs).
        bool wide = BeginWide(now);
        bool withMonsters = def.Monsters.Count > 0;
        // Raid pinball probe window (PinballProbe.cs): every entity type near the player is checked for the ball.
        bool probe = PinballProbeActive(def, now);
        bool floor = FloorTracking(def);               // raid grid floor damage (Floor.cs)
        if (floor && wide) FloorBeginPass();
        // Preset Return crystals (Crystals.cs): type 3, walked every scan while one is present / a round runs.
        bool crystals = CrystalTracking(def, MapEnabled);
        bool walk = wide || probe || (crystals && CrystalScanHot());

        foreach (long uuid in EntityUuids(localUuid, withMonsters, allTypes: walk))
        {
            long type = (uuid >> 6) & 31;
            bool isPlayer = type == EntChar;
            if (!isPlayer && !(withMonsters && (type == EntMonster || type == EntDummy)))
            {
                if (probe) ProbeEntity(uuid, null);
                if (wide && def.Kind == SceneKind.Raid) RingDiscover(uuid);   // ring bodies of other types (Rules.Ring.cs)
                if (floor && wide) FloorDiscover(uuid, type);
                if (crystals && walk && type == 3) CrystalDiscover(uuid, now);
                if (wide) WideCheck(uuid, null);
                continue;
            }
            var obj = GetEntity(uuid);
            if (obj == null) continue;
            int mid = 0;
            if (!isPlayer)
            {
                mid = MonsterIdOf(uuid, obj);
                if (mid == 0 || !def.Monsters.Contains(mid))
                {
                    if (probe) ProbeEntity(uuid, obj);
                    if (floor && mid != 0) FloorDummy(uuid, mid, obj);
                    if (wide) WideCheck(uuid, obj);
                    continue;
                }
            }
            if (!_firstSeen.TryGetValue(uuid, out long seen)) _firstSeen[uuid] = seen = now;
            var e = new McEnt { Uuid = uuid, MonsterId = mid, IsPlayer = isPlayer, FirstSeenTick = seen };
            if (needPos)
            {
                e.HasPos = TryReadPos(obj, out e.Pos, out e.GoComp);
                // Facing: monsters (waves, pizza, charge — rendered model rotation first, AttrDir fallback)
                // + the local player (AttrDir kept for the minimap-arrow fallback).
                if (!isPlayer || (localUuid != 0 && (uuid >> 16) == (localUuid >> 16)))
                {
                    if (!isPlayer && TryGoYaw(e.GoComp, out float yaw)) { e.Facing = yaw; e.FacingFromRot = true; }
                    else e.Facing = ReadFacing(obj);
                }
            }
            if (isPlayer) e.IsDead = ReadDead(obj);
            _ents[uuid] = e;
            ScanEntityBuffs(uuid, obj, def);
        }
        if (wide) EndWide();
        if (def.Kind == SceneKind.Raid) RingAddOthers(now);
        if (floor && wide) FloorEndPass();
        if (crystals && walk) CrystalEndPass(now);
        _buffs.AddRange(_wideBuffs);                     // last wide pass's hits (≤ 1 s old) join the snapshot (all scenes)
        // After the wide merge: 829314's carrier is a scene object (type 3) only the wide scan sees.
        if (def.Kind == SceneKind.Raid) PinballProbeArm(now);   // 829314 seen → keep probing 8 s after
        if (floor) FloorUpdate();                      // tile buffs / reset signals → cell states
        if (crystals) CrystalRoundUpdate(now);        // Preset Return round end → pressed crystals reset
    }

    // Reads ONE entity's full server buff list into _buffs (scene mechanic ids only).
    private void ScanEntityBuffs(long uuid, object ent, SceneDef def)
    {
        var comp = _piBuffComp?.GetValue(ent);
        if (comp == null) return;
        if (!_listsResolved) ResolveLists(comp);
        var list = _piFullList?.GetValue(comp);
        if (list == null) return;

        int n = ListCount(list);
        for (int i = 0; i < n; i++)
        {
            var item = ListItem(list, i);
            if (item == null) continue;
            if (!_itemResolved) ResolveItemFields(item);
            if (_piItemBaseId == null) return;

            int baseId = ReadInt(_piItemBaseId, item);
            if (baseId != 0 && def.MechanicBuffs.Contains(baseId)) _buffs.Add(ReadBuff(uuid, baseId, item));
        }
    }

    // One BuffItem → McBuff (BuffItem fields already resolved; Buff-Tracking.md §3).
    private McBuff ReadBuff(long uuid, int baseId, object item) => new(uuid, baseId,
        _piItemLayer != null ? ReadInt(_piItemLayer, item) : 1,
        _piItemUuid  != null ? ReadInt(_piItemUuid, item)  : 0,
        ReadLong(_piItemCreate, item), ReadLong(_piItemDuration, item), ReadLong(_piItemFire, item));

    // ── Live attribute / position reads ──────────────────────────────────────────────────────────────────────
    // Numeric attr read. AttrId/AttrDir/AttrSkillId are int32 on the wire; whether the GetAttr<long> instantiation
    // reads an int attr cleanly is UNVERIFIED (FollowController only proves it for HP), so a throw/0 falls back to
    // GetAttr<object> + Convert (the boxed value of whatever the attr stores).
    private bool _attrLongFailed;
    private long ReadAttrLong(object ent, object? box)
    {
        if (box == null) return 0;
        if (_miGetAttrLong != null && !_attrLongFailed)
        {
            try
            {
                long v = Convert.ToInt64(_miGetAttrLong.Invoke(ent, new object[] { box, true }) ?? 0L);
                if (v != 0) return v;
            }
            catch { _attrLongFailed = true; }   // → GetAttr<object> from now on
        }
        if (_miGetAttrObject == null) return 0;
        try
        {
            var o = _miGetAttrObject.Invoke(ent, new object[] { box, true });
            return o == null ? 0 : long.TryParse(o.ToString(), out long v) ? v : 0;
        }
        catch { return 0; }
    }

    // AttrDir is yaw in centidegrees (upstream: `value / 100`). 0 is taken at face value (a wave facing exactly 0° is
    // a valid "vertical"); an unreadable attr also reads 0 — an accepted ambiguity for an axis guess.
    private float ReadFacing(object ent) => _boxAttrDir == null ? float.NaN : ReadAttrLong(ent, _boxAttrDir) / 100f;

    // Entity → ModelGoComp (kept on McEnt so the minimap's fast path can re-read Position per frame without walking
    // entity → model → goComp again) → Position.
    private bool TryReadPos(object ent, out Vector3 pos, out object? goComp)
    {
        pos = default; goComp = null;
        try
        {
            var model = _piModel?.GetValue(ent);
            goComp = model != null ? _piModelGoComp?.GetValue(model) : null;
            return goComp != null && TryGoPos(goComp, out pos);
        }
        catch { return false; }
    }

    // Dead = MaxHp > 0 && Hp <= 0, read LIVE off the entity (Entity-Attributes-DeadState.md: the validated check that
    // also flips back on revive; EntityDetail / CombatLookup vitals are stale or AOI-scoped). Unknown MaxHp → alive.
    private bool ReadDead(object ent)
    {
        long max = ReadAttrLong(ent, _boxAttrMaxHp);
        return max > 0 && ReadAttrLong(ent, _boxAttrHp) <= 0;
    }

    // Rendered model yaw (degrees, upstream facing convention: forward = (sin f, cos f) in x/z) from
    // ModelGoComp.Rotation — smooth (interpolated render rotation), unlike the server AttrDir. Forward vector of the
    // quaternion computed in managed math (no interop call): fx = 2(xz + wy), fz = 1 − 2(x² + y²).
    private bool TryGoYaw(object? goComp, out float yawDeg)
    {
        yawDeg = float.NaN;
        if (goComp == null) return false;
        try
        {
            Quaternion q;
            if (_getGoRot != null) q = _getGoRot(goComp);
            else if (_piRotation?.GetValue(goComp) is Quaternion pq) q = pq;
            else return false;
            float fx = 2f * (q.x * q.z + q.w * q.y), fz = 1f - 2f * (q.x * q.x + q.y * q.y);
            if (fx * fx + fz * fz < 1e-6f) return false;
            yawDeg = MathF.Atan2(fx, fz) * 180f / MathF.PI;
            return true;
        }
        catch { return false; }
    }

    // ModelGoComp.Position through a typed open delegate (no Vector3 boxing, no reflection invoke) when it could be
    // built, else the cached PropertyInfo. Never throws.
    private bool TryGoPos(object goComp, out Vector3 pos)
    {
        pos = default;
        try
        {
            if (_getGoPos != null) { pos = _getGoPos(goComp); return true; }
            if (_piPosition?.GetValue(goComp) is Vector3 p) { pos = p; return true; }
        }
        catch { }
        return false;
    }

    // ── Snapshot queries for the rules ───────────────────────────────────────────────────────────────────────
    private McEnt? Ent(long uuid) => uuid != 0 && _ents.TryGetValue(uuid, out var e) ? e : null;

    private bool HasBuff(long target, int baseId)
    {
        foreach (var b in _buffs) if (b.Target == target && b.BaseId == baseId) return true;
        return false;
    }

    private bool TryBuff(long target, int baseId, out McBuff buff)
    {
        foreach (var b in _buffs) if (b.Target == target && b.BaseId == baseId) { buff = b; return true; }
        buff = default;
        return false;
    }

    private bool AnyBuff(int baseId, out McBuff buff)
    {
        foreach (var b in _buffs) if (b.BaseId == baseId) { buff = b; return true; }
        buff = default;
        return false;
    }

    private bool AnyMonster(int monsterId)
    {
        foreach (var e in _ents.Values) if (e.MonsterId == monsterId) return true;
        return false;
    }
}
