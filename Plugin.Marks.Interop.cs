using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — the IL2CPP interop layer ──────────────────────────────────────────────────────────
//
// PORTED from StellarExperimentPlugin\Plugin.PunctuateSpike*.cs (both the placement and read paths are VALIDATED
// in-game 2026-09-30 — full context in Knowledge Base\Marker-and-Raid-Coordination.md). This file is the
// reflection/native-field plumbing only; state + UI live in the sibling Plugin.Marks*.cs partials.
//
// Placement (write a punctuate mark at an arbitrary world Vector3, no human aiming):
//   ZIndicatorMgr (Panda.ZGame, ZSingleton) → BeginIndicator(200+slot, 1100+slot) → overwrite the PRIVATE Vector3
//   field indicatorPos_ (resolve BY NAME, never hard-code 0x80 — version-brittle) → FireSkill(). All three calls
//   must run back-to-back in ONE synchronous main-thread callback BEFORE this frame's LateUpdate (LateUpdate
//   re-derives indicatorPos_ from the live cursor). The load queue drains in Framework.Update, which satisfies
//   that timing exactly. ⚠️ do NOT Harmony-patch ZIndicatorMgr.begin(in IndicatorData) — in-struct trampoline crash;
//   CALLING BeginIndicator/FireSkill directly is fine (no in-param).
//
// Read (recover placed marks' world positions): marks render as skill effects, so their coords live in the effect
//   registry — Panda.ZEffect.ZEffectManager.EffectDict (ZDictionary<long,ZEffect>, NOT plain IEnumerable → hand-roll
//   the Keys+Item walk). Keep effects whose GameObject name contains "dungeonsmask" (p_fx_dungeonsmask0N(Clone) →
//   slot = trailing digit N); filter BelongUuid == local player for our own marks. Enumerate ON-DEMAND (on Save).
public sealed partial class Plugin
{
    private const int MkMarkCap = 4000;   // hard cap — never walk an unbounded effect table

    // Cached reflection (resolved once, lazily). The singletons are re-fetched each call (scene-lazy).
    private bool         _mkResolved, _mkApiOk;
    private Type?        _mkIndicatorType, _mkEffMgrType;
    private MethodInfo?  _mkBeginIndicator, _mkFireSkill;
    private PropertyInfo? _mkEffectDict, _mkDictItem;
    private readonly object[] _mkKeyArg = new object[1];   // reused; holds the long key for the dict indexer

    // slot N (1..6): indicator slot id = 200+N, scene-mark skill id = 1100+N (SkillSlotPositionTable 201-206).
    private static int MkSlotId(int slot)  => 200 + slot;
    private static int MkSkillId(int slot) => 1100 + slot;

    private bool MkResolveApi()
    {
        if (_mkResolved) return _mkApiOk;
        _mkResolved = true;
        try
        {
            _mkIndicatorType = StellarInterop.FindType("Panda.ZGame.ZIndicatorMgr");
            _mkEffMgrType    = StellarInterop.FindType("Panda.ZEffect.ZEffectManager");   // ⚠️ Panda.ZEffect, not ZGame
            if (_mkIndicatorType == null) { _services.Log.Warning("[MarkPresets] ZIndicatorMgr type not found"); }
            else
            {
                _mkBeginIndicator = _mkIndicatorType.GetMethod("BeginIndicator", new[] { typeof(int), typeof(int) });
                _mkFireSkill      = _mkIndicatorType.GetMethod("FireSkill", Type.EmptyTypes);
            }
            if (_mkEffMgrType != null)
                _mkEffectDict = StellarInterop.FindPropertyUp(_mkEffMgrType, "EffectDict");

            _mkApiOk = _mkBeginIndicator != null && _mkFireSkill != null;
            if (!_mkApiOk)
                _services.Log.Warning(
                    $"[MarkPresets] partial resolve: Begin={_mkBeginIndicator != null} Fire={_mkFireSkill != null} " +
                    $"EffMgr={_mkEffMgrType != null} EffectDict={_mkEffectDict != null}");
            return _mkApiOk;
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] resolve threw: {ex.Message}"); return false; }
    }

