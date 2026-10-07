using System;
using System.Collections.Generic;
using static Stellar.RaidManager.MechanicCalloutData;

namespace Stellar.RaidManager;

// Minimap view building (painted by MechanicMinimapPainter / Plugin.MechanicMinimap.cs). Built once per scan from the
// same snapshot as the rows, every raid scan (shown only while MapEnabled), and only for scenes that have a view builder (raid for now —
// add a case to BuildMinimap per scene). Also hosts the raid's coordinate-dependent row logic that the arena data
// unlocked: the "not in ring arena" gate and Preset Return's floor cell.
internal sealed partial class MechanicCalloutTracker
{
    public bool MapEnabled { get; set; }

    private readonly MinimapView _map = new();
    private bool _mapValid;
    // Built every scan in a scene with a view builder; exposed only while MapEnabled.
    public MinimapView? Map => _mapValid && MapEnabled ? _map : null;
    public int MapVersion { get; private set; }            // bumps on every rebuild (painter redraw trigger)

    // entityColorSlots: the colour of the last row each entity was added to as a target (AddTarget records it).
    private readonly Dictionary<long, int> _entColor = new();
    private readonly Dictionary<long, bool> _safeStatus = new();   // reef wave lanes: team member inside the safe lane
    private readonly List<MinimapRegion> _raidPresetRegions = new();

    // ── Raid: Preset Return (addPresetReturn) ────────────────────────────────────────────────────────────────
    // Count buff 829372/3/4 (= 1/2/3) + link buff 829318 on the same player; the floor cell is the cell nearest the
    // LINK buff's caster. Label "<n>F · <cell>" ("<n>F ?" when the caster isn't a scanned/positioned entity — same
    // limitation upstream has: its entity map only holds team + configured monsters). Region = that cell, labelled.
    private static readonly Dictionary<int, int> PresetCounts = new() { [829372] = 1, [829373] = 2, [829374] = 3 };

    private void RaidPresetReturnRows()
    {
        _raidPresetRegions.Clear();
        foreach (var b in _buffs)
        {
            if (!PresetCounts.TryGetValue(b.BaseId, out int count)) continue;
            if (!TryBuff(b.Target, 829318, out var link)) continue;
            var src = Ent(link.Fire);
            int cell = src is { HasPos: true } ? RaidArena.NearestCell(src.Pos.x, src.Pos.z) : -1;
            string label = cell >= 0 ? $"{count}F · {McText.T(RaidArena.Cells[cell].Name)}" : $"{count}F ?";
            long create = b.Create;
            if (link.Create > 0 && (create <= 0 || link.Create < create)) create = link.Create;
            var a = Upsert($"raid:preset:{count}:{(cell >= 0 ? RaidArena.Cells[cell].Id : "unknown")}",
                           "Divine Scale - Preset Return", label, count - 1, 8, create, b.Dur > link.Dur ? b.Dur : link.Dur);
            AddTarget(a, b.Target);
            if (cell >= 0) _raidPresetRegions.Add(RaidArena.CellRect(cell, count - 1, $"{count}F"));
        }
    }

    // ── View building ────────────────────────────────────────────────────────────────────────────────────────
    private void BuildMinimap(SceneDef def)
    {
        _mapValid = false;
        switch (def.Kind)
        {
            case SceneKind.Raid:    BuildRaidMap(); break;
            case SceneKind.SeaReef: BuildReefMap(); break;
            case SceneKind.Tina:    BuildTinaMap(); break;
            case SceneKind.CursedTomb: BuildTombMap(); break;
            case SceneKind.WastelandCourt: BuildWastelandMap(); break;
            case SceneKind.GiantTower: BuildGiantMap(); break;
            default: return;                     // no minimap for this scene yet
        }
        if (MapEnabled) ReadMarkers();         // party markers (Markers.cs) — only while the map is on
        _mapValid = true;
        MapVersion++;
    }

    private void BuildRaidMap()
    {
        ClearMap();
        RaidArena.Layout(_raidArena, _map);
        // Grid regions unless the arena is CONFIDENTLY ring (mechanic-backed). A Y-only "ring" verdict used to drop
        // every region here even with grid buffs active (first in-game report: dots shown, tiles never).
        if (!(_raidArena == RaidArena.Kind.Ring && _arenaConfident))
        {
            AddFloorRegions();                                          // floor damage UNDER the mechanic cells (local floor only)
            var seen = new HashSet<int>();                              // dedupeRegions: one danger rect per cell
            // Edge-Mid / Corner Explosion (829214/829215) = DANGER floor tiles (user correction): the SAME red no-go
            // Style 3 as Phase Mapping, NOT a plain colour-slot cell. The old Cell(id,colour) drew Style-0 fills
            // (cyan/red), which read like a player assignment instead of a hazard to avoid standing on.
            void DangerCell(string id)
            {
                int i = RaidArena.CellIndex(id);
                if (i < 0 || !seen.Add(i)) return;
                var r = RaidArena.CellRect(i, 3);
                r.Style = 3;
                _map.Regions.Add(r);
            }
            foreach (var b in _buffs)
            {
                if (b.BaseId == 829214) foreach (var c in RaidArena.EdgeMidCells) DangerCell(c);   // Edge-Mid Explosion
                else if (b.BaseId == 829215) foreach (var c in RaidArena.CornerCells) DangerCell(c); // Corner Explosion
            }
            // Phase Mapping = DANGER tiles, one red no-go style (Style 3), above floor damage.
            foreach (int i in _phaseDanger)
            {
                var r = RaidArena.CellRect(i, 3);
                r.Style = 3;
                _map.Regions.Add(r);
            }
            _map.Regions.AddRange(_raidPresetRegions);
            AddCrystalRegions();                                        // crystals + pressed tiles, on top of 1F/2F/3F (Crystals.cs)
        }
        if (_raidArena == RaidArena.Kind.Ring && _arenaConfident) AddRingBand();   // latest ring band (Rules.Ring.cs)
        // Raid renders unlocalized; dot floor = the local player's (SameFloor). Pinball ball = slot 5, only while ARMED
        // (Rules.Pinball.cs) — an idle pooled ball has no colour and so no dot.
        AddDots(e => SameFloor(e.Pos.y), e => e.MonsterId == PinballBallId && BallArmed(e.Uuid) ? 5 : -1);
    }

