using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using static Stellar.RaidManager.MechanicCalloutData;

namespace Stellar.RaidManager;

// One rendered callout row: `● <label>   <countdown>   <names>`. Targets are pre-split into the local player's name
// (highlighted) and the comma-joined rest, so the HUD row lambdas never build strings per frame.
// Group = the ENGLISH group name (row grouping + the hit-offset key g_<slug> stay language-independent); Label is
// already localized (Upsert runs it through McText.T). The HUD localizes the group header via McText.T.
internal sealed class McRow
{
    public string Key = "", Group = "", Label = "";
    public int    Color, Order;
    public long   CreateMs, DurationMs;          // merged: min create (non-zero), max duration — upstream upsertRow
    public long   StartTick;                     // local-event rows (first-seen / skill cast): TickCount64 start
    public long   SnapTick;                      // Environment.TickCount64 at the last (re)snap
    public long   Arrival;                       // arrival sequence (Order.cs) — set once when the row first appears
    public float  SnapRemain = -1f;              // seconds at SnapTick; -1 = no timer
    public string LocalName = "", OtherNames = "";

    public bool  HasTimer  => DurationMs > 0;
    public float RemainSec => DurationMs <= 0 ? -1f
        : MathF.Max(0f, SnapRemain - (Environment.TickCount64 - SnapTick) / 1000f);
    // Per-mechanic hit offset (HitOffset.cs): the mechanic lands this long BEFORE the debuff expires. The DISPLAYED
    // countdown reaches 0 at the hit; the row itself still lives as long as the buff (RemainSec).
    public float OffsetSec;
    public float ShownRemainSec => DurationMs <= 0 ? -1f : IsHitNow ? 0f : MathF.Max(0f, RemainSec - OffsetSec);
    // Set when the boss "release" buff marks the actual hit (Release.cs): the countdown shows NOW briefly.
    public long  HitNowUntil;
    // …and while the hit-offset window runs (shown countdown reached 0, debuff still up) — never a static "0.0 s".
    public bool  IsHitNow => HitNowUntil > Environment.TickCount64
                             || (OffsetSec > 0f && DurationMs > 0 && RemainSec > 0f && RemainSec <= OffsetSec);
}

// A flattened HUD line: a group header (localized) or a row. The HUD binds a fixed slot pool over this list.
internal readonly struct McLine
{
    public readonly string? Header;
    public readonly McRow?  Row;
    public McLine(string header) { Header = header; Row = null; }
    public McLine(McRow row)     { Header = null;   Row = row; }
}

// One scanned entity this poll: a player, or a non-player whose monster id is in the scene's relevant-monster list.
internal sealed class McEnt
{
    public long    Uuid;
    public int     MonsterId;                // ZEntity.BaseId (MonsterId.cs); 0 for players
    public bool    IsPlayer;
    public long    FirstSeenTick;            // TickCount64 when first seen in this scene (upstream entityFirstSeen)
    public bool    HasPos;
    public Vector3 Pos;                      // ZEntity.Model.ModelGoComp.Position (only read where a rule needs it)
    public object? GoComp;                   // the ModelGoComp itself (minimap per-frame position refresh)
    public bool    IsDead;                   // players: live HP == 0 with MaxHp > 0 (Entity-Attributes-DeadState.md)
    // Facing (yaw degrees, upstream convention: 0 = +Z, 90 = +X, forward = (sin f, cos f)). Non-players: the RENDERED
    // model rotation when readable (FacingFromRot), else AttrDir (EAttrType 50) / 100 — in game AttrDir read the same
    // value for every Tina pizza dummy (→ one overlapped slice), so the rotation wins.
    public float   Facing = float.NaN;
    public bool    FacingFromRot;
}

// One mechanic-relevant buff (base id in SceneDef.MechanicBuffs) on a scanned entity.
internal readonly struct McBuff
{
    public readonly long Target, Create, Dur, Fire;
    public readonly int  BaseId, Layer, BuffUuid;
    public McBuff(long target, int baseId, int layer, int buffUuid, long create, long dur, long fire)
    { Target = target; BaseId = baseId; Layer = layer; BuffUuid = buffUuid; Create = create; Dur = dur; Fire = fire; }
}

/// <summary>
/// "Mechanic Callouts" poller (ported from StellarExperimentPlugin, minus its diagnostics/recorder). ONLY while enabled
/// + in-world + in a scene listed in <see cref="MechanicCalloutData.Scenes"/>: walks the AOI entities
/// (ZEntityMgr.entityDict_) — players plus the scene's relevant monsters/dummies — reads each one's FULL unfiltered
/// server buff list (pBuffList_/buffList_; mechanic debuffs are hidden/NotShow — Buff-Tracking.md §2), keeps the
/// scene's mechanic buff ids, then builds rows: the static buff→label table (MechanicCallouts.Data.cs) plus the
/// per-scene rules (MechanicCalloutTracker.Rules*.cs: monster buffs, buff casters, PlayEffect ids, monster presence,
/// skill casts). Rows merge by key (upstream upsertRow), local player first. Outside a supported scene it does
/// nothing but read the scene id. See Knowledge Base/Mechanic-Callouts.md for the validated findings behind it.
/// </summary>
internal sealed partial class MechanicCalloutTracker : IDisposable
{
    private const int EntMonster = 1, EntChar = 10, EntDummy = 11;   // EEntityType, = (uuid >> 6) & 31

