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
        var weapon = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon { tractor = true, range = 60f } };
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
                receiver.pose.velocity = new Vector2(25f, -10f);
                receiver.pose.angularVelocity = .7f;
                Vector2 emitterLocal = SpaceTransform.MapToShipLocal(destination, new Vector2(cell.x + .5f, cell.z + .5f));
                scattered.pose = SpaceTransform.RigidPointPose(receiver.pose, emitterLocal + new Vector2(45f, 15f), .8f);
                scattered.pose.velocity = new Vector2(-900f, 1600f);
                scattered.pose.angularVelocity = -5f;
                Set(Find.TickManager, "ticksGameInt", pawn ? 12490 : 9490);
                weapon.aimMode = SpaceWeaponAimMode.Volley;
                var manual = new SpaceWeaponShot(universe, weapon, receiver, new WeaponFireRequest(scattered.pose.position));
                Check(universe.Scatter.TryLaunch(manual), "manual tractor finds scattered cargo near the cursor without a collision surface");
                universe.Scatter.Tick();
                Check(universe.Scatter.TryLaunch(manual), "manual cursor anchor retains a target as it moves toward the receiver");
                SpacePose beforePowerLoss = scattered.pose;
                Set(power, "powerOnInt", false);
                universe.Scatter.Tick();
                Check(!universe.Scatter.Pull(scattered, launcher)
                    && scattered.pose.direction.x == beforePowerLoss.direction.x
                    && scattered.pose.direction.y == beforePowerLoss.direction.y
                    && scattered.pose.direction.z == beforePowerLoss.direction.z,
                    "power loss cancels the real pull request without moving or losing cargo");
                Set(power, "powerOnInt", true);
                scattered.pose = SpaceTransform.RigidPointPose(receiver.pose, emitterLocal + new Vector2(70f, 0f), .8f);
                Check(!universe.Scatter.Pull(scattered, launcher), "tractor acquisition rejects targets outside its configured range");
                scattered.pose = SpaceTransform.RigidPointPose(receiver.pose, emitterLocal + new Vector2(45f, 15f), .8f);
                scattered.pose.velocity = new Vector2(-900f, 1600f);
                float previousDistance = 48f;
                bool monotonic = true, refreshed = true;
                int startTick = pawn ? 12500 : 9500;
                for (int step = 0; step < 130; step++)
                {
                    Set(Find.TickManager, "ticksGameInt", startTick + step);
                    refreshed &= universe.Scatter.Pull(scattered, launcher);
                    ShipPhysics.Integrate(receiver, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
                    ShipPhysics.Integrate(scattered, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
                    universe.Scatter.Tick();
                    var endpoint = SpaceTransform.RigidPointPose(receiver.pose, emitterLocal, 0f);
                    float distance = OrbitSphere.ArcCells(endpoint.direction, scattered.pose.direction);
                    monotonic &= distance < previousDistance;
                    previousDistance = distance;
                }
                Check(refreshed && monotonic && previousDistance < SpaceScatterService.CollectionRadius,
                    "high lateral velocity cannot escape: real tractor converges while receiver translates and rotates");
                Check(Math.Abs(SpaceTransform.WrapAngle(scattered.pose.angle - receiver.pose.angle) - .8f) < .001f,
                    "tractor preserves the captured relative angle during receiver rotation");
                Set(Find.TickManager, "ticksGameInt", startTick + 130);
                SpacePose released = scattered.pose;
                universe.Scatter.Tick();
                Check(scattered.pose.direction.x == released.direction.x && scattered.pose.direction.y == released.direction.y
                    && scattered.pose.direction.z == released.direction.z, "no refreshed shot means no coordinate correction");
                var inherited = SpaceTransform.RigidPointPose(receiver.pose,
                    SpaceTransform.UniverseToShipLocal(receiver.pose, scattered.pose.direction), .8f);
                Check((inherited.velocity - scattered.pose.velocity).magnitude < .01f,
                    "release inherits receiving ship rigid-point motion rather than the original runaway speed");
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
                universe.Scatter.Tick();
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
            ReceivingPort ownPort = Port(ownMap), otherPort = Port(destination);
            var dock = new CompSoftDocking { parent = ownPort };
            var otherDock = new CompSoftDocking { parent = otherPort };
            Set(typeof(ThingWithComps), ownPort, "comps", new List<ThingComp> { dock });
            Set(typeof(ThingWithComps), otherPort, "comps", new List<ThingComp> { otherDock });
            Set(dock, "peer", otherPort); Set(dock, "active", true);
            Set(dock, "progress", 1f); Set(dock, "connected", true);
            Set(otherDock, "incomingOwner", dock);
            universe.SoftDockEnabled = true;
            var portal = (SoftDockPortal)Raw(typeof(SoftDockPortal));
            portal.port = ownPort; portal.peer = otherPort;
            Check(portal.GetOtherMap() == destination && portal.GetDestinationLocation() == cell - Rot4.North.FacingCell,
                "native MapPortal adapter exposes the opposite ship and interior cell without foreign-pawn reachability");
            CheckNativePortal(portal, ownMap);
            universe.SoftDockEnabled = false;
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
