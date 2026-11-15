using System;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static bool StructureFoundationWithOdyssey(TerrainDef __instance, ref bool __result)
    { __result = __instance.HasTag("Substructure"); return false; }

    static void CheckStructureSelection(UniverseManager universe, Faction player, Faction enemy)
    {
        // Supply DLC availability without running Unity's ModsConfig initialization.
        var harmony = new Harmony("DeltaV.StructureSelectionRegression");
        harmony.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
            prefix: new HarmonyMethod(typeof(Program), nameof(StructureFoundationWithOdyssey)));
        TerrainDef oldSpace = TerrainDefOf.Space;
        var space = (TerrainDef)Raw(typeof(TerrainDef));
        space.defName = "regressionSpace";
        TerrainDefOf.Space = space;
        var foundation = (TerrainDef)Raw(typeof(TerrainDef));
        foundation.defName = "regressionFoundation";
        foundation.isFoundation = true;
        foundation.tags = new List<string> { "Substructure" };
        Map map = TestMap(2600), smallMap = TestMap(2601), largeMap = TestMap(2602);
        var frame = new MapSpaceObject(2600, map, new SpacePose(Vector3.forward, 0));
        universe.RegisterObject(frame);
        var ownership = SubstructureOwnership.TryGet(map);
        var owners = new int[map.cellIndices.NumGridCells];
        Set(ownership, "ownerGrid", owners);
        void Deck(int owner, params IntVec3[] cells)
        {
            foreach (var cell in cells)
            {
                int index = map.cellIndices.CellToIndex(cell);
                map.terrainGrid.foundationGrid[index] = foundation;
                owners[index] = owner;
                universe.NotifyFoundationChanged(map, cell);
            }
            SubstructureOwnership.BumpStructureVersion();
        }
        try
        {
            var console = AddTestConsole(map, enemy, 2610);
            var main = new HashSet<IntVec3> { console.parent.Position };
            var debris = new HashSet<IntVec3> { new IntVec3(3, 0, 1), new IntVec3(3, 0, 2), new IntVec3(3, 0, 3) };
            Deck(2610, main.Concat(debris).ToArray());
            Vector2 centerBefore = console.Vessel.HullCenter;
            Check(centerBefore == SpaceTransform.BoundsCenterOfCells(ShipRegistry.ControlDomain(console.Ship))
                && centerBefore == console.Vessel.HullCenter, "hull center warm query uses the current connected domain");
            var bridge = new IntVec3(2, 0, 2);
            Deck(2610, bridge);
            Check(console.Vessel.HullCenter != centerBefore && console.Vessel.HullCenter
                == SpaceTransform.BoundsCenterOfCells(ShipRegistry.ControlDomain(console.Ship)),
                "structure-version change refreshes cached center after hull expansion");
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(bridge)] = null;
            universe.NotifyFoundationChanged(map, bridge);
            SubstructureOwnership.BumpStructureVersion();
            Check(console.Vessel.HullCenter == centerBefore, "severed foundation immediately restores main-domain center");
            ShipRegistry.Forget(map);
            Check(console.Vessel.HullCenter == centerBefore, "map forget rebuilds domain and center together");
            ShipRegistry.ClearCaches();
            Check(console.Vessel.HullCenter == centerBefore, "new-game cache reset rebuilds the same center");
            var shipId = new DriveSubject(0, 2610);
            ShipStatusPanel.SelectStructure(map, shipId);
            var followMain = new ShipFlightTransitionService.SplitSelection(map, shipId);
            followMain.Transferred(debris, largeMap, 2611, new HashSet<Building>());
            followMain.Transferred(main, smallMap, 2610, new HashSet<Building>());
            Check(followMain.Winner() == (smallMap, shipId),
                "selected enemy follows smaller primary-console side without faction or pilot gating");

            var stationCells = new[] { new IntVec3(0, 0, 0), new IntVec3(0, 0, 1) };
            Deck(2620, stationCells);
            var stationId = new DriveSubject(0, 2620);
            var station = new ShipFlightTransitionService.SplitSelection(map, stationId);
            station.Transferred(main, smallMap, 2610, new HashSet<Building>());
            Check(station.Winner() == (map, stationId), "another vessel leaving does not steal the station selection");

            console.isMain = false;
            SubstructureOwnership.BumpStructureVersion();
            ShipStatusPanel.SelectStructure(map, shipId);
            Check(ShipStatusPanel.TryGetSelectedStructure(map, out var selected) && selected == shipId
                && !ShipStatusPanel.TryGetSelectedShip(map, out _) && !universe.Pilot.SelectedShipOnMap(map).IsValid,
                "losing primary console preserves structure selection without redirecting ship shortcuts");
            var largest = new ShipFlightTransitionService.SplitSelection(map, shipId);
            largest.Transferred(main, smallMap, 2630, new HashSet<Building>());
            largest.Transferred(debris, largeMap, 2631, new HashSet<Building>());
            Check(largest.Winner() == (largeMap, new DriveSubject(0, 2631)),
                "console-less selection follows largest fragment and its newly assigned owner");
            var tie = new ShipFlightTransitionService.SplitSelection(map, stationId);
            tie.Transferred(new HashSet<IntVec3> { stationCells[0] }, smallMap, 2632, new HashSet<Building>());
            Check(tie.Winner() == (map, stationId), "equal-size fragments keep the source map");

            Deck(0, new IntVec3(4, 0, 4));
            ShipStatusPanel.SelectStructure(map, default);
            Check(ShipStatusPanel.TryGetSelectedStructure(map, out selected) && selected.key == 0
                && ShipRegistry.StructuresOnMap(map).Contains(default(DriveSubject)),
                "unassigned foundations can be selected without inventing a persistent owner");

            for (int i = 0; i < map.terrainGrid.topGrid.Length; i++) map.terrainGrid.topGrid[i] = space;
            map.listerThings.AllThings.Clear();
            ownership.ClearMapSupportIndex();
            Check(SpaceLifecycleService.HasMapSupport(frame)
                && ownership.SupportCellCount == map.terrainGrid.foundationGrid.Count(terrain => terrain == foundation),
                "foundation alone supports an otherwise all-space map");
            Array.Clear(map.terrainGrid.foundationGrid, 0, map.terrainGrid.foundationGrid.Length);
            foreach (IntVec3 cell in map.AllCells) ownership.UpdateSupportCell(cell);
            var filth = (Thing)Raw(typeof(Thing));
            filth.def = (ThingDef)Raw(typeof(ThingDef)); filth.def.category = ThingCategory.Filth;
            map.listerThings.AllThings.Add(filth);
            Check(!SpaceLifecycleService.HasMapSupport(frame), "filth cannot keep an unsupported map alive");
            int queued = Actions(universe).Count;
            universe.Lifecycle.QueueStructureCheck(map);
            universe.Lifecycle.QueueStructureCheck(map);
            Check(Actions(universe).Count == queued + 1, "structure-loss events coalesce into one frame action");
            map.terrainGrid.topGrid[0] = (TerrainDef)Raw(typeof(TerrainDef));
            SubstructureOwnership.BumpStructureVersion(map);
            Check(!SpaceLifecycleService.HasMapSupport(frame),
                "a generic structure version change does not rescan the support grid");
            ShipStructureChanges.Notify(map, terrainCell: IntVec3.Zero);
            ShipStructureChanges.Notify(map, terrainCell: IntVec3.Zero);
            Check(SpaceLifecycleService.HasMapSupport(frame), "non-space terrain supports a foundation-less map");
            Call(universe, "RunFrameActions");
            Check(universe.ObjectById(frame.objectId) == frame,
                "deferred recycling rechecks final terrain and preserves restored support");
            map.terrainGrid.topGrid[0] = space;
            ShipStructureChanges.Notify(map, terrainCell: IntVec3.Zero);
            var rockDef = TestBuildingDef("StructureSupportRock");
            rockDef.building.isNaturalRock = true;
            var rock = AddTestBuilding(map, null, 2639, rockDef, new CompDeltaVPilotConsole(),
                (Mineable)Raw(typeof(Mineable)));
            ShipStructureChanges.Notify(map, building: rock);
            ShipStructureChanges.Notify(map, building: rock);
            Check(SpaceLifecycleService.HasMapSupport(frame) && ownership.SpawnedBuildingCount == 1
                && ownership.SupportCellCount == 0, "one indexed mineable keeps a pure Space map alive");
            Set(typeof(Thing), rock, "mapIndexOrState", (sbyte)-1);
            map.listerThings.AllThings.Remove(rock);
            ShipStructureChanges.Notify(map, building: rock);
            Check(!SpaceLifecycleService.HasMapSupport(frame), "despawning the final mineable immediately removes support");
            var temporaryTerrain = (TerrainDef[])AccessTools.Field(typeof(TerrainGrid), "tempGrid").GetValue(map.terrainGrid);
            temporaryTerrain[0] = (TerrainDef)Raw(typeof(TerrainDef));
            ShipStructureChanges.Notify(map, terrainCell: IntVec3.Zero);
            Check(ownership.SupportCellCount == 1, "temporary terrain counts once at the changed cell");
            temporaryTerrain[0] = null;
            map.terrainGrid.foundationGrid[0] = foundation;
            ShipStructureChanges.Notify(map, terrainCell: IntVec3.Zero);
            Check(ownership.SupportCellCount == 1, "replacing temporary terrain with foundation keeps one support cell");
            map.terrainGrid.foundationGrid[0] = null;
            owners[0] = SubstructureOwnership.Unassigned;
            Patch_TerrainGrid_RemoveGravshipTerrainUnsafe_Ownership.Postfix(map.terrainGrid, IntVec3.Zero);
            Check(ownership.SupportCellCount == 0, "unsafe foundation removal updates the index");
            map.terrainGrid.topGrid[0] = (TerrainDef)Raw(typeof(TerrainDef));
            ownership.ClearMapSupportIndex();
            Check(ownership.SupportCellCount == 1 && ownership.SpawnedBuildingCount == 0,
                "a released map index rebuilds both counts from the real map");
            map.terrainGrid.topGrid[0] = space;
            ShipStructureChanges.Notify(map, terrainCell: IntVec3.Zero);
            var hull = AddTestHull(map, player, 2640);
            ShipStructureChanges.Notify(map, building: (Building)hull.parent);
            Check(SpaceLifecycleService.HasMapSupport(frame), "shuttle remains supported without any terrain or foundation");
            UnspawnFixture(hull, map);
            ShipStructureChanges.Notify(map, building: (Building)hull.parent);
            frame.projectileRoleOverride = true;
            Check(SpaceLifecycleService.HasMapSupport(frame), "projectile role retains its own lifecycle");
        }
        finally
        {
            universe.objects.Remove(frame);
            Set(universe, "objectById", null);
            Set(universe, "objectByMap", null);
            universe.Lifecycle.Reset();
            ShipStatusPanel.ClearRuntimeState();
            ShipRegistry.ClearCaches();
            TerrainDefOf.Space = oldSpace;
            harmony.UnpatchAll(harmony.Id);
        }
    }
}
