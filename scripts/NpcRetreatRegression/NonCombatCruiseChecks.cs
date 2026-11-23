using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static int cruiseFactsBuilt, cruiseReferencesBuilt;
    static void CountCruiseFacts() => cruiseFactsBuilt++;
    static void CountCruiseReferences() => cruiseReferencesBuilt++;

    sealed class CruiseHull : FoundationShip
    {
        internal float thrust = 2000;
        internal int thrustReads;
        internal CruiseHull(Ship ship) : base(ship) { }
        public override bool Contains(Thing thing) => thing?.Map == Map;
        public override IEnumerable<IntVec3> HullCells => new[] { Anchor.Position };
        public override Vector2 HullCenter => new Vector2(1.5f, 2.5f);
        public override IEnumerable<ShipPhysics.Thruster> BuildThrusters()
        {
            thrustReads++;
            yield return new ShipPhysics.Thruster { thrust = thrust, operational = thrust > 0, dir = Vector2.right };
        }
    }

    sealed class CruiseReference : SpaceObject
    {
        internal bool playerReference;
        internal CruiseReference(int id, Vector3 position, bool playerReference, Faction player)
            : base(id, new SpacePose(position, 0)) { this.playerReference = playerReference; faction = player; }
        protected override SpaceObjectTag MemberTags => playerReference ? SpaceObjectTag.Ship : 0;
    }

    sealed class CruiseFixture : IDisposable
    {
        internal readonly UniverseManager universe;
        internal readonly Map map;
        internal readonly CombatObject own;
        internal readonly CruiseHull hull;
        internal readonly CompShipCombatAI ai;
        internal readonly ShipFireControl fire;
        internal readonly Faction player, enemy;
        readonly List<SpaceObject> savedObjects;
        readonly List<Map> savedMaps;
        readonly FactionManager savedFactions;
        readonly int savedTick;
        readonly Harmony probe = new Harmony("DeltaV.Regression.NonCombatCruise");

        internal CruiseFixture(UniverseManager universe, int buildings = 1)
        {
            this.universe = universe;
            savedObjects = universe.objects.ToList(); savedMaps = Find.Maps.ToList();
            savedFactions = Find.World.factionManager; savedTick = Find.TickManager.TicksGame;
            universe.objects.Clear();
            Set(universe, "objectById", null); Set(universe, "objectByMap", null);
            player = (Faction)Raw(typeof(Faction)); enemy = (Faction)Raw(typeof(Faction));
            player.def = new FactionDef { isPlayer = true }; enemy.def = new FactionDef();
            Set(player, "relations", new List<FactionRelation> { new FactionRelation { other = enemy, kind = FactionRelationKind.Hostile } });
            Set(enemy, "relations", new List<FactionRelation> { new FactionRelation { other = player, kind = FactionRelationKind.Hostile } });
            var factions = (FactionManager)Raw(typeof(FactionManager)); Set(factions, "ofPlayer", player);
            Find.World.factionManager = factions;
            probe.Patch(AccessTools.PropertyGetter(typeof(Thing), "MaxHitPoints"),
                prefix: new HarmonyMethod(typeof(Program), nameof(TestHullMaxHitPoints)));
            probe.Patch(AccessTools.Constructor(typeof(CombatStrategyFacts), new[] { typeof(UniverseManager), typeof(ShipBase), typeof(SpaceObject) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(CountCruiseFacts)));
            probe.Patch(AccessTools.Method(typeof(UniverseManager), nameof(UniverseManager.PlayerReferenceObjects)),
                prefix: new HarmonyMethod(typeof(Program), nameof(CountCruiseReferences)));
            ThingDefOf.PilotConsole = TestBuildingDef("cruiseConsole");
            map = TestMap(53000);
            var console = AddTestConsole(map, player, 53001);
            ai = new CompShipCombatAI { parent = console.parent, props = new CompProperties_ShipCombatAI() };
            console.parent.AllComps.Add(ai);
            console.combat.weaponsArmed = true;
            hull = new CruiseHull(console.Ship);
            own = new CombatObject(53000, map, new SpacePose(Vector3.forward, 0)); own.members.Add(hull);
            universe.objects.Add(own);
            for (int i = 1; i < buildings; i++)
                AddTestBuilding(map, player, 53010 + i, TestBuildingDef("cruiseHull" + i), new EmptyComp());
            Current.Game.GetComponent<UniverseManager>().ShipStructure.Changed(map);
            fire = new ShipFireControl(universe, hull.Identity);
            ai.strategy.targetRules.Clear(); ai.strategy.targetRules.Add(new CombatStrategyRule { includeDisarmed = true });
        }

        internal void NextDecision() => Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 60);
        public void Dispose()
        {
            probe.UnpatchAll(probe.Id);
            universe.objects.Clear(); universe.objects.AddRange(savedObjects);
            Find.Maps.Clear(); Find.Maps.AddRange(savedMaps);
            Find.World.factionManager = savedFactions;
            Set(Find.TickManager, "ticksGameInt", savedTick);
            Set(universe, "objectById", null); Set(universe, "objectByMap", null);
            Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll();
        }
    }

    static void CheckNonCombatCruise(UniverseManager universe)
    {
        using var f = new CruiseFixture(universe);
        cruiseFactsBuilt = 0;
        f.fire.Decide(f.hull, f.own);
        Check(cruiseFactsBuilt == 1 && f.ai.baselineShipKey == f.hull.Identity.key
            && f.ai.initialHullHp > 0 && f.ai.initialThrust == 2000,
            "idle automatic fire establishes the original hull and thrust baselines before any enemy");
        float hullBaseline = f.ai.initialHullHp;
        for (int i = 0; i < 120; i++) { f.NextDecision(); f.fire.Decide(f.hull, f.own); }
        Check(cruiseFactsBuilt == 1 && !f.fire.Target.HasLock,
            "120 scheduled empty decisions preserve their result without rebuilding combat facts");
        f.hull.thrust = 500; f.NextDecision(); f.fire.Decide(f.hull, f.own);
        Check(f.ai.initialThrust == 2000 && f.ai.initialHullHp == hullBaseline,
            "damage during peaceful cruise does not redefine the pre-enemy baseline");
        f.ai.initialThrust = 0; f.hull.thrust = 0; f.NextDecision(); f.fire.Decide(f.hull, f.own);
        int zeroBuilds = cruiseFactsBuilt;
        f.NextDecision(); f.fire.Decide(f.hull, f.own);
        Check(cruiseFactsBuilt == zeroBuilds + 1 && f.ai.initialThrust == 0,
            "a genuine zero-thrust baseline retains the original observation path");
        f.hull.thrust = 2000; f.NextDecision(); f.fire.Decide(f.hull, f.own);
        Check(f.ai.initialThrust == 2000, "restored propulsion establishes a missing positive baseline at the old decision time");
        f.ai.baselineShipKey = -1; f.NextDecision(); f.fire.Decide(f.hull, f.own);
        Check(f.ai.baselineShipKey == f.hull.Identity.key && f.ai.initialThrust == 2000,
            "a changed baseline identity cannot enter the empty fast path");

        Map enemyMap = TestMap(54000);
        var console = AddTestConsole(enemyMap, f.enemy, 54001);
        var opponent = new CombatObject(54000, enemyMap,
            new SpacePose(OrbitSphere.Move(f.own.pose.position, new Vector2(50, 0)), 0));
        opponent.members.Add(new CruiseHull(console.Ship)); universe.objects.Add(opponent);
        int beforeEnemy = cruiseFactsBuilt;
        f.NextDecision(); f.fire.Decide(f.hull, f.own);
        Check(f.fire.Target.Ship == console.Vessel.Identity && cruiseFactsBuilt > beforeEnemy,
            "an enemy appearing during navigation is evaluated on the next unchanged decision boundary");
        var expected = f.fire.Target;
        // Force a fresh full observation on the same state and compare the selected identity.
        Set(typeof(ShipFireControl), f.fire, "observation", null);
        f.fire.Decide(f.hull, f.own);
        Check(f.fire.Target == expected, "idle optimization and a fresh full observation select the same target");
        opponent.pose.position = OrbitSphere.Move(f.own.pose.position,
            new Vector2(DeltaVMod.CurrentSettings.poiActiveRadius + 100, 0));
        f.NextDecision(); f.fire.Decide(f.hull, f.own);
        Check(!f.fire.Target.HasLock && ((Dictionary<DriveSubject, CombatStrategyRule>)
            AccessTools.Field(typeof(ShipFireControl), "targetRules").GetValue(f.fire)).Count == 0,
            "an out-of-range enemy clears target and stale rules at the original decision boundary");
        CheckCruiseDistances(f);
        RunCruiseBenchmarks(f);
    }

    static void CheckCruiseDistances(CruiseFixture f)
    {
        var universe = f.universe;
        universe.objects.Clear();
        var reference = new CruiseReference(55000, Vector3.forward, true, f.player);
        var target = new SpaceObject(55001, new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(100, 0)), 0));
        universe.objects.Add(reference); universe.objects.Add(target);
        var batch = new SpaceMapStateManager.PlayerDistanceBatch(universe);
        cruiseReferencesBuilt = 0;
        Check(cruiseReferencesBuilt == 0, "a distance batch without queries does not enumerate player references");
        bool distancesMatch = true;
        for (int i = 0; i < 20; i++)
        {
            float value = batch.Distance(target);
            distancesMatch &= Math.Abs(value - 100) < .01;
        }
        Check(distancesMatch, "shared membership retains exact spherical distance");
        Check(cruiseReferencesBuilt == 1, "twenty stable distance queries build one player reference list");
        reference.pose.position = OrbitSphere.Move(Vector3.forward, new Vector2(40, 0));
        Check(Math.Abs(batch.Distance(target) - 60) < .01 && cruiseReferencesBuilt == 1,
            "shared references read the current integrated pose instead of freezing the batch position");
        var nearer = new CruiseReference(55002, OrbitSphere.Move(Vector3.forward, new Vector2(90, 0)), true, f.player);
        universe.objects.Add(nearer);
        Check(Math.Abs(batch.Distance(target) - 10) < .01 && cruiseReferencesBuilt == 2,
            "membership is rebuilt when objects enter the synchronous batch");
        nearer.playerReference = false; Current.Game.GetComponent<UniverseManager>().ShipStructure.Changed(f.map);
        Check(Math.Abs(batch.Distance(target) - 60) < .01 && cruiseReferencesBuilt == 3,
            "structure-driven reference eligibility invalidates batch membership");
        universe.objects.Remove(reference);
        Check(float.IsPositiveInfinity(batch.Distance(target)), "no player references retain positive-infinity distance");
        nearer.playerReference = true; batch.Invalidate();
        Check(Math.Abs(batch.Distance(target) - 10) < .01, "explicit native-map invalidation rebuilds membership");
        var replacement = new CruiseReference(nearer.objectId, Vector3.forward, true, f.player);
        universe.objects[universe.objects.IndexOf(nearer)] = replacement;
        var nextBatch = new SpaceMapStateManager.PlayerDistanceBatch(universe);
        Check(Math.Abs(nextBatch.Distance(target) - 100) < .01,
            "a new synchronous batch sees a replacement representation with the same object ID");
        float deferred = -1;
        universe.QueueFrameAction(() => deferred = universe.MapStates.MinPlayerDistance(target));
        replacement.pose.position = OrbitSphere.Move(Vector3.forward, new Vector2(70, 0));
        var queued = Actions(universe); var action = queued[queued.Count - 1]; queued.RemoveAt(queued.Count - 1); action();
        Check(Math.Abs(deferred - 30) < .01, "frame actions resolve current references rather than capturing the batch");

        float radius = DeltaVMod.CurrentSettings.poiActiveRadius;
        var archive = new ArchivedSpaceObject { objectId = 55003,
            pose = new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(radius + 10, 0)), 0) };
        universe.objects.Clear(); universe.objects.Add(replacement); universe.objects.Add(archive);
        replacement.pose.position = Vector3.forward;
        var arrivalBatch = new SpaceMapStateManager.PlayerDistanceBatch(universe);
        int pending = Actions(universe)?.Count ?? 0;
        universe.MapStates.CheckArchivedProximity(archive, arrivalBatch);
        Check((Actions(universe)?.Count ?? 0) == pending, "an archive outside the current radius does not queue an early restore");
        replacement.pose.position = OrbitSphere.Move(Vector3.forward, new Vector2(20, 0));
        universe.MapStates.CheckArchivedProximity(archive, arrivalBatch);
        Check(Actions(universe).Count == pending + 1,
            "an integrated player reference crossing the radius queues restoration in the same check");
        Actions(universe).RemoveAt(pending);
        Call(universe.MapStates, "CancelTransition", archive.objectId, MapTransitionKind.Restore);
    }

    // Reflection lets the same workload run against the preserved pre-change production DLL.
    static void RunCruiseBenchmarks(CruiseFixture f)
    {
        f.universe.objects.Clear(); f.universe.objects.Add(f.own);
        f.ai.baselineShipKey = f.hull.Identity.key; f.ai.initialThrust = 2000; f.ai.initialHullHp = 100;
        f.hull.thrust = 2000;
        Action decisions = () => { for (int i = 0; i < 180; i++) { f.NextDecision(); f.fire.Decide(f.hull, f.own); } };
        SampleCruise("idle_decisions_180", decisions, () => cruiseFactsBuilt, () => cruiseFactsBuilt = 0);

        var map = f.map;
        var engine = AddTestBuilding(map, f.player, 56000, TestBuildingDef("cruisePresentation"), new EmptyComp());
        var power = new DrivePowerProbe(); var previous = ThrusterPower.Model;
        try
        {
            ThrusterPower.Model = power;
            var report = new ShipPhysics.MassReport { mass = 100, inertia = 1000 };
            for (int i = 0; i < 64; i++) report.thrusters.Add(new ShipPhysics.Thruster
                { thing = engine, thrust = 2000, dir = Vector2.right, operational = true });
            var body = new DriveExecutionProbe(report);
            var getter = AccessTools.PropertyGetter(typeof(SpaceObject), "PresentationInfo")
                ?? AccessTools.PropertyGetter(typeof(SpaceObject), "MotionInfo");
            var read = (Func<DriveExecutionProbe, DriveInfo>)getter.CreateDelegate(typeof(Func<DriveExecutionProbe, DriveInfo>));
            Action displays = () => { for (int i = 0; i < 10000; i++) _ = read(body); };
            SampleCruise("presentation_10000_x64", displays, () => power.reads, () => power.reads = 0);
        }
        finally { ThrusterPower.Model = previous; }

        f.universe.objects.Clear();
        f.universe.objects.Add(new CruiseReference(57000, Vector3.forward, true, f.player));
        for (int i = 1; i < 256; i++) f.universe.objects.Add(new SpaceObject(57000 + i,
            new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(i * 10, 0)), 0)));
        var type = typeof(SpaceMapStateManager).GetNestedType("PlayerDistanceBatch", BindingFlags.NonPublic);
        Func<SpaceObject, float> getDistance;
        Action distances;
        if (type == null)
            distances = () => { foreach (var obj in f.universe.objects) _ = f.universe.MapStates.MinPlayerDistance(obj); };
        else
        {
            var ctor = AccessTools.Constructor(type, new[] { typeof(UniverseManager) });
            var method = AccessTools.Method(type, "Distance");
            // The reflection overhead deliberately remains in the new side of the comparison.
            distances = () =>
            {
                object batch = ctor.Invoke(new object[] { f.universe });
                getDistance = (Func<SpaceObject, float>)method.CreateDelegate(typeof(Func<SpaceObject, float>), batch);
                foreach (var obj in f.universe.objects) _ = getDistance(obj);
            };
        }
        SampleCruise("distance_batch_256", distances, () => cruiseReferencesBuilt, () => cruiseReferencesBuilt = 0);
    }

    static void SampleCruise(string name, Action workload, Func<int> count, Action reset)
    {
        for (int i = 0; i < 3; i++) workload();
        for (int round = 0; round < 3; round++)
        {
            reset(); long before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew(); workload(); watch.Stop();
            Console.WriteLine($"CRUISE_PERF {name} round={round} ms={watch.Elapsed.TotalMilliseconds:F4} bytes={GC.GetAllocatedBytesForCurrentThread() - before} work={count()}");
        }
    }
}
