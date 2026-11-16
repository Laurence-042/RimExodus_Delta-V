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
#endif
    }

    static ShipAutoManeuverControl MakeMothershipPlanner(UniverseManager universe,
        ManeuverMotionObject own, ManeuverMotionObject opponent)
    {
        var plan = new ShipAutoManeuverControl(universe, own.member.Identity);
        Set(plan, "anchorTarget", CombatTarget.ForShip(opponent.member.Identity));
        Set(plan, "anchorDistance", 0f);
        Set(plan, "offenseEnabled", true);
        return plan;
    }

    sealed record MothershipPlan(bool Valid, int Start, int Distance, int Heading, int Phase, int[] Track);
    static MothershipPlan ReadMothershipPlan(ShipAutoManeuverControl planner,
        ManeuverMotionObject own, ManeuverMotionObject foe)
    {
        var read = (FleetPlan)MothershipPlanMethod.CreateDelegate(typeof(FleetPlan), planner);
        bool valid = read(own, ShipRegistry.ShipPose(foe.member), 0, new DeltaV.DriveInfo(own.layout), out var intent);
        if (!valid) return new(false, 0, 0, 0, 0, Array.Empty<int>());
        var pose = (CombatAttackPose)typeof(ShipAutoManeuverControl).GetField("attackPose", Private).GetValue(planner);
        var movement = (CombatReposition)typeof(ShipAutoManeuverControl).GetField("reposition", Private).GetValue(planner);
        return new(true, intent.startTick, BitConverter.SingleToInt32Bits(pose.distance),
            BitConverter.SingleToInt32Bits(pose.heading), BitConverter.SingleToInt32Bits(movement.phase),
            FleetTrajectoryBits(intent));
    }

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
