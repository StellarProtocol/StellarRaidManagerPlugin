using System;
using System.Collections.Generic;
using System.Reflection;

namespace Stellar.RaidManager;

// Typed-delegate fast paths for the tracker's hot reflection reads (raid perf, 2026-11-10). The scan reads every
// scanned entity's FULL buff list 5×/s (and every AOI entity's once a second in the wide pass); through
// PropertyInfo.GetValue / MethodInfo.Invoke each read paid the reflection dispatch PLUS garbage: an object[] per
// indexer/method call, a box per int/long/bool result. In the 11-10 raid that garbage drove a managed gen-0 GC into
// most of RaidManager's >10 ms frames. Here each interop getter/method is bound ONCE into an open-instance delegate
// (Func<object, …>, one cast + one call, no boxing). Every builder returns null when the member's shape doesn't
// allow it (value-type declaring type, unexpected return type, …) and the caller keeps the old reflection path, so a
// shape surprise costs speed, never correctness. Same values, same fallbacks — no behaviour change.
// (The interop wrapper objects themselves — BuffItem, BuffComp, ZEntity … — are still created per read; avoiding
// those would mean raw field-offset reads, not attempted here.)
internal sealed partial class MechanicCalloutTracker
{
    // ── Generic binders ──────────────────────────────────────────────────────────────────────────────────────
    private static MethodInfo Helper(string name, params Type[] args) =>
        typeof(MechanicCalloutTracker).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(args);

    private static bool ReturnFits<TR>(Type rt) => rt.IsValueType ? rt == typeof(TR) : typeof(TR).IsAssignableFrom(rt);

    // Instance method, no args → Func<object, TR>. A reference return binds covariantly (ZEntity → object).
    private static Func<object, TR>? Bind0<TR>(MethodInfo? m)
    {
        try
        {
            if (m == null || m.IsStatic || m.GetParameters().Length != 0) return null;
            var dt = m.DeclaringType;
            if (dt == null || dt.IsValueType || dt.ContainsGenericParameters || !ReturnFits<TR>(m.ReturnType)) return null;
            return (Func<object, TR>?)Helper(nameof(Call0), dt, m.ReturnType).Invoke(null, new object[] { m });
        }
        catch { return null; }
    }

    // Instance method, one arg of exactly TA → Func<object, TA, TR>.
    private static Func<object, TA, TR>? Bind1<TA, TR>(MethodInfo? m)
    {
        try
        {
            if (m == null || m.IsStatic || m.GetParameters() is not { Length: 1 } ps || ps[0].ParameterType != typeof(TA)) return null;
            var dt = m.DeclaringType;
            if (dt == null || dt.IsValueType || dt.ContainsGenericParameters || !ReturnFits<TR>(m.ReturnType)) return null;
            return (Func<object, TA, TR>?)Helper(nameof(Call1), dt, typeof(TA), m.ReturnType).Invoke(null, new object[] { m });
        }
        catch { return null; }
    }

    // Static method, no args, reference return → Func<object?> (ZEntityMgr.Instance).
    private static Func<object?>? BindStatic0(MethodInfo? m)
    {
        try
        {
            if (m == null || !m.IsStatic || m.GetParameters().Length != 0 || m.ReturnType.IsValueType) return null;
            return (Func<object?>?)Helper(nameof(StaticCall0), m.ReturnType).Invoke(null, new object[] { m });
        }
        catch { return null; }
    }

    private static Func<object, TRet> Call0<T, TRet>(MethodInfo m) where T : class
    {
        var d = (Func<T, TRet>)Delegate.CreateDelegate(typeof(Func<T, TRet>), m);
        return o => d((T)o);
    }

    private static Func<object, TA, TRet> Call1<T, TA, TRet>(MethodInfo m) where T : class
    {
        var d = (Func<T, TA, TRet>)Delegate.CreateDelegate(typeof(Func<T, TA, TRet>), m);
        return (o, a) => d((T)o, a);
    }

    private static Func<TRet> StaticCall0<TRet>(MethodInfo m) => (Func<TRet>)Delegate.CreateDelegate(typeof(Func<TRet>), m);

    // ZEntity.GetAttr<long>(EAttrType attr, bool) with the attr baked in → Func<object, long>.
    private static Func<object, long> AttrCall<T, TE>(MethodInfo m, object box) where T : class where TE : struct
    {
        var d = (Func<T, TE, bool, long>)Delegate.CreateDelegate(typeof(Func<T, TE, bool, long>), m);
        var attr = (TE)box;
        return o => d((T)o, attr, true);
    }

    // ── Property reads (cached per PropertyInfo; static — ReadInt/ReadLong are static) ───────────────────────
    private static readonly Dictionary<PropertyInfo, Func<object, object?>?> _refGetters = new();
    private static readonly Dictionary<PropertyInfo, Func<object, long>?>    _numGetters = new();

    // Reference-typed property (BuffComp, buff list, Model, ModelGoComp, entityDict_) — PropertyInfo.GetValue fallback.
    private static object? GetRef(PropertyInfo pi, object o)
    {
        if (!_refGetters.TryGetValue(pi, out var g))
            _refGetters[pi] = g = pi.PropertyType.IsValueType ? null : Bind0<object?>(pi.GetGetMethod(true));
        return g != null ? g(o) : pi.GetValue(o);
    }

    // Null-safe form for the call sites that used `_pi?.GetValue(o)`.
    private static object? Ref(PropertyInfo? pi, object? o) => pi == null || o == null ? null : GetRef(pi, o);

    // int/long property without the box; false = no fast getter (caller falls back to GetValue + Convert).
    private static bool TryNum(PropertyInfo pi, object o, out long v)
    {
        if (!_numGetters.TryGetValue(pi, out var g)) _numGetters[pi] = g = MakeNumGetter(pi);
        if (g == null) { v = 0; return false; }
        v = g(o);
        return true;
    }

