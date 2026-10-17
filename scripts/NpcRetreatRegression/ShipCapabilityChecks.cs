using System;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    sealed class EmptyComp : ThingComp { }
    // A third-party hull provider exercises the production object aggregation paths.
    sealed class ExtendedHull : ShuttleShip
    {
        public int roleReads, weaponReads;
        public ExtendedHull(CompSpaceShuttle hull) : base(hull) { }
        public override SpaceObjectTag RolesIn(SpaceObject container)
        { roleReads++; return base.RolesIn(container); }
        protected override IEnumerable<CompSpaceWeapon> CollectWeapons()
        { weaponReads++; return base.CollectWeapons(); }
        public override IEnumerable<SpaceCollisionCell> CollisionCells => base.CollisionCells.Concat(
            new[] { new SpaceCollisionCell(new IntVec3(4, 0, 4), SpaceCellKind.Substructure, null) });
    }
    sealed class ExtendedHullComp : CompSpaceShuttle
    {
        ExtendedHull ship;
        public int discoveryReads;
        public override ShipBase Vessel { get { discoveryReads++; return ship ??= new ExtendedHull(this); } }
    }

    sealed class ScopedFoundation : FoundationShip
    {
        public Thing member;
        public ScopedFoundation(Ship ship) : base(ship) { }
        public override bool Contains(Thing thing) => thing == Anchor || thing == member;
        public override IEnumerable<IntVec3> HullCells => new[] { Anchor.Position, member.Position };
    }
    sealed class ProviderObject : MapSpaceObject
    {
        public ShipBase member;
        public ProviderObject(int id, Map map, SpacePose pose, ShipBase member) : base(id, map, pose)
        { this.member = member; }
        public override IEnumerable<ShipBase> Ships { get { yield return member; } }
    }

    static void CheckShipCapabilities(UniverseManager universe, Faction player)
    {
        Map map = TestMap(1100);
        var hull = new ExtendedHullComp { props = new CompProperties_SpaceShuttle
        {
            virtualThrusters = new List<ShuttleThrusterConfig>
            {
                new ShuttleThrusterConfig { position = new Vector2(1, 0), force = new Vector2(0, 17) },
                new ShuttleThrusterConfig { position = new Vector2(-1, 0), force = new Vector2(0, -9) }
            }
        }};
        AddTestHull(map, player, 1101, hull);
        var ship = (ExtendedHull)hull.Vessel;
        var obj = new MapSpaceObject(1100, map, new SpacePose(Vector3.forward, 0));
        universe.objects.Add(obj);
        Check(obj.baseTag == SpaceObjectTag.Container && ship.CanDriveIn(obj)
            && obj.HasTag(SpaceObjectTag.Ship | SpaceObjectTag.Shuttle) && obj.InterceptionEligible,
            "hull interface supplies the cached Shuttle PD role without a special object type");
        int reads = ship.roleReads;
        for (int i = 0; i < 100; i++) _ = obj.InterceptionEligible;
        Check(ship.roleReads == reads, "repeated point-defense filters read object tags without rescanning ships");
        Check(!obj.Weapons.Any() && !ship.Weapons.Any(), "default shuttle exposes an empty weapon capability");
        var members = obj.Ships;
        _ = ShipRegistry.ShipByIdentity(ship.Identity);
        int discoveries = hull.discoveryReads, weaponReads = ship.weaponReads;
        for (int i = 0; i < 100; i++)
        {
            _ = obj.Weapons.Count();
            _ = ShipRegistry.ShipByIdentity(ship.Identity);
        }
        Check(ReferenceEquals(members, obj.Ships) && hull.discoveryReads == discoveries
            && ship.weaponReads == weaponReads,
            "repeated identity and weapon queries reuse member discovery and equipment caches");
        Check(ship.Transfer.console == null && ship.Transfer.body == hull.parent
            && ship.Transfer.ownerId == 0 && !ship.Transfer.includeTerrain
            && ship.Transfer.pocketSize.Value.x == hull.parent.def.Size.x + 8,
            "shuttle transfer provider supplies body, terrain policy and pocket size through ShipBase");
        var transition = (ShipFlightTransitionService)Raw(typeof(ShipFlightTransitionService));
        Set(typeof(ShipFlightTransitionService), transition, "dockingCleanupMaps",
            new HashSet<Map>());
        transition.RegisterDockingCleanup(map);
        Check(transition.HasLandedTargetAwaitingCleanup && transition.FreeFlightTransitionPending(),
            "shuttle transfer holds the common lifecycle gate until source cleanup");
        transition.FinishDockingCleanup(map);
        Check(!transition.FreeFlightTransitionPending(), "cleared transfer releases the common lifecycle gate");
        var layout = obj.MotionInfo.thrusters.ToList();
        Vector2 center = ship.HullCenter - SpaceTransform.ShipLocalOrigin(map);
        Check(layout.Count == 2 && Math.Abs(layout[0].thrust - 17) < 0.001f
            && Math.Abs(layout[1].thrust - 9) < 0.001f
            && (layout[0].localPos - center - SpaceTransform.Rotate(new Vector2(1, 0), ship.FrameAngle)).sqrMagnitude < 0.0001f,
            "object physics consumes the hull XML layout with its actual frame and asymmetric forces");
        var numeric = DriveSnapshot.Capture(obj.MotionInfo, obj.PrimaryDriver);
        Check(numeric.positions.SequenceEqual(layout.Select(t => t.localPos))
            && numeric.forces.SequenceEqual(layout.Select(t => t.thrust)),
            "archive captures the same provider layout used by live object physics");
        hull.ShuttleProps.virtualThrusters[0].force = new Vector2(0, 31);
        SubstructureOwnership.BumpStructureVersion();
        Check(Math.Abs(obj.MotionInfo.thrusters.First().thrust - 31) < 0.001f,
            "structure invalidation rebuilds provider propulsion instead of retaining a stale virtual array");

        var gun = new CompSpaceWeapon { parent = hull.parent, props = new CompProperties_SpaceWeapon() };
        hull.parent.AllComps.Add(gun);
        _ = obj.Weapons.Count();
        SubstructureOwnership.BumpStructureVersion();
        Check(obj.Weapons.Single() == gun && SpaceWeaponService.WeaponsOn(ship).Single() == gun,
            "mod-added hull weapon reaches both object aggregation and the shared ship fire-control entry");
        var other = AddTestBuilding(map, player, 1102, TestBuildingDef("debris"), new EmptyComp());
        map.thingGrid.Deregister(other);
        Set(typeof(Thing), other, "positionInt", new IntVec3(3, 0, 1));
        map.thingGrid.Register(other);
        SubstructureOwnership.BumpStructureVersion();
        Check(!ship.CanDriveIn(obj) && !obj.InterceptionEligible && !obj.BuildThrusters().Any(),
            "hull sharing a host cannot drive it or mark the host as a Shuttle PD target");
        var occupied = new List<IntVec3>();
        SpaceCollision.CollectDebugSurface(obj, occupied, out _, out _);
        Check(occupied.Contains(ship.Anchor.Position) && occupied.Contains(other.Position)
            && occupied.Contains(new IntVec3(4, 0, 4)) && occupied.Count == 3,
            "shared collision surface merges subclass geometry and unowned environmental buildings once");
        map.thingGrid.Deregister(other);
        map.listerThings.AllThings.Remove(other);
        SubstructureOwnership.BumpStructureVersion();
        Check(obj.InterceptionEligible && obj.MotionInfo.thrusters.Count() == 2,
            "host removal refreshes driver eligibility and object role and propulsion caches together");

        // Scope is supplied by a hull adapter; the real foundation provider must honor it.
        Map foundationMap = TestMap(1200);
        CompDeltaVPilotConsole console = AddTestConsole(foundationMap, player, 1201);
        var engineDef = TestBuildingDef("engine");
        engineDef.comps.Add(new CompProperties_GravshipThruster());
        var engine = AddTestBuilding(foundationMap, player, 1202, engineDef,
            new CompThrusterPowerLimit { powerPercent = 25 });
        foundationMap.thingGrid.Deregister(engine);
        Set(typeof(Thing), engine, "positionInt", new IntVec3(2, 0, 2));
        foundationMap.thingGrid.Register(engine);
        var orphan = AddTestBuilding(foundationMap, player, 1203, engineDef, new EmptyComp());
        foundationMap.thingGrid.Deregister(orphan);
        Set(typeof(Thing), orphan, "positionInt", new IntVec3(4, 0, 0));
        foundationMap.thingGrid.Register(orphan);
        var foundation = new ScopedFoundation(console.Ship) { member = engine };
        var foundationObject = new ProviderObject(1200, foundationMap, obj.pose, foundation);
        universe.objects.Add(foundationObject);
        SubstructureOwnership.BumpStructureVersion();
        Check(!foundationObject.InterceptionEligible && foundationObject.HasTag(SpaceObjectTag.Ship)
            && !foundationObject.HasTag(SpaceObjectTag.Shuttle),
            "foundation ships retain Ship role and are excluded from point-defense targets");
        Check(foundation.Transfer.console == console.parent && foundation.Transfer.body == null
            && foundation.Transfer.ownerId == console.shipId && foundation.Transfer.includeTerrain
            && !foundation.Transfer.pocketSize.HasValue,
            "foundation transfer provider supplies its console and ownership through the same contract");
        ShipBase cachedFoundation = console.Vessel;
        Check(ReferenceEquals(cachedFoundation, console.Vessel)
            && ReferenceEquals(cachedFoundation, (ShipBase)console.Ship),
            "structural snapshot conversion preserves the common vessel adapter and its caches");
        var installed = foundationObject.MotionInfo.thrusters.ToList();
        Check(installed.Count == 1 && installed[0].thing == engine
            && Math.Abs(installed[0].thrust - ShipPhysics.ThrusterThrust * 0.25f) < 0.01f,
            "foundation provider exposes only its installed thruster and preserves the real power limit");
        var replacementMap = TestMap(1300);
        Set(typeof(Thing), console.parent, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(replacementMap));
        ShipBase relocated = console.Vessel;
        Check(!cachedFoundation.IsValid && relocated.IsValid && relocated.Map == replacementMap
            && !ReferenceEquals(relocated, cachedFoundation),
            "cross-map transfer replaces the stale foundation snapshot while preserving its identity");
        universe.objects.Clear();
        Find.Maps.Clear();
        ShipRegistry.ClearCaches();
        ShipPhysics.ClearCache();
        SpaceCollision.ClearCaches();
    }
}
