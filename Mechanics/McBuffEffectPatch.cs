using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
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
        catch (Exception ex) { _log?.Invoke($"[MechCallout] patch {target.Name} failed: {ex.Message}"); }
    }

    // ── Hooks ────────────────────────────────────────────────────────────────────────────────────────────────
    private static void PrefixOnAddBuff(object __instance, object __0)
    {
        if (!Active || __0 == null) return;
        try
        {
            long host = Long(Prop(__0, "HostUuid"));
            if (host == 0) host = HostUuid(__instance);
            ProcessBuffInfo(host, __0);
        }
        catch (Exception ex) { LogOnce("OnAddBuff", ex); }
    }

    private static void PrefixOnBuffEffectSync(object __instance, object __0)
    {
        if (!Active || __0 == null) return;
        try
        {
            long compHost = 0;
            foreach (var eff in Items(__0))
            {
                if (eff == null) continue;
                long host = Long(Prop(eff, "HostUuid"));
                if (host == 0) host = compHost != 0 ? compHost : (compHost = HostUuid(__instance));
                ProcessEffect(host, eff);
            }
        }
        catch (Exception ex) { LogOnce("OnBuffEffectSync", ex); }
    }

    // __0 = ZEntity, __1 = BuffInfoSync (may be null), __2 = BuffEffectSync (may be null).
    private static void PrefixParseBuff(object __0, object __1, object __2)
    {
        if (!Active) return;
        try
        {
            long entHost = __0 != null ? Long(Prop(__0, "Uuid")) : 0;
            if (__1 != null && Prop(__1, "BuffInfos") is { } infos)
                foreach (var bi in Items(infos))
                    if (bi != null) ProcessBuffInfo(HostOr(Long(Prop(bi, "HostUuid")), entHost), bi);
            if (__2 != null)
            {
                long syncHost = HostOr(Long(Prop(__2, "Uuid")), entHost);
                if (Prop(__2, "BuffEffects") is { } effs)
                    foreach (var eff in Items(effs))
                        if (eff != null) ProcessEffect(HostOr(Long(Prop(eff, "HostUuid")), syncHost), eff);
            }
        }
        catch (Exception ex) { LogOnce("parseBuff", ex); }
    }

    private static long HostOr(long a, long b) => a != 0 ? a : b;

    // A BuffInfo (add / snapshot): its own LogicEffect PlayEffect ids (upstream observed_buff).
    private static void ProcessBuffInfo(long host, object info)
    {
        int buff = Int(Prop(info, "BuffUuid"));
        if (buff == 0 || host == 0) return;
        var ids = PlayEffectIds(Prop(info, "LogicEffect"), out _);
        if (ids.Count > 0) Store(host, buff, ids);
    }

    // A BuffEffect (sync): outer PlayEffect ids override on AddBuff/BuffChange; Customize replaces; Remove drops.
    private static void ProcessEffect(long host, object eff)
    {
        int buff = Int(Prop(eff, "BuffUuid"));
        if (buff == 0 || host == 0) return;
        int type  = Int(Prop(eff, "Type"));
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
        foreach (var li in Items(logicList))
        {
            if (li == null) continue;
            int et = Int(Prop(li, "EffectType"));
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
        foreach (var li in Items(logicList))
        {
            if (li == null || Int(Prop(li, "EffectType")) == LogicStopAll) continue;
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
    private static readonly Dictionary<(Type, string), PropertyInfo?> _props = new();

    private static object? Prop(object o, string name)
    {
        var key = (o.GetType(), name);
        if (!_props.TryGetValue(key, out var pi))
            _props[key] = pi = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return pi?.GetValue(o);
    }

    private static IEnumerable<object?> Items(object list)
    {
        int n = Int(Prop(list, "Count"));
        var item = list.GetType().GetProperty("Item");
        for (int i = 0; i < n; i++) yield return item?.GetValue(list, new object[] { i });
    }

    // ByteString → bytes via its own ToByteArray() (dump.cs: `public byte[] ToByteArray()`; the interop surfaces an
    // Il2Cpp array, enumerated generically).
    private static byte[]? Bytes(object? bs)
    {
        if (bs == null) return null;
        var arr = bs.GetType().GetMethod("ToByteArray", Type.EmptyTypes)?.Invoke(bs, null);
        if (arr is byte[] b) return b;
        if (arr is IEnumerable<byte> e) return new List<byte>(e).ToArray();
        if (arr is IEnumerable ne) { var l = new List<byte>(); foreach (var x in ne) l.Add(Convert.ToByte(x)); return l.ToArray(); }
        return null;
    }

    private static int  Int(object? o)  { try { return o == null ? 0 : Convert.ToInt32(o); } catch { return 0; } }
    private static long Long(object? o) { try { return o == null ? 0 : Convert.ToInt64(o); } catch { return 0; } }

    // BuffComp → ZComponent.Host (ZEntity) → Uuid (base-walk; mirrors TargetLens BuffTrackPatch.GetHostEntityUuid).
    private static PropertyInfo? _piHost, _piHostUuid;
    private static bool _hostResolved;
    private static long HostUuid(object comp)
    {
        if (!_hostResolved)
        {
            _hostResolved = true;
            for (var cur = comp.GetType(); cur != null && _piHost == null; cur = cur.BaseType)
                _piHost = cur.GetProperty("Host", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _piHostUuid = _piHost?.PropertyType.GetProperty("Uuid", BindingFlags.Public | BindingFlags.Instance);
        }
        try { var h = _piHost?.GetValue(comp); return h == null ? 0 : Long(_piHostUuid?.GetValue(h)); } catch { return 0; }
    }

    private static readonly HashSet<string> _logged = new();
    private static void LogOnce(string where, Exception ex)
    {
        if (_logged.Add(where)) _log?.Invoke($"[MechCallout] {where} prefix: {ex.InnerException?.Message ?? ex.Message}");
    }
}