    private static Func<object, long>? MakeNumGetter(PropertyInfo pi)
    {
        var getter = pi.GetGetMethod(true);
        if (pi.PropertyType == typeof(int))  { var g = Bind0<int>(getter);  return g == null ? null : o => g(o); }
        if (pi.PropertyType == typeof(long)) { var g = Bind0<long>(getter); return g; }
        return null;
    }

    // ── ZEntityMgr.Instance, once per poll ───────────────────────────────────────────────────────────────────
    // Every GetEntity used to re-read the singleton (a fresh interop wrapper each time); the scan, casts and rules
    // call GetEntity dozens of times per poll. The wrapper is re-fetched at the start of every poll.
    private int     _pollSeq, _mgrSeq = -1;
    private long    _mgrAt;
    private object? _mgr;
    private Func<object?>? _getMgr;
    private bool    _fastResolved;

    private object? EntMgr()
    {
        long now = Environment.TickCount64;
        if (_mgrSeq == _pollSeq && now - _mgrAt < 500) return _mgr;   // callers outside the poll (recorder) refresh too
        _mgrSeq = _pollSeq; _mgrAt = now;
        _mgr = _getMgr != null ? _getMgr() : _piEntMgrInstance?.GetValue(null);
        return _mgr;
    }

    // ── Bound once after EnsureApi ───────────────────────────────────────────────────────────────────────────
    private Func<object, long, object?>? _getEntityFn;
    private Func<object, object?>? _getSkillCompFn;

    private void ResolveFastPaths()
    {
        if (_fastResolved) return;
        _fastResolved = true;
        _getMgr      = BindStatic0(_piEntMgrInstance?.GetGetMethod(true));
        _getEntityFn = Bind1<long, object?>(_miGetEntity);
        _services.Log.Info($"[MechPerf] fast paths: mgr={_getMgr != null} getEntity={_getEntityFn != null} " +
                           $"attrLong={(_miGetAttrLong != null && _boxAttrHp != null && AttrSlotFor(_boxAttrHp).Fn != null)}");
    }

    // ── GetAttr<long> per attr ───────────────────────────────────────────────────────────────────────────────
    // Proven = this attr has read non-zero through GetAttr<long> at least once, so the instantiation reads it cleanly
    // and a later 0 is a real 0 — no GetAttr<object> + ToString + parse fallback (which allocated on every dead-HP /
    // facing-0 read). Until proven, the fallback runs exactly as before.
    private sealed class AttrSlot { public Func<object, long>? Fn; public bool Proven; }
    private readonly Dictionary<object, AttrSlot> _attrSlots = new();

    private AttrSlot AttrSlotFor(object box)
    {
        if (_attrSlots.TryGetValue(box, out var s)) return s;
        s = new AttrSlot();
        try
        {
            var m = _miGetAttrLong;
            if (m != null && m.ReturnType == typeof(long) && m.DeclaringType is { IsValueType: false } dt
                && m.GetParameters() is { Length: 2 } ps && ps[0].ParameterType == box.GetType() && ps[0].ParameterType.IsValueType
                && ps[1].ParameterType == typeof(bool))
                s.Fn = (Func<object, long>?)Helper(nameof(AttrCall), dt, ps[0].ParameterType).Invoke(null, new[] { m, box });
        }
        catch { s.Fn = null; }
        _attrSlots[box] = s;
        return s;
    }

    // ── AOI key walk ─────────────────────────────────────────────────────────────────────────────────────────
    // entityDict_.Keys → GetEnumerator → MoveNext/Current, bound once. The reflection walk boxed every key and every
    // MoveNext result and collected them into a fresh List<object> per scan.
    private bool          _walkResolved;
    private PropertyInfo? _piDictKeys;
    private Func<object, object?>? _dictKeysFn, _keysEnumFn;
    private Func<object, bool>? _enumMoveFn;
    private Func<object, long>? _enumCurFn;

    private void ResolveKeyWalk(object dict)
    {
        _walkResolved = true;
        try
        {
            _piDictKeys = dict.GetType().GetProperty("Keys");
            var keysGet = _piDictKeys?.GetGetMethod();
            var getEnum = keysGet != null ? FindNoArg(keysGet.ReturnType, "GetEnumerator") : null;
            var enT     = getEnum?.ReturnType;
            _dictKeysFn = Bind0<object?>(keysGet);
            _keysEnumFn = Bind0<object?>(getEnum);
            _enumMoveFn = Bind0<bool>(enT != null ? FindNoArg(enT, "MoveNext") : null);
            _enumCurFn  = Bind0<long>(enT?.GetProperty("Current")?.GetGetMethod());
        }
        catch { }
        if (_dictKeysFn == null || _keysEnumFn == null || _enumMoveFn == null || _enumCurFn == null)
        { _dictKeysFn = _keysEnumFn = null; _enumMoveFn = null; _enumCurFn = null; }
        _services.Log.Info($"[MechPerf] fast key walk={_enumCurFn != null}");
    }

    // ── ZList<T> / List<T> Count + indexer ───────────────────────────────────────────────────────────────────
    private readonly Dictionary<Type, (Func<object, int>? Count, Func<object, int, object?>? Item)> _listFast = new();

    private (Func<object, int>? Count, Func<object, int, object?>? Item) ListFast(object list)
    {
        var t = list.GetType();
        if (_listFast.TryGetValue(t, out var f)) return f;
        var (pc, pi) = ListProps(list);
        f = (Bind0<int>(pc?.GetGetMethod()), Bind1<int, object?>(pi?.GetGetMethod()));
        _listFast[t] = f;
        return f;
    }
}
