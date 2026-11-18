using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static readonly List<string> aiMessages = new List<string>();
    static bool CaptureAiMessage(string text)
    { if (text.StartsWith("[DeltaV:AI]")) aiMessages.Add(text); return false; }
    // Scene-free hull adapter; planning, guidance, allocation and integration stay in production.
    sealed class ManeuverMotionObject : MapSpaceObject
    {
        internal ShipBase member;
        internal readonly ShipPhysics.MassReport layout = new ShipPhysics.MassReport { mass = 1000, inertia = 10000 };
        private readonly bool productionDrive;
        internal ManeuverMotionObject(int id, Map map, SpacePose pose, bool productionDrive = false) : base(id, map, pose)
        {
            this.productionDrive = productionDrive;
            foreach (var direction in new[] { Vector2.right, Vector2.left, Vector2.up, Vector2.down })
                foreach (float side in new[] { -3f, 3f })
                    layout.thrusters.Add(new ShipPhysics.Thruster { dir = direction,
                        localPos = new Vector2(side, side), operational = true, thrust = 2000 });
        }
        public override IEnumerable<ShipBase> Ships => productionDrive ? base.Ships : FixtureShips();
        private IEnumerable<ShipBase> FixtureShips() { yield return member; }
        protected override ShipPhysics.MassReport MotionReport => layout;
        public override bool TryGetDrive(DriveSubject subject, out DeltaV.DriveInfo info)
        { if (productionDrive) return base.TryGetDrive(subject, out info); info = ReadDriveInfo(); return true; }
        public override DriveResult ApplyDrive(DriveSubject subject, DriveControl control, float dt, out float torque)
        {
            if (productionDrive) return base.ApplyDrive(subject, control, dt, out torque);
            ReadDriveInfo().Preview(control.force, control.torque, out var force, out torque);
            ShipPhysics.Integrate(this, layout, force, torque, dt, false, false, false);
            return DriveResult.Executed;
        }
    }

    static void CheckCombatManeuverMotion(UniverseManager universe, Faction player, Faction enemy)
    {
        var patch = new Harmony("DeltaV.Regression.ManeuverMotion");
        patch.Patch(AccessTools.PropertyGetter(typeof(Thing), "MaxHitPoints"),
            prefix: new HarmonyMethod(typeof(Program), nameof(TestHullMaxHitPoints)));
        patch.Patch(AccessTools.Method(typeof(Log), nameof(Log.Message), new[] { typeof(string) }),
            prefix: new HarmonyMethod(typeof(Program), nameof(CaptureAiMessage)));
        Set(enemy, "relations", new List<FactionRelation> { new FactionRelation { other = player, kind = FactionRelationKind.Hostile } });
        Set(player, "relations", new List<FactionRelation> { new FactionRelation { other = enemy, kind = FactionRelationKind.Hostile } });
        ThingDefOf.PilotConsole = TestBuildingDef("motionConsole");
        var factions = (FactionManager)Raw(typeof(FactionManager));
        Set(factions, "ofPlayer", player); Set(Find.World, "factionManager", factions);
        try
        {
            Check(!new DeltaVSettings().logAI, "AI logging defaults off");
            DeltaVMod.CurrentSettings.logCombat = DeltaVMod.CurrentSettings.logDevelopment = false;
            var baselines = new Dictionary<(float speed, bool hold, int layout), List<int>>();
            var axialTravel = new Dictionary<int, float>();
            DeltaV.DriveInfo referenceDrive = null;
            foreach (bool logging in new[] { false, true })
            foreach (var scenario in new[] { (0f, false, 0), (4f, false, 0), (12f, false, 0), (20f, false, 0), (0f, true, 0), (0f, false, 1), (0f, false, 2), (0f, false, 3), (0f, false, 4), (0f, false, 5) })
            {
                float closingSpeed = scenario.Item1; bool hold = scenario.Item2;
                DeltaVMod.CurrentSettings.logAI = logging; aiMessages.Clear();
                var ownMap = TestMap(8100); var targetMap = TestMap(8200);
                var own = new ManeuverMotionObject(8100, ownMap,
                    new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(100, 0)), Mathf.PI));
                if (scenario.Item3 > 0 && scenario.Item3 < 4)
                {
                    for (int i = 0; i < own.layout.thrusters.Count; i++)
                    {
                        var thruster = own.layout.thrusters[i];
                        thruster.thrust = Mathf.Abs(thruster.dir.x) > .5f ? 2000f : 100f;
                        own.layout.thrusters[i] = thruster;
                    }
                    if (scenario.Item3 == 3) own.layout.inertia *= 1000f;
                }
                var target = new ManeuverMotionObject(8200, targetMap, new SpacePose(Vector3.forward, 0));
                if (scenario.Item3 == 5)
                {
                    own.layout.comLocal = new Vector2(7f, -4f);
                    foreach (var thruster in own.layout.thrusters) thruster.localPos += own.layout.comLocal;
                }
                foreach (var item in new[] { (obj: own, faction: player), (obj: target, faction: enemy) })
                {
                    var console = AddTestConsole(item.obj.map, item.faction, item.obj.objectId + 1);
                    var hull = new CombatHull(console.Ship);
                    for (int x = 0; x < 5; x++) for (int z = 0; z < 5; z++) hull.cells.Add(new IntVec3(x, 0, z));
                    item.obj.member = hull; universe.objects.Add(item.obj);
                    universe.Controls.For(hull);
                }
                Set(universe, "objectById", null); Set(universe, "objectByMap", null);
                if (!logging && closingSpeed == 0f && !hold && scenario.Item3 == 0)
                {
                    CheckManeuverDecisionBoundaries(universe, own, target);
                    own.TryGetDrive(own.member.Identity, out var choiceDrive);
                    CheckRepositionChoices(choiceDrive);
                    referenceDrive = choiceDrive;
                    CheckRotatingPrediction(own);
                    CheckRotatingPrediction(own, true);
                    CheckRotatingPrediction(own, true, true);
                }
                var planner = new ShipAutoManeuverControl(universe, own.member.Identity);
                Set(planner, "anchorTarget", CombatTarget.ForShip(target.member.Identity));
                Set(planner, "anchorDistance", 100f);
                // Sustained orbit is now an explicit action. Automatic engagement has
                // a separate reach-and-hold regression and must not orbit without value.
                universe.Controls.For(own.member).FireControl.Strategy(own.member).maneuverRules.Insert(0,
                    new CombatStrategyRule { tactic = new CombatTacticSettings { distance = 100,
                        behavior = hold ? CombatTactic.HoldRange : CombatTactic.Circle,
                        facing = scenario.Item3 == 2 ? CombatFacing.Target : CombatFacing.Automatic } });
                var plan = typeof(ShipAutoManeuverControl).GetMethod("PlanTacticTrajectory", Private);
                var state = (CombatReposition)typeof(ShipAutoManeuverControl).GetField("reposition", Private).GetValue(planner);
                bool planningPreservesMotion = true;
                var plannerClock = new System.Diagnostics.Stopwatch();
                var pilotClock = new System.Diagnostics.Stopwatch();
                double qpMilliseconds = 0; int qpCalls = 0;
                bool Sample(out SpacePose pose) { pose = ShipRegistry.ShipPose(target.member); return true; }
                bool Plan(SpaceObject body, SpacePose enemyPose, float radius, DeltaV.DriveInfo drive, out CombatTrajectoryIntent intent)
                {
                    var before = body.pose;
                    object[] args = { body, enemyPose, radius, drive, null };
                    plannerClock.Start();
                    bool result = (bool)plan.Invoke(planner, args); intent = (CombatTrajectoryIntent)args[4];
                    plannerClock.Stop();
                    planningPreservesMotion &= body.pose.Equals(before);
                    return result;
                }
                var pilot = universe.Autopilot; var subject = new DriveSubject(own.objectId, own.member.Identity.key);
                pilot.TrackOrbit(subject, Sample, 10f, Plan);
                object flight = RecoveryFlight(pilot, subject);
                bool continuousExecution = true, deadlinesHonored = true;
                int events = 0, lastNext = 0, reversals = 0, lastDirection = 0;
                float maxCrossSpeed = 0, minRange = float.MaxValue, maxRange = 0, traveledPhase = 0f;
                float legPeak = 0f;
                bool legDropped = false;
                int measuredLegs = 0, slowLegEnds = 0, slowWithinLeg = 0;
                var trace = new List<int>();
                // Three minutes include many opportunities for an independent random walk to reverse.
                for (int tick = 0; tick < 10800; tick++)
                {
                    if (scenario.Item3 == 4 && tick == 3600)
                    {
                        foreach (var thruster in own.layout.thrusters) thruster.thrust *= .25f;
                        own.layout.version++; legPeak = 0f;
                    }
                    var delta = OrbitSphere.Offset(target.pose.position, own.pose.position);
                    target.pose.velocity = delta.normalized * closingSpeed;
                    target.pose.position = OrbitSphere.Move(target.pose.position, target.pose.velocity / 60f);
                    var before = own.pose;
                    pilotClock.Start(); RecoveryTick(pilot, tick); pilotClock.Stop();
                    if (FlightValue<int>(flight, "planStartTick") == tick)
                    {
                        var solved = FlightValue<GuidanceCommand>(flight, "heldCommand");
                        qpMilliseconds += solved.solveMilliseconds; qpCalls += solved.qpCalls;
                    }
                    continuousExecution &= ReferenceEquals(flight, RecoveryFlight(pilot, subject))
                        && (own.pose.velocity - SpaceTransform.TransportVelocity(before, own.pose.position)).magnitude < .28f;
                    // The shared pilot samples tactics every six ticks; completion must not delay a due decision.
                    if (!hold) deadlinesHonored &= tick < (int)typeof(ShipAutoManeuverControl)
                        .GetField("nextTacticTick", Private).GetValue(planner) + 6;
                    int measurementInterval = hold ? state.nextTick : tick / 360 + 1;
                    if (measurementInterval != lastNext)
                    {
                        Vector2 offsetBefore = OrbitSphere.Offset(target.pose.position, before.position);
                        float endSpeed = Mathf.Abs(Vector2.Dot(new Vector2(-offsetBefore.y, offsetBefore.x).normalized,
                            SpaceTransform.TransportVelocity(before, target.pose.position) - target.pose.velocity));
                        if (tick > (scenario.Item3 == 4 ? 4800 : 1200) && legPeak > 1f)
                        { measuredLegs++; if (endSpeed < legPeak * .6f) slowLegEnds++; if (legDropped) slowWithinLeg++; }
                        legPeak = 0f; legDropped = false;
                        events++; lastNext = measurementInterval;
                        Vector2 start = OrbitSphere.Offset(target.pose.position, before.position);
                        float step = SpaceTransform.WrapAngle(state.PhaseAt(target.pose.position) - Mathf.Atan2(start.y, start.x));
                        var action = (CombatManeuverIntent)typeof(ShipAutoManeuverControl).GetField("maneuverIntent", Private).GetValue(planner);
                        int direction = Math.Sign(action.tangentSpeed);
                        if (lastDirection != 0 && direction != 0 && direction != lastDirection) reversals++;
                        if (direction != 0) lastDirection = direction;
                    }
                    delta = OrbitSphere.Offset(target.pose.position, own.pose.position);
                    Vector2 previous = OrbitSphere.Offset(target.pose.position, before.position);
                    traveledPhase += SpaceTransform.WrapAngle(Mathf.Atan2(delta.y, delta.x) - Mathf.Atan2(previous.y, previous.x));
                    var tangent = new Vector2(-delta.y, delta.x).normalized;
                    maxCrossSpeed = Mathf.Max(maxCrossSpeed, Mathf.Abs(Vector2.Dot(tangent,
                        SpaceTransform.TransportVelocity(own.pose, target.pose.position) - target.pose.velocity)));
                    float currentCrossSpeed = Mathf.Abs(Vector2.Dot(tangent,
                        SpaceTransform.TransportVelocity(own.pose, target.pose.position) - target.pose.velocity));
                    if (legPeak > 1f && currentCrossSpeed < legPeak * .6f) legDropped = true;
                    legPeak = Mathf.Max(legPeak, currentCrossSpeed);
                    minRange = Mathf.Min(minRange, delta.magnitude); maxRange = Mathf.Max(maxRange, delta.magnitude);
                    foreach (float value in new[] { own.pose.position.x, own.pose.position.y, own.pose.position.z,
                        own.pose.velocity.x, own.pose.velocity.y, own.pose.angle, own.pose.angularVelocity, state.phase })
                        trace.Add(BitConverter.SingleToInt32Bits(value));
                    trace.Add(state.nextTick);
                }
                Console.WriteLine($"MOTION_PERF logging={logging} closing={closingSpeed} hold={hold} pilotMs={pilotClock.Elapsed.TotalMilliseconds:F3} planMs={plannerClock.Elapsed.TotalMilliseconds:F3} qpMs={qpMilliseconds:F3} qpCalls={qpCalls}");
                Console.WriteLine($"MANEUVER layout={scenario.Item3} logging={logging} hold={hold} closing={closingSpeed} events={events} reversals={reversals} traveledDeg={traveledPhase * Mathf.Rad2Deg:F1} transverseMax={maxCrossSpeed:F3} range={minRange:F2}..{maxRange:F2} logs={aiMessages.Count}");
                Console.WriteLine($"CONTINUITY slowLegEnds={slowLegEnds}/{measuredLegs} slowWithinLeg={slowWithinLeg}/{measuredLegs} endToPeakMinimum=0.6 warmupTicks=1200");
                if (!Environment.GetCommandLineArgs().Contains("--continuous-baseline") && !hold && closingSpeed == 0f)
                    Check(measuredLegs >= 10 && slowLegEnds == 0 && slowWithinLeg == 0,
                        "stable orbit never loses over 40 percent of speed from an earlier peak within a decision interval");
                if (scenario.Item3 == 4)
                    Check(own.pose.velocity.magnitude < 6f, "in-flight thrust loss lowers the reachable cruise speed without replacing the MPC flight");
                if (scenario.Item3 > 0)
                    Check(minRange > 85f && maxRange < 115f, "strong-axis and weak-turn ships keep their explicit 100-cell range within 15 percent");
                Check(hold ? maxCrossSpeed < .01f : events >= 3 && maxCrossSpeed > 1f,
                    "production maneuver remains valid at closing speed " + closingSpeed + ", hold=" + hold);
                Check(deadlinesHonored, "every reposition deadline is honored within the pilot sampling cadence");
                Check(planningPreservesMotion && continuousExecution,
                    "replanning preserves physical state and the live MPC flight; velocity changes stay within thrust bounds");
                Check(hold || reversals == 0 && events >= 18 && Mathf.Abs(traveledPhase) > (scenario.Item3 == 3 ? .5f : Mathf.PI),
                    "explicit production circling makes sustained progress without direction reversals over three minutes");
                if (logging)
                {
                    Check(trace.SequenceEqual(baselines[(closingSpeed, hold, scenario.Item3)]), "AI log switch preserves every motion tick and reposition event exactly");
                    Check(aiMessages.Any(s => s.Contains("maneuver tick=") && s.Contains("intentSpeed=") && s.Contains("terminalVelocity=") && s.Contains("reset="))
                        && aiMessages.Any(s => s.Contains("drive tick=") && s.Contains("planTick=") && s.Contains("deltaV=")),
                        "AI module alone pairs actual planner state with drive results");
                    Check(aiMessages.Count < (hold ? 15 : 1500), "stable state is suppressed and moving traces are bounded by decision edges");
                }
                else { baselines[(closingSpeed, hold, scenario.Item3)] = trace; Check(aiMessages.Count == 0, "disabled AI module emits no messages"); }
                if (!logging && scenario.Item3 > 0) axialTravel[scenario.Item3] = Mathf.Abs(traveledPhase);
                pilot.Cancel(subject); universe.Controls.Reset(); universe.objects.Clear(); Find.Maps.Clear();
                ShipRegistry.ClearCaches(); ShipPhysics.ClearCache(); SpaceCollision.ClearCaches();
                Set(universe, "objectById", null); Set(universe, "objectByMap", null);
            }
            Console.WriteLine($"AXIAL auto={axialTravel[1]:F3} fixed={axialTravel[2]:F3} weakTurn={axialTravel[3]:F3}");
            Check(axialTravel[1] > axialTravel[2] * 1.2f, "rotating the strong axis produces at least 20 percent more actual orbit progress than fixed facing");
            CheckAiLogSettings();
            CheckContinuousReferences(referenceDrive);
        }
        finally { DeltaVMod.CurrentSettings.logAI = false; aiMessages.Clear(); patch.UnpatchAll(patch.Id); }
    }

    static void CheckHullAvoidanceMotion(UniverseManager universe, Faction faction, HullRoundFixture f)
    {
        f.Clear();
        foreach (bool ownOn in new[] { false, true })
        foreach (bool enemyOn in new[] { false, true })
        {
            var own = f.Craft(faction, 0, 0, Vector2.zero, ownOn, hp: 10000, size: 3, motion: true);
            var target = f.Craft(faction, 0, 70, Vector2.zero, true, hp: 10000);
            var rammer = f.Craft(faction, 30, 0, new Vector2(-10, 0), enemyOn, hp: 10000, size: 3);
            f.Prepare();
            var stationary = CombatFlightPlanner.Brake(own.body.pose, null, Find.TickManager.TicksGame);
            Check(universe.Projectiles.HullRouteClear(own.ship, stationary, 0, out _, out _) == (!ownOn || enemyOn),
                $"combat prediction consumes actual incoming rounds: own={ownOn}, enemy={enemyOn}");
            var planner = own.control.AutoManeuver;
            Set(planner, "anchorTarget", CombatTarget.ForShip(target.ship.Identity));
            Set(planner, "anchorDistance", 70f);
            own.control.FireControl.Strategy(own.ship).maneuverRules.Insert(0,
                new CombatStrategyRule { tactic = new CombatTacticSettings { distance = 70, behavior = CombatTactic.HoldRange } });
            int planCalls = 0; float plannedTravel = 0;
            bool Sample(out SpacePose pose) { pose = ShipRegistry.ShipPose(target.ship); return true; }
            bool Plan(SpaceObject body, SpacePose pose, float radius, DeltaV.DriveInfo drive, out CombatTrajectoryIntent intent)
            {
                object[] args = { body, pose, radius, drive, null };
                bool result = (bool)Call(planner, "PlanTacticTrajectory", args);
                intent = (CombatTrajectoryIntent)args[4]; planCalls++;
                intent.Sample(3, out var later, out _, out _, out _);
                plannedTravel = Mathf.Max(plannedTravel, OrbitSphere.ArcCells(own.body.pose.position, later));
                return result;
            }
            universe.Autopilot.TrackOrbit(DriveSubject.Of(own.ship), Sample, 0, Plan);
            float maxLateral = 0, minimum = float.MaxValue; bool collided = false;
            for (int tick = 0; tick < 300; tick++)
            {
                f.Prepare();
                universe.Autopilot.Tick();
                foreach (var body in universe.objects.ToArray()) body.AdvanceDrift(1f / 60f);
                var beforeImpact = own.body.pose;
                universe.Projectiles.ResolveCollisions(); f.Flush();
                bool impulse = (own.body.pose.velocity - SpaceTransform.TransportVelocity(beforeImpact,own.body.pose.position)).magnitude > .0001f
                    || Mathf.Abs(own.body.pose.angularVelocity-beforeImpact.angularVelocity)>.0001f;
                if (!collided && !impulse)
                    maxLateral = Mathf.Max(maxLateral, Mathf.Abs(OrbitSphere.Offset(Vector3.forward, own.body.pose.position).y));
                collided |= impulse;
                minimum = Mathf.Min(minimum, OrbitSphere.ArcCells(own.body.pose.position, rammer.body.pose.position));
            }
            Check(ownOn && !enemyOn ? own.wall.HitPoints == 10000 && maxLateral > 1f
                : maxLateral < .1f,
                $"closed-loop combat evasion own={ownOn}, enemy={enemyOn}: lateral={maxLateral:F2}, nearest={minimum:F2}, HP={own.wall.HitPoints}, plans={planCalls}, planned={plannedTravel:F2}");
            universe.Autopilot.Cancel(own.ship.Identity); f.Clear();
        }
        foreach (int mode in new[] { 0, 1, 2 })
        {
            var own = f.Craft(faction, 0, 0, Vector2.zero, true, hp: 10000, motion: true);
            f.Craft(faction, 15, 0, new Vector2(-10, 0), false, hp: 10000);
            var subject = DriveSubject.Of(own.ship);
            if (mode == 1) universe.Autopilot.FlyTo(subject, own.body.pose.position, own.body.pose.angle);
            if (mode == 2) universe.Autopilot.FollowReference(subject, _ => new SpacePose(Vector3.forward, 0), true, () => true, null);
            bool collided = false; float preContactDrift = 0;
            for (int tick = 0; tick < 120; tick++)
            {
                f.Prepare(); universe.Autopilot.Tick();
                foreach (var body in universe.objects.ToArray()) body.AdvanceDrift(1f / 60f);
                if (!collided) preContactDrift = Mathf.Max(preContactDrift,OrbitSphere.ArcCells(Vector3.forward,own.body.pose.position));
                var beforeImpact=own.body.pose;
                universe.Projectiles.ResolveCollisions(); f.Flush();
                collided |= (own.body.pose.velocity-SpaceTransform.TransportVelocity(beforeImpact,own.body.pose.position)).magnitude>.0001f;
            }
            Check(preContactDrift < .01f && collided,
                $"enabled avoidance does not steer outside combat autopilot, mode={mode}");
            universe.Autopilot.Cancel(subject); f.Clear();
        }
    }

    static void CheckRepositionChoices(DeltaV.DriveInfo drive)
    {
        var target = new SpacePose(Vector3.forward, 0f);
        var own = new SpacePose(OrbitSphere.Move(target.position, new Vector2(100f, 0f)), Mathf.PI);
        var pose = new CombatAttackPose { behavior = CombatTactic.Automatic, distance = 100f };
        var settings = new CombatTacticSettings { intervalMin = 6f, intervalMax = 6f };
        bool equalContinues = true, smallGainContinues = true, largeGainReverses = true, blockedReverses = true;
        var initialDirections = new HashSet<int>();
        for (int seed = 1; seed <= 64; seed++)
        {
            own.velocity = Vector2.zero;
            var fromRest = new CombatReposition(seed);
            fromRest.Update(own, target, pose, settings, drive, 0);
            Check(Mathf.Abs(fromRest.phase) < .00001f && fromRest.tangentSpeed == 0,
                "no positional benefit invents no automatic movement at seed " + seed);
            fromRest.Reset();
            fromRest.Update(own, target, pose, settings, drive, 0,
                preference: (phase, speed) => Mathf.Abs(phase) > .001f ? 10 : 0);
            initialDirections.Add(Math.Sign(fromRest.phase));
            foreach (int direction in new[] { -1, 1 })
            {
                own.velocity = new Vector2(0, direction * 4f);
                CombatReposition Decide(float reverseGain, bool blocked = false)
                {
                    var result = new CombatReposition(seed);
                    result.Update(own, target, pose, settings, drive, 0, preference: (phase, speed) =>
                        phase * direction < 0f ? reverseGain : blocked ? float.NegativeInfinity : 0f);
                    return result;
                }
                equalContinues &= Decide(0f).phase * direction > 0f;
                smallGainContinues &= Decide(.1f).phase * direction > 0f;
                var reverse = Decide(20f);
                largeGainReverses &= reverse.phase * direction < 0f && reverse.selectedReversalCost > 0f;
                blockedReverses &= Decide(0f, true).phase * direction < 0f;
            }
        }
        Check(initialDirections.SetEquals(new[] { -1, 1 }), "random tie-breaking can choose either equal direction from rest");
        Check(equalContinues, "64 seeds and both actual velocity signs preserve motion when positional benefits are equal");
        Check(smallGainContinues, "a marginal positional gain cannot pay for reversing real transverse motion");
        Check(largeGainReverses, "a sufficient positional gain can reverse immediately without waiting for the previous leg");
        Check(blockedReverses, "an unsafe continuation is excluded even when reversing costs more");
        own.velocity = new Vector2(0f, 12f);
        var fast = new CombatReposition(1);
        fast.Update(own, target, pose, settings, drive, 0, preference: (phase, speed) => phase < 0f ? 100f : 0f);
        own.velocity = new Vector2(0f, 2f);
        var slow = new CombatReposition(1);
        slow.Update(own, target, pose, settings, drive, 0, preference: (phase, speed) => phase < 0f ? 100f : 0f);
        Check(fast.selectedReversalCost > slow.selectedReversalCost && slow.selectedReversalCost > 0f,
            "reversal cost increases with the actual speed to brake");

        own.velocity = new Vector2(0f, 4f);
        var weapon = new CombatAttackWeapon(Vector2.zero, -12f * Mathf.Deg2Rad, 22f,
            150f, 0f, 45f, 100f, false, true);
        var weapons = new[] { weapon };
        CombatReposition WithCoverage(bool offense)
        {
            var state = new CombatReposition(17);
            state.Update(own, target, pose, settings, drive, 0, preference: (phase, speed) =>
            {
                var candidate = pose;
                candidate.EvaluateFire(weapons, new Vector2(0f, speed), offense, null);
                return offense ? candidate.firepower : 0;
            });
            return state;
        }
        Check(WithCoverage(true).phase < 0f && WithCoverage(false).phase > 0f,
            "real finite-speed weapon coverage can pay for reversal, while maneuver-only ignores offensive coverage");

        foreach (CombatTurn direction in new[] { CombatTurn.Clockwise, CombatTurn.Counterclockwise })
        {
            var explicitSettings = settings.Copy(); explicitSettings.direction = direction;
            var explicitState = new CombatReposition(17);
            explicitState.Update(own, target, pose, explicitSettings, drive, 0,
                preference: (phase, speed) => speed * (direction == CombatTurn.Clockwise ? 1 : -1) > 0f ? 1000f : 0f);
            Check(explicitState.tangentSpeed * (direction == CombatTurn.Clockwise ? -1 : 1) >= 0f,
                "explicit orbit direction excludes conflicting candidates even if their score is higher");
        }
        // Compare the same requested reversal against an actual weaker braking layout.
        var report = new ShipPhysics.MassReport { mass = 1000f, inertia = 10000f };
        foreach (var direction in new[] { Vector2.right, Vector2.left, Vector2.up, Vector2.down })
            foreach (float side in new[] { -3f, 3f })
                report.thrusters.Add(new ShipPhysics.Thruster { dir = direction, localPos = new Vector2(side, side),
                    operational = true, thrust = direction == Vector2.up ? 200f : 2000f });
        var weak = new CombatReposition(17);
        weak.Update(own, target, pose, settings, new DeltaV.DriveInfo(report), 0,
            preference: (phase, speed) => phase < 0f ? 100f : 0f);
        var strong = new CombatReposition(17);
        strong.Update(own, target, pose, settings, drive, 0,
            preference: (phase, speed) => phase < 0f ? 100f : 0f);
        Check(weak.selectedReversalCost > strong.selectedReversalCost,
            "actual weak reverse thrust raises the stopping-time cost instead of relying on a fixed speed threshold");
        own.angle = 0f;
        var turned = new CombatReposition(17);
        turned.Update(own, target, pose, settings, new DeltaV.DriveInfo(report), 0,
            preference: (phase, speed) => phase < 0f ? 100f : 0f);
        Check(turned.selectedReversalCost < weak.selectedReversalCost,
            "braking cost uses the current hull attitude, not the candidate's desired heading");
    }

    static void CheckManeuverDecisionBoundaries(UniverseManager universe, ManeuverMotionObject own, ManeuverMotionObject target)
    {
        var savedOwn = own.pose;
        var savedTarget = target.pose;
        var control = universe.Controls.For(own.member).FireControl;
        var strategy = control.Strategy(own.member);
        var method = typeof(ShipAutoManeuverControl).GetMethod("PlanTacticTrajectory", Private);
        T Read<T>(ShipAutoManeuverControl planner, string field) =>
            (T)typeof(ShipAutoManeuverControl).GetField(field, Private).GetValue(planner);
        CombatTrajectoryIntent Plan(ShipAutoManeuverControl planner, int tick)
        {
            Set(Find.TickManager, "ticksGameInt", tick);
            own.TryGetDrive(own.member.Identity, out var drive);
            object[] args = { own, ShipRegistry.ShipPose(target.member), 10f, drive, null };
            if (!(bool)method.Invoke(planner, args)) throw new Exception("boundary fixture lost its production planner");
            return (CombatTrajectoryIntent)args[4];
        }
        var a = new ShipAutoManeuverControl(universe, own.member.Identity);
        var b = new ShipAutoManeuverControl(universe, own.member.Identity);
        foreach (var planner in new[] { a, b })
        {
            Set(planner, "anchorTarget", CombatTarget.ForShip(target.member.Identity));
            Set(planner, "anchorDistance", 100f);
            Plan(planner, 0);
        }
        var state = Read<CombatReposition>(a, "reposition");
        var reference = Read<CombatReposition>(b, "reposition");
        int deadline = state.nextTick;
        float initialPhase = state.phase;
        uint Random(CombatReposition s) => (uint)typeof(CombatReposition).GetField("random", Private).GetValue(s);
        uint initialRandom = Random(state);
        SubstructureOwnership.BumpStructureVersion(); Plan(a, 1);
        Check(state.nextTick == deadline && Mathf.Abs(state.phase - initialPhase) < .00001f && Random(state) == initialRandom,
            "global structure invalidation refreshes caches without restarting intent, timer or random stream");
        own.layout.version++; Plan(a, 2);
        Check(state.nextTick == deadline && Mathf.Abs(state.phase - initialPhase) < .00001f && Random(state) == initialRandom,
            "drive invalidation does not trigger another random reposition");
        control.configurationVersion++; Plan(a, 3);
        Check(state.nextTick == deadline && Mathf.Abs(state.phase - initialPhase) < .00001f && Random(state) == initialRandom,
            "fire-control cache invalidation preserves the maneuver deadline and random stream");

        // Give the two planners contradictory unfinished goals, but identical current physical facts.
        own.pose.position = OrbitSphere.Move(target.pose.position, new Vector2(40f, 90f));
        own.pose.velocity = new Vector2(-8f, 5f); own.pose.angularVelocity = .2f;
        state.phase = -1f; reference.phase = 2.5f;
        foreach (var planner in new[] { a, b })
        {
            Set(planner, "nextPoseTick", deadline + 1000);
            Set(planner, "nextTacticTick", deadline + 1000);
        }
        // Synchronize the reference's derived cache metadata without changing its pending decision.
        Plan(b, 4);
        Set(b, "nextTacticTick", deadline + 1000);
        Set(b, "nextPoseTick", deadline + 1000);
        Set(Find.TickManager, "ticksGameInt", deadline - 1);
        var staleFacts = control.Facts(own.member, own);
        float staleDistance = staleFacts.Context(target.member).distance;
        target.pose.position = new Vector3(.03f, .999f, .01f).normalized;
        own.pose.position = OrbitSphere.Move(target.pose.position, new Vector2(60f, 110f));
        var rule = Read<CombatStrategyRule>(a, "activeRule");
        float savedDistance = rule.tactic.distance;
        rule.tactic.distance = 80f;
        var before = own.pose;
        var intent = Plan(a, deadline);
        var expected = Plan(b, deadline);
        float currentDistance = OrbitSphere.ArcCells(ShipRegistry.ShipPose(own.member).position,
            ShipRegistry.ShipPose(target.member).position);
        var currentFacts = control.Facts(own.member, own);
        Check(!ReferenceEquals(staleFacts, currentFacts) && Mathf.Abs(staleDistance - currentDistance) > 10f
            && Mathf.Abs(currentFacts.Context(target.member).distance - currentDistance) < .001f,
            "a due decision refreshes rule facts even when fire control sampled the old situation one tick ago");
        Check(state.nextTick > deadline && reference.nextTick == state.nextTick,
            "an unfinished, fast-moving leg never blocks a decision at its deadline");
        Check(Mathf.Abs(SpaceTransform.WrapAngle(state.phase - reference.phase)) < .00001f
            && intent.Equals(expected) && Random(state) == Random(reference),
            "independent decisions use current facts and the continuing random stream, regardless of previous goal or cache refreshes");
        Vector2 actualOffset = OrbitSphere.Offset(target.pose.position, own.pose.position);
        Check(Mathf.Abs(SpaceTransform.WrapAngle(state.phase - Mathf.Atan2(actualOffset.y, actualOffset.x)))
            <= rule.tactic.repositionDegrees * Mathf.Deg2Rad + .00001f,
            "a moving target near the pole uses its current tangent frame for the new angular offset");
        Check(Read<CombatAttackPose>(a, "attackPose").distance == 80f
            && Read<int>(a, "nextPoseTick") < deadline + 1000,
            "a reposition deadline forces fresh pose scoring even before the pose and trajectory cache timers expire");
        intent.Sample(0f, out var origin, out _, out _, out _);
        Check(own.pose.Equals(before) && OrbitSphere.ArcCells(origin, own.pose.position) < .01f,
            "replacement trajectory starts at the real position without overwriting velocity or angular state");
        rule.tactic.distance = savedDistance;

        uint randomBeforeRule = Random(state);
        var hold = new CombatStrategyRule { tactic = new CombatTacticSettings { behavior = CombatTactic.HoldRange } };
        strategy.maneuverRules.Insert(0, hold);
        Set(a, "nextTacticTick", 0); Plan(a, deadline + 1);
        Check(Read<CombatAttackPose>(a, "attackPose").behavior == CombatTactic.HoldRange
            && state.nextTick == 0 && Random(state) == randomBeforeRule,
            "a real rule change replaces intent without reseeding the controller");
        strategy.maneuverRules.Remove(hold);
        Set(a, "nextTacticTick", 0); Plan(a, deadline + 2);
        Check(state.nextTick > deadline + 2 && Random(state) != initialRandom,
            "returning to automatic maneuver starts a new decision without replaying the first random draw");
        own.pose = savedOwn;
        target.pose = savedTarget;
    }

    static void CheckAiLogSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), "delta-v-ai-log-" + Guid.NewGuid() + ".xml");
        var settings = new DeltaVSettings { logAI = true };
        try
        {
            Scribe.saver.InitSaving(path, "settings"); settings.ExposeData(); Scribe.saver.FinalizeSaving();
            var loaded = new DeltaVSettings(); Scribe.loader.InitLoading(path); loaded.ExposeData(); Scribe.loader.FinalizeLoading();
            Check(loaded.logAI && !loaded.logCombat && !loaded.logDevelopment, "AI log setting roundtrips independently through real Scribe");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
