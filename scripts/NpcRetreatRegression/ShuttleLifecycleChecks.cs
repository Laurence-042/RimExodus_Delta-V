using System;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static Dictionary<DriveSubject, ShipController> Controllers(UniverseManager universe) =>
        (Dictionary<DriveSubject, ShipController>)typeof(ShipControlService)
            .GetField("controllers", Private).GetValue(universe.Controls);

    static void UnspawnFixture(CompSpaceShuttle hull, Map map)
    {
        map.thingGrid.Deregister(hull.parent);
        map.listerThings.AllThings.Remove(hull.parent);
        map.listerThings.ThingsOfDef(hull.parent.def).Remove(hull.parent);
        map.listerThings.ThingsInGroup(ThingRequestGroup.Transporter).Remove(hull.parent);
        Set(typeof(Thing), hull.parent, "mapIndexOrState", (sbyte)-1);
        UniverseManager universe = Current.Game.GetComponent<UniverseManager>();
        DriveSubject identity = hull.Vessel.Identity;
        universe.QueueFrameAction(() => universe.Controls.ReconcileOwner(identity));
        ShipStructureChanges.Notify(map); // Building.DeSpawn postfix runs after the Comp callback.
    }

    static void SpawnFixture(CompSpaceShuttle hull, Map map)
    {
        Set(typeof(Thing), hull.parent, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        map.listerThings.AllThings.Add(hull.parent);
        var defs = (Dictionary<ThingDef, List<Thing>>)typeof(ListerThings)
            .GetField("listsByDef", Private).GetValue(map.listerThings);
        defs[hull.parent.def] = new List<Thing> { hull.parent };
        var groups = (List<Thing>[])typeof(ListerThings).GetField("listsByGroup", Private).GetValue(map.listerThings);
        groups[(int)ThingRequestGroup.Transporter] = new List<Thing> { hull.parent };
        map.thingGrid.Register(hull.parent);
        // Invoke the shared lifecycle entry used by the Comp; its graphical invalidation needs Unity.
        ShipStructureChanges.Notify(map);
        Current.Game.GetComponent<UniverseManager>().Controls.For(hull.Vessel);
        ShipStructureChanges.Notify(map);
    }

    static void CheckShuttleLifecycle(UniverseManager universe, Faction player)
    {
        universe.Controls.Reset();
        var hostMap = TestMap(1400);
        var flightMap = TestMap(1401);
        var host = new MapSpaceObject(1400, hostMap, new SpacePose(Vector3.forward, 0));
        var flight = new MapSpaceObject(1401, flightMap, host.pose);
        universe.objects.Add(host);
        universe.objects.Add(flight);
        AddTestBuilding(hostMap, player, 1402, TestBuildingDef("host structure"), new EmptyComp());
        Check(!host.HasTag(SpaceObjectTag.Ship) && !flight.Ships.Any(), "lifecycle fixture warms empty membership caches before hull spawn");
        var hull = AddTestHull(hostMap, player, 1403);
        ShipStructureChanges.Notify(hostMap);
        universe.Controls.For(hull.Vessel);
        var identity = hull.Vessel.Identity;
        Check(host.HasTag(SpaceObjectTag.Ship) && Controllers(universe).ContainsKey(identity),
            "spawn reconciliation invalidates prewarmed membership before registering the ship controller");
        ShipController controller = Controllers(universe)[identity];
        hull.combat.fireControlMode = ShipFireControlMode.Manual;
        hull.combat.weaponGroupsMain = 8;
        var gun = new CompSpaceWeapon { parent = hull.parent, props = new CompProperties_SpaceWeapon() };
        hull.parent.AllComps.Add(gun);
        ShipStructureChanges.Notify(hostMap);
        Check(hull.Vessel.Weapons.Single() == gun && !hull.Vessel.CanDriveIn(host),
            "parked ship retains intrinsic weapons and combat state without driving its host");
        var subject = identity.InContainer(host.objectId);
        UnspawnFixture(hull, hostMap);
        SpawnFixture(hull, flightMap);
        Call(universe, "RunFrameActions");
        Check(ReferenceEquals(Controllers(universe)[identity], controller)
            && ShipRegistry.ShipByIdentity(identity).Map == flightMap && subject.Resolve(universe) == flight,
            "departure and deferred despawn reconciliation preserve controller and resolve the new container");
        Check(!host.HasTag(SpaceObjectTag.Ship) && flight.InterceptionEligible
            && !host.Weapons.Any() && flight.Weapons.Single() == gun,
            "departure refreshes source and destination tags and weapon caches immediately");
        universe.Controls.ForgetMap(hostMap);
        Check(ReferenceEquals(Controllers(universe)[identity], controller),
            "old-map cleanup cannot evict the controller that already followed its ship");

        // Return through the same Comp lifecycle as Gravship placement; shared identity never changes.
        UnspawnFixture(hull, flightMap);
        SpawnFixture(hull, hostMap);
        Call(universe, "RunFrameActions");
        Check(!flight.InterceptionEligible && !hull.Vessel.CanDriveIn(host)
            && ShipRegistry.ShipByIdentity(identity).Map == hostMap && subject.Resolve(universe) == null,
            "docking removes the old flight role and rejects driving the destination host");
        Check(ReferenceEquals(Controllers(universe)[identity], controller)
            && hull.combat.fireControlMode == ShipFireControlMode.Manual && hull.combat.weaponGroupsMain == 8,
            "docking preserves the ship controller, fire-control mode and weapon groups");
        universe.Controls.ForgetMap(flightMap);
        Check(Controllers(universe).ContainsKey(identity), "source flight-map cleanup preserves the docked ship controller");

        // Exercise the real pending-work contract without initializing vanilla presentation services.
        var transition = (ShipFlightTransitionService)Raw(typeof(ShipFlightTransitionService));
        Set(typeof(ShipFlightTransitionService), transition, "dockingCleanupMaps", new HashSet<Map>());
        transition.RegisterDockingCleanup(flightMap);
        transition.RegisterDockingCleanup(hostMap);
        transition.FinishDockingCleanup(flightMap);
        Check(transition.HasLandedTargetAwaitingCleanup && transition.IsAwaitingDockingCleanup(hostMap),
            "concurrent ordinary and intrusion completions retain the gate until every source is cleaned");
        transition.FinishDockingCleanup(hostMap);
        Check(!transition.FreeFlightTransitionPending(), "last source completion releases the shared transition gate");

        UnspawnFixture(hull, hostMap);
        Call(universe, "RunFrameActions");
        Check(!ShipRegistry.ShipByIdentity(identity).IsValid && !Controllers(universe).ContainsKey(identity)
            && !host.HasTag(SpaceObjectTag.Ship) && !host.Weapons.Any(),
            "destroyed or removed hull releases identity, controller, role and weapon membership");

        // A restored object has a new Comp and adapter, but the same persisted Thing identity.
        var restoredHull = AddTestHull(flightMap, player, identity.key);
        ShipStructureChanges.Notify(flightMap);
        universe.Controls.For(restoredHull.Vessel);
        Check(restoredHull.Vessel.Identity == identity && !ReferenceEquals(restoredHull.Vessel, hull.Vessel)
            && ReferenceEquals(ShipRegistry.ShipByIdentity(identity), restoredHull.Vessel)
            && !ReferenceEquals(Controllers(universe)[identity], controller),
            "load-style spawn reconstructs runtime adapters and controllers from the persisted identity");
        restoredHull.parent.AllComps.Add(new CompLaunchable
            { parent = restoredHull.parent, props = new CompProperties_Launchable() });
        int countBeforeLaunch = universe.objects.Count;
        universe.Shuttles.Launch((Building)restoredHull.parent);
        Check(universe.objects.Count == countBeforeLaunch && restoredHull.parent.Map == flightMap
            && !restoredHull.CompGetGizmosExtra().Any(),
            "already flying shuttle neither exposes nor executes another launch");
        universe.Shuttles.PlaceBoardedShuttle(flight, (Building)hull.parent, host,
            host.pose.position, Vector2.zero, Vector2.up, new IntVec3(1, 0, 2), false);
        Call(universe, "RunFrameActions");
        Check(restoredHull.parent.Map == flightMap && Actions(universe).Count == 0,
            "queued intrusion cannot substitute a restored hull for the original impact body");
        universe.Controls.Reset();
        Check(Controllers(universe).Count == 0, "runtime reset drops ship controllers");
        universe.Controls.Rebuild();
        Check(Controllers(universe).ContainsKey(identity) && flight.InterceptionEligible,
            "load rebuild recreates controllers and derived tags from live map members");
        universe.Controls.Reset();
        universe.objects.Clear();
        Find.Maps.Clear();
        ShipRegistry.ClearCaches();
        ShipPhysics.ClearCache();
        SpaceCollision.ClearCaches();
    }
}
