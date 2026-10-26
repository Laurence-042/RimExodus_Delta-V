using System;
using DeltaV;
using UnityEngine;

internal static class SolverChecks
{
    internal static bool Run()
    {
        bool passed = RandomOptima() & FailureAndGapChecks() & AccuracyChecks() & GuidanceRecovery();
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} solver contracts: enumerated optima, scaling, failures, gap, mode policy, workspace reuse");
        return passed;
    }

    // Independent reference: in 2D a strictly convex optimum is interior, on one
    // boundary, or at a pairwise intersection. Enumerate all of them, no iterative solver.
    private static bool RandomOptima()
    {
        var random = new System.Random(71237);
        var solver = new DenseActiveSetQpSolver();
        for (int sample = 0; sample < 100; sample++)
        {
            double scale = Math.Pow(10, sample % 7 - 2);
            double aa = 1 + random.NextDouble() * 4, bb = random.NextDouble() - .5, cc = 1 + random.NextDouble() * 4;
            var p = new[] { aa * scale, bb * scale, bb * scale, cc * scale };
            var q = new[] { (random.NextDouble() * 20 - 10) * scale, (random.NextDouble() * 20 - 10) * scale };
            double angle = random.NextDouble() * 6.28;
            var a = new[] { 1.0, 0, 0, 1.0, Math.Cos(angle), Math.Sin(angle) };
            var lo = new[] { -1.0, -.7, -.25 };
            var hi = new[] { .8, 1.2, .45 };
            double best = Enumerate(p, q, a, lo, hi);
            for (int row = 0; row < 3; row++)
            {
                double rowScale = Math.Pow(10, (sample + row * 3) % 9 - 4);
                a[row * 2] *= rowScale; a[row * 2 + 1] *= rowScale;
                lo[row] *= rowScale; hi[row] *= rowScale;
            }
            var x = new[] { double.NaN, 900.0 }; // invalid warm start must not leak into an exact solve
            bool solved = solver.Solve(2, 3, p, q, a, lo, hi, 512, x, out _, out _);
            double value = Cost(p, q, x);
            if (!solved || solver.Status != QpSolveStatus.Optimal || Math.Abs(value - best) / scale > 1e-5)
            {
                Console.WriteLine($"FAIL enumerated QP #{sample}: status={solver.Status} value={value} expected={best}");
                return false;
            }
        }
        return true;
    }

    private static double Enumerate(double[] p, double[] q, double[] a, double[] lo, double[] hi)
    {
        double determinant = p[0] * p[3] - p[1] * p[2];
        double i00 = p[3] / determinant, i01 = -p[1] / determinant, i11 = p[0] / determinant;
        double f0 = -i00 * q[0] - i01 * q[1], f1 = -i01 * q[0] - i11 * q[1];
        double best = double.PositiveInfinity;
        void Consider(double x, double y)
        {
            for (int r = 0; r < lo.Length; r++)
            {
                double val = a[r * 2] * x + a[r * 2 + 1] * y;
                if (val < lo[r] - 1e-9 || val > hi[r] + 1e-9) return;
            }
            best = Math.Min(best, Cost(p, q, new[] { x, y }));
        }
        Consider(f0, f1);
        for (int s = 0; s < lo.Length * 2; s++)
        {
            int r = s / 2;
            double bound = s % 2 == 0 ? lo[r] : hi[r], gx = a[r * 2], gy = a[r * 2 + 1];
            double dx = i00 * gx + i01 * gy, dy = i01 * gx + i11 * gy;
            double lambda = (gx * f0 + gy * f1 - bound) / (gx * dx + gy * dy);
            Consider(f0 - lambda * dx, f1 - lambda * dy);
            for (int t = 0; t < s; t++)
            {
                int rr = t / 2;
                double other = t % 2 == 0 ? lo[rr] : hi[rr], hx = a[rr * 2], hy = a[rr * 2 + 1];
                double det = gx * hy - gy * hx;
                if (Math.Abs(det) > 1e-12) Consider((bound * hy - gy * other) / det, (gx * other - bound * hx) / det);
            }
        }
        return best;
    }

    private static double Cost(double[] p, double[] q, double[] x)
        => .5 * (x[0] * (p[0] * x[0] + p[1] * x[1]) + x[1] * (p[2] * x[0] + p[3] * x[1])) + q[0] * x[0] + q[1] * x[1];

    private static bool FailureAndGapChecks()
    {
        var solver = new DenseActiveSetQpSolver();
        var p = new[] { 2.0, 0, 0, 2.0 };
        var q = new[] { -4.0, 2.0 };
        var a = new[] { 1.0, 0, 0, 1.0 };
        var lo = new[] { -3.0, -3.0 };
        var hi = new[] { 1.99, 3.0 };
        var x = new[] { 1.989, -1.0 };
        if (!solver.Solve(2, 2, p, q, a, lo, hi, 512, x, out _, out _, .01)
            || solver.Status != QpSolveStatus.FeasibleApproximate || solver.RelativeGap > .01
            || x[0] > hi[0] + 1e-9 || x[0] < lo[0]) return false;
        double optimum = Cost(p, q, new[] { hi[0], -1.0 });
        if ((Cost(p, q, x) - optimum) / Math.Max(1, Math.Abs(optimum)) > .01) return false;
        x[0] = 300;
        if (solver.Solve(2, 2, p, q, a, lo, hi, 1, x, out _, out _, 1e-12)
            || solver.Status != QpSolveStatus.IterationLimit || solver.IterationsUsed > 1) return false;
        if (!solver.Solve(2, 2, p, q, a, lo, hi, 2, x, out _, out _)
            || solver.Status != QpSolveStatus.Optimal || solver.IterationsUsed != 2) return false;
        // Contradictory separate rows: cannot claim approximate success even with a generous gap.
        if (solver.Solve(1, 2, new[] { 2.0 }, new[] { 0.0 }, new[] { 1.0, 1.0 },
            new[] { 1.0, -1e30 }, new[] { 1e30, 0.0 }, 64, new double[1], out _, out _, .01)) return false;
        var nan = new[] { double.NaN, 0, 0, 2.0 };
        if (solver.Solve(2, 2, nan, q, a, lo, hi, 512, x, out _, out _)
            || solver.Status != QpSolveStatus.InvalidInput) return false;
        if (solver.Solve(2, 2, new[] { -1.0, 0, 0, 1.0 }, q, a, lo, hi, 512, x, out _, out _)
            || solver.Status != QpSolveStatus.NumericalFailure) return false;
        // Reuse after failure and dimension changes, including duplicate / equality rows.
        if (!solver.Solve(2, 3, p, q, new[] { 1.0, 1.0, 1.0, 1.0, 1.0, 0.0 },
            new[] { 0.0, 0.0, -3.0 }, new[] { 0.0, 0.0, 1.0 }, 512, x, out _, out _)
            || Math.Abs(x[0] - 1) > 1e-5 || Math.Abs(x[1] + 1) > 1e-5) return false;
        if (!solver.Solve(2, 2, p, q, a, lo, hi, 512, x, out _, out _)) return false;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
            if (!solver.Solve(2, 2, p, q, a, lo, hi, 512, x, out _, out _, .01)) return false;
        if (GC.GetAllocatedBytesForCurrentThread() != before)
        { Console.WriteLine("FAIL solver allocated after workspace warmup"); return false; }
        return true;
    }

    private static bool AccuracyChecks()
    {
        var request = new GuidanceRequest { accuracy = GuidanceAccuracy.Cruise };
        if (OnlineMpcGuidance.OptimalityTolerance(request) != .01) return false;
        request.accuracy = GuidanceAccuracy.Braking;
        if (OnlineMpcGuidance.OptimalityTolerance(request) != .001) return false;
        request.accuracy = GuidanceAccuracy.Strict;
        if (OnlineMpcGuidance.OptimalityTolerance(request) != 0) return false;
        request.accuracy = GuidanceAccuracy.Cruise;
        request.intercept = true;
        if (OnlineMpcGuidance.OptimalityTolerance(request) != 0) return false;
        request.intercept = false; request.motionOnly = true;
        if (OnlineMpcGuidance.OptimalityTolerance(request) != 0) return false;
        request.motionOnly = false; request.combatTrajectory.seconds = 3;
        return OnlineMpcGuidance.OptimalityTolerance(request) == 0;
    }

    private static bool GuidanceRecovery()
    {
        DeltaVMod.CurrentSettings.passiveDrag = false;
        var report = new ShipPhysics.MassReport { mass = 1, inertia = 1 };
        report.thrusters.Add(new ShipPhysics.Thruster { dir = Vector2.right, thrust = 1, operational = true });
        report.thrusters.Add(new ShipPhysics.Thruster { dir = Vector2.left, thrust = 1, operational = true });
        var model = ShipPhysics.BuildControlModel(report);
        var acceleration = new double[9];
        for (int j = 0; j < 3; j++)
        {
            acceleration[j] = model.forceColumns[j].x;
            acceleration[j + 3] = model.forceColumns[j].y;
            acceleration[j + 6] = model.torqueColumns[j];
        }
        var request = new GuidanceRequest { controlModel = model, controlAcceleration = acceleration,
            dt = .1f, motionOnly = true, positionError = new Vector2(1000, 300),
            relativeVelocity = Vector2.right, accuracy = GuidanceAccuracy.Cruise };
        var guidance = new OnlineMpcGuidance();
        guidance.Reset(AutopilotMpcProfile.Balanced);
        if (!guidance.Compute(request, out var first) || first.qpStatus != QpSolveStatus.Optimal
            || first.desiredLocalForce.x >= 0 || !guidance.TryGetPlannedStep(1, model, out _)) return false;
        request.positionError = new Vector2(-1000, -300);
        if (!guidance.Compute(request, out var second) || Math.Abs(first.normalizedX - second.normalizedX) > 1e-6) return false;
        request.dt = 0;
        if (guidance.Compute(request, out var failed) || failed.qpStatus != QpSolveStatus.InvalidInput
            || !failed.usedFallback || guidance.TryGetPlannedStep(1, model, out _)) return false;
        request.dt = .1f;
        if (!guidance.Compute(request, out _)) return false;
        // Capability loss must not replay the former nonzero control through a successful solve.
        foreach (var thruster in report.thrusters) thruster.operational = false;
        request.controlModel = ShipPhysics.BuildControlModel(report);
        request.controlAcceleration = new double[9];
        return guidance.Compute(request, out var disabled)
            && disabled.desiredLocalForce.sqrMagnitude == 0 && disabled.desiredTorque == 0;
    }
}
