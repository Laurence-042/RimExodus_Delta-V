using System;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Verse;
using DeltaV;
using UnityEngine;

// Production guidance/solver sources are linked, and allocation calls the production DLL.
// No game loop, void thrust, residual assist or substitute controller is used.
internal static class Program
{
    static readonly MethodInfo Allocate = typeof(ShipPhysics).GetMethod("SolveAllocation", BindingFlags.NonPublic | BindingFlags.Static);
    static int Main(string[] args)
    {
        bool strict = Array.IndexOf(args, "--strict") >= 0;
        var mod = (DeltaVMod)RuntimeHelpers.GetUninitializedObject(typeof(DeltaVMod));
        typeof(DeltaVMod).GetField("<Settings>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(mod, new DeltaVSettings());
        var mods = (Dictionary<Type, Mod>)typeof(LoadedModManager)
            .GetField("runningModClasses", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        mods[typeof(DeltaVMod)] = mod;
        bool passed = CheckPredictionFactors() & CheckSolver();
        passed &= Run("straight", new Vector2(-58.7f, 0), Vector2.zero, 0f, 0f, strict);
        passed &= Run("turn-55", new Vector2(-58.7f, -0.657f), Vector2.zero, 0f, -55f * Mathf.Deg2Rad, strict);
        passed &= Run("lateral", new Vector2(-20f, -8f), new Vector2(4f, 3f), -55f * Mathf.Deg2Rad, -55f * Mathf.Deg2Rad, strict);
        passed &= Run("playback-4", new Vector2(-58.7f, -0.657f), Vector2.zero, 0f, -55f * Mathf.Deg2Rad, strict, 1f, 4);
        passed &= Run("asymmetric", new Vector2(-58.7f, -10f), Vector2.zero, 0f, 0f, strict, 0.35f);
        passed &= Run("fast-20", new Vector2(-160f, -8f), new Vector2(20f, 2f), 0f, 0f, strict, 0.55f, 4);
        passed &= Run("turn-180", new Vector2(-60f, -20f), Vector2.zero, 0f, Mathf.PI, strict);
        passed &= Run("drag", new Vector2(-58.7f, -0.657f), Vector2.zero, 0f, -55f * Mathf.Deg2Rad, strict, drag: true);
        passed &= Run("moving-drag", new Vector2(-58.7f, -10f), Vector2.zero, 0f, 0f, strict,
            reverseFactor: 0.55f, drag: true, targetVelocity: new Vector2(2f, 1f));
        return passed ? 0 : 1;
    }

    static bool CheckPredictionFactors()
    {
        foreach (float decay in new[] { 1f, 0.999f, 0.97f })
        foreach (int ticks in new[] { 1, 6, 120 })
        {
            double p = 0, v = 3, acceleration = -0.7;
            for (int i = 0; i < ticks; i++) { v = (v + acceleration / 60) * decay; p += v / 60; }
            ShipPhysics.HeldInputFactors(ticks / 60.0, decay, out double d, out double g, out double h);
            if (Math.Abs(v - (3 * d + acceleration * g)) > 1e-8
                || Math.Abs(p - (3 * g + acceleration * h)) > 1e-8)
            { Console.WriteLine("FAIL prediction factors"); return false; }
        }
        Console.WriteLine("PASS prediction factors: exact 60 Hz composition, 9 cases");
        return true;
    }

    static bool CheckSolver()
    {
        foreach (double scale in new[] { 0.01, 1.0, 10000.0 })
        foreach (double bound in new[] { 0.5, 5.0 })
        {
            var solver = new DenseAdmmQpSolver();
            var x = new double[2];
            bool solved = solver.Solve(2, 2, new[] { 2*scale, 0, 0, 2*scale },
                new[] { -4*scale, 2*scale }, new[] { 1.0, 0, 0, 1.0 },
                new[] { -bound, -bound }, new[] { bound, bound }, 512, x, out _, out _);
            if (!solved || Math.Abs(x[0] - Math.Min(2, bound)) > 0.002
                || Math.Abs(x[1] + Math.Min(1, bound)) > 0.002)
            { Console.WriteLine($"FAIL QP scale={scale} bound={bound}"); return false; }
        }
        var equalitySolver = new DenseAdmmQpSolver();
        var equalityX = new double[2];
        if (!equalitySolver.Solve(2, 1, new[] { 2.0, 0, 0, 2.0 }, new[] { -4.0, 2.0 },
            new[] { 1.0, 1.0 }, new[] { 0.0 }, new[] { 0.0 }, 512, equalityX, out _, out _)
            || Math.Abs(equalityX[0] - 1.5) > 0.002 || Math.Abs(equalityX[1] + 1.5) > 0.002)
        { Console.WriteLine("FAIL QP equality"); return false; }
        Console.WriteLine("PASS QP: known interior/bound optima at 3 scales, equality constraint");
        return true;
    }

    static bool Run(string name, Vector2 position, Vector2 velocity, float angle, float targetAngle, bool strict,
        float reverseFactor = 1f, int playbackSteps = 1, bool drag = false, Vector2 targetVelocity = default)
    {
        DeltaVMod.CurrentSettings.passiveDrag = drag;
        var report = new ShipPhysics.MassReport { mass = 1200f, inertia = 63926f };
        // Paired thrusters, with translation/rotation sharing the same physical actuators.
        foreach (float side in new[] { -1f, 1f })
        {
            report.thrusters.Add(new ShipPhysics.Thruster { localPos = new Vector2(0, side * 7), dir = Vector2.right, thrust = 2000, operational = true });
            report.thrusters.Add(new ShipPhysics.Thruster { localPos = new Vector2(0, side * 7), dir = Vector2.left, thrust = 2000 * reverseFactor, operational = true });
            report.thrusters.Add(new ShipPhysics.Thruster { localPos = new Vector2(side * 4, 0), dir = Vector2.up, thrust = 4000, operational = true });
            report.thrusters.Add(new ShipPhysics.Thruster { localPos = new Vector2(side * 4, 0), dir = Vector2.down, thrust = 4000, operational = true });
        }
        var guidance = new OnlineMpcGuidance();
        guidance.Reset(AutopilotMpcProfile.Balanced);
        GuidanceCommand held = default;
        float omega = 0;
        int fallback = 0, solves = 0, heldTicks = 0, losses = 0;
        int planStep = playbackSteps;
        double maxPrimal = 0, maxDual = 0;
        float maxSpeed = 0;
        Vector2 initialDirection = position.normalized;
        float furthestPastTarget = 0;
        var solveTimes = new List<double>();
        var clock = Stopwatch.StartNew();
        double[] b = new double[9];
        var scratch = new float[report.thrusters.Count];
        for (int tick = 0; tick < 3600; tick++)
        {
            if (tick % 6 == 0)
            {
                var model = ShipPhysics.BuildControlModel(report);
                for (int axis = 0; axis < 3; axis++)
                {
                    Vector2 acceleration = SpaceTransform.Rotate(model.forceColumns[axis] / report.mass, angle);
                    b[axis] = acceleration.x; b[3 + axis] = acceleration.y;
                    b[6 + axis] = model.torqueColumns[axis] / report.inertia;
                }
                bool solved;
                if (planStep + 1 < playbackSteps && guidance.TryGetPlannedStep(planStep + 1, model, out GuidanceCommand planned))
                {
                    held = planned;
                    planStep++;
                    solved = true;
                }
                else
                {
                    solved = guidance.Compute(new GuidanceRequest {
                        positionError = -position, relativeVelocity = velocity, referenceVelocity = targetVelocity,
                        angleError = SpaceTransform.WrapAngle(targetAngle - angle), relativeAngularVelocity = omega,
                        dt = 0.1f, elapsedSteps = Math.Min(planStep + 1, playbackSteps), previousX = held.normalizedX,
                        previousZ = held.normalizedZ, previousAngular = held.normalizedAngular,
                        controlModel = model, controlAcceleration = b,
                        brakingAcceleration = ShipPhysics.PreviewBrakingAcceleration(report, SpaceTransform.RotateInverse(-position.normalized, angle))
                    }, out held);
                    planStep = 0;
                    solves++;
                    solveTimes.Add(held.solveMilliseconds);
                }
                if (!solved || held.usedFallback) fallback++;
                maxPrimal = Math.Max(maxPrimal, held.primalResidual);
                maxDual = Math.Max(maxDual, held.dualResidual);
                if (strict && (!solved || held.usedFallback))
                {
                    Console.WriteLine($"FAIL {name}: MPC rejected at tick {tick}; residual={held.primalResidual:F5}/{held.dualResidual:F5}");
                    return false;
                }
            }
            var cmd = ShipPhysics.ForceToCommand(report, held.desiredLocalForce, held.desiredTorque);
            object[] call = { report, cmd, scratch, Vector2.zero, 0f };
            Allocate.Invoke(null, call);
            Vector2 force = (Vector2)call[3];
            float torque = (float)call[4];
            (float linearDecay, float angularDecay) = ShipPhysics.PassiveDragFactors(1f / 60f);
            velocity = (velocity + targetVelocity + SpaceTransform.Rotate(force, angle) / report.mass / 60f)
                * linearDecay - targetVelocity;
            omega = (omega + torque / report.inertia / 60f) * angularDecay;
            position += velocity / 60f;
            angle = SpaceTransform.WrapAngle(angle + omega / 60f);
            maxSpeed = Math.Max(maxSpeed, velocity.magnitude);
            furthestPastTarget = Math.Max(furthestPastTarget, -Vector2.Dot(position, initialDirection));
            bool window = position.magnitude <= 0.35f && Math.Abs(SpaceTransform.WrapAngle(targetAngle - angle)) <= 1.15f * Mathf.Deg2Rad;
            if (!window && heldTicks > 0) { losses++; Console.WriteLine($"WINDOW LOST {name} tick={tick} d={position.magnitude:F4} v={velocity.magnitude:F4} angle={SpaceTransform.WrapAngle(targetAngle-angle)*Mathf.Rad2Deg:F2} hold={heldTicks}"); }
            heldTicks = window ? heldTicks + 1 : 0;
            if (heldTicks >= 60 && velocity.magnitude < 0.15f && Math.Abs(omega) < 0.03f)
            {
                solveTimes.Sort();
                bool clean = fallback == 0 && losses == 0 && furthestPastTarget <= 0.35f;
                Console.WriteLine($"{(clean ? "PASS" : "BASELINE")} {name}: seconds={(tick+1)/60f:F2} d={position.magnitude:F4} speed={velocity.magnitude:F4} peak={maxSpeed:F2} windowLosses={losses} fallback={fallback}/{solves} residualMax={maxPrimal:F3}/{maxDual:F3} pastTarget={furthestPastTarget:F3} solveP95={solveTimes[(int)((solveTimes.Count-1)*0.95)]:F2}ms wallMs={clock.ElapsedMilliseconds}");
                return !strict || clean;
            }
        }
        Console.WriteLine($"FAIL {name}: timeout d={position.magnitude:F4} speed={velocity.magnitude:F4} fallback={fallback}/{solves}");
        return false;
    }
}