    // ── Place one mark at a world position (the validated Begin → write indicatorPos_ → Fire recipe) ─────────────
    private bool MkPlaceAt(int slot, Vector3 target)
    {
        if (!MkResolveApi()) return false;
        try
        {
            object? mgr = null;
            try { mgr = StellarInterop.GetSingleton(_mkIndicatorType!); }
            catch (Exception ex) { _services.Log.Warning($"[MarkPresets] ZIndicatorMgr GetSingleton threw: {ex.Message}"); }
            if (mgr is not Il2CppObjectBase b) { _services.Log.Warning("[MarkPresets] ZIndicatorMgr.Instance null (dungeon only?)"); return false; }

            IntPtr ptr = IL2CPP.Il2CppObjectBaseToPtr(b);
            if (ptr == IntPtr.Zero) { _services.Log.Warning("[MarkPresets] ZIndicatorMgr ptr null"); return false; }
            IntPtr klass = Marshal.ReadIntPtr(ptr);              // klass* = first IntPtr of every Il2CppObject
            int off = MkFieldOffset(klass, "indicatorPos_");
            if (off < 0) { _services.Log.Warning("[MarkPresets] indicatorPos_ offset not found (game update?)"); return false; }

            // BeginIndicator seeds the reticle; overwrite AFTER so our chosen point wins the same-frame race.
            _mkBeginIndicator!.Invoke(mgr, new object[] { MkSlotId(slot), MkSkillId(slot) });
            MkWriteVec3(ptr, off, target);
            _mkFireSkill!.Invoke(mgr, null);
            return true;
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] place slot {slot} threw: {ex.Message}"); return false; }
    }

    // Clear all six own marks via the game's own StopSkill (single-arg = skillId; main_copy_punctuate_view.lua:123).
    private void MkClearMarks()
    {
        try
        {
            _services.Lua.DoString(
                "pcall(function() local c = Z.PlayerInputController if c==nil then return end " +
                "for id=1101,1106 do pcall(function() c:StopSkill(id) end) end end)");
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] clear threw: {ex.Message}"); }
    }

    // ── Read the player's own currently-placed marks from EffectDict (on-demand, never per-frame) ────────────────
    private List<MarkPos> MkReadOwnMarks()
    {
        var outp = new List<MarkPos>();
        if (!MkResolveApi() || _mkEffMgrType == null || _mkEffectDict == null) return outp;
        try
        {
            object? mgr = null;
            try { mgr = StellarInterop.GetSingleton(_mkEffMgrType); }
            catch (Exception ex) { _services.Log.Warning($"[MarkPresets] ZEffectManager GetSingleton threw: {ex.Message}"); }
            if (mgr == null) return outp;

            object? dict = _mkEffectDict.GetValue(mgr);
            if (dict == null) return outp;

            long localUuid = 0;
            try { localUuid = _services.CombatSnapshot.LocalEntityId.Value; } catch { }

            var seen = new HashSet<int>();
            foreach (var ze in MkCollectEffects(dict))
            {
                if (ze == null) continue;
                string name = MkEffectGoName(ze);
                if (name.IndexOf("dungeonsmask", StringComparison.OrdinalIgnoreCase) < 0) continue;

                object? ctx = MkGetMember(ze, "Context");
                if (ctx == null) continue;
                Vector3 pos = MkGetMember(ctx, "Position") is Vector3 p ? p : default;
                long belong = 0;
                try { if (MkGetMember(ctx, "BelongUuid") is { } bv) belong = Convert.ToInt64(bv); } catch { }

                // Own marks only (full-uuid equality — no >>16). If we can't resolve localUuid, keep all.
                if (localUuid != 0 && belong != localUuid) continue;

                int slot = MkSlotFromName(name);
                if (slot < 1 || slot > 6 || !seen.Add(slot)) continue;   // one row per slot
                outp.Add(new MarkPos { Slot = slot, X = pos.x, Y = pos.y, Z = pos.z });
            }
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] read marks threw: {ex.Message}"); }
        return outp;
    }

    // Live "which slots are placed" popcount via the game's OWN Lua accessor (the C# GetLuaAttr(int) path reads the
    // WRONG attr family and returns 0 — see KB). Parks the value as a STRING global, reads it back. Returns -1 on miss.
    private long MkReadFlagStateViaLua()
    {
        try
        {
            if (!_services.Lua.Ready) return -1;
            const string chunk = @"
rawset(_G,'_rm_mkfs','ERR')
pcall(function()
  if Z.EntityMgr==nil or Z.LocalAttr==nil then return end
  local pe=(Z.EntityMgr).PlayerEnt
  if not pe then return end
  local a=pe:GetLuaAttr(Z.LocalAttr.FlagSkillState)
  if a then rawset(_G,'_rm_mkfs', tostring(a.Value)) end
end)";
            _services.Lua.DoString(chunk);
            string s = _services.Lua.ReadGlobalString("_rm_mkfs") ?? "";
            return long.TryParse(s, out long v) ? v : -1;
        }
        catch { return -1; }
    }

