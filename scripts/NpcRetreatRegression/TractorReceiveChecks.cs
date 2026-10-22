using System;
using System.Collections.Generic;
using HarmonyLib;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

internal static partial class Program
{
    // Replace only Unity-dependent grid allocation and spawn/despawn endpoints.
    // Pull, frame validation, vanilla GenPlace, handoff and pool release stay production code.
    static Map receivingFixture;
    static bool receivingBlocked;
    static bool ReceiveRoom(Map map, ref Room __result)
    { if (map != receivingFixture) return true; __result = null; return false; }
    static bool ReceiveDowned(Pawn __instance, ref bool __result)
    { if (!(__instance is ReceivingPawn)) return true; __result = false; return false; }
    sealed class ReceivingPort : Building_DockingPort
    {
        public override bool PawnCanOpen(Pawn pawn) => true;
    }
    static void ProbeRemoveCargo(Thing thing)
    {
        thing.Map.thingGrid.Deregister(thing);
        thing.Map.listerThings.AllThings.Remove(thing);
        Set(typeof(Thing), thing, "mapIndexOrState", (sbyte)-1);
    }
    sealed class ReceivingItem : Thing
    {
        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish) => ProbeRemoveCargo(this);
    }
    sealed class ReceivingPawn : Pawn
    {
        public bool swapped;
        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish) => ProbeRemoveCargo(this);
        public override void PostSwapMap() { swapped = true; }
    }
    static void ProbeSpawnCargo(Thing thing, IntVec3 cell, Map map)
    {
        Set(typeof(Thing), thing, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        Set(typeof(Thing), thing, "positionInt", cell);
        map.listerThings.AllThings.Add(thing);
        map.thingGrid.Register(thing);
    }
    static bool ReceiveSpawn(Thing newThing, IntVec3 loc, Map map, ref Thing __result)
    {
        if (!(newThing is ReceivingItem) && !(newThing is ReceivingPawn)) return true;
        ProbeSpawnCargo(newThing, loc, map); __result = newThing; return false;
    }
    static bool ReceiveWalkable(IntVec3 c, Map map, ref bool __result)
    {
        if (map != receivingFixture) return true;
        __result = !receivingBlocked && c.InBounds(map); return false;
    }
    static bool ReceiveStackable(Thing other, ref bool __result)
    {
        if (!(other is ReceivingItem)) return true;
        // Fixtures have different Defs; avoid the vanilla DLC/relic lookup requiring Unity.
        __result = false; return false;
    }
    static void CheckTractorReceive(UniverseManager universe)
    {
        var harness = new Harmony("DeltaV.Regression.TractorReceive");
        TerrainDef previousMoonPool = (TerrainDef)AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").GetValue(null);
        bool drag = DeltaVMod.CurrentSettings.passiveDrag;
        var radii = (Dictionary<RimWorld.Planet.PlanetLayer, float>)AccessTools.Field(typeof(OrbitSphere), "RadiusByLayer").GetValue(null);
        var scales = (Dictionary<RimWorld.Planet.PlanetLayer, float>)AccessTools.Field(typeof(OrbitSphere), "WorldPerCellByLayer").GetValue(null);
        var layer = OrbitSphere.Layer;
        float radius = radii[layer], scale = scales[layer];
        radii[layer] = 100f; scales[layer] = .0001f;
        Map destination = receivingFixture = TestMap(49750);
        destination.edificeGrid = new EdificeGrid(destination);
        var moon = (TerrainDef)Raw(typeof(TerrainDef));
        moon.defName = "tractor_test_moonpool";
        AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").SetValue(null, moon);
        var weapon = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon { tractor = true } };
        Building launcher = AddTestBuilding(destination, Faction.OfPlayer, 49751, TestBuildingDef("tractor"), weapon);
        launcher.def.passability = Traversability.Standable;
        launcher.def.hasInteractionCell = true;
        launcher.def.interactionCellOffset = new IntVec3(0, 0, 1);
        var power = new CompPowerTrader { parent = launcher };
        Set(power, "powerOnInt", true);
        ((List<ThingComp>)AccessTools.Field(typeof(ThingWithComps), "comps").GetValue(launcher)).Add(power);
        IntVec3 cell = launcher.Position;
        destination.terrainGrid.topGrid[destination.cellIndices.CellToIndex(cell)] = moon;
        var receiver = new MapSpaceObject(49750, destination, new SpacePose(new Vector3(1, 2, 3).normalized, 0f));
        universe.objects.Add(receiver);
        try
        {
            harness.Patch(AccessTools.Method(typeof(GenGrid), nameof(GenGrid.Walkable), new[] { typeof(IntVec3), typeof(Map) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(ReceiveWalkable)));
            harness.Patch(AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.GetRoom), new[] { typeof(IntVec3), typeof(Map) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(ReceiveRoom)));
            harness.Patch(AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Downed)),
                prefix: new HarmonyMethod(typeof(Program), nameof(ReceiveDowned)));
            harness.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.CanStackWith)),
                prefix: new HarmonyMethod(typeof(Program), nameof(ReceiveStackable)));
            harness.Patch(AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[] {
                typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(ReceiveSpawn)));
            DeltaVMod.CurrentSettings.passiveDrag = false;
            Current.Game.currentMapIndex = -1;
            Check(SpaceTractor.CanOperate(weapon), "real tractor gate accepts a powered moonpool receiver");
            foreach (bool pawn in new[] { false, true })
            {
                Map source = TestMap(pawn ? 49753 : 49752);
                source.info.Size = new IntVec3(1, 1, 1);
                source.cellIndices = new CellIndices(source);
                source.terrainGrid = new TerrainGrid(source);
                PreparePoolMap(source, SpaceObjectTag.Scattered);
                SpaceMapLease lease = universe.MapPool.Restore(source);
                var thing = (Thing)Raw(pawn ? typeof(ReceivingPawn) : typeof(ReceivingItem));
                thing.def = TestBuildingDef(pawn ? "castaway" : "cargo");
                thing.def.category = pawn ? ThingCategory.Pawn : ThingCategory.Item;
                thing.def.passability = Traversability.Standable;
                thing.def.stackLimit = 75; thing.stackCount = 1;
                ProbeSpawnCargo(thing, IntVec3.Zero, source);
                Vector3 at = SpaceTransform.MapPointToUniverse(receiver, destination, new Vector2(cell.x + .5f, cell.z + .5f));
                var scattered = new MapSpaceObject(source.uniqueID, source,
                    new SpacePose(OrbitSphere.Move(at, new Vector2(1.5f, 0f)), 0f)
                    { velocity = new Vector2(1f, 0f) }) { baseTag = SpaceObjectTag.Scattered };
                universe.objects.Add(scattered); universe.Scatter.Register(scattered);
                // Vanilla Near must find another cell instead of requiring a free device center.
                var blocker = (Thing)Raw(pawn ? typeof(ReceivingPawn) : typeof(ReceivingItem));
                blocker.def = TestBuildingDef("occupied_moonpool");
                blocker.def.category = thing.def.category; blocker.def.passability = Traversability.Standable;
                blocker.stackCount = 1;
                ProbeSpawnCargo(blocker, cell, destination);
                receivingBlocked = true;
                Check(!universe.Scatter.TryReceive(scattered, launcher) && thing.Map == source,
                    "no vanilla nearby placement retains cargo and pawn on the source map");
                receivingBlocked = false;
                Set(Find.TickManager, "ticksGameInt", pawn ? 13000 : 10000);
                universe.Scatter.Pull(scattered, launcher);
                Call(universe, "RunFrameActions");
                Check(thing.Map == destination,
                    "tractor collects inside the two-cell radius immediately without waiting for overlap or low relative speed");
                Check(thing.Map == destination && thing.Position != cell && thing.Position != launcher.InteractionCell
                    && (!(thing is ReceivingPawn p) || p.swapped),
                    "vanilla deep-drill Near output places cargo and pawn away from the occupied center and interaction cell");
                Call(universe, "RunFrameActions");
                Check(universe.ObjectById(scattered.objectId) == null && universe.MapPool.IsIdle(source),
                    "successful tractor handoff unregisters the empty scattered object and returns its map");
                thing.DeSpawn(); blocker.DeSpawn(); universe.MapPool.Forget(source); Find.Maps.Remove(source);
            }
            Map ownMap = TestMap(49754);
            var own = new MapSpaceObject(49754, ownMap, receiver.pose);
            universe.objects.Add(own);
            destination.reachability = (Reachability)Raw(typeof(Reachability));
            ReceivingPort Port(Map map)
            {
                var port = (ReceivingPort)Raw(typeof(ReceivingPort));
                port.def = TestBuildingDef("receiving_port");
                Set(typeof(Thing), port, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
                Set(typeof(Thing), port, "positionInt", cell);
                Set(typeof(Thing), port, "rotationInt", Rot4.North);
                return port;
            }
            var connection = new SoftDockService.Link { a = Port(ownMap), b = Port(destination), connected = true, progress = 1f };
            var service = universe.SoftDock;
            var links = (List<SoftDockService.Link>)AccessTools.Field(typeof(SoftDockService), "links").GetValue(service);
            Set(service, "<Enabled>k__BackingField", true); links.Add(connection);
            var portal = (SoftDockPortal)Raw(typeof(SoftDockPortal));
            portal.port = connection.a; portal.peer = connection.b;
            Check(portal.GetOtherMap() == destination && portal.GetDestinationLocation() == cell - Rot4.North.FacingCell,
                "native MapPortal adapter exposes the opposite ship and interior cell without foreign-pawn reachability");
            CheckNativePortal(portal, ownMap);
            links.Remove(connection); Set(service, "<Enabled>k__BackingField", false);
            universe.objects.Remove(own); Find.Maps.Remove(ownMap);
        }
        finally
        {
            harness.UnpatchAll(harness.Id);
            AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").SetValue(null, previousMoonPool);
            DeltaVMod.CurrentSettings.passiveDrag = drag;
            radii[layer] = radius; scales[layer] = scale;
            universe.objects.Remove(receiver); Find.Maps.Remove(destination); receivingFixture = null; receivingBlocked = false;
        }
    }
}
