using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;
#if MOTHERSHIP_PRODUCTION
using HarmonyLib;
#endif
#if MOTHERSHIP_OFFLINE
using Pool = DeltaV.ShipAutoManeuverControl.OfflinePool;
using Candidate = DeltaV.ShipAutoManeuverControl.EngagementCandidate;
using ShotInput = DeltaV.CombatEngagementEvaluation.OfflineShot;
using Benefit = Program.MothershipSharedDamageReference.Benefit;
#endif

internal static partial class Program
{
    static readonly JsonSerializerOptions MothershipJson = new()
    {
        IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    static readonly string[] MothershipScenarios = { "head-on", "hull-edge", "range-edge", "asymmetric-damage" };
    static readonly System.Reflection.MethodInfo MothershipPlanMethod =
        typeof(ShipAutoManeuverControl).GetMethod("PlanTacticTrajectory", Private);

    static void RunMothershipEngagement(UniverseManager universe, Faction player, Faction enemy)
    {
        var args = Environment.GetCommandLineArgs();
        int flag = Array.IndexOf(args, "--mothership-engagement");
        using var fixturePatch = new EngagementFixturePatch();
        var fixture = new MothershipEngagementFixture(universe, player, enemy, args[flag + 1]);
        DeltaVMod.CurrentSettings.logPerformance = false;
#if MOTHERSHIP_PRODUCTION
#if MOTHERSHIP_AIM_REFRESH_TIMING
        RunAimRefreshTiming(universe, fixture, args[flag + 2], args[flag + 3] == "count");
        return;
#elif MOTHERSHIP_HEADING_TIMING
        RunHeadingTiming(universe, fixture, args[flag + 2]);
        return;
#else
        var experimentProbe = new Harmony("DeltaV.Regression.MothershipProduction");
        if (args[flag + 3] == "topology")
        {
            CheckMothershipTopology(fixture);
            return;
        }
        if (args[flag + 3] == "health")
        {
            experimentProbe.Patch(typeof(ModsConfig).TypeInitializer,
                prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
            experimentProbe.Patch(typeof(CompProjectileInterceptor).TypeInitializer,
                prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
            experimentProbe.CreateClassProcessor(typeof(Patch_CombatProtectionHealth)).Patch();
            CheckProductionProtection(universe, fixture, enemy);
            return;
        }
        if (args[flag + 3] == "heading")
        {
            CompareMothershipRailFlights(universe, fixture, args[flag + 2], experimentProbe);
            return;
        }
        if (args[flag + 3] == "aim-cause")
        {
            CompareRailAimTiming(universe, fixture, args[flag + 2], experimentProbe);
            return;
        }
        PatchProductionCounts(experimentProbe);
        var production = new List<object>();
#endif
#endif
#if !MOTHERSHIP_HEADING_TIMING && !MOTHERSHIP_AIM_REFRESH_TIMING
#if MOTHERSHIP_OFFLINE
        string mode = args[flag + 3];
        int round = int.Parse(args[flag + 4]);
        int window = int.Parse(args[flag + 5]);
        if (mode == "cases")
        {
            fixture.CheckSharedDamageCases();
            return;
        }
        var rows = new List<MothershipRow>();
        var crossover = new List<CrossoverRow>();
#endif
        for (int scene = 0; scene < MothershipScenarios.Length; scene++)
        {
            var ships = fixture.Build(scene);
            foreach (var side in new[] { (Own: ships.Own, Foe: ships.Enemy, Name: "left"),
                (Own: ships.Enemy, Foe: ships.Own, Name: "right") })
            {
                if (scene == 0)
                {
                    var guns = ShipAutoManeuverControl.CaptureAttackWeapons(side.Own.member, side.Own);
                    var families = guns.Select(x => x.gun.source.parent.def.defName).ToHashSet();
                    Check(families.Contains("DeltaV_Weapon_LaserTurret") &&
                        families.Contains("DeltaV_Weapon_MissileLauncher") && families.Any(k => k.Contains("Main")),
                        "production capture accepts real-layout mixed weapons");
                    Check(guns.Any(x => x.gun.source.parent.def.defName == "DeltaV_Weapon_RailgunLoader" &&
                        x.shot.energy.active.Count == 10), "production energy evaluation activates ten railgun stages");
                }
#if MOTHERSHIP_OFFLINE
                if (mode == "crossover")
                    CompareMothershipCrossover(side.Own, side.Foe, MothershipScenarios[scene], side.Name,
                        universe, window, args[flag + 2], crossover);
                else
                    rows.Add(CompareMothership(side.Own, side.Foe, MothershipScenarios[scene], side.Name,
                        universe, mode, round, window));
#elif MOTHERSHIP_PRODUCTION
                production.Add(CompareProductionTables(universe, side.Own, side.Foe, scene, side.Name, args[flag + 2]));
#else
                Console.WriteLine("PRODUCTION_PLAN " + JsonSerializer.Serialize(
                    ReadMothershipPlan(MakeMothershipPlanner(universe, side.Own, side.Foe), side.Own, side.Foe), MothershipJson));
#endif
            }
        }
#if MOTHERSHIP_OFFLINE
        string file = mode == "time" ? $"time-{round}.jsonl" : mode + ".jsonl";
        var report = mode == "crossover"
            ? crossover.Select(x => JsonSerializer.Serialize(x, MothershipJson))
            : rows.Select(x => JsonSerializer.Serialize(x, MothershipJson));
        File.WriteAllLines(Path.Combine(args[flag + 2], file), report);
#elif MOTHERSHIP_PRODUCTION
        File.WriteAllLines(Path.Combine(args[flag + 2], "production.jsonl"),
            production.Select(row => JsonSerializer.Serialize(row, MothershipJson)));
#endif
#endif
    }

    static ShipAutoManeuverControl MakeMothershipPlanner(UniverseManager universe,
        ManeuverMotionObject own, ManeuverMotionObject opponent)
    {
        var plan = new ShipAutoManeuverControl(universe, own.member.Identity);
        ConfigureMothershipPlanner(plan, opponent);
        return plan;
    }

    static void ConfigureMothershipPlanner(ShipAutoManeuverControl plan, ManeuverMotionObject opponent)
    {
        Set(plan, "anchorTarget", CombatTarget.ForShip(opponent.member.Identity));
        Set(plan, "anchorDistance", 0f);
        Set(plan, "offenseEnabled", true);
    }

    sealed record MothershipPlan(bool Valid, int Start, int Distance, int Heading, int Phase, int[] Track);

#if MOTHERSHIP_AIM_REFRESH_TIMING
    static void RunAimRefreshTiming(UniverseManager universe, MothershipEngagementFixture fixture, string outPath, bool count)
    {
        DeltaVMod.CurrentSettings.logPerformance = count;
        var perf = universe.Performance;
        var stageCalls = (int[])typeof(DeltaVPerformanceProbe).GetField("calls", Private).GetValue(perf);
        var samples = new List<object>();
        var modes = new[] { (false, "baseline"), (true, "authority") };
        for (int scene = 0; scene < MothershipScenarios.Length; scene++)
        {
            var pair = fixture.Build(scene);
            foreach (var (own, foe, side) in new[] { (pair.Own, pair.Enemy, "left"), (pair.Enemy, pair.Own, "right") })
            {
                (int Candidates, long Rays, int[] Bits) Trial(int mode, bool cold, int round)
                {
                    var (authority, name) = modes[mode];
                    ShipAutoManeuverControl.OfflineReuseAuthority = authority;
                    Set(Find.TickManager, "ticksGameInt", 0);
                    universe.Controls.For(own.member).FireControl.Decide(own.member, own);
                    if (cold) { universe.Protection.Forget(own.map); universe.Protection.Forget(foe.map); }
                    var planner = MakeMothershipPlanner(universe, own, foe);
                    var read = (FleetPlan)MothershipPlanMethod.CreateDelegate(typeof(FleetPlan), planner);
                    var pose = ShipRegistry.ShipPose(foe.member);
                    if (!own.TryGetDrive(DriveSubject.Of(own.member), out var drive)) throw new InvalidOperationException("Mothership has no drive");
                    var flight = new CombatTrajectoryIntent[5];
                    long candidateStart = perf.engagementCandidates, rayStart = universe.Protection.Scans;
                    long start = Stopwatch.GetTimestamp();
                    bool valid = read(own, pose, 0, drive, out flight[0]);
                    long initial = Stopwatch.GetTimestamp() - start, refresh = 0;
                    int angles = stageCalls[(int)PerformanceStage.DrivePreviewAngular];
                    int aim = stageCalls[(int)PerformanceStage.WeaponAim];
                    for (int i = 1; i < flight.Length; i++)
                    {
                        Set(Find.TickManager, "ticksGameInt", i * 6);
                        start = Stopwatch.GetTimestamp();
                        valid &= read(own, pose, 0, drive, out flight[i]);
                        refresh += Stopwatch.GetTimestamp() - start;
                    }
                    int candidates = (int)(perf.engagementCandidates - candidateStart);
                    long rays = universe.Protection.Scans - rayStart;
                    if (!valid || candidates <= 0 || !cold && rays != 0)
                        throw new InvalidOperationException($"Invalid refresh trial {scene}/{side}/{name}/{round}");
                    var score = (CombatAttackPose)typeof(ShipAutoManeuverControl).GetField("attackPose", Private).GetValue(planner);
                    var reposition = (CombatReposition)typeof(ShipAutoManeuverControl).GetField("reposition", Private).GetValue(planner);
                    if (round > 0)
                    {
                        double Ms(long n) => n * (1000.0 / Stopwatch.Frequency);
                        samples.Add(new { scene = MothershipScenarios[scene], side, mode = name, cold, round, candidates, rays,
                            initialMs = Ms(initial), refreshMs = Ms(refresh), totalMs = Ms(initial + refresh),
                            angles = stageCalls[(int)PerformanceStage.DrivePreviewAngular] - angles,
                            aim = stageCalls[(int)PerformanceStage.WeaponAim] - aim });
                    }
                    return (candidates, rays, new[] { BitConverter.SingleToInt32Bits(score.distance),
                        BitConverter.SingleToInt32Bits(score.heading), BitConverter.SingleToInt32Bits(reposition.phase) }
                        .Concat(flight.SelectMany(FleetTrajectoryBits)).ToArray());
                }
                foreach (int mode in Enumerable.Range(0, modes.Length)) Trial(mode, true, 0);
                foreach (bool cold in new[] { true, false })
                for (int round = 1; round <= (count ? 1 : 5); round++)
                {
                    var views = new (int Candidates, long Rays, int[] Bits)[modes.Length];
                    for (int i = 0; i < modes.Length; i++)
                    {
                        int mode = (round + (side == "right" ? -i : i) + modes.Length) % modes.Length;
                        views[mode] = Trial(mode, cold, round);
                    }
                    Check(views.Skip(1).All(v => v.Candidates == views[0].Candidates && v.Rays == views[0].Rays &&
                        v.Bits.SequenceEqual(views[0].Bits)), "aim refresh variants exactly match search and sampled flight reference");
                }
            }
        }
        File.WriteAllLines(Path.Combine(outPath, count ? "refresh-count.jsonl" : "refresh-time.jsonl"),
            samples.Select(v => JsonSerializer.Serialize(v, MothershipJson)));
    }
#endif

#if MOTHERSHIP_HEADING_TIMING
    static void RunHeadingTiming(UniverseManager universe, MothershipEngagementFixture fixture, string outPath)
    {
        var perf = universe.Performance;
        var totals = (long[])typeof(DeltaVPerformanceProbe).GetField("totals", Private).GetValue(perf);
        var calls = (int[])typeof(DeltaVPerformanceProbe).GetField("calls", Private).GetValue(perf);
        var output = new List<object>();
        for (int scene = 0; scene < MothershipScenarios.Length; scene++)
        {
            var pair = fixture.Build(scene);
            foreach (var (own, foe, side) in new[] { (pair.Own, pair.Enemy, "left"), (pair.Enemy, pair.Own, "right") })
            {
                (int Candidates, int[] Choice) Trial(bool shared, bool cold, bool profile, int round)
                {
                    CombatWeaponOutputTable.OfflineSharedAim = shared;
                    DeltaVMod.CurrentSettings.logPerformance = profile;
                    Set(Find.TickManager, "ticksGameInt", 0);
                    universe.Controls.For(own.member).FireControl.Decide(own.member, own);
                    if (cold)
                    {
                        universe.Protection.Forget(own.map);
                        universe.Protection.Forget(foe.map);
                    }
                    var planner = MakeMothershipPlanner(universe, own, foe);
                    var read = (FleetPlan)MothershipPlanMethod.CreateDelegate(typeof(FleetPlan), planner);
                    var pose = ShipRegistry.ShipPose(foe.member);
                    var drive = new DeltaV.DriveInfo(own.layout);
                    long before = perf.engagementCandidates, rays = universe.Protection.Scans;
                    var beforeTimes = (long[])totals.Clone();
                    var beforeCalls = (int[])calls.Clone();
                    long start = Stopwatch.GetTimestamp();
                    bool valid = read(own, pose, 0, drive, out _);
                    long initial = Stopwatch.GetTimestamp() - start, refresh = 0;
                    for (int tick = 6; tick < 30; tick += 6)
                    {
                        Set(Find.TickManager, "ticksGameInt", tick);
                        start = Stopwatch.GetTimestamp();
                        valid &= read(own, pose, 0, drive, out _);
                        refresh += Stopwatch.GetTimestamp() - start;
                    }
                    double TicksToMs(long ticks) => ticks * (1000.0 / Stopwatch.Frequency);
                    double totalMs = TicksToMs(initial + refresh);
                    int candidates = (int)(perf.engagementCandidates - before);
                    long scans = universe.Protection.Scans - rays;
                    if (!valid || candidates <= 0 || !cold && scans != 0)
                        throw new InvalidOperationException($"Invalid timing trial {scene}/{side}/{round}");
                    double Ms(PerformanceStage stage) =>
                        (totals[(int)stage] - beforeTimes[(int)stage]) * (1000.0 / Stopwatch.Frequency);
                    int Calls(PerformanceStage stage) => calls[(int)stage] - beforeCalls[(int)stage];
                    if (round > 0)
                        output.Add(new
                        {
                            scene = MothershipScenarios[scene], side, shared, cold, profile, round, candidates, scans,
                            totalMs, planMs = Ms(PerformanceStage.CombatPlan), scoreMs = Ms(PerformanceStage.EngagementScore),
                            initialMs = TicksToMs(initial), refreshMs = TicksToMs(refresh),
                            aimMs = Ms(PerformanceStage.WeaponAim), aimCalls = Calls(PerformanceStage.WeaponAim),
                            weaponMs = Ms(PerformanceStage.EngagementWeaponTable), scanMs = Ms(PerformanceStage.EngagementProtectionScan),
                            routeMs = Ms(PerformanceStage.CombatRoute), routeCalls = Calls(PerformanceStage.CombatRoute),
                            damageCalls = Calls(PerformanceStage.EngagementDamage)
                        });
                    var choice = (CombatAttackPose)typeof(ShipAutoManeuverControl).GetField("attackPose", Private).GetValue(planner);
                    var reposition = (CombatReposition)typeof(ShipAutoManeuverControl).GetField("reposition", Private).GetValue(planner);
                    return (candidates, new[] { BitConverter.SingleToInt32Bits(choice.distance),
                        BitConverter.SingleToInt32Bits(choice.heading), BitConverter.SingleToInt32Bits(reposition.phase) });
                }
                // Warm code in both arms. Keep protection-path state matched for each measured pair.
                Trial(false, true, false, 0);
                Trial(true, true, false, 0);
                foreach (bool profile in new[] { false, true })
                foreach (bool cold in new[] { true, false })
                for (int round = 1; round <= 5; round++)
                {
                    bool first = (round % 2 == 0) ^ (side == "right");
                    var a = Trial(first, cold, profile, round);
                    var b = Trial(!first, cold, profile, round);
                    if (a.Candidates != b.Candidates || !a.Choice.SequenceEqual(b.Choice))
                        throw new InvalidOperationException($"Shared aim changed coarse scoring choice {scene}/{side}");
                }
            }
        }
        DeltaVMod.CurrentSettings.logPerformance = false;
        File.WriteAllLines(Path.Combine(outPath, "heading-timing.jsonl"),
            output.Select(row => JsonSerializer.Serialize(row, MothershipJson)));
        Console.WriteLine($"MOTHERSHIP_HEADING_TIMING {output.Count} samples");
    }
#endif

    static MothershipPlan ReadMothershipPlan(ShipAutoManeuverControl planner,
        ManeuverMotionObject own, ManeuverMotionObject foe, bool compareHeading = false)
    {
        var read = (FleetPlan)MothershipPlanMethod.CreateDelegate(typeof(FleetPlan), planner);
        if (!own.TryGetDrive(DriveSubject.Of(own.member), out var drive)) throw new InvalidOperationException("Mothership has no drive");
        bool valid = read(own, ShipRegistry.ShipPose(foe.member), 0, drive, out var intent);
        if (!valid) return new(false, 0, 0, 0, 0, Array.Empty<int>());
#if MOTHERSHIP_PRODUCTION
        if (compareHeading) Check(CheckLockedHeading(planner, intent), "score, command and flight heading agree");
#endif
        var pose = (CombatAttackPose)typeof(ShipAutoManeuverControl).GetField("attackPose", Private).GetValue(planner);
        var movement = (CombatReposition)typeof(ShipAutoManeuverControl).GetField("reposition", Private).GetValue(planner);
        return new(true, intent.startTick, BitConverter.SingleToInt32Bits(pose.distance),
            BitConverter.SingleToInt32Bits(pose.heading), BitConverter.SingleToInt32Bits(movement.phase),
            FleetTrajectoryBits(intent));
    }

#if MOTHERSHIP_PRODUCTION
    static void CheckMothershipTopology(MothershipEngagementFixture fixture)
    {
        var (a, b) = fixture.Build(3);
        Check(a.member.HullCells.Count() == 2000 && b.member.HullCells.Count() == 1906,
            "production discovers both connected Mothership domains");
        foreach (var body in new[] { a, b })
        {
            var ship = (FoundationShip)body.member;
            var original = ShipRegistry.ControlDomain(ship.Structure);
            Vector2 center = ship.HullCenter;
            Check(center.Equals(SpaceTransform.BoundsCenterOfCells(original)), "production caches the exact fixture center");
            int tick = Find.TickManager.TicksGame;
            int east = original.Max(p => p.x);
            var edge = original.Where(c => c.x == east).ToArray();
            void RemoveFoundation(IntVec3 cell)
            {
                body.map.terrainGrid.foundationGrid[body.map.cellIndices.CellToIndex(cell)] = null;
                SubstructureOwnership.For(body.map).NotifyFoundationRemoved(cell);
            }
            RemoveFoundation(edge[0]);
            Check(!ship.HullCells.Contains(edge[0]) && Find.TickManager.TicksGame == tick,
                "a single deleted cell is absent from the live domain immediately");
            foreach (var cell in edge.Skip(1)) RemoveFoundation(cell);
            var updated = ShipRegistry.ControlDomain(ship.Structure);
            Check(Find.TickManager.TicksGame == tick && !ReferenceEquals(updated, original)
                && edge.All(c => !updated.Contains(c)) && !ship.HullCenter.Equals(center)
                && ship.HullCenter.Equals(SpaceTransform.BoundsCenterOfCells(updated)),
                "same-tick deletion invalidates the production domain and center");
            var missileRack = ship.Weapons.First(w => w.parent.def.defName == "DeltaV_Weapon_MissileLauncher").parent;
            Check(ship.Contains(missileRack), "launcher initially belongs to the production domain");
            foreach (var cell in missileRack.OccupiedRect().Cells.Where(updated.Contains)) RemoveFoundation(cell);
            Check(Find.TickManager.TicksGame == tick && !ship.Contains(missileRack)
                && !ship.Weapons.Any(w => w.parent == missileRack), "unsupported launcher immediately leaves ownership and weapon list");
        }
    }

    static readonly List<CombatEngagementEvaluation.Result> productionScores = new();
    static int productionAim, productionExact;
    static int lockedHeadingSamples;
    static void CountProductionAim() => productionAim++;
    static void CountProductionExact() => productionExact++;
    static void RecordProductionScore(CombatEngagementEvaluation.Result __result) => productionScores.Add(__result);

    static CompSpaceWeapon causeRail;
    static Thing causePart;
    static CombatTarget causeTarget;
    static bool causeLegacy, causeFixed, causeContinuous, causePlanLeft;

    static void SetCauseTarget(CombatEngagementEvaluation.Result __result)
    {
        if (__result.fixedAimWeapon != causeRail) return;
        if (causeLegacy) __result.fixedAimWeapon = null;
        else if (causeFixed)
        {
            __result.fixedAimPart = causePart;
            __result.fixedAimTarget = causeTarget;
        }
    }

    static void PinLegacyAssignment(ShipFireControl __instance, ShipBase ship)
    {
        if (!causeLegacy || ship.Identity != causeRail.OwnerShip.Identity) return;
        var assignments = (Dictionary<long, CombatTarget>)typeof(ShipFireControl)
            .GetField("automaticTargets", Private).GetValue(__instance);
        assignments[causeRail.Identity] = causeTarget;
    }

    static bool ContinueCauseHeading(CombatTrajectoryIntent intent, float heading, float elapsed,
        ref CombatTrajectoryIntent __result)
    {
        if (!causePlanLeft || !causeContinuous || elapsed <= 0f) return true;
        // This diagnostic arm advances the old angular reference instead of resetting it to the measured hull.
        float decay = intent.headingDecay;
        float factor = Mathf.Exp(decay * elapsed);
        float shift = SpaceTransform.WrapAngle(-intent.frameAngle - heading) * factor;
        intent.headingError += shift * (1f - decay * elapsed);
        intent.headingRateError += decay * decay * elapsed * shift;
        intent.frameAngle = -heading;
        __result = intent;
        return false;
    }

    static void CompareRailAimTiming(UniverseManager universe, MothershipEngagementFixture fixture,
        string outPath, Harmony patch)
    {
        void Add(Type type, string method, string prefix = null, string postfix = null) => patch.Patch(
            AccessTools.Method(type, method), prefix: prefix == null ? null : new HarmonyMethod(typeof(Program), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(Program), postfix));
        patch.Patch(typeof(ModsConfig).TypeInitializer, prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
        Add(typeof(SpaceProjectileService), "RegisterShotVisuals", prefix: nameof(BattleSceneOnly));
        Add(typeof(CombatWeaponOutputTable), "Evaluate", postfix: nameof(SetCauseTarget));
        Add(typeof(ShipFireControl), "AssignAutomaticTargets", postfix: nameof(PinLegacyAssignment));
        Add(typeof(CombatFlightPlanner), "RetargetHeading", prefix: nameof(ContinueCauseHeading));
        var projectiles = (System.Collections.IList)typeof(SpaceProjectileService).GetField("projectiles", Private)
            .GetValue(universe.Projectiles);
        var results = new List<object>();
        foreach (var arm in new[] { "legacy-fixed", "fixed-rebase", "dynamic-rebase", "fixed-continuous", "dynamic-continuous" })
        {
            causeLegacy = arm == "legacy-fixed";
            causeFixed = arm.StartsWith("fixed");
            causeContinuous = arm.EndsWith("continuous");
            var (left, right) = fixture.BuildRailgunFlight(false);
            causeRail = left.member.Weapons.Single(w => w.parent.def.defName == "DeltaV_Weapon_RailgunLoader");
            causePart = right.member.Weapons.Single(w => w.parent.thingIDNumber == 200115).parent;
            causeTarget = CombatTarget.ForShip(right.member.Identity, causePart.thingIDNumber);
            void Start(ManeuverMotionObject own, ManeuverMotionObject foe)
            {
                var controller = universe.Controls.For(own.member);
                ConfigureMothershipPlanner(controller.AutoManeuver, foe);
                controller.FireControl.Decide(own.member, own);
                bool Sample(out SpacePose pose) { pose = ShipRegistry.ShipPose(foe.member); return true; }
                var plan = (CombatTrajectoryPlanner)MothershipPlanMethod.CreateDelegate(typeof(CombatTrajectoryPlanner), controller.AutoManeuver);
                bool Track(SpaceObject body, SpacePose target, float radius, DeltaV.DriveInfo drive, out CombatTrajectoryIntent trajectory)
                {
                    causePlanLeft = own == left;
                    try { return plan(body, target, radius, drive, out trajectory); }
                    finally { causePlanLeft = false; }
                }
                universe.Autopilot.TrackOrbit(DriveSubject.Of(own.member), Sample, 0f, Track);
            }
            Start(left, right);
            Start(right, left);
            var controller = universe.Controls.For(left.member);
            var defense = universe.Controls.For(right.member).FireControl;
            var fire = controller.FireControl;
            var planner = controller.AutoManeuver;
            var rightRail = right.member.Weapons.Single(w => w.parent.def.defName == "DeltaV_Weapon_RailgunLoader");
            int firstReady = -1, first = -1, readyOnRefresh = -1, cooldown = 0;
            int lastPart = -1;
            var parts = new List<object>();
            for (int tick = 0; tick < 1800; tick++)
            {
                Set(Find.TickManager, "ticksGameInt", tick);
                var priorTarget = causeLegacy ? causeTarget : (CombatTarget)typeof(ShipAutoManeuverControl)
                    .GetField("fixedAimTarget", Private).GetValue(planner);
                Vector3? extraLead = null;
                if (!causeLegacy && priorTarget.HasLock && fire.TryResolveAutomatic(left.member, left, priorTarget,
                    out var priorResolved, planner.FixedAimPart))
                {
                    var info = SpaceWeaponBallistics.Read(causeRail, left, causeRail.QueryEnergy, true).aim;
                    if (SpaceWeaponBallistics.TryAim(info, left.pose, priorResolved.Pose, out var lead)) extraLead = lead;
                }
                RecoveryTick(universe.Autopilot, tick);
                var shared = planner.SharedFixedAim;
                bool refresh = shared.HasValue && shared.Value.Tick == tick;
                CombatTarget handle = refresh ? shared.Value.Target : priorTarget;
                if (handle.HasLock && handle.SystemId != lastPart)
                { lastPart = handle.SystemId; parts.Add(new { tick, part = lastPart }); }
                if (causeLegacy && fire.TryResolveAutomatic(left.member, left, handle, out var oldResolved, causePart))
                {
                    var info = SpaceWeaponBallistics.Read(causeRail, left, causeRail.QueryEnergy, true).aim;
                    if (SpaceWeaponBallistics.TryAim(info, left.pose, oldResolved.Pose, out var lead)) extraLead = lead;
                }
                Vector3? point = refresh ? shared.Value.Point : extraLead;
                if (point.HasValue && handle.HasLock)
                {
                    var shot = new SpaceWeaponShot(universe, causeRail, left, new WeaponFireRequest(point.Value, handle));
                    if ((bool)Call(shot, "Prepare"))
                    {
                        if (firstReady < 0) firstReady = tick;
                        if (refresh && readyOnRefresh < 0) readyOnRefresh = tick;
                    }
                }
                fire.Decide(left.member, left);
                int before = projectiles.Count;
                fire.TickAutoVolley(left.member, left, false);
                if (projectiles.Count > before && causeRail.CooldownTicksLeft > 0)
                {
                    first = tick;
                    cooldown = causeRail.CooldownTicksLeft;
                    break;
                }
                if (rightRail.CooldownTicksLeft <= 0)
                { defense.Decide(right.member, right); defense.TickAutoVolley(right.member, right, false); }
            }
            foreach (var body in new[] { left, right }) universe.Autopilot.Cancel(DriveSubject.Of(body.member));
            universe.Projectiles.ResetFlow();
            var row = new { arm, firstReady, readyOnRefresh, first, cooldown, parts };
            results.Add(row);
            Console.WriteLine("AIM_CAUSE " + JsonSerializer.Serialize(row, MothershipJson));
            Check(first >= 0 && firstReady >= 0 && firstReady <= first, "aim experiment reaches actual launch and cooldown");
        }
        File.WriteAllLines(Path.Combine(outPath, "aim-cause.jsonl"), results.Select(r => JsonSerializer.Serialize(r, MothershipJson)));
        patch.UnpatchAll(patch.Id);
    }

    static bool CheckLockedHeading(ShipAutoManeuverControl planner, CombatTrajectoryIntent trajectory)
    {
        var pose = (CombatAttackPose)typeof(ShipAutoManeuverControl).GetField("attackPose", Private).GetValue(planner);
        if (!pose.fireHeading || trajectory.holdCurrent && !trajectory.brakeTarget.HasValue) return true;
        var action = (CombatManeuverIntent)typeof(ShipAutoManeuverControl).GetField("maneuverIntent", Private).GetValue(planner);
        lockedHeadingSamples++;
        return Math.Abs(SpaceTransform.WrapAngle(action.heading + trajectory.frameAngle)) <= 1e-6f &&
            (planner.SharedFixedAim.HasValue || Math.Abs(SpaceTransform.WrapAngle(pose.heading - action.heading)) <= 1e-6f);
    }

    static readonly Dictionary<CompSpaceWeapon, Dictionary<WeaponReject, int>> railRejects = new();
    static readonly Dictionary<CompSpaceWeapon, (int Part, float Error)> railAlignment = new();
    static readonly Dictionary<CombatEngagementEvaluation.Result, int> scoredRailTargets = new();
    static readonly Dictionary<CompSpaceWeapon, ShipAutoManeuverControl> railPlanners = new();
    static int railAimCalls, sharedRailAttempts;
    static bool countRailAim;
    static void CountRailAim(WeaponAimInfo info)
    {
        if (countRailAim && info.Projectile?.defName == "DeltaV_Projectile_RailgunSlug") railAimCalls++;
    }

    static void CheckSharedRailShot(SpaceWeaponShot __instance)
    {
        if (!railPlanners.TryGetValue(__instance.weapon, out var planner)) return;
        Check(planner.SharedFixedAim is FixedWeaponAim, "rail shot reads the shared solution");
        var aim = planner.SharedFixedAim.Value;
        Check(aim.Tick == Find.TickManager.TicksGame && aim.Weapon == __instance.weapon
            && aim.Target == __instance.guidanceTarget && aim.Point.Equals(__instance.aimPoint),
            "rail shot uses the same tick, weapon, part and world intercept as the helm");
        Vector2 projected = SpaceTransform.UniverseToMapPoint(__instance.container, __instance.weapon.parent.Map, aim.Point);
        var prepared = (Vector2?)typeof(SpaceWeaponShot).GetField("preparedAimMap", Private).GetValue(__instance);
        Check(prepared.HasValue && prepared.Value.Equals(projected), "fire lead angle projects the shared point at the live muzzle");
        sharedRailAttempts++;
    }

    static void RecordRailScoringTarget(CombatWeaponOutputTable __instance, CombatEngagementEvaluation.Result __result)
    {
        if (__result.outgoing <= 0f) return;
        var deliveries = (System.Collections.IEnumerable)typeof(CombatWeaponOutputTable)
            .GetField("contributions", Private).GetValue(__instance);
        var paths = (System.Collections.IList)typeof(CombatWeaponOutputTable).GetField("paths", Private).GetValue(__instance);
        foreach (var delivery in deliveries)
        {
            var row = delivery.GetType().GetField("Item1").GetValue(delivery);
            var battery = (CombatEngagementEvaluation.Battery)row.GetType()
                .GetField("Battery", Private).GetValue(row);
            if (!battery.outgoing) continue;
            var path = paths[(int)delivery.GetType().GetField("Item2").GetValue(delivery)];
            var target = (Thing)path.GetType().GetField("Item2").GetValue(path);
            scoredRailTargets[__result] = target?.thingIDNumber ?? 0;
            return;
        }
    }
    static void CountRailReject(SpaceWeaponShot __instance, WeaponReject reason)
    {
        if (!railRejects.TryGetValue(__instance.weapon, out var counters)) return;
        counters.TryGetValue(reason, out int count);
        counters[reason] = count + 1;
        if (reason == WeaponReject.Alignment)
            railAlignment[__instance.weapon] = (__instance.guidanceTarget.SystemId,
                Mathf.Abs(SpaceTransform.WrapAngle(__instance.desiredMapAngle - __instance.launchMapAngle)) * Mathf.Rad2Deg);
    }

    static void CompareMothershipRailFlights(UniverseManager universe, MothershipEngagementFixture fixture,
        string outPath, Harmony patch)
    {
        patch.Patch(typeof(ModsConfig).TypeInitializer,
            prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
        patch.Patch(AccessTools.Method(typeof(SpaceProjectileService), "RegisterShotVisuals"),
            prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
        patch.Patch(AccessTools.Method(typeof(SpaceWeaponShot), "Reject"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountRailReject)));
        patch.Patch(AccessTools.Method(typeof(SpaceWeaponShot), "Prepare"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CheckSharedRailShot)));
        patch.Patch(AccessTools.Method(typeof(SpaceWeaponBallistics), "TryAim"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountRailAim)));
        patch.Patch(AccessTools.Method(typeof(CombatWeaponOutputTable), "Evaluate"),
            postfix: new HarmonyMethod(typeof(Program), nameof(RecordRailScoringTarget)));
        CheckMothershipHeadingPolicy(universe, fixture);
        var projectiles = (System.Collections.IList)typeof(SpaceProjectileService)
            .GetField("projectiles", Private).GetValue(universe.Projectiles);
        var report = new List<object>();
        bool allLaunched = true;
        foreach (bool mirrored in new[] { false, true })
        {
            var pair = fixture.BuildRailgunFlight(mirrored);
            var frames = new[] { pair.Own, pair.Enemy };
            var subject = new DriveSubject[2];
            var rails = new CompSpaceWeapon[2];
            var planners = new ShipAutoManeuverControl[2];
            var references = new CombatTrajectoryIntent[2];
            var timeline = new List<object>();
            var first = new[] { -1, -1 };
            bool matched = true;
            int before = lockedHeadingSamples;
            int beforeShots = sharedRailAttempts;
            var aimUpdates = new int[2];
            railRejects.Clear();
            railAlignment.Clear();
            railPlanners.Clear();
            scoredRailTargets.Clear();
            for (int i = 0; i < frames.Length; i++)
            {
                var own = frames[i];
                var foe = frames[1 - i];
                rails[i] = own.member.Weapons.Single(weapon => weapon.parent.def.defName == "DeltaV_Weapon_RailgunLoader");
                railRejects.Add(rails[i], new Dictionary<WeaponReject, int>());
                var control = universe.Controls.For(own.member);
                var planner = control.AutoManeuver;
                ConfigureMothershipPlanner(planner, foe);
                control.FireControl.Decide(own.member, own);
                railPlanners.Add(rails[i], planner);
                int side = i;
                planners[i] = planner;
                var plan = (FleetPlan)MothershipPlanMethod.CreateDelegate(typeof(FleetPlan), planner);
                bool Sample(out SpacePose pose) { pose = ShipRegistry.ShipPose(foe.member); return true; }
                bool Track(SpaceObject body, SpacePose target, float radius, DeltaV.DriveInfo drive,
                    out CombatTrajectoryIntent trajectory)
                {
                    railAimCalls = 0;
                    countRailAim = true;
                    bool valid = plan(body, target, radius, drive, out trajectory);
                    countRailAim = false;
                    if (valid)
                    {
                        Check(railAimCalls <= 1, "each helm aim update solves at most one rail intercept");
                        if (planner.SharedFixedAim is FixedWeaponAim aim)
                        {
                            Check(railAimCalls == 1 && aim.Weapon == rails[side], "published aim comes from one selected rail solve");
                            var choice = (CombatEngagementEvaluation.Result)typeof(ShipAutoManeuverControl)
                                .GetField("engagementResult", Private).GetValue(planner);
                            if (choice?.fixedAimWeapon != null)
                                Check(choice.fixedAimWeapon == aim.Weapon && choice.fixedAimTarget == aim.Target,
                                    "the selected coarse aim identity reaches the real helm update");
                            var ownPose = SpaceTransform.RigidPointPose(body.pose,
                                SpaceTransform.MapToShipLocal(own.map, own.member.HullCenter), 0f);
                            Vector2 toTarget = OrbitSphere.Offset(ownPose.position, target.position);
                            float expected = SpaceTransform.WrapAngle(aim.HullAngle - Mathf.Atan2(toTarget.y, toTarget.x));
                            Check(Mathf.Abs(SpaceTransform.WrapAngle(expected + trajectory.frameAngle)) <= 1e-6f,
                                "flight receives the heading of the shared intercept in the same update");
                            aimUpdates[side]++;
                        }
                        matched &= CheckLockedHeading(planner, trajectory);
                        references[side] = trajectory;
                    }
                    return valid;
                }
                subject[i] = new DriveSubject(own.objectId, own.member.Identity.key);
                universe.Autopilot.TrackOrbit(subject[i], Sample, 0f, Track);
                Check(rails[i].CooldownTicksLeft == 0, "closed-loop railgun starts ready");
            }
            for (int tick = 0; tick < 1800 && first.Any(x => x < 0); tick++)
            {
                RecoveryTick(universe.Autopilot, tick);
                for (int i = 0; i < frames.Length; i++)
                {
                    if (tick % 15 == 0)
                    {
                        var framePose = frames[i].pose;
                        var self = ShipRegistry.ShipPose(frames[i].member);
                        var target = ShipRegistry.ShipPose(frames[1 - i].member);
                        var line = OrbitSphere.Offset(self.position, target.position);
                        float bearing = Mathf.Atan2(line.y, line.x);
                        float mount = ShipOrientation.AngleFromRot4(rails[i].parent.Rotation);
                        var score = (CombatAttackPose)typeof(ShipAutoManeuverControl)
                            .GetField("attackPose", Private).GetValue(planners[i]);
                        var reference = references[i];
                        var scoreResult = (CombatEngagementEvaluation.Result)typeof(ShipAutoManeuverControl)
                            .GetField("engagementResult", Private).GetValue(planners[i]);
                        int forecastPart = scoreResult != null &&
                            scoredRailTargets.TryGetValue(scoreResult, out int targetId) ? targetId : -1;
                        float elapsed = Mathf.Max(0, tick - reference.startTick) / 60f;
                        reference.Sample(elapsed, out _, out _, out float referenceAngle, out float referenceRate);
                        reference.Sample(reference.seconds, out _, out _, out float horizonAngle, out _);
                        float Degrees(float angle) => SpaceTransform.WrapAngle(angle) * Mathf.Rad2Deg;
                        var info = SpaceWeaponService.AimInfo(rails[i], frames[i]);
                        float? centerLeadError = null;
                        if (SpaceWeaponBallistics.TryAim(info, framePose, target, out var point))
                        {
                            var lead = SpaceTransform.UniverseToMapPoint(frames[i], frames[i].map, point) - info.MuzzleMap;
                            centerLeadError = Degrees(Mathf.Atan2(lead.y, lead.x) - mount);
                        }
                        timeline.Add(new { tick, side = i, score = Degrees(score.heading), locked = score.fireHeading,
                            reference = Degrees(referenceAngle - bearing), horizon = Degrees(horizonAngle - bearing),
                            real = Degrees(framePose.angle - bearing), centerAxisError = Degrees(framePose.angle + mount - bearing),
                            yawRate = framePose.angularVelocity * Mathf.Rad2Deg, referenceRate = referenceRate * Mathf.Rad2Deg,
                            bearing = Degrees(bearing), brake = reference.holdCurrent, forecastPart,
                            forecastDamage = scoreResult?.outgoing, centerLeadError,
                            aimBrake = reference.brakeTarget.HasValue, targetPart = railAlignment.TryGetValue(rails[i], out var alignment)
                                ? alignment.Part : 0, lastAimError = alignment.Error });
                    }
                    if (first[i] >= 0) continue;
                    var frame = frames[i];
                    var fire = universe.Controls.For(frame.member).FireControl;
                    fire.Decide(frame.member, frame);
                    int launchedBefore = projectiles.Count;
                    railAimCalls = 0;
                    countRailAim = planners[i].FixedAimWeapon == rails[i];
                    fire.TickAutoVolley(frame.member, frame, false);
                    countRailAim = false;
                    Check(railAimCalls == 0, "coordinated volley performs no second rail intercept solve");
                    for (int j = launchedBefore; j < projectiles.Count; j++)
                    {
                        var projectile = projectiles[j];
                        var type = projectile.GetType();
                        var def = (ThingDef)type.GetField("def").GetValue(projectile);
                        if (def.defName != "DeltaV_Projectile_RailgunSlug" ||
                            !ReferenceEquals(type.GetField("launcher").GetValue(projectile), rails[i].parent)) continue;
                        first[i] = tick;
                        Check(rails[i].CooldownTicksLeft > 0, "rail launch commits the weapon cooldown");
                    }
                }
            }
            foreach (var id in subject) universe.Autopilot.Cancel(id);
            var row = new { mirrored, first, headingSamples = lockedHeadingSamples - before,
                aimUpdates, sharedAttempts = sharedRailAttempts - beforeShots,
                matched, rejects = rails.Select(rail => railRejects[rail].ToDictionary(p => p.Key.ToString(), p => p.Value)).ToArray(),
                lastAlignment = rails.Select(rail => railAlignment.TryGetValue(rail, out var last)
                    ? new { part = last.Part, errorDeg = last.Error } : null).ToArray() };
            report.Add(row);
            File.WriteAllLines(Path.Combine(outPath, mirrored ? "heading-trace-mirror.jsonl" : "heading-trace.jsonl"),
                timeline.Select(x => JsonSerializer.Serialize(x, MothershipJson)));
            Console.WriteLine("MOTHERSHIP_FIRE " + JsonSerializer.Serialize(row, MothershipJson));
            universe.Projectiles.ResetFlow();
            Check(matched && row.headingSamples > 0, "closed-loop execution retains scored heading");
            allLaunched &= first.All(tick => tick > 0);
        }
        File.WriteAllLines(Path.Combine(outPath, "heading-flights.jsonl"),
            report.Select(row => JsonSerializer.Serialize(row, MothershipJson)));
        Check(allLaunched, "both ships turn into range and launch railgun slugs automatically in both mirrors");
    }

    static void CheckMothershipHeadingPolicy(UniverseManager universe, MothershipEngagementFixture fixture)
    {
        var (own, foe) = fixture.Build(0);
        var planner = MakeMothershipPlanner(universe, own, foe);
        var self = ShipRegistry.ShipPose(own.member);
        self.angle -= own.member.FrameAngle;
        self.velocity = new Vector2(3f, -2f);
        self.angularVelocity = .12f;
        var target = ShipRegistry.ShipPose(foe.member);
        var offset = OrbitSphere.Offset(target.position, self.position);
        float phase = Mathf.Atan2(offset.y, offset.x) + Mathf.PI / 2;
        var drive = new DeltaV.DriveInfo(own.layout);
        var read = (FleetPlan)MothershipPlanMethod.CreateDelegate(typeof(FleetPlan), planner);
        Check(read(own, target, 0, drive, out var originalDrive) && originalDrive.yawAuthority > 0f,
            "the full plan supplies measured turning authority");
        foreach (var thruster in own.layout.thrusters) thruster.thrust *= .5f;
        own.layout.version++;
        Check(read(own, target, 0, drive, out var weakenedDrive) && weakenedDrive.yawAuthority < originalDrive.yawAuthority
            && weakenedDrive.yawAuthority == Mathf.Min(drive.PreviewAngular(1), drive.PreviewAngular(-1)),
            "a changed drive version refreshes turning authority in the same tick");
        var settings = new CombatTacticSettings { behavior = CombatTactic.Automatic, facing = CombatFacing.Automatic };
        var pose = new CombatAttackPose { heading = -.71f, distance = offset.magnitude, fireHeading = true };
        CombatManeuverIntent Action(bool preserve = false) =>
            (CombatManeuverIntent)Call(planner, "EngagementAction", self, target, pose, phase, drive, settings, preserve);
        var transfer = Action();
        Check(transfer.heading == pose.heading && transfer.bearing == phase, "large transfer retains the firing heading");
        transfer.bearing = Mathf.Atan2(offset.y, offset.x) + Mathf.PI / 4;
        var shortened = CombatFlightPlanner.Plan(self, target, transfer, drive, 0);
        Check(shortened.frameAngle == -pose.heading, "shortening the route retains the yaw target");
        pose.fireHeading = false;
        Check(Action().heading != pose.heading, "zero-output transfer can choose a thrust heading");
        settings.facing = CombatFacing.Target;
        Check(Action().heading == pose.heading, "explicit facing retains the requested heading");
        settings.facing = CombatFacing.Automatic;
        settings.behavior = pose.behavior = CombatTactic.Circle;
        pose.fireHeading = true;
        Check(!Action().bearing.HasValue && Action().heading == pose.heading && Action(true).heading == pose.heading,
            "circle route preview and execution use the same firing heading");

        var straightBrake = CombatFlightPlanner.Brake(self, drive, 0);
        var aimingBrake = CombatFlightPlanner.BrakeWhileAiming(self, target, pose.heading, drive, 0);
        aimingBrake.Sample(0, out var start, out var velocity, out float angle, out float omega);
        Check((start - self.position).sqrMagnitude < 1e-10f && (velocity - self.velocity).sqrMagnitude < 1e-8f &&
            Math.Abs(SpaceTransform.WrapAngle(angle - self.angle)) < 1e-5f && Math.Abs(omega - self.angularVelocity) < 1e-4f,
            "aiming brake begins at the real pose and velocities");
        aimingBrake.Sample(4, out var end, out var finalSpeed, out _, out _);
        straightBrake.Sample(4, out var brakeEnd, out var brakeSpeed, out _, out _);
        Check(end == brakeEnd && finalSpeed == brakeSpeed && aimingBrake.frameAngle == -pose.heading,
            "aiming brake retains the firing target with identical braking translation");

        bool Clear(CombatTrajectoryIntent intent)
        {
            object[] args = { own.member, intent, default(DriveSubject), 0 };
            return (bool)typeof(ShipAutoManeuverControl).GetMethod("RouteClear", Private).Invoke(planner, args);
        }
        Check(Clear(aimingBrake), "braking turn clears the distant opposing ship");
        var routeCheck = typeof(ShipAutoManeuverControl).GetMethod("RouteClearAt", Private);
        bool ClearRefresh(CombatTrajectoryIntent intent) => (bool)routeCheck.Invoke(planner,
            new object[] { own.member, intent, .1f, default(DriveSubject), 0 });
        var track = CombatFlightPlanner.Plan(self, target, new CombatManeuverIntent
            { distance = offset.magnitude, heading = pose.heading }, drive, 0);
        Check(ClearRefresh(track), "far hulls pass the angular refresh clearance check");
        track.Sample(1.1f, out var firstPosition, out _, out _, out _);
        foe.pose = new SpacePose(firstPosition, foe.pose.angle);
        Check(!ClearRefresh(track), "nearby hulls run the exact overlap check");
        var approaching = new SpacePose(OrbitSphere.Move(firstPosition, new Vector2(180f, 0f)), foe.pose.angle);
        approaching.velocity = OrbitSphere.Offset(approaching.position, firstPosition);
        foe.pose = approaching;
        Check(!ClearRefresh(track), "approaching hulls run the exact overlap check even when currently separated");
        foe.pose = new SpacePose(self.position, foe.pose.angle);
        Check(!Clear(aimingBrake), "braking turn is rejected when its swept ship overlaps");
    }

    static void PatchProductionCounts(Harmony patch)
    {
        patch.Patch(AccessTools.Method(typeof(CombatAttackWeapon), "Aim"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountProductionAim)));
        patch.Patch(AccessTools.Method(typeof(CombatEngagementEvaluation), "TryEstimate"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountProductionExact)));
        patch.Patch(AccessTools.Method(typeof(CombatDamageForecast), "Fire"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountProductionExact)));
        patch.Patch(AccessTools.Method(typeof(CombatEngagementEvaluation), "Evaluate",
            new[] { typeof(SpacePose), typeof(CombatAttackPose).MakeByRefType(), typeof(CombatManeuverCurves) }),
            postfix: new HarmonyMethod(typeof(Program), nameof(RecordProductionScore)));
    }

