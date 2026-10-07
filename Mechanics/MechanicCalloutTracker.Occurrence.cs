using System;
using System.Collections.Generic;
using static Stellar.RaidManager.MechanicCalloutData;

namespace Stellar.RaidManager;

// One NEW mechanic occurrence (fired once per instance by the tracker — consumer: the on-me alert).
// Group is the ENGLISH group (as McRow.Group); Label is localized.
internal sealed class McOccurrence
{
    public int       SceneId;
    public SceneKind Scene;
    public string    Key = "", Group = "", Label = "";
    public int       Color;
    public long      DurationMs;
    public string    LocalName = "", OtherNames = "";
    public bool      IsLocalTarget;
    public bool      LocalJoin;            // the local player joined an EXISTING row (not a new row occurrence)
    public McRow     Row = null!;          // live row (countdown; still-active check via IsRowActive)
    public string    InstanceKey = "";
}

// Occurrence detection — mirrors upstream's voice-cue dedupe (minimap-voice-dedupe.ts MinimapVoiceCueDeduper +
// voice-cue-utils buffInstanceKey): a bounded seen-set (200, FIFO eviction) of instance keys, reset on scene change.
// Our instance key = row key + the row's timing identity (CreateMs for buff rows — upstream's createTimeMs — or the
// local StartTick for cast / first-seen rows — upstream's per-cast timeMs) + an APPEARANCE EPOCH that bumps whenever
// the key re-enters after being absent from a scan. So:
//   - a row persisting across scans never refires (same key, same timing, same epoch);
//   - the same mechanic re-applied (new createTime) or re-cast refires;
//   - an untimed row (portal, ring sequence, pizza) refires when it comes back after expiring (epoch bump).
internal sealed partial class MechanicCalloutTracker
{
    public event Action<McOccurrence>? Occurrence;

    private const int OccurrenceMemory = 200;
    private readonly HashSet<string> _occSeen = new();
    private readonly Queue<string>   _occOrder = new();
    private readonly Dictionary<string, int> _occEpoch = new();
    private HashSet<string> _occPresent = new(), _occPresentNext = new();

    private void ResetOccurrences()
    {
        _occSeen.Clear(); _occOrder.Clear(); _occEpoch.Clear(); _occPresent.Clear(); _occPresentNext.Clear();
    }

    public bool IsRowActive(McOccurrence o) =>
        _rows.TryGetValue(o.Key, out var r) && ReferenceEquals(r, o.Row) && (!r.HasTimer || r.RemainSec > 0.05f);

    private void DetectOccurrences(SceneDef def)
    {
        _occPresentNext.Clear();
        long local = _services.CombatSnapshot.LocalEntityId.Value;
        foreach (var row in _rows.Values)
        {
            _occPresentNext.Add(row.Key);
            if (!_occPresent.Contains(row.Key))
                _occEpoch[row.Key] = (_occEpoch.TryGetValue(row.Key, out int ep) ? ep : 0) + 1;
            int epoch = _occEpoch[row.Key];
            string inst = $"{row.Key}|{row.CreateMs}|{row.StartTick}|{epoch}";
            bool fresh = Remember(inst);
            // Clock bias (HitOffset.cs): first sight of a NEW server-timed buff row samples (create − serverNow).
            if (fresh && row.StartTick <= 0) SampleClockBias(row.CreateMs);

            // Local-target instance: rows MERGE every target of a wave, so when a teammate got the debuff a moment
            // earlier the local player joins an EXISTING row and the row-level occurrence above never sees it
            // (raid logs: 0 on-me banners for Mirage Decay). Key per local buff instance; on a fresh row it is just
            // recorded (the row occurrence already carries IsLocalTarget), otherwise it fires as a LocalJoin.
            bool localJoin = false;
            if (row.LocalName.Length > 0)
            {
                string localInst = $"{row.Key}|local|{LocalBuffIdentity(row, local)}|{epoch}";
                bool newLocal = Remember(localInst);
                if (newLocal && !fresh) localJoin = true;
            }
            if ((!fresh && !localJoin) || Occurrence == null) continue;
            var o = new McOccurrence
            {
                SceneId = SceneId, Scene = def.Kind, Key = row.Key, Group = row.Group, Label = row.Label,
                Color = row.Color, DurationMs = row.DurationMs, LocalName = row.LocalName, OtherNames = row.OtherNames,
                IsLocalTarget = row.LocalName.Length > 0, Row = row, InstanceKey = inst, LocalJoin = localJoin,
            };
            try { Occurrence(o); }
            catch (Exception ex) { LogErrOnce("occurrence handler: " + ex.Message); }
        }
        (_occPresent, _occPresentNext) = (_occPresentNext, _occPresent);
    }

    // Bounded seen-set insert (FIFO eviction past OccurrenceMemory). True when the key is new.
    private bool Remember(string key)
    {
        if (!_occSeen.Add(key)) return false;
        _occOrder.Enqueue(key);
        if (_occOrder.Count > OccurrenceMemory) _occSeen.Remove(_occOrder.Dequeue());
        return true;
    }

    // The local player's own buff instance behind a buff row ("<uuid>:<create>"); "rule" for non-buff rows.
    private string LocalBuffIdentity(McRow row, long local)
    {
        int n = 0;
        while (n < row.Key.Length && char.IsDigit(row.Key[n])) n++;
        if (n == 0 || local == 0 || !int.TryParse(row.Key.AsSpan(0, n), out int baseId)) return "rule";
        foreach (var b in _buffs)
            if (b.BaseId == baseId && (b.Target >> 16) == (local >> 16)) return $"{b.BuffUuid}:{b.Create}";
        return "?";
    }
}
