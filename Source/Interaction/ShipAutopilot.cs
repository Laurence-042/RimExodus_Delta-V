using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 轨道层通用自动驾驶：把任意 ShipState 自动驾驶到统一宇宙坐标的目标位姿
    /// （球面方向 + 宇宙角），到达（收敛或超时兜底）后精确对位并发完成信号。
    ///
    /// 出力架构（刚体动量管理）：制导回路（临界阻尼 PD）只产生期望力/力矩，
    /// 经 ShipPhysics.ForceToCommand 归一化后由联合分配器 Allocate 求解**真实**
    /// 逐推进器油门与可达力/力矩，积分使用可达值——机动受推进器布局硬约束，
    /// 玩家船/敌舰/未来舰船 AI 完全同构，火焰表现层读到的油门即真实出力。
    /// 停靠任务（allowVoidThrust）在终段按距离渐变叠加虚空动力补齐「期望 − 可达」
    /// 差值，保证任意推进器布局都能精确对接；虚空动力不写油门、不进火焰。
    ///
    /// 语义纯轨道层：目标与内部误差全部经 OrbitSphere/SpaceTransform 表达，不接触
    /// 任何 Map/聚焦坐标——「地图放置位置/旋转 → 轨道位姿」的反向映射由调用方完成
    /// （战术降落见 DockController.ComputeLandingPose）。后续飞船 AI / 点击巡航等
    /// 可直接复用本能力（非停靠用途传 allowVoidThrust: false 获得纯真实动力）；
    /// 自动驾驶中的船由 UniverseManager.GameComponentTick 跳过正常积分，不会双重驱动。
    ///
    /// 帧驱动（Time.deltaTime）：降落过场期间 tick 冻结所必需；常规飞行使用如需
    /// 游戏时间语义（暂停/倍速跟随），可另行增加 tick 驱动入口。
    ///
    /// 积分复用 ShipPhysics.Integrate（球面三步走 + 平行输运，assist 全关）。
    /// 任务为运行时数据不存档——进场期间原版禁存档；常规使用如需跨存档，
    /// 由调用方持久化目标并在读档后重启任务。
    /// </summary>
    public static class ShipAutopilot
    {
        private class Flight
        {
            public Vector3 direction;
            public float angle;
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

        /// <summary>超时兜底：无论如何强制对位并视为到达。</summary>
        private const float MaxFlightSeconds = 20f;

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
            active[ship] = new Flight
            {
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

        /// <summary>UniverseManager.GameComponentUpdate 每帧驱动。</summary>
        public static void Update()
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
                flight.onFailed?.Invoke();
                return;
            }

            float dt = Time.deltaTime;
            flight.elapsed += dt;
            if (flight.elapsed > MaxFlightSeconds)
            {
                Arrive(ship, flight);
                return;
            }

            ShipPhysics.MassReport report = ShipPhysics.GetReport(ship);
            if (report == null) return;

            // 误差与速度统一表达在目标锚点的切空间基下（球面几何唯一入口 OrbitSphere）。
            Vector2 posErr = -OrbitSphere.Offset(flight.direction, ship.pose.position);
            Vector2 vel = VelocityInTargetFrame(ship, flight.direction);
            float angleErr = SpaceTransform.WrapAngle(flight.angle - ship.pose.angle);
            float omega = ship.pose.angularVelocity;

            if (posErr.sqrMagnitude < ArrivePositionEpsilon * ArrivePositionEpsilon
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

            // 停靠终段虚空动力：按距离渐变补齐「期望 − 可达」差值（远场 0 → 吸附圈 1），
            // 保证任意推进器布局都能精确对接；不写油门、不进火焰（表现层只见真实出力）。
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
            // 精确对位：到达即目标位姿、速度清零（对接语义；与停靠写入共用同一目标，
            // 保证船壳收敛帧与真船落位严格重合）。
            ship.pose.position = flight.direction;
            ship.pose.angle = flight.angle;
            ship.pose.velocity = Vector2.zero;
            ship.pose.angularVelocity = 0f;
            flight.onArrived?.Invoke();
        }

        /// <summary>船速从其锚点基平行输运到目标锚点基（精确换基，供 PD 阻尼项使用）。</summary>
        private static Vector2 VelocityInTargetFrame(ShipState ship, Vector3 targetDirection)
        {
            OrbitSphere.Basis(ship.pose.position, out Vector3 east, out Vector3 north);
            Vector3 world = east * ship.pose.velocity.x + north * ship.pose.velocity.y;
            OrbitSphere.Basis(targetDirection, out east, out north);
            return new Vector2(Vector3.Dot(world, east), Vector3.Dot(world, north));
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
