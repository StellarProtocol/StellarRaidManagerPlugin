using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — Auto Combo workaround + placement diagnostics ─────────────────────────────────────────
//
// BUG: with the game's Auto Combo (= AutoBattle) switched ON, every preset mark lands at the player's feet instead of
// its stored position. The game's own help text says "Free Casting Mode does not apply when Auto Combo is enabled",
// so the inferred cause is that FireSkill's point-target path ignores ZIndicatorMgr.indicatorPos_ while auto-battle
// is on and falls back to self. Method bodies aren't in the dump, so this is a FIX ATTEMPT + diagnostics:
//
//   1. Suspend the live Auto Combo switch (local attr EAutoBattleSwitch = 49) around the Begin → write → Fire
//      sequence, restoring it in a finally. Lua is the ONLY read/write path for local attrs (the game does exactly
//      this in fighterbtns_view.lua:1957-1960). We do NOT call SetIsAutoBattle / StopPlayerAIBattle or touch
//      setting 1012 — those have side effects beyond a one-frame suspend.
//   2. Also write the ENTITY-level indicator position (EntityAttrExtensions.SetIndicatorPos → IndicatorPosComponent
//      on the player ZEntity) — a second target-point store that the cast path may read instead of the mgr field.
//   3. One diagnostic log line per placed mark (never per frame) + one post-placement verification block comparing
//      intended vs actual mark positions read back from the effect registry (MkReadOwnMarks).
public sealed partial class Plugin
{
    private const float MkVerifyDelay = 0.5f;   // seconds after the queue drains — lets the mark effects spawn

    // Lazily-resolved reflection for the entity-level indicator store + diagnostics. Each piece is optional: a miss
    // just skips that write/log field, it never blocks placement.
    private bool _mkExtResolved;
    private MethodInfo? _mkSetIndicatorPos, _mkGetIndicatorPos, _mkGetIsAutoBattle, _mkCheckIndicatorEnable;
    private PropertyInfo? _mkIsEnable, _mkEntMgrInstance;
    private MethodInfo? _mkGetEntity;

    // Post-placement verification: what each slot was SENT to in the current apply, and a countdown armed when the
    // load queue drains. Cleared on every ApplyStep so a verify never mixes two applies.
    private readonly Dictionary<int, Vector3> _mkIntended = new();
    private float _mkVerifyTimer = -1f;

    private void MkResolveExt()
    {
        if (_mkExtResolved) return;
        _mkExtResolved = true;
        try
        {
            const BindingFlags S = BindingFlags.Public | BindingFlags.Static;
            var ext = StellarInterop.FindType("Panda.ZGame.EntityAttrExtensions");
            if (ext != null)
                foreach (var m in ext.GetMethods(S))
                {
                    int n = m.GetParameters().Length;
                    if      (m.Name == "SetIndicatorPos" && n == 2) _mkSetIndicatorPos = m;
                    else if (m.Name == "GetIndicatorPos" && n == 1) _mkGetIndicatorPos = m;
                    else if (m.Name == "GetIsAutoBattle" && n == 1) _mkGetIsAutoBattle = m;
                }

            if (_mkIndicatorType != null)
            {
                _mkIsEnable = _mkIndicatorType.GetProperty("IsEnable", BindingFlags.Public | BindingFlags.Instance);
                _mkCheckIndicatorEnable = _mkIndicatorType.GetMethod("CheckIndicatorEnable", new[] { typeof(int), typeof(bool) });
            }

            // ZEntityMgr.Instance is inherited from ZSingleton<T> → FlattenHierarchy (same as MechanicCalloutTracker).
            var entMgr = StellarInterop.FindType("Panda.ZGame.ZEntityMgr");
            if (entMgr != null)
            {
                _mkEntMgrInstance = entMgr.GetProperty("Instance", S | BindingFlags.FlattenHierarchy);
                foreach (var m in entMgr.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "GetEntity" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(long))
                    { _mkGetEntity = m; break; }
            }

            _services.Log.Info(
                $"[MarkPresets] ext resolve: SetIndicatorPos={_mkSetIndicatorPos != null} GetIndicatorPos={_mkGetIndicatorPos != null} " +
                $"GetIsAutoBattle={_mkGetIsAutoBattle != null} IsEnable={_mkIsEnable != null} " +
                $"CheckIndicatorEnable={_mkCheckIndicatorEnable != null} GetEntity={_mkGetEntity != null}");
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] ext resolve threw: {ex.Message}"); }
    }

