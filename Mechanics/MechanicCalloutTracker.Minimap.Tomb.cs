using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// Cursed Radiant Tomb minimap — port of scenes/s3-cursed-tomb/{arena.ts, index.ts, mechanics.ts}: outline + 3×3 floor
// grid, the charge-clone danger half (arena rect clipped by the clone's charge line), towers coloured by state, boss
// and clone markers. Callout targets (energy pillar / charge / puzzle) colour through their rows.
internal sealed partial class MechanicCalloutTracker
{
    private const int TombBossId = 33901;
    private static readonly HashSet<int> TombLeftClones  = new() { 33908, 33921 };
    private static readonly HashSet<int> TombRightClones = new() { 33909, 33922 };

    private void BuildTombMap()
    {
        ClearMap();
        var spec = MinimapArenas.Tomb;
        spec.Apply(_map);

        // Charge clones (addChargeCloneRegions): each charge cast in the last 10 s (−0.5 s tolerance) paints the
        // arena half on the clone's left/right of its charge line (anchored at the caster's position + facing AT
        // CAST TIME) red 3, and marks the casting clone danger-red.
        // Deduped, newest-per-clone halves + diagnostics: Minimap.Tomb.Charge.cs.
        var dangerCasters = new HashSet<long>();
        foreach (var h in ActiveChargeHalves(spec))
        {
            dangerCasters.Add(h.Cast.Caster);
            _map.Regions.Add(MinimapRegion.Polygon(h.Poly, 3));
        }

        AddDots(e => InSpecArena(spec, e), e =>
        {
            if (e.MonsterId == TombBossId) return 3;
            if (TombTowers.Contains(e.MonsterId)) return TombTowerColor(e.Uuid);
            if (TombLeftClones.Contains(e.MonsterId) || TombRightClones.Contains(e.MonsterId))
                return dangerCasters.Contains(e.Uuid) ? 3 : 5;
            return -1;
        });
    }

    // Tower state (towerState): gold complete (884103) → gold 0, blue complete (884102) → cyan, else activating → blue.
    // Upstream cyan / blue kept: a tower is an object, not a player highlight (palette rule, MechanicCallouts.Data.cs).
    private int TombTowerColor(long tower) =>
        HasBuff(tower, 884103) ? 0 : HasBuff(tower, 884102) ? MechanicCalloutData.CyanSlot : MechanicCalloutData.BlueSlot;

    // clipArenaRectToHand: the arena's WORLD rect clipped to one side of the line through (ax, az) along the facing
    // (forward = (sin f, cos f)). cross = fwd.x·(p.z − a.z) − fwd.z·(p.x − a.x); left keeps cross ≥ −ε, right ≤ ε.
    private static float[]? ChargeHalfPoly(MinimapArenaSpec s, float ax, float az, float facingDeg, bool left)
    {
        float rad = facingDeg * MathF.PI / 180f, fx = MathF.Sin(rad), fz = MathF.Cos(rad);
        const float eps = 0.0001f;
        float Side(float x, float z) { float cross = fx * (z - az) - fz * (x - ax); return left ? cross + eps : eps - cross; }

        var rect = new List<(float X, float Z)>
        {
            (s.Cx - s.HalfX, s.Cz - s.HalfZ), (s.Cx + s.HalfX, s.Cz - s.HalfZ),
            (s.Cx + s.HalfX, s.Cz + s.HalfZ), (s.Cx - s.HalfX, s.Cz + s.HalfZ),
        };
        var outPts = new List<float>();
        for (int i = 0; i < rect.Count; i++)
        {
            var p = rect[i]; var q = rect[(i + 1) % rect.Count];
            float sp = Side(p.X, p.Z), sq = Side(q.X, q.Z);
            if (sp >= 0f) { outPts.Add(p.X); outPts.Add(p.Z); }
            if ((sp >= 0f) != (sq >= 0f))
            {
                float t = sp / (sp - sq);
                outPts.Add(p.X + (q.X - p.X) * t); outPts.Add(p.Z + (q.Z - p.Z) * t);
            }
        }
        return outPts.Count >= 6 ? outPts.ToArray() : null;
    }
}
