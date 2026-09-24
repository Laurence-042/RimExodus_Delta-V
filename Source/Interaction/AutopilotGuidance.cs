using System;
using System.Diagnostics;
using UnityEngine;

namespace DeltaV
{
    internal enum AutopilotMpcProfile
    {
        Fast,
        Balanced,
        Precise
    }

    internal static class AutopilotMpcDiagnostics
    {
        private const int SampleCapacity = 256;
        private static readonly double[] solveSamples = new double[SampleCapacity];
        private static int sampleCount;
        private static int sampleCursor;
        private static int solveCount;
        private static int fallbackCount;
        private static double totalMilliseconds;
        private static double maximumMilliseconds;

        public static void Record(GuidanceCommand command)
        {
            solveCount++;
            if (command.usedFallback) fallbackCount++;
            totalMilliseconds += command.solveMilliseconds;
            maximumMilliseconds = Math.Max(maximumMilliseconds, command.solveMilliseconds);
            solveSamples[sampleCursor] = command.solveMilliseconds;
            sampleCursor = (sampleCursor + 1) % SampleCapacity;
            sampleCount = Math.Min(sampleCount + 1, SampleCapacity);
        }

        public static string Summary(AutopilotMpcProfile profile)
        {
            double[] sorted = new double[sampleCount];
            Array.Copy(solveSamples, sorted, sampleCount);
            Array.Sort(sorted);
            double p95 = sampleCount == 0 ? 0.0 : sorted[(int)Math.Floor((sampleCount - 1) * 0.95)];
            double average = solveCount == 0 ? 0.0 : totalMilliseconds / solveCount;
            return $"profile={profile}; solves={solveCount}; fallback={fallbackCount}; "
                + $"avg={average:F3}ms; p95(last {sampleCount})={p95:F3}ms; max={maximumMilliseconds:F3}ms";
        }

        public static string RunSelfTest()
        {
            double[] accelerations = { 0.5, 1.0, 2.2 };
            var result = new System.Text.StringBuilder("[DeltaV:MPC] deterministic self-test");
            bool allPassed = true;
            foreach (double acceleration in accelerations)
            {
                bool passed = RunOne(acceleration, out double finalPosition, out double finalSpeed,
                    out int crossings, out int fallbackUses, out double maxPrimal, out double maxDual);
                allPassed &= passed;
                result.Append($"\n- a={acceleration:F1}: {(passed ? "PASS" : "FAIL")}; "
                    + $"p={finalPosition:F3}; v={finalSpeed:F3}; crossings={crossings}; fallback={fallbackUses}; "
                    + $"maxResidual=({maxPrimal:F3},{maxDual:F3})");
            }
            result.Append($"\n- overall: {(allPassed ? "PASS" : "FAIL")}");
            return result.ToString();
        }

