using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>固定航点、随动和战术轨迹共用 MPC；只通过对象驾驶契约读取能力和提交控制。</summary>
    public class ShipAutopilot
    {
        private enum ApproachPhase { Cruise, Braking, Precision }

        private class Flight
        {
            public DriveSubject subject;
            public int ownerId => subject.key;
            public bool playerInitiated;
            public bool combatOrbit;
            public float orbitRadius;
            public CombatTrajectoryPlanner combatPlanner;
            public CombatTargetSampler combatTarget;
            public CombatTrajectoryIntent combatIntent;
            public int combatIntentMicroStep = -1;
            public int combatIntentTick;
            public float diagnosticTargetAngle;
            public float diagnosticAngleError;
            public float diagnosticTargetOmega;
            public float diagnosticActualOmega;
            public float diagnosticRequestedTorque;
            public float diagnosticAllocatedTorque;
            public float diagnosticDistance;
            public bool traceNextCombatDrive;
            public bool traceNextAiDrive;
            public bool aiDriveObserved, lastAiRecovery;
            public DriveResult lastAiResult;
            public QpSolveStatus lastAiSolver;
            public QpSolveStatus lastSolvedStatus;
            // 固定航点模式：direction/angle；随动模式：host ⊕ hostOffset（+ 相对转角）。
            public bool tracked;
            public Func<SpacePose, SpacePose> reference;
            public Func<bool> referenceValid;
            public bool motionOnly;
            public bool finishWhenMatched;
            public float arrivalVelocityTolerance = DefaultArrivalVelocityTolerance;
            public float arrivalApproachSpeed;
            public bool intercept;
            public Vector3 direction;
            public float angle;
            public int hostId;
            public SpaceObject host => Current.Game?.GetComponent<UniverseManager>()?.ObjectById(hostId);
            public Vector2 hostOffset;
            public float relativeAngle;
            public float elapsed;
            public bool allowVoidThrust;
            public Action onArrived;
            public Func<bool> arrivalGate;
            public Action onFailed;
            public Action<string> onFailedWithReason;
            public int maxFlightTicks = MaxFlightTicks;
            public readonly IAutopilotGuidance guidance = new OnlineMpcGuidance();
            public readonly double[] controlAcceleration = new double[9];
            public readonly ShipPhysics.ControlModel controlModel = new ShipPhysics.ControlModel();
            public readonly ShipPhysics.ControlModel unboostedModel = new ShipPhysics.ControlModel();
            public readonly double[] unboostedAcceleration = new double[9];
            public GuidanceCommand heldCommand;
            public bool hasCommand;
            // 计划回放：currentPlanStep = 当前应用的计划步（0 = 求解当步）；
            // planStepsReady = 上一解可回放的微步数（数值回退解为 1，无可回放计划）。
            public int currentPlanStep;
            public int planStepsReady;
            public int planStartTick;
            public AutopilotMpcProfile profile;
            public float bestError = float.PositiveInfinity;
            public int noProgressTicks;
            public bool failureLogged;
            public readonly BrakingRecoveryState recovery = new BrakingRecoveryState();
            public bool recoveryRecheck;
            public float recoveryBestSpeed = float.PositiveInfinity;
            public ApproachPhase phase;
            public float brakingAcceleration;
            public float stoppingDistance;
            public int safeFarTicks;
            public int previewReportVersion = -1;
            public DriveInfo previewInfo;
        }

        private readonly Dictionary<DriveSubject, Flight> active = new Dictionary<DriveSubject, Flight>();

        private readonly List<DriveSubject> tmpShips = new List<DriveSubject>();

        private AutopilotMpcProfile profile = AutopilotMpcProfile.Balanced;

        internal AutopilotMpcProfile Profile => profile;

        /// <summary>制导微步（tick）：MPC 计划步长与指令更新粒度，积分仍 60 Hz。</summary>
        private const int GuidanceMicroStepTicks = 6;

        /// <summary>阶段对应 QP 求解周期；解之间逐微步回放计划控制。</summary>
        private const int NearSolvePeriodSteps = 1;
        private const int MidSolvePeriodSteps = 2;
        private const int FarSolvePeriodSteps = 4;
        private const float TightSolveDistance = 9f;  // 格（约 2× 虚空动力渐变半径）
        private const int FarRecoveryTicks = 60;

        private static int SolvePeriodFor(ApproachPhase phase)
        {
            if (phase == ApproachPhase.Precision) return NearSolvePeriodSteps;
            return phase == ApproachPhase.Braking ? MidSolvePeriodSteps : FarSolvePeriodSteps;
        }

        /// <summary>确定性错峰：多船任务的 QP 求解按 (微步序号 + ownerId) mod K 错开，
        /// 避免同 tick 叠加求解尖峰；纯游戏时钟判定（不变量 #20）。</summary>
        private static bool SolveDue(int ownerId, int microIndex, int solvePeriod)
        {
            return (microIndex + ownerId) % solvePeriod == 0;
        }

        /// <summary>虚空动力渐变半径（格）：外沿起效、进入吸附圈补满——只在停靠任务
        /// （allowVoidThrust）中补齐推进器布局无法形成的「期望 − 可达」差值，
        /// 远场仍是纯真实推进。</summary>
        private const float VoidRampRadius = 4.5f;

        private const float ArrivePositionEpsilon = 0.05f; // 格
        internal const float DefaultArrivalVelocityTolerance = 0.15f; // cells/s
        private const float ArriveAngleEpsilon = 0.01f;    // rad
        private const float ArriveOmegaEpsilon = 0.03f;    // rad/s

        /// <summary>固定航点最长 120 秒；超时/连续 10 秒无进展走失败，不再瞬移。</summary>
        private const int MaxFlightTicks = 7200;
        private const int MaxNoProgressTicks = 600;

        /// <summary>
        /// 把船自动驾驶到轨道坐标（球面方向 direction，宇宙角 angle）。物理收敛到
        /// 容差时精确对位（pose := 目标、速度清零）并回调 onArrived；超时/无进展或船图
        /// 失效等异常按 onFailed 退出。重复登记同一归属会覆盖旧任务（旧任务静默作废）。
        /// </summary>
        public void FlyTo(DriveSubject subject, Vector3 targetDirection, float targetAngle,
            Action onArrived = null, Action onFailed = null, bool allowVoidThrust = true, bool playerInitiated = true)
        {
            Register(new Flight { subject = subject, direction = targetDirection.normalized,
                angle = SpaceTransform.WrapAngle(targetAngle), onArrived = onArrived, onFailed = onFailed,
                allowVoidThrust = allowVoidThrust, playerInitiated = playerInitiated });
        }
        public void TrackTo(DriveSubject subject, SpaceObject host, Vector2 hostOffset,
            float relativeAngle, Action onFailed = null, bool allowVoidThrust = true, bool playerInitiated = true)
        {
            if (host == null) { onFailed?.Invoke(); return; }
            Register(new Flight { subject = subject, tracked = true, hostId = host.objectId,
                hostOffset = hostOffset, relativeAngle = SpaceTransform.WrapAngle(relativeAngle),
                onFailed = onFailed, allowVoidThrust = allowVoidThrust, playerInitiated = playerInitiated });
        }

        // The owning service supplies the same reference used by its alignment or readiness check.
        // Do not copy its anchor/angle into another navigation target.
        internal void FollowReference(DriveSubject subject, Func<SpacePose, SpacePose> reference,
            bool motionOnly, Func<bool> valid, Action onFailed)
            => Register(new Flight { subject = subject, tracked = true, reference = reference,
                referenceValid = valid, motionOnly = motionOnly, onFailed = onFailed,
                allowVoidThrust = false, playerInitiated = true });

        internal void NavigateToReference(DriveSubject subject, Func<SpacePose, SpacePose> reference,
            Func<bool> valid, Action onArrived, Action<string> onFailed, bool longRange = false,
            Func<bool> arrivalGate = null,
            float arrivalVelocityTolerance = DefaultArrivalVelocityTolerance,
            float arrivalApproachSpeed = 0f)
            => Register(new Flight { subject = subject, tracked = true, reference = reference,
                referenceValid = valid, finishWhenMatched = true, onArrived = onArrived,
                arrivalGate = arrivalGate,
                onFailedWithReason = onFailed, allowVoidThrust = false, playerInitiated = true,
                arrivalVelocityTolerance = Mathf.Max(0f, arrivalVelocityTolerance),
                arrivalApproachSpeed = Mathf.Max(0f, arrivalApproachSpeed),
                // A star-map route can span arbitrary sector scales. Reference/subject/
                // thrust loss still fail explicitly; an elapsed-time cutoff must not drop
                // control while the ship is braking and leave it drifting through the goal.
                maxFlightTicks = longRange ? int.MaxValue : MaxFlightTicks });

        internal bool IsFollowing(DriveSubject subject, Func<SpacePose, SpacePose> reference)
            => active.TryGetValue(subject, out Flight flight) && flight.reference == reference;

        internal void Intercept(DriveSubject subject, Func<SpacePose, SpacePose> reference,
            Func<bool> valid, Action onFailed)
            => Register(new Flight { subject = subject, tracked = true, reference = reference,
                referenceValid = valid, intercept = true, onFailed = onFailed });

        internal bool DetachReference(DriveSubject subject, Func<SpacePose, SpacePose> reference)
        {
            if (!IsFollowing(subject, reference)) return false;
            Detach(subject);
            return true;
        }
        private void Register(Flight flight)
        {
            var universe = Current.Game?.GetComponent<UniverseManager>();
            if (flight.subject.Resolve(universe)?.TryGetDrive(flight.subject, out _) != true)
            { flight.onFailed?.Invoke(); flight.onFailedWithReason?.Invoke("DeltaV_NavigationSubjectLost"); return; }
            Detach(flight.subject);
            flight.profile = profile; flight.guidance.Reset(profile);
            active[flight.subject] = flight;
        }
        internal void TrackOrbit(DriveSubject subject, CombatTargetSampler target, float radius,
            CombatTrajectoryPlanner planner, bool playerInitiated = false)
        {
            if (target == null || planner == null) return;
            if (active.TryGetValue(subject, out Flight existing) && existing.combatOrbit)
            { existing.orbitRadius = radius; existing.combatPlanner = planner; existing.combatTarget = target; return; }
            Register(new Flight { subject = subject, tracked = true, combatTarget = target, combatOrbit = true,
                orbitRadius = radius, combatPlanner = planner, playerInitiated = playerInitiated });
        }

        internal bool IsCombatOrbit(DriveSubject subject) => active.TryGetValue(subject, out Flight flight)
            && flight.combatOrbit;

        internal void RequestCombatTrace(DriveSubject subject)
        {
            if (active.TryGetValue(subject, out Flight flight) && flight.combatOrbit)
                flight.traceNextCombatDrive = true;
        }

        internal void RequestAiTrace(DriveSubject subject)
        {
            if (active.TryGetValue(subject, out Flight flight) && flight.combatOrbit)
                flight.traceNextAiDrive = true;
        }

        /// <summary>开发者按需读取的姿态／力矩快照；热路径只更新标量，不刷日志。</summary>
        internal string CombatDiagnostics()
        {
            var result = new System.Text.StringBuilder("[DeltaV:Combat] autopilot trajectory");
            foreach (KeyValuePair<DriveSubject, Flight> pair in active)
            {
                Flight flight = pair.Value;
                if (!flight.combatOrbit) continue;
                result.Append($"\n- ship #{pair.Key.key} "
                    + $"distance={flight.diagnosticDistance:F1}/{flight.orbitRadius:F1} "
                    + $"horizon={flight.combatIntent.seconds:F1}s phase={flight.combatIntent.phaseAdvance * Mathf.Rad2Deg:F1}° "
                    + $"heading={flight.diagnosticTargetAngle * Mathf.Rad2Deg:F1}° "
                    + $"error={flight.diagnosticAngleError * Mathf.Rad2Deg:F1}° "
                    + $"omega={flight.diagnosticActualOmega:F3}/{flight.diagnosticTargetOmega:F3} rad/s "
                    + $"torque={flight.diagnosticRequestedTorque:F2}/{flight.diagnosticAllocatedTorque:F2}");
            }
            return result.ToString();
        }

        public void Detach(DriveSubject subject, string reason = null)
        {
            if (DeltaVLog.Enabled(DeltaVLogModule.AI) && active.TryGetValue(subject, out var flight) && flight.combatOrbit)
                DeltaVLog.Message(DeltaVLogModule.AI, $"drive-detach tick={Find.TickManager.TicksGame} ship={subject} reason='{reason ?? "cancel/replace"}'");
            if (!active.Remove(subject)) return;
            subject.Resolve(Current.Game?.GetComponent<UniverseManager>())?.ClearDrive();
        }
        internal void Detach(SpaceObject container)
        {
            var universe = Current.Game?.GetComponent<UniverseManager>();
            var removed = new List<DriveSubject>();
            foreach (var pair in active)
                if ((pair.Key.Resolve(universe)?.objectId ?? pair.Key.objectId) == container.objectId) removed.Add(pair.Key);
            foreach (var subject in removed) Detach(subject);
        }
        public bool IsActive(DriveSubject subject) => subject.key > 0 && active.ContainsKey(subject);
        internal string DecisionStatus(DriveSubject subject)
        {
            if (!active.TryGetValue(subject, out Flight flight))
                return "DeltaV_CombatDecisionNoFlight".Translate();
            if (flight.combatOrbit && flight.combatIntent.holdCurrent)
                return "DeltaV_CombatPoseBlocked".Translate();
            if (flight.recovery.Active) return "DeltaV_CombatDecisionBraking".Translate();
            string state = (flight.combatOrbit ? "DeltaV_CombatDecisionFlightCombat"
                : "DeltaV_CombatDecisionFlightNavigation").Translate();
            return state + "\n" + (flight.hasCommand
                ? "DeltaV_CombatDecisionCommand".Translate(flight.heldCommand.desiredLocalForce.magnitude.ToString("0.##"),
                    flight.heldCommand.desiredTorque.ToString("0.##"),
                    ("DeltaV_CombatSolver_" + flight.heldCommand.qpStatus).Translate()).ToString()
                : "DeltaV_CombatDecisionWaitingCommand".Translate().ToString());
        }
        public bool IsActive(SpaceObject container)
        {
            var universe = Current.Game?.GetComponent<UniverseManager>();
            foreach (var subject in active.Keys)
                if (subject.Resolve(universe)?.objectId == container.objectId) return true;
            return false;
        }
        public void Cancel(DriveSubject subject)
        {
            if (!active.TryGetValue(subject, out Flight flight)) return;
            Detach(subject); flight.onFailed?.Invoke();
        }

        /// <summary>UniverseManager.GameComponentTick 每 tick 驱动（游戏时间语义：
        /// 暂停冻结、倍速加速）。</summary>
        public void Tick()
        {
            if (active.Count == 0) return;
            // 回调可能在迭代中增删任务（到达后立即发起新任务等），按快照迭代。
            tmpShips.Clear();
            foreach (KeyValuePair<DriveSubject, Flight> pair in active) tmpShips.Add(pair.Key);
            foreach (DriveSubject subject in tmpShips)
            {
                if (active.TryGetValue(subject, out Flight flight)) Step(subject.key, flight);
            }
        }

        private void Step(int ownerId, Flight flight)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject container = flight.subject.Resolve(universe);
            if (container == null || !container.TryGetDrive(flight.subject, out DriveInfo report))
            { Fail(container, flight, "DeltaV_NavigationSubjectLost"); return; }
            if (flight.reference != null ? flight.referenceValid?.Invoke() != true
                : flight.tracked && !flight.combatOrbit && flight.host == null)
            { Fail(container, flight, "DeltaV_NavigationTargetLost"); return; }
            SpacePose combatTarget = default;
            if (flight.combatOrbit && (flight.combatTarget == null || !flight.combatTarget(out combatTarget)))
            { Fail(container, flight, "combat reference unavailable"); return; }
            // A player-maintained EVA/personnel grapple is an operator force. Docking-owned
            // capture links are deliberately excluded: their thrust and this controller
            // cooperate during approach and braking.
            if (universe.TractorLinks.HasOperatorForce(container))
            {
                container.ClearDrive();
                return;
            }
            const float dt = 1f / 60f; // 游戏 tick 时间；绝不使用帧时钟（AGENTS §18 #20）
            flight.elapsed += 1;
            int ticksGame = Find.TickManager.TicksGame;

            // 目标位姿与宿主前馈：误差/速度统一表达在目标锚点的切空间基下（球面几何
            // 唯一入口 OrbitSphere）。随动模式取宿主刚体目标点速度（含 ω×offset），
            // MPC 在 0.1 s 后重新观测；预测期内按恒速/恒角速度目标处理。
            Vector3 targetDirection;
            float targetAngle;
            Vector2 hostVelocity = Vector2.zero;
            float hostOmega = 0f;
            float combatHorizon = 0f;
            float combatReferenceTime = 0f;
            SpacePose arrivalReference = default;
            if (flight.tracked)
            {
                if (flight.reference != null)
                {
                    SpacePose reference = flight.reference(container.pose);
                    arrivalReference = reference;
                    targetDirection = reference.position;
                    targetAngle = reference.angle;
                    hostVelocity = reference.velocity;
                    hostOmega = reference.angularVelocity;
                }
                else if (flight.combatOrbit)
                {
                    int planMicroStep = ticksGame / GuidanceMicroStepTicks;
                    if (flight.combatIntentMicroStep != planMicroStep
                        || report.version != flight.previewReportVersion || !ReferenceEquals(report, flight.previewInfo))
                    {
                        if (flight.combatPlanner == null || !flight.combatPlanner(
                            container, combatTarget, flight.orbitRadius, report,
                            out flight.combatIntent))
                        {
                            active.Remove(flight.subject);
                            container.ClearDrive();
                            return;
                        }
                        flight.combatIntentMicroStep = planMicroStep;
                        flight.combatIntentTick = flight.combatIntent.startTick;
                    }
                    CombatTrajectoryIntent intent = flight.combatIntent;
                    combatHorizon = intent.seconds;
                    combatReferenceTime = (ticksGame - flight.combatIntentTick) / 60f;
                    intent.Sample(combatReferenceTime, out targetDirection, out hostVelocity,
                        out targetAngle, out hostOmega);
                }
                else
                {
                    targetDirection = SpaceTransform.FocusedToUniverse(flight.host,
                        flight.hostOffset);
                    targetAngle = SpaceTransform.WrapAngle(flight.host.pose.angle
                        + flight.relativeAngle);
                    hostVelocity = SpaceTransform.VelocityAtMovingLocalPoint(
                        flight.host.pose, flight.hostOffset, Vector2.zero, targetDirection);
                    hostOmega = flight.host.pose.angularVelocity;
                }
            }
            else
            {
                targetDirection = flight.direction;
                targetAngle = flight.angle;
                arrivalReference = new SpacePose(targetDirection, targetAngle);
            }
            SpacePose pose = container.pose;
            ShipBase vessel = ShipRegistry.ShipByIdentity(flight.subject);
            SpacePose desired = new SpacePose(targetDirection, targetAngle)
            {
                velocity = hostVelocity,
                angularVelocity = hostOmega
            };
            desired = LandingApproachController.ApplyMotionContract(pose, desired, vessel);
            targetDirection = desired.position;
            targetAngle = desired.angle;
            hostVelocity = desired.velocity;
            hostOmega = desired.angularVelocity;
            if (!flight.tracked || flight.reference != null) arrivalReference = desired;
            Vector2 posErr = -OrbitSphere.Offset(targetDirection, pose.position);
            Vector2 vel = SpaceTransform.TransportVelocity(pose, targetDirection) - hostVelocity;
            float angleErr = SpaceTransform.WrapAngle(targetAngle - pose.angle);
            float omega = pose.angularVelocity - hostOmega;
            if (flight.combatOrbit)
            {
                flight.diagnosticDistance = OrbitSphere.ArcCells(pose.position,
                    combatTarget.position);
                flight.diagnosticTargetAngle = targetAngle;
                flight.diagnosticAngleError = angleErr;
                flight.diagnosticTargetOmega = hostOmega;
                flight.diagnosticActualOmega = pose.angularVelocity;
            }

            if ((!flight.tracked || flight.finishWhenMatched)
                && posErr.sqrMagnitude < ArrivePositionEpsilon * ArrivePositionEpsilon
                && vel.sqrMagnitude < flight.arrivalVelocityTolerance
                    * flight.arrivalVelocityTolerance
                && (vessel?.RequiresHeadingAlignment == false
                    || Mathf.Abs(angleErr) < ArriveAngleEpsilon)
                && (vessel?.RequiresAngularVelocityMatch == false
                    || Mathf.Abs(omega) < ArriveOmegaEpsilon)
                && flight.arrivalGate?.Invoke() != false)
            {
                Arrive(container, flight, arrivalReference);
                return;
            }

            if (flight.finishWhenMatched && report.AvailableThrust <= 0f)
            {
                Fail(container, flight, "DeltaV_NavigationNoThrust");
                return;
            }
            if (flight.finishWhenMatched && flight.elapsed > flight.maxFlightTicks)
            {
                Fail(container, flight, "DeltaV_NavigationTimedOut");
                return;
            }

            if (!flight.tracked)
            {
                float progressMetric = flight.recovery.Active
                    ? vel.magnitude + Mathf.Abs(omega) * 5f
                    : posErr.magnitude + Mathf.Abs(angleErr) * 10f;
                float best = flight.recovery.Active ? flight.recoveryBestSpeed : flight.bestError;
                if (progressMetric + (flight.recovery.Active ? 0.01f : 0.05f) < best)
                {
                    if (flight.recovery.Active) flight.recoveryBestSpeed = progressMetric;
                    else flight.bestError = progressMetric;
                    flight.noProgressTicks = 0;
                }
                else
                {
                    flight.noProgressTicks++;
                }
                if (flight.elapsed > flight.maxFlightTicks || flight.noProgressTicks > MaxNoProgressTicks)
                {
                    Fail(container, flight, "waypoint timeout/no progress");
                    return;
                }
            }

            float voidBlend = flight.allowVoidThrust && !flight.recovery.Active
                ? 1f - Mathf.Clamp01(posErr.magnitude / VoidRampRadius)
                : 0f;
            if (flight.profile != profile)
            {
                flight.profile = profile;
                flight.guidance.Reset(flight.profile);
                flight.hasCommand = false;
                flight.planStepsReady = 0;
                flight.recoveryRecheck = true;
            }
            // 制导微步节拍（2026-09-25 定案）：QP 按 K 个微步解一次（K 按距离自适应、
            // ownerId 确定性错峰），中间微步回放上一解的计划控制；计划耗尽或数值回退
            // 时立即重解恢复反馈节奏。微步内指令恒定，应用侧由此可安全去重（见下）。
            bool microBoundary = (flight.recovery.Active ? ticksGame - flight.planStartTick : ticksGame)
                % GuidanceMicroStepTicks == 0;
            bool phaseChanged = false;
            bool unsafePlan = false;
            bool reportChanged = report.version != flight.previewReportVersion || !ReferenceEquals(report, flight.previewInfo);
            flight.recoveryRecheck |= reportChanged;
            if (!flight.hasCommand || microBoundary || reportChanged)
            {
                flight.previewReportVersion = report.version;
                flight.previewInfo = report;
                Vector2 toward = flight.motionOnly ? vel : posErr;
                Vector2 towardLocal = toward.sqrMagnitude > 1e-8f
                    ? SpaceTransform.RotateInverse(SpaceTransform.TransportTangent(toward.normalized,
                        targetDirection, pose.position), pose.angle)
                    : Vector2.zero;
                flight.brakingAcceleration = report.PreviewBraking(towardLocal);
                float closing = posErr.sqrMagnitude > 1e-8f
                    ? Mathf.Max(0f, Vector2.Dot(vel, posErr.normalized)) : 0f;
                float excessClosing = Mathf.Max(0f, closing - flight.arrivalApproachSpeed);
                double safeAcceleration = flight.brakingAcceleration * BrakingGuidanceFallback.SafetyFactor;
                double stopping = BrakingGuidanceFallback.StoppingDistance(excessClosing, safeAcceleration);
                flight.stoppingDistance = double.IsInfinity(stopping)
                    ? float.PositiveInfinity : (float)stopping;
                // 巡航计划最多再回放四个微步；从切换判据中扣除这段闭合距离。
                float remaining = posErr.magnitude;
                float brakeMargin = flight.stoppingDistance + excessClosing *
                    (FarSolvePeriodSteps * GuidanceMicroStepTicks / 60f);
                unsafePlan = flight.brakingAcceleration > 1e-6f
                    && excessClosing > 0.15f
                    && remaining <= flight.stoppingDistance
                        + excessClosing * (GuidanceMicroStepTicks / 60f);
                ApproachPhase next = remaining <= TightSolveDistance
                    ? ApproachPhase.Precision
                    : remaining <= brakeMargin || flight.brakingAcceleration <= 1e-6f
                        ? ApproachPhase.Braking : ApproachPhase.Cruise;
                if (flight.phase == ApproachPhase.Precision
                    && remaining <= 2f * TightSolveDistance)
                    next = ApproachPhase.Precision;
                if (flight.combatOrbit && next == ApproachPhase.Precision
                    && remaining < 4f
                    && Mathf.Abs(angleErr) < 0.15f && vel.magnitude < 2f)
                {
                    // 环绕参考点常在船附近，不能因此让所有稳定环绕船常驻 10 Hz 求解。
                    next = ApproachPhase.Braking;
                }
                if (flight.phase != ApproachPhase.Cruise && next == ApproachPhase.Cruise
                    && flight.tracked)
                {
                    // 长期随动目标移远时持续安全一秒才恢复低频，防止边界抖动。
                    flight.safeFarTicks += GuidanceMicroStepTicks;
                    if (flight.safeFarTicks < FarRecoveryTicks
                        || remaining <= 2f * Mathf.Max(TightSolveDistance, brakeMargin))
                        next = flight.phase;
                }
                else flight.safeFarTicks = 0;
                if (flight.intercept)
                {
                    // 截击不使用停靠制动阶段；碰撞服务决定接触，MPC 从不宣告落地。
                    next = remaining <= TightSolveDistance ? ApproachPhase.Precision : ApproachPhase.Cruise;
                    unsafePlan = false;
                }
                if (next != flight.phase)
                {
                    flight.phase = next;
                    phaseChanged = true;
                }
            }
            if (flight.recovery.Active && (microBoundary || reportChanged || !flight.hasCommand))
            {
                if (flight.recovery.ControlDue(ticksGame, flight.recoveryRecheck)
                    || flight.recovery.Fallback)
                    Solve(ownerId, flight, report, pose, posErr, vel, angleErr, omega, 0f,
                        FarSolvePeriodSteps, combatHorizon, combatReferenceTime,
                        targetDirection, hostVelocity, targetAngle, hostOmega);
                else if (flight.currentPlanStep + 1 < flight.planStepsReady
                    && flight.guidance.TryGetPlannedStep(flight.currentPlanStep + 1, flight.controlModel, out GuidanceCommand planned))
                {
                    flight.heldCommand = planned;
                    flight.currentPlanStep++;
                }
                else
                    Solve(ownerId, flight, report, pose, posErr, vel, angleErr, omega, 0f,
                        FarSolvePeriodSteps, combatHorizon, combatReferenceTime,
                        targetDirection, hostVelocity, targetAngle, hostOmega);
            }
            else if (!flight.hasCommand)
            {
                Solve(ownerId, flight, report, pose, posErr, vel, angleErr, omega, voidBlend,
                    SolvePeriodFor(flight.phase), combatHorizon, combatReferenceTime,
                    targetDirection, hostVelocity, targetAngle, hostOmega);
            }
            else if (!flight.recovery.Active && (microBoundary || reportChanged))
            {
                int solvePeriod = SolvePeriodFor(flight.phase);
                if (reportChanged || phaseChanged || unsafePlan
                    || SolveDue(ownerId, ticksGame / GuidanceMicroStepTicks, solvePeriod))
                {
                    Solve(ownerId, flight, report, pose, posErr, vel, angleErr, omega, voidBlend,
                        solvePeriod, combatHorizon, combatReferenceTime,
                        targetDirection, hostVelocity, targetAngle, hostOmega);
                }
                else if (flight.currentPlanStep + 1 < flight.planStepsReady
                    && flight.guidance.TryGetPlannedStep(flight.currentPlanStep + 1, flight.controlModel,
                        out GuidanceCommand planned))
                {
                    flight.heldCommand = planned;
                    flight.currentPlanStep++;
                }
                else
                {
                    Solve(ownerId, flight, report, pose, posErr, vel, angleErr, omega, voidBlend,
                        solvePeriod, combatHorizon, combatReferenceTime,
                        targetDirection, hostVelocity, targetAngle, hostOmega);
                }
            }

            DriveControl control = new DriveControl { force = flight.heldCommand.desiredLocalForce,
                torque = flight.heldCommand.desiredTorque, voidBlend = flight.recovery.Active ? 0f : voidBlend,
                playerInitiated = flight.playerInitiated };
            DriveResult result = container.ApplyDrive(flight.subject, control, dt, out float torque);
            if (result == DriveResult.InvalidSubject)
                Fail(container, flight, "DeltaV_NavigationSubjectLost");
            if (flight.combatOrbit)
            {
                flight.diagnosticRequestedTorque = control.torque;
                flight.diagnosticAllocatedTorque = torque;
                if (DeltaVLog.Enabled(DeltaVLogModule.AI))
                {
                    if (flight.traceNextAiDrive || !flight.aiDriveObserved || result != flight.lastAiResult
                        || flight.recovery.Active != flight.lastAiRecovery
                        || flight.lastSolvedStatus != flight.lastAiSolver)
                    {
                        flight.aiDriveObserved = true; flight.lastAiResult = result;
                        flight.lastAiRecovery = flight.recovery.Active; flight.lastAiSolver = flight.lastSolvedStatus;
                        ShipBase ship = ShipRegistry.ShipByIdentity(flight.subject);
                        Vector2 deltaVelocity = SpaceTransform.TransportVelocity(container.pose, pose.position) - pose.velocity;
                        DeltaVLog.Message(DeltaVLogModule.AI,
                            $"drive tick={ticksGame} planTick={flight.combatIntentTick} ship={flight.subject} object={container.objectId} "
                            + $"{UniverseManager.MapTag(ship?.Map)} result={result} phase={flight.phase} "
                            + $"recovery={flight.recovery.Active} blocked={flight.combatIntent.holdCurrent} "
                            + $"positionError=({posErr.x:F3},{posErr.y:F3}) velocityError=({vel.x:F3},{vel.y:F3}) "
                            + $"angleErrorDeg={angleErr * Mathf.Rad2Deg:F2} omega={pose.angularVelocity:F4}->{hostOmega:F4} "
                            + $"localForce=({control.force.x:F2},{control.force.y:F2}) torque={control.torque:F2}/{torque:F2} "
                            + $"deltaV=({deltaVelocity.x:F5},{deltaVelocity.y:F5}) "
                            + $"deltaAngleDeg={SpaceTransform.WrapAngle(container.pose.angle - pose.angle) * Mathf.Rad2Deg:F4} "
                            + $"mpc={flight.lastSolvedStatus} steps={flight.currentPlanStep}/{flight.planStepsReady}");
                    }
                }
                else flight.aiDriveObserved = false;
                flight.traceNextAiDrive = false;
                if (flight.traceNextCombatDrive)
                {
                    flight.traceNextCombatDrive = false;
                    if (DeltaVLog.Enabled(DeltaVLogModule.Development))
                    {
                        ShipBase ship = ShipRegistry.ShipByIdentity(flight.subject);
                        DeltaVLog.Message(DeltaVLogModule.Development,
                            $"combat-drive ship={flight.subject} "
                            + $"{UniverseManager.MapTag(ship?.Map)} result={result} "
                            + $"phase={flight.phase} recovery={flight.recovery.Active} "
                            + $"posError={posErr.magnitude:F2} speedError={vel.magnitude:F2} "
                            + $"angleError={angleErr * Mathf.Rad2Deg:F1}deg "
                            + $"omega={pose.angularVelocity:F3} targetOmega={hostOmega:F3} "
                            + $"localForce={control.force} torque={control.torque:F2}/{torque:F2} "
                            + $"deltaAngle={SpaceTransform.WrapAngle(container.pose.angle - pose.angle) * Mathf.Rad2Deg:F3}deg "
                            + $"deltaV={container.pose.velocity - pose.velocity} "
                            + $"mpc={flight.heldCommand.qpStatus} "
                            + $"steps={flight.currentPlanStep}/{flight.planStepsReady}");
                    }
                }
            }
        }

        /// <summary>执行一次 MPC 求解（微步边界或首次/重解），并把解登记为可回放计划。
        /// elapsedSteps = 自上次求解以来已消费的微步数，供热启动平移对齐时间轴；
        /// previous* 传上一微步实际应用的指令（回放时即上一计划步），保持变化率代价连续。</summary>
        private void Solve(int ownerId, Flight flight, DriveInfo report, SpacePose pose,
            Vector2 posErr, Vector2 vel, float angleErr, float omega, float voidBlend,
            int solvePeriod, float trajectorySeconds, float combatReferenceTime,
            Vector3 referencePosition, Vector2 referenceVelocity, float referenceAngle,
            float referenceAngularVelocity)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            long solveStart = universe?.Performance.Start() ?? 0L;
            // The force model accelerates the COM. Task references remain map-origin poses;
            // convert both actual and desired states at the shared transform boundary.
            if (report.comLocal != Vector2.zero)
            {
                SpacePose center = SpaceTransform.RigidPointPose(pose, report.comLocal, 0f);
                SpacePose desiredCenter = SpaceTransform.RigidPointPose(new SpacePose(referencePosition, referenceAngle)
                    { velocity = referenceVelocity, angularVelocity = referenceAngularVelocity }, report.comLocal, 0f);
                referencePosition = desiredCenter.position;
                referenceVelocity = desiredCenter.velocity;
                posErr = -OrbitSphere.Offset(referencePosition, center.position);
                vel = SpaceTransform.TransportVelocity(center, referencePosition) - referenceVelocity;
            }
            ShipPhysics.ControlModel controlModel = report.PreviewModel(flight.controlModel);
            BuildControlAcceleration(pose, controlModel, voidBlend, flight.controlAcceleration, referencePosition);
            float boostSeconds = flight.intercept ? report.BoostSecondsRemaining : 0f;
            if (boostSeconds > 0f)
            {
                report.PreviewUnboostedModel(flight.unboostedModel);
                BuildControlAcceleration(pose, flight.unboostedModel, 0f, flight.unboostedAcceleration, referencePosition);
            }
            Vector2 arrivalVelocity = !flight.motionOnly && flight.arrivalApproachSpeed > 0f
                && posErr.sqrMagnitude > 1e-8f
                ? posErr.normalized * flight.arrivalApproachSpeed : Vector2.zero;
            float excessClosing = posErr.sqrMagnitude > 1e-8f
                ? Mathf.Max(0f, Vector2.Dot(vel - arrivalVelocity, posErr.normalized)) : 0f;
            var request = new GuidanceRequest
            {
                positionError = posErr,
                motionOnly = flight.motionOnly,
                intercept = flight.intercept,
                accuracy = flight.combatOrbit || flight.intercept || flight.motionOnly ? GuidanceAccuracy.Strict
                    : flight.phase == ApproachPhase.Cruise ? GuidanceAccuracy.Cruise
                    : flight.phase == ApproachPhase.Braking ? GuidanceAccuracy.Braking : GuidanceAccuracy.Strict,
                boostSeconds = boostSeconds,
                unboostedModel = boostSeconds > 0f ? flight.unboostedModel : null,
                unboostedAcceleration = flight.unboostedAcceleration,
                relativeVelocity = vel,
                arrivalVelocity = arrivalVelocity,
                angleError = angleErr,
                relativeAngularVelocity = omega,
                dt = GuidanceMicroStepTicks / 60f,
                voidBlend = voidBlend,
                elapsedSteps = flight.hasCommand ? flight.currentPlanStep + 1 : 1,
                previousX = flight.hasCommand ? flight.heldCommand.normalizedX : 0.0,
                previousZ = flight.hasCommand ? flight.heldCommand.normalizedZ : 0.0,
                previousAngular = flight.hasCommand ? flight.heldCommand.normalizedAngular : 0.0,
                controlModel = controlModel,
                controlAcceleration = flight.controlAcceleration,
                brakingAcceleration = flight.brakingAcceleration,
                trajectorySeconds = trajectorySeconds,
                combatTrajectory = flight.combatIntent,
                referenceStartSeconds = combatReferenceTime,
                referencePosition = referencePosition,
                referenceVelocity = referenceVelocity,
                referenceAngle = referenceAngle,
                referenceAngularVelocity = referenceAngularVelocity,
                referenceLocalPoint = report.comLocal,
                approachReserveDistance = flight.phase == ApproachPhase.Braking
                    && posErr.sqrMagnitude > 1e-8f
                    ? excessClosing * (solvePeriod - 1) * GuidanceMicroStepTicks / 60f
                    : 0.0
            };
            bool wasRecovering = flight.recovery.Active;
            bool solved = flight.recovery.Compute(flight.guidance, flight.profile, request, report, pose,
                Find.TickManager.TicksGame, flight.recoveryRecheck,
                !flight.motionOnly && !flight.intercept && !flight.combatOrbit,
                out flight.heldCommand, out GuidanceCommand approach);
            flight.lastSolvedStatus = flight.heldCommand.qpStatus;
            if (wasRecovering != flight.recovery.Active)
            {
                flight.noProgressTicks = 0;
                if (flight.recovery.Active)
                    flight.recoveryBestSpeed = vel.magnitude + Mathf.Abs(omega) * 5f;
                else
                    flight.bestError = posErr.magnitude + Mathf.Abs(angleErr) * 10f;
            }
            solvePeriod = flight.recovery.Active ? FarSolvePeriodSteps : SolvePeriodFor(flight.phase);
            flight.recoveryRecheck = false;
            if (wasRecovering != flight.recovery.Active)
            {
                SpaceObject subject = flight.subject.Resolve(universe);
                Log.Message($"[DeltaV] MPC braking recovery {(flight.recovery.Active ? "entered" : "exited")}: "
                    + $"structure=#{ownerId} object=#{subject?.objectId} label='{subject?.label}' "
                    + $"reason={flight.recovery.Reason} distance={posErr.magnitude:F2} relativeSpeed={vel.magnitude:F2} "
                    + $"braking={flight.brakingAcceleration:G6} approachStatus="
                    + (approach.qpCalls == 0 && approach.infeasiblePrechecks == 0 ? "Skipped" : approach.qpStatus.ToString()) + ".");
            }
            flight.hasCommand = true;
            flight.currentPlanStep = 0;
            flight.planStartTick = Find.TickManager.TicksGame;
            flight.planStepsReady = solved ? solvePeriod : 1; // 回退无计划；恢复中下一微步只更新反馈，QP 受重试节拍约束
            AutopilotMpcDiagnostics.Record(flight.heldCommand, solvePeriod);
            if (!solved && !flight.failureLogged)
            {
                flight.failureLogged = true;
                SpaceObject failedContainer = flight.subject.Resolve(universe);
                string task = flight.combatOrbit ? "combat-orbit" : flight.intercept ? "intercept"
                    : flight.motionOnly ? "velocity-sync" : flight.reference != null ? "reference"
                    : flight.tracked ? "tracked" : "waypoint";
                Log.Warning($"[DeltaV] MPC solve degraded; braking fallback active for structure #{ownerId} "
                    + $"object=#{failedContainer?.objectId} label='{failedContainer?.label}' "
                    + $"task={task} phase={flight.phase} npcRetreating={failedContainer?.npcRetreating} "
                    + $"host=#{flight.hostId} "
                    + $"distance={posErr.magnitude:F2} relativeSpeed={vel.magnitude:F2} "
                    + $"angleError={angleErr * Mathf.Rad2Deg:F1}° braking={flight.brakingAcceleration:F3} "
                    + $"modelVersion={report.version} solve={flight.heldCommand.solveMilliseconds:F2}ms "
                    + $"qp={flight.heldCommand.qpCalls} calls/{flight.heldCommand.qpIterations} "
                    + $"of {flight.heldCommand.qpIterationLimit} iterations "
                    + $"constraints={flight.heldCommand.qpConstraints} status={flight.heldCommand.qpStatus} "
                    + $"gap={flight.heldCommand.relativeOptimalityGap:G4} "
                    + $"primal={flight.heldCommand.primalResidual:F3} dual={flight.heldCommand.dualResidual:F3}.");
            }
            else if (solved && flight.failureLogged)
            {
                flight.failureLogged = false;
            }
            universe?.Performance.RecordAutopilotSolve(solveStart, solved, flight.heldCommand);
        }

        internal static void BuildControlAcceleration(SpacePose pose, ShipPhysics.ControlModel model,
            float voidBlend, double[] result, Vector3? frameDirection = null)
        {
            Array.Clear(result, 0, result.Length);
            if (model == null || model.mass <= 1e-6f || model.inertia <= 1e-6f) return;
            float desiredX = (model.positiveX + model.negativeX) * 0.5f;
            float desiredZ = (model.positiveZ + model.negativeZ) * 0.5f;
            float desiredMoment = (model.positiveTorque + model.negativeTorque) * 0.5f;
            for (int axis = 0; axis < 3; axis++)
            {
                Vector2 desiredForce = axis == 0
                    ? new Vector2(desiredX, 0f)
                    : (axis == 1 ? new Vector2(0f, desiredZ) : Vector2.zero);
                float desiredTorque = axis == 2 ? desiredMoment : 0f;
                Vector2 appliedForce = Vector2.Lerp(model.forceColumns[axis], desiredForce, voidBlend);
                float appliedTorque = Mathf.Lerp(model.torqueColumns[axis], desiredTorque, voidBlend);
                Vector2 targetFrameAcceleration = SpaceTransform.Rotate(appliedForce / model.mass, pose.angle);
                if (frameDirection.HasValue && frameDirection.Value.sqrMagnitude > 0f)
                    targetFrameAcceleration = SpaceTransform.TransportTangent(targetFrameAcceleration,
                        pose.position, frameDirection.Value);
                result[axis] = targetFrameAcceleration.x;
                result[3 + axis] = targetFrameAcceleration.y;
                result[6 + axis] = appliedTorque / model.inertia;
            }
        }

        private void Fail(SpaceObject container, Flight flight, string reason)
        {
            if (DeltaVLog.Enabled(DeltaVLogModule.AI) && flight.combatOrbit)
                DeltaVLog.Message(DeltaVLogModule.AI, $"drive-failed tick={Find.TickManager.TicksGame} ship={flight.subject} reason='{reason}'");
            active.Remove(flight.subject);
            container?.ClearDrive();
            flight.onFailed?.Invoke();
            flight.onFailedWithReason?.Invoke(reason);
        }

        private void Arrive(SpaceObject container, Flight flight, SpacePose reference)
        {
            active.Remove(flight.subject);
            container.SettleDrive(reference);
            flight.onArrived?.Invoke();
        }

        /// <summary>同实例上的流程清零（双船场景重建用；读档路径 = 组件
        /// 重建，天然无需调用）。静默清空、不回调。</summary>
        public void ResetFlow()
        {
            foreach (var subject in active.Keys) subject.Resolve(Current.Game?.GetComponent<UniverseManager>())?.ClearDrive();
            active.Clear();
        }
    }
}