    static void CheckProductionProtection(UniverseManager universe, MothershipEngagementFixture fixture, Faction enemy)
    {
        var (own, target) = fixture.Build(0);
        var ship = (FoundationShip)target.member;
        var map = target.map;
        foreach (var hull in new[] { own.member, target.member })
        {
            var centerOffset = hull.HullCenter - SpaceTransform.ShipLocalOrigin(hull.Map);
            Check(centerOffset.sqrMagnitude > 0f, "aim origin fixture has distinct map and hull centers");
            var source = new Vector2(140, -38);
            var aims = universe.ProtectionFor(hull).Select(source, CombatPart.Weapons);
            Check(aims.Length == 7 && aims[^1].Part == null && aims[^1].Offset == Vector2.zero,
                "six external aim targets and fallback share the hull origin");
            Vector2 Offset(Thing part)
            {
                var draw = part.DrawPos;
                return new Vector2(draw.x, draw.z) - hull.HullCenter;
            }
            var visible = new ShipFireControl.ExternalTargets(hull);
            var closest = visible.weapons.Concat(visible.propulsion)
                .OrderBy(part => (Offset(part) - source).sqrMagnitude)
                .ThenBy(part => part.thingIDNumber).Take(6).ToHashSet();
            Check(aims.Take(6).All(aim => aim.Offset == Offset(aim.Part)) &&
                closest.SetEquals(aims.Take(6).Select(aim => aim.Part)),
                "external aim offsets and nearest-target ordering use the hull frame");
        }
        map.events = new MapEvents(map);
        var center = ship.HullCenter;
        int x = Mathf.FloorToInt(center.x), z = Mathf.FloorToInt(center.y);
        AddStaticFleetShield(map, enemy, 97001, map.Size.x);
        var shield = (Building)map.listerThings.AllThings.Single(thing => thing.thingIDNumber == 97001);
        var wall = (Building)AddTestBuilding(map, enemy, 97002, TestBuildingDef("ProductionProtectionWall"), new EmptyComp());
        MoveCombatFixture(wall, new IntVec3(x, 0, z + 1));
        SetStaticFixtureHealth(wall, 100);
        wall.def.drawDamagedOverlay = false;
        Current.Game.GetComponent<UniverseManager>().ShipStructure.Changed(map);
        var receiver = universe.ProtectionFor(ship);
        int sector = CombatProtectionTable.Receiver.DirectionOf(Vector2.down);
        var path = receiver.Get(null, sector);
        var shieldDraw = shield.DrawPos;
        var incoming = center - new Vector2(shieldDraw.x, shieldDraw.z);
        var guardPath = receiver.Get(null, CombatProtectionTable.Receiver.DirectionOf(incoming));
        var guard = shield.GetComp<CompSpaceShield>();
        var layer = guardPath.Guards.Single(item => ReferenceEquals(item.Shield, guard));
        int gameTick = Find.TickManager.TicksGame;
        double fullHp = path.Hp;
        int fullLayers = path.Layers;
        long scans = universe.Protection.Scans;
        wall.HitPoints = 70;
        Check(receiver.Get(null, sector).Hp == fullHp - 30 && path.Layers == fullLayers,
            "native HP setter updates the existing protection path immediately");
        wall.HitPoints = 0;
        Check(receiver.Get(null, sector).Hp == fullHp - 100 && path.Layers == fullLayers - 1,
            "depleted layer disappears immediately");
        wall.HitPoints = 100;
        Check(receiver.Get(null, sector).Hp == fullHp && path.Layers == fullLayers,
            "restoring HP restores the existing layer");
        Check(receiver.RegularCapacity(layer) == 50, "same-tick test starts at full regular capacity");
        guard.DebitRegular(20);
        Check(receiver.RegularCapacity(layer) == 30, "real guard debit refreshes the cached pool in the same tick");
        Set(guard, "recoveryWaitTicks", 0);
        guard.AdvanceGuardTick(true);
        Check(receiver.RegularCapacity(layer) > 30 && receiver.RegularCapacity(layer) < 50,
            "actual guard recovery refreshes the cached pool in the same tick");
        Check(Find.TickManager.TicksGame == gameTick && universe.Protection.Scans == scans,
            "building HP, guard debit and guard recovery update without advancing tick or scanning again");

        Set(layer.Interceptor, "stunner", new StunHandler(shield));
        Check(guardPath != path && path.Guards.Any(item => ReferenceEquals(item.Shield, guard)),
            "two different directions see the same physical guard");
        var output = new CombatWeaponOutputTable(universe, own.member, ship, target.pose,
            new List<CombatEngagementEvaluation.Battery>(), new List<CombatAttackWeapon>(), true);
        float available = (float)guard.RegularCapacity;
        float firstDebit = (float)Call(output, "DebitShields", receiver, guardPath, 20f);
        float secondDebit = (float)Call(output, "DebitShields", receiver, path, 20f);
        Check(firstDebit == 20 && Math.Abs(firstDebit + secondDebit - available) < .0001f &&
            (float)guard.RegularCapacity == available, "two direction groups share one candidate-local bank debit");

        map.thingGrid.Deregister(wall);
        Set(typeof(Thing), wall, "mapIndexOrState", (sbyte)-2);
        Current.Game.GetComponent<UniverseManager>().ShipStructure.Changed(map, wall.OccupiedRect());
        var damaged = universe.ProtectionFor(ship);
        Check(damaged != receiver && damaged.Get(null, sector).Hp < fullHp && Find.TickManager.TicksGame == gameTick,
            "same-tick structural removal replaces the protection view");

        var modProjectile = TestBuildingDef("ProtectionExtensionProjectile");
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef
        {
            defName = "ProtectionExtensionProjectileDamage", projectileDefs = new List<ThingDef> { modProjectile },
            defenderEffect = new ImpactOutcomePart
            {
                kind = ImpactEffectKind.DamageMap, damage = 60, damageDef = DamageDefOf.Bullet,
                penetrates = true, incidenceMinFactor = 1
            }
        });
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef
        {
            defName = "ProtectionExtensionProjectileGuard", projectileDefs = new List<ThingDef> { modProjectile },
            targetThingDefs = new List<ThingDef> { shield.def },
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.ShieldDrain, damage = 18, incidenceMinFactor = 1 }
        });
        SpaceImpactEffects.ResetCache();
        var rule = SpaceImpactEffects.Resolve(modProjectile, ThingDefOf.Wall)?.defenderEffect;
        Check(rule?.penetrates == true && CombatWeaponOutputTable.DamagePerProjectile(rule, 60, 1, 1) == 60,
            "extension projectile resolves through the same mechanism as built-in weapons");
        var launcher = ShipAutoManeuverControl.CaptureAttackWeapons(own.member, own).First().gun;
        var battery = new CombatEngagementEvaluation.Battery
        {
            gun = launcher,
            shot = new SpaceWeaponParameters(null, new WeaponAimInfo(default, default, modProjectile, 60, 0, 200), default)
        };
        var drain = (Dictionary<ThingDef, float>)Call(output, "BuildGuards", battery, damaged.GuardDefinitions);
        Check(drain.TryGetValue(shield.def, out float demand) && demand > 0,
            "weapon table resolves the extension guard rule against the actual shield definition");
        Check(CombatDamageSurface.AtAverage(2, 120, 120, 100, 2) == (100f, 20f) &&
            CombatDamageSurface.AtAverage(4, 240, 0, 100, 2) == (100f, 120f),
            "grouped fire shares average wall durability");
    }

    static object CompareProductionTables(UniverseManager universe, ManeuverMotionObject own,
        ManeuverMotionObject foe, int scene, string side, string outPath)
    {
        var table = universe.Protection;
        var perf = universe.Performance;
        MothershipPlan Plan(out int winner, out int candidates)
        {
            productionScores.Clear();
            var planner = MakeMothershipPlanner(universe, own, foe);
            var plan = ReadMothershipPlan(planner, own, foe, compareHeading: true);
            var chosen = typeof(ShipAutoManeuverControl).GetField("engagementResult", Private).GetValue(planner);
            winner = productionScores.FindIndex(value => ReferenceEquals(value, chosen));
            candidates = productionScores.Count;
            return plan;
        }
        int beforeAim = productionAim, beforeExact = productionExact;
        long scans = table.Scans, layers = table.Layers, cells = table.Cells, count = perf.engagementCandidates;
        var cold = Plan(out int winner, out int candidates);
        long firstRays = table.Scans - scans, firstCells = table.Cells - cells, firstLayers = table.Layers - layers;
        Check(candidates != 0 && candidates == perf.engagementCandidates - count && winner >= 0,
            "production planner scored and selected from the real candidate pool");
        scans = table.Scans; layers = table.Layers; cells = table.Cells;
        var repeated = Plan(out int repeatWinner, out int repeatCandidates);
        Check(cold.Valid == repeated.Valid && cold.Start == repeated.Start && cold.Distance == repeated.Distance &&
            cold.Heading == repeated.Heading && cold.Phase == repeated.Phase &&
            cold.Track.SequenceEqual(repeated.Track) && winner == repeatWinner && candidates == repeatCandidates,
            "cold and warm production runs choose exactly the same plan");
        long extraRays = table.Scans - scans, extraCells = table.Cells - cells, extraLayers = table.Layers - layers;
        Check(extraRays == 0 && extraCells == 0 && extraLayers == 0, "repeat candidates generate no new protection scan work");
        int aim = productionAim - beforeAim, exact = productionExact - beforeExact;
        Check(aim == 0 && exact == 0, "production planner calls neither full aim nor precise damage prediction");
        string archived = Path.Combine(Directory.GetParent(outPath).FullName, "AverageDefense", "parity.jsonl");
        int? baseline = null;
        if (File.Exists(archived))
        {
            foreach (string line in File.ReadLines(archived))
            {
                using var document = JsonDocument.Parse(line);
                var row = document.RootElement;
                if (row.GetProperty("group").GetString() != "A" || row.GetProperty("side").GetString() != side ||
                    row.GetProperty("scene").GetString() != MothershipScenarios[scene]) continue;
                if (row.GetProperty("candidates").GetInt32() == candidates) baseline = row.GetProperty("winner").GetInt32();
                break;
            }
        }
        var replay = new { scene = MothershipScenarios[scene], side, candidates, winner, baseline, firstRays, firstCells,
            firstLayers, extraRays, extraCells, extraLayers, aim, exact, cold };
        Console.WriteLine("PRODUCTION_TABLE " + JsonSerializer.Serialize(replay, MothershipJson));
        return replay;
    }
