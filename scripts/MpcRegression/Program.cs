using System;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Verse;
using DeltaV;
using UnityEngine;
using System.Linq;
using System.Globalization;
using System.Xml.Linq;

// Guidance, solver and allocation all call the production DLL.
// No game loop, void thrust, residual assist or substitute controller is used.
internal static class Program
{
    static readonly MethodInfo Allocate = typeof(ShipPhysics).GetMethod("SolveAllocation", BindingFlags.NonPublic | BindingFlags.Static);
    static AutopilotMpcProfile profile = AutopilotMpcProfile.Balanced;
    static bool exactOnly;
    static int Main(string[] args)
    {
        bool strict = Array.IndexOf(args, "--strict") >= 0;
        exactOnly = Array.IndexOf(args, "--exact") >= 0;
        foreach (string arg in args)
            if (arg.StartsWith("--profile=")) profile = Enum.Parse<AutopilotMpcProfile>(arg.Substring(10), true);
        Console.WriteLine($"Profile={profile} accuracy={(exactOnly ? "strict" : "staged")}");
        var mod = (DeltaVMod)RuntimeHelpers.GetUninitializedObject(typeof(DeltaVMod));
        typeof(DeltaVMod).GetField("<Settings>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(mod, new DeltaVSettings());
        var mods = (Dictionary<Type, Mod>)typeof(LoadedModManager)
            .GetField("runningModClasses", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        mods[typeof(DeltaVMod)] = mod;
        bool interceptPassed = RunIntercept("boarding", new Vector2(-80, 0), Vector2.zero, 0, true,
            requireForwardThrust: true);
        interceptPassed &= RunIntercept("boarding-turn", new Vector2(-80, -20), new Vector2(0, 4), Mathf.PI, true);
        interceptPassed &= RunIntercept("boarding-moving-loaded", new Vector2(-180, -25), Vector2.zero, 0,
            true, 280, new Vector2(2, 1));
        interceptPassed &= RunIntercept("boarding-unboosted", new Vector2(-80, 0), Vector2.zero, 0, false,
            requireForwardThrust: true);
        interceptPassed &= RunIntercept("boarding-close", new Vector2(-9, 0), new Vector2(15, 0), 0, true,
            requireForwardThrust: true);
        interceptPassed &= RunIntercept("boarding-crosses-within-step", new Vector2(-5.5f, 0), new Vector2(65, 0), 0, true,
            requireForwardThrust: true);
        if (Array.IndexOf(args, "--intercept-only") >= 0) return interceptPassed ? 0 : 1;
        bool passed = interceptPassed & CheckPredictionFactors() & CheckSolver() & SolverChecks.Run();
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
        passed &= Run("retreat-1500", new Vector2(-1500, 0), new Vector2(22.83f, 0), 0, 0, strict, playbackSteps: 0, precision: true);
        passed &= Run("retreat-fast", new Vector2(-1500, 0), new Vector2(55, 0), 0, 0, strict, playbackSteps: 0, precision: true);
        passed &= Run("retreat-drag", new Vector2(-1500, 0), new Vector2(22.83f, 0), 0, 0, strict, playbackSteps: 0, drag: true, precision: true);
        passed &= Run("retreat-weak", new Vector2(-1500, -8), new Vector2(22.83f, 3), 0, 0, strict, reverseFactor: .55f, playbackSteps: 0, precision: true);
        // Balanced baseline: production ADMM, same physical allocator, actual arrival tolerances,
        // followed by a full second in the window. Allow at most 5% longer to arrive.
        passed &= Run("dock-rest60", new Vector2(-60, 0), Vector2.zero, 0, 0, strict, precision: true, requireFullThrust: true, baselineSeconds: 14.55);
        passed &= Run("dock-40-v12", new Vector2(-40, 0), new Vector2(12, 0), 0, 0, strict, precision: true, requireFullThrust: true, baselineSeconds: 11.08);
        passed &= Run("dock-20-v8", new Vector2(-20, 0), new Vector2(8, 0), 0, 0, strict, precision: true, baselineSeconds: 9.48);
        passed &= Run("dock-5-v2", new Vector2(-5, 0), new Vector2(2, 0), 0, 0, strict, precision: true, baselineSeconds: 7.60);
        passed &= Run("dock-weak160", new Vector2(-160, 0), new Vector2(20, 0), 0, 0, strict, reverseFactor: .55f, precision: true, requireFullThrust: true, baselineSeconds: 19.42);
        return passed ? 0 : 1;
    }

    static bool RunIntercept(string name, Vector2 position, Vector2 velocity, float angle,
        bool boost, float mass = 140, Vector2 targetVelocity = default, bool requireForwardThrust = false)
    {
        DeltaVMod.CurrentSettings.passiveDrag = true;
        XElement xml = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_ShuttlesAndMoonPool.xml")
            .Descendants("virtualThrusters").Single();
        Vector2 Pair(string text)
        {
            var p = text.Trim('(', ')').Split(',');
            return new Vector2(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture));
        }
        var report = new ShipPhysics.MassReport { mass = mass, inertia = mass * 34 / 12 };
        var normal = new ShipPhysics.MassReport { mass = mass, inertia = report.inertia };
        foreach (var entry in xml.Elements("li"))
        {
            Vector2 force = Pair((string)entry.Element("force"));
            Vector2 point = Pair((string)entry.Element("position"));
            foreach (var r in new[] { report, normal }) r.thrusters.Add(new ShipPhysics.Thruster
                { localPos = point, dir = force.normalized, thrust = force.magnitude, operational = true });
        }
        var guidance = new OnlineMpcGuidance(); guidance.Reset(profile);
        double[] Matrix(ShipPhysics.ControlModel m)
        {
            var b = new double[9];
            for (int axis = 0; axis < 3; axis++)
            {
                Vector2 a = SpaceTransform.Rotate(m.forceColumns[axis] / mass, angle);
                b[axis] = a.x; b[3 + axis] = a.y; b[6 + axis] = m.torqueColumns[axis] / report.inertia;
            }
            return b;
        }
        int remaining = boost ? 600 : 0, fallback = 0, solves = 0, planStep = 4;
        float omega = 0, closest = float.MaxValue;
        GuidanceCommand held = default;
        var scratch = new float[6];
        double solveMs = 0;
        for (int tick = 0; tick < 7200; tick++)
        {
            bool boosted = remaining > 0;
            for (int i = 0; i < 6; i++) report.thrusters[i].thrust = normal.thrusters[i].thrust * (boosted && i < 2 ? 2 : 1);
            if (tick % 6 == 0 || remaining == 0 && boost)
            {
                if (remaining == 0 && boost) planStep = 4; // Real capability version invalidates playback on depletion.
                var model = ShipPhysics.BuildControlModel(report);
                var normalModel = ShipPhysics.BuildControlModel(normal);
                if (position.sqrMagnitude > 9f * 9f && planStep < 3
                    && guidance.TryGetPlannedStep(planStep + 1, model, out var planned))
                {
                    planStep++;
                    held = planned;
                }
                else
                {
                    Vector2 toward = -position;
                    float targetAngle = Mathf.Atan2(toward.y, toward.x);
                    float targetOmega = (toward.x * -velocity.y - toward.y * -velocity.x) / Math.Max(0.01f, toward.sqrMagnitude);
                    bool solved = guidance.Compute(new GuidanceRequest
                    {
                        intercept = true, positionError = toward, relativeVelocity = velocity,
                        referenceVelocity = targetVelocity, angleError = SpaceTransform.WrapAngle(targetAngle - angle),
                        relativeAngularVelocity = omega - targetOmega, referenceAngularVelocity = targetOmega,
                        dt = 0.1f, elapsedSteps = planStep + 1, controlModel = model, controlAcceleration = Matrix(model),
                        previousX = held.normalizedX, previousZ = held.normalizedZ, previousAngular = held.normalizedAngular,
                        brakingAcceleration = ShipPhysics.PreviewBrakingAcceleration(report, SpaceTransform.RotateInverse(toward.normalized, angle)),
                        boostSeconds = remaining / 60f, unboostedModel = boosted ? normalModel : null,
                        unboostedAcceleration = Matrix(normalModel)
                    }, out held);
                    if (!solved || held.usedFallback) fallback++;
                    solveMs += held.solveMilliseconds; solves++; planStep = 0;
                }
                if (remaining == 0) boost = false;
            }
            var cmd = ShipPhysics.ForceToCommand(report, held.desiredLocalForce, held.desiredTorque);
            object[] call = { report, cmd, scratch, Vector2.zero, 0f };
            Allocate.Invoke(null, call);
            Vector2 force = (Vector2)call[3]; float torque = (float)call[4];
            if (requireForwardThrust && force.x < 0.99f * (report.thrusters[0].thrust + report.thrusters[1].thrust))
            {
                Console.WriteLine($"FAIL {name}: forward thrust dropped to {force.x:F1} at {tick / 60f:F2}s");
                return false;
            }
            if (remaining > 0 && (scratch[0] > 0.001f || scratch[1] > 0.001f)) remaining--;
            (float linear, float angular) = ShipPhysics.PassiveDragFactors(1f / 60f);
            velocity = (velocity + targetVelocity + SpaceTransform.Rotate(force, angle) / mass / 60f) * linear - targetVelocity;
            omega = (omega + torque / report.inertia / 60f) * angular;
            position += velocity / 60f; angle = SpaceTransform.WrapAngle(angle + omega / 60f);
            closest = Math.Min(closest, position.magnitude);
            bool reached = requireForwardThrust
                ? position.x >= 0f && Mathf.Abs(position.y) < 0.75f
                : position.magnitude < 2.5f && Math.Abs(SpaceTransform.WrapAngle(Mathf.Atan2(-position.y, -position.x) - angle)) < 0.5f;
            if (reached)
            {
                bool passed = fallback == 0 && velocity.magnitude > 2;
                Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name} seconds={(tick + 1)/60f:F2} speed={velocity.magnitude:F2} boostRemaining={remaining} fallback={fallback}/{solves} avgSolve={solveMs/solves:F2}ms");
                return passed;
            }
        }
        Console.WriteLine($"FAIL {name}: missed; closest={closest:F2} d={position.magnitude:F2} fallback={fallback}/{solves}");
        return false;
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
            var solver = new DenseActiveSetQpSolver();
            var x = new double[2];
            bool solved = solver.Solve(2, 2, new[] { 2*scale, 0, 0, 2*scale },
                new[] { -4*scale, 2*scale }, new[] { 1.0, 0, 0, 1.0 },
                new[] { -bound, -bound }, new[] { bound, bound }, 512, x, out _, out _);
            if (!solved || Math.Abs(x[0] - Math.Min(2, bound)) > 0.002
                || Math.Abs(x[1] + Math.Min(1, bound)) > 0.002)
            { Console.WriteLine($"FAIL QP scale={scale} bound={bound}"); return false; }
        }
        var equalitySolver = new DenseActiveSetQpSolver();
        var equalityX = new double[2];
        if (!equalitySolver.Solve(2, 1, new[] { 2.0, 0, 0, 2.0 }, new[] { -4.0, 2.0 },
            new[] { 1.0, 1.0 }, new[] { 0.0 }, new[] { 0.0 }, 512, equalityX, out _, out _)
            || Math.Abs(equalityX[0] - 1.5) > 0.002 || Math.Abs(equalityX[1] + 1.5) > 0.002)
        { Console.WriteLine("FAIL QP equality"); return false; }
        Console.WriteLine("PASS QP: known interior/bound optima at 3 scales, equality constraint");
        return true;
    }

    static bool Run(string name, Vector2 position, Vector2 velocity, float angle, float targetAngle, bool strict,
        float reverseFactor = 1f, int playbackSteps = 1, bool drag = false, Vector2 targetVelocity = default,
        bool precision = false, bool requireFullThrust = false, double baselineSeconds = 0)
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
        guidance.Reset(profile);
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
        float peakForward = 0, peakReverse = 0;
        int fullForwardTicks = 0, fullReverseTicks = 0;
        int approximate = 0;
        GuidanceAccuracy lastAccuracy = GuidanceAccuracy.Strict;
        for (int tick = 0; tick < 7200; tick++)
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
                double braking = ShipPhysics.PreviewBrakingAcceleration(report, SpaceTransform.RotateInverse(-position.normalized, angle));
                double closing = Math.Max(0, Vector2.Dot(velocity, -position.normalized));
                (float decay, _) = ShipPhysics.PassiveDragFactors(1f / 60f);
                double stop = BrakingGuidanceFallback.StoppingDistance(closing, .8 * braking, Math.Pow(decay, 60));
                GuidanceAccuracy accuracy = position.magnitude <= 9 || lastAccuracy == GuidanceAccuracy.Strict && position.magnitude <= 18
                    ? GuidanceAccuracy.Strict : position.magnitude <= stop + closing * .4
                        ? GuidanceAccuracy.Braking : GuidanceAccuracy.Cruise;
                int period = playbackSteps > 0 ? playbackSteps : accuracy == GuidanceAccuracy.Strict ? 1 : accuracy == GuidanceAccuracy.Braking ? 2 : 4;
                bool changed = accuracy != lastAccuracy;
                lastAccuracy = accuracy;
                if (!changed && planStep + 1 < period && guidance.TryGetPlannedStep(planStep + 1, model, out GuidanceCommand planned))
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
                        dt = 0.1f, elapsedSteps = Math.Max(1, planStep + 1), previousX = held.normalizedX,
                        previousZ = held.normalizedZ, previousAngular = held.normalizedAngular,
                        controlModel = model, controlAcceleration = b,
                        brakingAcceleration = braking, accuracy = exactOnly ? GuidanceAccuracy.Strict : accuracy,
                        approachReserveDistance = accuracy == GuidanceAccuracy.Braking ? closing * (period - 1) * .1 : 0
                    }, out held);
                    planStep = 0;
                    solves++;
                    solveTimes.Add(held.solveMilliseconds);
                    if (solved && !CheckPlanConstraints(guidance, held))
                    { Console.WriteLine($"FAIL {name}: original-unit constraint violation"); return false; }
                    if (held.qpStatus == QpSolveStatus.FeasibleApproximate)
                    {
                        approximate++;
                        double limit = accuracy == GuidanceAccuracy.Cruise ? .01 : accuracy == GuidanceAccuracy.Braking ? .001 : 0;
                        if (exactOnly || limit == 0 || held.relativeOptimalityGap < 0 || held.relativeOptimalityGap > limit)
                        { Console.WriteLine($"FAIL {name}: uncertified approximation"); return false; }
                    }
                }
                if (!solved || held.usedFallback) fallback++;
                maxPrimal = Math.Max(maxPrimal, held.primalResidual);
                maxDual = Math.Max(maxDual, held.dualResidual);
                if (strict && (!solved || held.usedFallback))
                {
                    Console.WriteLine($"FAIL {name}: MPC rejected at tick {tick}; status={held.qpStatus} iterations={held.qpIterations}; residual={held.primalResidual:F5}/{held.dualResidual:F5}");
                    return false;
                }
            }
            var cmd = ShipPhysics.ForceToCommand(report, held.desiredLocalForce, held.desiredTorque);
            object[] call = { report, cmd, scratch, Vector2.zero, 0f };
            Allocate.Invoke(null, call);
            Vector2 force = (Vector2)call[3];
            float torque = (float)call[4];
            peakForward = Math.Max(peakForward, force.x / 4000);
            peakReverse = Math.Max(peakReverse, -force.x / (4000 * reverseFactor));
            if (force.x / 4000 >= .95) fullForwardTicks++;
            if (-force.x / (4000 * reverseFactor) >= .95) fullReverseTicks++;
            (float linearDecay, float angularDecay) = ShipPhysics.PassiveDragFactors(1f / 60f);
            velocity = (velocity + targetVelocity + SpaceTransform.Rotate(force, angle) / report.mass / 60f)
                * linearDecay - targetVelocity;
            omega = (omega + torque / report.inertia / 60f) * angularDecay;
            position += velocity / 60f;
            angle = SpaceTransform.WrapAngle(angle + omega / 60f);
            maxSpeed = Math.Max(maxSpeed, velocity.magnitude);
            furthestPastTarget = Math.Max(furthestPastTarget, -Vector2.Dot(position, initialDirection));
            float positionTolerance = precision ? .05f : .35f;
            bool window = position.magnitude <= positionTolerance && Math.Abs(SpaceTransform.WrapAngle(targetAngle - angle)) <= (precision ? .01f : 1.15f * Mathf.Deg2Rad)
                && (!precision || velocity.magnitude < .15f && Math.Abs(omega) < .03f);
            if (!window && heldTicks > 0) { losses++; Console.WriteLine($"WINDOW LOST {name} tick={tick} d={position.magnitude:F4} v={velocity.magnitude:F4} angle={SpaceTransform.WrapAngle(targetAngle-angle)*Mathf.Rad2Deg:F2} hold={heldTicks}"); }
            heldTicks = window ? heldTicks + 1 : 0;
            if (heldTicks >= 60 && velocity.magnitude < 0.15f && Math.Abs(omega) < 0.03f)
            {
                solveTimes.Sort();
                bool clean = fallback == 0 && losses == 0 && furthestPastTarget <= positionTolerance
                    && (!requireFullThrust || fullForwardTicks >= 12 && fullReverseTicks >= 42)
                    && (baselineSeconds == 0 || profile != AutopilotMpcProfile.Balanced || (tick + 1) / 60.0 <= baselineSeconds * 1.05);
                Console.WriteLine($"{(clean ? "PASS" : "BASELINE")} {name}: seconds={(tick+1)/60f:F2} d={position.magnitude:F4} speed={velocity.magnitude:F4} peak={maxSpeed:F2} windowLosses={losses} fallback={fallback}/{solves} approx={approximate} thrustPeak={peakForward:P1}/{peakReverse:P1} pastTarget={furthestPastTarget:F3} solveP95={solveTimes[(int)((solveTimes.Count-1)*0.95)]:F2}ms wallMs={clock.ElapsedMilliseconds}");
                if (requireFullThrust) Console.WriteLine($"  fullThrustSeconds={fullForwardTicks/60.0:F2}/{fullReverseTicks/60.0:F2}");
                return !strict || clean;
            }
        }
        Console.WriteLine($"FAIL {name}: timeout d={position.magnitude:F4} speed={velocity.magnitude:F4} fallback={fallback}/{solves}");
        return false;
    }

    static bool CheckPlanConstraints(OnlineMpcGuidance guidance, GuidanceCommand command)
    {
        // Read the actual assembled QP and full plan, independently of the solver's acceptance.
        object Field(string name) => typeof(OnlineMpcGuidance).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(guidance);
        var a = (double[])Field("a"); var lo = (double[])Field("lower"); var hi = (double[])Field("upper");
        var x = (double[])Field("solution"); int n = (int)Field("variables");
        for (int r = 0; r < command.qpConstraints; r++)
        {
            double value = 0;
            for (int j = 0; j < n; j++) value += a[r * n + j] * x[j];
            double tolerance = r < n ? 1e-5 : (r - n) % 6 == 5 ? 1e-5 : 1e-4;
            if (!double.IsFinite(value) || value < lo[r] - tolerance || value > hi[r] + tolerance) return false;
        }
        return true;
    }
}
