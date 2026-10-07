using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

// Reflection side of MechanicCalloutTracker: scene id, AOI entity enumeration, entity → BuffComp → buff list, BuffItem
// fields and the AttrName read. Everything is resolved once and guarded; IL2CPP fields surface as PROPERTIES, so all
// member lookups go through GetProperties()/GetProperty (never FieldInfo for IL2CPP types). Mirrors the proven paths in
// Experiment FollowController (entity dict walk / GetEntity / GetAttr<T>) and TargetLens TargetBuffTracker (buff lists).
internal sealed partial class MechanicCalloutTracker
{
    // ── Scene id ─────────────────────────────────────────────────────────────────────────────────────────────
    // Bokura.Table.ITable.CurrentSceneId (static int, dump.cs 883973) — the SceneTable key, same read AutoGather
    // validated for its navmesh keying (Collision-and-Navmesh-Bake.md). Fallback: the framework's
    // IClientState.CurrentSceneName, documented as "currently a numeric scene id".
    private bool          _sceneTried;
    private PropertyInfo? _piSceneId;
    private FieldInfo?    _fiSceneId;   // static on a plain (non-IL2CPP-object) class may surface as a field

    private int FrameworkSceneId
    {
        get { try { return int.TryParse(_services.ClientState.CurrentSceneName, out int v) ? v : 0; } catch { return 0; } }
    }

    private int ReadSceneId()
    {
        if (!_sceneTried)
        {
            _sceneTried = true;
            var t = StellarInterop.FindType("Bokura.Table.ITable");
            if (t != null)
            {
                const BindingFlags sa = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
                _piSceneId = t.GetProperty("CurrentSceneId", sa);
                if (_piSceneId == null) _fiSceneId = t.GetField("CurrentSceneId", sa);
            }
        }
        int id = 0;
        try { if (_piSceneId != null) id = Convert.ToInt32(_piSceneId.GetValue(null)); } catch { }
        try { if (id == 0 && _fiSceneId != null) id = Convert.ToInt32(_fiSceneId.GetValue(null)); } catch { }
        return id != 0 ? id : FrameworkSceneId;
    }

    // ── Entity API ───────────────────────────────────────────────────────────────────────────────────────────
    private bool          _apiResolved, _apiOk;
    private PropertyInfo? _piEntMgrInstance;   // ZEntityMgr.Instance (ZSingleton → FlattenHierarchy)
    private PropertyInfo? _piEntityDict;       // ZEntityMgr.entityDict_
    private MethodInfo?   _miGetEntity;        // ZEntityMgr.GetEntity(long)
    private PropertyInfo? _piBuffComp;         // ZEntity.BuffComp
    private MethodInfo?   _miGetAttrObject;    // ZEntity.GetAttr<object>(EAttrType, bool) — AttrName read
    private object?       _boxAttrName;        // boxed EAttrType.AttrName (= 1)
    private MethodInfo?   _miGetAttrLong;      // ZEntity.GetAttr<long>(EAttrType, bool) — AttrId / AttrDir / AttrSkillId
    private object?       _boxAttrId, _boxAttrDir, _boxAttrSkillId;   // EAttrType 10 / 50 / 100
    private object?       _boxAttrHp, _boxAttrMaxHp;                  // EAttrType 11310 / 11320 (dead check)
    private PropertyInfo? _piModel, _piModelGoComp, _piPosition;       // ZEntity.Model.ModelGoComp.Position (FollowController)
    private Func<object, UnityEngine.Vector3>? _getGoPos;                // typed ModelGoComp.get_Position (see MakeGetter)
    private PropertyInfo? _piRotation;                                   // ModelGoComp.Rotation (rendered model yaw)
    private Func<object, UnityEngine.Quaternion>? _getGoRot;             // typed ModelGoComp.get_Rotation

