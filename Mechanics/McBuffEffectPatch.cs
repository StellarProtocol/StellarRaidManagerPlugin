using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Stellar.Abstractions.Services;

namespace Stellar.RaidManager;

/// <summary>
/// Captures a buff instance's PlayEffect / customize "effect ids" — the extra per-instance payload upstream
/// resonance-logs-cn reads off the wire (decoder.rs <c>decode_play_effect_ids</c> / <c>decode_customize_values</c>) and
/// that the client's <c>BuffItem</c> does NOT keep (dump.cs 175582: no effect field). Three Harmony PREFIXES (the
/// protobufs are pooled — a postfix can see them already returned/cleared), all plain reference/value params (no
/// <c>in</c> struct → no trampoline crash):
/// <list type="bullet">
/// <item><c>ZEntityCreator.parseBuff(ZEntity, BuffInfoSync, BuffEffectSync, bool, bool)</c> (dump.cs 233805) — the AOI
/// sync entry: AoiSyncDelta.BuffEffect (what upstream decodes off the wire) + the entity's BuffInfoSync.</item>
/// <item><c>BuffComp.OnAddBuff(BuffInfo pbBuff, bool isFromAppear)</c> (dump.cs 216242) — ids from the BuffInfo's own
/// LogicEffect PlayEffect entries.</item>
/// <item><c>BuffComp.OnBuffEffectSync(RepeatedField&lt;BuffEffect&gt;, bool, bool)</c> (dump.cs 216248, internal).</item>
/// </list>
/// Per <c>BuffEffect</c>: outer PlayEffect ids override when the effect carries an AddBuff/BuffChange logic entry; a
/// <c>BuffEventCustomize</c> (1002) event replaces them with its customize values; <c>BuffEventRemove</c> drops them.
/// Keyed by (host entId = uuid >> 16, BuffUuid). Recording is gated by <see cref="Active"/> (supported scene only).
/// Patched once on first need; the framework's IHarmonyHost unpatches on plugin dispose (never at runtime). Only a
/// patch-target / patch-failure warning is logged (the Experiment build keeps the per-capture diagnostics).
/// </summary>
internal static class McBuffEffectPatch
{
    public static bool Active;
    public static bool Installed { get; private set; }

    private static readonly Dictionary<(long Host, int Buff), int[]> _ids = new();
    private static Action<string>? _log;

    // EBuffEffectLogicPbType / EBuffEventType values (proto/zproto enum_e_buff_effect_logic_pb_type / _event_type).
    private const int LogicPlayEffect = 0, LogicStopAll = 15, LogicAddBuff = 18, LogicBuffChange = 19;
    private const int EventRemove = 2, EventCustomize = 1002;

    // Keyed by entId (uuid >> 16), not the full uuid: the local player's entity uuid carries the isClient bit
    // (Threat-Aggro-Tracking.md) which a wire HostUuid may not — the entId part is the stable identity.
    public static bool TryGet(long host, int buffUuid, out int[] ids) => _ids.TryGetValue((host >> 16, buffUuid), out ids!);
    public static void Clear() => _ids.Clear();

    public static void Install(Harmony harmony, Action<string> log)
    {
        if (Installed) return;
        Installed = true;
        _log = log;
        // PREFIXES, not postfixes: the BuffInfo / BuffEffect protobufs are POOLED (Rent/Return, dump.cs) — after the
        // game processed them they may already be returned and cleared, which a postfix would read as "no ids".
        var t = StellarInterop.FindType("Panda.ZGame.BuffComp");
        if (t == null) log("[MechCallout] BuffComp not found — BuffComp effect hooks off");
        else
        {
            Patch(harmony, StellarInterop.FindMethod(t, "OnAddBuff", 2), nameof(PrefixOnAddBuff));
            MethodInfo? sync = null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (m.Name == "OnBuffEffectSync" && m.GetParameters().Length == 3) { sync = m; break; }
            Patch(harmony, sync, nameof(PrefixOnBuffEffectSync));
        }
        // The AOI sync entry point: ZEntityCreator.parseBuff(ZEntity, BuffInfoSync, BuffEffectSync, bool, bool)
        // (dump.cs 233805, private static, reference/value params only — no `in` struct). It receives
        // AoiSyncDelta.BuffEffect (the typed BuffEffectSync upstream decodes from the wire) and the entity's
        // BuffInfoSync, so it sees every buff effect even if BuffComp.OnBuffEffectSync is inlined or bypassed.
        var creator = StellarInterop.FindType("Panda.ZGame.ZEntityCreator");
        MethodInfo? parse = null;
        if (creator != null)
            foreach (var m in creator.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "parseBuff" && m.GetParameters().Length == 5) { parse = m; break; }
        Patch(harmony, parse, nameof(PrefixParseBuff));
    }

