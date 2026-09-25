using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 轨道层通用自动驾驶（纯算法，2026-09-21 阶段4 定案：与对接完成语义解耦；2026-09-24
    /// 解耦重构：任务按结构归属 ID 键控，物理作用于船的**容器**——自由飞行船的容器即
    /// 其刚体，§5.3「每个对象只积分一次」）。两类目标——
    /// - 固定航点（FlyTo）：球面方向 + 宇宙角，物理收敛后精确对位并发完成信号；
    ///   超时/无进展只失败、不瞬移；未来巡航/舰船 AI 用；
    /// - 随动目标（TrackTo）：宿主 SpaceObject ⊕ 局部偏移 + 相对转角，每 tick 随宿主位姿
    ///   重算；宿主速度/角速度经**前馈匹配**，到位后**站位保持**直到取消——不自动到达、
    ///   无超时兜底、无完成回调。完成判据归调用方（对接计划 = 几何窗口 + 保持 tick）。
    ///
    /// 出力架构（刚体动量管理）：可替换 IAutopilotGuidance 制导器当前使用 10 Hz
    /// 六状态在线 MPC，只产生期望力/力矩；经 ShipPhysics.ForceToCommand 归一化后由联合
    /// 分配器 Allocate 求解**真实**逐推进器油门与可达力/力矩，积分使用可达值——机动受
    /// 推进器布局硬约束，玩家船/敌舰/未来舰船 AI 完全同构，火焰表现层读到的油门即
    /// 真实出力。停靠级任务（allowVoidThrust）在终段按距离渐变叠加虚空动力补齐
    /// 「期望 − 可达」差值；虚空动力不写油门、不进火焰。
    ///
    /// 出力前公共检查（解耦架构 §8）：将要施加推进器出力时先经
    /// ShipFlightTransitionService.RequestThrust——断连拆分/玩家确认在那一入口收口；
    /// 被拒绝的 tick 不施力（等待确认或拆分完成）。被动阻力与纯漂移不触发拆分。
    ///
    /// 自动驾驶不等于 NPC AI（§5.4）：自动驾驶负责执行机动任务及制导，玩家可以使用
    /// （对接计划），NPC AI 经共同驾驶入口调用；playerInitiated 标记本次任务的确认
    /// 归属（玩家任务弹拆分确认，NPC 任务直接执行公共拆分）。
    ///
    /// tick 驱动（UniverseManager.GameComponentTick，每 tick = 1/60 游戏秒）：
    /// **SpaceObject 的运动只属于游戏时钟**——暂停 = 无 tick = 冻结，倍速 = 更多
    /// tick = 加速（AGENTS §18 不变量 #20）。任务为运行时数据不存档——本类是
    /// UniverseManager 持有的实例服务（universe.Autopilot）：读档 = 组件重建 = 任务表
    /// 自动归零；对接计划飞行期可正常存档（任务不落盘、读档即丢）。
    /// </summary>
    public class ShipAutopilot
    {
        private class Flight
        {
            public int ownerId;
            public bool playerInitiated;
            // 固定航点模式：direction/angle；随动模式：host ⊕ hostOffset（+ 相对转角）。
            public bool tracked;
            public Vector3 direction;
            public float angle;
            public SpaceObject host;
            public Vector2 hostOffset;
            public float relativeAngle;
            public float elapsed;
            public bool allowVoidThrust;
            public Action onArrived;
            public Action onFailed;
            public readonly IAutopilotGuidance guidance = new OnlineMpcGuidance();
            public readonly double[] controlAcceleration = new double[9];
            public readonly ShipPhysics.ControlModel controlModel = new ShipPhysics.ControlModel();
            public GuidanceCommand heldCommand;
            public bool hasCommand;
            public int ticksUntilSolve;
            public AutopilotMpcProfile profile;
            public float bestError = float.PositiveInfinity;
            public int noProgressTicks;
            public bool failureLogged;
        }

        private readonly Dictionary<int, Flight> active = new Dictionary<int, Flight>();

        private readonly List<int> tmpShips = new List<int>();

        private AutopilotMpcProfile profile = AutopilotMpcProfile.Balanced;

        internal AutopilotMpcProfile Profile => profile;

        internal AutopilotMpcProfile CycleProfile()
        {
            profile = (AutopilotMpcProfile)(((int)profile + 1) % 3);
            return profile;
        }

        private const int GuidancePeriodTicks = 6; // 10 Hz；积分仍为 60 Hz。

        /// <summary>虚空动力渐变半径（格）：外沿起效、进入吸附圈补满——只在停靠任务
        /// （allowVoidThrust）中补齐推进器布局无法形成的「期望 − 可达」差值，
        /// 远场仍是纯真实推进。</summary>
        private const float VoidRampRadius = 4.5f;

        private const float ArrivePositionEpsilon = 0.05f; // 格
        private const float ArriveVelocityEpsilon = 0.15f; // cells/s
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
        public void FlyTo(Ship ship, Vector3 targetDirection, float targetAngle,
            Action onArrived, Action onFailed = null, bool allowVoidThrust = true,
            bool playerInitiated = true)
        {
            if (!ship.IsValid)
            {
                onFailed?.Invoke();
                return;
            }
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"autopilot waypoint engaged: structure #{ship.ownerId} -> ({targetDirection.x:F2},{targetDirection.y:F2},{targetDirection.z:F2}) angle={targetAngle * Mathf.Rad2Deg:F0} deg voidThrust={allowVoidThrust}");
            var flight = new Flight
            {
                ownerId = ship.ownerId,
                playerInitiated = playerInitiated,
                tracked = false,
                direction = targetDirection.normalized,
                angle = SpaceTransform.WrapAngle(targetAngle),
                elapsed = 0f,
                allowVoidThrust = allowVoidThrust,
                onArrived = onArrived,
                onFailed = onFailed,
            };
            flight.profile = profile;
            flight.guidance.Reset(flight.profile);
            active[ship.ownerId] = flight;
        }

        /// <summary>
        /// 随动目标模式：把船持续对位到「宿主对象位姿 ⊕ 局部偏移」（宇宙角 = 宿主角 ⊕
        /// relativeAngle）的**移动**位姿上——目标每 tick 随宿主重算，宿主落点线速度
        /// （含 ω×offset）/角速度经前馈匹配，收敛后站位保持直到取消。不自动到达、无超时
        /// 兜底、无完成回调：完成判据归调用方。宿主失效按 onFailed 退出。
        /// </summary>
        public void TrackTo(Ship ship, SpaceObject host, Vector2 hostOffset,
            float relativeAngle, Action onFailed = null, bool allowVoidThrust = true,
            bool playerInitiated = true)
        {
            if (!ship.IsValid || host == null)
            {
                onFailed?.Invoke();
                return;
            }
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"autopilot track engaged: structure #{ship.ownerId} -> '{host.label}' offset=({hostOffset.x:F0},{hostOffset.y:F0}) relAngle={SpaceTransform.WrapAngle(relativeAngle) * Mathf.Rad2Deg:F0} deg voidThrust={allowVoidThrust}");
            var flight = new Flight
            {
                ownerId = ship.ownerId,
                playerInitiated = playerInitiated,
                tracked = true,
                host = host,
                hostOffset = hostOffset,
                relativeAngle = SpaceTransform.WrapAngle(relativeAngle),
                elapsed = 0f,
                allowVoidThrust = allowVoidThrust,
                onFailed = onFailed,
            };
            flight.profile = profile;
            flight.guidance.Reset(flight.profile);
            active[ship.ownerId] = flight;
        }

        /// <summary>
        /// 静默摘除任务（清除油门、不触发任何回调）：对接完成接管位姿、玩家手操打断
        /// 自动驾驶（不取消对接计划）、计划取消等场景使用；reason 记入日志（边沿事件，
        /// 只在实际摘除时输出）。需要失败信号反馈的退出走 Cancel。
        /// </summary>
        public void Detach(Ship ship, string reason = null)
        {
            if (!ship.IsValid || !active.ContainsKey(ship.ownerId)) return;
            active.Remove(ship.ownerId);
            if (ship.map != null) ShipPhysics.ClearThrottles(ship.map); // 绕过积分器的出口：显式熄火
            if (reason != null)
            {
                DeltaVLog.Message(DeltaVLogModule.Core, $"autopilot detached ({reason}): structure #{ship.ownerId}");
            }
        }

        public bool IsActive(int ownerId)
        {
            return ownerId > 0 && active.ContainsKey(ownerId);
        }

        /// <summary>容器当前是否被任一自动驾驶任务驱动（tick 积分循环的跳过判据）。</summary>
        public bool IsActive(SpaceObject container)
        {
            if (container == null || active.Count == 0) return false;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null) return false;
            foreach (KeyValuePair<int, Flight> pair in active)
            {
                Ship ship = ShipRegistry.ShipByOwner(pair.Key);
                if (ship.IsValid && universe.FrameOf(ship.map) == container) return true;
            }
            return false;
        }

        /// <summary>异常出口：按失败信号收尾并清除任务。</summary>
        public void Cancel(Ship ship)
        {
            if (!ship.IsValid || !active.TryGetValue(ship.ownerId, out Flight flight)) return;
            active.Remove(ship.ownerId);
            if (ship.map != null) ShipPhysics.ClearThrottles(ship.map); // 绕过积分器的出口：显式熄火
            flight.onFailed?.Invoke();
        }

        /// <summary>UniverseManager.GameComponentTick 每 tick 驱动（游戏时间语义：
        /// 暂停冻结、倍速加速）。</summary>
        public void Tick()
        {
            if (active.Count == 0) return;
            // 回调可能在迭代中增删任务（到达后立即发起新任务等），按快照迭代。
            tmpShips.Clear();
            foreach (KeyValuePair<int, Flight> pair in active) tmpShips.Add(pair.Key);
            foreach (int ownerId in tmpShips)
            {
                if (active.TryGetValue(ownerId, out Flight flight)) Step(ownerId, flight);
            }
        }

        private void Step(int ownerId, Flight flight)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            Ship ship = ShipRegistry.ShipByOwner(ownerId);
            if (!ship.IsValid || ship.map == null || ship.map.Disposed || universe == null)
            {
                active.Remove(ownerId);
                if (ship.IsValid && ship.map != null) ShipPhysics.ClearThrottles(ship.map);
                DeltaVLog.Message(DeltaVLogModule.Core, $"autopilot failed (ship lost): structure #{ownerId}");
                flight.onFailed?.Invoke();
                return;
            }
            if (flight.tracked && flight.host == null)
            {
                active.Remove(ownerId);
                ShipPhysics.ClearThrottles(ship.map);
                DeltaVLog.Message(DeltaVLogModule.Core, $"autopilot failed (host lost): structure #{ownerId}");
                flight.onFailed?.Invoke();
                return;
            }
            SpaceObject container = universe.FrameOf(ship.map);
            if (container == null)
            {
                active.Remove(ownerId);
                ShipPhysics.ClearThrottles(ship.map);
                DeltaVLog.Message(DeltaVLogModule.Core, $"autopilot failed (container lost): structure #{ownerId}");
                flight.onFailed?.Invoke();
                return;
            }

            const float dt = 1f / 60f; // 游戏 tick 时间；绝不使用帧时钟（AGENTS §18 #20）
            flight.elapsed += 1;
            ShipPhysics.MassReport report = ShipPhysics.GetReport(container.map);
            if (report == null) return;

            // 目标位姿与宿主前馈：误差/速度统一表达在目标锚点的切空间基下（球面几何
            // 唯一入口 OrbitSphere）。随动模式取宿主刚体目标点速度（含 ω×offset），
            // MPC 在 0.1 s 后重新观测；预测期内按恒速/恒角速度目标处理。
            Vector3 targetDirection;
            float targetAngle;
            Vector2 hostVelocity = Vector2.zero;
            float hostOmega = 0f;
            if (flight.tracked)
            {
                targetDirection = SpaceTransform.FocusedToUniverse(flight.host, flight.hostOffset);
                targetAngle = SpaceTransform.WrapAngle(flight.host.pose.angle + flight.relativeAngle);
                hostVelocity = SpaceTransform.VelocityAtLocalPoint(
                    flight.host.pose, flight.hostOffset, targetDirection);
                hostOmega = flight.host.pose.angularVelocity;
            }
            else
            {
                targetDirection = flight.direction;
                targetAngle = flight.angle;
            }
            SpacePose pose = container.pose;
            Vector2 posErr = -OrbitSphere.Offset(targetDirection, pose.position);
            Vector2 vel = SpaceTransform.TransportVelocity(pose, targetDirection) - hostVelocity;
            float angleErr = SpaceTransform.WrapAngle(targetAngle - pose.angle);
            float omega = pose.angularVelocity - hostOmega;

            if (!flight.tracked
                && posErr.sqrMagnitude < ArrivePositionEpsilon * ArrivePositionEpsilon
                && vel.sqrMagnitude < ArriveVelocityEpsilon * ArriveVelocityEpsilon
                && Mathf.Abs(angleErr) < ArriveAngleEpsilon
                && Mathf.Abs(omega) < ArriveOmegaEpsilon)
            {
                Arrive(container, flight);
                return;
            }

            if (!flight.tracked)
            {
                float progressMetric = posErr.magnitude + Mathf.Abs(angleErr) * 10f;
                if (progressMetric + 0.05f < flight.bestError)
                {
                    flight.bestError = progressMetric;
                    flight.noProgressTicks = 0;
                }
                else
                {
                    flight.noProgressTicks++;
                }
                if (flight.elapsed > MaxFlightTicks || flight.noProgressTicks > MaxNoProgressTicks)
                {
                    Fail(ship, flight, "waypoint timeout/no progress");
                    return;
                }
            }

            float voidBlend = flight.allowVoidThrust
                ? 1f - Mathf.Clamp01(posErr.magnitude / VoidRampRadius)
                : 0f;
            if (flight.profile != profile)
            {
                flight.profile = profile;
                flight.guidance.Reset(flight.profile);
                flight.ticksUntilSolve = 0;
                flight.hasCommand = false;
            }
            if (!flight.hasCommand || flight.ticksUntilSolve <= 0)
            {
                ShipPhysics.ControlModel controlModel = ShipPhysics.BuildControlModel(report, flight.controlModel);
                BuildControlAcceleration(pose, controlModel, voidBlend, flight.controlAcceleration);
                var request = new GuidanceRequest
                {
                    positionError = posErr,
                    relativeVelocity = vel,
                    angleError = angleErr,
                    relativeAngularVelocity = omega,
                    dt = GuidancePeriodTicks / 60f,
                    voidBlend = voidBlend,
                    previousX = flight.hasCommand ? flight.heldCommand.normalizedX : 0.0,
                    previousZ = flight.hasCommand ? flight.heldCommand.normalizedZ : 0.0,
                    previousAngular = flight.hasCommand ? flight.heldCommand.normalizedAngular : 0.0,
                    controlModel = controlModel,
                    controlAcceleration = flight.controlAcceleration
                };
                bool solved = flight.guidance.Compute(request, out flight.heldCommand);
                flight.hasCommand = true;
                flight.ticksUntilSolve = GuidancePeriodTicks;
                AutopilotMpcDiagnostics.Record(flight.heldCommand);
                if (!solved && !flight.failureLogged)
                {
                    flight.failureLogged = true;
                    Log.Warning($"[DeltaV] MPC solve degraded; braking fallback active for structure #{ownerId} "
                        + $"(primal={flight.heldCommand.primalResidual:F3}, dual={flight.heldCommand.dualResidual:F3}).");
                }
                else if (solved && flight.failureLogged)
                {
                    flight.failureLogged = false;
                    DeltaVLog.Message(DeltaVLogModule.Core,
                        $"MPC solve recovered: structure #{ownerId}");
                }
            }
            flight.ticksUntilSolve--;

            // 刚体动量管理：期望力/力矩经归一化后由联合分配器求真实油门与可达出力，
            // 积分使用可达值——机动受推进器布局硬约束，火焰表现层读到的油门即真实出力。
            Vector2 desiredForce = flight.heldCommand.desiredLocalForce;
            float desiredTorque = flight.heldCommand.desiredTorque;
            ShipPhysics.Command command = ShipPhysics.ForceToCommand(report, desiredForce, desiredTorque);

            // 出力前分离 gate（§7 脱离 + §8 拆分统一）：将要施加推进器出力时先过
            // 「移动的船与其余部分分离」检查——玩家确认/排队分离在那一入口收口；
            // 被拒绝的 tick 不施力。
            bool wantsThrust = Mathf.Abs(command.fx) > 1e-6f || Mathf.Abs(command.fz) > 1e-6f
                || Mathf.Abs(command.torque) > 1e-6f;
            if (wantsThrust && !universe.ShipTransitions.RequestThrust(universe, ship, flight.playerInitiated))
            {
                return; // 本 tick 不施加推进器动量（确认窗暂停游戏，拆分后下一 tick 继续）。
            }

            ShipPhysics.Allocate(report, command, out Vector2 realForce, out float realTorque);

            // 终段虚空动力：按距离渐变补齐「期望 − 可达」差值（远场 0 → 吸附圈 1），
            // 保证任意推进器布局都能精确对位；不写油门、不进火焰（表现层只见真实出力）。
            Vector2 appliedForce = realForce;
            float appliedTorque = realTorque;
            if (flight.allowVoidThrust)
            {
                appliedForce += (desiredForce - realForce) * voidBlend;
                appliedTorque += (desiredTorque - realTorque) * voidBlend;
            }
            ShipPhysics.Integrate(container, report, appliedForce, appliedTorque, dt, false, false, false);
        }

        private static void BuildControlAcceleration(SpacePose pose, ShipPhysics.ControlModel model,
            float voidBlend, double[] result)
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
                result[axis] = targetFrameAcceleration.x;
                result[3 + axis] = targetFrameAcceleration.y;
                result[6 + axis] = appliedTorque / model.inertia;
            }
        }

        private void Fail(Ship ship, Flight flight, string reason)
        {
            active.Remove(flight.ownerId);
            if (ship.IsValid && ship.map != null) ShipPhysics.ClearThrottles(ship.map);
            DeltaVLog.Message(DeltaVLogModule.Core, $"autopilot failed ({reason}): structure #{flight.ownerId}");
            flight.onFailed?.Invoke();
        }

        private void Arrive(SpaceObject container, Flight flight)
        {
            active.Remove(flight.ownerId);
            ShipPhysics.ClearThrottles(container.map); // 直接改位姿绕过积分器：显式熄火
            // 精确对位（仅固定航点模式）：到达即目标位姿、速度清零。
            container.pose.position = flight.direction;
            container.pose.angle = flight.angle;
            container.pose.velocity = Vector2.zero;
            container.pose.angularVelocity = 0f;
            flight.onArrived?.Invoke();
        }

        /// <summary>同实例上的流程清零（dev ResetObjects 重建场景用；读档路径 = 组件
        /// 重建，天然无需调用）。静默清空、不回调。</summary>
        public void ResetFlow()
        {
            active.Clear();
        }
    }
}
