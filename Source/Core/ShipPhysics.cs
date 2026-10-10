using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 阶段2 飞船刚体物理（权威设计见 doc/阶段2-基础驾驶.md）。
    ///
    /// 模型（story「飞行模拟」：第一版只完成 推力→加速度→速度→位置 及角运动）：
    /// - 质量 = Substructure 甲板格数 × MassPerDeckCell + 建筑（有 Mass stat 用 stat 值，否则 MassPerBuilding）；
    /// - 质心 / 转动惯量按点质量对质心求和；
    /// - 推进器 = LargeThruster（Odyssey 逆重推进器）：占用矩形中心为作用点，Rot4 朝向为推力方向；
/// - 分配器把每台推进器对 Fx/Fz/力矩的贡献放入同一个盒约束最小二乘问题；
///   纯平移优先保持零净力矩，必要时会差动同向推进器或调用正交方向的力偶；
/// - 所有力源（驾驶输入、自动驾驶、未来舰船 AI）都必须经 ForceToCommand 归一化
///   后走同一分配器求解油门与可达力，禁止绕过分配器直接合成力——机动因此始终
///   受推进器布局约束，火焰表现层也只认分配器写出的真实油门；
    /// - 飞行辅助（含按住刹车键临时启用）在 PilotController 合成反向指令，走同一分配器；
    ///   最后的微小残差由 Residual* 常量直接衰减到零（用户决策 2026-09-13：不要求推进器
    ///   布局能完美消除微小漂移——刻意偏离纯牛顿的收尾便利）。
    ///
    /// 积分以质心为动力学中心（半隐式欧拉，dt = 1/60 每 tick）；
    /// SpacePose.position 是球面方向（锚 = 地图几何中心对应的球面点），按不变量
    /// "Move(position, Rotate(comLocal, angle)) = 质心球面方向" 每步派生——
    /// 与旧平面版公式形态一致，只是加法换成球面移动。
    ///
    /// 质量报告是可重建缓存：按 tick 间隔重扫，读档/场景重置时清空（不变量 #13）。
    /// </summary>
    public static class ShipPhysics
    {
        // ---- 阶段2 常量（数值调参记录进文档；物理细节按 story「原型完成后再设计」演进）----

        public const float MassPerDeckCell = 1f;

        public const float MassPerBuilding = 4f;

        /// <summary>单台推进器推力。玩家船（约 1800 质量、2 台前向推进器）→ 前进加速度约 2.2 cells/s²。</summary>
        public const float ThrusterThrust = 2000f;

        // ---- 联合推进器分配 ----

        /// <summary>坐标下降轮数。阶段2 船通常只有 8–12 台推进器，该值足以收敛且不产生每 tick GC。</summary>
        private const int AllocationSweeps = 96;

        /// <summary>很小的油门正则项，在多解时偏向更小的总出力。</summary>
        private const float AllocationRegularization = 0.0001f;

        private const float CommandedTranslationWeight = 1f;
        private const float UncommandedTranslationWeight = 4f;
        private const float CommandedTorqueWeight = 4f;
        private const float UncommandedTorqueWeight = 8f;

        // ---- 残差衰减（飞行辅助收尾；见类注释的用户决策）----

        /// <summary>低于此线速度（cells/s）的漂移由辅助直接衰减，不再依赖推进器几何。</summary>
        public const float ResidualSpeed = 0.6f;

        /// <summary>残差衰减加速度（cells/s²）：0.6 → 0 约 0.25 s。</summary>
        public const float ResidualDecel = 2.4f;

        /// <summary>低于此角速度（rad/s，约 5.7°/s）的自转由辅助直接衰减。</summary>
        public const float ResidualOmega = 0.1f;

        /// <summary>残差角加速度（rad/s²）。</summary>
        public const float ResidualAlphaDecel = 0.4f;

        public sealed class Thruster
        {
            /// <summary>对应的 Map 内推进器；只供运行时表现层读取，不持久化。</summary>
            public Thing thing;

            /// <summary>作用点（ship local，原点 = 地图几何中心）：占用矩形中心。</summary>
            public Vector2 localPos;

            /// <summary>ship local 单位推力方向。</summary>
            public Vector2 dir;

            /// <summary>动力来源判定（ThrusterPower 抽象）：不可用的推进器不参与分配。
            /// Measure 时先快照；Allocate 每 tick 重读实时状态刷新（故障/排气堵塞即时生效，
            /// 不受质量报告的 120 tick 重扫间隔拖慢）。</summary>
            public bool operational;

            /// <summary>本台推进器的推力（默认原版常量；shuttle 虚拟推进器按 comp 配置覆盖，§7.3）。</summary>
            public float thrust = ThrusterThrust;

            /// <summary>本 tick 分配到的油门 [0,1]。唯一写入者是 Allocate（驾驶/
            /// 自动驾驶/未来舰船 AI 共用同一分配器）；火焰表现层（地图路径与
            /// 船壳路径）只读此值。运行时派生状态，不参与物理积分或存档。</summary>
            public float throttle;
        }

        public sealed class MassReport
        {
            public float mass;
            public Vector2 comLocal;
            public float inertia;
            public readonly List<Thruster> thrusters = new List<Thruster>();

            /// <summary>质量模型重扫版本号：GetReport 每次用新 Measure 替换时 +1。
            /// 消费方（ShipAutopilot 的微步内分配去重缓存）据此让「同指令跳过重复
            /// 分配」在重扫后立即失效。运行时派生计数，不存档。</summary>
            public int version;

            // 分配器工作区。真实分配与只读预览共用同一求解核心；数组随质量报告重建，
            // 热路径不产生临时分配。只有 Allocate 会把结果提交到 Thruster.throttle。
            internal float[] allocationScratch;
            internal float[] previewScratch;

            /// <summary>最近一次 Allocate 求解的时间（Time.time）。0 = 从未求解
            /// （Measure 重扫后的缺省），火焰读取方据此判定油门新鲜，防止力源
            /// 停止后的幽灵火焰。运行时表现层标记，不存档。</summary>
            public float throttlesWrittenAt;
        }

        /// <summary>指令各分量 ∈ [-1, 1]；平移分量已经由主驾驶台系换算到 Map 局部 x/z。</summary>
        public struct Command
        {
            public float fx;
            public float fz;
            public float torque;
        }

        /// <summary>
        /// 当前推进器布局在零指令附近的局部控制模型。force/torque 列分别是归一化
        /// fx/fz/torque 指令对真实可达广义力的中心差分；方向上限只用于把 MPC 的
        /// 首个归一化控制量还原为期望力，再进入 ForceToCommand → Allocate 唯一链。
        /// </summary>
        public sealed class ControlModel
        {
            public float mass;
            public float inertia;
            public readonly Vector2[] forceColumns = new Vector2[3];
            public readonly float[] torqueColumns = new float[3];
            public float positiveX;
            public float negativeX;
            public float positiveZ;
            public float negativeZ;
            public float positiveTorque;
            public float negativeTorque;

            public void DesiredWrench(Command command, out Vector2 force, out float torque)
            {
                force = new Vector2(
                    command.fx >= 0f ? command.fx * positiveX : command.fx * negativeX,
                    command.fz >= 0f ? command.fz * positiveZ : command.fz * negativeZ);
                torque = command.torque >= 0f
                    ? command.torque * positiveTorque
                    : command.torque * negativeTorque;
            }
        }

        // ---- 质量模型 ----

        public static MassReport Measure(Map map)
        {
            var report = new MassReport();
            Vector2 origin = SpaceTransform.ShipLocalOrigin(map);

            float totalMass = 0f;
            var moment = Vector2.zero;
            var massPoints = new List<(Vector2 pos, float mass)>(256);
            float intrinsicInertia = 0f;

            TerrainGrid terrain = map.terrainGrid;
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                // IsSubstructure（tag 口径）：原版基架与月池（§7）同一计入，勿退回特定 def 比较。
                if (terrain.TerrainAt(cell)?.IsSubstructure != true)
                {
                    continue;
                }
                var r = new Vector2(cell.x + 0.5f - origin.x, cell.z + 0.5f - origin.y);
                totalMass += MassPerDeckCell;
                moment += r * MassPerDeckCell;
                massPoints.Add((r, MassPerDeckCell));
            }

            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (!(thing is Building))
                {
                    continue;
                }
                CellRect rect = thing.OccupiedRect();
                var r = new Vector2((rect.minX + rect.maxX + 1) * 0.5f - origin.x,
                    (rect.minZ + rect.maxZ + 1) * 0.5f - origin.y);
                float m = BuildingMass(thing);
                totalMass += m;
                moment += r * m;
                massPoints.Add((r, m));
                // 建筑不是位于中心的数学质点。矩形薄板绕中心的本征惯量为
                // m(w²+h²)/12；此前 shuttle 独占图只有一个 3x5 建筑，点质量模型把
                // 惯量夹成 1，任何侧推都产生数百 rad/s² 的虚假角加速度。
                intrinsicInertia += m * (rect.Width * rect.Width + rect.Height * rect.Height) / 12f;
                if (ThrusterPower.IsThruster(thing))
                {
                    // 出力上限收口（2026-09-27）：滑条 comp 的百分比在此一次性乘入——
                    // 分配器/权威测量/火焰只读 thruster.thrust，自动跟随。
                    CompThrusterPowerLimit powerLimit = thing.TryGetComp<CompThrusterPowerLimit>();
                    float thrust = ThrusterThrust;
                    if (powerLimit != null)
                    {
                        thrust *= Mathf.Clamp(powerLimit.powerPercent, 0f, 100f) * 0.01f;
                    }
                    report.thrusters.Add(new Thruster
                    {
                        thing = thing,
                        localPos = r,
                        dir = ThrustDir(thing.Rotation),
                        operational = ThrusterPower.IsOperational(thing) && thrust > 0f,
                        thrust = thrust
                    });
                }
            }

            if (totalMass < 1f)
            {
                // 空图防御：无结构时给单位质量，质心落在原点。
                report.mass = 1f;
                return report;
            }

            report.mass = totalMass;
            report.comLocal = moment / totalMass;
            float inertia = intrinsicInertia;
            for (int i = 0; i < massPoints.Count; i++)
            {
                inertia += massPoints[i].mass * (massPoints[i].pos - report.comLocal).sqrMagnitude;
            }
            report.inertia = Mathf.Max(inertia, 1f);
            // 虚拟推进器注入（shuttle MiniShip，太空战架构 §7.3）：XML 逐台配置的
            // 船体帧位置 + 推力矢量（PassengerShuttle 默认 8 台）——不经图内推进器建筑，
            // 但与真实推进器走同一分配器/油门/火焰读取链。
            if (VirtualThrustersByMap.TryGetValue(map, out Thruster[] virtualThrusters))
            {
                report.thrusters.AddRange(virtualThrusters);
            }
            return report;
        }

        // ---- 虚拟推进器注册（shuttle MiniShip 专用；真实推进器经 Measure 扫描）----

        private static readonly Dictionary<Map, Thruster[]> VirtualThrustersByMap = new Dictionary<Map, Thruster[]>();

        public static void SetVirtualThrusters(Map map, Thruster[] thrusters)
        {
            if (map == null) return;
            if (thrusters == null) VirtualThrustersByMap.Remove(map);
            else VirtualThrustersByMap[map] = thrusters;
            Cache.Remove(map); // 立即失效质量报告（控制模型随之重建）
        }

        public static void ClearVirtualThrusters(Map map) => SetVirtualThrusters(map, null);

        /// <summary>
        /// 建筑质量：def 带 Mass stat（如 LargeThruster=30）用 stat 实值，否则用
        /// MassPerBuilding；运输舱的 held 内容按原版 CompTransporter.MassUsage 加入，
        /// 使 shuttle 的质量/矩形惯量随实际载重变化（§7.3）。
        /// </summary>
        private static float BuildingMass(Thing thing)
        {
            float mass = MassPerBuilding;
            if (thing.def.statBases != null)
            {
                for (int i = 0; i < thing.def.statBases.Count; i++)
                {
                    if (thing.def.statBases[i].stat == StatDefOf.Mass)
                    {
                        mass = thing.GetStatValue(StatDefOf.Mass);
                        break;
                    }
                }
            }
            CompTransporter transporter = thing.TryGetComp<CompTransporter>();
            if (transporter != null)
            {
                mass += Mathf.Max(0f, transporter.MassUsage);
            }
            return mass;
        }

        /// <summary>
        /// 推进器 Rot4 → ship local 推力方向。约定：推力沿推进器朝向（喷口朝反方向排气）。
        /// 依据：LargeThruster 的 exhaustSettings 喷口偏移为 (0, 0, -N)（默认 North 朝向时在 -Z）。
        /// 若实测贴图方向与此矛盾，只需翻转本表——全项目唯一约定点。
        /// </summary>
        public static Vector2 ThrustDir(Rot4 rot)
        {
            if (rot == Rot4.North)
            {
                return new Vector2(0f, 1f);
            }
            if (rot == Rot4.East)
            {
                return new Vector2(1f, 0f);
            }
            if (rot == Rot4.South)
            {
                return new Vector2(0f, -1f);
            }
            return new Vector2(-1f, 0f);
        }

        // ---- 推进器分配与积分 ----

        /// <summary>
        /// 联合油门分配：求 0&lt;=u_i&lt;=1，使归一化后的 [Fx,Fz,torque] 尽量贴近指令。
        /// 未输入的平移轴和力矩有更高的零值保持权重：因此 AD 推力不过质心时，
        /// 求解器可以差动侧推器，或使用 WS 方向的相反推进器组成力偶抵消力矩。
        /// 若当前布局无法同时满足目标，则优先降低姿态/串轴误差，不伪造物理出力。
        /// </summary>
        public static void Allocate(MassReport report, Command command, out Vector2 force, out float torque)
        {
            EnsureAllocatorScratch(report);
            if (report == null || report.thrusters.Count == 0)
            {
                force = Vector2.zero;
                torque = 0f;
                return;
            }
            RefreshOperational(report);
            SolveAllocation(report, command, report.allocationScratch, out force, out torque);
            for (int i = 0; i < report.thrusters.Count; i++)
            {
                report.thrusters[i].throttle = report.allocationScratch[i];
            }
            report.throttlesWrittenAt = Time.time;
        }

        /// <summary>
        /// 构造制导器使用的只读局部控制模型。采样调用与 Allocate 完全相同的分配核心，
        /// 但结果只写 previewScratch，不触碰真实油门及火焰新鲜度。
        /// </summary>
        public static ControlModel BuildControlModel(MassReport report, ControlModel reuse = null)
        {
            ControlModel model = reuse ?? new ControlModel();
            model.mass = 0f;
            model.inertia = 0f;
            model.positiveX = model.negativeX = model.positiveZ = model.negativeZ = 0f;
            model.positiveTorque = model.negativeTorque = 0f;
            for (int i = 0; i < 3; i++)
            {
                model.forceColumns[i] = Vector2.zero;
                model.torqueColumns[i] = 0f;
            }
            if (report == null)
            {
                return model;
            }
            model.mass = report.mass;
            model.inertia = report.inertia;
            EnsureAllocatorScratch(report);
            RefreshOperational(report);
            MeasureAuthority(report, out model.positiveX, out model.negativeX,
                out model.positiveZ, out model.negativeZ,
                out model.positiveTorque, out model.negativeTorque);

            Command positive = default;
            Command negative = default;
            for (int axis = 0; axis < 3; axis++)
            {
                positive = default;
                negative = default;
                SetAxis(ref positive, axis, 1f);
                SetAxis(ref negative, axis, -1f);
                SolveAllocation(report, positive, report.previewScratch,
                    out Vector2 positiveForce, out float positiveMoment);
                SolveAllocation(report, negative, report.previewScratch,
                    out Vector2 negativeForce, out float negativeMoment);
                model.forceColumns[axis] = (positiveForce - negativeForce) * 0.5f;
                model.torqueColumns[axis] = (positiveMoment - negativeMoment) * 0.5f;
            }
            return model;
        }

        /// <summary>当前朝目标方向的实际反推能力。只读地调用真实分配核心；不提交油门。
        /// towardTargetLocal 是 Map 局部单位方向，返回沿反方向可获得的加速度。</summary>
        public static float PreviewBrakingAcceleration(MassReport report, Vector2 towardTargetLocal)
        {
            if (report == null || report.mass <= 0f || towardTargetLocal.sqrMagnitude < 1e-8f)
                return 0f;
            EnsureAllocatorScratch(report);
            RefreshOperational(report);
            Vector2 reverse = -towardTargetLocal.normalized;
            var command = new Command { fx = reverse.x, fz = reverse.y };
            SolveAllocation(report, command, report.previewScratch, out Vector2 force, out _);
            return Mathf.Max(0f, Vector2.Dot(force, reverse) / report.mass);
        }

        private static void SetAxis(ref Command command, int axis, float value)
        {
            if (axis == 0) command.fx = value;
            else if (axis == 1) command.fz = value;
            else command.torque = value;
        }

        private static void EnsureAllocatorScratch(MassReport report)
        {
            if (report == null) return;
            int count = report.thrusters.Count;
            if (report.allocationScratch == null || report.allocationScratch.Length != count)
            {
                report.allocationScratch = new float[count];
                report.previewScratch = new float[count];
            }
        }

        private static void RefreshOperational(MassReport report)
        {
            for (int i = 0; i < report.thrusters.Count; i++)
            {
                Thruster thruster = report.thrusters[i];
                if (thruster.thing == null)
                {
                    continue; // 虚拟推进器（shuttle MiniShip，§7.3）：无实体，恒可用
                }
                thruster.operational = ThrusterPower.IsOperational(thruster.thing);
            }
        }

        private static void SolveAllocation(MassReport report, Command command, float[] throttles,
            out Vector2 force, out float torque)
        {
            force = Vector2.zero;
            torque = 0f;
            if (report == null || report.thrusters.Count == 0 || throttles == null)
            {
                return;
            }
            System.Array.Clear(throttles, 0, throttles.Length);
            MeasureAuthority(report, out float positiveX, out float negativeX,
                out float positiveZ, out float negativeZ, out float positiveTorque, out float negativeTorque);

            float scaleX = DirectionalScale(command.fx, positiveX, negativeX);
            float scaleZ = DirectionalScale(command.fz, positiveZ, negativeZ);
            float scaleTorque = DirectionalScale(command.torque, positiveTorque, negativeTorque);
            float weightX = Mathf.Abs(command.fx) > 1e-6f
                ? CommandedTranslationWeight : UncommandedTranslationWeight;
            float weightZ = Mathf.Abs(command.fz) > 1e-6f
                ? CommandedTranslationWeight : UncommandedTranslationWeight;
            float weightTorque = Mathf.Abs(command.torque) > 1e-6f
                ? CommandedTorqueWeight : UncommandedTorqueWeight;

            // residual = A*u-b；u 从零开始，在调用方提供的复用工作区上迭代。
            float residualX = -Mathf.Clamp(command.fx, -1f, 1f);
            float residualZ = -Mathf.Clamp(command.fz, -1f, 1f);
            float residualTorque = -Mathf.Clamp(command.torque, -1f, 1f);
            for (int sweep = 0; sweep < AllocationSweeps; sweep++)
            {
                float maxDelta = 0f;
                bool reverse = (sweep & 1) != 0;
                for (int step = 0; step < report.thrusters.Count; step++)
                {
                    int index = reverse ? report.thrusters.Count - 1 - step : step;
                    Thruster thruster = report.thrusters[index];
                    if (!thruster.operational) continue;
                    Vector2 fullForce = thruster.dir * thruster.thrust;
                    float axisX = fullForce.x / scaleX;
                    float axisZ = fullForce.y / scaleZ;
                    float axisTorque = Cross(thruster.localPos - report.comLocal, fullForce) / scaleTorque;
                    float denominator = weightX * axisX * axisX
                        + weightZ * axisZ * axisZ
                        + weightTorque * axisTorque * axisTorque
                        + AllocationRegularization;
                    if (denominator <= 1e-12f)
                    {
                        continue;
                    }
                    float gradient = weightX * axisX * residualX
                        + weightZ * axisZ * residualZ
                        + weightTorque * axisTorque * residualTorque
                        + AllocationRegularization * throttles[index];
                    float next = Mathf.Clamp01(throttles[index] - gradient / denominator);
                    float delta = next - throttles[index];
                    if (Mathf.Abs(delta) <= 1e-7f)
                    {
                        continue;
                    }
                    throttles[index] = next;
                    residualX += axisX * delta;
                    residualZ += axisZ * delta;
                    residualTorque += axisTorque * delta;
                    maxDelta = Mathf.Max(maxDelta, Mathf.Abs(delta));
                }
                if (maxDelta < 0.0001f)
                {
                    break;
                }
            }

            // 权威物理结果始终由真实几何重算，不使用归一化求解残差反推。
            for (int i = 0; i < report.thrusters.Count; i++)
            {
                Thruster thruster = report.thrusters[i];
                float throttle = throttles[i];
                if (!thruster.operational || throttle < 0.0001f)
                {
                    throttles[i] = 0f;
                    continue;
                }
                Vector2 appliedForce = thruster.dir * (thruster.thrust * throttle);
                force += appliedForce;
                torque += Cross(thruster.localPos - report.comLocal, appliedForce);
            }
        }

        /// <summary>
        /// 累计当前 operational 快照的各轴正/负向出力上限——ForceToCommand 的归一化与分配器求解共用
        /// 同一份上限，保证指令换算与求解目标严格一致。
        /// </summary>
        private static void MeasureAuthority(MassReport report,
            out float positiveX, out float negativeX,
            out float positiveZ, out float negativeZ,
            out float positiveTorque, out float negativeTorque)
        {
            positiveX = 0f;
            negativeX = 0f;
            positiveZ = 0f;
            negativeZ = 0f;
            positiveTorque = 0f;
            negativeTorque = 0f;
            for (int i = 0; i < report.thrusters.Count; i++)
            {
                Thruster thruster = report.thrusters[i];
                if (!thruster.operational)
                {
                    continue;
                }
                Vector2 fullForce = thruster.dir * thruster.thrust;
                float fullTorque = Cross(thruster.localPos - report.comLocal, fullForce);
                positiveX += Mathf.Max(fullForce.x, 0f);
                negativeX += Mathf.Max(-fullForce.x, 0f);
                positiveZ += Mathf.Max(fullForce.y, 0f);
                negativeZ += Mathf.Max(-fullForce.y, 0f);
                positiveTorque += Mathf.Max(fullTorque, 0f);
                negativeTorque += Mathf.Max(-fullTorque, 0f);
            }
        }

        /// <summary>
        /// 制导层换算：把绝对期望力/力矩（Map 局部系）归一化为分配器指令。
        /// 按力符号匹配该方向出力上限（与 Allocate 内 DirectionalScale 的方向选择
        /// 同语义），超出布局能力的期望饱和到 ±1，该方向无可用推进器时指令为 0。
        /// 制导（ShipAutopilot）与未来舰船 AI 共用——期望力必须经此换算再进
        /// Allocate，得到的出力与油门才是布局内真实可达值。
        /// </summary>
        public static Command ForceToCommand(MassReport report, Vector2 force, float torque)
        {
            if (report == null || report.thrusters.Count == 0)
            {
                return default;
            }
            RefreshOperational(report);
            MeasureAuthority(report, out float positiveX, out float negativeX,
                out float positiveZ, out float negativeZ, out float positiveTorque, out float negativeTorque);
            return new Command
            {
                fx = NormalizeAxis(force.x, positiveX, negativeX),
                fz = NormalizeAxis(force.y, positiveZ, negativeZ),
                torque = NormalizeAxis(torque, positiveTorque, negativeTorque)
            };
        }

        /// <summary>单轴归一化：正值除正向上限、负值除负向上限，clamp 到 [-1,1]。</summary>
        private static float NormalizeAxis(float value, float positiveAuthority, float negativeAuthority)
        {
            if (value > 1e-6f)
            {
                return Mathf.Min(1f, value / Mathf.Max(positiveAuthority, 1f));
            }
            if (value < -1e-6f)
            {
                return Mathf.Max(-1f, value / Mathf.Max(negativeAuthority, 1f));
            }
            return 0f;
        }

        private static float DirectionalScale(float command, float positiveAuthority, float negativeAuthority)
        {
            float scale = command > 1e-6f
                ? positiveAuthority
                : (command < -1e-6f ? negativeAuthority : Mathf.Max(positiveAuthority, negativeAuthority));
            return Mathf.Max(scale, 1f);
        }

        /// <summary>
        /// 半隐式欧拉积分一个 tick（积分对象 = 地图容器，一张图一个刚体，§5.3「每个对象
        /// 只积分一次」）。pilotFrameAngle = 驾驶船的船帧角 σ（辅助轴定义在主台参考系；
        /// 非驾驶容器传 0）。
        /// assist* 为真的轴做两层收口：
        /// ① "反推不越过零"（推进器反推的过冲保护）；
        /// ② 残差直接衰减——低于 ResidualSpeed/ResidualOmega 的剩余漂移按固定减速度
        ///    MoveTowards 到零（用户决策：不要求推进器布局完美消除微小漂移）。
        /// 被动阻力（解耦架构 §11.1，默认开启）：按半衰期对速度/角速度指数衰减，
        /// 与飞行辅助的主动反推并存；不是推进器指令——不消耗分配器油门、不生成火焰、
        /// 不触发拆船；漂移积分（零力）同样生效（纯漂移也因此会停下）。
        /// 速度/力都在船位置处的切空间基下表达；位置沿球面推进（OrbitSphere.Move），
        /// 质心不变量换成球面三步走：锚→质心（旧角）→平移→回锚（新角），
        /// 切空间速度随后平行输运到新锚点的基。
        /// </summary>
        public static void Integrate(SpaceObject obj, MassReport report, Vector2 force, float torque, float dt,
            bool assistX, bool assistZ, bool assistW, float pilotFrameAngle = 0f)
        {
            SpacePose pose = obj.pose;
            Vector3 oldDir = pose.position;
            // 分配器与推进器几何都在稳定 Map 局部系；只有积分入切空间速度时旋转一次。
            Vector2 accel = SpaceTransform.Rotate(force, pose.angle) / report.mass;
            float alpha = torque / report.inertia;

            Vector2 velocity = pose.velocity + accel * dt;
            float angularVelocity = pose.angularVelocity + alpha * dt;

            // 被动阻力：v ← v · 2^(−dt/halfLife)（精确的每 tick 半衰期指数衰减）。
            // 开关/参数变化即时生效；与 MPC 预测（OnlineMpcGuidance）同一定义。
            // 弹体按 tag 豁免（太空战架构 §3.1：无推进器的飞行体不受阻力减速；
            // shuttle 与船照常受阻力）。
            (float linearDecay, float angularDecay) = obj.AppliesPassiveDrag
                ? PassiveDragFactors(dt)
                : (1f, 1f);
            velocity *= linearDecay;
            angularVelocity *= angularDecay;

            // 辅助轴与 W/S、A/D 一样定义在主驾驶台参考系，不能按切空间 x/y 直接截断。
            float pilotAngle = pose.angle + pilotFrameAngle;
            Vector2 previousPilotVelocity = SpaceTransform.RotateInverse(pose.velocity, pilotAngle);
            Vector2 pilotVelocity = SpaceTransform.RotateInverse(velocity, pilotAngle);
            if (assistX && previousPilotVelocity.x != 0f
                && pilotVelocity.x * previousPilotVelocity.x < 0f)
            {
                pilotVelocity.x = 0f;
            }
            if (assistZ && previousPilotVelocity.y != 0f
                && pilotVelocity.y * previousPilotVelocity.y < 0f)
            {
                pilotVelocity.y = 0f;
            }
            if (assistW && pose.angularVelocity != 0f && angularVelocity * pose.angularVelocity < 0f)
            {
                angularVelocity = 0f;
            }

            // 残差直接衰减：微小剩余漂移不依赖推进器几何，按固定减速度收尾到零。
            if (assistX && Mathf.Abs(pilotVelocity.x) < ResidualSpeed)
            {
                pilotVelocity.x = Mathf.MoveTowards(pilotVelocity.x, 0f, ResidualDecel * dt);
            }
            if (assistZ && Mathf.Abs(pilotVelocity.y) < ResidualSpeed)
            {
                pilotVelocity.y = Mathf.MoveTowards(pilotVelocity.y, 0f, ResidualDecel * dt);
            }
            if (assistW && Mathf.Abs(angularVelocity) < ResidualOmega)
            {
                angularVelocity = Mathf.MoveTowards(angularVelocity, 0f, ResidualAlphaDecel * dt);
            }
            velocity = SpaceTransform.Rotate(pilotVelocity, pilotAngle);

            // 质心球面方向 = Move(锚, Rotate(comLocal, 旧角))；平移后按新角移回锚，
            // 与旧平面版 "position + Rotate(comLocal, angle)" 的不变量一一对应。
            Vector3 comDir = OrbitSphere.Move(oldDir, SpaceTransform.Rotate(report.comLocal, pose.angle));
            Vector3 comMoved = OrbitSphere.Move(comDir, velocity * dt);
            pose.angle = SpaceTransform.WrapAngle(pose.angle + angularVelocity * dt);
            pose.position = OrbitSphere.Move(comMoved, -SpaceTransform.Rotate(report.comLocal, pose.angle));

            // 平行输运：把切空间速度从旧锚点的基重表达到新锚点的基（每步 O(基旋转)，
            // 保证"恒东向速度"沿纬线、恒北向速度沿经线等行星固连语义成立）。
            OrbitSphere.Basis(oldDir, out Vector3 east, out Vector3 north);
            Vector3 velocityWorld = east * velocity.x + north * velocity.y;
            OrbitSphere.Basis(pose.position, out east, out north);
            pose.velocity = new Vector2(Vector3.Dot(velocityWorld, east), Vector3.Dot(velocityWorld, north));
            pose.angularVelocity = angularVelocity;
            obj.pose = pose;
        }

        /// <summary>
        /// 被动阻力的每 tick 衰减因子（线速度, 角速度）。开关关闭时返回 (1,1)（零阻力）；
        /// 半衰期非正视为关闭。OnlineMpcGuidance 的预测模型必须使用同一因子定义，
        /// 避免「预测无阻力、实际有阻力」的持续误差（解耦架构 §11.1 设计要求）。
        /// </summary>
        public static (float linear, float angular) PassiveDragFactors(float dt)
        {
            DeltaVSettings settings = DeltaVMod.CurrentSettings;
            if (settings == null || !settings.passiveDrag)
            {
                return (1f, 1f);
            }
            float linear = settings.dragLinearHalfLifeTicks > 0
                ? Mathf.Pow(0.5f, dt * 60f / settings.dragLinearHalfLifeTicks)
                : 1f;
            float angular = settings.dragAngularHalfLifeTicks > 0
                ? Mathf.Pow(0.5f, dt * 60f / settings.dragAngularHalfLifeTicks)
                : 1f;
            return (linear, angular);
        }

        /// <summary>
        /// 无图弹体（轻导弹等）积分用的共享哑报告（tag 快路径，太空战架构 §3.3）：
        /// 单位质量/零质心/单位惯量、无推进器——力恒为零，积分退化为
        /// 「速度 × dt 的球面平移 + 平行输运」，与容器积分同一条 Integrate 核心
        ///（数据一致性），不经过按图缓存的质量报告。只读共享，绝不 Allocate。
        /// </summary>
        public static readonly MassReport PointMassReport = new MassReport
        {
            mass = 1f,
            comLocal = Vector2.zero,
            inertia = 1f
        };

        /// <summary>2D 叉积（z 分量）：逆时针为正，与 SpaceTransform 角度约定一致。</summary>
        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        // ---- 报告缓存 ----

        private sealed class CachedReport
        {
            public MassReport report;
            public int measuredAtTick = -1;
            public int structureVersion = -1;
        }

        private static readonly Dictionary<Map, CachedReport> Cache = new Dictionary<Map, CachedReport>();

        public static MassReport GetReport(Map map)
        {
            if (map == null || map.Disposed)
            {
                return null;
            }
            if (!Cache.TryGetValue(map, out CachedReport cached))
            {
                cached = new CachedReport();
                Cache[map] = cached;
            }
            int now = Find.TickManager.TicksGame;
            if (cached.report == null || SubstructureOwnership.CacheNeedsRefresh(cached.structureVersion, cached.measuredAtTick))
            {
                MassReport fresh = Measure(map);
                fresh.version = (cached.report?.version ?? 0) + 1;
                if (DeltaVLog.Enabled(DeltaVLogModule.Physics)
                    && cached.report != null && MassModelChanged(cached.report, fresh))
                {
                    DeltaVLog.Message(DeltaVLogModule.Physics,
                        $"mass model changed on '{map.Parent?.Label ?? map.ToString()}': mass {cached.report.mass:F0}->{fresh.mass:F0}, " +
                        $"thrusters {cached.report.thrusters.Count}->{fresh.thrusters.Count}, com {cached.report.comLocal:F2}->{fresh.comLocal:F2}, " +
                        $"inertia {cached.report.inertia:F0}->{fresh.inertia:F0}");
                }
                cached.report = fresh;
                cached.measuredAtTick = now;
                cached.structureVersion = SubstructureOwnership.GlobalStructureVersion;
            }
            return cached.report;
        }

        public static float AvailableThrust(Map map)
        {
            MassReport report = GetReport(map);
            if (report == null) return 0f;
            float total = 0f;
            foreach (Thruster thruster in report.thrusters)
                if ((thruster.thing == null && thruster.operational)
                    || (thruster.thing != null && ThrusterPower.IsOperational(thruster.thing)))
                    total += thruster.thrust;
            return total;
        }

        private static bool MassModelChanged(MassReport oldReport, MassReport newReport)
        {
            return Mathf.Abs(oldReport.mass - newReport.mass) > 0.01f
                || (oldReport.comLocal - newReport.comLocal).sqrMagnitude > 0.0001f
                || Mathf.Abs(oldReport.inertia - newReport.inertia) > 1f
                || oldReport.thrusters.Count != newReport.thrusters.Count;
        }

        /// <summary>
        /// 清零该图的分配器油门（表现层熄火）：仅当报告已在缓存时生效，不触发扫描。
        /// 供绕过积分器直接改位姿的出口（自动驾驶到达/取消等）立即熄焰；
        /// 常规停火由下一次 Allocate 的全零解自然覆盖，无需调用。
        /// </summary>
        public static void ClearThrottles(Map map)
        {
            if (map == null || !Cache.TryGetValue(map, out CachedReport cached) || cached.report == null)
            {
                return;
            }
            for (int i = 0; i < cached.report.thrusters.Count; i++)
            {
                cached.report.thrusters[i].throttle = 0f;
            }
            cached.report.throttlesWrittenAt = Time.time;
        }

        /// <summary>读档 / 场景重置时清空（与 ShellRenderer 的缓存自愈纪律一致）。</summary>
        public static void ClearCache()
        {
            Cache.Clear();
        }

        public static void Forget(Map map)
        {
            if (map == null) return;
            Cache.Remove(map);
            VirtualThrustersByMap.Remove(map);
        }

        /// <summary>
        /// 换档/dev 重建时清除全部可重建物理缓存。普通 ClearCache 不能清虚拟推进器：
        /// 它也用于同一局游戏内的跨图搬运，清掉会让仍在飞的 shuttle 失去动力。
        /// </summary>
        public static void ClearRuntimeCaches()
        {
            Cache.Clear();
            VirtualThrustersByMap.Clear();
        }
    }
}
