using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.RaidManager;

// Static buff → callout tables for the "Mechanic Callouts" experiment, ported from the third-party
// resonance-logs-cn minimap overlay (src/routes/minimap-overlay/scenes/<scene>/mechanics.ts + the scene gating in
// src-tauri/src/live/projections/minimap/scenes/*.rs). Labels are that project's en-US strings
// (src/lib/i18n/messages/en-US.ts, `minimap.*`). THIS file holds only the rows that are purely "this buff is ON this
// entity ⇒ <label>". Everything derived from monster entities / buff casters / PlayEffect ids / skill casts lives in
// MechanicCalloutTracker.Rules*.cs (keyed by SceneKind); what still can't be ported is listed in the per-scene notes.
//
// Scene ids are the game-table SceneTable ids (= Bokura.Table.ITable.CurrentSceneId), verified against
// Resources/release_3.7/tables/SceneTable.json (6615 is newer than our 3.7 table dump but is what upstream gates on).
internal static class MechanicCalloutData
{
    // How rows sharing a buff merge (mirrors each upstream scene's row key — same key ⇒ targets merge into one row).
    internal enum KeyMode
    {
        ByLayer,     // `<baseId>:<layer>`  — upstream `callout:<baseId>:<layer>` (the common case)
        ByBase,      // `<baseId>`          — upstream `phase:<baseId>` / `dual:<baseId>`
        PerTarget,   // one row per player  — upstream `wheel:<k>:<target>` / `tina:buff:841509:<target>`
        PerCreate,   // one row per application (createTime) — upstream `sticky:<baseId>:<createTimeMs>`
        PerSource,   // one row per caster (FireUuid) — upstream `matrixCallout:<source>`
    }

    internal sealed record CalloutDef(string Group, string Label, int Color)
    {
        public KeyMode Key          { get; init; } = KeyMode.ByLayer;
        public bool    AppendLayer  { get; init; }        // label gets " x<layer>" (upstream 829324)
        public int     RequiresBuff { get; init; }        // row only when the SAME player also carries this buff id
        public string? RowKey       { get; init; }        // explicit merge key (overrides Key)
        public long    DefaultDurationMs { get; init; }   // countdown to use when the live Duration is 0
        public int     Order        { get; init; }        // table position — stable group/row ordering (set below)
        // false = a FLOOR-targeted danger mechanic (e.g. raid Edge-Mid / Corner Explosion): like the Phase Mapping
        // danger rows, the carrier is NOT coloured/listed as a "target" (TableRows honours this, same colourEntity=false
        // path as MechanicCalloutTracker.PhaseMapping.cs). The row stays as an informational "<label>" alert.
        public bool    ColourEntity { get; init; } = true;
    }

    // 12-slot palette, same order as upstream colors.ts so a colorSlot index means the same colour as there — EXCEPT
    // NO BLUE and NO WHITE: the minimap draws teammates as sky-blue dots and the local player white, so a blue/cyan or
    // white mechanic reads as a player. Upstream 4 cyan 06B6D4 → magenta, 7 blue 3B82F6 → brown (same indices, so
    // data rows keep working; 7 is brown not magenta because Giant pairs it with pink 6). 2 violet / 10 teal borderline.
    // APPROVED EXCEPTION (user, 2026-10-08): Ice and Water mechanics keep upstream blue / cyan — the element hint
    // matters more there than the teammate-dot confusion. They get their OWN slots appended after the 12 (IceSlot /
    // WaterSlot) so every other user of 4 / 7 stays magenta / brown; never point a non-Ice/Water mechanic at them.
    internal const int BaseSlots = 12, IceSlot = 12, WaterSlot = 13;
    internal static readonly ColorRgba[] Palette =
    {
        Rgb(0xFACC15), // 0 yellow
        Rgb(0x22C55E), // 1 green
        Rgb(0x8B5CF6), // 2 violet
        Rgb(0xEF4444), // 3 red
        Rgb(0xD946EF), // 4 magenta (was cyan 06B6D4 — no blue)
        Rgb(0xF97316), // 5 orange
        Rgb(0xEC4899), // 6 pink
        Rgb(0xB45309), // 7 brown  (was blue 3B82F6 — no blue)
        Rgb(0x84CC16), // 8 lime
        Rgb(0xA855F7), // 9 purple
        Rgb(0x14B8A6), // 10 teal
        Rgb(0xF59E0B), // 11 amber
        Rgb(0x3B82F6), // 12 IceSlot   blue (upstream 7) — Ice exception
        Rgb(0x06B6D4), // 13 WaterSlot cyan (upstream 4) — Water exception
    };

