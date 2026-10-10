using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// WIDE buff scan (every supported scene). Upstream's `snapshot.buffs` holds every mechanic-id buff on ANY entity,
// while our normal scan reads only players + the scene's relevant monsters. In-game (raid) the Phase / Phase-Mapping
// buffs turned out to ride monsters we didn't scan (Mechanic-Callouts.md "Mirror Summon"), the pinball cast buff
// 829314 rides a scene object, and other scenes may park buffs on markers too. So once per second every OTHER entity
// in entityDict_ (any type) has its full buff list checked for the scene's mechanic ids (SceneDef.MechanicBuffs).
// Hits are kept until the next wide pass and merged into _buffs every scan, so the table rows and minimap regions use
// them unchanged (no names: upstream only names targets that are in its entity map, and these aren't in ours).
// Cheap: BuffComp is cached per uuid (null = has none → skipped without a reflection call), only BuffBaseId is read
// per item until an id matches.
internal sealed partial class MechanicCalloutTracker
{
    private const long WideIntervalMs = 1000;
    private long _wideAt;
    private readonly List<McBuff> _wideBuffs = new();
    private readonly List<McBuff> _wideNext = new();
    private readonly Dictionary<long, object?> _wideComp = new();     // uuid → BuffComp (null = none)

    // Pull the next wide pass forward to ≤ 300 ms from now (raid floor reset: restored type-3 tiles show quickly).
    private void RequestWideSoon()
    {
        long t = Environment.TickCount64 - WideIntervalMs + 300;
        if (t < _wideAt) _wideAt = t;
    }

    private bool BeginWide(long now)
    {
        if (now - _wideAt < WideIntervalMs) return false;
        _wideAt = now;
        _wideNext.Clear();
        return true;
    }

    private void EndWide()
    {
        _wideBuffs.Clear();
        _wideBuffs.AddRange(_wideNext);
    }

    // One non-scanned entity: scene mechanic-id buffs → _wideNext.
    private void WideCheck(long uuid, object? obj)
    {
        try
        {
            if (!_wideComp.TryGetValue(uuid, out var comp))
            {
                obj ??= GetEntity(uuid);
                comp = obj != null ? _piBuffComp?.GetValue(obj) : null;
                _wideComp[uuid] = comp;
            }
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
                if (_scene != null && _scene.MechanicBuffs.Contains(baseId)) _wideNext.Add(ReadBuff(uuid, baseId, item));
            }
        }
        catch { _wideComp[uuid] = null; }
    }

    private void ResetWide()
    {
        _wideAt = 0; _wideBuffs.Clear(); _wideNext.Clear(); _wideComp.Clear();
    }
}