        private static bool RunOne(double acceleration, out double finalPosition, out double finalSpeed,
            out int crossings, out int fallbackUses, out double maxPrimal, out double maxDual)
        {
            var model = new ShipPhysics.ControlModel
            {
                mass = 1f,
                inertia = 1f,
                positiveX = (float)acceleration,
                negativeX = (float)acceleration,
                positiveZ = (float)acceleration,
                negativeZ = (float)acceleration,
                positiveTorque = 1f,
                negativeTorque = 1f
            };
            model.forceColumns[0] = new Vector2((float)acceleration, 0f);
            model.forceColumns[1] = new Vector2(0f, (float)acceleration);
            model.torqueColumns[2] = 1f;
            double[] b =
            {
                acceleration, 0, 0,
                0, acceleration, 0,
                0, 0, 1
            };
            IAutopilotGuidance guidance = new OnlineMpcGuidance();
            guidance.Reset(AutopilotMpcProfile.Balanced);
            double position = -Math.Max(120.0, 20.0 * 20.0 / (2.0 * acceleration) + 20.0);
            double velocity = 20.0;
            double previousPosition = position;
            crossings = 0;
            fallbackUses = 0;
            maxPrimal = 0.0;
            maxDual = 0.0;
            for (int step = 0; step < 1200; step++)
            {
                var request = new GuidanceRequest
                {
                    positionError = new Vector2((float)-position, 0f),
                    relativeVelocity = new Vector2((float)velocity, 0f),
                    dt = 0.1f,
                    controlModel = model,
                    controlAcceleration = b
                };
                guidance.Compute(request, out GuidanceCommand command);
                if (command.usedFallback) fallbackUses++;
                maxPrimal = Math.Max(maxPrimal, command.primalResidual);
                maxDual = Math.Max(maxDual, command.dualResidual);
                double appliedAcceleration = command.desiredLocalForce.x;
                // 被动阻力同口径进入自测被控对象（预测模型一致性的确定性验证）。
                (float linearDecay, _) = ShipPhysics.PassiveDragFactors(0.1f);
                velocity = velocity * linearDecay + appliedAcceleration * 0.1 * linearDecay;
                position += velocity * 0.1;
                if (Math.Sign(previousPosition) != Math.Sign(position)
                    && Math.Abs(previousPosition) > 0.25 && Math.Abs(position) > 0.25) crossings++;
                previousPosition = position;
            }
            finalPosition = position;
            finalSpeed = velocity;
            return Math.Abs(position) < 0.5 && Math.Abs(velocity) < 0.2 && crossings <= 1;
        }
    }

    internal struct GuidanceRequest
    {
        // positionError = target - ship；relativeVelocity = ship - target。
        public Vector2 positionError;
        public Vector2 relativeVelocity;
        public float angleError;
        public float relativeAngularVelocity;
        public float dt;
        public float voidBlend;
        public double previousX;
        public double previousZ;
        public double previousAngular;
        public ShipPhysics.ControlModel controlModel;
        // 归一化 [fx,fz,torque] → 目标切基 [ax,ay,alpha]，row-major 3x3。
        public double[] controlAcceleration;
    }

    internal struct GuidanceCommand
    {
        public Vector2 desiredLocalForce;
        public float desiredTorque;
        public double normalizedX;
        public double normalizedZ;
        public double normalizedAngular;
        public bool usedFallback;
        public bool solved;
        public double primalResidual;
        public double dualResidual;
        public double solveMilliseconds;
    }

    internal interface IAutopilotGuidance
    {
        void Reset(AutopilotMpcProfile profile);
        bool Compute(GuidanceRequest request, out GuidanceCommand command);
    }

    /// <summary>求解异常时的停止距离制导；不会退回旧饱和 PD。</summary>
    internal static class BrakingGuidanceFallback
    {
        public static void Compute(GuidanceRequest request, out GuidanceCommand command)
        {
            command = default;
            Vector2 p = -request.positionError; // ship - target
            Vector2 v = request.relativeVelocity;
            double safeAccel = MaxTranslationAcceleration(request.controlAcceleration) * 0.8;
            double safeAlpha = MaxAngularAcceleration(request.controlAcceleration) * 0.8;
            double desiredX = 0.0;
            double desiredY = 0.0;
            double desiredAlpha = 0.0;

            double distance = p.magnitude;
            if (safeAccel > 1e-6 && distance > 1e-6)
            {
                Vector2 toward = -p.normalized;
                double speedLimit = Math.Min(20.0, Math.Sqrt(2.0 * safeAccel * distance));
                Vector2 targetVelocity = toward * (float)speedLimit;
                Vector2 accel = (targetVelocity - v) / 0.5f;
                if (accel.magnitude > safeAccel) accel = accel.normalized * (float)safeAccel;
                desiredX = accel.x;
                desiredY = accel.y;
            }
            if (safeAlpha > 1e-6)
            {
                double theta = -request.angleError;
                double omegaLimit = Math.Sqrt(2.0 * safeAlpha * Math.Abs(theta));
                double targetOmega = -Math.Sign(theta) * omegaLimit;
                desiredAlpha = Clamp((targetOmega - request.relativeAngularVelocity) / 0.5,
                    -safeAlpha, safeAlpha);
            }

            SolveRegularized3x3(request.controlAcceleration, desiredX, desiredY, desiredAlpha,
                out double ux, out double uz, out double uw);
            WriteDesired(request.controlModel, ux, uz, uw, ref command);
            command.usedFallback = true;
        }