#endif

#if MOTHERSHIP_OFFLINE
    sealed class CrossoverRow
    {
        public string scene, side, suite, arm;
        public int block, pair, order, process, candidates, paths, cells, layers, newPaths, newCells, newLayers;
        public int gc0, gc1, gc2;
        public long bytes;
        public double totalMs, queryMs, scanMs, ddaMs, classifyMs, registerMs, cacheMs,
            groupingMs, responseMs, distributeMs, fillMs, scoreMs, sortMs, copyMs, otherMs;
    }

    static JsonElement ArchivedCrossoverRow(string path, string scene, string side, string group = null)
    {
        foreach (string line in File.ReadLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            if (row.GetProperty("scene").GetString() == scene && row.GetProperty("side").GetString() == side &&
                (group == null || row.GetProperty("group").GetString() == group))
                return row.Clone();
        }
        throw new InvalidDataException($"Missing archive row for {scene}/{side} in {path}");
    }

    static void CompareMothershipCrossover(ManeuverMotionObject own, ManeuverMotionObject enemy,
        string scene, string side, UniverseManager universe, int window, string outPath, List<CrossoverRow> results)
    {
        Pool pool = null;
        ShipAutoManeuverControl.OfflineCapture = captured =>
        {
            pool = captured;
            return captured.Select(captured.Rows.Select(row => row.Candidate).ToList());
        };
        try { ReadMothershipPlan(MakeMothershipPlanner(universe, own, enemy), own, enemy); }
        finally { ShipAutoManeuverControl.OfflineCapture = null; }
        string archive = Directory.GetParent(outPath).FullName;
        var oldArchive = ArchivedCrossoverRow(Path.Combine(archive, "AverageDefense", "parity.jsonl"), scene, side, "C");
        var oldCounts = ArchivedCrossoverRow(Path.Combine(archive, "AverageDefense", "counts.jsonl"), scene, side, "C");
        var newArchive = ArchivedCrossoverRow(Path.Combine(archive, "DualTable", "count.jsonl"), scene, side);
        string hash = CandidateFingerprint(pool);
        Check(hash == oldArchive.GetProperty("candidateHash").GetString() &&
            hash == newArchive.GetProperty("candidateHash").GetString(), $"archived candidate fingerprint {scene}/{side}");

        ShotInput[] Capture(int index, MothershipAverageDefenseBaseline filter)
        {
            var shots = new List<ShotInput>();
            CombatEngagementEvaluation.OfflineApproximate = (ShotInput shot, out float critical) =>
            {
                shots.Add(shot);
                critical = 0;
                return default;
            };
            CombatEngagementEvaluation.OfflineTarget = filter == null ? null : filter.Allows;
            try { pool.Evaluator.Evaluate(pool.Rows[index].State); }
            finally
            {
                CombatEngagementEvaluation.OfflineApproximate = null;
                CombatEngagementEvaluation.OfflineTarget = null;
            }
            return shots.ToArray();
        }

        var oldSeed = new MothershipAverageDefenseBaseline(own, enemy, true);
        var newSeed = new MothershipForecastTable(own, enemy, true);
        var weaponTable = new MothershipWeaponOutputTable(pool.Evaluator, pool.Offense, window);
        int[] indices = Enumerable.Range(0, pool.Rows.Count).ToArray();
        var oldInputs = indices.Select(i =>
        {
            var pose = pool.Rows[i].Candidate.pose;
            pose.EvaluateFire(pool.Evaluator.primaryWeapons, Vector2.zero, pool.Offense, pool.Curves);
            return (Pose: pose, Shots: Capture(i, oldSeed));
        }).ToArray();
        var frozen = oldInputs.Select(row => MothershipWeaponOutputTable.FreezeLegacy(row.Shots, window)).ToArray();
        var newInputs = indices.Select(i =>
        {
            var row = pool.Rows[i];
            var pose = row.Candidate.pose;
            var shots = weaponTable.Gate(row.State, newSeed, ref pose, pool.Curves);
            return (Pose: pose, Shots: shots);
        }).ToArray();

        Candidate Score(int index, CombatAttackPose pose, float legacy, Benefit[] benefits,
            MothershipWeaponOutputTable.Delivery[] delivery, ShotInput[] raw, MothershipForecastTable.Profile profile,
            bool current)
        {
            long start = MothershipForecastTable.Profile.Stamp(profile);
            var value = new CombatEngagementEvaluation.Result();
            float rate = 60f / window;
            for (int i = 0; i < benefits.Length; i++)
            {
                if (raw == null)
                    AddBenefit(value, benefits[i], delivery[i].Weapon, delivery[i].Family, delivery[i].Outgoing, rate);
                else
                    AddBenefit(value, benefits[i], raw[i].Weapon,
                        CombatWeaponFilter.FamilyOf(raw[i].Weapon, raw[i].Shot.aim.Projectile), raw[i].Outgoing, rate);
            }
            if (profile != null)
            {
                profile.fill += Stopwatch.GetTimestamp() - start;
                start = Stopwatch.GetTimestamp();
            }
            var candidate = pool.Rows[index].Candidate;
            value.travel = candidate.value.travel;
            value.velocity = candidate.value.velocity;
            value.reverse = candidate.value.reverse;
            value.flank = candidate.value.flank;
            float score = value.Score(pool.Settings) + legacy;
            if (current) score = score + pose.outgoingScore + pose.alignmentScore;
            if (profile != null) profile.score += Stopwatch.GetTimestamp() - start;
            return new Candidate { phase = candidate.phase, pose = pose, value = value, score = score };
        }

        float OldLegacy(CombatAttackPose pose) => pose.distanceScore + pose.outgoingScore + pose.alignmentScore +
            pose.incomingScore + pose.timeScore + pose.overlapScore;
        Candidate OldScore(int i, MothershipAverageDefenseBaseline table, bool common,
            MothershipForecastTable.Profile profile)
        {
            var (pose, raw) = oldInputs[i];
            return common
                ? Score(i, pose, OldLegacy(pose), table.QueryFrozen(frozen[i]), frozen[i], null, profile, false)
                : Score(i, pose, OldLegacy(pose), table.Query(raw, window), null, raw, profile, false);
        }
        Candidate NewScore(int i, MothershipForecastTable table, bool common, MothershipForecastTable.Profile profile)
        {
            if (common)
            {
                var pose = oldInputs[i].Pose;
                return Score(i, pose, OldLegacy(pose), table.Query(frozen[i]), frozen[i], null, profile, false);
            }
            var (gatePose, shots) = newInputs[i];
            return Score(i, gatePose, pool.Rows[i].Legacy, table.Query(shots), shots, null, profile, true);
        }
        int[] Rank(Candidate[] scores) => indices.OrderByDescending(i => scores[i].score).ToArray();
        var oldScored = indices.Select(i => OldScore(i, oldSeed, false, null)).ToArray();
        var newScored = indices.Select(i => NewScore(i, newSeed, false, null)).ToArray();
        var oldRank = Rank(oldScored);
        var newRank = Rank(newScored);
        int[] ReadRank(JsonElement entry, string key) => entry.GetProperty(key).EnumerateArray()
            .Select(i => i.GetInt32()).ToArray();
        Check(oldRank.SequenceEqual(ReadRank(oldArchive, "rankedCandidates")), $"archived old rank {scene}/{side}");
        Check(newRank.SequenceEqual(ReadRank(newArchive, "ranking")), $"archived new rank {scene}/{side}");
        int ReplayWinner(Candidate[] assessed, int[] candidateIds)
        {
            var planner = MakeMothershipPlanner(universe, own, enemy);
            ShipAutoManeuverControl.OfflineComparisonReplay = () => pool.Select(assessed.ToList());
            try { ReadMothershipPlan(planner, own, enemy); }
            finally { ShipAutoManeuverControl.OfflineComparisonReplay = null; }
            int index = Array.FindIndex(assessed, row => ReferenceEquals(row.value, Chosen(planner)));
            return index < 0 ? -1 : candidateIds[index];
        }
        int[] six = oldRank.Take(6).ToArray();
        var oldFinal = six.Select(i =>
        {
            var raw = Capture(i, null);
            var pose = oldInputs[i].Pose;
            return Score(i, pose, OldLegacy(pose), MothershipSharedDamageReference.Run(raw, window, null),
                null, raw, null, false);
        }).ToArray();
        Check(ReplayWinner(oldFinal, six) == oldArchive.GetProperty("winner").GetInt32(),
            $"archived old selection {scene}/{side}");
        Check(ReplayWinner(newScored, indices) == newArchive.GetProperty("winner").GetInt32(),
            $"archived new selection {scene}/{side}");
        var paths = oldSeed.Observed();
        Check(paths.ToHashSet().SetEquals(newSeed.Observed()), $"identical path keys, HP and layers {scene}/{side}");
        void VerifyCounts(string field, (int Rays, int Cells, int Layers) oldSide,
            (int Rays, int Cells, int Layers) newSide)
        {
            foreach (var (key, oldCount, newCount) in new[]
            {
                ("defenseRays", oldSide.Rays, newSide.Rays),
                ("gridVisits", oldSide.Cells, newSide.Cells),
                ("layerVisits", oldSide.Layers, newSide.Layers)
            })
                Check(oldCount == newCount && oldCount == oldCounts.GetProperty(field).GetProperty(key).GetInt32() &&
                    newCount == newArchive.GetProperty(field).GetProperty(key).GetInt32(),
                    $"archived {field}/{key} {scene}/{side}");
        }
        VerifyCounts("outgoing", (oldSeed.outgoing.defenseRays, oldSeed.outgoing.gridVisits, oldSeed.outgoing.layerVisits),
            (newSeed.outgoing.defenseRays, newSeed.outgoing.gridVisits, newSeed.outgoing.layerVisits));
        VerifyCounts("incoming", (oldSeed.incoming.defenseRays, oldSeed.incoming.gridVisits, oldSeed.incoming.layerVisits),
            (newSeed.incoming.defenseRays, newSeed.incoming.gridVisits, newSeed.incoming.layerVisits));
        var oldReplay = new MothershipAverageDefenseBaseline(own, enemy, false);
        var newReplay = new MothershipForecastTable(own, enemy, false);
        int Cells() => newReplay.outgoing.gridVisits + newReplay.incoming.gridVisits;
        int OldCells() => oldReplay.outgoing.gridVisits + oldReplay.incoming.gridVisits;
        foreach (var path in paths)
        {
            int a = OldCells(), b = Cells();
            if (oldReplay.ReplayPath(path) != path || newReplay.ReplayPath(path) != path ||
                OldCells() - a != Cells() - b)
                throw new InvalidDataException($"Path scan differs in {scene}/{side}, sector {path.Sector}");
        }
        Check(indices.Select(i => BitConverter.SingleToInt32Bits(OldScore(i, oldSeed, true, null).score)).SequenceEqual(
            indices.Select(i => BitConverter.SingleToInt32Bits(NewScore(i, newSeed, true, null).score))),
            $"frozen input score bit parity {scene}/{side}");

        CrossoverRow Trial(string suite, bool newer)
        {
            bool scanOnly = suite is "same-path-scan" or "detail-scan";
            bool cold = scanOnly || suite is "as-is-cold" or "detail-cold";
            bool detail = suite.StartsWith("detail-", StringComparison.Ordinal);
            bool kernel = suite == "same-input-kernel";
            bool sort = suite == "same-input-sort";
            bool shot = suite == "same-input-shot-math";
            bool newKernel = sort || newer && !shot;
            bool newSort = newer && !kernel && !shot;
            var oldTable = newKernel ? null : new MothershipAverageDefenseBaseline(own, enemy, true);
            var newTable = newKernel ? new MothershipForecastTable(own, enemy, true) : null;
            var profile = detail ? new MothershipForecastTable.Profile() : null;
            Candidate Evaluate(int i) => newKernel
                ? NewScore(i, newTable, kernel || sort, profile)
                : OldScore(i, oldTable, kernel || shot && newer, profile);
            Candidate[] frozenScores = null;
            if (!cold)
            {
                if (sort) frozenScores = indices.Select(Evaluate).ToArray();
                else
                    foreach (int i in indices) Evaluate(i);
                if (newKernel) newTable.ExerciseCallbacks();
                else oldTable.ExerciseCallbacks();
                if (detail)
                {
                    profile = new MothershipForecastTable.Profile();
                    if (newKernel) newTable.Probe = profile;
                    else oldTable.Probe = profile;
                }
            }
            else if (detail)
            {
                if (newKernel) newTable.Probe = profile;
                else oldTable.Probe = profile;
            }
            long ScanTicks() => newKernel ? newTable.ScanTicks : oldTable.ScanTicks;
            (int Rays, int Cells, int Layers) ScanWork() => newKernel
                ? (newTable.outgoing.defenseRays + newTable.incoming.defenseRays,
                    newTable.outgoing.gridVisits + newTable.incoming.gridVisits,
                    newTable.outgoing.layerVisits + newTable.incoming.layerVisits)
                : (oldTable.outgoing.defenseRays + oldTable.incoming.defenseRays,
                    oldTable.outgoing.gridVisits + oldTable.incoming.gridVisits,
                    oldTable.outgoing.layerVisits + oldTable.incoming.layerVisits);
            var before = ScanWork();
            long alreadyScanned = ScanTicks();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            long start = Stopwatch.GetTimestamp();
            if (scanOnly)
            {
                foreach (var path in paths)
                    if (newKernel) newTable.ReplayPath(path);
                    else oldTable.ReplayPath(path);
            }
            else if (newSort || detail)
            {
                long sortStart;
                var assessed = indices.Select(i => sort ? frozenScores[i] : Evaluate(i)).ToArray();
                sortStart = MothershipForecastTable.Profile.Stamp(profile);
                GC.KeepAlive(Rank(assessed));
                if (profile != null) profile.sort += Stopwatch.GetTimestamp() - sortStart;
                if (cold && newSort)
                {
                    long copyStart = MothershipForecastTable.Profile.Stamp(profile);
                    GC.KeepAlive(assessed.Select(row => row.score).ToArray());
                    if (profile != null) profile.copy += Stopwatch.GetTimestamp() - copyStart;
                }
            }
            else
                GC.KeepAlive(indices.Select(i => (Index: i, Value: sort ? frozenScores[i] : Evaluate(i)))
                    .OrderByDescending(row => row.Value.score).ToArray());
            long elapsed = Stopwatch.GetTimestamp() - start;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            gc0 = GC.CollectionCount(0) - gc0;
            gc1 = GC.CollectionCount(1) - gc1;
            gc2 = GC.CollectionCount(2) - gc2;
            long scan = ScanTicks() - alreadyScanned;
            var after = ScanWork();
            if (!cold && (scan != 0 || after != before))
                throw new InvalidDataException($"Warm scan work in {scene}/{side}/{suite}");
            var stages = profile ?? new MothershipForecastTable.Profile();
            long response = detail && !scanOnly ? stages.response - scan : stages.response;
            long other = elapsed - scan - stages.grouping - response - stages.distribute -
                stages.fill - stages.score - stages.sort - stages.copy;
            return new CrossoverRow
            {
                scene = scene, side = side, suite = suite, arm = newer ? "new" : "old",
                process = Environment.ProcessId, candidates = indices.Length,
                paths = after.Rays, cells = after.Cells, layers = after.Layers,
                newPaths = after.Rays - before.Rays, newCells = after.Cells - before.Cells,
                newLayers = after.Layers - before.Layers,
                bytes = bytes, gc0 = gc0, gc1 = gc1, gc2 = gc2,
                totalMs = ToMs(elapsed), queryMs = ToMs(elapsed - scan), scanMs = ToMs(scan),
                ddaMs = ToMs(stages.scan - stages.classify - stages.register), classifyMs = ToMs(stages.classify),
                registerMs = ToMs(stages.register), cacheMs = ToMs(detail && scanOnly ? scan - stages.scan : 0),
                groupingMs = ToMs(stages.grouping), responseMs = ToMs(response),
                distributeMs = ToMs(stages.distribute), fillMs = ToMs(stages.fill), scoreMs = ToMs(stages.score),
                sortMs = ToMs(stages.sort), copyMs = ToMs(stages.copy), otherMs = ToMs(other)
            };
        }

        foreach (string suite in new[] { "as-is-cold", "as-is-warm", "same-input-kernel", "same-input-sort",
            "same-input-shot-math", "same-path-scan", "detail-warm", "detail-cold", "detail-scan" })
        {
            Trial(suite, false);
            Trial(suite, true);
            for (int block = 0; block < 3; block++)
            for (int order = 0; order < 4; order++)
            {
                bool newer = (order == 1 || order == 2) == (block % 2 == 0);
                var row = Trial(suite, newer);
                row.block = block + 1;
                row.pair = order / 2 + 1;
                row.order = order + 1;
                results.Add(row);
            }
        }
        Console.WriteLine($"CROSSOVER {scene}/{side} {indices.Length} candidates, {paths.Length} paths, {Cells()} cells, " +
            $"old top {oldRank[0]}, new top {newRank[0]}");
    }

    sealed class MothershipRow
    {
        public string scene, side, mode, candidateHash;
        public int round, windowTicks, candidates, winner, topRanked;
        public int fullAimCalls, legacyAimCalls, exactFireCalls;
        public float selectedDistance, selectedBearing, selectedHeading;
        public int[] ranking;
        public float[] scores;
        public double totalMs, candidateMs, weaponTableMs, defenseInitMs, gatesMs, firstScanMs,
            callbacksMs, queryMs, repeatQueryMs, routeMs, exactMs;
        public bool routeValid;
        public MothershipForecastTable.Counters outgoing, incoming;
    }

    static string CandidateFingerprint(Pool pool)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        foreach (var row in pool.Rows)
        {
            var state = row.State;
            foreach (float x in new[] { state.position.x, state.position.y, state.position.z, state.angle,
                state.velocity.x, state.velocity.y, state.angularVelocity, row.Candidate.phase,
                row.Candidate.pose.distance, row.Candidate.pose.heading, row.Legacy })
                writer.Write(BitConverter.SingleToInt32Bits(x));
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    static CombatEngagementEvaluation.Result Chosen(ShipAutoManeuverControl planner) =>
        (CombatEngagementEvaluation.Result)typeof(ShipAutoManeuverControl)
            .GetField("engagementResult", Private).GetValue(planner);

    static MothershipRow CompareMothership(ManeuverMotionObject own, ManeuverMotionObject enemy,
        string scene, string side, UniverseManager universe, string mode, int round, int window)
    {
        bool reference = mode == "reference";
        bool repeat = mode is "count" or "time";
        var result = new MothershipRow { scene = scene, side = side, mode = mode, round = round, windowTicks = window };
        var planner = MakeMothershipPlanner(universe, own, enemy);
        Candidate[] assessed = null;
        Pool captured = null;
        long diagnosticTicks = 0, routeStart = 0;

        List<Candidate> ScorePool(Pool pool)
        {
            captured = pool;
            result.candidates = pool.Rows.Count;
            result.candidateMs = ToMs(pool.GenerationTicks);
            Check(result.candidates > 6, "geometry enumerates a full engagement candidate pool");
            long stage = Stopwatch.GetTimestamp();
            var output = new MothershipWeaponOutputTable(pool.Evaluator, pool.Offense, window);
            result.weaponTableMs = ToMs(Stopwatch.GetTimestamp() - stage);
            stage = Stopwatch.GetTimestamp();
            var defense = new MothershipForecastTable(own, enemy, true);
            result.defenseInitMs = ToMs(Stopwatch.GetTimestamp() - stage);
            result.outgoing = defense.outgoing;
            result.incoming = defense.incoming;

            stage = Stopwatch.GetTimestamp();
            var gates = pool.Rows.Select(row =>
            {
                var pose = row.Candidate.pose;
                var deliveries = output.Gate(row.State, defense, ref pose, pool.Curves);
                return (Pose: pose, Deliveries: deliveries);
            }).ToArray();
            result.gatesMs = ToMs(Stopwatch.GetTimestamp() - stage);

            Candidate Score(int index)
            {
                var row = pool.Rows[index];
                var (pose, deliveries) = gates[index];
                var value = new CombatEngagementEvaluation.Result();
                float rate = 60f / window;
                if (reference)
                {
                    var shots = new List<ShotInput>();
                    CombatEngagementEvaluation.OfflineApproximate = (ShotInput shot, out float critical) =>
                    {
                        shots.Add(shot);
                        critical = 0;
                        return default;
                    };
                    try { pool.Evaluator.Evaluate(row.State); }
                    finally { CombatEngagementEvaluation.OfflineApproximate = null; }
                    var benefits = MothershipSharedDamageReference.Run(shots, window, null);
                    for (int i = 0; i < shots.Count; i++)
                    {
                        var shot = shots[i];
                        AddBenefit(value, benefits[i], shot.Weapon,
                            CombatWeaponFilter.FamilyOf(shot.Weapon, shot.Shot.aim.Projectile), shot.Outgoing, rate);
                    }
                }
                else
                {
                    var benefits = defense.Query(deliveries);
                    for (int i = 0; i < deliveries.Length; i++)
                    {
                        var shot = deliveries[i];
                        AddBenefit(value, benefits[i], shot.Weapon, shot.Family, shot.Outgoing, rate);
                    }
                }
                value.travel = row.Candidate.value.travel;
                value.velocity = row.Candidate.value.velocity;
                value.reverse = row.Candidate.value.reverse;
                value.flank = row.Candidate.value.flank;
                return new Candidate { phase = row.Candidate.phase, pose = pose, value = value,
                    score = value.Score(pool.Settings) + row.Legacy + pose.outgoingScore + pose.alignmentScore };
            }

            int[] Rank(Candidate[] scores) => Enumerable.Range(0, scores.Length)
                .OrderByDescending(i => scores[i].score).ToArray();
            stage = Stopwatch.GetTimestamp();
            assessed = Enumerable.Range(0, pool.Rows.Count).Select(Score).ToArray();
            result.ranking = Rank(assessed);
            result.topRanked = result.ranking[0];
            result.scores = assessed.Select(x => x.score).ToArray();
            double scoreMs = ToMs(Stopwatch.GetTimestamp() - stage);
            if (reference) result.exactMs = scoreMs;
            else
            {
                result.firstScanMs = ToMs(defense.ScanTicks);
                result.queryMs = scoreMs - result.firstScanMs;
            }

            if (repeat)
            {
                var sides = new[] { defense.outgoing, defense.incoming };
                var before = sides.Select(x => (Rays: x.defenseRays + x.shieldQueries, x.layerVisits, x.gridVisits)).ToArray();
                long callbackStart = Stopwatch.GetTimestamp();
                defense.ExerciseCallbacks();
                long warmStart = Stopwatch.GetTimestamp();
                var warmed = Enumerable.Range(0, pool.Rows.Count).Select(Score).ToArray();
                int[] warmRank = Rank(warmed);
                diagnosticTicks = Stopwatch.GetTimestamp() - callbackStart;
                result.callbacksMs = ToMs(warmStart - callbackStart);
                result.repeatQueryMs = ToMs(diagnosticTicks) - result.callbacksMs;
                for (int i = 0; i < sides.Length; i++)
                {
                    var c = sides[i];
                    c.repeatRays = c.defenseRays + c.shieldQueries - before[i].Rays;
                    c.repeatLayers = c.layerVisits - before[i].layerVisits;
                    c.repeatGridVisits = c.gridVisits - before[i].gridVisits;
                }
                Check(sides.All(x => x.repeatRays == 0 && x.repeatLayers == 0 && x.repeatGridVisits == 0),
                    "warm scoring performs no new protective rays, cell or layer visits");
                Check(warmRank.SequenceEqual(result.ranking) && warmed.Select(x => x.score).SequenceEqual(result.scores),
                    "restoring callback balances preserves the repeated scores and ranking");
            }

            routeStart = Stopwatch.GetTimestamp();
            return pool.Select(assessed.ToList());
        }

        CombatEngagementEvaluation.OfflineAimCalls = 0;
        CombatEngagementEvaluation.OfflineLegacyAimCalls = 0;
        CombatEngagementEvaluation.OfflineFireCalls = 0;
        CombatEngagementEvaluation.OfflineSkipObservations = !reference;
        ShipAutoManeuverControl.OfflineCapture = ScorePool;
        long start = Stopwatch.GetTimestamp();
        MothershipPlan plan;
        try { plan = ReadMothershipPlan(planner, own, enemy); }
        finally
        {
            ShipAutoManeuverControl.OfflineCapture = null;
            CombatEngagementEvaluation.OfflineSkipObservations = false;
        }
        long finish = Stopwatch.GetTimestamp();
        result.totalMs = ToMs(finish - start - diagnosticTicks);
        result.routeMs = ToMs(finish - routeStart);
        result.fullAimCalls = CombatEngagementEvaluation.OfflineAimCalls;
        result.legacyAimCalls = CombatEngagementEvaluation.OfflineLegacyAimCalls;
        result.exactFireCalls = CombatEngagementEvaluation.OfflineFireCalls;
        Check(reference ? result.fullAimCalls > 0 && result.exactFireCalls > 0
            : result.fullAimCalls == 0 && result.legacyAimCalls == 0 && result.exactFireCalls == 0,
            reference ? "reference exercises full aim and individual damage forecasts"
            : "dual-table search invokes neither full aim nor individual damage forecasts");

        result.candidateHash = CandidateFingerprint(captured);
        var chosen = Chosen(planner);
        result.winner = chosen == null ? -1 : Array.FindIndex(assessed, row => ReferenceEquals(row.value, chosen));
        bool blocked = (bool)typeof(ShipAutoManeuverControl).GetField("blockedPose", Private).GetValue(planner);
        result.routeValid = plan.Valid && result.winner >= 0 && !blocked;
        if (result.routeValid)
        {
            result.selectedDistance = BitConverter.Int32BitsToSingle(plan.Distance);
            result.selectedHeading = BitConverter.Int32BitsToSingle(plan.Heading) * Mathf.Rad2Deg;
            result.selectedBearing = BitConverter.Int32BitsToSingle(plan.Phase) * Mathf.Rad2Deg;
        }
        return result;
    }

    static void AddBenefit(CombatEngagementEvaluation.Result value, Benefit benefit, CompSpaceWeapon weapon,
        CombatWeaponFamily family, bool outgoing, float rate)
    {
        var contribution = new CombatEngagementEvaluation.Contribution
        {
            weapon = weapon, family = family,
            side = outgoing ? CombatEngagementSide.Outgoing : CombatEngagementSide.Incoming,
            damage = benefit.Damage * rate, guard = benefit.Guard * rate, critical = benefit.Critical * rate,
            armorProgress = benefit.Armor * rate, uncertainty = benefit.Uncertainty * rate
        };
        value.contributions.Add(contribution);
        if (outgoing) value.outgoing += contribution.damage;
        else value.incoming += contribution.damage;
        value.uncertain |= benefit.Uncertainty > 0;
        if (outgoing && benefit.Returned > 0)
        {
            value.contributions.Add(new CombatEngagementEvaluation.Contribution { weapon = weapon, family = family,
                side = CombatEngagementSide.Incoming, uncertainty = benefit.Returned * rate });
            value.uncertain = true;
        }
    }

    static double ToMs(long elapsed) => elapsed * (1000d / Stopwatch.Frequency);
#endif
}
