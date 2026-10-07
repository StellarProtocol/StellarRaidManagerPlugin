using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// Monster config id (MonsterTable id — what every per-scene monster rule keys on). In game (Cursed Tomb, build
// 436ac6a) the EAttrType.AttrId (10) read returned 0 for EVERY monster, so towers/boss/clones/orbs never matched.
// Ladder, cheapest + validated first — the same sources TargetLens's NearbyMonsterTracker uses for monster identity:
//   1. ZEntity.BaseId            — public int auto-property (dump.cs: ZEntity `<BaseId>k__BackingField`, `BaseId { get; set; }`)
//   2. ZEntity.entRow_.Id        — the bound IEntityConfig row (private field → interop property; row type varies)
//   3. AttrId (EAttrType 10)     — last fallback (the read that failed)
// A 0 is never cached as final: the entity may not be initialised on first sight, so it's retried with a 500 ms
// backoff.
internal sealed partial class MechanicCalloutTracker
{
    private readonly Dictionary<long, int>  _monsterIds = new();          // uuid → id (non-zero only)
    private readonly Dictionary<long, long> _monsterIdRetryAt = new();    // uuid → TickCount64 of the next attempt

    private bool          _monIdResolved;
    private PropertyInfo? _piEntBaseId;
    private PropertyInfo? _piEntRowProp;
    private FieldInfo?    _fiEntRowField;
    private Type?         _entRowType;
    private PropertyInfo? _piEntRowId;

    private int MonsterIdOf(long uuid, object ent)
    {
        if (_monsterIds.TryGetValue(uuid, out int id)) return id;
        long now = Environment.TickCount64;
        if (_monsterIdRetryAt.TryGetValue(uuid, out long at) && now < at) return 0;

        id = ReadMonsterId(ent);
        if (id == 0) { _monsterIdRetryAt[uuid] = now + 500; return 0; }
        _monsterIds[uuid] = id;
        _monsterIdRetryAt.Remove(uuid);
        return id;
    }

    private int ReadMonsterId(object ent)
    {
        if (!_monIdResolved) ResolveMonsterIdRefl();
        try
        {
            if (_piEntBaseId != null && Convert.ToInt32(_piEntBaseId.GetValue(ent) ?? 0) is int b && b != 0)
                return b;
        }
        catch { }
        try
        {
            var row = _piEntRowProp != null ? _piEntRowProp.GetValue(ent) : _fiEntRowField?.GetValue(ent);
            if (row != null)
            {
                var t = row.GetType();
                if (t != _entRowType) { _entRowType = t; _piEntRowId = t.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance); }
                if (_piEntRowId != null && Convert.ToInt32(_piEntRowId.GetValue(row) ?? 0) is int r && r != 0)
                    return r;
            }
        }
        catch { }
        return (int)ReadAttrLong(ent, _boxAttrId);
    }

    private void ResolveMonsterIdRefl()
    {
        _monIdResolved = true;
        var entType = StellarInterop.FindType("Panda.ZGame.ZEntity");
        if (entType == null) return;
        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _piEntBaseId   = entType.GetProperty("BaseId", BindingFlags.Public | BindingFlags.Instance);
        _piEntRowProp  = entType.GetProperty("entRow_", any);
        if (_piEntRowProp == null) _fiEntRowField = entType.GetField("entRow_", any);
    }

    private void ResetMonsterIds()
    {
        _monsterIds.Clear(); _monsterIdRetryAt.Clear();
    }
}