    private static void Patch(Harmony h, MethodInfo? target, string prefix)
    {
        if (target == null) { _log?.Invoke($"[MechCallout] {prefix}: target not found"); return; }
        try { h.Patch(target, prefix: new HarmonyMethod(typeof(McBuffEffectPatch), prefix)); }
        catch (Exception ex) { _log?.Invoke($"[MechCallout] patch {target.Name} failed: {ex.Message}"); return; }
        foreach (var p in target.GetParameters()) Warm(p.ParameterType);   // bind the getters now, not mid-fight
    }

    // ── Hooks ────────────────────────────────────────────────────────────────────────────────────────────────
    private static void PrefixOnAddBuff(object __instance, object __0)
    {
        if (!Active || __0 == null) return;
        var pm = McPerf.Begin();
        try
        {
            long host = PLong(__0, "HostUuid");
            if (host == 0) host = HostUuid(__instance);
            ProcessBuffInfo(host, __0);
        }
        catch (Exception ex) { LogOnce("OnAddBuff", ex); }
        finally { McPerf.EndHook(pm); }
    }

    private static void PrefixOnBuffEffectSync(object __instance, object __0)
    {
        if (!Active || __0 == null) return;
        var pm = McPerf.Begin();
        try
        {
            long compHost = 0;
            for (int i = 0, n = Count(__0); i < n; i++)
            {
                var eff = At(__0, i);
                if (eff == null) continue;
                long host = PLong(eff, "HostUuid");
                if (host == 0) host = compHost != 0 ? compHost : (compHost = HostUuid(__instance));
                ProcessEffect(host, eff);
            }
        }
        catch (Exception ex) { LogOnce("OnBuffEffectSync", ex); }
        finally { McPerf.EndHook(pm); }
    }

    // __0 = ZEntity, __1 = BuffInfoSync (may be null), __2 = BuffEffectSync (may be null).
    private static void PrefixParseBuff(object __0, object __1, object __2)
    {
        if (!Active) return;
        var pm = McPerf.Begin();
        try
        {
            long entHost = __0 != null ? PLong(__0, "Uuid") : 0;
            if (__1 != null && Prop(__1, "BuffInfos") is { } infos)
                for (int i = 0, n = Count(infos); i < n; i++)
                    if (At(infos, i) is { } bi) ProcessBuffInfo(HostOr(PLong(bi, "HostUuid"), entHost), bi);
            if (__2 != null)
            {
                long syncHost = HostOr(PLong(__2, "Uuid"), entHost);
                if (Prop(__2, "BuffEffects") is { } effs)
                    for (int i = 0, n = Count(effs); i < n; i++)
                        if (At(effs, i) is { } eff) ProcessEffect(HostOr(PLong(eff, "HostUuid"), syncHost), eff);
            }
        }
        catch (Exception ex) { LogOnce("parseBuff", ex); }
        finally { McPerf.EndHook(pm); }
    }

    private static long HostOr(long a, long b) => a != 0 ? a : b;

    // A BuffInfo (add / snapshot): its own LogicEffect PlayEffect ids (upstream observed_buff).
    private static void ProcessBuffInfo(long host, object info)
    {
        int buff = PInt(info, "BuffUuid");
        if (buff == 0 || host == 0) return;
        var ids = PlayEffectIds(Prop(info, "LogicEffect"), out _);
        if (ids.Count > 0) Store(host, buff, ids);
    }

