using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimExodus.Maps;
using RimWorld;
using Verse;
using CommonDocument = RimExodus.Maps.MapPrefabDocument;

namespace DeltaV
{
    /// <summary>Delta-V policies around REF capture and placement; serialization belongs to REF.</summary>
    internal static class SharedMapBackend
    {
        internal const string Owner = "RimExodus.DeltaV";

        internal static MapPrefabDocument Export(Map map, string name, IEnumerable<IntVec3> cells, out string path)
        {
            var selected = new HashSet<IntVec3>(cells);
            var growerCells = new HashSet<IntVec3>();
            foreach (Building_PlantGrower grower in map.listerThings.AllThings.OfType<Building_PlantGrower>())
            {
                if (!selected.Contains(grower.Position)) continue;
                foreach (IntVec3 cell in grower.OccupiedRect())
                    if (selected.Contains(cell)) growerCells.Add(cell);
            }

            var plants = map.listerThings.AllThings.OfType<Plant>()
                .Where(plant => growerCells.Contains(plant.Position)).Cast<Thing>().ToHashSet();
            var plantCells = plants.Select(plant => plant.Position).ToHashSet();
            var blights = map.listerThings.AllThings.OfType<Blight>()
                .Where(blight => plantCells.Contains(blight.Position)).Cast<Thing>().ToHashSet();

            CommonDocument shared = MapPrefabs.Capture(map, new CellSelection(selected), name,
                include: thing => ShouldExportThing(thing, plants, blights),
                order: OrderExportThings);
            string target = Path.Combine(MapPrefab.PrefabDirectory, name + ".xml");
            path = ExportFileService.Write("map prefab", target, shared.Save, UniverseManager.MapTag(map));
            return shared;
        }

        internal static bool ShouldExportThing(Thing thing, ISet<Thing> plants, ISet<Thing> blights)
        {
            return !CompShipPlanner.IsAuthoringMarker(thing)
                && (thing.def.category == ThingCategory.Building || thing.def.category == ThingCategory.Item
                    || plants.Contains(thing) || blights.Contains(thing));
        }

        internal static IOrderedEnumerable<Thing> OrderExportThings(IEnumerable<Thing> things)
        {
            return things.OrderBy(thing => thing.def.building?.isAttachment == true
                    || thing.TryGetComp<CompSpaceWeaponAttachment>() != null ? 1 : 0)
                .ThenBy(thing => thing is Blight ? 2 : thing is Plant ? 1 : 0)
                .ThenByDescending(thing => thing.def.category == ThingCategory.Building)
                .ThenByDescending(thing => thing.TryGetComp<CompDeltaVPilotConsole>()?.isMain == true)
                .ThenByDescending(thing => thing.def.shortHash);
        }

        internal static List<Thing> Spawn(MapPrefabDocument doc, Map map, IntVec3 position, Faction faction,
            int ownerId, Rot4 rot, UniverseManager universe, bool blankSpace, bool expandMarkers)
        {
            PrefabPlacementResult result = Place(doc, map, position, faction, ownerId, rot, universe, blankSpace, expandMarkers);
            if (!result.Success) throw new InvalidDataException(string.Join("\n", result.Errors));
            return result.Things;
        }

        internal static PrefabPlacementResult Place(MapPrefabDocument doc, Map map, IntVec3 position, Faction faction,
            int ownerId, Rot4 rot, UniverseManager universe, bool blankSpace, bool expandMarkers)
        {
            var runtime = universe ?? Current.Game?.GetComponent<UniverseManager>();
            var structure = runtime?.ShipStructure;
            var perf = runtime?.Performance;
            long start = perf?.Start() ?? 0;
            var workBefore = start != 0 ? structure.Topology.WorkOf(map) : default;
            long claimElapsed = 0;
            long foundationsElapsed = 0;
            long finalizeElapsed = 0;
            IDisposable changes = null;
            bool success = false;

            void FinishChanges()
            {
                var scope = changes;
                changes = null;
                scope?.Dispose();
            }

            var options = new PrefabPlacement
            {
                Faction = faction, OmitSpace = blankSpace,
                FoundationsPlaced = (target, first) =>
                {
                    FinishChanges();
                    if (start != 0) foundationsElapsed = perf.Elapsed(start);
                    if (ownerId > 0 && first.IsValid)
                    {
                        long claimStart = perf?.Start() ?? 0;
                        using (structure?.BeginChanges())
                            SubstructureOwnership.For(target).ClaimConnectedDomain(first, ownerId);
                        if (claimStart != 0) claimElapsed = perf.Elapsed(claimStart);
                    }
                    changes = structure?.BeginChanges();
                },
                BeforeSpawn = (thing, entry, root, rotation) =>
                {
                    if (thing.TryGetComp<CompDeltaVPilotConsole>() is { } console)
                    { console.shipId = -1; console.isMain = false; }
                    foreach (CompSpaceWeapon weapon in CompSpaceWeapon.On(thing as ThingWithComps))
                    {
                        weapon.ResetPrefabIdentity();
                        weapon.Component<CompSpaceWeaponTraverse>()?.RotateForPrefab(rotation);
                    }
                    if (thing is Building_DockingPort port)
                        port.RebasePrefabLocalState(map, thing.Position - entry.pos, root, rotation);
                },
                PlaceThing = (thing, target, cell, facing) =>
                {
                    if (thing.def.GetModExtension<DefModExtension_SpaceMissileAmmo>() != null)
                    {
                        CompMissileMagazineFacility magazine = cell.GetThingList(target)
                            .Where(t => t.OccupiedRect().Contains(cell)).Select(t => t.TryGetComp<CompMissileMagazineFacility>())
                            .FirstOrDefault(c => c != null);
                        if (magazine != null)
                        {
                            while (thing.stackCount > 0 && magazine.TryStore(thing.def)) thing.stackCount--;
                            if (thing.stackCount == 0) { thing.Destroy(DestroyMode.Vanish); return null; }
                            return GenPlace.TryPlaceThing(thing, cell, target, ThingPlaceMode.Near, out Thing overflow) ? overflow : null;
                        }
                    }
                    return MapContentSpawner.SpawnExisting(thing, target, cell, facing, true);
                },
                Completed = (target, things, root, rotation) =>
                {
                    FinishChanges();
                    long finalizeStart = perf?.Start() ?? 0;
                    try
                    {
                        MapContentSpawner.RelinkGravshipComponents(target);
                        if (expandMarkers)
                        {
                            var context = new PrefabSpawnContext(target, faction, ownerId, root, rotation, runtime);
                            if (runtime != null) runtime.MapStates.ExpandPrefabMarkers(context, things);
                            else foreach (Thing thing in things.ToList())
                            {
                                CompPrefabMarker marker = thing.TryGetComp<CompPrefabMarker>();
                                if (marker?.IsExternalSpaceObjectMarker == false) marker.Expand(context);
                            }
                            things.RemoveAll(t => t.Destroyed);
                        }
                    }
                    finally
                    {
                        if (finalizeStart != 0) finalizeElapsed = perf.Elapsed(finalizeStart);
                    }
                }
            };
            try
            {
                changes = structure?.BeginChanges();
                var result = MapPrefabs.Rebuild(doc, map, position, rot, options);
                success = result.Success;
                return result;
            }
            finally
            {
                try { FinishChanges(); }
                finally
                {
                    if (start != 0)
                        perf.RecordPrefabPlacement(doc?.name, map, success, perf.Elapsed(start), foundationsElapsed, claimElapsed,
                            finalizeElapsed, workBefore, structure.Topology.WorkOf(map));
                }
            }
        }

    }
}
