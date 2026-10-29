using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using DeltaV;
using UnityEngine;
using Verse;

internal static partial class Program
{
    private sealed class FighterFixture
    {
        internal ShipPhysics.MassReport mass = new ShipPhysics.MassReport();
        internal Map map;
        internal Vector2 hullCenter, hullLocal;
        internal HashSet<IntVec3> cells = new HashSet<IntVec3>();
        internal object surface;
        internal float radius;
        internal string hash;
    }
    private static readonly MethodInfo CellQuery = typeof(SpaceCollision).GetMethod("FindBestCell", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static FighterFixture ReadActualFighter()
    {
        XElement xml = XDocument.Load("Source/obj/CombatManeuverSimulation/fighter-inputs.xml").Root!;
        var f = new FighterFixture { hash = (string)xml.Attribute("sha256")! };
        int[] size = Numbers((string)xml.Attribute("size")!);
        f.map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
        f.map.info = new MapInfo { Size = new IntVec3(size[0], 1, size[1]) };
        f.map.cellIndices = new CellIndices(f.map);
        Vector2 origin = SpaceTransform.ShipLocalOrigin(f.map);
        var points = new List<(Vector2 p, float m, float intrinsic)>();
        foreach (var e in xml.Element("foundations")!.Elements("li"))
        {
            Vector2 p = ParseCell((string)e.Element("pos")!);
            f.cells.Add(new IntVec3((int)p.x, 0, (int)p.y));
            points.Add((p + Vector2.one * .5f - origin, ShipPhysics.MassPerDeckCell, 0));
        }
        foreach (var e in xml.Elements("building"))
        {
            Vector2 p = ParseCell((string)e.Attribute("pos")!);
            int[] wh = Numbers((string)e.Attribute("size")!);
            int rot = (int)e.Attribute("rotation")!;
            CellRect rect = GenAdj.OccupiedRect(new IntVec3((int)p.x, 0, (int)p.y), new Rot4(rot), wh[0], wh[1]);
            foreach (var cell in rect) f.cells.Add(cell);
            Vector2 center = new Vector2((rect.minX + rect.maxX + 1) * .5f, (rect.minZ + rect.maxZ + 1) * .5f) - origin;
            float m = (float)e.Attribute("mass")!;
            points.Add((center, m, m * (rect.Width * rect.Width + rect.Height * rect.Height) / 12f));
            string name = (string)e.Attribute("name")!;
            if (name == "SmallThruster" || name == "LargeThruster")
                f.mass.thrusters.Add(new ShipPhysics.Thruster { localPos = center,
                    dir = ShipPhysics.ThrustDir(new Rot4(rot)), thrust = ShipPhysics.ThrusterThrust * (float)e.Attribute("power")! / 100f, operational = true });
        }
        f.mass.mass = points.Sum(x => x.m);
        f.mass.comLocal = points.Aggregate(Vector2.zero, (s, x) => s + x.p * x.m) / f.mass.mass;
        f.mass.inertia = points.Sum(x => x.m * (x.p - f.mass.comLocal).sqrMagnitude + x.intrinsic);
        f.hullCenter = new Vector2((f.cells.Min(x => x.x) + f.cells.Max(x => x.x) + 1) * .5f,
            (f.cells.Min(x => x.z) + f.cells.Max(x => x.z) + 1) * .5f);
        f.hullLocal = f.hullCenter - origin;
        f.radius = f.cells.Max(x => (new Vector2(x.x + .5f, x.z + .5f) - f.hullCenter).magnitude) + .71f;
        Type surfaceType = typeof(SpaceCollision).GetNestedType("Surface", BindingFlags.NonPublic)!;
        f.surface = Activator.CreateInstance(surfaceType, true)!;
        byte[] kinds = new byte[f.map.cellIndices.NumGridCells];
        foreach (var cell in f.cells) kinds[f.map.cellIndices.CellToIndex(cell)] = (byte)SpaceCellKind.Substructure;
        surfaceType.GetField("kinds")!.SetValue(f.surface, kinds);
        surfaceType.GetField("buildings")!.SetValue(f.surface, new Thing[kinds.Length]);
        return f;
    }
    private static int[] Numbers(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"-?\d+").Select(m => int.Parse(m.Value)).ToArray();
    private static Vector3 HullPoint(FighterFixture f, SpacePose pose) => SpaceTransform.ShipLocalToUniverse(pose, f.hullLocal);
    private static Vector2 HullVelocity(FighterFixture f, SpacePose pose) => SpaceTransform.VelocityAtLocalPoint(pose, f.hullLocal, Origin);
    private static SpaceObject StartFighter(FighterFixture f, float distance, float angle, float vy = 0f)
    {
        var obj = new SpaceObject { pose = new SpacePose(OrbitSphere.Move(Origin,
            Vector2.right * distance - SpaceTransform.Rotate(f.hullLocal, angle)), angle) };
        var pose = obj.pose; pose.velocity = new Vector2(0, vy); obj.pose = pose;
        return obj;
    }
    private static void StepForce(FighterFixture f, SpaceObject body, Vector2 forceWorld, float heading)
    {
        float error = SpaceTransform.WrapAngle(heading - body.pose.angle);
        float alpha = 2.5f * error - 2.5f * body.pose.angularVelocity;
        ShipPhysics.PreviewAllocation(f.mass, ShipPhysics.ForceToCommand(f.mass,
            SpaceTransform.RotateInverse(forceWorld, body.pose.angle), alpha * f.mass.inertia), out var force, out float torque);
        ShipPhysics.Integrate(body, f.mass, force, torque, Tick, false, false, false);
    }
    private static float ForceCapacity(FighterFixture f, float heading, Vector2 direction)
    {
        return ShipPhysics.PreviewBrakingAcceleration(f.mass, -SpaceTransform.RotateInverse(direction, heading));
    }
    private static bool OccupiedSweep(FighterFixture f, SpaceObject body, Vector3 from, Vector3 to, float shellRadius)
    {
        Vector2 a = SpaceTransform.UniverseToMapPoint(body, f.map, from);
        Vector2 b = SpaceTransform.UniverseToMapPoint(body, f.map, to);
        Vector2 delta = b - a; float length = delta.magnitude;
        if (length < 1e-6f) return false;
        Vector2 dir = delta / length;
        float along = Mathf.Clamp(Vector2.Dot(f.hullCenter - a, dir), 0f, length);
        if ((f.hullCenter - a - dir * along).magnitude > f.radius + shellRadius + .5f) return false;
        var sweep = new SpaceCollision.SweptBody { sweepRadius = shellRadius };
        object[] args = { sweep, f.map, f.surface, a, a, dir, null!, null!, IntVec3.Invalid, SpaceCellKind.Empty, null! };
        // Same 0.5-cell sweep sampling as SweepOne; actual private cell-footprint query is called.
        for (int i = 0; i <= Mathf.CeilToInt(length / .5f); i++)
        {
            args[4] = a + dir * Mathf.Min(length, i * .5f);
            if ((bool)CellQuery.Invoke(null, args)!) return true;
        }
        return false;
    }
    private sealed class ShotResult
    {
        public bool intersection { get; set; }
        public float closest { get; set; }
        public float flight { get; set; }
        public float range { get; set; }
    }
    private static ShotResult FireAtTrack(FighterFixture f, SpacePose[] track, int launch, float speed, float aimDegrees = 0, float shellRadius = .7071068f)
    {
        Vector3 center = HullPoint(f, track[launch]);
        Vector2 p = OrbitSphere.Offset(Origin, center), v = HullVelocity(f, track[launch]);
        if (!SpaceTransform.TrySolveInterceptPlanar(p, v, speed, out float flight)) throw new Exception("No lead solution");
        Vector2 velocity = SpaceTransform.Rotate((p + v * flight).normalized * speed, aimDegrees * Mathf.Deg2Rad);
        var result = new ShotResult { closest = float.MaxValue, flight = flight, range = p.magnitude };
        var defender = new SpaceObject();
        Vector3 from = Origin;
        int maxTicks = Math.Min(track.Length - launch - 1, 900);
        for (int tick = 1; tick <= maxTicks; tick++)
        {
            Vector3 to = OrbitSphere.Move(Origin, velocity * (tick * Tick));
            defender.pose = track[launch + tick];
            result.closest = Mathf.Min(result.closest, OrbitSphere.ArcCells(to, HullPoint(f, defender.pose)));
            if (!result.intersection && OccupiedSweep(f, defender, from, to, shellRadius)) result.intersection = true;
            from = to;
        }
        return result;
    }
    private static SpacePose[] DirectTrack(FighterFixture f, float distance, string mode, float halfPeriod = 4, float vy = 0, int seconds = 96)
    {
        bool nose = mode.StartsWith("nose") || mode == "turn";
        float heading = nose ? Mathf.PI : Mathf.PI * .5f;
        float a = Mathf.Min(ForceCapacity(f, heading, Vector2.up), ForceCapacity(f, heading, Vector2.down)) * .95f;
        bool asymmetric = mode.Contains("asym");
        bool periodic = mode.Contains("weave") || mode.Contains("sine") || asymmetric;
        float ap = .95f * ForceCapacity(f, heading, Vector2.up), an = .95f * ForceCapacity(f, heading, Vector2.down);
        float reverseSeconds = halfPeriod * ap / an;
        float lambda = Mathf.Log(2) / 8f;
        if (periodic) vy = -a / lambda * (float)Math.Tanh(lambda * halfPeriod / 2);
        if (mode.Contains("sine")) { float k = Mathf.PI / halfPeriod; vy = -a * k / (lambda * lambda + k * k); }
        if (asymmetric)
        {
            float ep = Mathf.Exp(-lambda * halfPeriod), en = Mathf.Exp(-lambda * reverseSeconds);
            vy = (en * ap * (1 - ep) - an * (1 - en)) / (lambda * (1 - ep * en));
        }
        var body = StartFighter(f, distance, heading, vy);
        var track = new SpacePose[seconds * 60 + 1];
        for (int tick = 0; tick < track.Length; tick++)
        {
            track[tick] = body.pose;
            float t = tick * Tick;
            if (mode == "coast") { StepForce(f, body, Vector2.zero, heading); continue; }
            float ay;
            if (asymmetric)
            {
                float period = halfPeriod + reverseSeconds, phase = t % period;
                ay = phase < halfPeriod ? ap : -an;
                if (mode.EndsWith("ramp"))
                {
                    const float ramp = .5f;
                    if (phase < ramp / 2) ay = Mathf.Lerp(-an, ap, .5f + phase / ramp);
                    else if (phase > period - ramp / 2) ay = Mathf.Lerp(-an, ap, .5f + (phase - period) / ramp);
                    else if (Mathf.Abs(phase - halfPeriod) < ramp / 2) ay = Mathf.Lerp(ap, -an, .5f + (phase - halfPeriod) / ramp);
                }
            }
            else if (mode.Contains("weave")) ay = ((int)(t / halfPeriod) % 2 == 0 ? 1 : -1) * a;
            else if (mode.Contains("sine")) ay = a * Mathf.Sin(Mathf.PI * t / halfPeriod);
            else ay = 20f; // One-shot achievable bound: allocator saturates, no magic acceleration.
            Vector2 center = OrbitSphere.Offset(Origin, HullPoint(f, body.pose));
            float ax = periodic ? 1.5f * (distance - center.x) - 2f * body.pose.velocity.x : 0;
            if (mode == "turn") heading = Mathf.PI * .5f;
            StepForce(f, body, new Vector2(ax, ay) * f.mass.mass, heading);
        }
        return track;
    }
    private static SpacePose[] CircleTrack(FighterFixture f, float distance)
    {
        var report = f.mass; var drive = new DeltaV.DriveInfo(report); var settings = new CombatTacticSettings();
        var a = new TacticAgent(Vector2.right * distance, Mathf.PI);
        a.body = StartFighter(f, distance, Mathf.PI);
        // Fixed attack pose from the latest log; isolate trajectory/control instead of reselecting a synthetic weapon pose.
        a.attack = new CombatAttackPose { behavior = CombatTactic.Circle, distance = distance, heading = 31f * Mathf.Deg2Rad };
        var target = new SpacePose(Origin, 0);
        var track = new SpacePose[96 * 60 + 1];
        for (int tick = 0; tick < track.Length; tick++)
        {
            track[tick] = a.body.pose;
            if (tick % 30 == 0) a.intent = CombatAttackPose.Trajectory(a.body.pose, target, a.attack, drive, 1, settings.movement, tick);
            if (tick % 6 == 0) SolveTactic(a, target, report, drive, tick);
            ShipPhysics.PreviewAllocation(report, ShipPhysics.ForceToCommand(report, a.held.desiredLocalForce, a.held.desiredTorque), out var force, out float torque);
            ShipPhysics.Integrate(a.body, report, force, torque, Tick, false, false, false);
        }
        return track;
    }
    private static void RunActualFighter()
    {
        var f = ReadActualFighter();
        var rows = new List<object>(); var tracks = new Dictionary<string, object>();
        var capabilities = new List<object>();
        foreach (float heading in new[] { 0f, 30f, 60f, 90f, 180f })
        {
            float angle = heading * Mathf.Deg2Rad;
            var corners = f.cells.SelectMany(c => new[] {new Vector2(c.x,c.z),new Vector2(c.x+1,c.z),new Vector2(c.x,c.z+1),new Vector2(c.x+1,c.z+1)})
                .Select(p => SpaceTransform.Rotate(p - f.hullCenter, angle)).ToArray();
            capabilities.Add(new { mapAngle = heading, width = corners.Max(p => p.y) - corners.Min(p => p.y),
                plusY = ForceCapacity(f, angle, Vector2.up), minusY = ForceCapacity(f, angle, Vector2.down) });
        }
        Console.WriteLine($"ACTUAL mass={f.mass.mass:F2} inertia={f.mass.inertia:F2} COM=({f.mass.comLocal.x:F3},{f.mass.comLocal.y:F3}) radius={f.radius:F2}");
        Console.WriteLine(JsonSerializer.Serialize(capabilities));
        foreach (float distance in new[] { 100f, 115f, 140f })
        foreach (string mode in new[] { "nose", "side", "turn" })
        foreach (float vy in new[] { 0f, -12f, 12f })
        {
            var track = DirectTrack(f, distance, mode, vy: vy, seconds: 15);
            foreach (float speed in new[] { 30f, 35f, 45f })
            {
                var shot = FireAtTrack(f, track, 0, speed);
                rows.Add(new { experiment = "single", distance, mode, vy, speed, shot });
            }
        }
        foreach (float distance in new[] {100f,115f,140f})
        foreach (var config in new[] { ("circle",4f), ("nose-weave",4f), ("side-weave",2f), ("side-weave",4f), ("side-weave",6f), ("side-sine",4f), ("side-sine",6f), ("side-asym",2f), ("side-asym",3f), ("side-asym",4f), ("side-asym-ramp",4f), ("side-asym",6f) })
        {
            string mode = config.Item1; float half = config.Item2;
            if (distance != 115 && !(mode == "circle" || mode == "nose-weave" || mode == "side-asym" && half == 4)) continue;
            var track = mode == "circle" ? CircleTrack(f, distance) : DirectTrack(f, distance, mode, half);
            var phasePoses = track.Skip(40*60).Take(40*60).ToArray();
            float minRange = phasePoses.Min(p => OrbitSphere.ArcCells(Origin, HullPoint(f,p)));
            float maxRange = phasePoses.Max(p => OrbitSphere.ArcCells(Origin, HullPoint(f,p)));
            foreach (float speed in new[] { 30f, 35f, 45f })
            {
                var shots = new List<ShotResult>();
                float period = mode == "circle" ? 40 : mode.Contains("asym")
                    ? half * (1 + ForceCapacity(f, Mathf.PI * .5f, Vector2.up) / ForceCapacity(f, Mathf.PI * .5f, Vector2.down)) : half * 2;
                for (int phase = 0; phase < 240; phase++)
                    shots.Add(FireAtTrack(f, track, 40*60 + Mathf.RoundToInt(period * 60 * phase / 240), speed));
                int clear = shots.Count(s => !s.intersection);
                var row = new { experiment="phase", distance, mode, half, speed, count=shots.Count, clear,
                    minimumClosest=shots.Min(s=>s.closest), medianClosest=shots.OrderBy(s=>s.closest).ElementAt(shots.Count/2).closest,
                    minRange, maxRange };
                rows.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
                if (distance == 115 && mode == "side-asym" && half == 4)
                {
                    int denseClear = 0;
                    for (int phase = 0; phase < 480; phase++)
                        if (!FireAtTrack(f,track,40*60 + Mathf.RoundToInt((.125f + period * phase / 480)*60),speed).intersection) denseClear++;
                    float difference = Mathf.Abs(denseClear/480f-clear/240f);
                    if (difference > .025f) throw new Exception("Phase sampling has not converged");
                    rows.Add(new {experiment="phase-validation",distance,mode,half,speed,count=480,clear=denseClear,difference});
                }
            }
            tracks[distance+mode+half] = track.Where((p,i)=>i%6==0).Select((p,i)=>new {t=i*.1, x=OrbitSphere.Offset(Origin,HullPoint(f,p)).x,
                y=OrbitSphere.Offset(Origin,HullPoint(f,p)).y, angle=p.angle, vx=HullVelocity(f,p).x, vy=HullVelocity(f,p).y}).ToArray();
        }
        // Sanity: stationary target is intersected; far transverse ray does not touch the fixture.
        var stationary = StartFighter(f,115,0);
        var still = Enumerable.Repeat(stationary.pose,901).ToArray();
        if (!FireAtTrack(f,still,0,45).intersection || FireAtTrack(f,still,0,45,45).intersection)
            throw new Exception("Occupied-cell sweep sanity failure");
        File.WriteAllText("Source/obj/CombatManeuverSimulation/fighter-evasion.json", JsonSerializer.Serialize(new {
            prefabSha256=f.hash, mass=f.mass.mass,inertia=f.mass.inertia,comX=f.mass.comLocal.x,comY=f.mass.comLocal.y,
            radius=f.radius, thrust=f.mass.thrusters.GroupBy(t=>Mathf.RoundToInt(Mathf.Atan2(t.dir.y,t.dir.x)*Mathf.Rad2Deg))
                .Select(g=>new {angle=g.Key,count=g.Count(),force=g.Sum(t=>t.thrust)}),
            cells=f.cells.Select(c=>new[]{c.x,c.z}), capabilities, rows, tracks },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS actual prefab, native occupied rectangles, production allocation/integration/lead/cell query; diagnostic overlaps only, no damage or shields.");
    }
}