    // A BuffEffect (sync): outer PlayEffect ids override on AddBuff/BuffChange; Customize replaces; Remove drops.
    private static void ProcessEffect(long host, object eff)
    {
        int buff = PInt(eff, "BuffUuid");
        if (buff == 0 || host == 0) return;
        int type  = PInt(eff, "Type");
        var logic = Prop(eff, "LogicEffect");
        var ids = PlayEffectIds(logic, out bool addOrChange);
        if (type == EventRemove) _ids.Remove((host >> 16, buff));
        else
        {
            if (addOrChange && ids.Count > 0) Store(host, buff, ids);
            if (type == EventCustomize) { var custom = CustomizeValues(logic); if (custom.Count > 0) Store(host, buff, custom); }
        }
    }

    private static void Store(long host, int buff, List<int> ids)
    {
        if (_ids.Count > 4096) _ids.Clear();   // safety valve; entries are re-sent on the next sync/customize
        _ids[(host >> 16, buff)] = ids.ToArray();
    }

    // ── Logic-entry decoding (mirrors decoder.rs) ────────────────────────────────────────────────────────────
    // PlayEffect entries (EffectType 0) → BuffEffectLogicPlayEffect.effect_id (proto field 1, varint).
    private static List<int> PlayEffectIds(object? logicList, out bool hasAddOrChange)
    {
        hasAddOrChange = false;
        var res = new List<int>();
        if (logicList == null) return res;
        for (int i = 0, n = Count(logicList); i < n; i++)
        {
            var li = At(logicList, i);
            if (li == null) continue;
            int et = PInt(li, "EffectType");
            if (et == LogicAddBuff || et == LogicBuffChange) hasAddOrChange = true;
            if (et != LogicPlayEffect) continue;
            var raw = Bytes(Prop(li, "RawData"));
            if (raw != null && TryDecodeEffectId(raw, out int id)) res.Add(id);
        }
        return res;
    }

    // Customize values (logicIdx 1-4): every non-StopAll entry → a non-zero PlayEffect effect_id, else the raw 1-byte
    // / 2-byte little-endian integer (decode_customize_value).
    private static List<int> CustomizeValues(object? logicList)
    {
        var res = new List<int>();
        if (logicList == null) return res;
        for (int i = 0, n = Count(logicList); i < n; i++)
        {
            var li = At(logicList, i);
            if (li == null || PInt(li, "EffectType") == LogicStopAll) continue;
            var raw = Bytes(Prop(li, "RawData"));
            if (raw == null) continue;
            if (TryDecodeEffectId(raw, out int id) && id != 0) res.Add(id);
            else if (raw.Length == 1) res.Add(raw[0]);
            else if (raw.Length == 2) res.Add((short)(raw[0] | (raw[1] << 8)));
        }
        return res;
    }

    // Minimal protobuf read of `int32 effect_id = 1`. Succeeds only if the whole buffer parses; a missing field = 0
    // (proto3 default), like prost's decode.
    private static bool TryDecodeEffectId(byte[] b, out int id)
    {
        bool ok = TryReadVarintField(b, 1, out long v, requireField: false);
        id = (int)v;
        return ok;
    }

    // Whole-buffer protobuf walk returning varint field `field` (proto3: missing → 0). False on a malformed buffer, or
    // when requireField and the field is absent.
    private static bool TryReadVarintField(byte[] b, int want, out long value, bool requireField = true)
    {
        value = 0;
        bool found = false;
        int i = 0;
        while (i < b.Length)
        {
            if (!Varint(b, ref i, out ulong tag)) return false;
            int field = (int)(tag >> 3), wt = (int)(tag & 7);
            if (field == 0) return false;
            switch (wt)
            {
                case 0: if (!Varint(b, ref i, out ulong v)) return false; if (field == want) { value = (long)v; found = true; } break;
                case 1: i += 8; break;
                case 2: if (!Varint(b, ref i, out ulong len)) return false; i += (int)len; break;
                case 5: i += 4; break;
                default: return false;
            }
            if (i > b.Length) return false;
        }
        return found || !requireField;
    }

    private static bool Varint(byte[] b, ref int i, out ulong v)
    {
        v = 0;
        for (int shift = 0; shift < 64 && i < b.Length; shift += 7)
        {
            byte x = b[i++];
            v |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return true;
        }
        return false;
    }

