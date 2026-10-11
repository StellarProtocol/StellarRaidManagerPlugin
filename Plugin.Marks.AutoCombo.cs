using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — Auto Combo (auto-battle) fire path + placement diagnostics ────────────────────────────
//
// BUG: with the game's Auto Combo (= AutoBattle) ON, ZIndicatorMgr.FireSkill() places NO mark. Static RE of
// GameAssembly found the gate in ZSkillInputMgr.FirePlaySkillEvent(SkillControlData, clearSlot, isIndicator,
// isAIPress): `if (AutoBattleComponent.Value(PlayerEnt) && !isAIPress) { AICachePressSlot = slot; return true; }`.
// FireSkill passes isAIPress=false, so under auto-battle the press is parked for the AI and swallowed. Toggling the
// local attr EAutoBattleSwitch does NOT clear AutoBattleComponent (validated in-game) — that attempt is removed.
//
// FIX: replicate FireSkill ourselves with isAIPress=true (it has no other effect, so this works with Auto Combo on
// OR off):  SetIndicatorPos/Rot on the player entity (what the cast actually reads: GetPassiveSelectPoint →
// GetIndicatorPos) → SkillControlDataMgr.TryGetSkillControlData(skillId) → PlayerEnt.SkillInputComp.SkillInputMgr
// .FirePlaySkillEvent(data, true, true, true) → ZIndicatorMgr.TryCancelIndicator(false, !ok) (FireSkill's own
// `end(false, !ok)`). Any piece unresolved → fall back to FireSkill() (works with Auto Combo off) and log once why.
// Plain calls only — NO Harmony patch (in-struct trampoline crash risk, see KB).
public sealed partial class Plugin
{
    private const float MkVerifyDelay = 0.5f;   // seconds after the queue drains — lets the mark effects spawn

    // Lazily-resolved reflection, resolved ONCE. Each piece is optional: a miss skips that write/log field or drops
    // to the FireSkill fallback, it never blocks placement.
    private bool _mkExtResolved;
    private MethodInfo? _mkSetIndicatorPos, _mkSetIndicatorRot, _mkGetIndicatorPos, _mkGetIsAutoBattle;
    private PropertyInfo? _mkEntMgrInstance, _mkPlayerEntProp;
    private MethodInfo? _mkGetEntity;
    // aipress path: PlayerEnt.SkillInputComp → PlayerSkillInputComp.SkillInputMgr (internal ZSkillInputMgr; interop
    // surfaces it as a property) → FirePlaySkillEvent; SkillControlDataMgr (ZSingleton) → TryGetSkillControlData.
    private Type? _mkPlayerEntType, _mkScdMgrType;
    private PropertyInfo? _mkSkillInputComp, _mkSkillInputMgr;
    private MethodInfo? _mkTryGetScd, _mkFirePlaySkillEvent, _mkTryCancelIndicator;
    private string? _mkAiPressMissing;   // null = aipress path fully resolved; else why not (logged once)
    private bool _mkFallbackLogged;

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
            const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var ext = StellarInterop.FindType("Panda.ZGame.EntityAttrExtensions");
            if (ext != null)
                foreach (var m in ext.GetMethods(S))
                {
                    int n = m.GetParameters().Length;
                    if      (m.Name == "SetIndicatorPos" && n == 2) _mkSetIndicatorPos = m;
                    else if (m.Name == "SetIndicatorRot" && n == 2) _mkSetIndicatorRot = m;
                    else if (m.Name == "GetIndicatorPos" && n == 1) _mkGetIndicatorPos = m;
                    else if (m.Name == "GetIsAutoBattle" && n == 1) _mkGetIsAutoBattle = m;
                }

            // ZEntityMgr.Instance is inherited from ZSingleton<T> → FlattenHierarchy (same as MechanicCalloutTracker).
            // PlayerEnt is the TYPED local player — GetEntity(long) returns a ZEntity-typed wrapper on which the
            // PlayerEnt-only SkillInputComp property can't be read, so it's just the fallback.
            var entMgr = StellarInterop.FindType("Panda.ZGame.ZEntityMgr");
            if (entMgr != null)
            {
                _mkEntMgrInstance = entMgr.GetProperty("Instance", S | BindingFlags.FlattenHierarchy);
                _mkPlayerEntProp  = entMgr.GetProperty("PlayerEnt", BindingFlags.Public | BindingFlags.Instance);
                foreach (var m in entMgr.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "GetEntity" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(long))
                    { _mkGetEntity = m; break; }
            }

            _mkPlayerEntType = StellarInterop.FindType("Panda.ZGame.PlayerEnt");
            _mkSkillInputComp = _mkPlayerEntType?.GetProperty("SkillInputComp", I);
            _mkSkillInputMgr  = StellarInterop.FindType("Panda.ZGame.PlayerSkillInputComp")?.GetProperty("SkillInputMgr", I);
            var sim = StellarInterop.FindType("Panda.ZGame.ZSkillInputMgr");
            if (sim != null) _mkFirePlaySkillEvent = StellarInterop.FindMethod(sim, "FirePlaySkillEvent", 4);   // unique
            _mkScdMgrType = StellarInterop.FindType("Panda.ZGame.SkillControlDataMgr");
            // Instance overload only (ZSkillInputMgr has an unrelated STATIC 3-arg one — different type, no clash).
            if (_mkScdMgrType != null) _mkTryGetScd = StellarInterop.FindMethod(_mkScdMgrType, "TryGetSkillControlData", 2);
            if (_mkIndicatorType != null) _mkTryCancelIndicator = StellarInterop.FindMethod(_mkIndicatorType, "TryCancelIndicator", 2);

