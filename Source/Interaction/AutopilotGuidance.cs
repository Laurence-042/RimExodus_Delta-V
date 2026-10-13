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
            float available = ShipPhysics.PreviewBrakingAcceleration(damaged, Vector2.right);
            reverseThruster.operational = false;
            float disabled = ShipPhysics.PreviewBrakingAcceleration(damaged, Vector2.right);
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
            ShipPhysics.ControlModel model = ShipPhysics.BuildControlModel(report);
            double[] acceleration = new double[9];
            for (int axis = 0; axis < 3; axis++)
            {
                acceleration[axis] = model.forceColumns[axis].x / report.mass;
                acceleration[3 + axis] = model.forceColumns[axis].y / report.mass;
                acceleration[6 + axis] = model.torqueColumns[axis] / report.inertia;
            }
            leftTorque = HeadingTorque(report, model, acceleration, 0.5f, 0f);
            rightTorque = HeadingTorque(report, model, acceleration, -0.5f, 0f);
            brakeTorque = HeadingTorque(report, model, acceleration, 0f, 0.25f);
            return leftTorque > 0.001f && rightTorque < -0.001f
                && brakeTorque < -0.001f;
        }

        private static float HeadingTorque(ShipPhysics.MassReport report,
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
            ShipPhysics.Command allocated = ShipPhysics.ForceToCommand(report,
                command.desiredLocalForce, command.desiredTorque);
            ShipPhysics.Allocate(report, allocated, out _, out float torque);
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
            ShipPhysics.ControlModel model = ShipPhysics.BuildControlModel(report);
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
                        dt = 0.1f,
                        elapsedSteps = playbackSteps,
                        controlModel = model,
                        controlAcceleration = b,
                        brakingAcceleration = ShipPhysics.PreviewBrakingAcceleration(report,
                            targetPosition >= position ? Vector2.right : Vector2.left)
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
                        dt = 0.1f,
                        elapsedSteps = 1,
                        controlModel = model,
                        controlAcceleration = b,
                        brakingAcceleration = ShipPhysics.PreviewBrakingAcceleration(report,
                            targetPosition >= position ? Vector2.right : Vector2.left)
                    };
                    guidance.Compute(request, out command);
                }
                if (command.usedFallback) fallbackUses++;
                maxPrimal = Math.Max(maxPrimal, command.primalResidual);
                maxDual = Math.Max(maxDual, command.dualResidual);
                ShipPhysics.Command allocatedCommand = ShipPhysics.ForceToCommand(report,
                    command.desiredLocalForce, command.desiredTorque);
                ShipPhysics.Allocate(report, allocatedCommand, out Vector2 actualForce, out _);
                double appliedAcceleration = actualForce.x / report.mass;
                // 被动阻力同口径进入自测被控对象（预测模型一致性的确定性验证）。
                (float linearDecay, _) = ShipPhysics.PassiveDragFactors(0.1f);
                velocity = velocity * linearDecay + appliedAcceleration * 0.1 * linearDecay;
                position += velocity * 0.1;
                targetPosition += hostVelocity * 0.1;
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

    internal struct GuidanceRequest
    {
        // positionError = target - ship；relativeVelocity = ship - target。
        public Vector2 positionError;
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
            if (safeAccel > 1e-6 && distance > 1e-6)
            {
                Vector2 toward = -p.normalized;
                double speedLimit = Math.Sqrt(2.0 * safeAccel * distance);
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

        /// <summary>包络与回退共用的安全平移加速度（cells/s²）：控制模型各轴上限的最大列模 × 安全系数。</summary>
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

        // 软接近包络（刹车曲线形状，2026-09-25 定案）：闭合速度 ≤ floor + √(2·a_safe·d)。
        // 旧线性包络 0.15 + 0.2·d 在最后几格把闭合速度压成随距离线性衰减的爬行，终段
        // 对齐耗时过长；√(2·a·d) 是双积分器时间最优切换曲线——满推接近、满推反推、
        // v=0 时恰好到点（与 BrakingGuidanceFallback 的停止距离曲线同形状，两段衔接连续）。
        private const double CaptureSpeedFloor = 0.15;    // 格/s
        private const double CaptureOmegaFloor = 0.03;    // rad/s
        private const double EnvelopeSpeedCeiling = 20.0; // 高速时停止线性化斜率；不限制速度

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
        private bool hasPlannedSolution;

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
            hasPlannedSolution = false;
        }

        public bool TryGetPlannedStep(int stepIndex, ShipPhysics.ControlModel model, out GuidanceCommand command)
        {
            command = default;
            if (!hasPlannedSolution || stepIndex < 0 || stepIndex >= horizonSteps)
            {
                return false;
            }
            int input = stepIndex * InputCount;
            BrakingGuidanceFallback.WriteDesired(model,
                solution[input], solution[input + 1], solution[input + 2], ref command);
            return true;
        }

        public bool Compute(GuidanceRequest request, out GuidanceCommand command)
        {
            command = default;
            hasPlannedSolution = false;
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
            ShiftWarmStart(request.elapsedSteps <= 0 ? 1 : request.elapsedSteps);

            double distance = Math.Sqrt(state[0] * state[0] + state[1] * state[1]);
            double nx = distance > 1e-6 ? state[0] / distance : 1.0;
            double ny = distance > 1e-6 ? state[1] / distance : 0.0;
            double angleSign = Math.Abs(state[4]) > 1e-6 ? Math.Sign(state[4]) : 1.0;
            // 包络可用加速度与停止距离回退共用同一安全标量（单一实现）。
            double safeAccel = BrakingGuidanceFallback.SafeTranslationAcceleration(request);
            double safeAlpha = BrakingGuidanceFallback.SafeAngularAcceleration(request.controlAcceleration);
            int constraints = 0;
            bool combatTrajectory = request.combatTrajectory.seconds > 0f;
            double predictionTime = 0.0;

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

                // 刹车曲线软接近包络：闭合速度 ≤ floor + √(2·a_safe·d)，每步在零输入
                // rollout 状态（state[]）处线性化成切线约束 v_close − slope·d ≤ cap − slope·d₀；
                // 切线斜率 slope = a_safe/√(2·a_safe·d)（d→0 由 floor 封底），高速段封顶
                // EnvelopeSpeedCeiling（斜率归零）。视线方向冻结在求解时刻（与旧实现一致）。
                int row;
                if (!combatTrajectory)
                {
                    row = constraints++;
                    double losDistance = nx * state[0] + ny * state[1];
                    double brakingSpeed = Math.Sqrt(2.0 * safeAccel *
                        Math.Max(losDistance - request.approachReserveDistance, 0.0));
                    double speedCap = CaptureSpeedFloor + brakingSpeed;
                    double speedSlope = speedCap >= EnvelopeSpeedCeiling
                        ? 0.0
                        : safeAccel / Math.Max(brakingSpeed, CaptureSpeedFloor);
                    for (int col = 0; col < inputVariables; col++)
                    {
                        a[row * variables + col] = -nx * Sens(2, col) - ny * Sens(3, col)
                            - speedSlope * (nx * Sens(0, col) + ny * Sens(1, col));
                    }
                    a[row * variables + translationSlack] = -1.0;
                    double translationConstant = -nx * state[2] - ny * state[3]
                        - speedSlope * (nx * state[0] + ny * state[1]);
                    lower[row] = -Infinite;
                    upper[row] = speedCap - speedSlope * losDistance - translationConstant;
                }

                // 角向同构：|ω| ≤ floor + √(2·α_safe·|θ|)，在零输入 rollout 剩余角差处线性化。
                row = constraints++;
                double thetaSign = Math.Abs(state[4] - desiredAngle) > 1e-6
                    ? Math.Sign(state[4] - desiredAngle) : angleSign;
                double thetaSigned = thetaSign * (state[4] - desiredAngle);
                double brakingOmega = Math.Sqrt(2.0 * safeAlpha * Math.Max(thetaSigned, 0.0));
                double omegaCap = CaptureOmegaFloor + brakingOmega;
                double omegaSlope = safeAlpha / Math.Max(brakingOmega, CaptureOmegaFloor);
                for (int col = 0; col < inputVariables; col++)
                {
                    a[row * variables + col] = -thetaSign * Sens(5, col)
                        - omegaSlope * thetaSign * Sens(4, col);
                }
                a[row * variables + angularSlack] = -1.0;
                double angularConstant = -thetaSign * (state[5] - desiredOmega)
                    - omegaSlope * thetaSigned;
                lower[row] = -Infinite;
                upper[row] = omegaCap - omegaSlope * thetaSigned - angularConstant;
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
                double terminalOffset = s switch
                {
                    0 => -terminalPosition.x, 1 => -terminalPosition.y,
                    2 => -terminalVelocity.x, 3 => -terminalVelocity.y,
                    4 => -terminalAngle, _ => -terminalOmega
                };
                double headingPriority = combatTrajectory && s >= 4 ? 4.0 : 1.0;
                AddStateCost(s, StateWeight[s] * 20.0 * headingPriority,
                    StateScale[s], terminalOffset);
            }
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
            // 松弛变量与未完全收敛的 ADMM 解可能越过软包络。若首步已经不具备
            // 在目标前停下的余量，不回放这份计划，而走共用的制动回退。
            if (accepted && !combatTrajectory && distance > 1e-6)
            {
                double firstDt = intervals[0];
                double ax = 0.0, ay = 0.0;
                for (int c = 0; c < InputCount; c++)
                {
                    ax += request.controlAcceleration[c] * solution[c];
                    ay += request.controlAcceleration[3 + c] * solution[c];
                }
                double projectedClosing = Math.Max(0.0,
                    -(nx * request.relativeVelocity.x + ny * request.relativeVelocity.y)
                    - (nx * ax + ny * ay) * firstDt);
                double projectedDistance = Math.Max(0.0, distance
                    + (nx * request.relativeVelocity.x + ny * request.relativeVelocity.y) * firstDt);
                double stop = BrakingGuidanceFallback.StoppingDistance(projectedClosing, safeAccel);
                if (stop > projectedDistance + CaptureSpeedFloor * firstDt
                    && projectedClosing > CaptureSpeedFloor) accepted = false;
            }
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
                command = fallback;
                return false;
            }

            hasPlannedSolution = true; // 可回放：微步间经 TryGetPlannedStep 消费后续控制
            BrakingGuidanceFallback.WriteDesired(request.controlModel,
                solution[0], solution[1], solution[2], ref command);
            command.solved = true;
            return true;
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

        /// <summary>热启动平移：上次解左移 steps 个输入步（= 两次求解间已消费的微步数），
        /// 对齐「上次计划已执行到哪」的时间轴；steps 钳制到 [1, horizonSteps-1]。</summary>
        private void ShiftWarmStart(int steps)
        {
            int inputVariables = horizonSteps * InputCount;
            int shift = Math.Max(1, Math.Min(steps, horizonSteps - 1)) * InputCount;
            for (int i = 0; i < inputVariables - shift; i++) solution[i] = solution[i + shift];
            for (int i = inputVariables - shift; i < inputVariables; i++) solution[i] = 0.0;
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