    // ── ZDictionary<long,ZEffect> walk (Keys+Item primary; Values / KVP fallbacks) ───────────────────────────────
    private List<object?> MkCollectEffects(object dict)
    {
        var t = dict.GetType();

        // A — enumerate Keys (primitive longs marshal cleanly), fetch each value through the "Item" indexer.
        try
        {
            object? keys = t.GetProperty("Keys")?.GetValue(dict);
            if (keys != null)
            {
                var kl = MkEnumCapped(keys);
                if (kl.Count > 0)
                {
                    _mkDictItem ??= t.GetProperty("Item");
                    if (_mkDictItem != null)
                    {
                        var res = new List<object?>(kl.Count);
                        foreach (var k in kl)
                        {
                            if (k == null) continue;
                            try { _mkKeyArg[0] = k; res.Add(_mkDictItem.GetValue(dict, _mkKeyArg)); } catch { }
                        }
                        if (res.Count > 0) return res;
                    }
                }
            }
        }
        catch { }

        // B — Values directly.
        try
        {
            object? vals = t.GetProperty("Values")?.GetValue(dict);
            if (vals != null) { var vl = MkEnumCapped(vals); if (vl.Count > 0) return vl; }
        }
        catch { }

        // C — the dict itself → KeyValuePair<long,ZEffect>; take .Value.
        try
        {
            var pairs = MkEnumCapped(dict);
            if (pairs.Count > 0)
            {
                var res = new List<object?>(pairs.Count);
                foreach (var kv in pairs)
                {
                    if (kv == null) continue;
                    try { res.Add(kv.GetType().GetProperty("Value")?.GetValue(kv)); } catch { }
                }
                return res;
            }
        }
        catch { }

        return new List<object?>();
    }

    // Reflection-driven GetEnumerator/MoveNext/Current (il2cpp collections don't implement managed IEnumerable).
    private static List<object?> MkEnumCapped(object enumerable)
    {
        var list = new List<object?>();
        try
        {
            var e = enumerable.GetType().GetMethod("GetEnumerator", Type.EmptyTypes)?.Invoke(enumerable, null);
            if (e == null) return list;
            var eit = e.GetType();
            var moveNext = eit.GetMethod("MoveNext", Type.EmptyTypes);
            var current  = eit.GetProperty("Current");
            if (moveNext == null || current == null) return list;
            int guard = 0;
            while (guard++ < MkMarkCap && Convert.ToBoolean(moveNext.Invoke(e, null)))
                list.Add(current.GetValue(e));
        }
        catch { /* swallow — a read must never crash the game */ }
        return list;
    }

    // effectObj_.name (private GameObject); GetGo() (public) fallback.
    private string MkEffectGoName(object ze)
    {
        try { if (MkGetMember(ze, "effectObj_") is GameObject go && go != null) return go.name; }
        catch { }
        try
        {
            var m = ze.GetType().GetMethod("GetGo",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m?.Invoke(ze, null) is GameObject go2 && go2 != null) return go2.name;
        }
        catch { }
        return "?";
    }

    // Slot = digits right after "dungeonsmask" (…mask01 → 1); trailing-digit-run fallback survives "(Clone)".
    private static int MkSlotFromName(string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        int idx = name.IndexOf("dungeonsmask", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            int start = idx + "dungeonsmask".Length, v = 0; bool any = false;
            for (int i = start; i < name.Length && char.IsDigit(name[i]); i++) { v = v * 10 + (name[i] - '0'); any = true; }
            if (any) return v;
        }
        int end = name.Length - 1;
        while (end >= 0 && !char.IsDigit(name[end])) end--;
        if (end < 0) return -1;
        int s = end;
        while (s >= 0 && char.IsDigit(name[s])) s--;
        return int.TryParse(name.Substring(s + 1, end - s), out int r) ? r : -1;
    }

    // ── Reflection member read (property OR field) + native Vector3 field access (Marshal only, no `unsafe`) ──────
    // IL2CPP fields surface as PropertyInfo, but the marker effect chain is all properties anyway; the field branch
    // is a harmless fallback for effectObj_.
    private static object? MkGetMember(object obj, string name)
    {
        var t = obj.GetType();
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try { var p = t.GetProperty(name, F); if (p != null) return p.GetValue(obj); } catch { }
        try { var f = t.GetField(name, F); if (f != null) return f.GetValue(obj); } catch { }
        return null;
    }

    private static int MkFieldOffset(IntPtr klass, string name)
    {
        var field = IL2CPP.il2cpp_class_get_field_from_name(klass, name);
        return field == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(field);
    }

    private static void MkWriteVec3(IntPtr ptr, int off, Vector3 v)
    {
        Marshal.WriteInt32(ptr + off + 0, BitConverter.SingleToInt32Bits(v.x));
        Marshal.WriteInt32(ptr + off + 4, BitConverter.SingleToInt32Bits(v.y));
        Marshal.WriteInt32(ptr + off + 8, BitConverter.SingleToInt32Bits(v.z));
    }

    private static int MkPopCount(long value)
    {
        int c = 0; ulong u = (ulong)value;
        while (u != 0) { u &= u - 1; c++; }
        return c;
    }
}
