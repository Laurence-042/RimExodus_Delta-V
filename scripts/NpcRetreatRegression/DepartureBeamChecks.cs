using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void CheckEvaDeparture(UniverseManager universe, CompEvaSuit eva)
    {
        Map map = TestMap(58131);
        map.roofGrid = new RoofGrid(map);
        map.weatherManager = new WeatherManager(map);
        Set(map.weatherManager, "curWeather", new WeatherDef());
        Set(map, "gameConditionManager", new GameConditionManager(map));
        StatDef originalMass = StatDefOf.Mass, originalPiloting = StatDefOf.PilotingAbility;
        StatDefOf.Mass = new StatDef { defName = "DepartureFixtureMass", defaultBaseValue = 20f };
        StatDefOf.PilotingAbility = new StatDef { defName = "DepartureFixturePiloting", defaultBaseValue = 1f };
        CompEvaSuit PlaceSuit(int id, IntVec3 cell, List<ShuttleThrusterConfig> thrusters)
        {
            ThingDef def = TestBuildingDef("departureEva" + id);
            def.comps.Add(new CompProperties_LocalShuttle());
            def.comps.Add(new CompProperties_LocalLaunchable { minFuelCost = 0f, cooldownTicks = 0 });
            def.comps.Add(new CompProperties_VehicleCargo { massCapacity = 60f, crewCount = 1 });
            def.comps.Add(new CompProperties_StructureMass());
            def.comps.Add(new CompProperties_EvaSuit { virtualThrusters = thrusters });
            var building = CreateFixtureBuilding(def, id, cell, Rot4.North, eva.parent.Faction);
            PlaceFixtureBuilding(building, map);
            return building.GetComp<CompEvaSuit>();
        }
        try
        {
            CompEvaSuit powered = PlaceSuit(58132, new IntVec3(1, 0, 2), eva.ShuttleProps.virtualThrusters);
            CompEvaSuit unpowered = PlaceSuit(58133, new IntVec3(3, 0, 2), null);
            CompVehicleCargo cargo = powered.parent.GetComp<CompVehicleCargo>();
            var launch = powered.parent.GetComp<CompLocalLaunchable>();
            var ship = powered.Vessel;
            Check(!launch.CanLaunch() && !ship.CheckDeparture(), "unstarted EVA loading fails the native departure gate");
            cargo.groupID = 1;
            Check(!launch.CanLaunch() && !ship.CheckDeparture(), "a loaded EVA group still requires a pilot");
            var pilot = (Pawn)Raw(typeof(Pawn));
            pilot.def = TestBuildingDef("departureEvaPilot");
            pilot.def.category = ThingCategory.Pawn;
            pilot.def.race = new RaceProperties { intelligence = Intelligence.Humanlike };
            pilot.def.race.lifeStageAges = new List<LifeStageAge> { new LifeStageAge { def = new LifeStageDef() } };
            pilot.stackCount = 1;
            pilot.health = new Pawn_HealthTracker(pilot);
            pilot.ageTracker = new Pawn_AgeTracker(pilot);
            Set(pilot.ageTracker, "cachedLifeStageIndex", 0);
            Set(typeof(Thing), pilot, "mapIndexOrState", (sbyte)-1);
            Set(typeof(Thing), pilot, "factionInt", powered.parent.Faction);
            Check(cargo.innerContainer.TryAdd(pilot, canMergeWithExistingStacks: false), "native transporter accepts the pilot");
            Check(launch.CanLaunch() && ship.CheckDeparture(), "a ready EVA passes the native departure gate");
            var roofs = (RoofDef[])AccessTools.Field(typeof(RoofGrid), "roofGrid").GetValue(map.roofGrid);
            roofs[map.cellIndices.CellToIndex(powered.parent.Position)] = new RoofDef();
            Check(!launch.CanLaunch() && !ship.CheckDeparture(), "ordinary roof blocks the native EVA departure");
            Check(!unpowered.Vessel.BuildThrusters().Any() && !unpowered.Vessel.CheckDeparture(),
                "an EVA generated without propulsion cannot depart");
        }
        finally
        {
            StatDefOf.Mass = originalMass;
            StatDefOf.PilotingAbility = originalPiloting;
            RetireFixtureMap(map);
        }
    }

    static void CheckDepartureAndBeamFixes(UniverseManager universe, CompEvaSuit eva, CompSpaceWeapon weapon)
    {
        CheckEvaDeparture(universe, eva);
        var map = eva.parent.Map;
        var steel = (Thing)Raw(typeof(Thing)); steel.def = TestBuildingDef("departureSteel");
        steel.def.category = ThingCategory.Item; steel.def.bringAlongOnGravship = true;
        Set(steel, "positionInt", eva.parent.Position);
        Type interop = typeof(ShipBase).Assembly.GetType("DeltaV.GravshipInterop");
        var capture = AccessTools.Method(interop, "ShouldBringOnGravship");
        bool Captures(ShipBase ship) => (bool)capture.Invoke(null,
            new object[] { steel, steel.Position, ship.MoveColocatedItems });
        Check(!Captures(eva.Vessel) && !Captures(new ShuttleShip(eva)) && Captures(ShipBase.Invalid),
            "shared capture excludes loose steel for shuttle subclasses and includes it for foundation ships");
        eva.parent.def.bringAlongOnGravship = true;
        Check((bool)capture.Invoke(null, new object[] { eva.parent, eva.parent.Position, false }),
            "item exclusion still captures the hull with its held passengers and cargo");

        var service = universe.Projectiles;
        SpaceObject source = universe.FrameOf(map);
        Map targetMap = TestMap(58130);
        var target = new MapSpaceObject(58130, targetMap,
            new SpacePose(OrbitSphere.Move(source.pose.position, new Vector2(30, 10)), -.4f));
        universe.RegisterObject(target);
        Type projectileType = typeof(SpaceProjectileService).GetNestedType("Projectile", System.Reflection.BindingFlags.NonPublic);
        object projectile = Activator.CreateInstance(projectileType, true);
        Set(projectile, "sourceContainerId", source.objectId);
        Set(projectile, "weapon", weapon); Set(projectile, "launcher", eva.parent);
        Set(projectile, "props", new CompProperties_SpaceProjectile { laserRange = 60, laserBeamWidth = .2f });
        var beams = (IList)AccessTools.Field(typeof(SpaceProjectileService), "beams").GetValue(service);
        beams.Clear();
        Vector2 muzzle = SpaceWeaponGeometry.MuzzleMapPoint(weapon), hit = new Vector2(2, 3);
        Vector3 from = SpaceTransform.MapPointToUniverse(source, map, muzzle);
        Vector3 to = SpaceTransform.MapPointToUniverse(target, targetMap, hit);
        object Anchor(SpaceObject obj, Vector3 point) => AccessTools.Method(typeof(SpaceProjectileService), "BeamAnchor")
            .Invoke(service, new object[] { obj, point });
        var record = AccessTools.Method(typeof(SpaceProjectileService), "RecordBeamSegment");
        object[] args = { projectile, from, to, Anchor(source, from), Anchor(target, to), 0 };
        record.Invoke(service, args);
        object beam = beams[0];
        record.Invoke(service, args);
        Check(beams.Count == 1 && ReferenceEquals(beam, beams[0]), "laser pulses refresh one visual segment");
        source.pose.position = OrbitSphere.Move(source.pose.position, new Vector2(7, -3)); source.pose.angle += .8f;
        target.pose.position = OrbitSphere.Move(target.pose.position, new Vector2(-4, 9)); target.pose.angle -= .5f;
        var endpoints = AccessTools.Method(typeof(SpaceProjectileService), "BeamEndpoints");
        object[] ends = { beam, Vector3.zero, Vector3.zero };
        endpoints.Invoke(service, ends);
        Check(OrbitSphere.Offset((Vector3)ends[1], SpaceTransform.MapPointToUniverse(source, map, muzzle)).magnitude < .01f
            && OrbitSphere.Offset((Vector3)ends[2], SpaceTransform.MapPointToUniverse(target, targetMap, hit)).magnitude < .01f,
            "laser endpoints follow independent source/target translation and rotation");
        Vector3 sourceBefore = source.pose.position, targetBefore = target.pose.position;
        foreach (SpaceObject focus in new[] { source, target, source, target })
        {
            Vector3 world = (Vector3)ends[2];
            Vector3 draw = SpaceTransform.FocusedDrawPosition(focus, focus.map, world, 0);
            Check(OrbitSphere.Offset(world, SpaceTransform.MapPointToUniverse(focus, focus.map,
                new Vector2(draw.x, draw.z))).magnitude < .01f, "beam survives repeated focus changes without moving its anchor");
        }
        Check(source.pose.position == sourceBefore && target.pose.position == targetBefore, "beam presentation never mutates universe positions");
        int tick = Find.TickManager.TicksGame;
        var fade = AccessTools.Method(beam.GetType(), "FadeAge");
        Set(weapon, "lastBeamTriggerTick", tick + 20);
        Check((float)fade.Invoke(beam, new object[] { tick + 20 }) == 0, "held trigger bridges pulse cooldown without fading");
        Check((float)fade.Invoke(beam, new object[] { tick + 35 }) == 1, "released trigger fades out without extra damage ticks");
        Vector3 shieldPoint = SpaceTransform.MapPointToUniverse(target, targetMap, hit);
        record.Invoke(service, new object[] { projectile, shieldPoint, from,
            Anchor(target, shieldPoint), Anchor(source, from), 1 });
        object reflected = beams[1];
        target.pose.angle += .6f;
        object[] reflectedEnds = { reflected, Vector3.zero, Vector3.zero };
        endpoints.Invoke(service, reflectedEnds);
        Check(OrbitSphere.Offset((Vector3)reflectedEnds[1],
            SpaceTransform.MapPointToUniverse(target, targetMap, hit)).magnitude < .01f,
            "reflected segment starts at its moving shield anchor instead of the emitter muzzle");
        AccessTools.Method(typeof(SpaceProjectileService), "NotifyContainerRemoving").Invoke(service, new object[] { target });
        Check(beams.Count == 0, "target removal releases attached laser segments");
    }
}