        private static double MaxTranslationAcceleration(double[] b)
        {
            if (b == null) return 0.0;
            double max = 0.0;
            for (int c = 0; c < 3; c++)
            {
                double x = b[c];
                double y = b[3 + c];
                max = Math.Max(max, Math.Sqrt(x * x + y * y));
            }
            return max;
        }

        private static double MaxAngularAcceleration(double[] b)
        {
            if (b == null) return 0.0;
            return Math.Max(Math.Abs(b[6]), Math.Max(Math.Abs(b[7]), Math.Abs(b[8])));
        }

        internal static void WriteDesired(ShipPhysics.ControlModel model, double ux, double uz, double uw,
            ref GuidanceCommand command)
        {
            if (model == null) return;
            var normalized = new ShipPhysics.Command
            {
                fx = (float)Clamp(ux, -1.0, 1.0),
                fz = (float)Clamp(uz, -1.0, 1.0),
                torque = (float)Clamp(uw, -1.0, 1.0)
            };
            command.normalizedX = normalized.fx;
            command.normalizedZ = normalized.fz;
            command.normalizedAngular = normalized.torque;
            model.DesiredWrench(normalized, out command.desiredLocalForce, out command.desiredTorque);
        }

        private static void SolveRegularized3x3(double[] b, double desiredX, double desiredY,
            double desiredAlpha,
            out double x0, out double x1, out double x2)
        {
            x0 = x1 = x2 = 0.0;
            if (b == null || b.Length < 9) return;
            const double regularization = 1e-4;
            double a = b[0] * b[0] + b[3] * b[3] + b[6] * b[6] + regularization;
            double d = b[1] * b[1] + b[4] * b[4] + b[7] * b[7] + regularization;
            double f = b[2] * b[2] + b[5] * b[5] + b[8] * b[8] + regularization;
            double bb = b[0] * b[1] + b[3] * b[4] + b[6] * b[7];
            double c = b[0] * b[2] + b[3] * b[5] + b[6] * b[8];
            double e = b[1] * b[2] + b[4] * b[5] + b[7] * b[8];
            double r0 = b[0] * desiredX + b[3] * desiredY + b[6] * desiredAlpha;
            double r1 = b[1] * desiredX + b[4] * desiredY + b[7] * desiredAlpha;
            double r2 = b[2] * desiredX + b[5] * desiredY + b[8] * desiredAlpha;
            double determinant = a * (d * f - e * e) - bb * (bb * f - c * e)
                + c * (bb * e - c * d);
            if (Math.Abs(determinant) < 1e-12 || double.IsNaN(determinant)) return;
            x0 = Clamp((r0 * (d * f - e * e) - bb * (r1 * f - e * r2)
                + c * (r1 * e - d * r2)) / determinant, -1.0, 1.0);
            x1 = Clamp((a * (r1 * f - e * r2) - r0 * (bb * f - c * e)
                + c * (bb * r2 - r1 * c)) / determinant, -1.0, 1.0);
            x2 = Clamp((a * (d * r2 - r1 * e) - bb * (bb * r2 - r1 * c)
                + r0 * (bb * e - d * c)) / determinant, -1.0, 1.0);
        }

        internal static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }

    /// <summary>六状态、三输入的在线线性 MPC；任务和游戏对象生命周期由 ShipAutopilot 管理。</summary>
    internal sealed class OnlineMpcGuidance : IAutopilotGuidance
    {
        private const int StateCount = 6;
        private const int InputCount = 3;
        private const double Infinite = 1e30;
        private static readonly double[] StateScale = { 10, 10, 5, 5, 10 * Math.PI / 180, 10 * Math.PI / 180 };
        private static readonly double[] StateWeight = { 4, 4, 2, 2, 4, 2 };

