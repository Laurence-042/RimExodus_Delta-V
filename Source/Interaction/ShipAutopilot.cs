using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 轨道层通用自动驾驶（纯算法，2026-09-21 阶段4 定案：与对接完成语义解耦）：把船
    /// 自动驾驶到目标位姿，两类目标——
    /// - 固定航点（FlyTo）：球面方向 + 宇宙角，到达（收敛或超时兜底）后精确对位并发
    ///   完成信号；未来巡航/舰船 AI 用；
    /// - 随动目标（TrackTo）：宿主 SpaceObject ⊕ 局部偏移 + 相对转角，每帧随宿主位姿
    ///   重算；宿主速度/角速度经**前馈匹配**（阻尼项取相对速度/相对角速度），到位后
    ///   **站位保持**直到取消——不自动到达、无超时兜底、无完成回调。完成判据归调用方
    ///   （对接计划 = 几何窗口 + 保持 tick），本类不感知对接。
    ///
    /// 出力架构（刚体动量管理）：制导回路（临界阻尼 PD）只产生期望力/力矩，
    /// 经 ShipPhysics.ForceToCommand 归一化后由联合分配器 Allocate 求解**真实**
    /// 逐推进器油门与可达力/力矩，积分使用可达值——机动受推进器布局硬约束，
    /// 玩家船/敌舰/未来舰船 AI 完全同构，火焰表现层读到的油门即真实出力。
    /// 停靠级任务（allowVoidThrust）在终段按距离渐变叠加虚空动力补齐「期望 − 可达」
    /// 差值，保证任意推进器布局都能精确对位；虚空动力不写油门、不进火焰。
    ///
    /// 语义纯轨道层：目标与内部误差全部经 OrbitSphere/SpaceTransform 表达，不接触
    /// 任何 Map/聚焦坐标——「地图放置位置/旋转 → 轨道位姿」的反向映射由调用方完成
    /// （战术对接见 LandingApproachController.TryConsume）。后续飞船 AI / 点击巡航等
    /// 可直接复用本能力（非停靠用途传 allowVoidThrust: false 获得纯真实动力）；
    /// 自动驾驶中的船由 UniverseManager.GameComponentTick 跳过正常积分，不会双重驱动。
    ///
    /// tick 驱动（UniverseManager.GameComponentTick，每 tick = 1/60 游戏秒）：
    /// **SpaceObject 的运动只属于游戏时钟**——暂停 = 无 tick = 冻结，倍速 = 更多
    /// tick = 加速（2026-09-21 用户定案，AGENTS §18 不变量 #20）。模拟路径禁止
    /// Time.deltaTime/帧驱动积分（硬造时间）；表现层（火焰/音频）只读真实时间、
    /// 不移动对象，不受此限。
    ///
    /// 积分复用 ShipPhysics.Integrate（球面三步走 + 平行输运，assist 全关）。
    /// 任务为运行时数据不存档——任务存续期由调用方保证禁存档（对接计划/跳帮舱）；
    /// 读档防御见 ResetOnLoad。
    /// </summary>
    public static class ShipAutopilot
    {
        private class Flight
        {
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
        }

        private static readonly Dictionary<ShipState, Flight> active = new Dictionary<ShipState, Flight>();

        private static readonly List<ShipState> tmpShips = new List<ShipState>();

        // ---- PD 参数（制导回路：只产生期望力/力矩；实际出力上限由分配器按推进器
        //      布局决定，MaxAccel/MaxAngularAccel 仅作为期望值的饱和保护）----

        /// <summary>平移自然频率（rad/s)；临界阻尼下约 2.5–3 s 收敛。</summary>
        private const float PositionOmega = 1.6f;

        /// <summary>平移期望加速度上限（cells/s²)；参考船推进器实测约 2.2。</summary>
        private const float MaxAccel = 4f;

        /// <summary>旋转自然频率（rad/s）。</summary>
        private const float AngleOmega = 1.8f;

        /// <summary>角期望加速度上限（rad/s²）。</summary>
        private const float MaxAngularAccel = 0.6f;

        /// <summary>吸附半径（格）：进入后放大位置弹簧，保证有限时间收敛。</summary>
        private const float SnapRadius = 1.5f;

        /// <summary>吸附弹簧倍率。</summary>
        private const float SnapGain = 4f;

        /// <summary>虚空动力渐变半径（格）：外沿起效、进入吸附圈补满——只在停靠任务
        /// （allowVoidThrust）中补齐推进器布局无法形成的「期望 − 可达」差值，
        /// 远场仍是纯真实推进。</summary>
        private const float VoidRampRadius = 4.5f;

        private const float ArrivePositionEpsilon = 0.05f; // 格
        private const float ArriveVelocityEpsilon = 0.15f; // cells/s
        private const float ArriveAngleEpsilon = 0.01f;    // rad
        private const float ArriveOmegaEpsilon = 0.03f;    // rad/s

        /// <summary>固定航点模式的超时兜底（tick）：无论如何强制对位并视为到达。
        /// 随动模式无超时（完成判据归调用方）。</summary>
        private const int MaxFlightTicks = 1200;

        /// <summary>
        /// 把船自动驾驶到轨道坐标（球面方向 direction，宇宙角 angle）。到达（收敛或
        /// 超时）时精确对位（pose := 目标、速度清零）并回调 onArrived；ship.map 失效
        /// 等异常按 onFailed 退出。重复登记同一艘船会覆盖旧任务（旧任务静默作废）。
        /// allowVoidThrust：停靠任务传 true，终段叠加虚空动力保证任意布局精确对接；
        /// 非停靠用途（未来敌舰巡航/战斗）传 false，获得纯真实动力。
        /// </summary>
        public static void FlyTo(ShipState ship, Vector3 targetDirection, float targetAngle,
            Action onArrived, Action onFailed = null, bool allowVoidThrust = true)
        {
            if (ship == null)
            {
                onFailed?.Invoke();
                return;
            }
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"autopilot waypoint engaged: {ship.label} -> ({targetDirection.x:F2},{targetDirection.y:F2},{targetDirection.z:F2}) angle={targetAngle * Mathf.Rad2Deg:F0} deg voidThrust={allowVoidThrust}");
            active[ship] = new Flight
            {
                tracked = false,
                direction = targetDirection.normalized,
                angle = SpaceTransform.WrapAngle(targetAngle),
                elapsed = 0f,
                allowVoidThrust = allowVoidThrust,
                onArrived = onArrived,
                onFailed = onFailed,
            };
        }

        /// <summary>FlyTo 的可等待版本：到达完成、失败取消（延续回到 Unity 主线程同步上下文）。</summary>
        public static Task FlyToAsync(ShipState ship, Vector3 targetDirection, float targetAngle,
            bool allowVoidThrust = true)
        {
            var tcs = new TaskCompletionSource<object>();
            FlyTo(ship, targetDirection, targetAngle,
                () => tcs.TrySetResult(null), () => tcs.TrySetCanceled(), allowVoidThrust);
            return tcs.Task;
        }

        /// <summary>
        /// 随动目标模式：把船持续对位到「宿主对象位姿 ⊕ 局部偏移」（宇宙角 = 宿主角 ⊕
        /// relativeAngle）的**移动**位姿上——目标每帧随宿主重算，宿主速度/角速度经前馈
        /// 匹配（阻尼项取相对量），收敛后站位保持直到取消。不自动到达、无超时兜底、
        /// 无完成回调：完成判据归调用方（对接计划 = 几何窗口 + 保持 tick）。宿主失效按
        /// onFailed 退出（计划是否随之取消由调用方决定）。
        /// </summary>
        public static void TrackTo(ShipState ship, SpaceObject host, Vector2 hostOffset,
            float relativeAngle, Action onFailed = null, bool allowVoidThrust = true)
        {
            if (ship == null || host == null)
            {
                onFailed?.Invoke();
                return;
            }
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"autopilot track engaged: {ship.label} -> '{host.label}' offset=({hostOffset.x:F0},{hostOffset.y:F0}) relAngle={SpaceTransform.WrapAngle(relativeAngle) * Mathf.Rad2Deg:F0} deg voidThrust={allowVoidThrust}");
            active[ship] = new Flight
            {
                tracked = true,
                host = host,
                hostOffset = hostOffset,
                relativeAngle = SpaceTransform.WrapAngle(relativeAngle),
                elapsed = 0f,
                allowVoidThrust = allowVoidThrust,
                onFailed = onFailed,
            };
        }

        /// <summary>
        /// 静默摘除任务（清除油门、不触发任何回调）：对接完成接管位姿、玩家手操打断
        /// 自动驾驶（不取消对接计划）、计划取消等场景使用；reason 记入日志（边沿事件，
        /// 只在实际摘除时输出）。需要失败信号反馈的退出走 Cancel。
        /// </summary>
        public static void Detach(ShipState ship, string reason = null)
        {
            if (ship == null || !active.ContainsKey(ship)) return;
            active.Remove(ship);
            ShipPhysics.ClearThrottles(ship); // 绕过积分器的出口：显式熄火
            if (reason != null)
            {
                DeltaVLog.Message(DeltaVLogModule.Core, $"autopilot detached ({reason}): {ship.label}");
            }
        }

        public static bool IsActive(ShipState ship)
        {
            return ship != null && active.ContainsKey(ship);
        }

        /// <summary>异常出口：按失败信号收尾并清除任务。</summary>
        public static void Cancel(ShipState ship)
        {
            if (ship == null || !active.TryGetValue(ship, out Flight flight)) return;
            active.Remove(ship);
            ShipPhysics.ClearThrottles(ship); // 绕过积分器的出口：显式熄火
            flight.onFailed?.Invoke();
        }

        /// <summary>UniverseManager.GameComponentTick 每 tick 驱动（游戏时间语义：
        /// 暂停冻结、倍速加速）。</summary>
        public static void Tick()
        {
            if (active.Count == 0) return;
            // 回调可能在迭代中增删任务（到达后立即发起新任务等），按快照迭代。
            tmpShips.Clear();
            foreach (KeyValuePair<ShipState, Flight> pair in active) tmpShips.Add(pair.Key);
            foreach (ShipState ship in tmpShips)
            {
                if (active.TryGetValue(ship, out Flight flight)) Step(ship, flight);
            }
        }

        private static void Step(ShipState ship, Flight flight)
        {
            if (ship.map == null || ship.map.Disposed || Current.Game == null)
            {
                active.Remove(ship);
                ShipPhysics.ClearThrottles(ship); // 绕过积分器的出口：显式熄火
                DeltaVLog.Message(DeltaVLogModule.Core, $"autopilot failed (ship map lost): {ship.label}");
                flight.onFailed?.Invoke();
                return;
            }
            if (flight.tracked && flight.host == null)
            {
                active.Remove(ship);
                ShipPhysics.ClearThrottles(ship);
                DeltaVLog.Message(DeltaVLogModule.Core, $"autopilot failed (host lost): {ship.label}");
                flight.onFailed?.Invoke();
                return;
            }

            const float dt = 1f / 60f; // 游戏 tick 时间；绝不使用帧时钟（AGENTS §18 #20）
            flight.elapsed += 1;
            if (!flight.tracked && flight.elapsed > MaxFlightTicks)
            {
                Arrive(ship, flight); // 超时兜底仅固定航点模式；随动模式由调用方判完成
                return;
            }

            ShipPhysics.MassReport report = ShipPhysics.GetReport(ship);
            if (report == null) return;

            // 目标位姿与宿主前馈：误差/速度统一表达在目标锚点的切空间基下（球面几何
            // 唯一入口 OrbitSphere）。随动模式的「速度」是相对宿主的量——宿主速度
            // 平行输运到目标基后作差，PD 阻尼项即天然完成速度匹配（一阶预测：宿主
            // 机动由 PD 重新收敛，不需要高阶前馈）。
            Vector3 targetDirection;
            float targetAngle;
            Vector2 hostVelocity = Vector2.zero;
            float hostOmega = 0f;
            if (flight.tracked)
            {
                targetDirection = SpaceTransform.FocusedToUniverse(flight.host, flight.hostOffset);
                targetAngle = SpaceTransform.WrapAngle(flight.host.pose.angle + flight.relativeAngle);
                hostVelocity = SpaceTransform.TransportVelocity(flight.host.pose, targetDirection);
                hostOmega = flight.host.pose.angularVelocity;
            }
            else
            {
                targetDirection = flight.direction;
                targetAngle = flight.angle;
            }
            Vector2 posErr = -OrbitSphere.Offset(targetDirection, ship.pose.position);
            Vector2 vel = SpaceTransform.TransportVelocity(ship.pose, targetDirection) - hostVelocity;
            float angleErr = SpaceTransform.WrapAngle(targetAngle - ship.pose.angle);
            float omega = ship.pose.angularVelocity - hostOmega;

            if (!flight.tracked
                && posErr.sqrMagnitude < ArrivePositionEpsilon * ArrivePositionEpsilon
                && vel.sqrMagnitude < ArriveVelocityEpsilon * ArriveVelocityEpsilon
                && Mathf.Abs(angleErr) < ArriveAngleEpsilon
                && Mathf.Abs(omega) < ArriveOmegaEpsilon)
            {
                Arrive(ship, flight);
                return;
            }

            // 临界阻尼 PD + 近距吸附（制导回路：只产生期望力/力矩）。
            float gain = PositionOmega * PositionOmega;
            if (posErr.magnitude < SnapRadius) gain *= SnapGain;
            Vector2 accel = gain * posErr - 2f * PositionOmega * vel;
            if (accel.magnitude > MaxAccel) accel = accel.normalized * MaxAccel;
            float alpha = Mathf.Clamp(AngleOmega * AngleOmega * angleErr - 2f * AngleOmega * omega,
                -MaxAngularAccel, MaxAngularAccel);

            // 刚体动量管理：期望力/力矩经归一化后由联合分配器求真实油门与可达出力，
            // 积分使用可达值——机动受推进器布局硬约束（玩家船/敌舰同构），火焰
            // 表现层读到的油门即真实出力。目标锚点系与船锚点系的切空间基在战术
            // 距离下夹角可忽略（≤1°），按船局部系直喂。
            Vector2 desiredForce = SpaceTransform.RotateInverse(accel, ship.pose.angle) * report.mass;
            float desiredTorque = alpha * report.inertia;
            ShipPhysics.Command command = ShipPhysics.ForceToCommand(report, desiredForce, desiredTorque);
            ShipPhysics.Allocate(report, command, out Vector2 realForce, out float realTorque);

            // 终段虚空动力：按距离渐变补齐「期望 − 可达」差值（远场 0 → 吸附圈 1），
            // 保证任意推进器布局都能精确对位；不写油门、不进火焰（表现层只见真实出力）。
            Vector2 appliedForce = realForce;
            float appliedTorque = realTorque;
            if (flight.allowVoidThrust)
            {
                float blend = 1f - Mathf.Clamp01(posErr.magnitude / VoidRampRadius);
                appliedForce += (desiredForce - realForce) * blend;
                appliedTorque += (desiredTorque - realTorque) * blend;
            }
            ShipPhysics.Integrate(ship, report, appliedForce, appliedTorque, dt, false, false, false);
        }

        private static void Arrive(ShipState ship, Flight flight)
        {
            active.Remove(ship);
            ShipPhysics.ClearThrottles(ship); // 直接改位姿绕过积分器：显式熄火
            // 精确对位（仅固定航点模式）：到达即目标位姿、速度清零。
            ship.pose.position = flight.direction;
            ship.pose.angle = flight.angle;
            ship.pose.velocity = Vector2.zero;
            ship.pose.angularVelocity = 0f;
            flight.onArrived?.Invoke();
        }

        /// <summary>读档防御：任务为运行时数据，静默清空（不回调；调用方各自的读档
        /// 防御负责收尾，避免读档期弹消息）。FlyToAsync 的等待方会看到任务永悬，
        /// 由调用方避免在跨存档流程中持有该 Task。</summary>
        public static void ResetOnLoad()
        {
            active.Clear();
        }
    }
}
