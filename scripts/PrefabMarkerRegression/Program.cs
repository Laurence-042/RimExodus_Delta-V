using System;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using UnityEngine;

internal static class Program
{
    private static int checks;

    private static int Main()
    {
        try
        {
            Run();
            Console.WriteLine($"PASS {checks} prefab marker checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    private static void Run()
    {
        Check(PrefabObjectMarkerUtility.ClusterCount(0f, 100f, 1f, 128) == 3,
            "density is objects per 10,000 square cells");
        Check(PrefabObjectMarkerUtility.ClusterCount(100f, 20f, 1f, 128) == 0,
            "outer radius clamps to inner radius");
        Check(PrefabObjectMarkerUtility.ClusterCount(0f, 5000f, 100f, 37) == 37,
            "cluster count respects configured cap");
        Check(PrefabObjectMarkerUtility.InheritsClusterChild(false, true)
            && PrefabObjectMarkerUtility.InheritsClusterChild(true, false)
            && !PrefabObjectMarkerUtility.InheritsClusterChild(false, false),
            "cluster-child provenance propagates through later exact children");

        var pool = new List<PrefabClusterEntry>
        {
            new PrefabClusterEntry("AsteroidSmall.xml", 3f),
            new PrefabClusterEntry("AsteroidLarge.xml", 1f),
            new PrefabClusterEntry("Ignored.xml", 0f)
        };
        List<PrefabObjectSpawnPlan> first = PrefabObjectMarkerUtility.BuildClusterPlans(pool,
            100f, 1000f, 2f, 12f, 500, false, 1234567);
        List<PrefabObjectSpawnPlan> second = PrefabObjectMarkerUtility.BuildClusterPlans(pool,
            100f, 1000f, 2f, 12f, 500, false, 1234567);
        Check(first.Count == 500, "derived cluster count respects the configured cap deterministically");
        Check(first.Count == second.Count && first.Zip(second, Same).All(value => value),
            "same seed reproduces paths, offsets, angles, and child seeds");
        Check(first.All(plan => plan.localOffset.magnitude >= 99.99f
            && plan.localOffset.magnitude <= 1000.01f), "all samples stay inside annulus");
        Check(first.All(plan => plan.relativeAngle >= 0f && plan.relativeAngle < Mathf.PI * 2f),
            "random relative facings are normalized");
        Check(first.All(plan => plan.clusterChild),
            "all batch-generated objects carry the cluster-child tag");
        Check(first.All(plan => (plan.seed & 3) == 0),
            "configured SpacePose facing is not compounded by prefab-local rotation");
        Check(first.All(plan => plan.prefabPath != "Ignored.xml"),
            "zero-weight prefab is excluded");
        int small = first.Count(plan => plan.prefabPath == "AsteroidSmall.xml");
        Check(small > first.Count * 0.62f && small < first.Count * 0.87f,
            "weighted selection tracks 3:1 distribution");
        double averageSquared = first.Average(plan => (double)plan.localOffset.sqrMagnitude);
        double expectedSquared = (100d * 100d + 1000d * 1000d) * 0.5d;
        Check(Math.Abs(averageSquared - expectedSquared) / expectedSquared < 0.08d,
            "annulus sampling is uniform by area");

        List<PrefabObjectSpawnPlan> sparse = PrefabObjectMarkerUtility.BuildClusterPlans(pool,
            100f, 160f, 5f, 20f, 50, false, 9);
        bool spaced = true;
        for (int i = 0; i < sparse.Count; i++)
            for (int j = i + 1; j < sparse.Count; j++)
                spaced &= Vector2.Distance(sparse[i].localOffset, sparse[j].localOffset) >= 19.999f;
        Check(spaced, "feasible minimum spacing is honored");

        List<PrefabObjectSpawnPlan> crowded = PrefabObjectMarkerUtility.BuildClusterPlans(pool,
            0f, 30f, 100f, 25f, 128, false, 17);
        bool crowdedSpaced = true;
        for (int i = 0; i < crowded.Count; i++)
            for (int j = i + 1; j < crowded.Count; j++)
                crowdedSpaced &= Vector2.Distance(crowded[i].localOffset,
                    crowded[j].localOffset) >= 24.999f;
        Check(crowded.Count < PrefabObjectMarkerUtility.ClusterCount(0f, 30f, 100f, 128)
            && crowdedSpaced, "overcrowded clusters reduce count instead of violating spacing");

        List<PrefabObjectSpawnPlan> changed = PrefabObjectMarkerUtility.BuildClusterPlans(pool,
            100f, 1000f, 2f, 12f, 500, false, 7654321);
        Check(!Same(first[0], changed[0]), "rerolled seed changes the cluster");
        Check(PrefabObjectMarkerUtility.BuildClusterPlans(new List<PrefabClusterEntry>(),
            0f, 100f, 1f, 0f, 10, false, 1).Count == 0,
            "empty pool produces no plans");
    }

    private static bool Same(PrefabObjectSpawnPlan a, PrefabObjectSpawnPlan b)
    {
        return a.prefabPath == b.prefabPath
            && a.localOffset == b.localOffset && a.relativeAngle == b.relativeAngle
            && a.inheritFaction == b.inheritFaction && a.seed == b.seed;
    }
}