    // ── Reflection helpers (IL2CPP fields surface as properties; RepeatedField isn't IEnumerable to us) ─────
    // These run inside the buff prefixes, which are hot in raids. PropertyInfo.GetValue / MethodInfo.Invoke allocate an
    // object[] and box every value per call (Plugin-Performance-Rules.md §2), so each (type, member) is bound ONCE into a
    // compiled delegate; integral/enum members get an unboxed long getter. If binding throws, the reflection call is
    // kept as the fallback (same result, just slower). Install() pre-binds the known proto types so the Expression
    // compiles happen on scene entry, not on the first buff mid-fight.
    private sealed class Acc
    {
        public Func<object, object?>?      Obj;   // reference-typed property, or ByteString.ToByteArray()
        public Func<object, long>?         Num;   // integral / enum property, widened to long, no box
        public Func<object, int, object?>? Item;  // RepeatedField indexer
    }
    private const string ToBytes = "ToByteArray()", Indexer = "[]";
    private const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Dictionary<(Type, string), Acc?> _acc = new();

    private static Acc? Get(Type t, string name)
    {
        if (!_acc.TryGetValue((t, name), out var a)) _acc[(t, name)] = a = Bind(t, name);
        return a;
    }

    private static Acc? Bind(Type t, string name)
    {
        if (name == ToBytes)
        {
            var m = t.GetMethod("ToByteArray", Type.EmptyTypes);
            if (m == null) return null;
            try
            {
                var p = Expression.Parameter(typeof(object));
                return new Acc { Obj = Expression.Lambda<Func<object, object?>>(
                    Expression.Convert(Expression.Call(Expression.Convert(p, t), m), typeof(object)), p).Compile() };
            }
            catch { return new Acc { Obj = o => m.Invoke(o, null) }; }
        }
        if (name == Indexer)
        {
            var pi = t.GetProperty("Item");                     // same lookup the reflection path always used
            if (pi == null) return null;
            try
            {
                var p = Expression.Parameter(typeof(object)); var i = Expression.Parameter(typeof(int));
                return new Acc { Item = Expression.Lambda<Func<object, int, object?>>(
                    Expression.Convert(Expression.Property(Expression.Convert(p, t), pi, i), typeof(object)), p, i).Compile() };
            }
            catch { return new Acc { Item = (o, i) => pi.GetValue(o, new object[] { i }) }; }
        }
        var prop = t.GetProperty(name, Inst);
        return prop == null ? null : BindProp(t, prop);
    }

    // Widening to long is exact for these types, so PInt/PLong return what Convert.ToInt32/64 on the boxed value did
    // (ulong is excluded — it could wrap — and stays on the boxed path).
    private static bool IsNum(Type pt)
    {
        if (pt.IsEnum) pt = Enum.GetUnderlyingType(pt);
        return pt == typeof(int) || pt == typeof(long) || pt == typeof(uint) || pt == typeof(short)
            || pt == typeof(ushort) || pt == typeof(byte) || pt == typeof(sbyte);
    }

    private static Acc BindProp(Type t, PropertyInfo pi)
    {
        try
        {
            var p = Expression.Parameter(typeof(object));
            var get = Expression.Property(Expression.Convert(p, t), pi);
            return IsNum(pi.PropertyType)
                ? new Acc { Num = Expression.Lambda<Func<object, long>>(Expression.Convert(get, typeof(long)), p).Compile() }
                : new Acc { Obj = Expression.Lambda<Func<object, object?>>(Expression.Convert(get, typeof(object)), p).Compile() };
        }
        catch { return new Acc { Obj = o => pi.GetValue(o) }; }
    }

    private static object? Prop(object o, string name)
    {
        var a = Get(o.GetType(), name);
        return a == null ? null : a.Obj != null ? a.Obj(o) : a.Num != null ? a.Num(o) : null;
    }
    private static int PInt(object o, string name)
    {
        var a = Get(o.GetType(), name);
        if (a?.Num == null) return Int(a?.Obj?.Invoke(o));
        long v = a.Num(o);
        return v is >= int.MinValue and <= int.MaxValue ? (int)v : 0;   // Convert.ToInt32 overflow → caught → 0
    }
    private static long PLong(object o, string name)
    {
        var a = Get(o.GetType(), name);
        return a?.Num != null ? a.Num(o) : Long(a?.Obj?.Invoke(o));
    }

