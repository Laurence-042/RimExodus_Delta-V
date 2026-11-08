using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void CheckCombatPerformance(UniverseManager universe, Faction player, Faction enemy)
    {
        var factionManager = (FactionManager)Raw(typeof(FactionManager));
        Set(factionManager, "ofPlayer", player);
        Set(Find.World, "factionManager", factionManager);
        Set(enemy, "relations", new List<FactionRelation> { new FactionRelation { other = player, kind = FactionRelationKind.Hostile } });
        Set(player, "relations", new List<FactionRelation> { new FactionRelation { other = enemy, kind = FactionRelationKind.Hostile } });
        ThingDefOf.PilotConsole = TestBuildingDef("benchmarkConsole");
        SampleHullCost(player);
        var patch = new Harmony("DeltaV.Regression.CombatPerfStats");
        patch.Patch(AccessTools.PropertyGetter(typeof(Thing), "MaxHitPoints"),
            prefix: new HarmonyMethod(typeof(Program), nameof(TestHullMaxHitPoints)));
        bool largeHulls = Environment.GetCommandLineArgs().Contains("--combat-large-hulls");
        if (largeHulls) patch.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
            prefix: new HarmonyMethod(typeof(Program), nameof(StructureFoundationWithOdyssey)));
        var output = new List<string> { "ships,fire_mean_ms_per_second,fire_p95_ms_per_second,fire_max_ms_per_second,rebuild_ms,pose_mean_ms,pose_p95_ms,target_mean_ms" };
        var decisions = new List<string>();
        try
        {
            foreach (int count in new[] { 1, 4, 12, 24 })
            {
                var ships = new List<(ShipBase ship, SpaceObject frame, ShipFireControl fire)>();
                for (int i = 0; i < Math.Max(2, count); i++)
                {
                    int id = 7000 + i * 1000; var map = TestMap(id, largeHulls ? 60 : 5); var faction = i % 2 == 0 ? player : enemy;
                    var console = AddTestConsole(map, faction, id + 1);
                    ShipBase hull;
                    if (largeHulls)
                    {
                        var foundation = (TerrainDef)Raw(typeof(TerrainDef));
                        foundation.tags = new List<string> { "Substructure" }; foundation.isFoundation = true;
                        var owners = new int[map.cellIndices.NumGridCells]; Set(SubstructureOwnership.TryGet(map), "ownerGrid", owners);
                        for (int x = 0; x < 50; x++) for (int z = 0; z < 36; z++)
                        {
                            int index = map.cellIndices.CellToIndex(new IntVec3(x, 0, z));
                            map.terrainGrid.foundationGrid[index] = foundation; owners[index] = console.shipId;
                        }
                        hull = console.Vessel;
                    }
                    else
                    {
                        var small = new CombatHull(console.Ship);
                        for (int x = 0; x < 5; x++) for (int z = 0; z < 5; z++) small.cells.Add(new IntVec3(x, 0, z));
                        hull = small;
                    }
                    var frame = new CombatObject(id, map, new SpacePose(OrbitSphere.Move(Vector3.forward,
                        new Vector2(i % 2 == 0 ? -55 : 55, (i / 2) * 8)), 0f));
                    frame.members.Add(hull); universe.objects.Add(frame);
                    var engineDef = TestBuildingDef("benchEngine" + i); engineDef.comps.Add(new CompProperties_GravshipThruster());
                    MoveCombatFixture(AddTestBuilding(map, faction, id + 2, engineDef, new EmptyComp()), new IntVec3(4, 0, 4));
                    for (int g = 0; g < 4; g++)
                    {
                        var bullet = TestBuildingDef("benchBullet" + i + "_" + g);
                        bullet.comps.Add(new CompProperties_SpaceProjectile { speed = 45, hasMap = false });
                        var weapon = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon
                            { projectileDef = bullet, range = 250, intrinsicAimSkill = 20 }, safeFire = false, cooldownUntilTick = 1000000 };
                        var gun = AddTestBuilding(map, faction, id + 3 + g, TestBuildingDef("benchGun" + i + "_" + g), weapon);
                        MoveCombatFixture(gun, new IntVec3(2 + g % 2, 0, 2 + g / 2));
                    }
                    SubstructureOwnership.BumpStructureVersion(); universe.Controls.For(hull);
                    ships.Add((hull, frame, universe.Controls.For(hull).FireControl));
                }
                Set(universe, "objectById", null); Set(universe, "objectByMap", null);
                var times = new List<double>();
                var targetTimes = new List<double>();
                double rebuild = 0;
                for (int window = 0; window < 24; window++)
                {
                    if (window == 23) SubstructureOwnership.BumpStructureVersion();
                    var watch = Stopwatch.StartNew();
                    double targetTime = 0;
                    for (int tick = 0; tick < 60; tick++)
                    {
                        Set(Find.TickManager, "ticksGameInt", 1000 + window * 60 + tick);
                        foreach (var item in ships.Take(count))
                        {
                            if (tick == 0)
                            {
                                var targetWatch = Stopwatch.StartNew(); item.fire.Decide(item.ship, item.frame);
                                targetTime += targetWatch.Elapsed.TotalMilliseconds;
                            }
                            item.fire.TickAutoVolley(item.ship, item.frame, false);
                        }
                    }
                    watch.Stop();
                    // Observe outside the timed region; no production branch is replaced.
                    foreach (var item in ships.Take(count))
                    {
                        var locks = (Dictionary<long, CombatTarget>)typeof(ShipFireControl)
                            .GetField("automaticTargets", Private).GetValue(item.fire);
                        var anchor = (CombatTarget)typeof(ShipFireControl).GetField("target", Private).GetValue(item.fire);
                        decisions.Add($"{count}/{window}/{item.ship.Identity}:{anchor.Ship}:"
                            + string.Join(";", locks.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value.Ship}/{p.Value.SystemId}")));
                    }
                    if (window == 23) rebuild = watch.Elapsed.TotalMilliseconds;
                    else if (window >= 4) { times.Add(watch.Elapsed.TotalMilliseconds); targetTimes.Add(targetTime); }
                }
                // Reflection permits the identical probe binary to run against the saved pre-change DLL.
                var constructor = typeof(CombatAttackWeapon).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
                var arguments = new object[] { Vector2.zero, 0f, 360f, 120f, 0f, 45f, 100f, false, true, null };
                var weaponValue = constructor.Invoke(arguments.Take(constructor.GetParameters().Length).ToArray());
                var weapons = Array.CreateInstance(typeof(CombatAttackWeapon), 1); weapons.SetValue(weaponValue, 0);
                var choose = typeof(CombatAttackPose).GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic);
                var values = new object[] { weapons, new CombatTacticSettings(), null, Vector2.zero, 30f, 115f, 0f, 1, true, null, null, null, null, null, null };
                var poseTimes = new List<double>();
                for (int run = 0; run < 100; run++)
                {
                    var watch = Stopwatch.StartNew();
                    for (int i = 0; i < count; i++) choose.Invoke(null, values.Take(choose.GetParameters().Length).ToArray());
                    if (run >= 10) poseTimes.Add(watch.Elapsed.TotalMilliseconds);
                }
                double Percentile(List<double> values) => values.OrderBy(v => v).ElementAt((int)(values.Count * .95));
                string row = $"{count},{times.Average():F4},{Percentile(times):F4},{times.Max():F4},{rebuild:F4},{poseTimes.Average():F4},{Percentile(poseTimes):F4},{targetTimes.Average():F4}";
                Console.WriteLine("COMBAT_PERF " + row); output.Add(row);
                if (!largeHulls) SampleCombatPlans(universe, ships.Take(count).ToList(), decisions);
                universe.Controls.Reset(); universe.objects.Clear(); Find.Maps.Clear(); ShipRegistry.ClearCaches(); ShipPhysics.ClearCache(); SpaceCollision.ClearCaches();
            }
        }
        finally { patch.UnpatchAll(patch.Id); }
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "combat-performance.csv"), output);
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "combat-decisions.txt"), decisions);
    }

    static void SampleCombatPlans(UniverseManager universe,
        List<(ShipBase ship, SpaceObject frame, ShipFireControl fire)> ships, List<string> trace)
    {
        var report = new ShipPhysics.MassReport { mass = 1000, inertia = 10000 };
        foreach (var dir in new[] { Vector2.right, Vector2.left, Vector2.up, Vector2.down })
            foreach (float side in new[] { -3f, 3f })
                report.thrusters.Add(new ShipPhysics.Thruster { dir = dir, localPos = new Vector2(side, side),
                    operational = true, thrust = 2000 });
        var drive = new DeltaV.DriveInfo(report);
        var plans = ships.Select(s => new ShipAutoManeuverControl(universe, s.ship.Identity)).ToArray();
        var method = typeof(ShipAutoManeuverControl).GetMethod("PlanTacticTrajectory", Private);
        var watch = new Stopwatch();
        for (int step = 0; step < 24; step++)
        {
            Set(Find.TickManager, "ticksGameInt", 4000 + step * 30);
            if (step == 12) SubstructureOwnership.BumpStructureVersion();
            foreach (var item in ships)
            {
                item.frame.pose.velocity = new Vector2(1f, item.ship.Identity.key % 3 - 1f);
                item.frame.pose.angularVelocity = .02f;
                item.frame.pose.position = OrbitSphere.Move(item.frame.pose.position, item.frame.pose.velocity * .5f);
                item.frame.pose.angle += .01f;
            }
            for (int i = 0; i < ships.Count; i++)
            {
                var own = ships[i];
                if (step == 8 || step == 16)
                {
                    var rule = new CombatStrategyRule { tactic = new CombatTacticSettings {
                        bearing = step == 8 ? CombatBearing.Enemy : CombatBearing.World,
                        bearingDegrees = 73f, behavior = CombatTactic.HoldRange } };
                    rule.maneuverCurves = new CombatManeuverCurves {
                        distance = new CombatScoreCurve(true, new CombatScorePoint(0, -40), new CombatScorePoint(100, 20), new CombatScorePoint(200, -50)),
                        outgoing = new CombatScoreCurve(true, new CombatScorePoint(0, 0), new CombatScorePoint(100, 30)),
                        incoming = new CombatScoreCurve(true, new CombatScorePoint(0, 0), new CombatScorePoint(100, -30)),
                        hullOverlap = new CombatScoreCurve(false, new CombatScorePoint(0, 0), new CombatScorePoint(1, -100)) };
                    own.fire.Strategy(own.ship).maneuverRules.Clear();
                    own.fire.Strategy(own.ship).maneuverRules.Add(rule);
                    own.fire.configurationVersion++;
                    foreach (var weapon in own.ship.Weapons) weapon.cooldownUntilTick = 0;
                }
                var enemy = universe.objects.SelectMany(f => f.Ships).First(s => s.Faction != own.ship.Faction);
                Set(plans[i], "anchorTarget", CombatTarget.ForShip(enemy.Identity));
                Set(plans[i], "offenseEnabled", step % 8 < 4);
                Set(plans[i], "anchorDistance", 110f);
                var targetPose = ShipRegistry.ShipPose(enemy);
                object[] args = { own.frame, targetPose, 12f, drive, null };
                watch.Start();
                bool valid = (bool)method.Invoke(plans[i], args);
                watch.Stop();
                var pose = (CombatAttackPose)typeof(ShipAutoManeuverControl).GetField("attackPose", Private).GetValue(plans[i]);
                var intent = (CombatTrajectoryIntent)args[4];
                var values = new List<float> { pose.distance, pose.heading, pose.firepower, pose.readyOutput,
                    pose.alignment, pose.mobility, pose.preference, pose.incomingScore, pose.timeScore, pose.overlapScore };
                for (int second = 0; second <= 4; second++)
                {
                    intent.Sample(second, out var p, out var v, out float a, out float w);
                    values.AddRange(new[] { p.x, p.y, p.z, v.x, v.y, a, w });
                }
                trace.Add($"plan/{ships.Count}/{step}/{i}/{valid}:" + string.Join(",", values.Select(BitConverter.SingleToInt32Bits)));
            }
        }
        Console.WriteLine($"PLAN_PERF ships={ships.Count} calls={ships.Count * 24} ms={watch.Elapsed.TotalMilliseconds:F3}");
    }

    static void SampleHullCost(Faction faction)
    {
        var patch = new Harmony("DeltaV.Regression.HullCost");
        patch.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
            prefix: new HarmonyMethod(typeof(Program), nameof(StructureFoundationWithOdyssey)));
        var map = TestMap(6900, 60);
        var console = AddTestConsole(map, faction, 6901);
        var foundation = (TerrainDef)Raw(typeof(TerrainDef));
        foundation.tags = new List<string> { "Substructure" }; foundation.isFoundation = true;
        var owners = new int[map.cellIndices.NumGridCells];
        Set(SubstructureOwnership.TryGet(map), "ownerGrid", owners);
        for (int x = 0; x < 50; x++) for (int z = 0; z < 36; z++)
        {
            int index = map.cellIndices.CellToIndex(new IntVec3(x, 0, z));
            map.terrainGrid.foundationGrid[index] = foundation; owners[index] = console.shipId;
        }
        SubstructureOwnership.BumpStructureVersion();
        Vector2 center = console.Vessel.HullCenter;
        var watch = Stopwatch.StartNew();
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) center = console.Vessel.HullCenter;
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes; watch.Stop();
        Console.WriteLine($"HULL_PERF cells=1800 calls=10000 ms={watch.Elapsed.TotalMilliseconds:F3} bytes={bytes} center={center.x:R}/{center.y:R}");
        ShipRegistry.Forget(map); Find.Maps.Remove(map); patch.UnpatchAll(patch.Id);
    }
}
