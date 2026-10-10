namespace Stellar.RaidManager;

// Tina's Mindrealm minimap — port of scenes/s3-tina-mindrealm/{arena.ts, index.ts, mechanics.ts}: the cabinet-ring
// arena, pizza danger sectors and the boss marker. Wudi-Slash marks colour their players by slash order through the
// rows (AddTarget → row colour = order % 12); Heavy Wound / Red-Light Bind likewise.
internal sealed partial class MechanicCalloutTracker
{
    private const int TinaBoss = 33701, TinaPizzaSlow = 300086, TinaPizzaFast = 300089;

    private void BuildTinaMap()
    {
        ClearMap();
        var spec = MinimapArenas.Tina;
        spec.Apply(_map);

        // Pizza (addPizzaDangerRegions): only the NEWEST dummy batch (first seen within 1 s of the latest); each
        // dummy = a 45° sector (facing ± 22.5°) out to the cabinet ring (r 16); slow wave red 3, fast wave orange 5.
        long newest = 0;
        foreach (var e in _ents.Values)
            if ((e.MonsterId == TinaPizzaSlow || e.MonsterId == TinaPizzaFast) && e.FirstSeenTick > newest) newest = e.FirstSeenTick;
        // Facing = rendered model rotation (AttrDir read identical for every dummy in game → one overlapped slice).
        foreach (var e in _ents.Values)
        {
            if (e.MonsterId != TinaPizzaSlow && e.MonsterId != TinaPizzaFast) continue;
            if (newest - e.FirstSeenTick > 1000 || !e.HasPos || float.IsNaN(e.Facing)) continue;
            int color = e.MonsterId == TinaPizzaSlow ? 3 : 5;
            _map.Regions.Add(MinimapRegion.Sector(e.Pos.x, e.Pos.z, 16f, e.Facing - 22.5f, e.Facing + 22.5f, color));
        }

        AddDots(e => InSpecArena(spec, e), e =>
            e.MonsterId == TinaBoss ? 3
            : (e.MonsterId == TinaPizzaSlow || e.MonsterId == TinaPizzaFast) && newest - e.FirstSeenTick <= 1000
                ? (e.MonsterId == TinaPizzaSlow ? 3 : 5) : -1);
    }
}