    private static int     Count(object list)     => PInt(list, "Count");
    private static object? At(object list, int i) => Get(list.GetType(), Indexer)?.Item?.Invoke(list, i);

    // Pre-binds every member the hooks read, walking from the patched methods' parameter types into the nested proto
    // types (LogicEffect / BuffInfos / BuffEffects lists → their element type → RawData ByteString).
    private static readonly string[] _scalars = { "Count", "HostUuid", "BuffUuid", "BaseId", "Type", "Uuid", "EffectType" };
    private static readonly string[] _nested  = { "LogicEffect", "BuffInfos", "BuffEffects" };
    private static void Warm(Type? t, int depth = 0)
    {
        if (t == null || depth > 4 || t.IsValueType || _acc.ContainsKey((t, "Count"))) return;
        try
        {
            foreach (var m in _scalars) Get(t, m);
            if (Get(t, Indexer) != null) Warm(t.GetProperty("Item")?.PropertyType, depth + 1);
            foreach (var m in _nested)
                if (t.GetProperty(m, Inst) is { } pi) { Get(t, m); Warm(pi.PropertyType, depth + 1); }
            if (t.GetProperty("RawData", Inst) is { } raw) { Get(t, "RawData"); Get(raw.PropertyType, ToBytes); }
        }
        catch (Exception ex) { LogOnce("warm " + t.Name, ex); }
    }

    // ByteString → bytes via its own ToByteArray() (dump.cs: `public byte[] ToByteArray()`; the interop surfaces an
    // Il2CppStructArray<byte>). Never enumerate/index it element by element: Length / this[i] / CopyTo cost ~5 native
    // calls PER BYTE (Plugin-Performance-Rules.md §3). AsSpan() reads the length once and views the native buffer, so
    // ToArray() is one memcpy — byte-identical to the old per-element copy.
    private static byte[]? Bytes(object? bs)
    {
        if (bs == null) return null;
        var arr = Get(bs.GetType(), ToBytes)?.Obj?.Invoke(bs);
        if (arr is Il2CppStructArray<byte> sa) return sa.AsSpan().ToArray();
        if (arr is byte[] b) return b;
        if (arr is IEnumerable<byte> e) return new List<byte>(e).ToArray();
        if (arr is IEnumerable ne) { var l = new List<byte>(); foreach (var x in ne) l.Add(Convert.ToByte(x)); return l.ToArray(); }
        return null;
    }

    private static int  Int(object? o)  { try { return o == null ? 0 : Convert.ToInt32(o); } catch { return 0; } }
    private static long Long(object? o) { try { return o == null ? 0 : Convert.ToInt64(o); } catch { return 0; } }

    // BuffComp → ZComponent.Host (ZEntity) → Uuid (base-walk; mirrors TargetLens BuffTrackPatch.GetHostEntityUuid).
    private static Acc? _accHost, _accHostUuid;
    private static bool _hostResolved;
    private static long HostUuid(object comp)
    {
        if (!_hostResolved)
        {
            _hostResolved = true;
            PropertyInfo? piHost = null;
            for (var cur = comp.GetType(); cur != null && piHost == null; cur = cur.BaseType)
                piHost = cur.GetProperty("Host", Inst);
            var piUuid = piHost?.PropertyType.GetProperty("Uuid", BindingFlags.Public | BindingFlags.Instance);
            if (piHost != null) _accHost = BindProp(comp.GetType(), piHost);
            if (piUuid != null) _accHostUuid = BindProp(piHost!.PropertyType, piUuid);
        }
        try
        {
            var h = _accHost?.Obj?.Invoke(comp);
            if (h == null || _accHostUuid == null) return 0;
            return _accHostUuid.Num != null ? _accHostUuid.Num(h) : Long(_accHostUuid.Obj?.Invoke(h));
        }
        catch { return 0; }
    }

    private static readonly HashSet<string> _logged = new();
    private static void LogOnce(string where, Exception ex)
    {
        if (_logged.Add(where)) _log?.Invoke($"[MechCallout] {where} prefix: {ex.InnerException?.Message ?? ex.Message}");
    }
}
