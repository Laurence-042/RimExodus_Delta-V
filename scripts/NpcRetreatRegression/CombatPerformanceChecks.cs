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
        var patch = new Harmony("DeltaV.Regression.CombatPerfStats");
        patch.Patch(AccessTools.PropertyGetter(typeof(Thing), "MaxHitPoints"),
            prefix: new HarmonyMethod(typeof(Program), nameof(TestHullMaxHitPoints)));
        var output = new List<string> { "ships,fire_mean_ms_per_second,fire_p95_ms_per_second,fire_max_ms_per_second,rebuild_ms,pose_mean_ms,pose_p95_ms,target_mean_ms" };
        try
        {
            foreach (int count in new[] { 1, 4, 12, 24 })
            {
                var ships = new List<(ShipBase ship, SpaceObject frame, ShipFireControl fire)>();
                for (int i = 0; i < Math.Max(2, count); i++)
                {
                    int id = 7000 + i * 20; var map = TestMap(id); var faction = i % 2 == 0 ? player : enemy;
                    var console = AddTestConsole(map, faction, id + 1);
                    var hull = new CombatHull(console.Ship);
                    for (int x = 0; x < 5; x++) for (int z = 0; z < 5; z++) hull.cells.Add(new IntVec3(x, 0, z));
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
                    if (window == 23) rebuild = watch.Elapsed.TotalMilliseconds;
                    else if (window >= 4) { times.Add(watch.Elapsed.TotalMilliseconds); targetTimes.Add(targetTime); }
                }
                // Reflection permits the identical probe binary to run against the saved pre-change DLL.
                var constructor = typeof(CombatAttackWeapon).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
                var arguments = new object[] { Vector2.zero, 0f, 360f, 120f, 0f, 45f, 100f, false, true, null };
                var weaponValue = constructor.Invoke(arguments.Take(constructor.GetParameters().Length).ToArray());
                var weapons = Array.CreateInstance(typeof(CombatAttackWeapon), 1); weapons.SetValue(weaponValue, 0);
                var choose = typeof(CombatAttackPose).GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic);
                var values = new object[] { weapons, new CombatTacticSettings(), null, Vector2.zero, 30f, 115f, 0f, 1, true, null, null, null, null };
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
                universe.Controls.Reset(); universe.objects.Clear(); Find.Maps.Clear(); ShipRegistry.ClearCaches(); ShipPhysics.ClearCache(); SpaceCollision.ClearCaches();
            }
        }
        finally { patch.UnpatchAll(patch.Id); }
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "combat-performance.csv"), output);
    }
}
