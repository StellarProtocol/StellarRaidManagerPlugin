using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using static Stellar.RaidManager.MechanicCalloutData;

namespace Stellar.RaidManager;

// Row building for MechanicCalloutTracker. Every poll the table + rules Upsert into a fresh accumulator (upstream
// upsertRow: same key ⇒ targets merge, min non-zero create, max duration); Reconcile then carries each key into the
// persistent McRow, re-snapping the countdown ONLY when the merged timing changes (Buff-Tracking.md §7 — snap once,
// tick locally), and drops keys that weren't produced this poll.
internal sealed partial class MechanicCalloutTracker
{
    private sealed class RowAcc
    {
        public string Group = "", Label = "";
        public int    Color, Order;
        public long   CreateMs, StartTick, DurMs;
        public readonly List<(long Uuid, int Safe)> Targets = new();   // Safe: -1 n/a, 0 out, 1 safe
    }

    private readonly Dictionary<string, RowAcc> _acc  = new();
    private readonly Dictionary<string, McRow>  _rows = new();
    private readonly List<string> _stale = new();

    private void BuildRows(SceneDef def)
    {
        _acc.Clear();
        _entColor.Clear();
        _safeStatus.Clear();
        UpdateLocalPos();
        if (def.Kind == SceneKind.Raid) UpdateRaidArena();
        if (!RaidRingGate(def)) TableRows(def);   // raid ring arena: upstream skips the callout/phase rows
        ApplyRules(def);
        Reconcile();
        if (def.Kind == SceneKind.Raid) RaidReleaseCheck();   // boss release buff = hit moment (Release.cs)
        Flatten();
        DetectOccurrences(def);                 // new-occurrence events (Occurrence.cs) — alerts / voice
        BuildMinimap(def);
    }

    // Adds or merges a row. createMs = server-epoch ms (buff timing); startTick = TickCount64 for locally-observed
    // events (first-seen / skill cast) — whichever the source has; 0 = unknown. The label is localized here
    // (McText.T: a known English mechanic name → the active language; composed labels arrive already localized and
    // pass through unchanged); the group stays English (grouping + hit-offset keys are language-independent).
    private RowAcc Upsert(string key, string group, string label, int color, int order, long createMs, long durMs,
                          long startTick = 0)
    {
        if (!_acc.TryGetValue(key, out var a))
        {
            _acc[key] = a = new RowAcc { Group = group, Label = McText.T(label), Color = color, Order = order,
                                         CreateMs = createMs, StartTick = startTick, DurMs = durMs };
            return a;
        }
        if (createMs > 0 && (a.CreateMs <= 0 || createMs < a.CreateMs)) a.CreateMs = createMs;
        if (startTick > 0 && (a.StartTick <= 0 || startTick < a.StartTick)) a.StartTick = startTick;
        if (durMs > a.DurMs) a.DurMs = durMs;
        return a;
    }

    // Also records the entity's mechanic colour (upstream entityColorSlots.set(target, colorSlot)) for the minimap;
    // safe-status rows don't colour (upstream keeps those in a separate safe-status map).
    // colourEntity=false keeps the target's minimap dot uncoloured (Phase Mapping danger rows).
    private void AddTarget(RowAcc a, long uuid, int safe = -1, bool colourEntity = true)
    {
        if (safe < 0) { if (colourEntity) _entColor[uuid] = a.Color; }
        else _safeStatus[uuid] = safe == 1;      // upstream entitySafeStatus (minimap dot: green safe / red out)
        foreach (var t in a.Targets) if (t.Uuid == uuid) return;   // mergeMechanicTargets: dedupe by uuid
        a.Targets.Add((uuid, safe));
    }