        private readonly DenseAdmmQpSolver solver = new DenseAdmmQpSolver();
        private AutopilotMpcProfile profile;
        private int horizonSteps;
        private int iterations;
        private double maxHorizon;
        private int variables;
        private int maxConstraints;
        private double[] p;
        private double[] q;
        private double[] a;
        private double[] lower;
        private double[] upper;
        private double[] solution;
        private double[] sensitivities;
        private double[] state;
        private double[] intervals;

        public void Reset(AutopilotMpcProfile selectedProfile)
        {
            profile = selectedProfile;
            if (profile == AutopilotMpcProfile.Fast)
            {
                horizonSteps = 16; iterations = 24; maxHorizon = 30.0;
            }
            else if (profile == AutopilotMpcProfile.Precise)
            {
                horizonSteps = 32; iterations = 64; maxHorizon = 60.0;
            }
            else
            {
                horizonSteps = 24; iterations = 40; maxHorizon = 45.0;
            }
            variables = horizonSteps * InputCount + 2;
            maxConstraints = horizonSteps * InputCount + horizonSteps * 2 + 2;
            p = new double[variables * variables];
            q = new double[variables];
            a = new double[maxConstraints * variables];
            lower = new double[maxConstraints];
            upper = new double[maxConstraints];
            solution = new double[variables];
            sensitivities = new double[StateCount * variables];
            state = new double[StateCount];
            intervals = new double[horizonSteps];
            solver.ResetDual();
        }

        public bool Compute(GuidanceRequest request, out GuidanceCommand command)
        {
            command = default;
            if (solution == null) Reset(AutopilotMpcProfile.Balanced);
            if (!ValidRequest(request))
            {
                BrakingGuidanceFallback.Compute(request, out command);
                return false;
            }

            long started = Stopwatch.GetTimestamp();
            Array.Clear(p, 0, p.Length);
            Array.Clear(q, 0, q.Length);
            Array.Clear(a, 0, a.Length);
            Array.Clear(sensitivities, 0, sensitivities.Length);

            state[0] = -request.positionError.x;
            state[1] = -request.positionError.y;
            state[2] = request.relativeVelocity.x;
            state[3] = request.relativeVelocity.y;
            state[4] = -request.angleError;
            state[5] = request.relativeAngularVelocity;
            BuildIntervals(request);
            ShiftWarmStart();

            double distance = Math.Sqrt(state[0] * state[0] + state[1] * state[1]);
            double nx = distance > 1e-6 ? state[0] / distance : 1.0;
            double ny = distance > 1e-6 ? state[1] / distance : 0.0;
            double angleSign = Math.Abs(state[4]) > 1e-6 ? Math.Sign(state[4]) : 1.0;
            int constraints = 0;

            // Input box constraints.
            int inputVariables = horizonSteps * InputCount;
            for (int i = 0; i < inputVariables; i++)
            {
                a[constraints * variables + i] = 1.0;
                lower[constraints] = -1.0;
                upper[constraints] = 1.0;
                constraints++;
            }
            int translationSlack = inputVariables;
            int angularSlack = inputVariables + 1;
            a[constraints * variables + translationSlack] = 1.0;
            lower[constraints] = 0.0; upper[constraints] = Infinite; constraints++;
            a[constraints * variables + angularSlack] = 1.0;
            lower[constraints] = 0.0; upper[constraints] = Infinite; constraints++;

            for (int step = 0; step < horizonSteps; step++)
            {
                AdvanceStateAndSensitivity(step, intervals[step], request.controlAcceleration);
                double weightScale = intervals[step];
                for (int s = 0; s < StateCount; s++)
                {
                    AddStateCost(s, StateWeight[s] * weightScale, StateScale[s]);
                }

                // Frozen line-of-sight soft approach envelope.
                int row = constraints++;
                for (int col = 0; col < inputVariables; col++)
                {
                    a[row * variables + col] = -nx * Sens(2, col) - ny * Sens(3, col)
                        - 0.2 * (nx * Sens(0, col) + ny * Sens(1, col));
                }
                a[row * variables + translationSlack] = -1.0;
                double constant = -nx * state[2] - ny * state[3]
                    - 0.2 * (nx * state[0] + ny * state[1]);
                lower[row] = -Infinite;
                upper[row] = 0.15 - constant;

                row = constraints++;
                for (int col = 0; col < inputVariables; col++)
                {
                    a[row * variables + col] = -angleSign * Sens(5, col)
                        - angleSign * Sens(4, col);
                }
                a[row * variables + angularSlack] = -1.0;
                constant = -angleSign * state[5] - angleSign * state[4];
                lower[row] = -Infinite;
                upper[row] = 0.03 - constant;
            }

            for (int s = 0; s < StateCount; s++) AddStateCost(s, StateWeight[s] * 20.0, StateScale[s]);
            AddControlCosts(inputVariables, request.previousX, request.previousZ, request.previousAngular);
            p[translationSlack * variables + translationSlack] += 20000.0;
            p[angularSlack * variables + angularSlack] += 20000.0;

            bool numerical = solver.Solve(variables, constraints, p, q, a, lower, upper,
                iterations, solution, out double primal, out double dual);
            command.solveMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            command.primalResidual = primal;
            command.dualResidual = dual;
            // 固定少迭代 MPC 的第一控制量即使尚未达到高精度 KKT 残差通常仍可用；
            // 最终指令在 WriteDesired 再做盒约束投影。阈值只排除明显发散，避免把软
            // 接近约束尚未完全吸收的残差误判成控制器故障。
            bool accepted = numerical && primal <= 100.0 && dual <= 1000.0
                && AllFinite(solution, variables);
            if (!accepted)
            {
                BrakingGuidanceFallback.Compute(request, out GuidanceCommand fallback);
                fallback.solveMilliseconds = command.solveMilliseconds;
                fallback.primalResidual = primal;
                fallback.dualResidual = dual;
                command = fallback;
                return false;
            }

            BrakingGuidanceFallback.WriteDesired(request.controlModel,
                solution[0], solution[1], solution[2], ref command);
            command.solved = true;
            return true;
        }

