using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using HarmonyLib;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static readonly List<(bool start, Vector2 point)> engineEdges = new();
    static bool CaptureEngineEdge(Vector2 point, bool start)
    { engineEdges.Add((start, point)); return false; }

    static void CheckMotionFeedback(UniverseManager universe)
    {
        var service = new SpaceProjectileService(universe);
        var patch = new Harmony("DeltaV.Regression.MotionFeedback");
        var edge = AccessTools.Method(typeof(SpaceProjectileService), "PlayEngineEdge");
        patch.Patch(edge, prefix: new HarmonyMethod(typeof(Program), nameof(CaptureEngineEdge)));
        try
        {
            object group = Activator.CreateInstance(typeof(SpaceProjectileService)
                .GetNestedType("EngineGroups", System.Reflection.BindingFlags.NonPublic), true);
            var level = (float[])group.GetType().GetField("level").GetValue(group);
            var power = (float[])group.GetType().GetField("power").GetValue(group);
            var points = (Vector2[])group.GetType().GetField("point").GetValue(group);
            power[0] = 400;
            points[0] = new Vector2(8, 2) * power[0];
            void Step(int tick, float throttle, bool existing = true)
            {
                level[0] = throttle;
                Call(service, "UpdateEngineGroup", null, group, 0, tick, existing);
            }
            engineEdges.Clear();
            Step(0, 0.5f, false);
            Check(engineEdges.Count == 0, "observing an already-burning engine does not replay ignition");
            Step(10, 0.5f);
            Step(15, 0);
            Step(16, 0.04f);
            Check(engineEdges.Count == 0, "brief low-throttle gaps do not retrigger grouped audio");
            Step(27, 0);
            Check(engineEdges.Count == 0, "shutdown waits for the quiet interval");
            Step(28, 0);
            Check(engineEdges.Count == 1 && !engineEdges[0].start && engineEdges[0].point == new Vector2(8, 2),
                "shutdown retains the last powered nozzle-group location");
            Step(30, 0.04f);
            Check(engineEdges.Count == 1, "idle micro-corrections do not trigger ignition");
            Step(31, 0.5f);
            Step(40, 0.5f);
            Check(engineEdges.Count == 2 && engineEdges[1].start, "a sustained new burn starts exactly once");
            Step(52, 0);
            Step(53, 0.5f);
            Check(engineEdges.Count == 3 && !engineEdges[2].start,
                "rapid restart respects ignition rate limiting after shutdown");

            var map = TestMap(9081);
            var ship = new MapSpaceObject(9081, map, new SpacePose(Vector3.forward, 0f));
            universe.objects.Add(ship);
            try
            {
                var smoke = (IList)AccessTools.Field(typeof(SpaceProjectileService), "driveExhaust").GetValue(service);
                var fire = (IList)AccessTools.Field(typeof(SpaceProjectileService), "driveFlames").GetValue(service);
                var profile = new ThrusterVisualProfile { smokeInterval = 1, flame = true };
                Vector2 nozzle = new Vector2(2.5f, 1.5f);
                void Emit(int tick) => Call(service, "EmitDriveExhaust", ship, nozzle, Vector2.up, 4f, 1f, profile, 0, tick);
                Emit(0);
                object first = smoke[0];
                Vector3 Origin(object p) => (Vector3)AccessTools.Field(p.GetType(), "origin").GetValue(p);
                Vector2 Velocity(object p) => (Vector2)AccessTools.Field(p.GetType(), "velocity").GetValue(p);
                Vector3 firstOrigin = Origin(first);
                Vector2 firstVelocity = Velocity(first);
                ship.pose.angle = Mathf.PI / 2f;
                ship.pose.position = OrbitSphere.Move(ship.pose.position, new Vector2(10, 0));
                Emit(1);
                Check(Origin(smoke[0]) == firstOrigin && Velocity(smoke[0]) == firstVelocity
                    && OrbitSphere.Offset(firstOrigin, Origin(smoke[1])).magnitude > 9f
                    && Vector2.Dot(firstVelocity.normalized, Velocity(smoke[1]).normalized) < 0.2f,
                    "hard turn changes newly emitted exhaust without rotating its detached trail");
                profile.smoke = false;
                Emit(2);
                Check(smoke.Count == 2 && fire.Count == 3, "thruster profile disables smoke independently of flame");
                profile.smoke = true;
                for (int i = 3; i < 1200; i++) Emit(i);
                Check(smoke.Count == 768 && fire.Count == 384, "dense exhaust obeys independent smoke and flame budgets");
                var particles = (IList)AccessTools.Field(typeof(SpaceProjectileService), "particles").GetValue(service);
                Call(service, "NotifyFoundationDestroyed", map, new IntVec3(1, 0, 1));
                Check(particles.Count == 4 && particles.Cast<object>().All(p =>
                    AccessTools.Field(p.GetType(), "kind").GetValue(p).ToString() == "Chip"
                    && (int)AccessTools.Field(p.GetType(), "lifetime").GetValue(p) >= 150),
                    "foundation destruction emits a small set of long-lived metal fragments");
                OrbitSphere.GridAngles(ship.pose.position, out double lat, out double lon);
                Check(OrbitSphere.Offset(ship.pose.position, OrbitSphere.GridPoint(lat, lon)).magnitude < 0.01f,
                    "absolute grid coordinates round-trip through the authoritative orbit sphere");
                Type grid = typeof(SpaceProjectileService).Assembly.GetType("DeltaV.SpaceCoordinateGrid");
                double Spacing(double n) => (double)AccessTools.Method(grid, "Step").Invoke(null, new object[] { n });
                Check(Spacing(4.1) == 8 && Spacing(8) == 8 && Spacing(8.1) == 16,
                    "zoom density changes retain nested absolute grid lines");
                double angular = (double)AccessTools.Method(grid, "AngularStep").Invoke(null, new object[] { 0.0123 });
                double turns = Math.PI * 2 / angular;
                Check(Math.Abs(turns - Math.Round(turns)) < 1e-8
                    && Math.Abs(Math.PI / 2 / angular - Math.Round(Math.PI / 2 / angular)) < 1e-8,
                    "grid spacing joins exactly at longitude seam and latitude poles");
            }
            finally { universe.objects.Remove(ship); Find.Maps.Remove(map); }
            Call(service, "ResetMotionFeedback");
            Check(((IDictionary)AccessTools.Field(typeof(SpaceProjectileService), "engineGroups")
                .GetValue(service)).Count == 0, "runtime reset releases motion feedback caches");
            Check(((IList)AccessTools.Field(typeof(SpaceProjectileService), "driveExhaust").GetValue(service)).Count == 0
                && ((IList)AccessTools.Field(typeof(SpaceProjectileService), "driveFlames").GetValue(service)).Count == 0,
                "runtime reset clears both procedural exhaust pools");
        }
        finally { patch.UnpatchAll(patch.Id); engineEdges.Clear(); }
    }
}
