using System;
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
        SubstructureOwnership.BumpStructureVersion();
        ShipRegistry.ShipByIdentity(attacker.Identity);
        foreach (ShipBase member in victim.Ships) universe.Controls.For(member);
        ShipBase selected = ShipFireControl.Candidates(attacker, own, victim).FirstOrDefault();
        Check(selected?.Identity == console.Vessel.Identity, "intact hostile foundation ship is an automatic target");
        Vector2 intactCenter = selected.HullCenter;
        var fire = new ShipFireControl(universe, attacker.Identity);
        fire.Decide(attacker, own);
        Check(fire.TryGetIntent(attacker, own, false, out var intactIntent), "automatic target produces a volley intent");

        // Break the connecting foundation while leaving the wreck on the same Map.
        hull.cells.RemoveWhere(cell => cell.x >= 3);
        SubstructureOwnership.BumpStructureVersion();
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
        Check(ShipFireControl.TrySolveIntercept(own.pose.position, own.pose, pointPose, 100f, out var intercept)
            && OrbitSphere.Offset(pointPose.position, intercept).magnitude > 0.01f,
            "off-center rotating hull contributes rigid point velocity to shared intercept prediction");

        // Put the remaining usable engine in a different, friendly ship on this same Map.
        Set(typeof(Thing), engine, "mapIndexOrState", (sbyte)-1);
        map.listerThings.AllThings.Remove(engine);
        map.thingGrid.Deregister(engine);
        var friendlyConsole = AddTestConsole(map, player, 1914);
        MoveCombatFixture(friendlyConsole.parent, new IntVec3(4, 0, 2));
        var friendlyHull = new CombatHull(friendlyConsole.Ship);
        friendlyHull.cells.Add(new IntVec3(4, 0, 2));
        victim.members.Add(friendlyHull);
        SubstructureOwnership.BumpStructureVersion();
        ShipRegistry.ShipByIdentity(attacker.Identity);
        foreach (ShipBase member in victim.Ships) universe.Controls.For(member);
        Check(victim.MotionInfo.AvailableThrust > 0f
            && !ShipFireControl.Candidates(attacker, own, victim).Any()
            && !fire.TryGetIntent(attacker, own, false, out _),
            "friendly member thrust cannot keep a locked powerless enemy in automatic targeting");
        fire.Decide(attacker, own);
        Check(!fire.Target.HasLock && NpcCombatAi.CountEligibleTargets(universe, attacker, own) == 0,
            "NPC selection and diagnostics drop disabled targets through the shared gate");

        // A same-facing replacement still defines a ship; no primary defines only wreckage.
        Set(typeof(Thing), friendlyConsole.parent, "factionInt", enemy);
        SubstructureOwnership.BumpStructureVersion();
        ShipRegistry.ShipByIdentity(attacker.Identity);
        foreach (ShipBase member in victim.Ships) universe.Controls.For(member);
        Check(ShipFireControl.Candidates(attacker, own, victim).FirstOrDefault()?.Identity == friendlyConsole.Vessel.Identity,
            "mixed-map selection chooses the hostile member that retains propulsion");
        console.isMain = friendlyConsole.isMain = false;
        victim.faction = enemy;
        SubstructureOwnership.BumpStructureVersion();
        ShipRegistry.ShipByIdentity(attacker.Identity);
        foreach (ShipBase member in victim.Ships) universe.Controls.For(member);
        Check(!ShipFireControl.Candidates(attacker, own, victim).Any(),
            "hostile environment affiliation and remaining engines cannot turn console-free debris into a target");
        friendlyConsole.isMain = true;
        SubstructureOwnership.BumpStructureVersion();
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
    }

    static void MoveCombatFixture(Thing thing, IntVec3 position)
    {
        Map map = thing.Map;
        map.thingGrid.Deregister(thing);
        Set(typeof(Thing), thing, "positionInt", position);
        map.thingGrid.Register(thing);
    }
}
