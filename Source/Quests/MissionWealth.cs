using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    internal static class MissionWealth
    {
        internal static float Total(UniverseManager universe)
        {
            var counted = new HashSet<Thing>();
            float total = 0;
            // Count each actual map once: WealthWatcher totals already include child pocket maps.
            foreach (Map map in Find.Maps)
            {
                var playerCells = new HashSet<IntVec3>(ShipRegistry.AllShipsOnMap(map)
                    .Where(s => s.Faction == Faction.OfPlayer).SelectMany(s => s.HullCells));
                bool colony = map.IsPlayerHome && !ShipRegistry.AllShipsOnMap(map).Any(s => s.Faction != Faction.OfPlayer);
                IEnumerable<Thing> things = universe.FrameOf(map) != null
                    ? universe.ShipStructure.For(map).Things : map.listerThings.AllThings;
                foreach (Thing thing in things)
                {
                    bool own = thing.Faction == Faction.OfPlayer;
                    if (thing is Pawn) { if (own) total += Value(thing, counted); continue; }
                    if (thing is Building) { if (own) total += Value(thing, counted); continue; }
                    if (thing.def.category == ThingCategory.Item && (colony || playerCells.Contains(thing.Position)) && !thing.Position.Fogged(map))
                        total += Value(thing, counted);
                }
                IEnumerable<IntVec3> floors = colony ? map.AllCells : playerCells;
                foreach (IntVec3 c in floors)
                {
                    if (c.Fogged(map)) continue;
                    total += c.GetTerrain(map).GetStatValueAbstract(StatDefOf.MarketValue);
                    TerrainDef foundation = map.terrainGrid.FoundationAt(c);
                    if (foundation != null) total += foundation.GetStatValueAbstract(StatDefOf.MarketValue);
                }
            }
            foreach (Caravan caravan in Find.WorldObjects.Caravans.Where(c => c.Faction == Faction.OfPlayer))
                foreach (Pawn pawn in caravan.PawnsListForReading) total += Value(pawn, counted);
            foreach (Gravship ship in Find.WorldObjects.AllWorldObjects.OfType<Gravship>().Where(s => s.Faction == Faction.OfPlayer))
            {
                foreach (Thing thing in ship.Things) total += Value(thing, counted);
                foreach (Pawn pawn in ship.Pawns) total += Value(pawn, counted);
                // During landing the same records may already point at live map entities.
                if (!ship.Things.Any(t => t.Spawned))
                {
                    total += ship.Terrains.Values.Sum(t => t.GetStatValueAbstract(StatDefOf.MarketValue));
                    total += ship.Foundations.Values.Where(t => t != null).Sum(t => t.GetStatValueAbstract(StatDefOf.MarketValue));
                }
            }
            foreach (TravellingTransporters transport in Find.WorldObjects.AllWorldObjects.OfType<TravellingTransporters>()
                .Where(t => t.Faction == Faction.OfPlayer)) total += HeldValue(transport, counted);
            return total;
        }

        private static float Value(Thing thing, HashSet<Thing> counted, bool includeContents = true)
        {
            if (thing == null || !counted.Add(thing)) return 0;
            float value = 0;
            if (thing is Pawn pawn)
            {
                if (pawn.IsQuestLodger() || pawn.Faction != Faction.OfPlayer) return 0;
                value = pawn.MarketValue * (pawn.IsSlave ? .75f : 1f);
            }
            else if (thing is Building)
            {
                if (thing.Faction != Faction.OfPlayer) return 0;
                value = thing.GetStatValue(StatDefOf.MarketValueIgnoreHp);
            }
            else if (thing.def.category == ThingCategory.Item) value = thing.MarketValue * thing.stackCount;
            if (includeContents)
            {
                var holders = new List<IThingHolder>();
                ThingOwnerUtility.AppendThingHoldersFromThings(holders, new[] { thing });
                foreach (var holder in holders) value += HeldValue(holder, counted);
            }
            return value;
        }
        private static float HeldValue(IThingHolder holder, HashSet<Thing> counted)
        {
            var things = new List<Thing>();
            ThingOwnerUtility.GetAllThingsRecursively(holder, things, allowUnreal: false, passCheck: WealthWatcher.WealthItemsFilter);
            return things.Sum(t => Value(t, counted, includeContents: false));
        }
    }
}