    // 0xRRGGBB → opaque ColorRgba (explicit floats; avoids depending on FromHex's byte order).
    private static ColorRgba Rgb(uint hex) =>
        new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, 1f);

    // In-range slots (incl. the Ice/Water exception slots) map directly; anything else wraps over the 12 BASE slots
    // only, so a cycling/out-of-range index can never land on the blue/cyan exception colours by accident.
    internal static ColorRgba SlotColor(int slot)
    {
        if ((uint)slot < (uint)Palette.Length) return Palette[slot];
        return Palette[((slot % BaseSlots) + BaseSlots) % BaseSlots];
    }

    // ── S3 raid "Forgotten Dreamwild" (s3-raid) ──────────────────────────────────────────────────────────────
    // Rules: Preset Return with its floor cell + the "not in ring arena" gate (MechanicCalloutTracker.Minimap.cs, using
    // the raid arena data in MechanicMinimap.View.cs).
    // Also rules: pinball cast + ball, electromagnetic ring sequence (MechanicCalloutTracker.Rules*.cs).
    // Phase Mapping 829327-829332 = DANGER tiles: one merged red "Avoid: …" row (MechanicCalloutTracker.PhaseMapping.cs).
    // Edge-Mid / Corner Explosion 829214/829215 = DANGER tiles too (floor-targeted, ColourEntity=false; Minimap.cs Style 3).
    private const string RaidPhase   = "Phase";
    private const string RaidEmp     = "Electromagnetic Pulse";
    private const string RaidShareMg = "Share / Decay";
    private const string RaidShare   = "Share / Decay / Spread";
    private const string RaidMShare  = "Mirage Share / Decay / Spread";
    private const string RaidKill    = "Execution Sentence";
    private const string RaidKillMg  = "Execution Sentence - Mirage";

    private static readonly (int Id, CalloutDef Def)[] Raid =
    {
        // Edge-Mid / Corner Explosion target the FLOOR, not a player (user correction 2026-10-07): the marked tiles
        // become no-go, like Phase Mapping (829327-332). ColourEntity=false → the carrier (a MONSTER — "Mirror Summon",
        // see Mechanic-Callouts.md) is never coloured/listed as a target; the tiles draw as Style-3 danger (Minimap.cs).
        (829214, new(RaidPhase, "Edge-Mid Explosion", 3) { Key = KeyMode.ByBase, ColourEntity = false }),
        (829215, new(RaidPhase, "Corner Explosion",   3) { Key = KeyMode.ByBase, ColourEntity = false }),
        (829104, new(RaidEmp, "A", 0)),
        (829105, new(RaidEmp, "B", 1)),
        (829106, new(RaidEmp, "C", 2)),
        (829115, new(RaidShareMg, "Share",        0)),
        (829116, new(RaidShareMg, "Mirage Share", 3)),
        // 829117 衰减 Decay / 829118 幻衰减 Mirage Decay (BuffTable) — kept as rows but NEVER observed in game. The real
        // later P3 Decay (the one dropped on the floor / dodgeable) is 829325 连结试炼 (row below, next to 829306).
        (829117, new(RaidShareMg, "Decay",        1)),
        (829118, new(RaidShareMg, "Mirage Decay", 4)),
        (829304, new(RaidShare, "Share",  0)),
        (829306, new(RaidShare, "Decay",  1)),
        // 829325 连结试炼 = the P3 floor Decay (15 s, 4 players, fire = boss): ~14 s after a Mirage Decay 829307 hits 0,
        // or on its own near ball rounds (user video 2026-10-08). Own row (ByLayer key = <baseId>:<layer>, never merges
        // with 829306); same palette/group as normal Decay, own label so the two are told apart. Hit offset 0 (default).
        (829325, new(RaidShare, "Decay (Linked Trial)", 1)),
        (829308, new(RaidShare, "Spread", 2)),
        (829305, new(RaidMShare, "Mirage Share",  3)),
        (829307, new(RaidMShare, "Mirage Decay",  4)),
        (829309, new(RaidMShare, "Mirage Spread", 5)),
        (829316, new("Divine Scale - Causal Jump", "Causal Jump Ricochet", 4)),
        (829217, new("Normal Target / Decay Target", "Normal Target", 1)),
        (829245, new("Normal Target / Decay Target", "Decay Target",  2)),
        (829226, new("Hit Order", "Mark 1", 0)),
        (829227, new("Hit Order", "Mark 2", 1)),
        (829228, new("Hit Order", "Mark 3", 2)),
        (829323, new(RaidKill, "Divine Trick - Execution Sentence", 0)),
        (829324, new(RaidKill, "Execution Sentence", 1) { AppendLayer = true }),
        (829326, new(RaidKillMg, "Divine Trick - Execution Sentence - Mirage", 2)),
    };

    // ── S3 Cursed Radiant Tomb (s3-cursed-tomb) ──────────────────────────────────────────────────────────────
    // Rules: blue tower activating (tower MONSTER buffs), charge clones (skill casts; the half-plane polygon is
    // minimap-only, the row label already says Left/Right).
    private static readonly (int Id, CalloutDef Def)[] CursedTomb =
    {
        (884129, new("Energy Pillar Target", "Energy Pillar",       5)),
        (884141, new("Energy Pillar Target", "Energy Pillar Short", 11)),
        (884162, new("Half-Field Charge Target", "Left Charge",              1)),
        (884163, new("Half-Field Charge Target", "Right Charge",             2)),
        (884168, new("Half-Field Charge Target", "Random Half-Field Charge", 6)),
        (884169, new("Puzzle Target", "Puzzle Piece 1", 8)),
        (884170, new("Puzzle Target", "Puzzle Piece 2", 9)),
    };

    // ── S3 Towering Ruin / "Giant Tower" (s3-giant-tower) ────────────────────────────────────────────────────
    // Rules: correct portal (monster 2106 present), gravity blast (boss skill 111103).
    private static readonly (int Id, CalloutDef Def)[] GiantTower =
    {
        (821076, new("Sticky Bomb", "Sticky Bomb Countdown", 5) { Key = KeyMode.PerCreate }),
    };

    // ── S3 Tina's Mindrealm (s3-tina-mindrealm) ──────────────────────────────────────────────────────────────
    // Rules: Wudi-Slash ORDER (841509 → first PlayEffect effect id, via McBuffEffectPatch), pizza waves (monsters
    // 300086/300089 present).
    private static readonly (int Id, CalloutDef Def)[] Tina =
    {
        (510571, new("Tina · Mechanic", "Heavy Wound",     3)),
        (841519, new("Tina · Mechanic", "Red-Light Bind",  6)),
    };

    // ── S3 Chaotic Sea-Ringed Reef (s3-sea-ringed-reef boss.ts + matrix.ts) ──────────────────────────────────
    // Rules: matrix callout 522602 (row coloured by the SOURCE matrix's rune buff), ice/sea wave axis + safe-zone
    // status, pizza colour. NOT PORTED: orb markers (minimap-only, upstream adds no row).
    private static readonly (int Id, CalloutDef Def)[] SeaReef =
    {
        (883602, new("Duet Color", "Duet - Ice",   IceSlot) { Key = KeyMode.ByBase }),
        (883603, new("Duet Color", "Duet - Water", WaterSlot) { Key = KeyMode.ByBase }),
    };

    // ── S4 Wasteland Court (s4-wasteland-court) ──────────────────────────────────────────────────────────────
    // Rules: Void-Mark pairing (884659 effect ids), resolve/penalty, swap orbs, near/far chain, Shadow phase,
    // energy-orb tracking (884641 with the energy-ball caster check + per-player colour).
    private static readonly (int Id, CalloutDef Def)[] WastelandCourt =
    {
        (884614, new("Wheels", "Fate Wheel · Stack",  7) { Key = KeyMode.PerTarget }),
        (884615, new("Wheels", "Woe Wheel · Spread",  3) { Key = KeyMode.PerTarget }),
        (884616, new("Wheels", "Despair Wheel",       2) { Key = KeyMode.PerTarget }),
    };

    internal enum SceneKind { CursedTomb, SeaReef, Raid, GiantTower, Tina, WastelandCourt }

    // A supported scene: its table rows plus the upstream backend filters (src-tauri .../minimap/scenes/*.rs):
    // `MechanicBuffs` = every buff id worth reading (table ids ∪ upstream mechanic_buff_ids — rules need e.g. the
    // tower/rune/chain buffs that sit on MONSTERS) and `Monsters` = upstream relevant_monster_ids (AttrId values of
    // the non-player entities to scan: bosses, towers, orbs, dummies...).
    internal sealed class SceneDef
    {
        public string Name = "";
        public SceneKind Kind;
        public Dictionary<int, CalloutDef> Buffs = new();
        public HashSet<int> MechanicBuffs = new();
        public HashSet<int> Monsters = new();
        // Minimap "boss" entities (upstream kind === "boss"; drawn even without a mechanic colour): MonsterTable
        // MonsterType 2 among the scene's monsters (Wasteland = upstream BOSS_MONSTER_IDS; not in our 3.7 tables).
        public HashSet<int> Bosses = new();
    }

    // sceneId → scene definition.
    internal static readonly Dictionary<int, SceneDef> Scenes = Build();

    private static Dictionary<int, SceneDef> Build()
    {
        var map = new Dictionary<int, SceneDef>();
        void Add(string name, SceneKind kind, int[] sceneIds, (int Id, CalloutDef Def)[] rows, int[] buffIds, int[] monsters,
                 int[] bosses)
        {
            var def = new SceneDef { Name = name, Kind = kind };
            for (int i = 0; i < rows.Length; i++) def.Buffs[rows[i].Id] = rows[i].Def with { Order = i };
            def.MechanicBuffs.UnionWith(def.Buffs.Keys);
            def.MechanicBuffs.UnionWith(buffIds);
            def.Monsters.UnionWith(monsters);
            def.Bosses.UnionWith(bosses);
            foreach (var s in sceneIds) map[s] = def;
        }
        Add("Cursed Radiant Tomb", SceneKind.CursedTomb, new[] { 6513, 6514, 6515 }, CursedTomb,
            new[] { 884101, 884102, 884103, 884104, 884106, 884122, 884129, 884141, 884162, 884163, 884166, 884168, 884169, 884170 },
            new[] { 33901, 33904, 33905, 33908, 33909, 33921, 33922 }, new[] { 33901 });
        Add("Sea-Ringed Reef", SceneKind.SeaReef, new[] { 6563, 6564, 6565 }, SeaReef,
            new[] { 883707, 883708, 883709, 883710, 883714, 883601, 883602, 883603, 883605, 883631, 522602, 883633, 883634 },
            new[] { 4601, 4603, 4604, 4605, 4639, 3340219, 3340220, 3340227, 3340228, 1000 }, new[] { 4601 });
        Add("Forgotten Dreamwild (raid)", SceneKind.Raid, new[] { 13021, 13022, 13023 }, Raid,
            new[] { 829104, 829105, 829106, 829115, 829116, 829117, 829118, 829214, 829215, 829217, 829226, 829227, 829228,
                    829245, 829304, 829305, 829306, 829307, 829308, 829309, 829314, 829316, 829318, 829323, 829324, 829326,
                    829327, 829328, 829329, 829330, 829331, 829332, 829372, 829373, 829374,
                    // P3 floor Decay 连结试炼 (row)
                    829325,
                    // boss "release" buffs = Share / Decay / Spread hit moment (no rows; Release.cs shows "NOW")
                    829310, 829311, 829312,
                    // NOT yet used (raid logs, data note only): 829238 death teleport to a living teammate (player) ·
                    //   829320 时停 time stop (player) · 829321 渐隐 fade ·
                    //   829354 / 829357 / 829360 prayer-fountain nightmare phase 1 / 2 / 3 (player) · 829364 jet ·
                    //   829371 invisible / ignore collision · 829377 cannon mark · 829301 airflow FX · 829303 cannon
                    //   init · 829313 boss init · 829319 "grow bigger!"
                    // floor-damage signals (Floor.cs; tile buffs + boss reset/regenerate, inferred)
                    829209, 829208, 829239, 829205, 829204, 829233, 829216, 829234, 829235, 829261 },
            // + 103201/103204 (invisible blink target points), 103202 (arena centre point), 103203 (Golem illusion P2),
            //   103304-103306 (Zone A markers) — in-game confirmed: Phase / Phase-Mapping buffs ride monsters like these.
            new[] { 103100, 103107, 103108, 103106, 103207, 103208, 103308, 103200, 103300, 103301, 103302, 103303, 103309,
                    103201, 103202, 103203, 103204, 103304, 103305, 103306,
                    103310, 103311, 10310062, 10310063, 10310064, 3543, 10330051 },
            new[] { 103100, 103107, 103108, 103200, 103207, 103208, 103300, 103301, 103302, 103308, 103309, 103310, 103311 });
        Add("Towering Ruin", SceneKind.GiantTower, new[] { 1150, 1151, 1152 }, GiantTower,
            new[] { 821076 }, new[] { 2106, 2107, 1150, 1151, 1152 }, new[] { 1150, 1151, 1152 });
        Add("Tina's Mindrealm", SceneKind.Tina, new[] { 1631, 1632, 1633 }, Tina,
            new[] { 510571, 841519, 841509 }, new[] { 33701, 300086, 300089 }, new[] { 33701 });
        Add("Wasteland Court", SceneKind.WastelandCourt, new[] { 6615 }, WastelandCourt,
            new[] { 884609, 884610, 884614, 884615, 884616, 884641, 884659, 884660, 884661, 884664 },
            new[] { 4701, 4711, 4702, 470131, 884606, 884607, 884640, 884642, 884668, 884669, 884670, 884671 }, new[] { 4701, 4711 });
        return map;
    }
}