    // ── Static table rows (MechanicCallouts.Data.cs) ─────────────────────────────────────────────────────────
    private void TableRows(SceneDef def)
    {
        foreach (var b in _buffs)
        {
            if (!def.Buffs.TryGetValue(b.BaseId, out var d)) continue;
            long create = b.Create, dur = b.Dur;
            if (d.RequiresBuff != 0)
            {
                // Pair rule (upstream addPresetReturn): the count buff only means something alongside the link buff
                // on the SAME entity; the row spans both (min create, max duration).
                if (!TryBuff(b.Target, d.RequiresBuff, out var link)) continue;
                if (link.Create > 0 && (create <= 0 || link.Create < create)) create = link.Create;
                if (link.Dur > dur) dur = link.Dur;
            }
            if (dur <= 0 && d.DefaultDurationMs > 0) dur = d.DefaultDurationMs;

            string key = d.RowKey ?? d.Key switch
            {
                KeyMode.ByBase    => $"{b.BaseId}",
                KeyMode.PerTarget => $"{b.BaseId}:t{b.Target}",
                KeyMode.PerCreate => $"{b.BaseId}:c{b.Create}",
                KeyMode.PerSource => $"{b.BaseId}:s{b.Fire}",
                _                 => $"{b.BaseId}:{b.Layer}",
            };
            var a = Upsert(key, d.Group, d.AppendLayer ? $"{d.Label} x{b.Layer}" : d.Label, d.Color, d.Order, create, dur);
            // Only entities in our scan get named (upstream toMechanicTargets: target must be in its entity map) —
            // a wide-scan carrier (Raid WideScan.cs) yields a row/region without names.
            if (Ent(b.Target) is { } te)
            {
                // Floor-danger mechanics (ColourEntity=false, e.g. Edge-Mid / Corner Explosion) target the FLOOR, not a
                // player: mirror Phase Mapping — never colour a dot, and don't list a MONSTER carrier as a "target"
                // (a real player carrier, if any, is still named but stays uncoloured).
                if (d.ColourEntity) AddTarget(a, b.Target);
                else if (te.IsPlayer) AddTarget(a, b.Target, colourEntity: false);
            }
        }
    }

    // ── Accumulator → persistent rows ────────────────────────────────────────────────────────────────────────
    private void Reconcile()
    {
        PruneExpired();                         // expired countdowns never reach a row (Expiry.cs)
        _stale.Clear();
        foreach (var k in _rows.Keys) if (!_acc.ContainsKey(k)) _stale.Add(k);
        foreach (var k in _stale) _rows.Remove(k);

        long localUuid = _services.CombatSnapshot.LocalEntityId.Value;
        foreach (var kv in _acc)
        {
            var a = kv.Value;
            if (!_rows.TryGetValue(kv.Key, out var row))
                _rows[kv.Key] = row = new McRow { Key = kv.Key, CreateMs = -1, DurationMs = -1 };
            row.Group = a.Group; row.Label = a.Label; row.Color = a.Color; row.Order = a.Order;
            if (row.CreateMs != a.CreateMs || row.StartTick != a.StartTick || row.DurationMs != a.DurMs)
            {
                row.CreateMs = a.CreateMs; row.StartTick = a.StartTick; row.DurationMs = a.DurMs;
                if (a.StartTick > 0) { row.SnapTick = a.StartTick; row.SnapRemain = a.DurMs > 0 ? a.DurMs / 1000f : -1f; }
                else { row.SnapTick = Environment.TickCount64; row.SnapRemain = ComputeSnapRemain(a.DurMs, a.CreateMs); }
            }
            row.OffsetSec = row.HasTimer ? RowHitOffset(row) : 0f;   // per-mechanic; shown countdown = remain − offset
            if (!row.HasTimer) _hitSeenUntimed.Add(MechKey(row));
            FinishTargets(row, a, localUuid);
        }
    }

    // Local player first (strict uuid OR roleId — the local uuid carries the isClient bit, Threat-Aggro-Tracking.md);
    // a safe-status target gets an inline coloured "(safe)"/"(out)" (upstream's ✓/✗ chips — words because the HUD
    // font's coverage of ✓/✗ is unverified; the names Text is rich-text, see Plugin.MechanicCalloutsHud.cs; localized).
    private void FinishTargets(McRow row, RowAcc a, long localUuid)
    {
        string local = "";
        var others = new List<string>(a.Targets.Count);
        foreach (var t in a.Targets)
        {
            string name = NameFor(t.Uuid);
            if (t.Safe >= 0) name += t.Safe == 1 ? $" <color=#22C55E>{McText.L("rm.mech.safe")}</color>"
                                                 : $" <color=#EF4444>{McText.L("rm.mech.out")}</color>";
            bool isLocal = localUuid != 0 && (t.Uuid == localUuid || (t.Uuid >> 16) == (localUuid >> 16));
            if (isLocal && local.Length == 0) local = name;
            else others.Add(name);
        }
        row.LocalName  = local;
        row.OtherNames = string.Join(", ", others);
    }

