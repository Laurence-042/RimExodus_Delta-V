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
        private static readonly int[] periodCounts = new int[3];
        private static double totalMilliseconds;
        private static double maximumMilliseconds;

        public static void Record(GuidanceCommand command, int solvePeriod = 1)
        {
            solveCount++;
            periodCounts[solvePeriod == 4 ? 2 : solvePeriod == 2 ? 1 : 0]++;
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
            return $"profile={profile}; solves={solveCount} (precision={periodCounts[0]}, braking={periodCounts[1]}, cruise={periodCounts[2]}); fallback={fallbackCount}; "
                + $"avg={average:F3}ms; p95(last {sampleCount})={p95:F3}ms; max={maximumMilliseconds:F3}ms";
        }

        public static string RunSelfTest()
        {
            double[] accelerations = { 0.5, 1.0, 2.2 };
            var result = new System.Text.StringBuilder("[DeltaV:MPC] deterministic self-test");
            bool allPassed = true;
            foreach (double acceleration in accelerations)
            {
                // K=1：每微步求解（终段紧反馈语义）；K=4：每 4 微步求解 + 计划回放（远场降频语义）。
                foreach (int playbackSteps in new[] { 1, 4 })
                {
                    bool passed = RunOne(acceleration, playbackSteps, 1.0, 0.0, out double finalPosition,
                        out double finalSpeed, out int crossings, out int fallbackUses,
                        out double maxPrimal, out double maxDual);
                    allPassed &= passed;
                    result.Append($"\n- a={acceleration:F1} K={playbackSteps}: {(passed ? "PASS" : "FAIL")}; "
                        + $"p={finalPosition:F3}; v={finalSpeed:F3}; crossings={crossings}; fallback={fallbackUses}; "
                        + $"maxResidual=({maxPrimal:F3},{maxDual:F3})");
                }
            }
            // 非对称反推、不同质量/惯量、移动目标：每步执行真实 Allocate。
            bool asymmetric = RunOne(0.5, 4, 0.55, 1.0, out double asymmetricPosition,
                out double asymmetricSpeed, out int asymmetricCrossings, out int asymmetricFallback,
                out _, out _);
            allPassed &= asymmetric;
            result.Append($"\n- asymmetric moving target: {(asymmetric ? "PASS" : "FAIL")}; "
                + $"p={asymmetricPosition:F3}; v={asymmetricSpeed:F3}; crossings={asymmetricCrossings}; "
                + $"fallback={asymmetricFallback}");
            double noDragStop = BrakingGuidanceFallback.StoppingDistance(20.0, 0.5, 1.0);
            double mildDragStop = BrakingGuidanceFallback.StoppingDistance(20.0, 0.5, Math.Pow(0.5, 1.0 / 8.0));
            double strongDragStop = BrakingGuidanceFallback.StoppingDistance(20.0, 0.5, 0.5);
            bool dragPassed = noDragStop > mildDragStop && mildDragStop > strongDragStop
                && Math.Abs(noDragStop - 400.0) < 1e-6;
            allPassed &= dragPassed;
            result.Append($"\n- drag stopping distance: {(dragPassed ? "PASS" : "FAIL")}; "
                + $"none={noDragStop:F1} mild={mildDragStop:F1} strong={strongDragStop:F1}");
            var damaged = new ShipPhysics.MassReport { mass = 3f, inertia = 12f };
            var reverseThruster = new ShipPhysics.Thruster { dir = Vector2.left,
                thrust = 3f, operational = true };
            damaged.thrusters.Add(reverseThruster);
            float available = new DriveInfo(damaged).PreviewBraking(Vector2.right);
            reverseThruster.operational = false;
            float disabled = new DriveInfo(damaged).PreviewBraking(Vector2.right);
            bool damagePassed = available > 0.5f && disabled == 0f;
            allPassed &= damagePassed;
            result.Append($"\n- failed reverse thruster: {(damagePassed ? "PASS" : "FAIL")}; "
                + $"available={available:F3} disabled={disabled:F3}");
            bool headingPassed = RunHeadingCheck(out float leftTorque,
                out float rightTorque, out float brakeTorque);
            allPassed &= headingPassed;
            result.Append($"\n- fixed heading/rotation sign: {(headingPassed ? "PASS" : "FAIL")}; "
                + $"left={leftTorque:F3} right={rightTorque:F3} brake={brakeTorque:F3}");
            result.Append($"\n- overall: {(allPassed ? "PASS" : "FAIL")}");
            return result.ToString();
        }

        private static bool RunHeadingCheck(out float leftTorque, out float rightTorque,
            out float brakeTorque)
        {
            var report = new ShipPhysics.MassReport { mass = 3f, inertia = 10f };
            report.thrusters.Add(new ShipPhysics.Thruster { localPos = new Vector2(0f, -1f),
                dir = Vector2.right, thrust = 5f, operational = true });
            report.thrusters.Add(new ShipPhysics.Thruster { localPos = new Vector2(0f, 1f),
                dir = Vector2.left, thrust = 5f, operational = true });
            report.thrusters.Add(new ShipPhysics.Thruster { localPos = new Vector2(0f, -1f),
                dir = Vector2.left, thrust = 5f, operational = true });
            report.thrusters.Add(new ShipPhysics.Thruster { localPos = new Vector2(0f, 1f),
                dir = Vector2.right, thrust = 5f, operational = true });
            var capabilities = new DriveInfo(report);
            ShipPhysics.ControlModel model = capabilities.PreviewModel();
            double[] acceleration = new double[9];
            for (int axis = 0; axis < 3; axis++)
            {
                acceleration[axis] = model.forceColumns[axis].x / report.mass;
                acceleration[3 + axis] = model.forceColumns[axis].y / report.mass;
                acceleration[6 + axis] = model.torqueColumns[axis] / report.inertia;
            }
            leftTorque = HeadingTorque(capabilities, model, acceleration, 0.5f, 0f);
            rightTorque = HeadingTorque(capabilities, model, acceleration, -0.5f, 0f);
            brakeTorque = HeadingTorque(capabilities, model, acceleration, 0f, 0.25f);
            return leftTorque > 0.001f && rightTorque < -0.001f
                && brakeTorque < -0.001f;
        }

        private static float HeadingTorque(DriveInfo capabilities,
            ShipPhysics.ControlModel model, double[] acceleration, float angleError,
            float relativeOmega)
        {
            IAutopilotGuidance guidance = new OnlineMpcGuidance();
            guidance.Reset(AutopilotMpcProfile.Balanced);
            guidance.Compute(new GuidanceRequest
            {
                angleError = angleError,
                relativeAngularVelocity = relativeOmega,
                dt = 0.1f,
                controlModel = model,
                controlAcceleration = acceleration,
                trajectorySeconds = 3f
            }, out GuidanceCommand command);
            capabilities.Preview(command.desiredLocalForce, command.desiredTorque, out _, out float torque);
            return torque;
        }

        private static bool RunOne(double acceleration, int playbackSteps, double reverseFactor,
            double hostVelocity, out double finalPosition,
            out double finalSpeed, out int crossings, out int fallbackUses, out double maxPrimal, out double maxDual)
        {
            var report = new ShipPhysics.MassReport { mass = 3f, inertia = 12f };
            report.thrusters.Add(new ShipPhysics.Thruster { dir = Vector2.right,
                localPos = Vector2.zero, thrust = (float)(report.mass * acceleration), operational = true });
            report.thrusters.Add(new ShipPhysics.Thruster { dir = Vector2.left,
                localPos = Vector2.zero, thrust = (float)(report.mass * acceleration * reverseFactor), operational = true });
            report.thrusters.Add(new ShipPhysics.Thruster { dir = Vector2.up,
                localPos = Vector2.zero, thrust = (float)(report.mass * acceleration), operational = true });
            report.thrusters.Add(new ShipPhysics.Thruster { dir = Vector2.down,
                localPos = Vector2.zero, thrust = (float)(report.mass * acceleration), operational = true });
            var capabilities = new DriveInfo(report);
            ShipPhysics.ControlModel model = capabilities.PreviewModel();
            double[] b =
            {
                model.forceColumns[0].x / report.mass, model.forceColumns[1].x / report.mass, model.forceColumns[2].x / report.mass,
                model.forceColumns[0].y / report.mass, model.forceColumns[1].y / report.mass, model.forceColumns[2].y / report.mass,
                model.torqueColumns[0] / report.inertia, model.torqueColumns[1] / report.inertia, model.torqueColumns[2] / report.inertia
            };
            IAutopilotGuidance guidance = new OnlineMpcGuidance();
            guidance.Reset(AutopilotMpcProfile.Balanced);
            double position = -Math.Max(120.0, 20.0 * 20.0 / (2.0 * acceleration * reverseFactor) + 20.0);
            double velocity = 20.0 + hostVelocity;
            double targetPosition = 0.0;
            double previousPosition = position;
            crossings = 0;
            fallbackUses = 0;
            maxPrimal = 0.0;
            maxDual = 0.0;
            for (int step = 0; step < 1200; step++)
            {
                GuidanceCommand command;
                if (step % playbackSteps == 0)
                {
                    var request = new GuidanceRequest
                    {
                        positionError = new Vector2((float)(targetPosition - position), 0f),
                        relativeVelocity = new Vector2((float)(velocity - hostVelocity), 0f),
                        referenceVelocity = new Vector2((float)hostVelocity, 0f),
                        dt = 0.1f,
                        elapsedSteps = playbackSteps,
                        controlModel = model,
                        controlAcceleration = b,
                        brakingAcceleration = capabilities.PreviewBraking(targetPosition >= position ? Vector2.right : Vector2.left)
                    };
                    guidance.Compute(request, out command);
                }
                else if (!guidance.TryGetPlannedStep(step % playbackSteps, model, out command))
                {
                    // 计划不可用（数值回退）：与 ShipAutopilot 同语义，立即重解恢复反馈。
                    var request = new GuidanceRequest
                    {
                        positionError = new Vector2((float)(targetPosition - position), 0f),
                        relativeVelocity = new Vector2((float)(velocity - hostVelocity), 0f),
                        referenceVelocity = new Vector2((float)hostVelocity, 0f),
                        dt = 0.1f,
                        elapsedSteps = 1,
                        controlModel = model,
                        controlAcceleration = b,
                        brakingAcceleration = capabilities.PreviewBraking(targetPosition >= position ? Vector2.right : Vector2.left)
                    };
                    guidance.Compute(request, out command);
                }
                if (command.usedFallback) fallbackUses++;
                maxPrimal = Math.Max(maxPrimal, command.primalResidual);
                maxDual = Math.Max(maxDual, command.dualResidual);
                capabilities.Preview(command.desiredLocalForce, command.desiredTorque, out Vector2 actualForce, out _);
                double appliedAcceleration = actualForce.x / report.mass;
                // 被动阻力同口径进入自测被控对象（预测模型一致性的确定性验证）。
                (float linearDecay, _) = ShipPhysics.PassiveDragFactors(1f / 60f);
                for (int tick = 0; tick < 6; tick++)
                {
                    velocity = (velocity + appliedAcceleration / 60.0) * linearDecay;
                    position += velocity / 60.0;
                    targetPosition += hostVelocity / 60.0;
                }
                double relativePosition = position - targetPosition;
                if (Math.Sign(previousPosition) != Math.Sign(relativePosition)
                    && Math.Abs(previousPosition) > 0.25 && Math.Abs(relativePosition) > 0.25) crossings++;
                previousPosition = relativePosition;
            }
            finalPosition = position - targetPosition;
            finalSpeed = velocity - hostVelocity;
            return Math.Abs(finalPosition) < 0.5 && Math.Abs(finalSpeed) < 0.2 && crossings <= 1;
        }
    }

    internal enum GuidanceAccuracy { Strict, Braking, Cruise }

    internal struct GuidanceRequest
    {
        // positionError = target - ship；relativeVelocity = ship - target。
        public Vector2 positionError;
        // Disable position costs AND approach constraints; a zero position error alone
        // still makes MPC brake and fly back to its predicted starting point.
        public bool motionOnly;
        public bool intercept;
        public GuidanceAccuracy accuracy;
        public float boostSeconds;
        public ShipPhysics.ControlModel unboostedModel;
        public double[] unboostedAcceleration;
        internal double arrivalSeconds;
        public Vector2 relativeVelocity;
        public float angleError;
        public float relativeAngularVelocity;
        public float dt;
        public float voidBlend;
        // 自上次求解以来已消费的微步数（计划回放步数）：热启动平移量按此对齐；
        // ≤0 视为 1（每次求解都立即应用首控制量的旧语义）。
        public int elapsedSteps;
        public double previousX;
        public double previousZ;
        public double previousAngular;
        public ShipPhysics.ControlModel controlModel;
        // 归一化 [fx,fz,torque] → 目标切基 [ax,ay,alpha]，row-major 3x3。
        public double[] controlAcceleration;
        // 真实分配器沿目标接近方向预览的反推加速度；包络和回退共用。
        public double brakingAcceleration;
        // 战斗轨迹每个预测步由同一球面曲线采样；普通任务保持零。
        public float trajectorySeconds;
        public CombatTrajectoryIntent combatTrajectory;
        public float referenceStartSeconds;
        public Vector3 referencePosition;
        public Vector2 referenceVelocity;
        public float referenceAngle;
        public float referenceAngularVelocity;
        // 制动阶段在下一次反馈前可能消耗的闭合距离，直接从速度与求解周期派生。
        public double approachReserveDistance;
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
        public double arrivalSeconds;
        public int qpCalls;
        public int qpIterations;
        public int qpIterationLimit;
        public int qpConstraints;
        public QpSolveStatus qpStatus;
        public double relativeOptimalityGap;
    }

    internal interface IAutopilotGuidance
    {
        void Reset(AutopilotMpcProfile profile);

        bool Compute(GuidanceRequest request, out GuidanceCommand command);

        /// <summary>读取上一次成功解的计划步（stepIndex ≥ 1 的后续控制）用于微步间回放；
        /// 无有效计划（数值回退/未求解/越界）返回 false，调用方应立即重解。</summary>
        bool TryGetPlannedStep(int stepIndex, ShipPhysics.ControlModel model, out GuidanceCommand command);
    }

    /// <summary>求解异常时的停止距离制导；不会退回旧饱和 PD。</summary>
    internal static class BrakingGuidanceFallback
    {
        /// <summary>安全加速度系数：回退制导与 MPC 软包络共用同一标量（单一实现）——
        /// 覆盖离散化误差与分配器真实出力相对控制模型上限的缺口。</summary>
        internal const double SafetyFactor = 0.8;

        public static void Compute(GuidanceRequest request, out GuidanceCommand command)
        {
            command = default;
            Vector2 p = -request.positionError; // ship - target
            Vector2 v = request.relativeVelocity;
            double safeAccel = SafeTranslationAcceleration(request);
            double safeAlpha = SafeAngularAcceleration(request.controlAcceleration);
            double desiredX = 0.0;
            double desiredY = 0.0;
            double desiredAlpha = 0.0;

            double distance = Math.Max(0.0, p.magnitude - request.approachReserveDistance);
            if (safeAccel > 1e-6 && (request.motionOnly || distance > 1e-6))
            {
                Vector2 toward = -p.normalized;
                double speedLimit = Math.Sqrt(2.0 * safeAccel * distance);
                Vector2 targetVelocity = request.motionOnly ? Vector2.zero : toward * (float)speedLimit;
                if (request.intercept)
                    targetVelocity = v + (request.positionError - v * 2f) * 0.5f;
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

        /// <summary>包络与回退共用的安全平移加速度（cells/s²）：当前接近方向的真实反推预览 × 安全系数。</summary>
        internal static double SafeTranslationAcceleration(GuidanceRequest request)
        {
            return Math.Max(0.0, request.brakingAcceleration) * SafetyFactor;
        }

        // 相对闭合速度在当前阻力下的连续停止距离；零阻力时退化为 v²/(2a)。
        internal static double StoppingDistance(double closingSpeed, double acceleration)
        {
            (float decay, _) = ShipPhysics.PassiveDragFactors(1f);
            return StoppingDistance(closingSpeed, acceleration, decay);
        }

        internal static double StoppingDistance(double closingSpeed, double acceleration,
            double linearDecayPerSecond)
        {
            if (closingSpeed <= 0.0) return 0.0;
            double drag = linearDecayPerSecond > 0.0 ? -Math.Log(linearDecayPerSecond) : 0.0;
            if (drag < 1e-6) return acceleration > 1e-6
                ? closingSpeed * closingSpeed / (2.0 * acceleration) : double.PositiveInfinity;
            if (acceleration <= 1e-6) return closingSpeed / drag;
            double ratio = drag * closingSpeed / acceleration;
            return acceleration / (drag * drag) * (ratio - Math.Log(1.0 + ratio));
        }

        /// <summary>包络与回退共用的安全角加速度（rad/s²）。</summary>
        internal static double SafeAngularAcceleration(double[] b)
        {
            return MaxAngularAcceleration(b) * SafetyFactor;
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
            model.ControlBounds(0, out double lx, out double hx);
            model.ControlBounds(1, out double lz, out double hz);
            model.ControlBounds(2, out double lw, out double hw);
            var normalized = new Vector3((float)Clamp(ux, lx, hx),
                (float)Clamp(uz, lz, hz), (float)Clamp(uw, lw, hw));
            command.normalizedX = normalized.x;
            command.normalizedZ = normalized.y;
            command.normalizedAngular = normalized.z;
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

        private readonly DenseActiveSetQpSolver solver = new DenseActiveSetQpSolver();
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
        private double[] constraintTolerances;
        private double[] solution;
        private double[] sensitivities;
        private double[] state;
        private double[] intervals;
        private bool hasPlannedSolution;
        private double[] previousIntervals;
        private double[] previousSolution;
        private double[] interceptSolution, interceptIntervals;
        private double lastInterceptHorizon;
        private double plannedStepSeconds;

        public void Reset(AutopilotMpcProfile selectedProfile)
        {
            profile = selectedProfile;
            if (profile == AutopilotMpcProfile.Fast)
            {
                horizonSteps = 16; iterations = 128; maxHorizon = 30.0;
            }
            else if (profile == AutopilotMpcProfile.Precise)
            {
                horizonSteps = 32; iterations = 768; maxHorizon = 60.0;
            }
            else
            {
                horizonSteps = 24; iterations = 512; maxHorizon = 45.0;
            }
            variables = horizonSteps * InputCount;
            maxConstraints = horizonSteps * (InputCount + 6);
            p = new double[variables * variables];
            q = new double[variables];
            a = new double[maxConstraints * variables];
            lower = new double[maxConstraints];
            upper = new double[maxConstraints];
            constraintTolerances = new double[maxConstraints];
            solution = new double[variables];
            sensitivities = new double[StateCount * variables];
            state = new double[StateCount];
            intervals = new double[horizonSteps];
            previousIntervals = new double[horizonSteps];
            previousSolution = new double[variables];
            interceptSolution = new double[variables];
            interceptIntervals = new double[horizonSteps];
            lastInterceptHorizon = 0;
            plannedStepSeconds = 0;
            hasPlannedSolution = false;
        }

        public bool TryGetPlannedStep(int stepIndex, ShipPhysics.ControlModel model, out GuidanceCommand command)
        {
            command = default;
            if (!hasPlannedSolution || stepIndex < 0 || stepIndex >= horizonSteps)
            {
                return false;
            }
            // 回放按固定微步消费，不能把短终段或加力切换后的非均匀网格当成 0.1 s。
            for (int i = 0; i <= stepIndex; i++)
                if (Math.Abs(intervals[i] - plannedStepSeconds) > 1e-6) return false;
            command.solved = true;
            int input = stepIndex * InputCount;
            BrakingGuidanceFallback.WriteDesired(model,
                solution[input], solution[input + 1], solution[input + 2], ref command);
            return true;
        }

        public bool Compute(GuidanceRequest request, out GuidanceCommand command)
        {
            if (solution == null) Reset(AutopilotMpcProfile.Balanced);
            if (!request.intercept || !ValidRequest(request)) return ComputePlan(request, out command);
            // 有界时间搜索：同一 QP 在候选到达时刻求解，允许非零接触速度。
            // 端点误差决定是否可达；有限视域尚不可达时保留最远时域的接近计划。
            long started = Stopwatch.GetTimestamp();
            double acceleration = 0.01;
            for (int c = 0; c < 3; c++) acceleration = Math.Max(acceleration,
                Math.Sqrt(request.controlAcceleration[c] * request.controlAcceleration[c]
                    + request.controlAcceleration[3 + c] * request.controlAcceleration[3 + c]));
            double candidate = lastInterceptHorizon > 0
                ? Math.Max(request.dt, lastInterceptHorizon - Math.Max(1, request.elapsedSteps) * request.dt)
                : Math.Sqrt(2 * request.positionError.magnitude / acceleration) * 1.5 + 1;
            candidate = Math.Max(request.dt, Math.Min(maxHorizon, candidate));
            double low = request.dt, high = maxHorizon;
            bool found = false, havePlan = false;
            int qpCalls = 0, qpIterations = 0, qpIterationLimit = 0, qpConstraints = 0;
            command = default;
            GuidanceCommand lastAttempt = default;
            for (int trial = 0; trial < 4; trial++)
            {
                request.arrivalSeconds = candidate;
                hasPlannedSolution = false;
                bool solved = ComputePlan(request, out GuidanceCommand attempt);
                lastAttempt = attempt;
                qpCalls += attempt.qpCalls;
                qpIterations += attempt.qpIterations;
                qpIterationLimit += attempt.qpIterationLimit;
                qpConstraints = attempt.qpConstraints;
                double px = PredictedState(0), py = PredictedState(1);
                bool reachable = solved && px * px + py * py <= 0.75 * 0.75
                    && Math.Abs(PredictedState(4)) <= 15 * Math.PI / 180;
                if (solved && (reachable || !found))
                {
                    havePlan = true;
                    command = attempt;
                    command.arrivalSeconds = candidate;
                    Array.Copy(solution, interceptSolution, variables);
                    Array.Copy(intervals, interceptIntervals, horizonSteps);
                }
                if (reachable) { found = true; high = candidate; }
                else low = candidate;
                if (high - low < request.dt) break;
                candidate = found ? (low + high) * 0.5 : Math.Min(maxHorizon, candidate * 1.8);
            }
            if (havePlan)
            {
                Array.Copy(interceptSolution, solution, variables);
                Array.Copy(interceptIntervals, intervals, horizonSteps);
                hasPlannedSolution = true;
                lastInterceptHorizon = command.arrivalSeconds;
            }
            else
            {
                BrakingGuidanceFallback.Compute(request, out command);
                command.primalResidual = lastAttempt.primalResidual;
                command.dualResidual = lastAttempt.dualResidual;
                command.qpStatus = lastAttempt.qpStatus;
                command.relativeOptimalityGap = lastAttempt.relativeOptimalityGap;
            }
            command.solveMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            command.qpCalls = qpCalls;
            command.qpIterations = qpIterations;
            command.qpIterationLimit = qpIterationLimit;
            command.qpConstraints = qpConstraints;
            return havePlan;
        }

        private double PredictedState(int index)
        {
            double value = state[index];
            for (int i = 0; i < variables; i++) value += Sens(index, i) * solution[i];
            return value;
        }

        private bool ComputePlan(GuidanceRequest request, out GuidanceCommand command)
        {
            command = default;
            bool warmStart = hasPlannedSolution;
            hasPlannedSolution = false;
            if (solution == null) Reset(AutopilotMpcProfile.Balanced);
            if (!ValidRequest(request))
            {
                BrakingGuidanceFallback.Compute(request, out command);
                command.qpStatus = QpSolveStatus.InvalidInput;
                return false;
            }

            long started = Stopwatch.GetTimestamp();
            Array.Clear(p, 0, p.Length);
            Array.Clear(q, 0, q.Length);
            Array.Clear(a, 0, a.Length);
            Array.Clear(sensitivities, 0, sensitivities.Length);

            state[0] = request.motionOnly ? 0.0 : -request.positionError.x;
            state[1] = request.motionOnly ? 0.0 : -request.positionError.y;
            state[2] = request.relativeVelocity.x;
            state[3] = request.relativeVelocity.y;
            state[4] = -request.angleError;
            state[5] = request.relativeAngularVelocity;
            plannedStepSeconds = request.dt;
            Array.Copy(intervals, previousIntervals, horizonSteps);
            BuildIntervals(request);
            if (warmStart) ShiftWarmStart(Math.Max(1, request.elapsedSteps) * request.dt);
            else Array.Clear(solution, 0, solution.Length);

            double distance = Math.Sqrt(state[0] * state[0] + state[1] * state[1]);
            double nx = distance > 1e-6 ? state[0] / distance : 1.0;
            double ny = distance > 1e-6 ? state[1] / distance : 0.0;
            // 包络可用加速度与停止距离回退共用同一安全标量（单一实现）。
            double safeAccel = BrakingGuidanceFallback.SafeTranslationAcceleration(request);
            int constraints = 0;
            bool combatTrajectory = request.combatTrajectory.seconds > 0f;
            double predictionTime = 0.0;
            double speedCeiling = Math.Max(1.0, Math.Sqrt(request.relativeVelocity.sqrMagnitude + 2.0 * safeAccel * distance));

            // Input box constraints.
            int inputVariables = horizonSteps * InputCount;
            for (int i = 0; i < inputVariables; i++)
            {
                a[constraints * variables + i] = 1.0;
                constraintTolerances[constraints] = 1e-5;
                bool unboosted = UsesUnboosted(request, i / InputCount);
                (unboosted ? request.unboostedModel : request.controlModel).ControlBounds(i % InputCount,
                    out lower[constraints], out upper[constraints]);
                if (unboosted)
                {
                    double ratio = ControlUnitRatio(request, i % InputCount);
                    lower[constraints] /= ratio;
                    upper[constraints] /= ratio;
                }
                constraints++;
            }
            for (int step = 0; step < horizonSteps; step++)
            {
                AdvanceStateAndSensitivity(step, intervals[step], request);
                predictionTime += intervals[step];
                Vector2 desiredPosition = Vector2.zero;
                Vector2 desiredVelocity = Vector2.zero;
                float desiredAngle = 0f;
                float desiredOmega = 0f;
                if (combatTrajectory)
                    CombatReferenceOffsets(request, (float)predictionTime,
                        out desiredPosition, out desiredVelocity,
                        out desiredAngle, out desiredOmega);
                double weightScale = intervals[step];
                for (int s = 0; s < StateCount; s++)
                {
                    if (request.motionOnly && s < 2) continue;
                    if (request.intercept && s < 4) continue;
                    double target = s switch
                    {
                        0 => desiredPosition.x, 1 => desiredPosition.y,
                        2 => desiredVelocity.x, 3 => desiredVelocity.y,
                        4 => desiredAngle, _ => desiredOmega
                    };
                    double headingPriority = combatTrajectory && s >= 4 ? 4.0 : 1.0;
                    AddStateCost(s, StateWeight[s] * weightScale * headingPriority,
                        StateScale[s], -target);
                }
                // v_close²/(2a) <= d 的保守分段线性内近似：凸平方函数在
                // [vi,vi+1] 上的弦位于曲线上方，所有弦的上包络覆盖 [0,vMax]。
                // 相比 sqrt 的切线外近似，这组线性约束不会放行刹不住的计划。
                if (!request.intercept && !request.motionOnly && !combatTrajectory && distance > 0.05 && safeAccel > 1e-6)
                {
                    double d0 = nx * state[0] + ny * state[1];
                    double v0 = -nx * state[2] - ny * state[3];
                    // 前四行是停止距离弦；第 5 行限制越过目标平面，第 6 行界定弦的有效速度域。
                    for (int segment = 0; segment < 6; segment++)
                    {
                        double lowSpeed = speedCeiling * segment / 4.0;
                        double highSpeed = speedCeiling * (segment + 1) / 4.0;
                        double slope = segment < 4 ? (lowSpeed + highSpeed) / (2.0 * safeAccel) : 0.0;
                        double intercept = segment < 4 ? lowSpeed * highSpeed / (2.0 * safeAccel) : 0.05;
                        int row = constraints++;
                        // Original QP rows divide by StateScale[0]: enforce 0.001 cells
                        // for stop/plane constraints, 0.0001 cells/s for the speed ceiling.
                        constraintTolerances[row] = (segment == 5 ? 1e-4 : 1e-3) / StateScale[0];
                        for (int col = 0; col < inputVariables; col++)
                        {
                            double dp = nx * Sens(0, col) + ny * Sens(1, col);
                            double dv = -nx * Sens(2, col) - ny * Sens(3, col);
                            a[row * variables + col] = (segment == 5 ? dv : slope * dv - dp) / StateScale[0];
                        }
                        lower[row] = -Infinite;
                        upper[row] = (segment == 5 ? speedCeiling - v0 : intercept - slope * v0 + d0) / StateScale[0];
                    }
                }
            }
            Vector2 terminalPosition = Vector2.zero;
            Vector2 terminalVelocity = Vector2.zero;
            float terminalAngle = 0f;
            float terminalOmega = 0f;
            if (combatTrajectory)
                CombatReferenceOffsets(request, (float)predictionTime,
                    out terminalPosition, out terminalVelocity,
                    out terminalAngle, out terminalOmega);
            for (int s = 0; s < StateCount; s++)
            {
                if (request.motionOnly && s < 2) continue;
                if (request.intercept && (s == 2 || s == 3)) continue;
                double terminalOffset = s switch
                {
                    0 => -terminalPosition.x, 1 => -terminalPosition.y,
                    2 => -terminalVelocity.x, 3 => -terminalVelocity.y,
                    4 => -terminalAngle, _ => -terminalOmega
                };
                double headingPriority = combatTrajectory && s >= 4 ? 4.0 : 1.0;
                AddStateCost(s, StateWeight[s] * (request.intercept && s < 2 ? 2500.0 : 20.0) * headingPriority,
                    StateScale[s], terminalOffset);
            }
            AddControlCosts(inputVariables, request.previousX, request.previousZ, request.previousAngular);

            bool numerical = solver.Solve(variables, constraints, p, q, a, lower, upper,
                iterations, solution, out double primal, out double dual,
                OptimalityTolerance(request), constraintTolerances);
            command.solveMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            command.primalResidual = primal;
            command.dualResidual = dual;
            command.qpCalls = 1;
            command.qpIterations = solver.IterationsUsed;
            command.qpIterationLimit = iterations;
            command.qpConstraints = constraints;
            command.qpStatus = solver.Status;
            command.relativeOptimalityGap = solver.RelativeGap;
            // Every accepted plan is feasible in original units. Far-field plans may
            // terminate on a certified objective gap instead of strict stationarity.
            bool accepted = numerical
                && AllFinite(solution, variables);
            if (!accepted)
            {
                GuidanceRequest fallbackRequest = request;
                if (combatTrajectory)
                {
                    fallbackRequest.positionError += terminalPosition;
                    fallbackRequest.relativeVelocity -= terminalVelocity;
                }
                BrakingGuidanceFallback.Compute(fallbackRequest, out GuidanceCommand fallback);
                fallback.solveMilliseconds = command.solveMilliseconds;
                fallback.primalResidual = primal;
                fallback.dualResidual = dual;
                fallback.qpCalls = command.qpCalls;
                fallback.qpIterations = command.qpIterations;
                fallback.qpIterationLimit = command.qpIterationLimit;
                fallback.qpConstraints = command.qpConstraints;
                fallback.qpStatus = command.qpStatus;
                fallback.relativeOptimalityGap = command.relativeOptimalityGap;
                command = fallback;
                return false;
            }

            hasPlannedSolution = true; // 可回放：微步间经 TryGetPlannedStep 消费后续控制
            BrakingGuidanceFallback.WriteDesired(request.controlModel,
                solution[0], solution[1], solution[2], ref command);
            command.solved = true;
            return true;
        }

        internal static double OptimalityTolerance(GuidanceRequest request)
        {
            if (request.motionOnly || request.intercept || request.combatTrajectory.seconds > 0f)
                return 0;
            return request.accuracy == GuidanceAccuracy.Cruise ? 0.01
                : request.accuracy == GuidanceAccuracy.Braking ? 0.001 : 0;
        }

        private static void CombatReferenceOffsets(GuidanceRequest request, float predictionTime,
            out Vector2 position, out Vector2 velocity, out float angle, out float omega)
        {
            float sampleTime = request.referenceStartSeconds + predictionTime;
            request.combatTrajectory.Sample(sampleTime, out Vector3 desiredPosition,
                out Vector2 desiredVelocity, out float desiredAngle, out float desiredOmega);
            Vector3 baselinePosition = OrbitSphere.Move(request.referencePosition,
                request.referenceVelocity * predictionTime);
            Vector2 displacement = OrbitSphere.Offset(baselinePosition, desiredPosition);
            position = SpaceTransform.TransportVelocity(new SpacePose(baselinePosition, 0f)
                { velocity = displacement }, request.referencePosition);
            velocity = SpaceTransform.TransportVelocity(new SpacePose(desiredPosition, 0f)
                { velocity = desiredVelocity }, request.referencePosition)
                - request.referenceVelocity;
            angle = SpaceTransform.WrapAngle(desiredAngle - request.referenceAngle
                - request.referenceAngularVelocity * predictionTime);
            omega = desiredOmega - request.referenceAngularVelocity;
        }

        private void BuildIntervals(GuidanceRequest request)
        {
            if (request.intercept)
            {
                double duration = Math.Max(request.dt, Math.Min(maxHorizon, request.arrivalSeconds));
                int interceptFine = Math.Min(Math.Min(8, horizonSteps - 1),
                    Math.Max(1, (int)Math.Floor(duration / request.dt)));
                for (int i = 0; i < interceptFine; i++) intervals[i] = request.dt;
                for (int i = interceptFine; i < horizonSteps; i++)
                    intervals[i] = Math.Max(0, duration - interceptFine * request.dt) / (horizonSteps - interceptFine);
                double time = 0;
                for (int i = 0; i < horizonSteps - 1; i++)
                {
                    if (i >= interceptFine && request.boostSeconds > time + 1e-6
                        && request.boostSeconds < time + intervals[i] - 1e-6)
                    {
                        double tail = time + intervals[i] - request.boostSeconds;
                        intervals[i] -= tail;
                        intervals[i + 1] += tail;
                        break;
                    }
                    time += intervals[i];
                }
                return;
            }
            if (request.trajectorySeconds > 0f)
            {
                double duration = Math.Max(2.0, Math.Min(6.0, request.trajectorySeconds));
                int fineSteps = Math.Min(8, horizonSteps);
                for (int i = 0; i < fineSteps; i++) intervals[i] = request.dt;
                double combatCoarse = (duration - fineSteps * request.dt)
                    / Math.Max(1, horizonSteps - fineSteps);
                for (int i = fineSteps; i < horizonSteps; i++) intervals[i] = combatCoarse;
                return;
            }
            double speed = request.relativeVelocity.magnitude;
            double distance = request.motionOnly ? 0.0 : request.positionError.magnitude;
            // 必须看到弱反推完成制动的时刻；用最强轴（常是侧推）估计会把制动段
            // 截在时域之外，尤其是正/反推不对称的船。
            double brake = Math.Max(0.0, request.brakingAcceleration);
            double stopTime = brake > 1e-6 ? speed / brake : maxHorizon;
            double travelTime = brake > 1e-6 ? Math.Sqrt(2.0 * distance / brake) : maxHorizon;
            double horizon = Math.Max(6.0, Math.Min(maxHorizon, 1.25 * Math.Max(stopTime, travelTime) + 3.0));
            int fine = Math.Min(8, horizonSteps);
            for (int i = 0; i < fine; i++) intervals[i] = request.dt;
            double remainder = Math.Max(request.dt, horizon - fine * request.dt);
            double coarse = horizonSteps > fine ? remainder / (horizonSteps - fine) : request.dt;
            for (int i = fine; i < horizonSteps; i++) intervals[i] = coarse;
        }

        private void AdvanceStateAndSensitivity(int step, double dt, GuidanceRequest request)
        {
            bool unboosted = UsesUnboosted(request, step);
            double[] b = unboosted ? request.unboostedAcceleration : request.controlAcceleration;
            int baseControl = step * InputCount;
            (float linear, float angular) = ShipPhysics.PassiveDragFactors(1f / 60f);
            ShipPhysics.HeldInputFactors(dt, linear, out double dl, out double gl, out double hl);
            ShipPhysics.HeldInputFactors(dt, angular, out double da, out double ga, out double ha);
            // 阻力作用于本船绝对速度，不能直接衰减相对目标的速度。
            state[0] += state[2] * gl + request.referenceVelocity.x * (gl - dt);
            state[1] += state[3] * gl + request.referenceVelocity.y * (gl - dt);
            state[4] += state[5] * ga + request.referenceAngularVelocity * (ga - dt);
            state[2] = state[2] * dl + request.referenceVelocity.x * (dl - 1.0);
            state[3] = state[3] * dl + request.referenceVelocity.y * (dl - 1.0);
            state[5] = state[5] * da + request.referenceAngularVelocity * (da - 1.0);
            for (int col = 0; col < variables; col++)
            {
                SetSens(0, col, Sens(0, col) + Sens(2, col) * gl);
                SetSens(1, col, Sens(1, col) + Sens(3, col) * gl);
                SetSens(4, col, Sens(4, col) + Sens(5, col) * ga);
                SetSens(2, col, Sens(2, col) * dl);
                SetSens(3, col, Sens(3, col) * dl);
                SetSens(5, col, Sens(5, col) * da);
            }
            for (int c = 0; c < InputCount; c++)
            {
                int col = baseControl + c;
                double ratio = unboosted ? ControlUnitRatio(request, c) : 1;
                SetSens(0, col, Sens(0, col) + b[c] * hl * ratio);
                SetSens(1, col, Sens(1, col) + b[3 + c] * hl * ratio);
                SetSens(2, col, Sens(2, col) + b[c] * gl * ratio);
                SetSens(3, col, Sens(3, col) + b[3 + c] * gl * ratio);
                SetSens(4, col, Sens(4, col) + b[6 + c] * ha * ratio);
                SetSens(5, col, Sens(5, col) + b[6 + c] * ga * ratio);
            }
        }

        private bool UsesUnboosted(GuidanceRequest request, int step)
        {
            if (!request.intercept || request.unboostedModel == null) return false;
            double end = 0;
            for (int i = 0; i <= step; i++) end += intervals[i];
            return end > request.boostSeconds + 1e-6;
        }

        private static double ControlUnitRatio(GuidanceRequest request, int axis)
        {
            var current = request.controlModel;
            var normal = request.unboostedModel;
            double active = axis == 0 ? current.positiveX + current.negativeX
                : axis == 1 ? current.positiveZ + current.negativeZ : current.positiveTorque + current.negativeTorque;
            double baseline = axis == 0 ? normal.positiveX + normal.negativeX
                : axis == 1 ? normal.positiveZ + normal.negativeZ : normal.positiveTorque + normal.negativeTorque;
            return baseline > 1e-8 ? Math.Max(1e-8, active / baseline) : 1;
        }

        private void AddStateCost(int stateIndex, double weight, double scale,
            double referenceOffset = 0.0)
        {
            double normalizedWeight = weight / (scale * scale);
            double offset = state[stateIndex] + referenceOffset;
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
            const double deltaPenalty = 0.75;
            for (int i = 0; i < inputVariables; i++) p[i * variables + i] += 2.0 * inputWeight * intervals[i / InputCount];
            for (int step = 0; step < horizonSteps; step++)
            {
                for (int axis = 0; axis < InputCount; axis++)
                {
                    double deltaWeight = deltaPenalty * intervals[step];
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

        // 非均匀网格按经过的物理时间重采样旧计划，不能把远场粗段当作微步左移。
        private void ShiftWarmStart(double elapsedSeconds)
        {
            Array.Copy(solution, previousSolution, variables);
            int oldStep = 0;
            double oldEnd = previousIntervals[0];
            double time = elapsedSeconds;
            for (int step = 0; step < horizonSteps; step++)
            {
                while (oldStep < horizonSteps - 1 && time >= oldEnd - 1e-9)
                    oldEnd += previousIntervals[++oldStep];
                for (int axis = 0; axis < InputCount; axis++)
                    solution[step * InputCount + axis] = time < oldEnd
                        ? previousSolution[oldStep * InputCount + axis] : 0.0;
                time += intervals[step];
            }
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