            var missing = new List<string>();
            if (_mkPlayerEntProp == null)      missing.Add("ZEntityMgr.PlayerEnt");
            if (_mkSkillInputComp == null)     missing.Add("PlayerEnt.SkillInputComp");
            if (_mkSkillInputMgr == null)      missing.Add("PlayerSkillInputComp.SkillInputMgr");
            if (_mkFirePlaySkillEvent == null) missing.Add("ZSkillInputMgr.FirePlaySkillEvent");
            if (_mkTryGetScd == null)          missing.Add("SkillControlDataMgr.TryGetSkillControlData");
            if (_mkTryCancelIndicator == null) missing.Add("ZIndicatorMgr.TryCancelIndicator");
            _mkAiPressMissing = missing.Count == 0 ? null : string.Join(",", missing);

            _services.Log.Info(
                $"[MarkPresets] ext resolve: SetIndicatorPos={_mkSetIndicatorPos != null} SetIndicatorRot={_mkSetIndicatorRot != null} " +
                $"GetIndicatorPos={_mkGetIndicatorPos != null} GetIsAutoBattle={_mkGetIsAutoBattle != null} " +
                $"GetEntity={_mkGetEntity != null} aipress={(_mkAiPressMissing == null ? "ok" : "MISSING " + _mkAiPressMissing)}");
        }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] ext resolve threw: {ex.Message}"); _mkAiPressMissing ??= "resolve threw"; }
    }

    private object? MkPlayerEntity()
    {
        try
        {
            var mgr = _mkEntMgrInstance?.GetValue(null);
            if (mgr == null) return null;
            if (_mkPlayerEntProp?.GetValue(mgr) is { } pe) return pe;
            if (_mkGetEntity == null) return null;
            long uuid = _services.CombatSnapshot.LocalEntityId.Value;
            return uuid == 0 ? null : _mkGetEntity.Invoke(mgr, new object[] { uuid });
        }
        catch { return null; }
    }

    // ── FireSkill replica with isAIPress=true (the Auto Combo gate bypass) ───────────────────────────────────────────
    // Returns false ONLY when FirePlaySkillEvent was never invoked — the caller then falls back to FireSkill(). Once
    // the event has been fired we report true even if the cancel throws: falling back then would double-cast.
    private bool MkFireAiPress(object indicatorMgr, object? pe, int skillId, out string tryGet, out string fireRet)
    {
        tryGet = "n/a"; fireRet = "n/a";
        string? why = _mkAiPressMissing;
        object? sim = null, scdMgr = null, data = null;
        try
        {
            if (why == null && (pe == null || !_mkPlayerEntType!.IsInstanceOfType(pe))) why = "PlayerEnt null/untyped";
            if (why == null && (sim = _mkSkillInputMgr!.GetValue(_mkSkillInputComp!.GetValue(pe))) == null) why = "SkillInputMgr null";
            if (why == null && (scdMgr = StellarInterop.GetSingleton(_mkScdMgrType!)) == null) why = "SkillControlDataMgr.Instance null";
            if (why == null)
            {
                var args = new object?[] { skillId, null };   // out SkillControlData lands in args[1]
                bool got = _mkTryGetScd!.Invoke(scdMgr, args) is true;
                data = args[1];
                tryGet = got.ToString();
                if (!got || data == null) why = $"TryGetSkillControlData({skillId}) failed";
            }
        }
        catch (Exception ex) { why = $"resolve threw: {ex.InnerException?.Message ?? ex.Message}"; }

        if (why != null)
        {
            if (!_mkFallbackLogged) { _mkFallbackLogged = true; _services.Log.Warning($"[MarkPresets] aipress path unavailable ({why}) — falling back to FireSkill (no marks while Auto Combo is on)"); }
            return false;
        }

        bool ok = false;
        try { ok = _mkFirePlaySkillEvent!.Invoke(sim, new object[] { data!, true, true, true }) is true; fireRet = ok.ToString(); }
        catch (Exception ex) { fireRet = "ERR"; _services.Log.Warning($"[MarkPresets] FirePlaySkillEvent threw: {ex.InnerException?.Message ?? ex.Message}"); }
        // Mirror FireSkill's `end(false, !ok)` — closes the indicator we opened with BeginIndicator.
        try { _mkTryCancelIndicator!.Invoke(indicatorMgr, new object[] { false, !ok }); }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] TryCancelIndicator threw: {ex.InnerException?.Message ?? ex.Message}"); }
        return true;
    }

    // ── Entity-level indicator position/rotation + diagnostic reads (all best-effort, never throw) ─────────────────
    private bool MkSetEntityIndicatorPos(object? playerEnt, Vector3 target)
    {
        if (playerEnt == null || _mkSetIndicatorPos == null) return false;
        try { _mkSetIndicatorPos.Invoke(null, new object[] { playerEnt, target }); return true; }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] SetIndicatorPos threw: {ex.InnerException?.Message ?? ex.Message}"); return false; }
    }

    private void MkSetEntityIndicatorRot(object? playerEnt, Quaternion rot)
    {
        if (playerEnt == null || _mkSetIndicatorRot == null) return;
        try { _mkSetIndicatorRot.Invoke(null, new object[] { playerEnt, rot }); }
        catch (Exception ex) { _services.Log.Warning($"[MarkPresets] SetIndicatorRot threw: {ex.InnerException?.Message ?? ex.Message}"); }
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

    private static Quaternion MkReadQuat(IntPtr ptr, int off) => new(
        BitConverter.Int32BitsToSingle(Marshal.ReadInt32(ptr + off + 0)),
        BitConverter.Int32BitsToSingle(Marshal.ReadInt32(ptr + off + 4)),
        BitConverter.Int32BitsToSingle(Marshal.ReadInt32(ptr + off + 8)),
        BitConverter.Int32BitsToSingle(Marshal.ReadInt32(ptr + off + 12)));

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
