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

        /// <summary>质量模型重扫间隔（tick）。建造期间允许最多 2s 的质量/推进器过期。</summary>
        public const int RescanIntervalTicks = 120;

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

            /// <summary>本 tick 分配到的油门 [0,1]；运行时派生状态，不参与物理积分或存档。</summary>
            public float throttle;
        }

        public sealed class MassReport
        {
            public float mass;
            public Vector2 comLocal;
            public float inertia;
            public readonly List<Thruster> thrusters = new List<Thruster>();
        }

        /// <summary>指令各分量 ∈ [-1, 1]；平移分量已经由主驾驶台系换算到 Map 局部 x/z。</summary>
        public struct Command
        {
            public float fx;
            public float fz;
            public float torque;
        }

        // ---- 质量模型 ----

        public static MassReport Measure(Map map)
        {
            var report = new MassReport();
            Vector2 origin = SpaceTransform.ShipLocalOrigin(map);

            float totalMass = 0f;
            var moment = Vector2.zero;
            var massPoints = new List<(Vector2 pos, float mass)>(256);

            TerrainGrid terrain = map.terrainGrid;
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                if (terrain.TerrainAt(cell) != DeltaVDefOf.Substructure)
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
                if (thing.def == DeltaVDefOf.LargeThruster)
                {
                    report.thrusters.Add(new Thruster
                    {
                        thing = thing,
                        localPos = r,
                        dir = ThrustDir(thing.Rotation)
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
            float inertia = 0f;
            for (int i = 0; i < massPoints.Count; i++)
            {
                inertia += massPoints[i].mass * (massPoints[i].pos - report.comLocal).sqrMagnitude;
            }
            report.inertia = Mathf.Max(inertia, 1f);
            return report;
        }

        /// <summary>建筑质量：def 带 Mass stat（如 LargeThruster=30）用 stat 实值，否则用 MassPerBuilding。</summary>
        private static float BuildingMass(Thing thing)
        {
            if (thing.def.statBases != null)
            {
                for (int i = 0; i < thing.def.statBases.Count; i++)
                {
                    if (thing.def.statBases[i].stat == StatDefOf.Mass)
                    {
                        return thing.GetStatValue(StatDefOf.Mass);
                    }
                }
            }
            return MassPerBuilding;
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
            force = Vector2.zero;
            torque = 0f;
            if (report == null || report.thrusters.Count == 0)
            {
                return;
            }

            float positiveX = 0f;
            float negativeX = 0f;
            float positiveZ = 0f;
            float negativeZ = 0f;
            float positiveTorque = 0f;
            float negativeTorque = 0f;
            for (int i = 0; i < report.thrusters.Count; i++)
            {
                Thruster thruster = report.thrusters[i];
                Vector2 fullForce = thruster.dir * ThrusterThrust;
                float fullTorque = Cross(thruster.localPos - report.comLocal, fullForce);
                positiveX += Mathf.Max(fullForce.x, 0f);
                negativeX += Mathf.Max(-fullForce.x, 0f);
                positiveZ += Mathf.Max(fullForce.y, 0f);
                negativeZ += Mathf.Max(-fullForce.y, 0f);
                positiveTorque += Mathf.Max(fullTorque, 0f);
                negativeTorque += Mathf.Max(-fullTorque, 0f);
                thruster.throttle = 0f;
            }

            float scaleX = DirectionalScale(command.fx, positiveX, negativeX);
            float scaleZ = DirectionalScale(command.fz, positiveZ, negativeZ);
            float scaleTorque = DirectionalScale(command.torque, positiveTorque, negativeTorque);
            float weightX = Mathf.Abs(command.fx) > 1e-6f
                ? CommandedTranslationWeight : UncommandedTranslationWeight;
            float weightZ = Mathf.Abs(command.fz) > 1e-6f
                ? CommandedTranslationWeight : UncommandedTranslationWeight;
            float weightTorque = Mathf.Abs(command.torque) > 1e-6f
                ? CommandedTorqueWeight : UncommandedTorqueWeight;

            // residual = A*u-b；u 从零开始，坐标下降直接在 Thruster.throttle 上迭代，无临时数组。
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
                    Vector2 fullForce = thruster.dir * ThrusterThrust;
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
                        + AllocationRegularization * thruster.throttle;
                    float next = Mathf.Clamp01(thruster.throttle - gradient / denominator);
                    float delta = next - thruster.throttle;
                    if (Mathf.Abs(delta) <= 1e-7f)
                    {
                        continue;
                    }
                    thruster.throttle = next;
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
                if (thruster.throttle < 0.0001f)
                {
                    thruster.throttle = 0f;
                    continue;
                }
                Vector2 appliedForce = thruster.dir * (ThrusterThrust * thruster.throttle);
                force += appliedForce;
                torque += Cross(thruster.localPos - report.comLocal, appliedForce);
            }
        }

        private static float DirectionalScale(float command, float positiveAuthority, float negativeAuthority)
        {
            float scale = command > 1e-6f
                ? positiveAuthority
                : (command < -1e-6f ? negativeAuthority : Mathf.Max(positiveAuthority, negativeAuthority));
            return Mathf.Max(scale, 1f);
        }

        /// <summary>
        /// 半隐式欧拉积分一个 tick。assist* 为真的轴做两层收口：
        /// ① "反推不越过零"（推进器反推的过冲保护）；
        /// ② 残差直接衰减——低于 ResidualSpeed/ResidualOmega 的剩余漂移按固定减速度
        ///    MoveTowards 到零（用户决策：不要求推进器布局完美消除微小漂移）。
        /// 速度/力都在船位置处的切空间基下表达；位置沿球面推进（OrbitSphere.Move），
        /// 质心不变量换成球面三步走：锚→质心（旧角）→平移→回锚（新角），
        /// 切空间速度随后平行输运到新锚点的基。
        /// </summary>
        public static void Integrate(ShipState ship, MassReport report, Vector2 force, float torque, float dt,
            bool assistX, bool assistZ, bool assistW)
        {
            SpacePose pose = ship.pose;
            Vector3 oldDir = pose.position;
            // 分配器与推进器几何都在稳定 Map 局部系；只有积分入切空间速度时旋转一次。
            Vector2 accel = SpaceTransform.Rotate(force, pose.angle) / report.mass;
            float alpha = torque / report.inertia;

            Vector2 velocity = pose.velocity + accel * dt;
            // 辅助轴与 W/S、A/D 一样定义在主驾驶台参考系，不能按切空间 x/y 直接截断。
            float pilotAngle = pose.angle + ShipOrientation.LocalForwardAngle(ship);
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
            float angularVelocity = pose.angularVelocity + alpha * dt;
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
            ship.pose = pose;
        }

        /// <summary>2D 叉积（z 分量）：逆时针为正，与 SpaceTransform 角度约定一致。</summary>
        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        // ---- 报告缓存 ----

        private sealed class CachedReport
        {
            public MassReport report;
            public int measuredAtTick;
        }

        private static readonly Dictionary<Map, CachedReport> Cache = new Dictionary<Map, CachedReport>();

        public static MassReport GetReport(ShipState ship)
        {
            Map map = ship.map;
            if (map == null)
            {
                return null;
            }
            if (!Cache.TryGetValue(map, out CachedReport cached))
            {
                cached = new CachedReport();
                Cache[map] = cached;
            }
            int now = Find.TickManager.TicksGame;
            if (cached.report == null || now - cached.measuredAtTick >= RescanIntervalTicks)
            {
                MassReport fresh = Measure(map);
                if (cached.report != null && MassModelChanged(cached.report, fresh))
                {
                    DeltaVLog.Message(DeltaVLogModule.Physics,
                        $"mass model changed on '{map.Parent?.Label ?? map.ToString()}': mass {cached.report.mass:F0}->{fresh.mass:F0}, " +
                        $"thrusters {cached.report.thrusters.Count}->{fresh.thrusters.Count}, com {cached.report.comLocal:F2}->{fresh.comLocal:F2}, " +
                        $"inertia {cached.report.inertia:F0}->{fresh.inertia:F0}");
                }
                cached.report = fresh;
                cached.measuredAtTick = now;
            }
            return cached.report;
        }

        private static bool MassModelChanged(MassReport oldReport, MassReport newReport)
        {
            return Mathf.Abs(oldReport.mass - newReport.mass) > 0.01f
                || (oldReport.comLocal - newReport.comLocal).sqrMagnitude > 0.0001f
                || Mathf.Abs(oldReport.inertia - newReport.inertia) > 1f
                || oldReport.thrusters.Count != newReport.thrusters.Count;
        }

        /// <summary>读档 / 场景重置时清空（与 ShellRenderer 的缓存自愈纪律一致）。</summary>
        public static void ClearCache()
        {
            Cache.Clear();
        }
    }
}