    private bool EnsureApi()
    {
        if (_apiResolved) return _apiOk;
        _apiResolved = true;
        try
        {
            var entMgr   = StellarInterop.FindType("Panda.ZGame.ZEntityMgr");
            var entType  = StellarInterop.FindType("Panda.ZGame.ZEntity");
            var attrEnum = StellarInterop.FindType("Zproto.EAttrType");
            if (entMgr == null || entType == null) { _services.Log.Warning("[MechCallout] ZEntityMgr/ZEntity not found"); return false; }

            const BindingFlags pubInst = BindingFlags.Public | BindingFlags.Instance;
            const BindingFlags anyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _piEntMgrInstance = entMgr.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            _piEntityDict     = entMgr.GetProperty("entityDict_", anyInst);
            foreach (var m in entMgr.GetMethods(pubInst))
                if (m.Name == "GetEntity" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(long))
                { _miGetEntity = m; break; }
            _piBuffComp = entType.GetProperty("BuffComp", pubInst);

            if (attrEnum != null)
            {
                foreach (var m in entType.GetMethods(pubInst))
                {
                    if (m.Name != "GetAttr" || !m.IsGenericMethodDefinition) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == attrEnum && ps[1].ParameterType == typeof(bool))
                    {
                        _miGetAttrObject = m.MakeGenericMethod(typeof(object));
                        _miGetAttrLong   = m.MakeGenericMethod(typeof(long));   // same instantiation FollowController uses
                        break;
                    }
                }
                try
                {
                    _boxAttrName    = Enum.ToObject(attrEnum, 1);
                    _boxAttrId      = Enum.ToObject(attrEnum, 10);    // monster config id (upstream ATTR_ID 0x0a)
                    _boxAttrDir     = Enum.ToObject(attrEnum, 50);    // facing in centidegrees (upstream ATTR_FACING 0x32)
                    _boxAttrSkillId = Enum.ToObject(attrEnum, 100);   // current skill id (upstream cast signal)
                    _boxAttrHp      = Enum.ToObject(attrEnum, 11310); // AttrHp
                    _boxAttrMaxHp   = Enum.ToObject(attrEnum, 11320); // AttrMaxHp
                }
                catch { }
            }
            var modelT = StellarInterop.FindType("Panda.ZGame.ZModel");
            _piModel = entType.GetProperty("Model", pubInst);
            if (modelT != null)
            {
                _piModelGoComp = modelT.GetProperty("ModelGoComp", pubInst);
                _piPosition    = _piModelGoComp?.PropertyType?.GetProperty("Position", pubInst);
                _getGoPos      = MakeGetter<UnityEngine.Vector3>(_piPosition);
                _piRotation    = _piModelGoComp?.PropertyType?.GetProperty("Rotation", pubInst);
                _getGoRot      = MakeGetter<UnityEngine.Quaternion>(_piRotation);
            }

            _apiOk = _piEntMgrInstance != null && _piEntityDict != null && _miGetEntity != null && _piBuffComp != null;
            if (!_apiOk)
                _services.Log.Warning($"[MechCallout] entity api incomplete (dict={_piEntityDict != null} getEnt={_miGetEntity != null} " +
                                      $"buffComp={_piBuffComp != null}) — mechanic callouts off");
            return _apiOk;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[MechCallout] api resolve error: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
    }

    // Open-instance delegate over an interop property getter, wrapped once as Func<object, TR>: a per-frame read is
    // then one cast + one call — no MethodInfo.Invoke, no struct boxing (PropertyInfo.GetValue allocates a box per
    // read). Null (→ PropertyInfo fallback) if the getter's shape doesn't allow it. Used for ModelGoComp.Position
    // (Vector3) and .Rotation (Quaternion).
    private static Func<object, TR>? MakeGetter<TR>(PropertyInfo? pi) where TR : struct
    {
        try
        {
            var getter = pi?.GetGetMethod();
            if (getter == null || getter.IsStatic || pi!.PropertyType != typeof(TR) || pi.DeclaringType!.IsValueType) return null;
            var make = typeof(MechanicCalloutTracker).GetMethod(nameof(MakeTypedGetter), BindingFlags.NonPublic | BindingFlags.Static)!
                                                     .MakeGenericMethod(pi.DeclaringType, typeof(TR));
            return (Func<object, TR>?)make.Invoke(null, new object[] { getter });
        }
        catch { return null; }
    }

    private static Func<object, TR> MakeTypedGetter<T, TR>(MethodInfo getter) where T : class
    {
        var d = (Func<T, TR>)Delegate.CreateDelegate(typeof(Func<T, TR>), getter);
        return o => d((T)o);
    }

    private object? GetEntity(long uuid)
    {
        try
        {
            var mgr = _piEntMgrInstance!.GetValue(null);
            return mgr == null ? null : _miGetEntity!.Invoke(mgr, new object[] { uuid });
        }
        catch { return null; }
    }

    // AOI uuids from ZEntityMgr.entityDict_ keys — players (EntChar) and, when asked, monster/dummy candidates
    // (EntMonster/EntDummy; the caller filters those by AttrId) — plus the local player if the dict walk missed it.
    private readonly List<long> _uuidScratch = new();
    private List<long> EntityUuids(long localUuid, bool withMonsters, bool allTypes = false)
    {
        _uuidScratch.Clear();
        bool sawLocal = false;
        try
        {
            var mgr  = _piEntMgrInstance!.GetValue(null);
            var dict = mgr != null ? _piEntityDict!.GetValue(mgr) : null;
            var keys = dict?.GetType().GetProperty("Keys")?.GetValue(dict);
            if (keys != null)
                foreach (var k in WalkIl2Cpp(keys))
                {
                    long uuid = Convert.ToInt64(k);
                    long type = (uuid >> 6) & 31;
                    if (!allTypes && type != EntChar && !(withMonsters && (type == EntMonster || type == EntDummy))) continue;
                    if (uuid == localUuid) sawLocal = true;
                    _uuidScratch.Add(uuid);
                }
        }
        catch (Exception ex) { LogErrOnce("entity walk: " + (ex.InnerException?.Message ?? ex.Message)); }
        if (!sawLocal && localUuid != 0) _uuidScratch.Add(localUuid);
        return _uuidScratch;
    }

