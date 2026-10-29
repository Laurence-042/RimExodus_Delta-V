using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeltaV;
using UnityEngine;

internal static partial class Program
{
    private sealed class ExpansionTrack
    {
        internal SpacePose[] defender, shooter;
        internal int changes, completed, reversals, windows, inRange;
        internal float min = float.MaxValue, max, maxAngle;
        internal int samples;
    }
    private static ExpansionTrack RepositionTrack(FighterFixture f, float distance, string mode, int seed, bool moving)
    {
        var result = new ExpansionTrack { defender = new SpacePose[96 * 60 + 1], shooter = new SpacePose[96 * 60 + 1] };
        var drive = new DeltaV.DriveInfo(f.mass);
        var settings = new CombatTacticSettings();
        var a = new TacticAgent(Vector2.right * distance, Mathf.PI / 2f) { body = StartFighter(f, distance, Mathf.PI / 2f) };
        var shooter = new TacticAgent(Vector2.zero, 0f);
        a.reposition.Reset(seed); shooter.reposition.Reset(seed + 100);
        a.attack = new CombatAttackPose { behavior = mode == "hold" ? CombatTactic.HoldRange
            : mode == "circle" ? CombatTactic.Circle : CombatTactic.Automatic, distance = distance, heading = -Mathf.PI / 2f };
        shooter.attack = new CombatAttackPose { behavior = CombatTactic.HoldRange, distance = distance, heading = 0f };
        float previousSpeed = 0f;
        var random = new System.Random(seed); int rawNext = 0; float rawPhase = 0f;
        for (int tick = 0; tick < result.defender.Length; tick++)
        {
            result.defender[tick] = a.body.pose;
            result.shooter[tick] = moving ? shooter.body.pose : new SpacePose(Origin, 0f);
            SpacePose target = result.shooter[tick];
            if (tick % 30 == 0)
            {
                float? phase = null;
                if (mode == "raw")
                {
                    if (tick >= rawNext)
                    {
                        var delta = OrbitSphere.Offset(target.position, a.body.pose.position);
                        rawPhase = Mathf.Atan2(delta.y, delta.x) + ((float)random.NextDouble() * 2 - 1) * Mathf.PI / 6f;
                        rawNext = tick + random.Next(300, 601); result.changes++;
                    }
                    phase = rawPhase;
                }
                else if (mode != "circle")
                {
                    int oldNext = a.reposition.nextTick;
                    a.reposition.Update(a.body.pose, target, a.attack, settings, drive, tick);
                    if (a.reposition.nextTick != oldNext) { result.changes++; if (oldNext > 0) result.completed++; }
                    phase = a.reposition.PhaseAt(target.position);
                }
                a.intent = CombatAttackPose.Trajectory(a.body.pose, target, a.attack, drive, 1, .8f, tick, phase);
                if (moving)
                    shooter.intent = CombatAttackPose.Trajectory(shooter.body.pose, a.body.pose, shooter.attack, drive, 1, .8f, tick);
            }
            if (tick % 6 == 0)
            {
                SolveTactic(a, target, f.mass, drive, tick);
                if (moving) SolveTactic(shooter, a.body.pose, f.mass, drive, tick);
            }
            if (tick >= 2400)
            {
                Vector2 delta = OrbitSphere.Offset(target.position, HullPoint(f, a.body.pose));
                float range = delta.magnitude;
                float tangent = Vector2.Dot(a.body.pose.velocity - SpaceTransform.TransportVelocity(target, a.body.pose.position),
                    new Vector2(-delta.y, delta.x).normalized);
                if (Mathf.Abs(tangent) > .5f && Mathf.Abs(previousSpeed) > .5f && tangent * previousSpeed < 0) result.reversals++;
                if (Mathf.Abs(tangent) > .5f) previousSpeed = tangent;
                result.min = Mathf.Min(result.min, range); result.max = Mathf.Max(result.max, range);
                result.samples++; if (Mathf.Abs(range - distance) <= distance * .1f) result.inRange++;
                if (range <= distance + 10f) result.windows++;
                result.maxAngle = Mathf.Max(result.maxAngle, a.angleError * Mathf.Rad2Deg);
            }
            foreach (var agent in moving ? new[] { a, shooter } : new[] { a })
            {
                ShipPhysics.PreviewAllocation(f.mass, ShipPhysics.ForceToCommand(f.mass, agent.held.desiredLocalForce,
                    agent.held.desiredTorque), out var force, out float torque);
                ShipPhysics.Integrate(agent.body, f.mass, force, torque, Tick, false, false, false);
            }
        }
        return result;
    }
    private static bool MovingShot(FighterFixture f, ExpansionTrack track, int launch, float speed)
    {
        var source = track.shooter[launch];
        var defender = SpaceTransform.RigidPointPose(track.defender[launch], f.hullLocal, 0f);
        Vector2 p = OrbitSphere.Offset(source.position, defender.position);
        Vector2 v = SpaceTransform.TransportVelocity(defender, source.position) - source.velocity;
        if (!SpaceTransform.TrySolveInterceptPlanar(p, v, speed, out float flight)) return false;
        Vector2 velocity = (p + v * flight).normalized * speed + source.velocity;
        Vector3 from = source.position;
        var body = new SpaceObject();
        for (int i = 1; i <= 900 && launch + i < track.defender.Length; i++)
        {
            Vector3 to = OrbitSphere.Move(source.position, velocity * (i * Tick));
            body.pose = track.defender[launch + i];
            if (OccupiedSweep(f, body, from, to, .7071068f)) return true;
            from = to;
        }
        return false;
    }
    private static void RunExpansion()
    {
        var f = ReadActualFighter();
        var rows = new List<object>();
        foreach (bool moving in new[] { false, true })
        foreach (float distance in new[] { 100f, 115f, 140f })
        foreach (string mode in new[] { "hold", "circle", "raw", "reachable", "asymmetric" })
        foreach (int seed in mode == "raw" || mode == "reachable" ? new[] { 17, 42, 137 } : new[] { 17 })
        {
            if (moving && mode == "asymmetric") continue; // Direct-force baseline has a stationary reference by definition.
            ExpansionTrack track = mode == "asymmetric" ? new ExpansionTrack
                { defender = DirectTrack(f, distance, "side-asym", 4), shooter = Enumerable.Repeat(new SpacePose(Origin, 0f), 5761).ToArray() }
                : RepositionTrack(f, distance, mode, seed, moving);
            if (mode == "asymmetric")
            {
                float oldSpeed = 0f;
                for (int tick = 2400; tick < track.defender.Length; tick++)
                {
                    var body = track.defender[tick];
                    var delta = OrbitSphere.Offset(Origin, HullPoint(f, body));
                    float range = delta.magnitude;
                    float tangent = Vector2.Dot(body.velocity, new Vector2(-delta.y, delta.x).normalized);
                    if (Mathf.Abs(tangent) > .5f)
                    {
                        if (Mathf.Abs(oldSpeed) > .5f && oldSpeed * tangent < 0f) track.reversals++;
                        oldSpeed = tangent;
                    }
                    track.min = Mathf.Min(track.min, range); track.max = Mathf.Max(track.max, range);
                    track.samples++; if (Mathf.Abs(range - distance) <= distance * .1f) track.inRange++;
                    if (range <= distance + 10f) track.windows++;
                }
            }
            foreach (float speed in new[] { 30f, 35f, 45f })
            {
                int clear = 0;
                for (int shot = 0; shot < 120; shot++)
                    if (!MovingShot(f, track, 2400 + shot * 20, speed)) clear++;
                var row = new { moving, distance, mode, seed, speed, shots = 120, clear,
                    minRange = track.min, maxRange = track.max, track.changes, track.completed, track.reversals,
                    rangeFraction = track.samples == 0 ? 0f : (float)track.inRange / track.samples,
                    rangeWindow = track.samples == 0 ? 0f : (float)track.windows / track.samples,
                    maxHeadingError = mode == "asymmetric" ? (float?)null : track.maxAngle };
                rows.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
            }
        }
        File.WriteAllText("Source/obj/CombatManeuverSimulation/expansion-evasion.json",
            JsonSerializer.Serialize(new { f.hash, mass = f.mass.mass, inertia = f.mass.inertia, rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
