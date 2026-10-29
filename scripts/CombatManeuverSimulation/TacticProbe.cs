using System;
using System.Diagnostics;
using DeltaV;
using UnityEngine;

internal static partial class Program
{
    private sealed class TacticAgent
    {
        internal SpaceObject body;
        internal readonly OnlineMpcGuidance guidance = new OnlineMpcGuidance();
        internal CombatAttackPose attack;
        internal readonly CombatReposition reposition = new CombatReposition();
        internal CombatTrajectoryIntent intent;
        internal GuidanceCommand held;
        internal readonly double[] acceleration = new double[9];
        internal int plans, solves, fallbacks;
        internal float minRange = float.MaxValue, maxRange, motion, angleError;
        internal double planningMs;
        internal TacticAgent(Vector2 start, float angle)
        {
            body = new SpaceObject { pose = new SpacePose(OrbitSphere.Move(Origin, start), angle) };
            guidance.Reset(AutopilotMpcProfile.Balanced);
            reposition.Reset(42);
        }
    }

    private static void RunTactic(string label, ShipPhysics.MassReport report, bool fixedGun, bool pair = false)
    {
        var drive = new DriveInfo(report);
        var settings = new CombatTacticSettings();
        var weapons = new[] { new CombatAttackWeapon(new Vector2(0f, 4f), 0f,
            fixedGun ? 2f : 360f, 120f, 0f, 45f, 100f, false, false) };
        var agents = pair ? new[] { new TacticAgent(Vector2.right * 75f, 0f),
            new TacticAgent(Vector2.left * 75f, Mathf.PI) }
            : new[] { new TacticAgent(Vector2.right * 150f, 0f) };
        for (int tick = 0; tick < 2400; tick++)
        {
            // Both planners read the same tick before either body advances.
            for (int i = 0; i < agents.Length; i++)
            {
                TacticAgent a = agents[i];
                SpacePose target = pair ? agents[1 - i].body.pose : new SpacePose(Origin, 0f);
                SpacePose own = a.body.pose;
                Vector2 toward = OrbitSphere.Offset(own.position, target.position);
                float bearing = Mathf.Atan2(toward.y, toward.x);
                if (tick % 120 == 0)
                {
                    var timer = Stopwatch.StartNew();
                    a.attack = CombatAttackPose.Choose(weapons, settings, drive,
                        SpaceTransform.RotateInverse(SpaceTransform.TransportVelocity(target, own.position)
                            - own.velocity, bearing), 30f, 150f,
                        SpaceTransform.WrapAngle(own.angle - bearing), 1, true, null,
                        a.plans == 0 ? (CombatAttackPose?)null : a.attack);
                    a.planningMs += timer.Elapsed.TotalMilliseconds;
                    a.plans++;
                }
                if (tick % 30 == 0)
                {
                    a.reposition.Update(own, target, a.attack, settings, drive, tick);
                    a.intent = CombatAttackPose.Trajectory(own, target, a.attack, drive, 1, settings.movement, tick,
                        a.reposition.PhaseAt(target.position));
                }
                if (tick % 6 == 0)
                    SolveTactic(a, target, report, drive, tick);
            }
            foreach (var a in agents)
            {
                ShipPhysics.PreviewAllocation(report, ShipPhysics.ForceToCommand(report,
                    a.held.desiredLocalForce, a.held.desiredTorque), out Vector2 force, out float torque);
                ShipPhysics.Integrate(a.body, report, force, torque, Tick, false, false, false);
            }
            if (tick < 1200) continue;
            for (int i = 0; i < agents.Length; i++)
            {
                var a = agents[i];
                var target = pair ? agents[1 - i].body.pose : new SpacePose(Origin, 0f);
                float range = OrbitSphere.ArcCells(a.body.pose.position, target.position);
                a.minRange = Mathf.Min(a.minRange, range); a.maxRange = Mathf.Max(a.maxRange, range);
                Vector2 toward = OrbitSphere.Offset(a.body.pose.position, target.position).normalized;
                a.motion += Mathf.Abs(Vector2.Dot(a.body.pose.velocity - SpaceTransform.TransportVelocity(target, a.body.pose.position),
                    new Vector2(toward.y, -toward.x)));
            }
        }
        foreach (var a in agents)
        {
            float meanTangent = a.motion / 1200f;
            Console.WriteLine($"TACTIC {label}: {a.attack.behavior} desiredRange={a.attack.distance:F1} "
                + $"range@20-40s={a.minRange:F1}..{a.maxRange:F1} meanRelativeTangent={meanTangent:F2} "
                + $"headingError={a.angleError * Mathf.Rad2Deg:F1}deg "
                + $"posePlanAvg={a.planningMs / a.plans:F3}ms fallback={a.fallbacks}/{a.solves}");
            if (a.maxRange > 165f || a.minRange < 30f || !fixedGun && meanTangent < 1f)
                throw new Exception("Tactic failed to sustain engagement/motion: " + label);
            if (fixedGun && a.angleError > 0.15f)
                throw new Exception("Fixed weapon failed to establish firing heading.");
        }
    }

    private static void SolveTactic(TacticAgent a, SpacePose target,
        ShipPhysics.MassReport report, DriveInfo drive, int tick)
    {
        SpacePose own = a.body.pose;
        var model = ShipPhysics.BuildControlModel(report);
        float elapsed = (tick - a.intent.startTick) * Tick;
        a.intent.Sample(elapsed, out Vector3 reference, out Vector2 referenceVelocity,
            out float referenceAngle, out float referenceOmega);
        for (int axis = 0; axis < 3; axis++)
        {
            Vector2 force = SpaceTransform.Rotate(model.forceColumns[axis] / report.mass, own.angle);
            a.acceleration[axis] = force.x;
            a.acceleration[3 + axis] = force.y;
            a.acceleration[6 + axis] = model.torqueColumns[axis] / report.inertia;
        }
        Vector2 error = -OrbitSphere.Offset(reference, own.position);
        bool solved = a.guidance.Compute(new GuidanceRequest
        {
            positionError = error,
            relativeVelocity = SpaceTransform.TransportVelocity(own, reference) - referenceVelocity,
            angleError = SpaceTransform.WrapAngle(referenceAngle - own.angle),
            relativeAngularVelocity = own.angularVelocity - referenceOmega,
            dt = .1f, elapsedSteps = 1,
            previousX = a.held.normalizedX, previousZ = a.held.normalizedZ,
            previousAngular = a.held.normalizedAngular,
            controlModel = model, controlAcceleration = a.acceleration,
            brakingAcceleration = error.sqrMagnitude < 1e-8f ? 0f : drive.PreviewBraking(
                SpaceTransform.RotateInverse(error.normalized, own.angle)),
            accuracy = GuidanceAccuracy.Strict, trajectorySeconds = a.intent.seconds,
            combatTrajectory = a.intent, referenceStartSeconds = elapsed,
            referencePosition = reference, referenceVelocity = referenceVelocity,
            referenceAngle = referenceAngle, referenceAngularVelocity = referenceOmega
        }, out a.held);
        a.solves++;
        if (!solved || a.held.usedFallback) a.fallbacks++;
        a.angleError = Mathf.Abs(SpaceTransform.WrapAngle(referenceAngle - own.angle));
    }
}