    // Il2Cpp enumerable walk via GetEnumerator/MoveNext/Current (Dictionary.KeyCollection isn't IEnumerable to us).
    private static List<object> WalkIl2Cpp(object collection)
    {
        var res = new List<object>();
        var getEnum = FindNoArg(collection.GetType(), "GetEnumerator");
        var en = getEnum?.Invoke(collection, null);
        if (en == null) return res;
        var enT  = en.GetType();
        var move = FindNoArg(enT, "MoveNext");
        var cur  = enT.GetProperty("Current");
        if (move == null || cur == null) return res;
        while ((bool)move.Invoke(en, null)!)
        {
            var v = cur.GetValue(en);
            if (v != null) res.Add(v);
        }
        return res;
    }

    private static MethodInfo? FindNoArg(Type t, string name)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == name && m.GetParameters().Length == 0) return m;
        return null;
    }

    // ── Buff lists ───────────────────────────────────────────────────────────────────────────────────────────
    private bool          _listsResolved;
    private PropertyInfo? _piFullList;     // pBuffList_ / buffList_ — unfiltered (mechanic debuffs live here)

    // Priority pick by name over GetProperties() (order is non-deterministic; GetProperty(name, flags) misses IL2CPP
    // privates) — Buff-Tracking.md §2, same as TargetBuffTracker.ResolveShowedList.
    private void ResolveLists(object comp)
    {
        _listsResolved = true;
        int fullPrio = 99;
        foreach (var p in comp.GetType().GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            int fp = p.Name switch { "pBuffList_" => 0, "buffList_" => 1, _ => 99 };
            if (fp < fullPrio) { _piFullList = p; fullPrio = fp; }
        }
        if (_piFullList == null) _services.Log.Warning("[MechCallout] BuffComp full buff list not found");
    }

    // ZList<T>/List<T> are not IEnumerable from here — Count + Item indexer, PropertyInfos cached per list TYPE (a
    // dictionary, not a single slot: nested reads of two lists of different generic types would otherwise swap the
    // indexer under the outer loop — Mechanic-Callouts.md "nested reflection over two buff lists").
    private readonly Dictionary<Type, (PropertyInfo? Count, PropertyInfo? Item)> _listProps = new();

    private (PropertyInfo? Count, PropertyInfo? Item) ListProps(object list)
    {
        var t = list.GetType();
        if (!_listProps.TryGetValue(t, out var p)) _listProps[t] = p = (t.GetProperty("Count"), t.GetProperty("Item"));
        return p;
    }

    private int ListCount(object list)
    {
        try { return Convert.ToInt32(ListProps(list).Count?.GetValue(list) ?? 0); } catch { return 0; }
    }

    private object? ListItem(object list, int i)
    {
        try { return ListProps(list).Item?.GetValue(list, new object[] { i }); } catch { return null; }
    }

    private bool          _itemResolved;
    private PropertyInfo? _piItemUuid, _piItemBaseId, _piItemLayer, _piItemDuration, _piItemCreate, _piItemFire;

    // BuffItem exposes BuffBaseId directly (Buff-Tracking.md §3) — no derivation from BuffUuid needed.
    private void ResolveItemFields(object item)
    {
        _itemResolved = true;
        foreach (var p in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if      (p.Name == "BuffUuid")   _piItemUuid     = p;
            else if (p.Name == "BuffBaseId") _piItemBaseId   = p;
            else if (p.Name == "Layer")      _piItemLayer    = p;
            else if (p.Name == "Duration")   _piItemDuration = p;
            else if (p.Name == "CreateTime") _piItemCreate   = p;
            else if (p.Name == "FireUuid")   _piItemFire     = p;
        }
        if (_piItemBaseId == null) _services.Log.Warning("[MechCallout] BuffItem.BuffBaseId not found");
    }

    private static int ReadInt(PropertyInfo pi, object o)
    {
        try { return Convert.ToInt32(pi.GetValue(o) ?? 0); } catch { return 0; }
    }

    private static long ReadLong(PropertyInfo? pi, object o)
    {
        if (pi == null) return 0;
        try { return Convert.ToInt64(pi.GetValue(o) ?? 0L); } catch { return 0; }
    }

    // AttrName (EAttrType 1) off the live entity via GetAttr<object> — GetAttr<T> has no string instantiation
    // (Threat-Aggro-Tracking.md technique (a)). The native get_Value fallback (technique (b)) is not ported; the
    // PartyRoster/CombatLookup/self rungs of the ladder cover a miss.
    private string ReadAttrName(long uuid)
    {
        if (_miGetAttrObject == null || _boxAttrName == null) return "";
        var ent = GetEntity(uuid);
        if (ent == null) return "";
        try { return _miGetAttrObject.Invoke(ent, new object[] { _boxAttrName, true })?.ToString() ?? ""; }
        catch { return ""; }
    }
}