    // Groups by their lowest Order (table position; rules use 100+), rows inside by colour then label (upstream sort).
    private void Flatten()
    {
        _lines.Clear();
        var rows = new List<McRow>(_rows.Values);
        var groupOrder = new Dictionary<string, int>();
        foreach (var r in rows)
            if (!groupOrder.TryGetValue(r.Group, out int o) || r.Order < o) groupOrder[r.Group] = r.Order;
        rows.Sort((a, b) =>
        {
            int g = groupOrder[a.Group].CompareTo(groupOrder[b.Group]);
            if (g != 0) return g;
            int gs = string.CompareOrdinal(a.Group, b.Group);
            if (gs != 0) return gs;
            int c = a.Color.CompareTo(b.Color);
            return c != 0 ? c : string.CompareOrdinal(a.Label, b.Label);
        });
        string? lastGroup = null;
        foreach (var r in rows)
        {
            if (r.Group != lastGroup) { _lines.Add(new McLine(McText.T(r.Group))); lastGroup = r.Group; }
            _lines.Add(new McLine(r));
        }
        RowCount = rows.Count;
    }

    // ── Clock ────────────────────────────────────────────────────────────────────────────────────────────────
    // Server clock = the framework's CombatSnapshot.ServerNowMs — NEVER the game's ZServerTime singleton
    // (Server-Clock-and-Ping-HUD.md: reflecting it stalls ping + day/night). 0 until synced.
    private long ServerNowMs()
    {
        long now = _services.CombatSnapshot.ServerNowMs;
        return now >= 1_600_000_000_000L ? now : 0;
    }

    // A server-epoch timestamp mapped onto the local TickCount64 timeline (0 when the clock isn't synced) — lets a
    // rule mix buff create times with locally-observed first-seen/cast ticks (upstream mixes them the same way).
    private long TickFromServer(long serverMs)
    {
        long now = ServerNowMs();
        return now == 0 || serverMs <= 0 ? 0 : Environment.TickCount64 - (now - serverMs);
    }

    // Remaining seconds = CreateTime + Duration − serverNow (Buff-Tracking.md §7). No create time / pre-sync clock
    // → full duration.
    private float ComputeSnapRemain(long durMs, long createMs)
    {
        if (durMs <= 0) return -1f;
        long now = ServerNowMs();
        if (createMs > 0 && now > 0)
        {
            // + ClockBiasMs: our server clock lags the buff timestamps (HitOffset.cs).
            float remain = (createMs + durMs - (now + ClockBiasMs)) / 1000f;
            float full   = durMs / 1000f;
            return remain < 0f ? 0f : (remain > full ? full : remain);
        }
        return durMs / 1000f;
    }

    // ── Names ────────────────────────────────────────────────────────────────────────────────────────────────
    // Threat-Aggro-Tracking.md ladder: AttrName off the live entity → PartyRoster (roleId) → CombatLookup → self →
    // "Player <roleId>". Only real names are cached, so a fallback retries on the next poll.
    private string NameFor(long uuid)
    {
        if (_names.TryGetValue(uuid, out var cached)) return cached;
        string n = ReadAttrName(uuid);
        if (n.Length == 0) n = PartyName(uuid >> 16);
        if (n.Length == 0)
        {
            try { n = _services.CombatLookup.GetEntityName(new EntityId(uuid)) ?? ""; } catch { n = ""; }
        }
        if (n.Length == 0)
        {
            long local = _services.CombatSnapshot.LocalEntityId.Value;
            if (local != 0 && (uuid >> 16) == (local >> 16))
                try { n = _services.PlayerState?.Name ?? ""; } catch { n = ""; }
        }
        if (n.Length == 0) return McText.F("rm.mech.player", uuid >> 16);
        _names[uuid] = n;
        return n;
    }

    private string PartyName(long charId)
    {
        try
        {
            var members = _services.PartyRoster?.Members;
            if (members == null) return "";
            foreach (var m in members)
                if (m.CharId == charId && !string.IsNullOrEmpty(m.Name)) return m.Name;
        }
        catch { }
        return "";
    }
}