        private void BuildIntervals(GuidanceRequest request)
        {
            double speed = request.relativeVelocity.magnitude;
            double accel = 0.0;
            for (int c = 0; c < 3; c++)
            {
                double x = request.controlAcceleration[c];
                double y = request.controlAcceleration[3 + c];
                accel = Math.Max(accel, Math.Sqrt(x * x + y * y));
            }
            double distance = request.positionError.magnitude;
            double stopTime = accel > 1e-6 ? speed / accel : maxHorizon;
            double travelTime = accel > 1e-6 ? Math.Sqrt(2.0 * distance / accel) : maxHorizon;
            double horizon = Math.Max(6.0, Math.Min(maxHorizon, 1.25 * Math.Max(stopTime, travelTime) + 3.0));
            int fine = Math.Min(8, horizonSteps);
            for (int i = 0; i < fine; i++) intervals[i] = request.dt;
            double remainder = Math.Max(request.dt, horizon - fine * request.dt);
            double coarse = horizonSteps > fine ? remainder / (horizonSteps - fine) : request.dt;
            for (int i = fine; i < horizonSteps; i++) intervals[i] = coarse;
        }

        private void AdvanceStateAndSensitivity(int step, double dt, double[] b)
        {
            int baseControl = step * InputCount;
            double halfDt2 = 0.5 * dt * dt;
            // 被动阻力（与 ShipPhysics.Integrate 同一定义/参数，§11.1：预测与实际积分
            // 同口径，避免「预测无阻力、实际有阻力」的持续误差）。顺序与积分器一致：
            // 速度先衰减（含输入增量），位置再按衰减后速度推进（半隐式）。
            (float linearDecay, float angularDecay) = ShipPhysics.PassiveDragFactors((float)dt);
            double dl = linearDecay;
            double da = angularDecay;
            state[2] *= dl;
            state[3] *= dl;
            state[5] *= da;
            for (int col = 0; col < variables; col++)
            {
                SetSens(2, col, Sens(2, col) * dl);
                SetSens(3, col, Sens(3, col) * dl);
                SetSens(5, col, Sens(5, col) * da);
            }
            state[0] += state[2] * dt;
            state[1] += state[3] * dt;
            state[4] += state[5] * dt;
            for (int col = 0; col < variables; col++)
            {
                SetSens(0, col, Sens(0, col) + Sens(2, col) * dt);
                SetSens(1, col, Sens(1, col) + Sens(3, col) * dt);
                SetSens(4, col, Sens(4, col) + Sens(5, col) * dt);
            }
            for (int c = 0; c < InputCount; c++)
            {
                int col = baseControl + c;
                SetSens(0, col, Sens(0, col) + b[c] * halfDt2 * dl);
                SetSens(1, col, Sens(1, col) + b[3 + c] * halfDt2 * dl);
                SetSens(2, col, Sens(2, col) + b[c] * dt * dl);
                SetSens(3, col, Sens(3, col) + b[3 + c] * dt * dl);
                SetSens(4, col, Sens(4, col) + b[6 + c] * halfDt2 * da);
                SetSens(5, col, Sens(5, col) + b[6 + c] * dt * da);
            }
        }