    private object? MkPlayerEntity()
    {
        try
        {
            if (_mkEntMgrInstance == null || _mkGetEntity == null) return null;
            long uuid = _services.CombatSnapshot.LocalEntityId.Value;
            if (uuid == 0) return null;
            var mgr = _mkEntMgrInstance.GetValue(null);
            return mgr == null ? null : _mkGetEntity.Invoke(mgr, new object[] { uuid });
        }
        catch { return null; }
    }

    // ── Auto Combo switch suspend / restore (Lua only — local attrs have no working C# path, see KB) ────────────────
    // Returns the switch value BEFORE the suspend as a string ("true"/"false"/"nil"/"ERR") — FOR LOGGING ONLY. The
    // restore decision never depends on this C# readback: the Lua chunk itself parks _rm_mkab_was=true exactly when it
    // flipped the switch off, and MkRestoreAutoCombo (run UNCONDITIONALLY in the caller's finally) keys on that Lua
    // global. So even if ReadGlobalString throws after the flip, Auto Combo still comes back on. String global for the
    // log value = no Lua-type-strict readback pitfalls (Framework-Interop-Foundation.md §2).
    private string MkSuspendAutoCombo()
    {
        try
        {
            if (!_services.Lua.Ready) return "ERR";
            const string chunk = @"
rawset(_G,'_rm_mkab','ERR')
rawset(_G,'_rm_mkab_was',nil)
pcall(function()
  if Z.EntityMgr==nil or Z.LocalAttr==nil then return end
  local pe=(Z.EntityMgr).PlayerEnt
  if not pe then return end
  local a=pe:GetLuaAttr(Z.LocalAttr.EAutoBattleSwitch)
  local v=nil
  if a then v=a.Value end
  rawset(_G,'_rm_mkab', tostring(v))
  if v==true then
    pe:SetLuaAttr(Z.LocalAttr.EAutoBattleSwitch, false)
    rawset(_G,'_rm_mkab_was',true)
  end
end)";
            _services.Lua.DoString(chunk);
            return _services.Lua.ReadGlobalString("_rm_mkab") ?? "ERR";
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] auto-combo suspend threw: {ex.Message}"); return "ERR"; }
    }

    // Safe to call unconditionally: a no-op unless the suspend chunk parked _rm_mkab_was. The flag is cleared even if
    // the SetLuaAttr inside fails (outer pcall + trailing rawset) so a stale flag can't re-enable Auto Combo later.
    private void MkRestoreAutoCombo()
    {
        try
        {
            _services.Lua.DoString(
                "pcall(function() if rawget(_G,'_rm_mkab_was') then local pe=(Z.EntityMgr).PlayerEnt if pe then " +
                "pe:SetLuaAttr(Z.LocalAttr.EAutoBattleSwitch, true) end end end) " +
                "rawset(_G,'_rm_mkab_was',nil)");
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] auto-combo RESTORE threw: {ex.Message}"); }
    }

