using System.Collections.Generic;
using UnityEngine;
using static Stellar.RaidManager.MechanicCalloutData;

namespace Stellar.RaidManager;

// Raid pinball-ball finder (the functional half of Experiment's pinball probe; its discovery logging stays there).
// The pinball cast buff 829314 rides a scene object, and the ball (id 10330051, a DummyTable row "因果折跃") may be
// spawned as an entity type the regular scan never looks at. So while 829314 is active in the raid and for 8 s after,
// every scan also walks EVERY entity type within 60 m of the local player, resolves its id with the monster-id ladder
// (MonsterId.cs), and adds anything whose id is 10330051 — whatever its entity type — to the scan as the BALL, so the
// "Ball" row (Rules.Pinball.cs) and the orange (slot 5) minimap dot pick it up.
internal sealed partial class MechanicCalloutTracker
{
    private const int  PinballBallId = 10330051, PinballCastBuff = 829314;
    private const long PinballProbeTailMs = 8000;
    private const float PinballProbeRange = 60f;

    private long _pinballProbeUntil;
    private readonly Dictionary<long, int> _probeIds = new();          // uuid → resolved id (0 retried)

    private bool PinballProbeActive(SceneDef def, long now) => def.Kind == SceneKind.Raid && now < _pinballProbeUntil;

    private void PinballProbeArm(long now)
    {
        if (AnyBuff(PinballCastBuff, out _)) _pinballProbeUntil = now + PinballProbeTailMs;
    }

    private void ResetPinballProbe()
    {
        _pinballProbeUntil = 0; _probeIds.Clear();
    }

    // One non-scanned entity during the probe window. obj may be null (resolved here only when needed).
    private void ProbeEntity(long uuid, object? obj)
    {
        if (!_hasLocalPos || _ents.ContainsKey(uuid)) return;
        obj ??= GetEntity(uuid);
        if (obj == null || !TryReadPos(obj, out var pos, out var go)) return;
        if (Vector3.Distance(pos, _localPos) > PinballProbeRange) return;

        if (!_probeIds.TryGetValue(uuid, out int id) || id == 0)
        {
            if (_probeIds.Count > 4096) _probeIds.Clear();             // bullets churn uuids
            _probeIds[uuid] = id = ReadMonsterId(obj);
        }
        if (id != PinballBallId) return;

        // The ball: join the scan like a relevant monster (Ball row + orange dot).
        if (!_firstSeen.TryGetValue(uuid, out long seen)) _firstSeen[uuid] = seen = System.Environment.TickCount64;
        _ents[uuid] = new McEnt { Uuid = uuid, MonsterId = PinballBallId, FirstSeenTick = seen, HasPos = true, Pos = pos, GoComp = go };
    }
}
