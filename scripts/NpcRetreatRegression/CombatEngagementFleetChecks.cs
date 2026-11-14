using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static List<object> fleetCandidateScores;
    static BinaryWriter fleetRangeTrace;
    static long fleetRangeCalls, fleetInstantGates, fleetOtherGates, fleetRejectedGates;
    static bool fleetRangeRejected;

    static float CountFleetArcCells(Vector3 source, Vector3 target)
    {
        fleetRangeCalls++;
        return OrbitSphere.ArcCells(source, target);
    }

    static bool ObserveFleetRange(ref CombatAttackWeapon gun, float distance, float margin)
    {
        fleetRangeRejected = gun.OutsideInstantRange(distance, margin);
        return fleetRangeRejected;
    }

    static IEnumerable<CodeInstruction> TraceFleetRange(IEnumerable<CodeInstruction> instructions)
    {
        var arc = AccessTools.Method(typeof(OrbitSphere), nameof(OrbitSphere.ArcCells), new[] { typeof(Vector3), typeof(Vector3) });
        var gate = AccessTools.Method(typeof(CombatAttackWeapon), nameof(CombatAttackWeapon.OutsideInstantRange));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(arc)) instruction.operand = AccessTools.Method(typeof(Program), nameof(CountFleetArcCells));
            else if (instruction.Calls(gate)) instruction.operand = AccessTools.Method(typeof(Program), nameof(ObserveFleetRange));
            yield return instruction;
        }
    }

    static void StartFleetRangeGate() => fleetRangeRejected = false;

    static void RecordFleetRangeGate(object __0, bool __result)
    {
        var gun = (CombatAttackWeapon)AccessTools.Field(__0.GetType(), "gun").GetValue(__0);
        if (gun.instant) fleetInstantGates++; else fleetOtherGates++;
        if (fleetRangeRejected) fleetRejectedGates++;
        fleetRangeTrace.Write(gun.source.parent.thingIDNumber);
        fleetRangeTrace.Write(Find.TickManager.TicksGame);
        fleetRangeTrace.Write(fleetRangeRejected);
        fleetRangeTrace.Write(__result);
    }

    static void CaptureFleetCandidate(CombatEngagementEvaluation.Result __instance, float __result)
    {
        fleetCandidateScores.Add(new { score = __result, __instance.outgoing, __instance.incoming,
            __instance.travel, __instance.velocity, __instance.reverse, __instance.flank, __instance.uncertain,
            contributions = __instance.contributions.Select(c => new { weapon = c.weapon?.parent.ThingID,
                c.family, c.side, c.damage, c.guard, c.critical, c.uncertainty, c.armorProgress }).ToArray() });
    }

    static void CheckEngagementFleetBaseline(UniverseManager universe, Faction player, Faction enemy)
    {
        using var patch = new EngagementFixturePatch();
        Set(enemy, "relations", new List<FactionRelation> { new FactionRelation { other = player, kind = FactionRelationKind.Hostile } });
        Set(player, "relations", new List<FactionRelation> { new FactionRelation { other = enemy, kind = FactionRelationKind.Hostile } });
        var factions = (FactionManager)Raw(typeof(FactionManager));
        Set(factions, "ofPlayer", player); Set(Find.World, "factionManager", factions);
        ThingDefOf.PilotConsole = TestBuildingDef("fleetConsole");
        var args = Environment.GetCommandLineArgs();
        bool parity = args.Contains("--engagement-cover-parity");
        bool rangeParity = args.Contains("--engagement-range-parity");
        bool mixed = args.Contains("--engagement-range-mixed");
        parity |= rangeParity;
        bool dense = args.Contains("--engagement-cover-dense");
        bool profileFirst24 = args.Contains("--engagement-profile-first-24");
        bool timeFirst24 = args.Contains("--engagement-time-first-24");
        bool first24 = profileFirst24 || timeFirst24;
        if (args.Contains("--engagement-verify-targeting"))
        {
            Check(typeof(SpaceCollision).GetMethods(Private).All(m => m.Name != "TargetObstruction"),
                "engagement has no obstruction ranking or sweep entry point");
            Check(new CombatStrategy().BuiltInFire.part == CombatPart.Weapons,
                "default engagement prioritizes exterior weapons");
        }
        int width = dense ? 43 : 9, gunCount = dense ? 16 : 4;
        var parityPatch = new Harmony("DeltaV.Regression.FleetParity");
        var decisions = new List<object>();
        using var gateBytes = rangeParity ? new MemoryStream() : null;
        using var gateWriter = rangeParity ? new BinaryWriter(gateBytes) : null;
        if (rangeParity)
        {
            fleetRangeTrace = gateWriter;
            fleetRangeCalls = fleetInstantGates = fleetOtherGates = fleetRejectedGates = 0;
            var transpiler = new HarmonyMethod(typeof(Program), nameof(TraceFleetRange));
            parityPatch.Patch(AccessTools.Method(typeof(CombatEngagementEvaluation), "Evaluate"), transpiler: transpiler);
            parityPatch.Patch(AccessTools.Method(typeof(CombatEngagementEvaluation), "TryEstimate"),
                prefix: new HarmonyMethod(typeof(Program), nameof(StartFleetRangeGate)),
                postfix: new HarmonyMethod(typeof(Program), nameof(RecordFleetRangeGate)), transpiler: transpiler);
        }
        if (parity)
        {
            fleetCandidateScores = new List<object>();
            parityPatch.Patch(AccessTools.Method(typeof(CombatEngagementEvaluation.Result), "Score"),
                postfix: new HarmonyMethod(typeof(Program), nameof(CaptureFleetCandidate)));
        }
        var projectile = TestBuildingDef("fleetLaser");
        projectile.comps.Add(new CompProperties_SpaceProjectile { laserRange = 150, speed = 60, hasMap = false });
        var rounds = new[] { projectile, projectile, projectile, projectile };
        if (mixed)
        {
            rounds[0] = TestBuildingDef("fleetBallistic");
            rounds[0].comps.Add(new CompProperties_SpaceProjectile { speed = 80, hasMap = false });
            rounds[2] = TestBuildingDef("fleetGuided");
            rounds[2].comps.Add(new CompProperties_SpaceProjectile { guided = true, speed = 60, hasMap = false });
        }
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = "FleetLaserEffect",
            projectileDefs = rounds.Distinct().ToList(),
            targetsSubstructure = dense,
            substructureHitChance = dense ? .01f : SpaceImpactEffectDef.DefaultSubstructureHitChance,
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damage = 8 } });
        SpaceImpactEffects.ResetCache();
        var rows = new List<object>();
        bool profile = Environment.GetCommandLineArgs().Contains("--engagement-profile");
        bool profileWindow = Environment.GetCommandLineArgs().Contains("--engagement-profile-window");
        foreach (int count in dense || mixed || profile || profileWindow ? new[] { 4 } : new[] { 1, 4, 12, 24 })
        {
            var ships = new List<ManeuverMotionObject>();
            for (int i = 0; i < Math.Max(2, count); i++)
            {
                int id = 110000 + i * 100;
                var faction = i % 2 == 0 ? player : enemy;
                var map = TestMap(id, width);
                var body = new ManeuverMotionObject(id, map, new SpacePose(OrbitSphere.Move(Vector3.forward,
                    new Vector2(i % 2 == 0 ? 50 : -50, i / 2 * (dense ? 120 : 40))), 0));
                var console = AddTestConsole(map, faction, id + 1);
                MoveCombatFixture(console.parent, new IntVec3(width / 2, 0, width / 2));
                Set(typeof(Thing), console.parent, "hitPointsInt", 300);
                var hull = new CombatHull(console.Ship);
                hull.Combat.weaponsArmed = true;
                var foundation = (TerrainDef)Raw(typeof(TerrainDef));
                foundation.tags = new List<string> { "Substructure" }; foundation.isFoundation = true;
                var owners = new int[map.cellIndices.NumGridCells]; Set(SubstructureOwnership.TryGet(map), "ownerGrid", owners);
                for (int x = 2; x < width - 2; x++) for (int z = 2; z < width - 2; z++)
                {
                    var cell = new IntVec3(x, 0, z); hull.cells.Add(cell);
                    int index = map.cellIndices.CellToIndex(cell); map.terrainGrid.foundationGrid[index] = foundation; owners[index] = console.shipId;
                }
                body.member = hull; universe.objects.Add(body); ships.Add(body);
                for (int gunIndex = 0; gunIndex < gunCount; gunIndex++)
                {
                    var weapon = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon {
                        projectileDef = rounds[gunIndex % rounds.Length], intrinsicAimSkill = 20,
                        range = mixed && gunIndex == 3 ? 55 : 150, minRange = mixed && gunIndex == 1 ? 90 : 0,
                        cooldownTicks = 12 }, safeFire = false };
                    var gun = AddTestBuilding(map, faction, id + 2 + gunIndex, TestBuildingDef("fleetGun" + id + "_" + gunIndex), weapon);
                    int columns = dense ? 4 : 2;
                    int offset = dense ? width / 2 - 5 : 3;
                    MoveCombatFixture(gun, new IntVec3(offset + gunIndex % columns * 2, 0, offset + gunIndex / columns * 2));
                }
            }
            SubstructureOwnership.BumpStructureVersion(); Set(universe, "objectById", null); Set(universe, "objectByMap", null);
            var method = typeof(ShipAutoManeuverControl).GetMethod("PlanTacticTrajectory", Private);
            foreach (var own in ships.Take(count))
            {
                var target = ships[ships.IndexOf(own) ^ 1];
                universe.Controls.For(own.member);
                var planner = new ShipAutoManeuverControl(universe, own.member.Identity);
                Set(planner, "anchorTarget", CombatTarget.ForShip(target.member.Identity));
                Set(planner, "anchorDistance", 100f); Set(planner, "offenseEnabled", true);
                bool Sample(out SpacePose pose) { pose = ShipRegistry.ShipPose(target.member); return true; }
                bool Plan(SpaceObject body, SpacePose other, float radius, DeltaV.DriveInfo drive, out CombatTrajectoryIntent intent)
                {
                    long done = universe.Performance.engagementCompleted;
                    object[] call = { body, other, radius, drive, null };
                    bool result = (bool)method.Invoke(planner, call); intent = (CombatTrajectoryIntent)call[4];
                    if (parity && done != universe.Performance.engagementCompleted)
                    {
                        var pose = (CombatAttackPose)typeof(ShipAutoManeuverControl).GetField("attackPose", Private).GetValue(planner);
                        var state = (CombatReposition)typeof(ShipAutoManeuverControl).GetField("reposition", Private).GetValue(planner);
                        int tick = Find.TickManager.TicksGame;
                        Check(result && intent.startTick == tick && fleetCandidateScores.Count > 0,
                            $"fleet search applies a scored intent on the same tick ({own.objectId}, {tick})");
                        decisions.Add(new { ship = own.objectId, tick, result,
                            intent.startTick, intent.startPhase, intent.targetRadius, intent.headingError, intent.holdCurrent,
                            pose.distance, pose.heading, pose.preference, pose.behavior, state.phase, state.nextTick,
                            state.selectedScore, state.continuationScore, state.candidateCount,
                            trajectory = rangeParity ? FleetTrajectoryBits(intent) : null,
                            gateEnd = rangeParity ? fleetInstantGates + fleetOtherGates : 0,
                            scores = fleetCandidateScores.ToArray() });
                        fleetCandidateScores.Clear();
                    }
                    return result;
                }
                universe.Autopilot.TrackOrbit(new DriveSubject(own.objectId, own.member.Identity.key), Sample, 10, Plan);
            }
            var times = new List<double>();
            var watch = new Stopwatch();
            double coldMs = 0, invalidationMs = 0, peakMs = 0;
            int peakTick = 0;
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            TimeSpan gcPauseStart = GC.GetTotalPauseDuration();
            long started = universe.Performance.engagementStarted, completed = universe.Performance.engagementCompleted;
            long candidates = universe.Performance.engagementCandidates;
            long events = universe.Structures.Events, publications = universe.Structures.Publications;
            long arcStart = fleetRangeCalls, gatesStart = fleetInstantGates + fleetOtherGates;
            long instantStart = fleetInstantGates, rejectsStart = fleetRejectedGates;
            if (first24 && count == 24)
            {
                Array.Clear((long[])typeof(DeltaVPerformanceProbe).GetField("totals", Private).GetValue(universe.Performance));
                Array.Clear((int[])typeof(DeltaVPerformanceProbe).GetField("calls", Private).GetValue(universe.Performance));
                DeltaVMod.CurrentSettings.logPerformance = profileFirst24;
            }
            for (int tick = 0; tick < 1800; tick++)
            {
                if (profile || profileWindow) DeltaVMod.CurrentSettings.logPerformance = true;
                if (tick == 1500) SubstructureOwnership.BumpStructureVersion();
                watch.Restart(); RecoveryTick(universe.Autopilot, tick); watch.Stop();
                if (rangeParity && tick == 0)
                    Console.WriteLine($"RANGE_FIRST ships={count} calls={fleetRangeCalls - arcStart} gates={fleetInstantGates + fleetOtherGates - gatesStart} instant={fleetInstantGates - instantStart} rejects={fleetRejectedGates - rejectsStart}");
                if (first24 && count == 24)
                {
                    Console.WriteLine(FormattableString.Invariant($"FIRST_24 wallMs={watch.Elapsed.TotalMilliseconds:F5} searches={universe.Performance.engagementCompleted - completed}/{universe.Performance.engagementStarted - started} candidates={universe.Performance.engagementCandidates - candidates}"));
                    if (timeFirst24) return;
                }
                if (watch.Elapsed.TotalMilliseconds > peakMs) { peakMs = watch.Elapsed.TotalMilliseconds; peakTick = tick; }
                if (profile || profileWindow && tick == 1799 || profileFirst24 && count == 24)
                {
                    Console.WriteLine($"PROFILE allocationBytes={GC.GetAllocatedBytesForCurrentThread() - allocationStart} gen0={GC.CollectionCount(0) - gen0} gen1={GC.CollectionCount(1) - gen1} gen2={GC.CollectionCount(2) - gen2} gcPauseMs={(GC.GetTotalPauseDuration() - gcPauseStart).TotalMilliseconds:F3} searches={universe.Performance.engagementCompleted - completed}/{universe.Performance.engagementStarted - started} candidates={universe.Performance.engagementCandidates - candidates}");
                    var totals = (long[])typeof(DeltaVPerformanceProbe).GetField("totals", Private).GetValue(universe.Performance);
                    var calls = (int[])typeof(DeltaVPerformanceProbe).GetField("calls", Private).GetValue(universe.Performance);
                    foreach (PerformanceStage stage in Enum.GetValues(typeof(PerformanceStage)))
                        if (stage != PerformanceStage.Count && calls[(int)stage] > 0)
                            Console.WriteLine(FormattableString.Invariant($"PROFILE {stage} calls={calls[(int)stage]} ms={totals[(int)stage] * 1000d / Stopwatch.Frequency:F3}"));
                    return;
                }
                if (tick == 0) coldMs = watch.Elapsed.TotalMilliseconds;
                if (tick >= 1500 && tick < 1506) invalidationMs += watch.Elapsed.TotalMilliseconds;
                if (tick >= 300) times.Add(watch.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            double mean = times.Average(), p95 = times[(int)(times.Count * .95)], p99 = times[(int)(times.Count * .99)];
            rows.Add(new { ships = count, gunCount, floorsPerShip = (width - 4) * (width - 4), ticks = 1800,
                warmup = 300, peakMs, peakTick, meanTickMs = mean, p95TickMs = p95,
                p99TickMs = p99, maxTickMs = times.Last(), coldMs, invalidationSixTicksMs = invalidationMs,
                allocationBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart,
                gcPauseMs = (GC.GetTotalPauseDuration() - gcPauseStart).TotalMilliseconds,
                searchesStarted = universe.Performance.engagementStarted - started,
                searchesCompleted = universe.Performance.engagementCompleted - completed,
                candidatesEvaluated = universe.Performance.engagementCandidates - candidates,
                structureEvents = universe.Structures.Events - events,
                structurePublications = universe.Structures.Publications - publications,
                collections = new[] { GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2 } });
            Check(universe.Performance.engagementStarted - started == universe.Performance.engagementCompleted - completed,
                "fleet completes every started search at " + count + " ships");
            Console.WriteLine($"ENGAGEMENT_FLEET ships={count} meanMs={mean:F3} p95Ms={p95:F3} p99Ms={p99:F3} maxMs={times.Last():F3}");
            Check(ships.Take(count).All(s => !float.IsNaN(s.pose.velocity.x)), "fleet production guidance remains finite at " + count + " ships");
            foreach (var own in ships.Take(count)) universe.Autopilot.Cancel(new DriveSubject(own.objectId, own.member.Identity.key));
            universe.Controls.Reset(); universe.objects.Clear(); Find.Maps.Clear();
            ShipRegistry.ClearCaches(); ShipPhysics.ClearCache(); SpaceCollision.ClearCaches();
            Set(universe, "objectById", null); Set(universe, "objectByMap", null);
        }
        string output = Environment.GetEnvironmentVariable("DELTAV_ENGAGEMENT_OUTPUT") ?? (args.Contains("--engagement-fleet-regression")
            ? "Source/obj/EngagementForecast" : "Source/obj/EngagementBaseline-20261108");
        Directory.CreateDirectory(output);
        if (parity)
        {
            parityPatch.UnpatchAll(parityPatch.Id);
            Check(decisions.Count > 0, "fleet parity recorded production decisions");
            File.WriteAllText(Path.Combine(output, "fleet-parity.json"), JsonSerializer.Serialize(decisions,
                new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            fleetCandidateScores = null;
            if (rangeParity)
            {
                gateWriter.Flush();
                string hash = Convert.ToHexString(SHA256.HashData(gateBytes.GetBuffer().AsSpan(0, (int)gateBytes.Length)));
                File.WriteAllText(Path.Combine(output, "range-gates.txt"), $"{fleetInstantGates} {fleetOtherGates} {fleetRejectedGates} {hash}");
                Console.WriteLine($"RANGE_TOTAL calls={fleetRangeCalls} instant={fleetInstantGates} other={fleetOtherGates} rejects={fleetRejectedGates}");
                fleetRangeTrace = null;
            }
        }
        else
            File.WriteAllText(Path.Combine(output, "fleet.json"), JsonSerializer.Serialize(new {
                scope = "Real tactics, weapon/threat queries, MPC, thruster allocation and integration. No projectile launch, damage, render or UI.",
                rows }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static int[] FleetTrajectoryBits(CombatTrajectoryIntent intent)
    {
        var bits = new List<int>();
        foreach (float time in new[] { 0f, .1f, 1f, 2f, 3f, 4f })
        {
            intent.Sample(time, out var position, out var velocity, out float angle, out float angularVelocity);
            foreach (float value in new[] { position.x, position.y, position.z, velocity.x, velocity.y, angle, angularVelocity })
                bits.Add(BitConverter.SingleToInt32Bits(value));
        }
        return bits.ToArray();
    }
}
