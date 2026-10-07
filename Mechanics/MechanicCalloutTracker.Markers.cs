using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.RaidManager;

// Party markers 1..6 for the minimap. Marks are punctuate SKILLS (1101-1106) whose world VFX are server-broadcast
// effects, so placed positions live in the effect registry (Marker-and-Raid-Coordination.md, validated; read code
// mirrors Plugin.Marks.Interop.cs): Panda.ZEffect.ZEffectManager (ZSingleton) → EffectDict
// (ZDictionary<long, ZEffect>, Keys + Item walk) → effects whose GameObject name contains "dungeonsmask" (slot = the
// digits after it) → ZEffect.Context.Position. ALL placers' marks (teammates' included), like upstream.
//
// The KB warns against enumerating EffectDict per frame (a dungeon has many effects). This runs at the scan cadence
// (~200 ms), only while the minimap + "Show party markers" are on in a supported scene, and caches per effect KEY:
// a key's GameObject name is read once (non-marks are remembered as -1 and skipped), so a poll is a Keys walk plus a
// Context.Position read for at most six mark effects.
internal sealed partial class MechanicCalloutTracker
{
    public bool ShowMarkers { get; set; } = true;

    private bool          _mkResolved;
    private Type?         _mkEffMgrType;
    private PropertyInfo? _mkEffectDict, _mkDictItem, _mkDictKeys;
    private readonly Dictionary<long, int> _mkSlotByKey = new();     // effect key → slot 1..6, or -1 (not a mark)
    private readonly HashSet<long> _mkLive = new();
    private readonly List<long> _mkStale = new();
    private readonly object[] _mkKeyArg = new object[1];

    private void ReadMarkers()
    {
        _map.Markers.Clear();
        if (!ShowMarkers) return;
        WalkMarkers(_markerScratch);
        foreach (var m in _markerScratch) _map.Markers.Add((m.Slot, m.Pos.x, m.Pos.z));
    }

    // The cached EffectDict walk: every placed mark as (slot, world position).
    private readonly List<(int Slot, Vector3 Pos)> _markerScratch = new();

    private void WalkMarkers(List<(int Slot, Vector3 Pos)> outList)
    {
        outList.Clear();
        try
        {
            if (!_mkResolved)
            {
                _mkResolved = true;
                _mkEffMgrType = StellarInterop.FindType("Panda.ZEffect.ZEffectManager");   // Panda.ZEffect, not ZGame
                if (_mkEffMgrType != null) _mkEffectDict = StellarInterop.FindPropertyUp(_mkEffMgrType, "EffectDict");
            }
            if (_mkEffMgrType == null || _mkEffectDict == null) return;
            var mgr = StellarInterop.GetSingleton(_mkEffMgrType);
            var dict = mgr != null ? _mkEffectDict.GetValue(mgr) : null;
            if (dict == null) return;
            var dt = dict.GetType();
            _mkDictKeys ??= dt.GetProperty("Keys");
            _mkDictItem ??= dt.GetProperty("Item");
            var keys = _mkDictKeys?.GetValue(dict);
            if (keys == null || _mkDictItem == null) return;

            _mkLive.Clear();
            foreach (var k in WalkIl2Cpp(keys))
            {
                long key = Convert.ToInt64(k);
                _mkLive.Add(key);
                if (_mkSlotByKey.TryGetValue(key, out int slot) && slot < 0) continue;   // known non-mark
                _mkKeyArg[0] = k;
                var effect = _mkDictItem.GetValue(dict, _mkKeyArg);
                if (effect == null) continue;
                if (!_mkSlotByKey.ContainsKey(key))
                {
                    string nm = EffectGoName(effect);
                    if (nm.Length == 0) continue;                      // GO not bound yet — retry next poll
                    _mkSlotByKey[key] = slot = MarkSlot(nm);
                }
                if (slot < 1 || slot > 6) continue;
                var ctx = Member(effect, "Context");
                if (ctx != null && Member(ctx, "Position") is Vector3 p) outList.Add((slot, p));
            }
            _mkStale.Clear();
            foreach (var k in _mkSlotByKey.Keys) if (!_mkLive.Contains(k)) _mkStale.Add(k);
            foreach (var k in _mkStale) _mkSlotByKey.Remove(k);
        }
        catch (Exception ex) { LogErrOnce("markers: " + (ex.InnerException?.Message ?? ex.Message)); }
    }

    // effectObj_.name (private GameObject), GetGo() fallback — as Plugin.Marks.Interop.cs's MkEffectGoName. "" on a miss (not cached;
    // the key is retried next poll).
    private static string EffectGoName(object ze)
    {
        try { if (Member(ze, "effectObj_") is GameObject go && go != null) return go.name; } catch { }
        try
        {
            var m = ze.GetType().GetMethod("GetGo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m?.Invoke(ze, null) is GameObject go2 && go2 != null) return go2.name;
        }
        catch { }
        return "";
    }

    // "p_fx_dungeonsmask0N(Clone)" → N; -1 when the name isn't a mark.
    private static int MarkSlot(string name)
    {
        int idx = name.IndexOf("dungeonsmask", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return -1;
        int v = 0; bool any = false;
        for (int i = idx + "dungeonsmask".Length; i < name.Length && char.IsDigit(name[i]); i++) { v = v * 10 + (name[i] - '0'); any = true; }
        return any ? v : -1;
    }

    private static object? Member(object obj, string name)
    {
        const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var t = obj.GetType();
        try { var p = t.GetProperty(name, f); if (p != null) return p.GetValue(obj); } catch { }
        try { var fi = t.GetField(name, f); if (fi != null) return fi.GetValue(obj); } catch { }
        return null;
    }
}