    private readonly IPluginServices _services;
    private IDisposable? _tick;
    private int _tickCount;

    public bool Enabled { get; private set; }
    public int  SceneId { get; private set; }

    // ── Output ──
    private readonly List<McLine> _lines = new();
    public IReadOnlyList<McLine> Lines => _lines;
    public int RowCount { get; private set; }

    // ── Per-scene state (cleared on scene change) ──
    private SceneDef? _scene;
    private readonly Dictionary<long, McEnt>  _ents = new();         // present THIS poll
    private readonly List<McBuff>             _buffs = new();        // mechanic buffs on present entities
    private readonly Dictionary<long, long>   _firstSeen = new();    // uuid → TickCount64 first seen (kept while in scene)
    private readonly Dictionary<long, string> _names = new();        // uuid → resolved name

    public MechanicCalloutTracker(IPluginServices services) => _services = services;

    public void SetEnabled(bool on)
    {
        if (on == Enabled) return;
        Enabled = on;
        // 100 ms tick: skill-cast sampling runs every tick (casts are short attr flips), the buff/entity scan + row
        // build every 2nd tick (≈ 200 ms).
        if (on) _tick ??= _services.Framework.Every(TimeSpan.FromMilliseconds(100), Poll);
        else
        {
            _tick?.Dispose(); _tick = null;
            ClearOutput();
        }
        McBuffEffectPatch.Active = false;   // re-armed by the next poll in a supported scene
    }

    public void Dispose()
    {
        _tick?.Dispose(); _tick = null;
        McBuffEffectPatch.Active = false;
        McBuffEffectPatch.Clear();
        ClearOutput();
    }

    private void ClearOutput()
    {
        _rows.Clear(); _lines.Clear(); RowCount = 0;
        _mapValid = false;
        PhaseDangerCell = "";
    }

    private void ResetScene()
    {
        _names.Clear(); _rows.Clear(); _ents.Clear(); _buffs.Clear(); _firstSeen.Clear();
        ResetMonsterIds(); _mkSlotByKey.Clear(); _casts.Clear(); _castState.Clear(); _entColor.Clear(); _mapValid = false;
        ResetWide(); ResetOccurrences(); ResetPinballProbe(); ResetPinballBalls(); ResetExpiry(); ResetRing(); ResetFloor();
        _raidArena = RaidArena.Kind.Unknown;
        McBuffEffectPatch.Clear();          // buff uuids are recycled across scenes (Buff-Tracking.md §8)
    }

    // Effect-id capture (McBuffEffectPatch) is installed lazily on the first poll inside a supported scene, so a
    // player who never enters one never gets the Harmony prefixes.
    private void EnsureEffectPatch()
    {
        if (McBuffEffectPatch.Installed) return;
        McBuffEffectPatch.Install(_services.Harmony.Create("mechcallouts"), _services.Log.Warning);
    }

    // ── Poll ─────────────────────────────────────────────────────────────────────────────────────────────────
    private void Poll()
    {
        try
        {
            if (_services.ClientState.Phase != GamePhase.World
                || (_services.ClientState.UiState & GameUIState.Loading) != 0)
            { McBuffEffectPatch.Active = false; ClearOutput(); return; }

            int scene = ReadSceneId();
            if (scene != SceneId) { SceneId = scene; ResetScene(); }
            if (!Scenes.TryGetValue(scene, out var def))
            {
                _scene = null;
                McBuffEffectPatch.Active = false;
                ClearOutput();
                return;                         // unsupported scene: no entity/buff work at all
            }
            _scene = def;
            if (!EnsureApi()) { ClearOutput(); return; }
            EnsureEffectPatch();
            McBuffEffectPatch.Active = true;    // record PlayEffect ids only while in a supported scene

            bool full = (_tickCount++ & 1) == 0 || _ents.Count == 0;
            if (full) ScanEntities(def);
            SampleCasts();
            if (full) BuildRows(def);
        }
        catch (Exception ex)
        {
            LogErrOnce(ex.InnerException?.Message ?? ex.Message);
        }
    }

    private bool _loggedErr;
    private void LogErrOnce(string msg)
    {
        if (_loggedErr) return;
        _loggedErr = true;
        _services.Log.Warning($"[MechCallout] {msg}");
    }
}