        private void AddStateCost(int stateIndex, double weight, double scale)
        {
            double normalizedWeight = weight / (scale * scale);
            double offset = state[stateIndex];
            for (int i = 0; i < variables; i++)
            {
                double si = Sens(stateIndex, i);
                if (si == 0.0) continue;
                q[i] += 2.0 * normalizedWeight * offset * si;
                int row = i * variables;
                for (int j = 0; j < variables; j++)
                {
                    double sj = Sens(stateIndex, j);
                    if (sj != 0.0) p[row + j] += 2.0 * normalizedWeight * si * sj;
                }
            }
        }

        private void AddControlCosts(int inputVariables, double previousX, double previousZ, double previousAngular)
        {
            const double inputWeight = 0.15;
            const double deltaWeight = 0.75;
            for (int i = 0; i < inputVariables; i++) p[i * variables + i] += 2.0 * inputWeight;
            for (int step = 0; step < horizonSteps; step++)
            {
                for (int axis = 0; axis < InputCount; axis++)
                {
                    int current = step * InputCount + axis;
                    p[current * variables + current] += 2.0 * deltaWeight;
                    if (step == 0)
                    {
                        double previousValue = axis == 0 ? previousX : (axis == 1 ? previousZ : previousAngular);
                        q[current] -= 2.0 * deltaWeight * previousValue;
                        continue;
                    }
                    int previousIndex = current - InputCount;
                    p[previousIndex * variables + previousIndex] += 2.0 * deltaWeight;
                    p[current * variables + previousIndex] -= 2.0 * deltaWeight;
                    p[previousIndex * variables + current] -= 2.0 * deltaWeight;
                }
            }
        }

        private void ShiftWarmStart()
        {
            int inputVariables = horizonSteps * InputCount;
            for (int i = 0; i < inputVariables - InputCount; i++) solution[i] = solution[i + InputCount];
            for (int i = inputVariables - InputCount; i < inputVariables; i++) solution[i] = 0.0;
        }

        private double Sens(int stateIndex, int variable)
        {
            return sensitivities[stateIndex * variables + variable];
        }

        private void SetSens(int stateIndex, int variable, double value)
        {
            sensitivities[stateIndex * variables + variable] = value;
        }

        private static bool ValidRequest(GuidanceRequest request)
        {
            return request.controlModel != null && request.controlAcceleration != null
                && request.controlAcceleration.Length >= 9 && request.dt > 0f
                && IsFinite(request.positionError.x) && IsFinite(request.positionError.y)
                && IsFinite(request.relativeVelocity.x) && IsFinite(request.relativeVelocity.y)
                && IsFinite(request.angleError) && IsFinite(request.relativeAngularVelocity);
        }

        private static bool AllFinite(double[] values, int count)
        {
            for (int i = 0; i < count; i++) if (!IsFinite(values[i])) return false;
            return true;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
