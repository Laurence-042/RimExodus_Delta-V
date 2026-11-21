using System;
﻿using System.Collections.Generic;
using System.Linq;
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
/// - 手动归一化输入与自动驾驶期望力都在对象执行入口汇入同一分配器，
///   期望力经 ForceToCommand 归一化，禁止绕过分配器直接合成力——机动因此始终
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
    /// 结构变化即时更新报告，专用货柜载重按游戏 tick 合并更新。
    /// </summary>
    public static partial class ShipPhysics
    {
        // ---- 阶段2 常量（数值调参记录进文档；物理细节按 story「原型完成后再设计」演进）----

        public const float MassPerDeckCell = 1f;

        public const float MassPerBuilding = 4f;

        /// <summary>裸数值布局的默认推力；实体推进器由 ThrusterPower.BaseThrust 读取 Def。</summary>
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
            public ThrusterVisualProfile visuals;
            internal ThrusterVisualProfile normalVisuals;
            internal CompSpaceShuttle shuttle;
            internal float baseThrust;
            internal bool boostable;

            /// <summary>作用点（ship local，原点 = 地图几何中心）：占用矩形中心。</summary>
            public Vector2 localPos;

            /// <summary>ship local 单位推力方向。</summary>
            public Vector2 dir;

            /// <summary>动力来源判定（ThrusterPower 抽象）：不可用的推进器不参与分配。
            /// 在对象读取驾驶信息或执行出力时刷新；数值求解只消费这一快照。</summary>
            public bool operational;

            /// <summary>沿 dir 的非负推力大小；实体的有符号 Def 推力在适配入口归一化。</summary>
            public float thrust = ThrusterThrust;

            /// <summary>本 tick 分配到的油门 [0,1]。唯一写入者是 Allocate（驾驶/
            /// 自动驾驶/未来舰船 AI 共用同一分配器）；火焰表现层（地图路径与
            /// 船壳路径）只读此值。运行时派生状态，不参与物理积分或存档。</summary>
            public float throttle;

            internal Thruster NominalSnapshot()
            {
                var snapshot = (Thruster)MemberwiseClone();
                snapshot.thrust = baseThrust;
                snapshot.throttle = 0f;
                snapshot.operational = true;
                return snapshot;
            }
        }

        public sealed class MassReport
        {
            public bool allowsAngularMotion = true;
            public float mass;
            public Vector2 comLocal;
            public float inertia;
            public readonly List<Thruster> thrusters;

            public MassReport() : this(new List<Thruster>()) { }
            internal MassReport(List<Thruster> thrusters) => this.thrusters = thrusters;

            /// <summary>动力模型版本：结构重测、推进器可用性或加力改变时递增，
            /// 使对象的分配结果与自动驾驶预览立即失效。不存档。</summary>
            public int version;

            // 分配器工作区。真实分配与只读预览共用同一求解核心；数组随质量报告重建，
            // 热路径不产生临时分配。只有 Allocate 会把结果提交到 Thruster.throttle。
            internal float[] allocationScratch;
            internal float[] previewScratch;
            internal Vector3[] allocationColumns;
            internal float[] allocationDenominators;

            /// <summary>最近一次执行出力的游戏 tick；复用分配结果也刷新。
            /// 暂停不会使油门过期，初始／清零为 -1。不存档。</summary>
            public int throttlesWrittenTick = -1;
            internal bool throttlesNeedClearing;
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
        /// fx/fz/torque 指令对真实可达广义力的中心差分。MPC 使用正反向平均能力
        /// 作为力单位，方向能力决定变量上下界；期望力进入 ForceToCommand → Allocate。
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

            // MPC 的变量以正/负向平均能力为同一个力单位。方向差异只进入上下界，
            // 不能预测时用平均能力、执行时再按符号换一次单位（会高估弱反推）。
            public void ControlBounds(int axis, out double lower, out double upper)
            {
                float positive = axis == 0 ? positiveX : axis == 1 ? positiveZ : positiveTorque;
                float negative = axis == 0 ? negativeX : axis == 1 ? negativeZ : negativeTorque;
                float scale = (positive + negative) * 0.5f;
                lower = scale > 1e-6f ? -negative / scale : 0.0;
                upper = scale > 1e-6f ? positive / scale : 0.0;
            }

            public void DesiredWrench(Vector3 control, out Vector2 force, out float torque)
            {
                force = new Vector2(control.x * (positiveX + negativeX) * 0.5f,
                    control.y * (positiveZ + negativeZ) * 0.5f);
                torque = control.z * (positiveTorque + negativeTorque) * 0.5f;
            }
        }

        // ---- 质量模型 ----

        internal struct MassMoments
        {
            internal double mass;
            private double momentX, momentZ, originInertia;

            internal void Add(MassMoments contribution)
            {
                mass += contribution.mass;
                momentX += contribution.momentX;
                momentZ += contribution.momentZ;
                originInertia += contribution.originInertia;
            }

            internal void Add(double weight, Vector2 center, double intrinsicPerMass = 0d)
            {
                double x = center.x, z = center.y;
                mass += weight;
                momentX += weight * x;
                momentZ += weight * z;
                originInertia += weight * (x * x + z * z + intrinsicPerMass);
            }

            internal void AddBuilding(CellRect rect, Vector2 origin, double weight)
            {
                var center = new Vector2((rect.minX + rect.maxX + 1) * 0.5f - origin.x,
                    (rect.minZ + rect.maxZ + 1) * 0.5f - origin.y);
                Add(weight, center, (rect.Width * rect.Width + rect.Height * rect.Height) / 12d);
            }

            internal bool WriteTo(MassReport report)
            {
                float total = (float)System.Math.Max(1d, mass);
                Vector2 center = mass < 1d ? Vector2.zero : new Vector2((float)(momentX / mass), (float)(momentZ / mass));
                // Parallel-axis theorem. Keep the unclamped moment about the map origin for later deltas.
                float inertia = mass < 1d ? 1f : (float)System.Math.Max(1d,
                    originInertia - (momentX * momentX + momentZ * momentZ) / mass);
                bool changed = report.mass != total || !report.comLocal.Equals(center) || report.inertia != inertia;
                report.mass = total;
                report.comLocal = center;
                report.inertia = inertia;
                return changed;
            }
        }

        /// <summary>实体推进器的唯一数值适配；所属船决定是否纳入布局。</summary>
        public static Thruster BuildInstalledThruster(Thing thing, bool readOperational = true)
        {
            CompThrusterPowerLimit limit = thing.TryGetComp<CompThrusterPowerLimit>();
            float configured = ThrusterPower.BaseThrust(thing.def);
            float thrust = Mathf.Abs(configured) * (limit == null ? 1f : Mathf.Clamp(limit.powerPercent, 0f, 100f) * 0.01f);
            return new Thruster
            {
                thing = thing,
                localPos = SpaceTransform.BoundsCenterOfCells(thing.OccupiedRect().Cells)
                    - SpaceTransform.ShipLocalOrigin(thing.Map),
                dir = ThrustDir(thing.Rotation) * (configured < 0f ? -1f : 1f),
                operational = readOperational && ThrusterPower.IsOperational(thing) && thrust > 0f,
                thrust = thrust
            };
        }

        /// <summary>
        /// 建筑质量：def 带 Mass stat（如 LargeThruster=30）用 stat 实值，否则用
        /// MassPerBuilding。
        /// </summary>
        internal static double BuildingWeight(Building building) =>
            BuildingMass(building) + (building.TryGetComp<CompStructureMass>()?.PublishedMass ?? 0d);

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
        internal static void Allocate(MassReport report, Command command, out Vector2 force, out float torque)
        {
            EnsureAllocatorScratch(report);
            if (report == null || report.thrusters.Count == 0)
            {
                force = Vector2.zero;
                torque = 0f;
                return;
            }
            SolveAllocation(report, command, report.allocationScratch, out force, out torque);
            for (int i = 0; i < report.thrusters.Count; i++)
            {
                report.thrusters[i].throttle = report.allocationScratch[i];
            }
            MarkThrottlesWritten(report);
        }

        internal static void PreviewAllocation(MassReport report, Command command, out Vector2 force, out float torque)
        {
            EnsureAllocatorScratch(report);
            SolveAllocation(report, command, report.previewScratch, out force, out torque);
        }

        internal static void MarkThrottlesWritten(MassReport report)
        {
            report.throttlesWrittenTick = Find.TickManager.TicksGame;
            report.throttlesNeedClearing = true;
        }

        /// <summary>
        /// 构造制导器使用的只读局部控制模型。采样调用与 Allocate 完全相同的分配核心，
        /// 但结果只写 previewScratch，不触碰真实油门及火焰新鲜度。
        /// </summary>
        internal static ControlModel BuildControlModel(MassReport report, ControlModel reuse = null)
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
        internal static float PreviewBrakingAcceleration(MassReport report, Vector2 towardTargetLocal)
        {
            if (report == null || report.mass <= 0f || towardTargetLocal.sqrMagnitude < 1e-8f)
                return 0f;
            EnsureAllocatorScratch(report);
            Vector2 reverse = -towardTargetLocal.normalized;
            var command = new Command { fx = reverse.x, fz = reverse.y };
            SolveAllocation(report, command, report.previewScratch, out Vector2 force, out _);
            return Mathf.Max(0f, Vector2.Dot(force, reverse) / report.mass);
        }

        /// <summary>只读预览指定转向的实际角加速度；与真实 Allocate 共用分配核心。</summary>
        internal static float PreviewAngularAcceleration(MassReport report, int direction)
        {
            if (report == null || report.inertia <= 0f || direction == 0) return 0f;
            EnsureAllocatorScratch(report);
            SolveAllocation(report, new Command { torque = direction > 0 ? 1f : -1f },
                report.previewScratch, out _, out float torque);
            return Mathf.Max(0f, torque * (direction > 0 ? 1f : -1f) / report.inertia);
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
                report.allocationColumns = new Vector3[count];
                report.allocationDenominators = new float[count];
            }
        }

        // Live state is read at the object boundary. Allocation and preview methods below are numeric only.
        internal static void RefreshOperational(MassReport report)
        {
            for (int i = 0; i < report.thrusters.Count; i++)
            {
                Thruster thruster = report.thrusters[i];
                if (thruster.shuttle != null)
                {
                    float multiplier = thruster.boostable ? thruster.shuttle.BoostMultiplier : 1f;
                    float thrust = thruster.baseThrust * multiplier;
                    if (thruster.thrust != thrust) { thruster.thrust = thrust; report.version++; }
                    thruster.visuals = multiplier > 1f ? thruster.shuttle.ShuttleProps.afterburner
                        : thruster.normalVisuals;
                }
                if (thruster.thing == null)
                {
                    continue; // 无实体推进器的可用性由船体提供，不用建筑电力模型覆盖。
                }
                bool operational = ThrusterPower.IsOperational(thruster.thing) && thruster.thrust > 0f;
                if (operational != thruster.operational) { thruster.operational = operational; report.version++; }
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

            // Geometry and weights do not change during coordinate descent.
            for (int i = 0; i < report.thrusters.Count; i++)
            {
                Thruster thruster = report.thrusters[i];
                if (!thruster.operational) continue;
                Vector2 fullForce = thruster.dir * thruster.thrust;
                Vector3 axis = new Vector3(fullForce.x / scaleX, fullForce.y / scaleZ,
                    Cross(thruster.localPos - report.comLocal, fullForce) / scaleTorque);
                report.allocationColumns[i] = axis;
                report.allocationDenominators[i] = weightX * axis.x * axis.x
                    + weightZ * axis.y * axis.y + weightTorque * axis.z * axis.z + AllocationRegularization;
            }

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
                    Vector3 axis = report.allocationColumns[index];
                    float axisX = axis.x, axisZ = axis.y, axisTorque = axis.z;
                    float denominator = report.allocationDenominators[index];
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
        internal static Command ForceToCommand(MassReport report, Vector2 force, float torque)
        {
            if (report == null || report.thrusters.Count == 0)
            {
                return default;
            }
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
        /// 从锚点真实速度派生质心速度，力转到质心切基后积分；再由同一刚体点
        /// 逆变换恢复地图原点的位置与速度。质心不另存，不能把其速度写成锚点速度。
        /// </summary>
        internal static void Integrate(SpaceObject obj, MassReport report, Vector2 force, float torque, float dt,
            bool assistX, bool assistZ, bool assistW, float pilotFrameAngle = 0f, DriveInputBasis? inputBasis = null)
        {
            SpacePose pose = obj.pose;
            SpacePose center = SpaceTransform.RigidPointPose(pose, report.comLocal, 0f);
            // 分配器与推进器几何都在稳定 Map 局部系；只有积分入切空间速度时旋转一次。
            Vector2 accel = SpaceTransform.TransportTangent(SpaceTransform.Rotate(force, pose.angle),
                pose.position, center.position) / report.mass;
            float alpha = torque / report.inertia;

            Vector2 velocity = center.velocity + accel * dt;
            float angularVelocity = report.allowsAngularMotion ? pose.angularVelocity + alpha * dt : 0f;

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
            DriveInputBasis basis = inputBasis ?? DriveInputBasis.ForAngle(pilotFrameAngle);
            Vector2 previousPilotVelocity = basis.FromMap(SpaceTransform.RotateInverse(
                SpaceTransform.TransportVelocity(center, pose.position), pose.angle));
            Vector2 pilotVelocity = basis.FromMap(SpaceTransform.RotateInverse(
                SpaceTransform.TransportTangent(velocity, center.position, pose.position), pose.angle));
            Vector2 unassistedPilotVelocity = pilotVelocity;
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
            velocity += SpaceTransform.TransportTangent(SpaceTransform.Rotate(
                basis.ToMap(pilotVelocity - unassistedPilotVelocity), pose.angle), pose.position, center.position);

            center.velocity = velocity;
            center.angularVelocity = angularVelocity;
            obj.pose = DriftCenter(center, report.comLocal, dt);
        }

        internal static SpacePose DriftAfterContact(SpacePose pose, MassReport report, float dt) =>
            DriftCenter(SpaceTransform.RigidPointPose(pose, report.comLocal, 0f), report.comLocal, dt);

        // 原接触冲量工具。预读时保留全部能量；结算后按实际耗能写入等大反向冲量。
        // 使用命中前同一份质量报告，不把随动弹丸当成独立的单位质量刚体。
        internal static float ApplyHullContactImpulse(SpaceObject a, MassReport ma, SpaceObject b, MassReport mb,
            Vector3 point, Vector2 normal, out float normalEnergy, float remainingEnergyFraction = 0f)
        {
            normalEnergy = 0f;
            if (ma == null || mb == null || ma.mass <= 0f || mb.mass <= 0f) return 0f;
            Vector2 localA = SpaceTransform.UniverseToShipLocal(a.pose, point);
            Vector2 localB = SpaceTransform.UniverseToShipLocal(b.pose, point);
            Vector2 va = SpaceTransform.VelocityAtLocalPoint(a.pose, localA, point);
            Vector2 vb = SpaceTransform.VelocityAtLocalPoint(b.pose, localB, point);
            float closing = Vector2.Dot(vb - va, normal);
            if (closing <= .00001f) return 0f;
            float EffectiveAngularMass(SpaceObject body, MassReport report, Vector2 local)
            {
                if (!report.allowsAngularMotion || report.inertia <= 0f) return 0f;
                Vector2 arm = local - report.comLocal;
                Vector2 axis = SpaceTransform.RotateInverse(
                    SpaceTransform.TransportTangent(normal, point, body.pose.position), body.pose.angle);
                float lever = arm.x * axis.y - arm.y * axis.x;
                return lever * lever / report.inertia;
            }
            float inverseMass = 1f / ma.mass + 1f / mb.mass
                + EffectiveAngularMass(a, ma, localA) + EffectiveAngularMass(b, mb, localB);
            float impulseMagnitude = closing / inverseMass;
            normalEnergy = .5f * impulseMagnitude * closing;
            impulseMagnitude *= 1f - Mathf.Sqrt(Mathf.Clamp01(remainingEnergyFraction));
            if (impulseMagnitude > 0f)
            {
                Vector2 impulse = normal * impulseMagnitude;
                ApplyAt(a, ma, localA, point, impulse);
                ApplyAt(b, mb, localB, point, -impulse);
            }
            return closing;
        }

        private static SpacePose DriftCenter(SpacePose center, Vector2 localCenter, float dt)
        {
            Vector3 from = center.position;
            center.direction = OrbitSphere.Move(center.direction, center.velocity * dt);
            center.angle = SpaceTransform.WrapAngle(center.angle + center.angularVelocity * dt);
            center.velocity = SpaceTransform.TransportTangent(center.velocity, from, center.position);
            return localCenter == Vector2.zero ? center
                : SpaceTransform.AlignLocalPoint(center, Vector2.zero, localCenter, 0f);
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

        /// <summary>恒定加速度下组合 60 Hz 半隐式积分的预测系数。
        /// v' = decay*v + velocityGain*a；p' = p + velocityGain*v + positionGain*a。
        /// 非整数 tick 的预测段使用同一几何级数的连续延拓。</summary>
        public static void HeldInputFactors(double seconds, float decayPerTick,
            out double decay, out double velocityGain, out double positionGain)
        {
            const double tick = 1.0 / 60.0;
            double r = decayPerTick;
            if (r >= 1.0 - 1e-9)
            {
                decay = 1.0;
                velocityGain = seconds;
                positionGain = 0.5 * seconds * (seconds + tick);
                return;
            }
            double count = seconds / tick;
            decay = System.Math.Pow(r, count);
            double sum = r * (1.0 - decay) / (1.0 - r);
            velocityGain = tick * sum;
            positionGain = tick * tick * r / (1.0 - r) * (count - sum);
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

        /// <summary>Exact contribution ledger in the stable map frame.</summary>
        internal sealed class MassIndex
        {
            private readonly Vector2 origin;
            private readonly HashSet<IntVec3> foundations = new HashSet<IntVec3>();
            private readonly Dictionary<Building, (CellRect footprint, double weight)> buildings =
                new Dictionary<Building, (CellRect, double)>();
            private MassMoments moments;

            internal MassIndex(Map map) => origin = SpaceTransform.ShipLocalOrigin(map);
            internal MassMoments Moments => moments;
            internal IEnumerable<IntVec3> FoundationCells => foundations;

            internal bool SetFoundation(IntVec3 cell, bool present)
            {
                if (present ? !foundations.Add(cell) : !foundations.Remove(cell)) return false;
                moments.Add(present ? MassPerDeckCell : -MassPerDeckCell,
                    new Vector2(cell.x + 0.5f - origin.x, cell.z + 0.5f - origin.y));
                return true;
            }

            internal void SetBuilding(Building building, CellRect footprint, double weight)
            {
                if (buildings.TryGetValue(building, out var before))
                {
                    if (before.footprint == footprint)
                    {
                        if (before.weight == weight) return;
                        moments.AddBuilding(footprint, origin, weight - before.weight);
                        buildings[building] = (footprint, weight);
                        return;
                    }
                    moments.AddBuilding(before.footprint, origin, -before.weight);
                }
                moments.AddBuilding(footprint, origin, weight);
                buildings[building] = (footprint, weight);
            }

            internal void RemoveBuilding(Building building)
            {
                if (buildings.Remove(building, out var previous))
                    moments.AddBuilding(previous.footprint, origin, -previous.weight);
            }
        }

        internal sealed class CachedReport : IDisposable
        {
            private sealed class HullLayout
            {
                internal IDisposable Subscription;
                internal IReadOnlyList<Thruster> VirtualLayout;
                internal readonly HashSet<Thruster> Entries = new HashSet<Thruster>();
            }

            private const ShipStructureAspect HullChanges = ShipStructureAspect.Propulsion | ShipStructureAspect.Membership
                | ShipStructureAspect.Footprint | ShipStructureAspect.Primary | ShipStructureAspect.Representation;
            internal MassReport report = new MassReport();
            private readonly SpaceObject container;
            private readonly ShipStructureIndex.MapView view;
            private readonly IDisposable watch;
            private readonly Dictionary<ShipStructure, HullLayout> hulls = new Dictionary<ShipStructure, HullLayout>();
            private readonly Dictionary<Thing, Thruster> installed = new Dictionary<Thing, Thruster>();
            private int massRevision = -1;
            private MassMoments measured;
            private bool entriesChanged;

            internal CachedReport(ShipStructureIndex index, SpaceObject container, ShipStructureIndex.MapView view)
            {
                this.container = container;
                this.view = view;
                if (container is MapSpaceObject)
                    RefreshHulls();
                else
                    report.thrusters.AddRange(container.BuildThrusters());
                report.version = 1;
                entriesChanged = false;
                watch = index.WatchMap(container.map, ShipStructureAspect.Membership | ShipStructureAspect.Primary
                    | ShipStructureAspect.Foundation | ShipStructureAspect.Terrain | ShipStructureAspect.Footprint
                    | ShipStructureAspect.Representation, OnMapChanged);
            }

            internal bool Matches(SpaceObject next) => container == next;

            internal MassReport ReadReport()
            {
                if (massRevision != view.MassRevision)
                {
                    measured = view.TotalMass();
                    massRevision = view.MassRevision;
                    if (measured.WriteTo(report)) report.version++;
                }
                return report;
            }

            private void OnMapChanged(ShipStructureBatch batch)
            {
                if (container is not MapSpaceObject) return;
                bool representationChanged = batch.Changes.Any(change => change.Aspect == ShipStructureAspect.Representation);
                if (representationChanged || view.Shuttles.Any() || batch.Changes.Any(change => change.Aspect == ShipStructureAspect.Primary
                    || change.Thing.TryGetComp<CompDeltaVPilotConsole>() != null
                    || change.Thing.TryGetComp<CompSpaceShuttle>() != null))
                {
                    RefreshHulls();
                    if (entriesChanged || representationChanged) RefreshDriveView();
                }
            }

            private void RefreshHulls()
            {
                var active = new HashSet<ShipStructure>();
                bool angular = true;
                foreach (ShipBase ship in container.Ships)
                {
                    if (!ship.CanDriveIn(container) || ship.StructuralState is not { } structure || !active.Add(structure)) continue;
                    angular &= ship.AllowsAngularMotion;
                    if (hulls.ContainsKey(structure)) continue;
                    var layout = new HullLayout();
                    hulls.Add(structure, layout);
                    layout.Subscription = structure.Watch(HullChanges, batch => OnHullChanged(structure, layout, batch));
                    ReplaceEntries(layout, structure);
                }
                foreach (var entry in hulls.ToArray())
                {
                    if (active.Contains(entry.Key)) continue;
                    entry.Value.Subscription.Dispose();
                    foreach (var thruster in entry.Value.Entries) RemoveEntry(thruster);
                    hulls.Remove(entry.Key);
                }
                if (report.allowsAngularMotion != angular)
                {
                    report.allowsAngularMotion = angular;
                    report.version++;
                }
            }

            private void OnHullChanged(ShipStructure structure, HullLayout layout, ShipStructureBatch batch)
            {
                if (!hulls.TryGetValue(structure, out var live) || live != layout) return;
                if (structure.Vessel is ShuttleShip || batch.Changes.Any(c => c.Aspect == ShipStructureAspect.Representation))
                {
                    ReplaceEntries(layout, structure);
                }
                else
                {
                    foreach (var change in batch.Changes)
                    {
                        if (change.Thing is not Building building) continue;
                        Thruster current = structure.InstalledThruster(building);
                        if (installed.TryGetValue(building, out var previous) && layout.Entries.Contains(previous))
                        {
                            if (previous == current)
                            {
                                if (change.Aspect == ShipStructureAspect.Propulsion) report.version++;
                                continue;
                            }
                            layout.Entries.Remove(previous);
                            RemoveEntry(previous);
                        }
                        if (current != null && layout.Entries.Add(current)) AddEntry(current);
                    }
                }
                if (entriesChanged) RefreshDriveView();
            }

            private void ReplaceEntries(HullLayout layout, ShipStructure structure)
            {
                IEnumerable<Thruster> entries = structure.InstalledThrusters;
                if (structure.Vessel is ShuttleShip)
                {
                    var virtualLayout = structure.VirtualThrusters;
                    if (ReferenceEquals(layout.VirtualLayout, virtualLayout)) return;
                    layout.VirtualLayout = virtualLayout;
                    entries = virtualLayout.Select(thruster => thruster.NominalSnapshot());
                }
                var next = new HashSet<Thruster>(entries);
                foreach (Thruster old in layout.Entries.Where(t => !next.Contains(t)).ToArray())
                {
                    RemoveEntry(old);
                    layout.Entries.Remove(old);
                }
                foreach (Thruster current in next)
                    if (layout.Entries.Add(current)) AddEntry(current);
            }

            private void AddEntry(Thruster entry)
            {
                if (entry.thing != null)
                {
                    if (installed.TryGetValue(entry.thing, out var former)) RemoveEntry(former);
                    installed[entry.thing] = entry;
                    entry.operational = ThrusterPower.IsOperational(entry.thing) && entry.thrust > 0f;
                }
                report.thrusters.Add(entry);
                entriesChanged = true;
            }

            private void RemoveEntry(Thruster entry)
            {
                if (entry.thing != null && installed.TryGetValue(entry.thing, out var current) && current == entry)
                    installed.Remove(entry.thing);
                entriesChanged |= report.thrusters.Remove(entry);
            }

            internal void NotifyOperationalChange(Thruster entry)
            {
                if (installed.TryGetValue(entry.thing, out var current) && entry == current) report.version++;
            }

            private void RefreshDriveView()
            {
                var next = new MassReport(report.thrusters)
                {
                    version = report.version + 1,
                    allowsAngularMotion = report.allowsAngularMotion
                };
                measured.WriteTo(next);
                report = next;
                entriesChanged = false;
            }

            public void Dispose()
            {
                watch.Dispose();
                foreach (var hull in hulls.Values) hull.Subscription.Dispose();
                hulls.Clear();
            }
        }

        internal static MassReport GetReport(SpaceObject container)
        {
            Map map = container.map;
            if (map == null || map.Disposed) return null;
            ShipStructureIndex index = Current.Game.GetComponent<UniverseManager>().ShipStructure;
            ShipStructureIndex.MapView view = index.For(map);
            if (view.PhysicsReport is not { } cached || !cached.Matches(container))
            {
                view.ForgetPhysics();
                view.PhysicsReport = cached = new CachedReport(index, container, view);
            }
            return cached.ReadReport();
        }

        internal static Thruster InstalledThruster(ShipStructure structure, Building building)
        {
            Thruster thruster = structure.InstalledThruster(building);
            if (thruster == null) return null;
            bool operational = ThrusterPower.IsOperational(building) && thruster.thrust > 0f;
            if (thruster.operational != operational)
            {
                thruster.operational = operational;
                PhysicsView(building.Map)?.PhysicsReport?.NotifyOperationalChange(thruster);
            }
            return thruster;
        }

        /// <summary>
        /// 清零该图的分配器油门（表现层熄火）：仅当报告已在缓存时生效，不触发扫描。
        /// 供绕过积分器直接改位姿的出口（自动驾驶到达/取消等）立即熄焰；
        /// 常规停火由下一次 Allocate 的全零解自然覆盖，无需调用。
        /// </summary>
        internal static void ClearThrottles(Map map)
        {
            if (PhysicsView(map)?.PhysicsReport is not { } cached)
            {
                return;
            }
            if (!cached.report.throttlesNeedClearing) return;
            for (int i = 0; i < cached.report.thrusters.Count; i++)
            {
                cached.report.thrusters[i].throttle = 0f;
            }
            cached.report.throttlesWrittenTick = -1;
            cached.report.throttlesNeedClearing = false;
        }

        /// <summary>读档 / 场景重置时清空（与 ShellRenderer 的缓存自愈纪律一致）。</summary>
        public static void ClearCache() =>
            Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.ClearPhysicsReports();

        public static void Forget(Map map) => PhysicsView(map)?.ForgetPhysics();

        private static ShipStructureIndex.MapView PhysicsView(Map map) =>
            Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.ExistingView(map);

    }
}
