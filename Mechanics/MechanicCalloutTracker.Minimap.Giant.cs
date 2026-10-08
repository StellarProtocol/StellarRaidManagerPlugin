namespace Stellar.RaidManager;

// Towering Ruin minimap — port of scenes/s3-giant-tower/{arena.ts, index.ts, mechanics.ts}: arena outline + portal ring,
// no regions (upstream `regions: []`). The correct portal (2106) is pink 6, the other portal (2107) brown 7 (upstream blue); the boss
// (Kartgriff) draws as the boss marker; Sticky Bomb targets colour through their rows.
internal sealed partial class MechanicCalloutTracker
{
    private void BuildGiantMap()
    {
        ClearMap();
        var spec = MinimapArenas.Giant;
        spec.Apply(_map);
        AddDots(e => InSpecArena(spec, e), e => e.MonsterId == 2106 ? 6 : e.MonsterId == 2107 ? 7 : -1);
    }
}
