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
        universe.objects.Add(frame);
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
            }
            SubstructureOwnership.BumpStructureVersion();
        }
        try
        {
            var console = AddTestConsole(map, enemy, 2610);
            var main = new HashSet<IntVec3> { console.parent.Position };
            var debris = new HashSet<IntVec3> { new IntVec3(3, 0, 1), new IntVec3(3, 0, 2), new IntVec3(3, 0, 3) };
            Deck(2610, main.Concat(debris).ToArray());
            var shipId = new DriveSubject(0, 2610);
            ShipStatusPanel.SelectStructure(map, shipId);
            var followMain = new ShipFlightTransitionService.SplitSelection(map, shipId);
            followMain.Transferred(debris, largeMap, 2611);
            followMain.Transferred(main, smallMap, 2610);
            Check(followMain.Winner() == (smallMap, shipId),
                "selected enemy follows smaller primary-console side without faction or pilot gating");

            var stationCells = new[] { new IntVec3(0, 0, 0), new IntVec3(0, 0, 1) };
            Deck(2620, stationCells);
            var stationId = new DriveSubject(0, 2620);
            var station = new ShipFlightTransitionService.SplitSelection(map, stationId);
            station.Transferred(main, smallMap, 2610);
            Check(station.Winner() == (map, stationId), "another vessel leaving does not steal the station selection");

            console.isMain = false;
            SubstructureOwnership.BumpStructureVersion();
            ShipStatusPanel.SelectStructure(map, shipId);
            Check(ShipStatusPanel.TryGetSelectedStructure(map, out var selected) && selected == shipId
                && !ShipStatusPanel.TryGetSelectedShip(map, out _) && !universe.Pilot.SelectedShipOnMap(map).IsValid,
                "losing primary console preserves structure selection without redirecting ship shortcuts");
            var largest = new ShipFlightTransitionService.SplitSelection(map, shipId);
            largest.Transferred(main, smallMap, 2630);
            largest.Transferred(debris, largeMap, 2631);
            Check(largest.Winner() == (largeMap, new DriveSubject(0, 2631)),
                "console-less selection follows largest fragment and its newly assigned owner");
            var tie = new ShipFlightTransitionService.SplitSelection(map, stationId);
            tie.Transferred(new HashSet<IntVec3> { stationCells[0] }, smallMap, 2632);
            Check(tie.Winner() == (map, stationId), "equal-size fragments keep the source map");

            Deck(0, new IntVec3(4, 0, 4));
            ShipStatusPanel.SelectStructure(map, default);
            Check(ShipStatusPanel.TryGetSelectedStructure(map, out selected) && selected.key == 0
                && ShipRegistry.StructuresOnMap(map).Contains(default(DriveSubject)),
                "unassigned foundations can be selected without inventing a persistent owner");

            for (int i = 0; i < map.terrainGrid.topGrid.Length; i++) map.terrainGrid.topGrid[i] = space;
            Check(SpaceLifecycleService.HasMapSupport(frame), "foundation supports an otherwise all-space map");
            Array.Clear(map.terrainGrid.foundationGrid, 0, map.terrainGrid.foundationGrid.Length);
            SubstructureOwnership.BumpStructureVersion();
            // Existing console and loose contents do not provide terrain support.
            var filth = (Thing)Raw(typeof(Thing));
            filth.def = (ThingDef)Raw(typeof(ThingDef)); filth.def.category = ThingCategory.Filth;
            map.listerThings.AllThings.Add(filth);
            Check(!SpaceLifecycleService.HasMapSupport(frame), "buildings and filth cannot keep an unsupported map alive");
            int queued = Actions(universe).Count;
            universe.Lifecycle.QueueStructureCheck(map);
            universe.Lifecycle.QueueStructureCheck(map);
            Check(Actions(universe).Count == queued + 1, "structure-loss events coalesce into one frame action");
            map.terrainGrid.topGrid[0] = (TerrainDef)Raw(typeof(TerrainDef));
            SubstructureOwnership.BumpStructureVersion();
            Check(SpaceLifecycleService.HasMapSupport(frame), "non-space terrain supports a foundation-less map");
            Call(universe, "RunFrameActions");
            Check(universe.ObjectById(frame.objectId) == frame,
                "deferred recycling rechecks final terrain and preserves restored support");
            map.terrainGrid.topGrid[0] = space;
            var hull = AddTestHull(map, player, 2640);
            SubstructureOwnership.BumpStructureVersion();
            Check(SpaceLifecycleService.HasMapSupport(frame), "shuttle remains supported without any terrain or foundation");
            UnspawnFixture(hull, map);
            frame.projectileRoleOverride = true;
            Check(SpaceLifecycleService.HasMapSupport(frame), "projectile role retains its own lifecycle");
        }
        finally
        {
            universe.objects.Remove(frame);
            universe.Lifecycle.Reset();
            ShipStatusPanel.ClearRuntimeState();
            ShipRegistry.ClearCaches();
            TerrainDefOf.Space = oldSpace;
            harmony.UnpatchAll(harmony.Id);
        }
    }
}
