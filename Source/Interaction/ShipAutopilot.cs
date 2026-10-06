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
    /// 出力架构（刚体动量管理）：可替换 IAutopilotGuidance 制导器当前使用六状态在线 MPC，
    /// 只产生期望力/力矩；经 ShipPhysics.ForceToCommand 归一化后由联合分配器 Allocate 求解
    /// **真实**逐推进器油门与可达力/力矩，积分使用可达值——机动受推进器布局硬约束，
    /// 玩家船/敌舰/未来舰船 AI 完全同构，火焰表现层读到的油门即真实出力。制导按微步
    /// （6 tick = 0.1 s）节拍：巡航/制动/精确对位分别每 4/2/1 微步求解
    /// （多船按 ownerId 确定性错峰），解之间逐微步**回放计划控制**
    /// （TryGetPlannedStep）而非持有首控制量——2026-09-25 定案：10 Hz 全速求解实测
    /// 拖垮 6x 倍速（360 tps 下单 tick 预算 2.78 ms，单次求解 avg ~3 ms）。
    /// 停靠级任务（allowVoidThrust）在终段按距离渐变叠加虚空动力补齐
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
        private enum ApproachPhase { Cruise, Braking, Precision }

        private class Flight
        {
            public int ownerId;
            public bool playerInitiated;
            /// <summary>shuttle 舱体（MiniShip 任务；null = 船任务）。身份解析不走 ShipByOwner。</summary>
            public Building shuttleBody;
            /// <summary>满推追踪（侵入式对接，§7.5）：无刹车包络、全程全速追目标当前位姿（与导弹同律）。</summary>
            public bool pursue;
            public bool combatOrbit;
            public float orbitRadius;
            public float orbitPhase;
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
            // 计划回放：currentPlanStep = 当前应用的计划步（0 = 求解当步）；
            // planStepsReady = 上一解可回放的微步数（数值回退解为 1，无可回放计划）。
            public int currentPlanStep;
            public int planStepsReady;
            // 微步内应用路径去重缓存：指令与质量报告版本未变时跳过重复
            // ForceToCommand/Allocate（同输入确定性同输出）。
            public bool hasAllocation;
            public ShipPhysics.Command cachedCommand;
            public Vector2 cachedDesiredForce;
            public float cachedDesiredTorque;
            public Vector2 cachedRealForce;
            public float cachedRealTorque;
            public int cachedReportVersion;
            public int cachedMicroStep;
            public AutopilotMpcProfile profile;
            public float bestError = float.PositiveInfinity;
            public int noProgressTicks;
            public bool failureLogged;
            public ApproachPhase phase;
            public float brakingAcceleration;
            public float stoppingDistance;
            public int safeFarTicks;
            public int previewReportVersion = -1;
        }

        private readonly Dictionary<int, Flight> active = new Dictionary<int, Flight>();

        private readonly List<int> tmpShips = new List<int>();

        // 本 tick 活跃容器集合（Tick 内随 Step 解析容器顺带构建）：积分循环的
        // IsActive(SpaceObject) 跳过判据由此变 O(1)，替代旧的「每对象 × 每任务」线性扫描。
        private readonly HashSet<SpaceObject> activeContainers = new HashSet<SpaceObject>();
        private int activeContainersTick = -1;

        private AutopilotMpcProfile profile = AutopilotMpcProfile.Balanced;

        internal AutopilotMpcProfile Profile => profile;

        internal AutopilotMpcProfile CycleProfile()
        {
            profile = (AutopilotMpcProfile)(((int)profile + 1) % 3);
            return profile;
        }

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

        /// <summary>动态环绕任务：只调整同一任务的半径，不反复重建 MPC；始终使用真实推力。</summary>
        public void TrackOrbit(Ship ship, SpaceObject host, float radius)
        {
            if (!ship.IsValid || host == null) return;
            radius = Mathf.Max(radius, SpaceCollision.BoundsRadiusOf(host) + 8f);
            if (active.TryGetValue(ship.ownerId, out Flight existing)
                && existing.combatOrbit && existing.host == host)
            {
                existing.orbitRadius = radius;
                return;
            }
            SpaceObject own = Current.Game?.GetComponent<UniverseManager>()?.FrameOf(ship.map);
            if (own == null) return;
            Vector2 local = SpaceTransform.UniverseToShipLocal(host, own.pose.position);
            var flight = new Flight
            {
                ownerId = ship.ownerId, tracked = true, host = host, combatOrbit = true,
                orbitRadius = radius, orbitPhase = Mathf.Atan2(local.y, local.x),
                allowVoidThrust = false, playerInitiated = false, profile = profile
            };
            flight.guidance.Reset(profile);
            active[ship.ownerId] = flight;
        }

        /// <summary>PilotTarget 口径（shuttle MiniShip 任务同表；键 = ControlKey）。</summary>
        public void Detach(PilotTarget target, string reason = null)
        {
            if (target.IsShuttle)
            {
                if (!active.ContainsKey(target.ControlKey)) return;
                active.Remove(target.ControlKey);
                if (target.Map != null) ShipPhysics.ClearThrottles(target.Map);
                if (reason != null)
                {
                    DeltaVLog.Message(DeltaVLogModule.Core, $"autopilot detached ({reason}): shuttle #{target.ControlKey}");
                }
                return;
            }
            Detach(target.ship, reason);
        }

        /// <summary>TrackTo 的 PilotTarget 口径（shuttle 对接计划随动）。</summary>
        public void TrackTo(PilotTarget target, SpaceObject host, Vector2 hostOffset,
            float relativeAngle, Action onFailed = null, bool allowVoidThrust = true,
            bool playerInitiated = true)
        {
            if (!target.IsShuttle)
            {
                TrackTo(target.ship, host, hostOffset, relativeAngle, onFailed, allowVoidThrust, playerInitiated);
                return;
            }
            var flight = new Flight
            {
                ownerId = target.ControlKey,
                playerInitiated = playerInitiated,
                shuttleBody = target.shuttle,
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
            active[flight.ownerId] = flight;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"autopilot track engaged: shuttle #{flight.ownerId} -> '{host.label}' voidThrust={allowVoidThrust}");
        }

        /// <summary>
        /// 满推追踪任务（侵入式对接，§7.5）：shuttle 容器 tag 切换为 projectile 后由
        /// Shuttles 服务发起——无刹车包络、全程全速追目标容器当前位姿（与导弹同律）。
        /// 命中判据不在本任务内（碰撞核心结算）；目标失效按 onFailed 退出。
        /// </summary>
        public void Pursue(Building shuttleBody, SpaceObject target, Action onFailed = null, bool playerInitiated = true)
        {
            if (shuttleBody == null || target == null)
            {
                onFailed?.Invoke();
                return;
            }
            var flight = new Flight
            {
                ownerId = shuttleBody.thingIDNumber,
                playerInitiated = playerInitiated,
                shuttleBody = shuttleBody,
                pursue = true,
                tracked = true,
                host = target,
                elapsed = 0f,
                allowVoidThrust = false,
                onFailed = onFailed,
            };
            flight.profile = profile;
            active[flight.ownerId] = flight;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"autopilot pursue engaged: shuttle #{flight.ownerId} -> '{target.label}' (full thrust, no braking envelope)");
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

        /// <summary>容器当前是否被任一自动驾驶任务驱动（tick 积分循环的跳过判据）。
        /// 优先查本 tick 由 Tick()/Step 构建的容器集合（O(1)）；集合缺失时回退线性扫描。</summary>
        public bool IsActive(SpaceObject container)
        {
            if (container == null || active.Count == 0) return false;
            if (activeContainersTick == Find.TickManager.TicksGame)
            {
                return activeContainers.Contains(container);
            }
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null) return false;
            foreach (KeyValuePair<int, Flight> pair in active)
            {
                if (pair.Value.shuttleBody != null)
                {
                    if (pair.Value.shuttleBody.Spawned
                        && universe.FrameOf(pair.Value.shuttleBody.Map) == container) return true;
                    continue;
                }
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
            // 重建本 tick 活跃容器集合（Step 解析容器时登记；先于物理积分循环消费）。
            activeContainers.Clear();
            activeContainersTick = Find.TickManager.TicksGame;
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
            Map bodyMap;
            if (flight.shuttleBody != null)
            {
                // MiniShip 任务：身份 = 舱体（无主台/归属，不走 ShipByOwner）。
                if (universe == null || !flight.shuttleBody.Spawned || flight.shuttleBody.Map == null
                    || flight.shuttleBody.Map.Disposed)
                {
                    active.Remove(ownerId);
                    DeltaVLog.Message(DeltaVLogModule.Core, $"autopilot failed (shuttle lost): shuttle #{ownerId}");
                    flight.onFailed?.Invoke();
                    return;
                }
                bodyMap = flight.shuttleBody.Map;
            }
            else if (!ship.IsValid || ship.map == null || ship.map.Disposed || universe == null)
            {
                active.Remove(ownerId);
                if (ship.IsValid && ship.map != null) ShipPhysics.ClearThrottles(ship.map);
                DeltaVLog.Message(DeltaVLogModule.Core, $"autopilot failed (ship lost): structure #{ownerId}");
                flight.onFailed?.Invoke();
                return;
            }
            else
            {
                bodyMap = ship.map;
            }
            if (flight.tracked && flight.host == null)
            {
                active.Remove(ownerId);
                ShipPhysics.ClearThrottles(bodyMap);
                DeltaVLog.Message(DeltaVLogModule.Core, $"autopilot failed (host lost): structure #{ownerId}");
                flight.onFailed?.Invoke();
                return;
            }
            SpaceObject container = universe.FrameOf(bodyMap);
            if (container == null)
            {
                active.Remove(ownerId);
                ShipPhysics.ClearThrottles(bodyMap);
                DeltaVLog.Message(DeltaVLogModule.Core, $"autopilot failed (container lost): structure #{ownerId}");
                flight.onFailed?.Invoke();
                return;
            }
            activeContainers.Add(container);

            const float dt = 1f / 60f; // 游戏 tick 时间；绝不使用帧时钟（AGENTS §18 #20）
            flight.elapsed += 1;
            ShipPhysics.MassReport report = ShipPhysics.GetReport(container.map);
            if (report == null) return;

            if (flight.pursue)
            {
                // 满推追踪（§7.5）：无刹车包络、全程全速追目标当前位姿——不走 MPC
                //（软接近包络必刹车），但仍经 RequestThrust gate 与同一分配器（机动
                // 受推进器布局硬约束）。命中判据归碰撞核心。
                Vector2 pursueErr = OrbitSphere.Offset(container.pose.position, flight.host.pose.position);
                if (pursueErr.sqrMagnitude < 1e-8f) pursueErr = Vector2.right;
                Vector2 mapDir = SpaceTransform.Rotate(pursueErr.normalized, container.pose.angle);
                var pursueCommand = new ShipPhysics.Command
                {
                    fx = mapDir.x,
                    fz = mapDir.y,
                    torque = Mathf.Clamp(SpaceTransform.WrapAngle(flight.host.pose.angle - container.pose.angle) * 2f, -1f, 1f),
                };
                PilotTarget pursueTarget = flight.shuttleBody != null
                    ? PilotTarget.Of(flight.shuttleBody)
                    : PilotTarget.Of(ship);
                if (universe.ShipTransitions.RequestThrust(universe, pursueTarget, flight.playerInitiated))
                {
                    ShipPhysics.Allocate(report, pursueCommand, out Vector2 pursueForce, out float pursueTorque);
                    ShipPhysics.Integrate(container, report, pursueForce, pursueTorque, dt, false, false, false);
                }
                else
                {
                    ShipPhysics.Integrate(container, report, Vector2.zero, 0f, dt, false, false, false);
                }
                return;
            }

            // 目标位姿与宿主前馈：误差/速度统一表达在目标锚点的切空间基下（球面几何
            // 唯一入口 OrbitSphere）。随动模式取宿主刚体目标点速度（含 ω×offset），
            // MPC 在 0.1 s 后重新观测；预测期内按恒速/恒角速度目标处理。
            Vector3 targetDirection;
            float targetAngle;
            Vector2 hostVelocity = Vector2.zero;
            float hostOmega = 0f;
            if (flight.tracked)
            {
                if (flight.combatOrbit)
                {
                    const float orbitAngularRate = 0.03f;
                    flight.orbitPhase = SpaceTransform.WrapAngle(flight.orbitPhase
                        + orbitAngularRate * dt);
                    flight.hostOffset = new Vector2(Mathf.Cos(flight.orbitPhase),
                        Mathf.Sin(flight.orbitPhase)) * flight.orbitRadius;
                }
                targetDirection = SpaceTransform.FocusedToUniverse(flight.host, flight.hostOffset);
                targetAngle = flight.combatOrbit
                    ? SpaceTransform.WrapAngle(Mathf.Atan2(
                        OrbitSphere.Offset(targetDirection, flight.host.pose.position).y,
                        OrbitSphere.Offset(targetDirection, flight.host.pose.position).x)
                        - PilotTarget.Of(ship).FrameAngle)
                    : SpaceTransform.WrapAngle(flight.host.pose.angle + flight.relativeAngle);
                Vector2 orbitVelocity = flight.combatOrbit
                    ? new Vector2(-flight.hostOffset.y, flight.hostOffset.x) * 0.03f
                    : Vector2.zero;
                hostVelocity = SpaceTransform.VelocityAtMovingLocalPoint(
                    flight.host.pose, flight.hostOffset, orbitVelocity, targetDirection);
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
                flight.hasCommand = false;
                flight.planStepsReady = 0;
            }
            // 制导微步节拍（2026-09-25 定案）：QP 按 K 个微步解一次（K 按距离自适应、
            // ownerId 确定性错峰），中间微步回放上一解的计划控制；计划耗尽或数值回退
            // 时立即重解恢复反馈节奏。微步内指令恒定，应用侧由此可安全去重（见下）。
            int ticksGame = Find.TickManager.TicksGame;
            bool microBoundary = ticksGame % GuidanceMicroStepTicks == 0;
            bool phaseChanged = false;
            bool unsafePlan = false;
            bool reportChanged = report.version != flight.previewReportVersion;
            if (!flight.hasCommand || microBoundary || reportChanged)
            {
                flight.previewReportVersion = report.version;
                Vector2 towardLocal = posErr.sqrMagnitude > 1e-8f
                    ? SpaceTransform.RotateInverse(posErr.normalized, pose.angle)
                    : Vector2.zero;
                flight.brakingAcceleration = ShipPhysics.PreviewBrakingAcceleration(report, towardLocal);
                float closing = posErr.sqrMagnitude > 1e-8f
                    ? Mathf.Max(0f, Vector2.Dot(vel, posErr.normalized)) : 0f;
                double safeAcceleration = flight.brakingAcceleration * BrakingGuidanceFallback.SafetyFactor;
                double stopping = BrakingGuidanceFallback.StoppingDistance(closing, safeAcceleration);
                flight.stoppingDistance = double.IsInfinity(stopping)
                    ? float.PositiveInfinity : (float)stopping;
                // 巡航计划最多再回放四个微步；从切换判据中扣除这段闭合距离。
                float remaining = posErr.magnitude;
                float brakeMargin = flight.stoppingDistance + closing *
                    (FarSolvePeriodSteps * GuidanceMicroStepTicks / 60f);
                unsafePlan = flight.brakingAcceleration > 1e-6f
                    && closing > 0.15f
                    && remaining <= flight.stoppingDistance
                        + closing * (GuidanceMicroStepTicks / 60f);
                ApproachPhase next = remaining <= TightSolveDistance
                    ? ApproachPhase.Precision
                    : remaining <= brakeMargin || flight.brakingAcceleration <= 1e-6f
                        ? ApproachPhase.Braking : ApproachPhase.Cruise;
                if (flight.phase == ApproachPhase.Precision
                    && remaining <= 2f * TightSolveDistance)
                    next = ApproachPhase.Precision;
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
                if (next != flight.phase)
                {
                    flight.phase = next;
                    phaseChanged = true;
                    if (DeltaVLog.Enabled(DeltaVLogModule.Physics))
                        DeltaVLog.Message(DeltaVLogModule.Physics,
                            $"autopilot phase: #{ownerId} {next} d={remaining:F1} closing={closing:F2} "
                            + $"brake={flight.brakingAcceleration:F3} stop={flight.stoppingDistance:F1}");
                }
            }
            if (!flight.hasCommand)
            {
                Solve(ownerId, flight, report, pose, posErr, vel, angleErr, omega, voidBlend,
                    SolvePeriodFor(flight.phase));
            }
            else if (microBoundary || reportChanged)
            {
                int solvePeriod = SolvePeriodFor(flight.phase);
                if (reportChanged || phaseChanged || unsafePlan
                    || SolveDue(ownerId, ticksGame / GuidanceMicroStepTicks, solvePeriod))
                {
                    Solve(ownerId, flight, report, pose, posErr, vel, angleErr, omega, voidBlend, solvePeriod);
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
                    Solve(ownerId, flight, report, pose, posErr, vel, angleErr, omega, voidBlend, solvePeriod);
                }
            }

            // 刚体动量管理：期望力/力矩经归一化后由联合分配器求真实油门与可达出力，
            // 积分使用可达值——机动受推进器布局硬约束，火焰表现层读到的油门即真实出力。
            // 微步内去重：指令与质量报告版本都未变时直接复用上一次 ForceToCommand/
            // Allocate 结果（同输入确定性同输出，油门值无需重写）；微步边界、报告重扫
            // （version 变化）或指令变化即失效——推进器故障的建模延迟因此 ≤ 1 微步。
            Vector2 desiredForce = flight.heldCommand.desiredLocalForce;
            float desiredTorque = flight.heldCommand.desiredTorque;
            int microStep = ticksGame / GuidanceMicroStepTicks;
            bool allocationCached = flight.hasAllocation
                && flight.cachedMicroStep == microStep
                && flight.cachedReportVersion == report.version
                && flight.cachedDesiredForce == desiredForce
                && flight.cachedDesiredTorque == desiredTorque;
            ShipPhysics.Command command = allocationCached
                ? flight.cachedCommand
                : ShipPhysics.ForceToCommand(report, desiredForce, desiredTorque);

            // 出力前分离 gate（§7 脱离 + §8 拆分统一）：将要施加推进器出力时先过
            // 「移动的船与其余部分分离」检查——玩家确认/排队分离在那一入口收口；
            // 被拒绝的 tick 不施力。
            bool wantsThrust = Mathf.Abs(command.fx) > 1e-6f || Mathf.Abs(command.fz) > 1e-6f
                || Mathf.Abs(command.torque) > 1e-6f;
            PilotTarget thrustTarget = flight.shuttleBody != null
                ? PilotTarget.Of(flight.shuttleBody)
                : PilotTarget.Of(ship);
            if (wantsThrust
                && !universe.ShipTransitions.RequestThrust(universe, thrustTarget, flight.playerInitiated))
            {
                return; // 本 tick 不施加推进器动量（确认窗暂停游戏，拆分后下一 tick 继续）。
            }

            Vector2 realForce;
            float realTorque;
            if (allocationCached)
            {
                realForce = flight.cachedRealForce;
                realTorque = flight.cachedRealTorque;
            }
            else
            {
                // 零指令也照常 Allocate：把油门清零，火焰随之熄灭（不能因去重跳过首次写）。
                ShipPhysics.Allocate(report, command, out realForce, out realTorque);
                flight.hasAllocation = true;
                flight.cachedMicroStep = microStep;
                flight.cachedReportVersion = report.version;
                flight.cachedDesiredForce = desiredForce;
                flight.cachedDesiredTorque = desiredTorque;
                flight.cachedCommand = command;
                flight.cachedRealForce = realForce;
                flight.cachedRealTorque = realTorque;
            }

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

        /// <summary>执行一次 MPC 求解（微步边界或首次/重解），并把解登记为可回放计划。
        /// elapsedSteps = 自上次求解以来已消费的微步数，供热启动平移对齐时间轴；
        /// previous* 传上一微步实际应用的指令（回放时即上一计划步），保持变化率代价连续。</summary>
        private void Solve(int ownerId, Flight flight, ShipPhysics.MassReport report, SpacePose pose,
            Vector2 posErr, Vector2 vel, float angleErr, float omega, float voidBlend, int solvePeriod)
        {
            ShipPhysics.ControlModel controlModel = ShipPhysics.BuildControlModel(report, flight.controlModel);
            BuildControlAcceleration(pose, controlModel, voidBlend, flight.controlAcceleration);
            var request = new GuidanceRequest
            {
                positionError = posErr,
                relativeVelocity = vel,
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
                approachReserveDistance = flight.phase == ApproachPhase.Braking
                    && posErr.sqrMagnitude > 1e-8f
                    ? Mathf.Max(0f, Vector2.Dot(vel, posErr.normalized))
                        * (solvePeriod - 1) * GuidanceMicroStepTicks / 60f
                    : 0.0
            };
            bool solved = flight.guidance.Compute(request, out flight.heldCommand);
            flight.hasCommand = true;
            flight.currentPlanStep = 0;
            flight.planStepsReady = solved ? solvePeriod : 1; // 数值回退无计划：下一微步重解
            AutopilotMpcDiagnostics.Record(flight.heldCommand, solvePeriod);
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
