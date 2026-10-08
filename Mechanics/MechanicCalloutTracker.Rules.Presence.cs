using System;
using System.Collections.Generic;
using UnityEngine;

namespace Stellar.RaidManager;

// Rules driven by the PRESENCE of specific monster/dummy entities (by AttrId) — upstream rows that come from
// `snapshot.entities` rather than buffs. Positional upstream logic is kept only where its own row LABEL is text
// (wave axis, wave safe-zone ✓/✗ per teammate); pure minimap geometry (sectors, rings, orb markers) is dropped.
internal sealed partial class MechanicCalloutTracker
{
    // ── Towering Ruin: correct portal (2106) is up ───────────────────────────────────────────────────────────
    private void GiantPortalRow()
    {
        if (AnyMonster(2106)) Upsert("giant:portal:correct", "Portal", "Correct Portal", 6, 100, 0, 0);
    }

    // (Raid pinball ball rows: Rules.Pinball.cs — timed from the ball dummy's spawn, once per uuid.)

    // ── Tina: pizza waves (activePizzaDummies) ───────────────────────────────────────────────────────────────
    // Danger dummies spawn in batches of 8 within ms of each other; only the NEWEST batch (first-seen within 1 s of
    // the latest) counts, one untimed row per wave type present in it.
    private void TinaPizzaRows()
    {
        long newest = 0;
        foreach (var e in _ents.Values)
            if ((e.MonsterId == 300086 || e.MonsterId == 300089) && e.FirstSeenTick > newest) newest = e.FirstSeenTick;
        if (newest == 0) return;
        bool slow = false, fast = false;
        foreach (var e in _ents.Values)
        {
            if (newest - e.FirstSeenTick > 1000) continue;
            if (e.MonsterId == 300086) slow = true;
            else if (e.MonsterId == 300089) fast = true;
        }
        if (slow) Upsert("tina:pizza:300086", "Pizza Danger Zone", "Pizza · Slow Wave", 3, 101, 0, 0);
        if (fast) Upsert("tina:pizza:300089", "Pizza Danger Zone", "Pizza · Fast Wave", 5, 101, 0, 0);
    }

    // ── Sea-Ringed Reef: ice / sea wave safe lanes (addWaveSafeRegions) ──────────────────────────────────────
    // Each wave monster (ice 3340219, sea 3340220) is a 4-unit-wide SAFE lane along its facing axis. One row per
    // wave ("Ice Wave Safe: Vertical"), plus a status row listing every teammate (safe)/(out): both waves → only the
    // 4×4 crossing is safe; one wave → inside its lane. "Safe" = the whole player circle (r 0.25) inside the lane.
    // Positions are the client model positions (same frame for wave and player — only differences are used).
    private const float WaveHalfWidth = 2f, PlayerRadius = 0.25f;

    private void ReefWaveRows()
    {
        McEnt? ice = null, sea = null;
        foreach (var e in _ents.Values)
        {
            if (float.IsNaN(e.Facing) || !e.HasPos) continue;
            if (e.MonsterId == 3340219) ice = e;
            else if (e.MonsterId == 3340220) sea = e;
        }
        if (ice == null && sea == null) return;

        McEnt? vertical = null, horizontal = null;
        string? verticalLabel = null, horizontalLabel = null;
        foreach (var (wave, label, color, key) in new[] { (ice, "Ice Wave Safe", MechanicCalloutData.IceSlot, "ice"), (sea, "Sea Wave Safe", MechanicCalloutData.WaterSlot, "water") })
        {
            if (wave == null) continue;
            bool isVertical = IsVerticalFacing(wave.Facing);
            Upsert($"reef:wave:{key}:{wave.Uuid}", "Ice/Sea Wave Safe Zone",
                   $"{McText.T(label)}: {McText.T(isVertical ? "Vertical" : "Horizontal")}", color, 101, 0, 0);
            if (isVertical) { vertical = wave; verticalLabel = label; }
            else            { horizontal = wave; horizontalLabel = label; }
        }

        float inner = MathF.Max(0f, WaveHalfWidth - PlayerRadius);
        Func<Vector3, bool> safe;
        string key2, rowLabel;
        if (vertical != null && horizontal != null)
        {
            float cx = vertical.Pos.x, cz = horizontal.Pos.z;
            safe = p => MathF.Abs(p.x - cx) < inner && MathF.Abs(p.z - cz) < inner;
            key2 = $"reef:wave:cross:{vertical.Uuid}:{horizontal.Uuid}"; rowLabel = "Cross Intersection";
        }
        else
        {
            var w = vertical ?? horizontal!;
            bool v = vertical != null;
            float axis = v ? w.Pos.x : w.Pos.z;
            safe = p => MathF.Abs((v ? p.x : p.z) - axis) < inner;
            key2 = $"reef:wave:single:{w.Uuid}"; rowLabel = $"{McText.T((v ? verticalLabel : horizontalLabel)!)}: {McText.T("Single Wave Safe")}";
        }
        var a = Upsert(key2, "Ice/Sea Wave Safe Zone", rowLabel, 1, 101, 0, 0);
        foreach (var e in _ents.Values)
            // Dead team members are excluded (upstream filters !isDead).
            if (e.IsPlayer && e.HasPos && !e.IsDead && IsTeam(e.Uuid)) AddTarget(a, e.Uuid, safe(e.Pos) ? 1 : 0);
    }

    // axisFromFacing: whichever of 0°/180° (vertical) or 90°/270° (horizontal) the yaw is closer to.
    private static bool IsVerticalFacing(float facing)
    {
        float n = ((facing % 360f) + 360f) % 360f;
        float toVertical   = MathF.Min(n, MathF.Min(MathF.Abs(n - 180f), 360f - n));
        float toHorizontal = MathF.Min(MathF.Abs(n - 90f), MathF.Abs(n - 270f));
        return toVertical <= toHorizontal;
    }
}
