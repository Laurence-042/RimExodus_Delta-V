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
    sealed class DepartureLaunchProbe : CompLaunchable
    {
        internal AcceptanceReport report = true;
        internal int calls;
        public override AcceptanceReport CanLaunch(float? overrideFuelLevel = null) { calls++; return report; }
    }

    static void CheckDepartureAndBeamFixes(UniverseManager universe, CompEvaSuit eva, CompSpaceWeapon weapon)
    {
        var map = eva.parent.Map;
        map.roofGrid = new RoofGrid(map);
        var launch = new DepartureLaunchProbe { parent = eva.parent };
        eva.parent.AllComps.Add(launch);
        eva.ShuttleProps.virtualThrusters = new List<ShuttleThrusterConfig> {
            new ShuttleThrusterConfig { force = new Vector2(300, 0) } };
        launch.report = "pilot unavailable";
        Check(!eva.Vessel.CheckDeparture() && launch.calls == 1,
            "shortcut departure propagates the native launch gate including pilot/cargo checks");
        launch.report = true;
        Check(eva.Vessel.CheckDeparture(), "ready EVA passes the same departure gate");
        var roofs = (RoofDef[])AccessTools.Field(typeof(RoofGrid), "roofGrid").GetValue(map.roofGrid);
        roofs[map.cellIndices.CellToIndex(eva.parent.Position)] = new RoofDef();
        Check(!eva.Vessel.CheckDeparture(), "ordinary ceiling blocks departure even if native gate accepted");
        Array.Clear(roofs, 0, roofs.Length);
        eva.ShuttleProps.virtualThrusters.Clear();
        Check(!eva.Vessel.CheckDeparture(), "departure rejects a craft without effective propulsion");

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
        eva.parent.AllComps.Remove(launch);
    }
}
