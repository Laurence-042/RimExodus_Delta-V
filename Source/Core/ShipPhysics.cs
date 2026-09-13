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
    /// - 分配器是启发式油门（对称铺设下纯指令无副作用），但合力/合力矩一律按真实
    ///   推进器位置与朝向求和——推力不过质心自然产生力矩，满足"考虑质心与推进器位置"；
    /// - 飞行辅助（含按住刹车键临时启用）在 PilotController 合成反向指令，走同一分配器；
    ///   最后的微小残差由 Residual* 常量直接衰减到零（用户决策 2026-09-13：不要求推进器
    ///   布局能完美消除微小漂移——刻意偏离纯牛顿的收尾便利）。
    ///
    /// 积分以质心为动力学中心（半隐式欧拉，dt = 1/60 每 tick）；
    /// SpacePose.position 的锚仍是地图几何中心（阶段1 约定），按不变量
    /// "position + Rotate(comLocal, angle) = 质心宇宙位置" 每步派生——
    /// SpaceTransform 与存档语义完全不变，无迁移。
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

        /// <summary>指令各分量 ∈ [-1, 1]；正方向 = 船艏 +X / 左舷 +Z / 逆时针（与 SpaceTransform 角度约定一致）。</summary>
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
        /// 启发式油门：线性指令点燃朝向同向的推进器；未被线性使用的推进器若全力力矩
        /// 与转向指令同号则参与转向。返回值是按真实位置/朝向求出的合力与合力矩，
        /// 非对称铺设的副作用（推力诱导力矩、转向诱导漂移）会如实保留。
        /// </summary>
        public static void Allocate(MassReport report, Command command, out Vector2 force, out float torque)
        {
            force = Vector2.zero;
            torque = 0f;
            for (int i = 0; i < report.thrusters.Count; i++)
            {
                Thruster thruster = report.thrusters[i];
                float throttle = 0f;
                if (command.fx != 0f && thruster.dir.x * command.fx > 0f)
                {
                    throttle = Mathf.Max(throttle, Mathf.Abs(command.fx));
                }
                if (command.fz != 0f && thruster.dir.y * command.fz > 0f)
                {
                    throttle = Mathf.Max(throttle, Mathf.Abs(command.fz));
                }
                if (throttle <= 0f && command.torque != 0f)
                {
                    Vector2 lever = thruster.localPos - report.comLocal;
                    if (Cross(lever, thruster.dir) * command.torque > 0f)
                    {
                        throttle = Mathf.Abs(command.torque);
                    }
                }
                if (throttle <= 0f)
                {
                    thruster.throttle = 0f;
                    continue;
                }
                thruster.throttle = throttle;
                Vector2 f = thruster.dir * (ThrusterThrust * throttle);
                force += f;
                torque += Cross(thruster.localPos - report.comLocal, f);
            }
        }

        /// <summary>
        /// 半隐式欧拉积分一个 tick。assist* 为真的轴做两层收口：
        /// ① "反推不越过零"（推进器反推的过冲保护）；
        /// ② 残差直接衰减——低于 ResidualSpeed/ResidualOmega 的剩余漂移按固定减速度
        ///    MoveTowards 到零（用户决策：不要求推进器布局完美消除微小漂移）。
        /// 位置经质心不变量派生，见类注释。
        /// </summary>
        public static void Integrate(ShipState ship, MassReport report, Vector2 force, float torque, float dt,
            bool assistX, bool assistZ, bool assistW)
        {
            SpacePose pose = ship.pose;
            Vector2 accel = force / report.mass;
            float alpha = torque / report.inertia;

            Vector2 velocity = pose.velocity + accel * dt;
            if (assistX && pose.velocity.x != 0f && velocity.x * pose.velocity.x < 0f)
            {
                velocity.x = 0f;
            }
            if (assistZ && pose.velocity.y != 0f && velocity.y * pose.velocity.y < 0f)
            {
                velocity.y = 0f;
            }
            float angularVelocity = pose.angularVelocity + alpha * dt;
            if (assistW && pose.angularVelocity != 0f && angularVelocity * pose.angularVelocity < 0f)
            {
                angularVelocity = 0f;
            }

            // 残差直接衰减：微小剩余漂移不依赖推进器几何，按固定减速度收尾到零。
            if (assistX && Mathf.Abs(velocity.x) < ResidualSpeed)
            {
                velocity.x = Mathf.MoveTowards(velocity.x, 0f, ResidualDecel * dt);
            }
            if (assistZ && Mathf.Abs(velocity.y) < ResidualSpeed)
            {
                velocity.y = Mathf.MoveTowards(velocity.y, 0f, ResidualDecel * dt);
            }
            if (assistW && Mathf.Abs(angularVelocity) < ResidualOmega)
            {
                angularVelocity = Mathf.MoveTowards(angularVelocity, 0f, ResidualAlphaDecel * dt);
            }

            // 质心宇宙位置 = position + Rotate(comLocal, angle)；先按旧角度还原，平移后按新角度写回。
            Vector2 comUniverse = pose.position + SpaceTransform.Rotate(report.comLocal, pose.angle);
            comUniverse += velocity * dt;
            pose.angle = SpaceTransform.WrapAngle(pose.angle + angularVelocity * dt);
            pose.position = comUniverse - SpaceTransform.Rotate(report.comLocal, pose.angle);
            pose.velocity = velocity;
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