    // ── Entity-level indicator position + diagnostic reads (all best-effort, never throw) ────────────────────────
    private bool MkSetEntityIndicatorPos(object? playerEnt, Vector3 target)
    {
        if (playerEnt == null || _mkSetIndicatorPos == null) return false;
        try { _mkSetIndicatorPos.Invoke(null, new object[] { playerEnt, target }); return true; }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] SetIndicatorPos threw: {ex.InnerException?.Message ?? ex.Message}"); return false; }
    }

    private string MkEntityIndicatorPosStr(object? playerEnt)
    {
        if (playerEnt == null || _mkGetIndicatorPos == null) return "n/a";
        try { return _mkGetIndicatorPos.Invoke(null, new[] { playerEnt }) is Vector3 v ? MkFmt(v) : "?"; }
        catch { return "ERR"; }
    }

    private string MkIsAutoBattleStr(object? playerEnt)
    {
        if (playerEnt == null || _mkGetIsAutoBattle == null) return "n/a";
        try { return _mkGetIsAutoBattle.Invoke(null, new[] { playerEnt })?.ToString() ?? "null"; }
        catch { return "ERR"; }
    }

    private string MkIsEnableStr(object mgr)
    {
        if (_mkIsEnable == null) return "n/a";
        try { return _mkIsEnable.GetValue(mgr)?.ToString() ?? "null"; } catch { return "ERR"; }
    }

    private string MkCheckEnableStr(object mgr, int skillId)
    {
        if (_mkCheckIndicatorEnable == null) return "n/a";
        try { return _mkCheckIndicatorEnable.Invoke(mgr, new object[] { skillId, false })?.ToString() ?? "null"; }
        catch { return "ERR"; }
    }

    private static Vector3 MkReadVec3(IntPtr ptr, int off) => new(
        BitConverter.Int32BitsToSingle(Marshal.ReadInt32(ptr + off + 0)),
        BitConverter.Int32BitsToSingle(Marshal.ReadInt32(ptr + off + 4)),
        BitConverter.Int32BitsToSingle(Marshal.ReadInt32(ptr + off + 8)));

    private static string MkFmt(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

    // ── Post-placement verification (intended vs actual, one summary block per apply) ─────────────────────────────
    private void MkVerifyReset() { _mkIntended.Clear(); _mkVerifyTimer = -1f; }

    private void MkNoteIntended(int slot, Vector3 target) => _mkIntended[slot] = target;

    private void MkArmVerify() { if (_mkIntended.Count > 0) _mkVerifyTimer = MkVerifyDelay; }

    // Pumped from TickMarks. Fires once, MkVerifyDelay after the queue drained (deltaTime countdown — no clock epoch).
    private void MkTickVerify(float deltaTime)
    {
        if (_mkVerifyTimer < 0f) return;
        _mkVerifyTimer -= deltaTime;
        if (_mkVerifyTimer > 0f) return;
        _mkVerifyTimer = -1f;

        var actual = new Dictionary<int, Vector3>();
        foreach (var m in MkReadOwnMarks()) actual[m.Slot] = new Vector3(m.X, m.Y, m.Z);

        Vector3 player = default; bool havePlayer = false;
        // Player feet (ZEntity.Model.ModelGoComp.Position) — a mark with distToPlayer ≈ 0 is the "landed on me" bug.
        var pe = MkPlayerEntity();
        try
        {
            if (pe != null && MkGetMember(pe, "Model") is { } model && MkGetMember(model, "ModelGoComp") is { } go
                && MkGetMember(go, "Position") is Vector3 pp) { player = pp; havePlayer = true; }
        }
        catch { }

        var sb = new StringBuilder();
        sb.Append($"[MarkPresets] verify: {_mkIntended.Count} intended, {actual.Count} read back, player=")
          .Append(havePlayer ? MkFmt(player) : "?");
        foreach (var kv in _mkIntended)
        {
            sb.Append($"\n  slot {kv.Key}: intended={MkFmt(kv.Value)} ");
            if (actual.TryGetValue(kv.Key, out var a))
            {
                sb.Append($"actual={MkFmt(a)} dist={Vector3.Distance(kv.Value, a):F2}");
                if (havePlayer) sb.Append($" distToPlayer={Vector3.Distance(a, player):F2}");
            }
            else sb.Append("actual=MISSING");
        }
        _services.Log.Info(sb.ToString());
        _mkIntended.Clear();
    }
}
