using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void CheckEngagementFleetBaseline(UniverseManager universe, Faction player, Faction enemy)
    {
        using var patch = new EngagementFixturePatch();
        Set(enemy, "relations", new List<FactionRelation> { new FactionRelation { other = player, kind = FactionRelationKind.Hostile } });
        Set(player, "relations", new List<FactionRelation> { new FactionRelation { other = enemy, kind = FactionRelationKind.Hostile } });
        var factions = (FactionManager)Raw(typeof(FactionManager));
        Set(factions, "ofPlayer", player); Set(Find.World, "factionManager", factions);
        ThingDefOf.PilotConsole = TestBuildingDef("fleetConsole");
        var projectile = TestBuildingDef("fleetLaser");
        projectile.comps.Add(new CompProperties_SpaceProjectile { laserRange = 150, speed = 60, hasMap = false });
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = "FleetLaserEffect",
            projectileDefs = new List<ThingDef> { projectile },
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damage = 8 } });
        SpaceImpactEffects.ResetCache();
        var rows = new List<object>();
        bool profile = Environment.GetCommandLineArgs().Contains("--engagement-profile");
        bool profileWindow = Environment.GetCommandLineArgs().Contains("--engagement-profile-window");
        foreach (int count in profile || profileWindow ? new[] { 4 } : new[] { 1, 4, 12, 24 })
        {
            var ships = new List<ManeuverMotionObject>();
            for (int i = 0; i < Math.Max(2, count); i++)
            {
                int id = 110000 + i * 100;
                var faction = i % 2 == 0 ? player : enemy;
                var map = TestMap(id, 9);
                var body = new ManeuverMotionObject(id, map, new SpacePose(OrbitSphere.Move(Vector3.forward,
                    new Vector2(i % 2 == 0 ? 50 : -50, i / 2 * 40)), 0));
                var console = AddTestConsole(map, faction, id + 1);
                MoveCombatFixture(console.parent, new IntVec3(4, 0, 4));
                Set(typeof(Thing), console.parent, "hitPointsInt", 300);
                var hull = new CombatHull(console.Ship);
                var foundation = (TerrainDef)Raw(typeof(TerrainDef));
                foundation.tags = new List<string> { "Substructure" }; foundation.isFoundation = true;
                var owners = new int[map.cellIndices.NumGridCells]; Set(SubstructureOwnership.TryGet(map), "ownerGrid", owners);
                for (int x = 2; x <= 6; x++) for (int z = 2; z <= 6; z++)
                {
                    var cell = new IntVec3(x, 0, z); hull.cells.Add(cell);
                    int index = map.cellIndices.CellToIndex(cell); map.terrainGrid.foundationGrid[index] = foundation; owners[index] = console.shipId;
                }
                body.member = hull; universe.objects.Add(body); ships.Add(body);
                for (int gunIndex = 0; gunIndex < 4; gunIndex++)
                {
                    var weapon = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon {
                        projectileDef = projectile, intrinsicAimSkill = 20, range = 150, cooldownTicks = 12 }, safeFire = false };
                    var gun = AddTestBuilding(map, faction, id + 2 + gunIndex, TestBuildingDef("fleetGun" + id + "_" + gunIndex), weapon);
                    MoveCombatFixture(gun, new IntVec3(3 + gunIndex % 2 * 2, 0, 3 + gunIndex / 2 * 2));
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
                    object[] args = { body, other, radius, drive, null };
                    bool result = (bool)method.Invoke(planner, args); intent = (CombatTrajectoryIntent)args[4]; return result;
                }
                universe.Autopilot.TrackOrbit(new DriveSubject(own.objectId, own.member.Identity.key), Sample, 10, Plan);
            }
            var times = new List<double>();
            var watch = new Stopwatch();
            double coldMs = 0, invalidationMs = 0;
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            TimeSpan gcPauseStart = GC.GetTotalPauseDuration();
            long started = universe.Performance.engagementStarted, completed = universe.Performance.engagementCompleted;
            long coarse = universe.Performance.engagementCoarse, fine = universe.Performance.engagementFine;
            long elapsed = universe.Performance.engagementElapsed;
            long events = universe.Structures.Events, publications = universe.Structures.Publications;
            for (int tick = 0; tick < 1800; tick++)
            {
                if (profile || profileWindow) DeltaVMod.CurrentSettings.logPerformance = true;
                if (tick == 1500) SubstructureOwnership.BumpStructureVersion();
                watch.Restart(); RecoveryTick(universe.Autopilot, tick); watch.Stop();
                if (profile || profileWindow && tick == 1799)
                {
                    Console.WriteLine($"PROFILE allocationBytes={GC.GetAllocatedBytesForCurrentThread() - allocationStart} gen0={GC.CollectionCount(0) - gen0} gen1={GC.CollectionCount(1) - gen1} gen2={GC.CollectionCount(2) - gen2} gcPauseMs={(GC.GetTotalPauseDuration() - gcPauseStart).TotalMilliseconds:F3} searches={universe.Performance.engagementCompleted - completed}/{universe.Performance.engagementStarted - started} coarse={universe.Performance.engagementCoarse - coarse} fine={universe.Performance.engagementFine - fine}");
                    var totals = (long[])typeof(DeltaVPerformanceProbe).GetField("totals", Private).GetValue(universe.Performance);
                    var calls = (int[])typeof(DeltaVPerformanceProbe).GetField("calls", Private).GetValue(universe.Performance);
                    foreach (PerformanceStage stage in Enum.GetValues(typeof(PerformanceStage)))
                        if (stage != PerformanceStage.Count && calls[(int)stage] > 0)
                            Console.WriteLine($"PROFILE {stage} calls={calls[(int)stage]} ms={totals[(int)stage] * 1000d / Stopwatch.Frequency:F3}");
                    return;
                }
                if (tick == 0) coldMs = watch.Elapsed.TotalMilliseconds;
                if (tick >= 1500 && tick < 1506) invalidationMs += watch.Elapsed.TotalMilliseconds;
                if (tick >= 300) times.Add(watch.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            double mean = times.Average(), p95 = times[(int)(times.Count * .95)], p99 = times[(int)(times.Count * .99)];
            rows.Add(new { ships = count, ticks = 1800, warmup = 300, meanTickMs = mean, p95TickMs = p95,
                p99TickMs = p99, maxTickMs = times.Last(), coldMs, invalidationSixTicksMs = invalidationMs,
                allocationBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart,
                gcPauseMs = (GC.GetTotalPauseDuration() - gcPauseStart).TotalMilliseconds,
                searchesStarted = universe.Performance.engagementStarted - started,
                searchesCompleted = universe.Performance.engagementCompleted - completed,
                coarseCandidates = universe.Performance.engagementCoarse - coarse,
                fineCandidates = universe.Performance.engagementFine - fine,
                searchElapsedMs = (universe.Performance.engagementElapsed - elapsed) * 1000d / Stopwatch.Frequency,
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
        string output = Environment.GetCommandLineArgs().Contains("--engagement-fleet-regression")
            ? "Source/obj/EngagementForecast" : "Source/obj/EngagementBaseline-20261108";
        Directory.CreateDirectory(output);
        File.WriteAllText(output + "/fleet.json", JsonSerializer.Serialize(new {
            scope = "Simultaneous real tactics, weapon/threat queries, MPC, thruster allocation and integration. Four guns per ship; no projectile launch, damage, render or UI. Thirty seconds per workload.",
            rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
