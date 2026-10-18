using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeltaV;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

internal static partial class Program
{
    // Seed real vanilla grids/listers without running Unity rendering or GenSpawn.
    // The assertions call the production membership and distance paths.
    static void CheckObjectReferences(UniverseManager universe, Faction player, Faction enemy)
    {
        var factionManager = (FactionManager)Raw(typeof(FactionManager));
        Set(factionManager, "ofPlayer", player);
        Set(Find.World, "factionManager", factionManager);
        ThingDefOf.PilotConsole = TestBuildingDef("console");
        Map map = TestMap(800);
        CompDeltaVPilotConsole console = AddTestConsole(map, enemy, 810);
        var hull = AddTestHull(map, player, 820);
        var container = new MapSpaceObject(800, map, new SpacePose(Vector3.forward, 0f)) { faction = enemy };
        universe.objects.Add(container);
        ShipBase ordinary = ShipRegistry.FoundationShipsOnMap(map).Single();
        ShipBase singleBuilding = hull.Vessel;
        Check(ordinary.IsValid && singleBuilding.IsValid && ordinary.Map == singleBuilding.Map
            && ShipRegistry.AllShipsOnMap(map).Count() == 2, "ordinary and single-building ships share ShipBase enumeration");
        Check(container.HasTag(SpaceObjectTag.Ship) && container.HasFaction(player) && container.HasFaction(enemy),
            "mixed container exposes both member factions and the Ship role");
        Check(universe.PlayerReferenceObjects().SequenceEqual(new[] { container }),
            "multiple members produce one player reference object");
        Check(container.HasTag(SpaceObjectTag.Container) && ordinary.CanDriveIn(container) && !singleBuilding.CanDriveIn(container),
            "parked shuttle membership cannot drive its host container");
        hull.parent.thingIDNumber = console.shipId;
        Check(ordinary.Identity != singleBuilding.Identity
            && ShipRegistry.ShipByIdentity(ordinary.Identity) is FoundationShip
            && ShipRegistry.ShipByIdentity(singleBuilding.Identity) is ShuttleShip,
            "typed registry identities distinguish a hull ID equal to a foundation owner ID");
        var npcController = universe.Controls.For(ordinary) as NpcShipController;
        var playerController = universe.Controls.For(singleBuilding) as PlayerShipController;
        Check(npcController != null && playerController != null
            && ReferenceEquals(universe.Controls.For(singleBuilding), playerController)
            && npcController.OwnerId != playerController.OwnerId,
            "both ship implementations use the same persistent controller registry without ID aliasing");
        Check(ShipRegistry.DefaultVesselOnMap(map).Identity == singleBuilding.Identity
            && universe.PlayerShips().Single().Identity == singleBuilding.Identity,
            "default selection and world marker enumeration include the player shuttle");
        ShipStatusPanel.SelectShip(singleBuilding);
        Check(ShipStatusPanel.TryGetSelectedShip(map, out ShipBase selected) && selected.Identity == singleBuilding.Identity,
            "sidebar selection resolves the typed ship identity");
        var weapon = new CompSpaceWeapon { parent = hull.parent, props = new CompProperties_SpaceWeapon() };
        ((List<ThingComp>)typeof(ThingWithComps).GetField("comps", Private).GetValue(hull.parent)).Add(weapon);
        Check(weapon.OwnerShip.Identity == singleBuilding.Identity
            && SpaceWeaponService.WeaponsOn(singleBuilding).Contains(weapon)
            && !SpaceWeaponService.WeaponsOn(ordinary).Contains(weapon),
            "intrinsic shuttle weapons belong to the shuttle rather than the host foundation ship");
        var shot = new SpaceWeaponShot(universe, weapon, container, new WeaponFireRequest(container.pose.position))
            { muzzleMap = singleBuilding.HullCenter, launchMapAngle = 0f };
        Check((bool)Call(shot, "SafePathClear"),
            "hull safety checks allow a console-free intrinsic weapon without foundation ownership");
        var landing = new LandingApproachController.DockingPlan(PilotTarget.Of(singleBuilding), container, map,
            new IntVec3(2, 0, 2), Rot4.North, new Vector2(2, 2), 0.3f);
        landing.TargetPose(out Vector3 landingDirection, out float landingAngle);
        Check(landing.Target.Identity == singleBuilding.Identity && landing.structureId == 0
            && landing.PlanFootprint().SetEquals(singleBuilding.HullCells)
            && Math.Abs(landingAngle - 0.3f) < 0.0001f
            && OrbitSphere.ArcCells(landingDirection, SpaceTransform.MapPointToUniverse(container, map, new Vector2(2, 2))) < 0.01f,
            "common docking plan uses shuttle identity, footprint and the single registered landing pose");
        singleBuilding.Combat.fireControlMode = ShipFireControlMode.Manual;
        singleBuilding.Combat.SetWeaponGroupMask(SpaceWeaponSalvoSet.Main, 4);
        Check(hull.Vessel.Combat.fireControlMode == ShipFireControlMode.Manual
            && hull.Vessel.Combat.WeaponGroupMask(SpaceWeaponSalvoSet.Main) == 4
            && ordinary.Combat.fireControlMode == ShipFireControlMode.Automatic,
            "common fire control and weapon group state is anchored independently on each ship");
        console.isMain = false;
        Check(container.HasTag(SpaceObjectTag.Ship) && container.HasFaction(player),
            "single-building member keeps ship identity without a main console");
        Set(typeof(Thing), hull.parent, "factionInt", enemy);
        var shuttleNpc = universe.Controls.For(singleBuilding) as NpcShipController;
        Check(shuttleNpc != null && shuttleNpc.Ai.HasAuthority(singleBuilding)
            && !shuttleNpc.Ai.HasAuthority(ordinary),
            "captured shuttle receives full NPC tactical AI through the shared controller service");
        Check(ReferenceEquals(ShipRegistry.ShipByIdentity(singleBuilding.Identity), singleBuilding),
            "controller authority replacement does not evict the ship identity index");
        container.faction = player;
        Check(!universe.PlayerReferenceObjects().Any(), "losing the player member immediately removes the reference");
        Check(!container.HasFaction(player), "stale environment faction cannot override actual ship members");
        container.faction = enemy;
        Set(typeof(Thing), hull.parent, "factionInt", player);
        map.listerThings.AllThings.Remove(console.parent);
        map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole).Remove(console.parent);
        hull.ShuttleProps.virtualThrusters = new List<ShuttleThrusterConfig>
        {
            new ShuttleThrusterConfig { force = new Vector2(10f, 0f) }
        };
        SubstructureOwnership.BumpStructureVersion();
        Check(singleBuilding.CanDriveIn(container), "launched shuttle can drive its own container");
        Check(container.PrimaryDriver == singleBuilding.Identity,
            "primary driver resolves a console-free ship with its typed identity");
        Set(enemy, "relations", new List<FactionRelation> { new FactionRelation { other = player, kind = FactionRelationKind.Hostile } });
        Set(player, "relations", new List<FactionRelation> { new FactionRelation { other = enemy, kind = FactionRelationKind.Hostile } });
        Map attackerMap = TestMap(951);
        var source = new MapSpaceObject(950, attackerMap, container.pose);
        universe.objects.Add(source);
        var attacker = new CombatHull(AddTestConsole(attackerMap, enemy, 952).Ship);
        attacker.cells.Add(new IntVec3(1, 0, 2));
        Check(ShipFireControl.Candidates(attacker, source, container).Any(),
            "automatic fire accepts an enemy shuttle as a Ship target");
        universe.objects.Remove(source);
        Check(!container.HostileTo(player), "stale hostile environment faction cannot override a friendly ship member");
        var numeric = DriveSnapshot.Capture(new DeltaV.DriveInfo(new ShipPhysics.MassReport { mass = 100, inertia = 1000 }),
            singleBuilding.Identity.key, singleBuilding.Identity.shuttle);
        var unloaded = new ArchivedSpaceObject { objectId = 970, dynamics = numeric };
        Check(unloaded.PrimaryDriver == singleBuilding.Identity
            && unloaded.TryGetDrive(singleBuilding.Identity, out _)
            && !unloaded.TryGetDrive(ordinary.Identity, out _),
            "archived shuttle dynamics retain typed identity and reject an equal numeric foundation ID");
        container.projectileRoleOverride = true;
        Check(container.HasTag(SpaceObjectTag.Ship | SpaceObjectTag.Projectile)
            && container.IsActiveCollider && !container.AppliesPassiveDrag && !container.IsTransient
            && universe.PlayerReferenceObjects().Contains(container),
            "intrusion adds projectile behavior while preserving player ship membership and persistence");
        var target = new SpaceObject(900, new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(100, 0)), 0));
        Check(Math.Abs(universe.Lifecycle.MinPlayerDistance(target) - 100) < 0.1,
            "POI distance uses the solo shuttle container position");
        var snapshot = universe.PlayerReferenceObjects();
        universe.objects.Add(new SpaceObject(901, target.pose) { baseTag = SpaceObjectTag.Projectile, faction = player });
        Check(snapshot.Count == 1 && universe.PlayerReferenceObjects().Count == 1,
            "ordinary projectiles are excluded and the reference enumeration is a stable snapshot");
        container.projectileRoleOverride = false;
        console.isMain = true;
        map.listerThings.AllThings.Add(console.parent);
        map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole).Add(console.parent);
        SubstructureOwnership.BumpStructureVersion();
        var archived = new ArchivedSpaceObject { memberFactions = ShipRegistry.AllShipsOnMap(map).Select(ship => ship.Faction).ToList() };
        Check(archived.HasTag(SpaceObjectTag.Ship) && archived.HasFaction(player) && archived.HasFaction(enemy),
            "archived member affiliation retains ship identity without a live map");
        Check(container.HostileTo(player) && archived.HostileTo(player)
            && NpcShipAiService.ShipyardFaction(archived) == null,
            "live and archived mixed factions share hostility and cannot masquerade as a single-faction home");
        Set(typeof(Thing), hull.parent, "factionInt", enemy);
        var restored = archived.RestoreRepresentation(map);
        Check(!restored.HasFaction(player), "restoration reads actual ship members instead of the archived affiliation summary");
        universe.objects.Clear();
        ShipStatusPanel.ClearRuntimeState();
        universe.Controls.Reset();
        Find.Maps.Clear();
        ShipRegistry.ClearCaches();
        ShipPhysics.Forget(map);
    }

    static void Set(Type type, object owner, string name, object value) =>
        type.GetField(name, Private | BindingFlags.Public).SetValue(owner, value);

    static ThingDef TestBuildingDef(string name)
    {
        var def = (ThingDef)Raw(typeof(ThingDef));
        def.defName = name; def.category = ThingCategory.Building; def.size = new IntVec2(1, 1);
        def.shortHash = ++testDefHash;
        def.comps = new List<CompProperties>();
        def.building = new BuildingProperties();
        return def;
    }

    static ushort testDefHash = 100;

    static Map TestMap(int id)
    {
        var map = (Map)Raw(typeof(Map));
        map.uniqueID = id;
        map.info = new MapInfo { Size = new IntVec3(5, 1, 5), parent = (ShipPocketParent_DeltaV)Raw(typeof(ShipPocketParent_DeltaV)) };
        map.cellIndices = new CellIndices(map);
        map.components = new List<MapComponent>();
        map.terrainGrid = new TerrainGrid(map);
        map.thingGrid = new ThingGrid(map);
        map.listerThings = new ListerThings(ListerThingsUse.Global);
        map.floodFiller = new FloodFiller(map);
        Find.Maps.Add(map);
        return map;
    }

    static Building AddTestBuilding(Map map, Faction faction, int id, ThingDef def, ThingComp comp)
    {
        var body = (Building)Raw(typeof(Building));
        body.def = def; body.thingIDNumber = id;
        Set(typeof(Thing), body, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        Set(typeof(Thing), body, "positionInt", new IntVec3(1, 0, 2));
        Set(typeof(Thing), body, "factionInt", faction);
        Set(typeof(ThingWithComps), body, "comps", new List<ThingComp> { comp });
        comp.parent = body;
        var defs = (Dictionary<ThingDef, List<Thing>>)typeof(ListerThings).GetField("listsByDef", Private).GetValue(map.listerThings);
        if (!defs.TryGetValue(def, out var byDef)) defs[def] = byDef = new List<Thing>();
        byDef.Add(body);
        map.listerThings.AllThings.Add(body);
        map.thingGrid.Register(body);
        return body;
    }

    static CompDeltaVPilotConsole AddTestConsole(Map map, Faction faction, int id)
    {
        var comp = new CompDeltaVPilotConsole { isMain = true, shipId = id };
        AddTestBuilding(map, faction, id, ThingDefOf.PilotConsole, comp);
        return comp;
    }

    static CompSpaceShuttle AddTestHull(Map map, Faction faction, int id, CompSpaceShuttle comp = null)
    {
        comp ??= new CompSpaceShuttle { props = new CompProperties_SpaceShuttle() };
        var body = AddTestBuilding(map, faction, id, TestBuildingDef("shuttle"), comp);
        var groups = (List<Thing>[])typeof(ListerThings).GetField("listsByGroup", Private).GetValue(map.listerThings);
        (groups[(int)ThingRequestGroup.Transporter] ??= new List<Thing>()).Add(body);
        return comp;
    }
}
