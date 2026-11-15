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
        Set(enemy, "relations", new List<FactionRelation> { new FactionRelation { other = player, kind = FactionRelationKind.Hostile } });
        Set(player, "relations", new List<FactionRelation> { new FactionRelation { other = enemy, kind = FactionRelationKind.Hostile } });
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
        Check((bool)HarmonyLib.AccessTools.Method(typeof(SpaceWeaponShot), "HasClearPath").Invoke(null,
            new object[] { weapon, shot.muzzleMap, shot.launchMapAngle }),
            "hull safety checks allow a console-free intrinsic weapon without foundation ownership");
        var landing = new LandingApproachController.DockingPlan(PilotTarget.Of(singleBuilding), container,
            new IntVec3(1, 0, 1), new IntVec3(2, 0, 2), Rot4.North);
        Check(landing.Target.Identity == singleBuilding.Identity && landing.structureId == 0
            && landing.targetMap == map && landing.landingCell == new IntVec3(2, 0, 2),
            "command-local placement retains shuttle identity and its sole integer landing root");
        foreach (Rot4 rotation in Rot4.AllRotations)
        {
            var rebased = new LandingApproachController.DockingPlan(PilotTarget.Of(singleBuilding), container,
                landing.sourceRoot, landing.landingCell, rotation);
            foreach (IntVec3 capturedRoot in new[] { IntVec3.Zero, new IntVec3(-3, 0, 7), new IntVec3(4, 0, -2) })
            {
                IntVec3 cell = new IntVec3(8, 0, 9);
                Check(rebased.landingCell + PrefabUtility.GetAdjustedLocalPosition(cell - rebased.sourceRoot, rotation)
                    == rebased.PlacementRootFor(capturedRoot) + PrefabUtility.GetAdjustedLocalPosition(cell - capturedRoot, rotation),
                    "whole-map EVA capture consumes the exact registered landing grid without re-rounding");
                IntVec3 hostCell = rebased.PlacementRootFor(cell);
                Check(rebased.InversePlacementRootFor(hostCell) == cell,
                    "common worksite inverts the frozen placement exactly in the initiating ship grid");
                IntVec3 hostOrigin = new IntVec3(-6, 0, 11);
                Check(rebased.InversePlacementRootFor(hostOrigin)
                    + PrefabUtility.GetAdjustedLocalPosition(hostCell - hostOrigin, rebased.InverseRotation) == cell,
                    "reverse whole-map capture keeps initiating ship orientation for all four grid angles");
            }
        }
        singleBuilding.Combat.weaponsArmed = true;
        singleBuilding.Combat.fireControlMode = ShipFireControlMode.Manual;
        singleBuilding.Combat.SetWeaponGroupMask(SpaceWeaponSalvoSet.Main, 4);
        Check(singleBuilding.Combat.weaponsArmed && hull.Vessel.Combat.weaponsArmed
            && !ordinary.Combat.weaponsArmed
            && hull.Vessel.Combat.fireControlMode == ShipFireControlMode.Manual
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
        var soloMap = TestMap(801);
        var solo = new MapSpaceObject(801, soloMap, container.pose) { faction = enemy };
        universe.objects.Add(solo);
        RemoveFixtureBuilding((Building)hull.parent, map);
        PlaceFixtureBuilding((Building)hull.parent, soloMap);
        hull.ShuttleProps.virtualThrusters = new List<ShuttleThrusterConfig>
        {
            new ShuttleThrusterConfig { force = new Vector2(10f, 0f) }
        };
        Map attackerMap = TestMap(951);
        var source = new MapSpaceObject(950, attackerMap, container.pose);
        universe.objects.Add(source);
        var attacker = new CombatHull(AddTestConsole(attackerMap, enemy, 952).Ship);
        attacker.cells.Add(new IntVec3(1, 0, 2));
        Check(ShipFireControl.Candidates(attacker, source, solo).Any(),
            "automatic fire accepts an enemy shuttle as a Ship target");
        universe.objects.Remove(source);
        Check(!solo.HostileTo(player), "stale hostile environment faction cannot override a friendly ship member");
        var numeric = DriveSnapshot.Capture(new DeltaV.DriveInfo(new ShipPhysics.MassReport { mass = 100, inertia = 1000 }),
            singleBuilding.Identity.key, singleBuilding.Identity.shuttle);
        var unloaded = new ArchivedSpaceObject { objectId = 970, dynamics = numeric };
        Check(unloaded.PrimaryDriver == singleBuilding.Identity
            && unloaded.TryGetDrive(singleBuilding.Identity, out _)
            && !unloaded.TryGetDrive(ordinary.Identity, out _),
            "archived shuttle dynamics retain typed identity and reject an equal numeric foundation ID");
        solo.projectileRoleOverride = true;
        Check(solo.HasTag(SpaceObjectTag.Ship | SpaceObjectTag.Projectile)
            && solo.IsActiveCollider && solo.AppliesPassiveDrag && !solo.IsTransient
            && universe.PlayerReferenceObjects().Contains(solo),
            "intrusion adds projectile behavior while preserving player ship membership and persistence");
        var target = new SpaceObject(900, new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(100, 0)), 0));
        Check(Math.Abs(universe.Lifecycle.MinPlayerDistance(target) - 100) < 0.1,
            "POI distance uses the solo shuttle container position");
        var snapshot = universe.PlayerReferenceObjects();
        universe.objects.Add(new SpaceObject(901, target.pose) { baseTag = SpaceObjectTag.Projectile, faction = player });
        Check(snapshot.Count == 1 && universe.PlayerReferenceObjects().Count == 1,
            "ordinary projectiles are excluded and the reference enumeration is a stable snapshot");
        solo.projectileRoleOverride = false;
        console.isMain = true;
        RemoveFixtureBuilding((Building)hull.parent, soloMap);
        PlaceFixtureBuilding((Building)hull.parent, map);
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

    static Map TestMap(int id, int width = 5)
    {
        var map = (Map)Raw(typeof(Map));
        map.uniqueID = id;
        map.info = new MapInfo { Size = new IntVec3(width, 1, width), parent = (ShipPocketParent_DeltaV)Raw(typeof(ShipPocketParent_DeltaV)) };
        map.cellIndices = new CellIndices(map);
        map.designationManager = new DesignationManager(map);
        map.components = new List<MapComponent> { new SubstructureOwnership(map) };
        map.terrainGrid = new TerrainGrid(map);
        // These generic fixtures model supported stations. Structure-loss tests replace this terrain.
        var ground = (TerrainDef)Raw(typeof(TerrainDef));
        ground.defName = "regressionGround";
        map.terrainGrid.topGrid[0] = ground;
        map.thingGrid = new ThingGrid(map);
        map.listerThings = new ListerThings(ListerThingsUse.Global);
        map.spawnedThings = new ThingOwner<Thing>(map);
        map.floodFiller = new FloodFiller(map);
        Find.Maps.Add(map);
        return map;
    }

    static Building AddTestBuilding(Map map, Faction faction, int id, ThingDef def, ThingComp comp, Building body = null)
    {
        body ??= (Building)Raw(typeof(Building));
        body.def = def; body.thingIDNumber = id;
        Set(typeof(Thing), body, "positionInt", new IntVec3(1, 0, 2));
        Set(typeof(Thing), body, "factionInt", faction);
        Set(typeof(ThingWithComps), body, "comps", new List<ThingComp> { comp });
        comp.parent = body;
        PlaceFixtureBuilding(body, map, notify: false);
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
        AddTestBuilding(map, faction, id, TestBuildingDef("shuttle"), comp);
        return comp;
    }
}
