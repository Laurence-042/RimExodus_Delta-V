using System;
using System.Collections;
using System.Collections.Generic;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    sealed class DrivePowerProbe : IThrusterPowerModel
    {
        internal int reads;
        internal bool online = true;
        public bool IsOperational(Thing thing) { reads++; return online; }
    }

    sealed class DriveExecutionProbe : SpaceObject
    {
        internal ShipPhysics.MassReport layout;
        internal int allocations;
        internal bool allowed = true;
        internal Vector2 force;
        internal float torque;
        internal DriveExecutionProbe(ShipPhysics.MassReport layout)
            : base(1990, new SpacePose(Vector3.forward, 0f)) { this.layout = layout; }
        protected override ShipPhysics.MassReport MotionReport => layout;
        protected override DriveResult AuthorizeDrive(DriveSubject subject, ShipPhysics.Command command, bool playerInitiated)
            => allowed ? DriveResult.Executed : DriveResult.WaitingForSeparation;
        protected override void AllocateDrive(ShipPhysics.MassReport report, ShipPhysics.Command command,
            out Vector2 force, out float torque)
        {
            allocations++;
            // Use the production numeric core, omitting only the Unity presentation timestamp.
            ShipPhysics.PreviewAllocation(report, command, out force, out torque);
            for (int i = 0; i < report.thrusters.Count; i++)
                report.thrusters[i].throttle = report.previewScratch[i];
            this.force = force; this.torque = torque;
        }
        internal DriveResult Step(DriveControl control) =>
            ExecuteDrive(layout, 0f, new DriveSubject(objectId, 1), control, 1f / 60f, out _);
    }

    static void CheckDriveExecutionReuse(Faction player)
    {
        IThrusterPowerModel previous = ThrusterPower.Model;
        bool previousDrag = DeltaVMod.CurrentSettings.passiveDrag;
        Map map = TestMap(1990);
        var engine = AddTestBuilding(map, player, 1991, TestBuildingDef("driveProbe"), new EmptyComp());
        var power = new DrivePowerProbe();
        try
        {
            ThrusterPower.Model = power;
            DeltaVMod.CurrentSettings.passiveDrag = false;
            var layout = new ShipPhysics.MassReport { mass = 100f, inertia = 1000f };
            layout.thrusters.Add(new ShipPhysics.Thruster
                { thing = engine, dir = Vector2.right, thrust = 2000f, operational = true });
            var obj = new DriveExecutionProbe(layout);
            var input = DriveControl.FromInput(new ShipPhysics.Command { fx = 1f }, false, false, false);
            void Next() => Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
            bool executed = true;
            for (int i = 0; i < 120; i++) { Next(); executed &= obj.Step(input) == DriveResult.Executed; }
            Check(executed && obj.allocations == 1 && power.reads == 120,
                "stable manual input reuses allocation for 120 ticks with one live power read per execution");
            Check(obj.pose.velocity.x > 39.9f && obj.pose.velocity.x < 40.1f
                && layout.thrusters[0].throttle > .99f && obj.PropulsionUsedThisTick,
                "reused allocation keeps integrating real thrust and retaining actual throttle");
            Check(obj.Step(input) == DriveResult.AlreadyAdvanced && power.reads == 120,
                "a second drive in the same tick neither rereads power nor integrates twice");
            power.online = false; Next(); obj.Step(input);
            Check(obj.allocations == 2 && obj.force == Vector2.zero && !obj.PropulsionUsedThisTick,
                "power loss invalidates a held allocation before this tick's thrust");
            Next(); var view = obj.MotionInfo;
            power.online = true; obj.Step(input);
            Check(obj.allocations == 3 && obj.force.x > 1990f && obj.PropulsionUsedThisTick,
                "execution rechecks a power change even after a drive view was read in the same tick");
            var half = DriveControl.FromInput(new ShipPhysics.Command { fx = .5f }, false, false, false);
            Next(); obj.Step(half);
            Check(obj.allocations == 4 && obj.force.x > 990f && obj.force.x < 1010f,
                "changed input immediately replaces the cached allocation");
            Next(); obj.Step(new DriveControl { force = new Vector2(1000f, 0f) });
            Check(obj.allocations == 4,
                "manual input and equivalent autopilot force share the same command and allocation cache");
            int reads = power.reads;
            float throttle = layout.thrusters[0].throttle, stamp = layout.throttlesWrittenAt;
            view.PreviewModel(); view.PreviewBraking(Vector2.left); view.PreviewAngular(1);
            Check(power.reads == reads && layout.thrusters[0].throttle == throttle && layout.throttlesWrittenAt == stamp,
                "numeric previews consume the prepared report without repeating state reads or changing actual output");
            obj.allowed = false; Next();
            Check(obj.Step(input) == DriveResult.WaitingForSeparation && obj.allocations == 4,
                "cached drive cannot bypass the current separation authorization");
            obj.allowed = true; Next(); obj.Step(input);
            Check(obj.allocations == 5, "resuming after denied thrust rebuilds its allocation");
            var changed = new ShipPhysics.MassReport { mass = 100f, inertia = 1000f };
            changed.thrusters.Add(new ShipPhysics.Thruster
                { dir = Vector2.right, thrust = 1000f, operational = true });
            obj.layout = changed; Next(); obj.Step(input);
            Check(obj.allocations == 6 && obj.force.x > 990f && obj.force.x < 1010f,
                "a rebuilt rigid-body report invalidates the allocation even with the same numeric version");
            obj.ClearDrive(); Next(); obj.Step(input);
            Check(obj.allocations == 7, "drive cancellation invalidates held output before resumption");
            Next(); obj.Step(DriveControl.FromInput(default, false, false, false));
            Check(obj.force == Vector2.zero && !obj.PropulsionUsedThisTick,
                "zero input stops thrust through the same allocation path");
        }
        finally
        {
            ThrusterPower.Model = previous;
            DeltaVMod.CurrentSettings.passiveDrag = previousDrag;
            Find.Maps.Remove(map);
        }
    }

    sealed class PdCandidateProbe : MapSpaceObject
    {
        internal int factionReads;
        internal PdCandidateProbe(int id, Map map, SpacePose pose) : base(id, map, pose)
        { baseTag = SpaceObjectTag.Projectile; }
        protected override IEnumerable<Faction> ShipFactions
        { get { factionReads++; yield return faction; } }
    }
    static int pdSearchCalls, pdAimCalls;
    static void CountPdSearch() { pdSearchCalls++; }
    static void CountPdAim() { pdAimCalls++; }

    static void CheckSharedPdCandidates(UniverseManager universe, Faction player, Faction enemy)
    {
        Map map = TestMap(1992), targetMap = TestMap(1993);
        var ship = new CombatHull(AddTestConsole(map, player, 19920).Ship);
        Set(ship.Structure.console, "vessel", ship);
        var ownership = new int[map.cellIndices.NumGridCells];
        Set(SubstructureOwnership.For(map), "ownerGrid", ownership);
        var deck = (TerrainDef)Raw(typeof(TerrainDef)); deck.isFoundation = true;
        var own = new CombatObject(1992, map, new SpacePose(Vector3.forward, 0f));
        own.members.Add(ship);
        var victim = new PdCandidateProbe(1993, targetMap,
            new SpacePose(OrbitSphere.Move(own.pose.position, new Vector2(0f, 60f)), 0f)) { faction = enemy };
        universe.objects.Add(own); universe.objects.Add(victim);
        Set(universe, "objectById", null); // Fixture insertion bypasses RegisterObject.
        Set(universe, "objectByMap", null);
        var weapons = new List<CompSpaceWeapon>();
        for (int i = 0; i < 3; i++)
        {
            var weapon = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon { range = 500f },
                aimMode = SpaceWeaponAimMode.PointDefense, cooldownUntilTick = int.MaxValue };
            var building = AddTestBuilding(map, player, 19921 + i, TestBuildingDef("pdProbe" + i), weapon);
            MoveCombatFixture(building, new IntVec3(i + 1, 0, 2));
            ship.cells.Add(building.Position); weapons.Add(weapon);
            ownership[map.cellIndices.CellToIndex(building.Position)] = ship.Identity.key;
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(building.Position)] = deck;
        }
        SubstructureOwnership.BumpStructureVersion();
        ShipRegistry.ShipByIdentity(ship.Identity); universe.Controls.For(ship);
        var fire = new ShipFireControl(universe, ship.Identity);
        var counting = new Harmony("DeltaV.Regression.SharedPdCandidates");
        counting.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
            prefix: new HarmonyMethod(typeof(Program), nameof(RegressionSubstructure)));
        counting.Patch(AccessTools.Method(typeof(ShipFireControl), "FindPdTarget"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountPdSearch)));
        counting.Patch(AccessTools.Method(typeof(SpaceWeaponService), "TargetingMuzzle"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountPdAim)));
        try
        {
            pdSearchCalls = pdAimCalls = 0;
            bool hasCandidate = fire.TickPointDefense(ship, own, false);
            Check(hasCandidate && victim.factionReads == 1
                && pdSearchCalls == 3 && pdAimCalls == 3,
                $"three point-defense weapons share one hostile-object filter when acquiring targets (candidate={hasCandidate}, factions={victim.factionReads}, searches={pdSearchCalls}, aims={pdAimCalls})");
            Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 60);
            pdSearchCalls = 0;
            fire.TickPointDefense(ship, own, false);
            Check(pdSearchCalls == 0, $"all weapons keep valid targets beyond the old periodic reselection deadline (searches={pdSearchCalls}, registered={universe.ObjectById(victim.objectId) == victim})");
            victim.faction = player; pdSearchCalls = pdAimCalls = 0;
            Check(!fire.TickPointDefense(ship, own, false) && pdSearchCalls == 0 && pdAimCalls == 0,
                "no hostile candidates returns before per-weapon aiming or searching");
            victim.faction = enemy;
            foreach (var weapon in weapons) weapon.WeaponProps.range = 1f;
            fire.TickPointDefense(ship, own, false);
            Check(pdSearchCalls == 3, "out-of-range weapons record an empty lock from the shared candidates");
            pdSearchCalls = pdAimCalls = 0;
            Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
            fire.TickPointDefense(ship, own, false);
            Check(pdSearchCalls == 0 && pdAimCalls == 0,
                "empty locks wait for their retry deadline instead of searching every tick");
        }
        finally
        {
            counting.UnpatchAll(counting.Id); fire.Dispose();
            universe.objects.Clear(); Find.Maps.Clear(); universe.Controls.Reset();
            ShipRegistry.ClearCaches(); ShipPhysics.ClearCache(); SpaceCollision.ClearCaches();
        }
    }
}
