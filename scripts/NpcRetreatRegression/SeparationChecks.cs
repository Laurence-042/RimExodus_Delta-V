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
    static bool RegressionSubstructure(TerrainDef __instance, ref bool __result)
    { __result = __instance.isFoundation; return false; }
    static int separationTransfers, separationFailOn;
    static bool SeparationTransferFixture(Map sourceMap, SpaceObject sourceFrame,
        HashSet<IntVec3> cells, ref SpaceObject __result)
    {
        separationTransfers++;
        if (separationTransfers == separationFailOn) { __result = null; return false; }
        // Only replace Unity map generation/transfer; production grouping, gate and retries run.
        foreach (IntVec3 cell in cells)
            sourceMap.terrainGrid.foundationGrid[sourceMap.cellIndices.CellToIndex(cell)] = null;
        SubstructureOwnership.BumpStructureVersion();
        __result = sourceFrame;
        return false;
    }
    static bool FailScatterFixture(ref bool __result) { __result = false; return false; }

    static void CheckSeparationRules(UniverseManager universe, Faction faction)
    {
        var harmony = new Harmony("DeltaV.Regression.Separation");
        sbyte oldMapIndex = Current.Game.currentMapIndex;
        Current.Game.currentMapIndex = -1;
        Map map = TestMap(49900);
        var frame = new MapSpaceObject(49900, map, new SpacePose(Vector3.forward, .25f)
            { velocity = new Vector2(3, -2), angularVelocity = .1f });
        var metadata = new DeltaVPocketMapMetadata(map);
        map.components.Add(metadata);
        universe.objects.Add(frame);
        Set(universe, "objectById", null); // This fixture bypasses RegisterObject.
        var main = AddTestConsole(map, faction, 49901);
        var space = (TerrainDef)Raw(typeof(TerrainDef));
        space.defName = "separationSpace"; space.natural = true; space.exposesToVacuum = true;
        TerrainDef oldSpace = TerrainDefOf.Space;
        TerrainDefOf.Space = space;
        ThingDef oldPort = DeltaVDefOf.DeltaV_DockingPort;
        DeltaVDefOf.DeltaV_DockingPort = TestBuildingDef("separationPort");
        var deck = (TerrainDef)Raw(typeof(TerrainDef)); deck.isFoundation = true;
        var soil = (TerrainDef)Raw(typeof(TerrainDef)); soil.natural = true;
        var floor = (TerrainDef)Raw(typeof(TerrainDef)); floor.natural = false;
        for (int i = 0; i < map.terrainGrid.topGrid.Length; i++) map.terrainGrid.topGrid[i] = space;
        int[] owners = new int[map.cellIndices.NumGridCells];
        Set(SubstructureOwnership.For(map), "ownerGrid", owners);
        void Foundation(IntVec3 c, int owner)
        {
            int i = map.cellIndices.CellToIndex(c);
            map.terrainGrid.foundationGrid[i] = deck; owners[i] = owner;
            SubstructureOwnership.BumpStructureVersion();
        }
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
                prefix: new HarmonyMethod(typeof(Program), nameof(RegressionSubstructure)));
            Check(harmony.Patch(AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
                    postfix: new HarmonyMethod(typeof(Patch_ShipStructure_Terrain), "Postfix")) != null
                && harmony.Patch(AccessTools.Method(typeof(TerrainGrid), "RemoveGravshipTerrainUnsafe"),
                    postfix: new HarmonyMethod(typeof(Patch_TerrainGrid_RemoveGravshipTerrainUnsafe_Ownership), "Postfix")) != null,
                "foundation and support event postfixes bind to installed RimWorld signatures");
            Foundation(main.parent.Position, main.shipId);
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "single clean ship stays in its current container");
            Foundation(IntVec3.Zero, 0);
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "unassigned detached foundation does not force ship extraction");
            owners[0] = 999;
            SubstructureOwnership.BumpStructureVersion();
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "foreign wreck ownership does not force extraction either");
            metadata.worksite = true;
            Check(ShipRegistry.RequiresOwnPocket(main.Ship), "worksite always extracts the ship to a compact map");
            metadata.worksite = false;
            int far = map.cellIndices.CellToIndex(new IntVec3(4, 0, 4));
            map.terrainGrid.topGrid[far] = soil;
            SubstructureOwnership.BumpStructureVersion();
            Check(ShipRegistry.RequiresOwnPocket(main.Ship), "natural ground beside the ship preserves the host");
            map.terrainGrid.topGrid[far] = space;
            Check(ShipRegistry.RequiresOwnPocket(main.Ship), "natural-ground reads reuse the structure-version cache");
            SubstructureOwnership.BumpStructureVersion();
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "terrain structure invalidation refreshes the host decision");
            map.terrainGrid.topGrid[far] = floor;
            SubstructureOwnership.BumpStructureVersion();
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "artificial flooring is not asteroid ground");
            map.terrainGrid.topGrid[far] = space;
            var other = AddTestConsole(map, faction, 49902);
            SubstructureOwnership.BumpStructureVersion();
            Check(ShipRegistry.RequiresOwnPocket(main.Ship), "other valid ship requires extraction even without thrusters");
            other.isMain = false;
            SubstructureOwnership.BumpStructureVersion();
            var shuttle = AddTestHull(map, faction, 49903);
            SubstructureOwnership.BumpStructureVersion();
            Check(!shuttle.Vessel.CanDriveIn(frame) && !ShipRegistry.RequiresOwnPocket(main.Ship),
                "carried shuttle and backup console do not become independent host ships");

            Foundation(new IntVec3(4, 0, 4), main.shipId);
            harmony.Patch(AccessTools.Method(typeof(ShipFlightTransitionService), "TransferDomainToNewPocket"),
                prefix: new HarmonyMethod(typeof(Program), nameof(SeparationTransferFixture)));
            separationTransfers = 0; separationFailOn = 2;
            SpacePose before = frame.pose;
            Check(!universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "residuals stop this tick's thrust and queue one separation");
            Check(!universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false)
                && universe.ShipTransitions.pendingSeparationOwnerIds.Count == 1,
                "repeated requests cannot duplicate a pending separation");
            Call(universe, "RunFrameActions");
            Check(separationTransfers == 2 && ShipRegistry.ResidualBlocks(map, ShipRegistry.ControlDomain(main.Ship)).Count == 1,
                "failed second transfer leaves only the unresolved block in the source");
            separationFailOn = 0;
            Check(!universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "unresolved block continues to block thrust");
            Call(universe, "RunFrameActions");
            Check(separationTransfers == 3 && universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "retry moves only the remaining block then releases the thrust gate");
            Check(main.parent.Map == map && universe.FrameOf(map) == frame
                && frame.pose.position.Equals(before.position) && frame.pose.velocity == before.velocity
                && frame.pose.angle == before.angle && frame.pose.angularVelocity == before.angularVelocity,
                "residual separation preserves the main ship Map, container and absolute motion");

            var cargo = (Thing)Raw(typeof(Thing));
            cargo.def = TestBuildingDef("looseCargo"); cargo.def.category = ThingCategory.Item;
            Set(typeof(Thing), cargo, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
            Set(typeof(Thing), cargo, "positionInt", IntVec3.Zero);
            map.listerThings.AllThings.Add(cargo);
            harmony.Patch(AccessTools.Method(typeof(ThingGrid), "RegisterInCell"),
                postfix: new HarmonyMethod(typeof(Patch_SpaceScatterCargo_Register), "Postfix"));
            harmony.Patch(AccessTools.Method(typeof(ThingGrid), "DeregisterInCell"),
                postfix: new HarmonyMethod(typeof(Patch_SpaceScatterCargo_Deregister), "Postfix"));
            map.thingGrid.Register(cargo);
            harmony.Patch(AccessTools.Method(typeof(SpaceScatterService), "TryScatter"),
                prefix: new HarmonyMethod(typeof(Program), nameof(FailScatterFixture)));
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship)
                && !universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "loose cargo without fragments uses scatter, not extraction or immediate thrust");
            Call(universe, "RunFrameActions");
            Check(!universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "failed scatter keeps loose cargo from moving with the ship");
            universe.ShipTransitions.pendingSeparationOwnerIds.Remove(main.shipId);
            Actions(universe).Clear();
            Check(universe.Scatter.LooseCargo(map).Contains(cargo),
                "cargo registration adds an unsupported item to the per-map index");
            map.terrainGrid.topGrid[0] = soil;
            universe.NotifySupportChanged(map, IntVec3.Zero);
            Check(!universe.Scatter.LooseCargo(map).Any(),
                "support change removes the indexed loose cargo without a map scan");
            map.terrainGrid.topGrid[0] = space;
            universe.NotifySupportChanged(map, IntVec3.Zero);
            Check(universe.Scatter.LooseCargo(map).Contains(cargo),
                "losing support restores the loose cargo entry");
            map.thingGrid.Deregister(cargo);
            map.listerThings.AllThings.Remove(cargo);
            Check(!universe.Scatter.LooseCargo(map).Any(),
                "cargo deregistration clears the per-map index");

            Foundation(new IntVec3(4, 0, 4), 999);
            universe.NotifyFoundationChanged(map);
            Check(!universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "foundation change invalidates the previously clean split conclusion");
            Call(universe, "RunFrameActions");
            Check(separationTransfers == 4 && universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "new residual is transferred once and the clean result is reused");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            Current.Game.currentMapIndex = oldMapIndex;
            TerrainDefOf.Space = oldSpace; DeltaVDefOf.DeltaV_DockingPort = oldPort;
            Actions(universe).Clear(); universe.ShipTransitions.pendingSeparationOwnerIds.Remove(main.shipId);
            universe.objects.Remove(frame); Set(universe, "objectById", null);
            Find.Maps.Remove(map); ShipRegistry.ClearCaches();
        }
    }
}
