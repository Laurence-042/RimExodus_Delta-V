using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
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
            CommonDocument shared = MapPrefabs.Capture(map, new CellSelection(cells), name,
                include: t => (t.def.category == ThingCategory.Building || t.def.category == ThingCategory.Item)
                    && !CompShipPlanner.IsAuthoringMarker(t),
                order: things => things.OrderBy(t => t.def.building?.isAttachment == true
                    || t.TryGetComp<CompSpaceWeaponAttachment>() != null ? 1 : 0)
                    .ThenByDescending(t => t.def.category == ThingCategory.Building)
                    .ThenByDescending(t => t.TryGetComp<CompDeltaVPilotConsole>()?.isMain == true)
                    .ThenByDescending(t => t.def.shortHash));
            path = Path.GetFullPath(Path.Combine(MapPrefab.PrefabDirectory, name + ".xml"));
            shared.Save(path);
            return shared;
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
                        if (!expandMarkers) return;
                        var context = new PrefabSpawnContext(target, faction, ownerId, root, rotation, universe);
                        foreach (Thing thing in things.ToList()) thing.TryGetComp<CompPrefabMarker>()?.Expand(context);
                        things.RemoveAll(t => t.Destroyed);
                    }
                    finally
                    {
                        if (finalizeStart != 0) finalizeElapsed = perf.Elapsed(finalizeStart);
                    }
                }
            };
            try
            {
                RemoveMissingStorageFilterDefs(doc);
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

        // Exported shelves can retain filter entries from mods no longer loaded.
        // Those entries have no possible meaning in this game and Scribe otherwise
        // emits one cross-reference error per entry on every prefab reconstruction.
        private static void RemoveMissingStorageFilterDefs(MapPrefabDocument doc)
        {
            foreach (MapPrefabThingEntry entry in doc.thingEntries)
            {
                if (entry.def?.thingClass == null
                    || !typeof(Building_Storage).IsAssignableFrom(entry.def.thingClass)
                    || string.IsNullOrEmpty(entry.stateXml)) continue;
                var xml = new XmlDocument();
                xml.LoadXml(entry.stateXml);
                XmlNodeList names = xml.DocumentElement.SelectNodes(
                    "settings/filter/allowedDefs/li|settings/filter/disallowedDefs/li");
                bool changed = false;
                foreach (XmlNode name in names.Cast<XmlNode>().ToList())
                    if (DefDatabase<ThingDef>.GetNamedSilentFail(name.InnerText) == null)
                    {
                        name.ParentNode.RemoveChild(name);
                        changed = true;
                    }
                if (changed) entry.stateXml = xml.DocumentElement.OuterXml;
            }
        }
    }
}