    private void ClearMap() { _map.Clear(); _mapDotGo.Clear(); }

    // visibleEntities (every scene): local + teammates, and monsters that are a BOSS (SceneDef.Bosses, MonsterType 2)
    // or carry a mechanic colour — so invisible helpers (markers, dummies, blink points) never draw as a red
    // triangle unless a mechanic coloured them. Colour precedence: the scene's explicit slot (monsterSlot /
    // playerSlot, e.g. tower state, rune, safe-zone status) → the last mechanic row the entity was a target of.
    private void AddDots(System.Func<McEnt, bool> inArea, System.Func<McEnt, int>? monsterSlot = null,
                         System.Func<McEnt, int>? playerSlot = null)
    {
        long local = _services.CombatSnapshot.LocalEntityId.Value;
        foreach (var e in _ents.Values)
        {
            if (!e.HasPos || !inArea(e)) continue;
            int slot = _entColor.TryGetValue(e.Uuid, out int s) ? s : -1;
            if (e.IsPlayer)
            {
                bool isLocal = local != 0 && (e.Uuid >> 16) == (local >> 16);
                if (!isLocal && !IsTeam(e.Uuid)) continue;
                int ps = playerSlot?.Invoke(e) ?? -1;
                var dot = new MinimapDot { X = e.Pos.x, Z = e.Pos.z, Slot = ps >= 0 ? ps : slot,
                                           Kind = isLocal ? MinimapDotKind.Local : MinimapDotKind.Teammate, Dead = e.IsDead };
                if (isLocal)
                {
                    // Facing arrow: rendered model yaw, else the server AttrDir (centidegrees / 100).
                    if (TryGoYaw(e.GoComp, out float yaw)) { dot.Facing = yaw; dot.HasFacing = true; }
                    else if (!float.IsNaN(e.Facing)) { dot.Facing = e.Facing; dot.HasFacing = true; }
                }
                _mapDotGo.Add(e.GoComp);
                _map.Dots.Add(dot);
            }
            else
            {
                int ms = monsterSlot?.Invoke(e) ?? -1;
                if (ms >= 0) slot = ms;
                if (slot < 0 && !_scene!.Bosses.Contains(e.MonsterId)) continue;
                _mapDotGo.Add(e.GoComp);
                _map.Dots.Add(new MinimapDot { X = e.Pos.x, Z = e.Pos.z, Slot = slot, Kind = MinimapDotKind.Monster });
            }
        }
    }

    // Single-arena scenes: upstream yInArena (|y − centre y| ≤ range) && inBossArea. If the LOCAL player isn't inside
    // the spec's Y band our model-Y frame evidently differs from upstream's → fall back to "same floor as me".
    private bool InSpecArena(MinimapArenaSpec spec, McEnt e)
    {
        if (!spec.InBossArea(e.Pos.x, e.Pos.z)) return false;
        return _hasLocalPos && spec.YIn(_localPos.y) ? spec.YIn(e.Pos.y) : SameFloor(e.Pos.y);
    }

    // ── Fast position path (per frame while the minimap is visible) ──────────────────────────────────────────
    // The scan (~200 ms) decides WHICH dots exist and their colours; between scans the painter's tick re-reads just
    // those dots' ModelGoComp.Position (cached at scan time, parallel to _map.Dots — List<object?> indexed, no
    // dictionary walk, typed getter, no allocation). A failed read hides that dot until the next scan rebuilds the
    // list. Returns true when any dot moved more than MoveEpsilon (world units) — the repaint trigger.
    private readonly List<object?> _mapDotGo = new();
    private const float MoveEpsilon = 0.05f;

    private static float DeltaDeg(float a, float b) => ((a - b) % 360f + 540f) % 360f - 180f;

    public bool RefreshDotPositions()
    {
        if (!_mapValid || !MapEnabled) return false;
        bool moved = false;
        var dots = _map.Dots;
        int n = dots.Count < _mapDotGo.Count ? dots.Count : _mapDotGo.Count;
        for (int i = 0; i < n; i++)
        {
            var go = _mapDotGo[i];
            if (go == null) continue;
            var d = dots[i];
            // An exactly-zero position = a recycled/destroyed component that read back zeros rather than throwing.
            if (!TryGoPos(go, out var p) || (p.x == 0f && p.y == 0f && p.z == 0f))
            {
                _mapDotGo[i] = null;
                d.Hidden = true; dots[i] = d; moved = true;
                continue;
            }
            bool changed = false;
            float dx = p.x - d.X, dz = p.z - d.Z;
            if (dx * dx + dz * dz > MoveEpsilon * MoveEpsilon) { d.X = p.x; d.Z = p.z; changed = true; }
            // Local arrow: follow the rendered model yaw at the same rate (repaint on > 2° of turn).
            if (d.Kind == MinimapDotKind.Local && TryGoYaw(go, out float yaw)
                && (!d.HasFacing || MathF.Abs(DeltaDeg(yaw, d.Facing)) > 2f))
            { d.Facing = yaw; d.HasFacing = true; changed = true; }
            if (!changed) continue;
            dots[i] = d;
            moved = true;
        }
        return moved;
    }
}
