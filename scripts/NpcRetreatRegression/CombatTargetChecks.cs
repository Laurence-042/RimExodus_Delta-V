using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    // Supply changed hull scopes through the public provider contract; target selection,
    // object physics, faction resolution, fire intent and intercept code are production code.
    sealed class CombatHull : FoundationShip
    {
        public readonly HashSet<IntVec3> cells = new HashSet<IntVec3>();
        public CombatHull(Ship ship) : base(ship) { }
        public override IEnumerable<IntVec3> HullCells => cells;
        public override Vector2 HullCenter => SpaceTransform.BoundsCenterOfCells(cells);
        public override bool Contains(Thing thing) => thing?.Map == Map && cells.Contains(thing.Position);
    }
    sealed class CombatObject : MapSpaceObject
    {
        public readonly List<ShipBase> members = new List<ShipBase>();
        public CombatObject(int id, Map map, SpacePose pose) : base(id, map, pose) { }
        public override IEnumerable<ShipBase> Ships => members.Where(ship => ship.IsValid);
    }

    static void CheckCombatTargets(UniverseManager universe, Faction player, Faction enemy)
    {
        var sourceMap = TestMap(1900);
        var own = new MapSpaceObject(1900, sourceMap, new SpacePose(Vector3.forward, 0f));
        var attacker = new CombatHull(AddTestConsole(sourceMap, player, 1901).Ship);
        attacker.cells.Add(new IntVec3(1, 0, 2));
        var map = TestMap(1910);
        var victim = new CombatObject(1910, map,
            new SpacePose(OrbitSphere.Move(own.pose.position, new Vector2(60f, 0f)), 0f));
        universe.objects.Add(own);
        universe.objects.Add(victim);
        var console = AddTestConsole(map, enemy, 1911);
        var hull = new CombatHull(console.Ship);
        victim.members.Add(hull);
        for (int x = 1; x <= 4; x++)
        {
            hull.cells.Add(new IntVec3(x, 0, 2));
        }
        var engineDef = TestBuildingDef("combatEngine");
        engineDef.comps.Add(new CompProperties_GravshipThruster());
        var engine = AddTestBuilding(map, enemy, 1912, engineDef, new EmptyComp());
        MoveCombatFixture(engine, new IntVec3(2, 0, 2));
        var detachedEngine = AddTestBuilding(map, enemy, 1913, engineDef, new EmptyComp());
        MoveCombatFixture(detachedEngine, new IntVec3(4, 0, 2));
        Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll();
        ShipRegistry.ShipByIdentity(attacker.Identity);
        foreach (ShipBase member in victim.Ships) universe.Controls.For(member);
        ShipBase selected = ShipFireControl.Candidates(attacker, own, victim).FirstOrDefault();
        Check(selected?.Identity == console.Vessel.Identity, "intact hostile foundation ship is an automatic target");
        Vector2 intactCenter = selected.HullCenter;
        var fire = new ShipFireControl(universe, attacker.Identity);
        if (fire.Strategy(attacker).targetRules.Count == 0)
            fire.Strategy(attacker).targetRules.Add(new CombatStrategyRule { includeDisarmed = true });
        fire.Decide(attacker, own);
        Check(fire.TryGetIntent(attacker, own, false, out var intactIntent), "automatic target produces a volley intent");

        // Break the connecting foundation while leaving the wreck on the same Map.
        hull.cells.RemoveWhere(cell => cell.x >= 3);
        Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll();
        ShipRegistry.ShipByIdentity(attacker.Identity);
        foreach (ShipBase member in victim.Ships) universe.Controls.For(member);
        Check(fire.TryGetIntent(attacker, own, false, out var splitIntent)
            && splitIntent.target.TryResolve(universe, out var splitTarget)
            && OrbitSphere.Offset(splitTarget.Pose.position, ShipRegistry.ShipPose(hull).position).magnitude < 0.001f
            && hull.HullCenter != intactCenter && !hull.Contains(detachedEngine),
            "locked volley immediately follows the console-connected hull instead of same-map debris");
        SpacePose pointPose = ShipRegistry.ShipPose(hull);
        Check(OrbitSphere.ArcCells(pointPose.position, SpaceTransform.MapPointToUniverse(victim, map,
            hull.HullCenter)) < 0.001f,
            "lead marker anchor and volley target use the same current hull point");
        victim.pose.angularVelocity = 0.5f;
        pointPose = ShipRegistry.ShipPose(hull);
        Check(SpaceWeaponBallistics.TrySolveIntercept(own.pose.position, own.pose, pointPose, 100f, out var intercept)
            && OrbitSphere.Offset(pointPose.position, intercept).magnitude > 0.01f,
            "off-center rotating hull contributes rigid point velocity to shared intercept prediction");

        // Put the remaining usable engine in a different, friendly ship on this same Map.
        RemoveFixtureBuilding(engine, map);
        var friendlyConsole = AddTestConsole(map, player, 1914);
        MoveCombatFixture(friendlyConsole.parent, new IntVec3(4, 0, 2));
        var friendlyHull = new CombatHull(friendlyConsole.Ship);
        friendlyHull.cells.Add(new IntVec3(4, 0, 2));
        victim.members.Add(friendlyHull);
        Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll();
        ShipRegistry.ShipByIdentity(attacker.Identity);
        foreach (ShipBase member in victim.Ships) universe.Controls.For(member);
        Check(victim.MotionInfo.AvailableThrust > 0f
            && ShipFireControl.Candidates(attacker, own, victim).Any()
            && fire.TryGetIntent(attacker, own, false, out _),
            "powerless hostile hull remains attackable independently of friendly member thrust");
        fire.Decide(attacker, own);
        Check(fire.Target.HasLock && NpcCombatAi.CountEligibleTargets(universe, attacker, own) == 1,
            "NPC selection and diagnostics retain stationary targets through the shared gate");

        // A same-facing replacement still defines a ship; no primary defines only wreckage.
        SetFixtureFaction(friendlyConsole.parent, enemy);
        Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll();
        ShipRegistry.ShipByIdentity(attacker.Identity);
        foreach (ShipBase member in victim.Ships) universe.Controls.For(member);
        Check(ShipFireControl.Candidates(attacker, own, victim).Count() == 2,
            "mixed-map selection includes both hostile members regardless of propulsion");
        console.isMain = friendlyConsole.isMain = false;
        victim.faction = enemy;
        Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll();
        ShipRegistry.ShipByIdentity(attacker.Identity);
        foreach (ShipBase member in victim.Ships) universe.Controls.For(member);
        Check(!ShipFireControl.Candidates(attacker, own, victim).Any(),
            "hostile environment affiliation and remaining engines cannot turn console-free debris into a target");
        friendlyConsole.isMain = true;
        Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll();
        ShipRegistry.ShipByIdentity(attacker.Identity);
        foreach (ShipBase member in victim.Ships) universe.Controls.For(member);
        Check(ShipFireControl.Candidates(attacker, own, victim).Any(),
            "valid replacement primary restores target eligibility");

        CheckTargetContracts(universe, attacker, own, victim, friendlyConsole, friendlyHull, detachedEngine, player, enemy);

        universe.objects.Clear();
        universe.Controls.Reset();
        Find.Maps.Clear();
        ShipRegistry.ClearCaches();
        ShipPhysics.ClearCache();
        SpaceCollision.ClearCaches();

        var game = Current.Game;
        int componentIndex = game.components.IndexOf(universe);
        var prefabUniverse = new UniverseManager(game);
        game.components[componentIndex] = prefabUniverse;
        Rand.PushState();
        try { CheckNpcHostileTractorTarget(prefabUniverse, player, enemy); }
        finally
        {
            prefabUniverse.Controls.Reset();
            Find.Maps.Clear();
            ShipRegistry.ClearCaches();
            ShipPhysics.ClearCache();
            SpaceCollision.ClearCaches();
            Rand.PopState();
            game.components[componentIndex] = universe;
        }
    }

    static void CheckNpcHostileTractorTarget(UniverseManager universe, Faction player, Faction enemy)
    {
        var sourceMap = TestMap(1940);
        var targetMap = TestMap(1950);
        var sourceConsole = AddTestConsole(sourceMap, enemy, 1941);
        var targetConsole = AddTestConsole(targetMap, player, 1951);
        var backup = AddTestConsole(sourceMap, enemy, 1942);
        backup.shipId = sourceConsole.shipId;
        backup.isMain = false;
        foreach (var console in new[] { sourceConsole, backup, targetConsole })
        {
            console.combat.fireControlMode = ShipFireControlMode.Disabled;
            console.combat.weaponGroupsMain = 0;
            console.combat.weaponGroupsSecondary = 0;
        }
        var attacker = new CombatHull(sourceConsole.Ship);
        var target = new CombatHull(targetConsole.Ship);
        attacker.cells.Add(sourceConsole.parent.Position);
        target.cells.Add(targetConsole.parent.Position);
        var own = new CombatObject(1940, sourceMap, new SpacePose(Vector3.forward, 0f));
        var victim = new CombatObject(1950, targetMap,
            new SpacePose(OrbitSphere.Move(own.pose.position, new Vector2(60f, 0f)), 0f)) { faction = player };
        own.members.Add(attacker);
        victim.members.Add(target);
        universe.objects.Add(own);
        universe.objects.Add(victim);

        var projectile = TestBuildingDef("npcTractorTargetBullet");
        projectile.comps.Add(new CompProperties_SpaceProjectile { speed = 100f });
        var gun = new CompSpaceWeapon { parent = sourceConsole.parent, safeFire = false,
            props = new CompProperties_SpaceWeapon { projectileDef = projectile, range = 500f, topTurret = true },
            salvoSet = SpaceWeaponSalvoSet.Secondary, groupNumber = 6 };
        var tractor = new CompSpaceWeapon { parent = targetConsole.parent,
            props = new CompProperties_SpaceWeapon { tractor = true, manualOnly = true } };
        // Observe a real fire request without leaving a spawned round in the next fixture.
        AddFixtureComponents(sourceConsole.parent, gun, new CompMannable());
        AddFixtureComponents(targetConsole.parent, tractor);
        BindWeaponOwnerFixture(gun, attacker);
        BindWeaponOwnerFixture(tractor, target);
        MapPrefab.InitializeNpcCombat(sourceMap, new Thing[] { sourceConsole.parent, backup.parent });
        MapPrefab.InitializeNpcCombat(targetMap, new Thing[] { targetConsole.parent });
        Check(new[] { sourceConsole, backup }.All(console =>
            console.fireControlMode == ShipFireControlMode.Automatic
            && console.combat.weaponGroupsMain == 63 && console.combat.weaponGroupsSecondary == 63),
            "NPC prefab initialization enables automatic fire and all twelve groups on primary and backup consoles");
        Check(targetConsole.fireControlMode == ShipFireControlMode.Disabled
            && targetConsole.combat.weaponGroupsMain == 0 && targetConsole.combat.weaponGroupsSecondary == 0,
            "player prefab retains its saved fire control and weapon groups");
        var npc = (NpcShipController)universe.Controls.For(attacker);
        var fire = npc.FireControl;
        universe.Controls.For(target);
        Check(target.Weapons.Single() == tractor && !ShipFireControl.Armed(target),
            "NPC target fixture has only a manual tractor beam");
        npc.Ai.Tick(npc);
        Check(fire.Target.Ship == target.Identity, "default NPC decision locks a hostile tractor-only ship");
        Check(sourceConsole.combat.weaponsArmed && universe.Autopilot.IsCombatOrbit(attacker.Identity),
            "NPC tactical AI arms and starts automatic combat maneuvering against the tractor-only ship");
        var fireCommands = typeof(DeltaVPerformanceProbe).GetField("fireCommands", Private);
        bool previousPerformance = DeltaVMod.CurrentSettings.logPerformance;
        try
        {
            DeltaVMod.CurrentSettings.logPerformance = true;
            int requestsBefore = (int)fireCommands.GetValue(universe.Performance);
            bool hasInterceptionTargets = false;
            npc.TickCombat(ref hasInterceptionTargets, hasScatterTargets: false);
            Check((int)fireCommands.GetValue(universe.Performance) > requestsBefore,
                "NPC combat tick submits a fire request from the restored secondary group six");
        }
        finally { DeltaVMod.CurrentSettings.logPerformance = previousPerformance; }
        var assignments = (IDictionary)typeof(ShipFireControl).GetField("automaticTargets", Private).GetValue(fire);
        Check(assignments[gun.Identity] is CombatTarget assigned && assigned.Ship == target.Identity,
            "default NPC auto volley assigns its gun to the hostile tractor-only ship");

        var lockOnTarget = fire.Target;
        SetFixtureFaction(targetConsole.parent, enemy);
        fire.Decide(attacker, own);
        Check(!fire.Target.HasLock && !fire.TryResolveAutomatic(attacker, own, lockOnTarget, out _),
            "a non-hostile current main console invalidates NPC selection and the existing weapon lock");

        SetFixtureFaction(targetConsole.parent, player);
        fire.Decide(attacker, own);
        Check(fire.Target == lockOnTarget, "NPC reacquires the ship when its current main console is hostile again");
        targetConsole.isMain = false;
        Current.Game.GetComponent<UniverseManager>().ShipStructure.Changed(targetMap);
        fire.Decide(attacker, own);
        Check(!fire.Target.HasLock && !fire.TryResolveAutomatic(attacker, own, lockOnTarget, out _)
            && victim.HostileTo(enemy), "losing the only main console stops NPC targeting despite hostile container faction");
        npc.SetCombatReady(false);
    }
}
