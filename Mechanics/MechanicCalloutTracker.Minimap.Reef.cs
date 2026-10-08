using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// Sea-Ringed Reef minimap — port of scenes/s3-sea-ringed-reef/{index.ts, arena.ts, mechanics/boss.ts, matrix.ts}.
// Two arenas: MATRIX (rune matrices + callout beams) and BOSS (wave safe lanes, pizza sectors, orbs). Upstream picks
// the arena by the local player's Y (nearest of 75 / 27); a matrix monster (4639) or the boss (4601) in the scan
// decides first here (our model Y frame is unverified — the raid needed the same fallback). Everything is localized
// to the chosen arena's centre (view origin) and drawn with its rotationQuarters (matrix 2, boss 3; unknown → boss
// layout at rotation 0, as upstream).
internal sealed partial class MechanicCalloutTracker
{
    private const int ReefMatrixMonster = 4639, ReefBoss = 4601;
    private const int ReefIceWave = 3340219, ReefSeaWave = 3340220, ReefPizzaSkill = 3340245;
    private static readonly Dictionary<int, int> ReefOrbColor = new() { [4604] = 7, [4605] = 4 };   // ice ball / bubble

    private void BuildReefMap()
    {
        ClearMap();
        bool matrix = false, known = true;
        if (AnyMonster(ReefMatrixMonster)) matrix = true;
        else if (AnyMonster(ReefBoss)) { }
        else if (_hasLocalPos)
            matrix = MathF.Abs(_localPos.y - MinimapArenas.ReefMatrix.Cy) <= MathF.Abs(_localPos.y - MinimapArenas.ReefBoss.Cy);
        else known = false;
        var spec = matrix ? MinimapArenas.ReefMatrix : MinimapArenas.ReefBoss;
        spec.Apply(_map);
        if (!known) _map.RotationQuarters = 0;

        if (matrix) ReefMatrixRegions(spec);
        else ReefBossRegions(spec);
    }

    // ── Matrix arena (buildMatrixMechanicView) ───────────────────────────────────────────────────────────────
    // Rune buffs 883707-883710 colour their matrix (A green 1, B brown 7 (upstream blue), C pink 6, D yellow 0). Each 522602 callout
    // draws a 10 px beam from its SOURCE matrix through the target, extended to 48 units.
    private void ReefMatrixRegions(MinimapArenaSpec spec)
    {
        var rc = new Dictionary<long, int>();
        foreach (var b in _buffs)
            if (ReefRuneColor.TryGetValue(b.BaseId, out int c) && Ent(b.Target)?.MonsterId == ReefMatrixMonster)
                rc[b.Target] = c;
        foreach (var b in _buffs)
        {
            if (b.BaseId != 522602) continue;
            var target = Ent(b.Target); var src = Ent(b.Fire);
            if (target is not { HasPos: true } || src is not { HasPos: true } || src.MonsterId != ReefMatrixMonster) continue;
            float dx = target.Pos.x - src.Pos.x, dz = target.Pos.z - src.Pos.z, d = MathF.Sqrt(dx * dx + dz * dz);
            if (d <= 0f) continue;
            int color = rc.TryGetValue(src.Uuid, out int c) ? c : 0;
            _map.Regions.Add(MinimapRegion.Line(src.Pos.x, src.Pos.z, src.Pos.x + dx / d * 48f, src.Pos.z + dz / d * 48f, color, 10f));
        }
        AddDots(e => ReefOnFloor(spec, e), e => e.MonsterId == ReefMatrixMonster && rc.TryGetValue(e.Uuid, out int c) ? c : -1);
    }

    // ── Boss arena (buildBossMechanicView) ───────────────────────────────────────────────────────────────────
    private void ReefBossRegions(MinimapArenaSpec spec)
    {
        // Waves: latest ice / sea wave monster; a 4-unit SAFE lane across the arena along its facing axis, drawn
        // green (1) when the local player is inside it, red (3) otherwise.
        McEnt? ice = null, sea = null;
        foreach (var e in _ents.Values)
        {
            if (!e.HasPos || float.IsNaN(e.Facing)) continue;
            if (e.MonsterId == ReefIceWave) ice = e; else if (e.MonsterId == ReefSeaWave) sea = e;
        }
        float inner = MathF.Max(0f, WaveHalfWidth - PlayerRadius);
        foreach (var w in new[] { ice, sea })
        {
            if (w == null) continue;
            bool vertical = IsVerticalFacing(w.Facing);
            float axis = vertical ? w.Pos.x : w.Pos.z;
            bool inBand = _hasLocalPos && !_localDead && MathF.Abs((vertical ? _localPos.x : _localPos.z) - axis) < inner;
            int color = inBand ? 1 : 3;
            _map.Regions.Add(vertical
                ? new MinimapRegion { Kind = MinimapRegionKind.Rect, X = w.Pos.x, Z = spec.Cz, HalfX = WaveHalfWidth, HalfZ = spec.HalfZ, Color = color }
                : new MinimapRegion { Kind = MinimapRegionKind.Rect, X = spec.Cx, Z = w.Pos.z, HalfX = spec.HalfX, HalfZ = WaveHalfWidth, Color = color });
        }

        // Pizza: the indicator dummy (cast 3340245, still present) spins its facing; two opposite 90° sectors of
        // radius 20 around it. Purple marker 883634 rotates the danger diagonal 90° (colour 9), orange 883633 (5),
        // none → red 3. The indicator takes that colour.
        McEnt? ind = null; long indTick = -1;
        foreach (var c in _casts)
            if (c.SkillId == ReefPizzaSkill && c.Tick > indTick && Ent(c.Caster) is { HasPos: true } ce) { ind = ce; indTick = c.Tick; }
        int pizzaColor = -1; long pizzaUuid = 0;
        if (ind != null && !float.IsNaN(ind.Facing))
        {
            bool purple = AnyBuff(883634, out _), orange = !purple && AnyBuff(883633, out _);
            pizzaColor = purple ? 9 : orange ? 5 : 3;
            float baseDeg = ((ind.Facing + (purple ? 90f : 0f)) % 360f + 360f) % 360f;
            _map.Regions.Add(MinimapRegion.Sector(ind.Pos.x, ind.Pos.z, 20f, baseDeg - 45f, baseDeg + 45f, pizzaColor));
            _map.Regions.Add(MinimapRegion.Sector(ind.Pos.x, ind.Pos.z, 20f, baseDeg + 135f, baseDeg + 225f, pizzaColor));
            pizzaUuid = ind.Uuid;
        }

        AddDots(e => ReefOnFloor(spec, e),
                e => e.Uuid == pizzaUuid ? pizzaColor : ReefOrbColor.TryGetValue(e.MonsterId, out int oc) ? oc : -1,
                e => _safeStatus.TryGetValue(e.Uuid, out bool safe) ? (safe ? 1 : 3) : -1);
    }

    // Reef visibleEntities: arena Y only (nearest-centre band); model-Y fallback as in InSpecArena.
    private bool ReefOnFloor(MinimapArenaSpec spec, McEnt e) =>
        _hasLocalPos && spec.YIn(_localPos.y) ? spec.YIn(e.Pos.y) : SameFloor(e.Pos.y);
}
