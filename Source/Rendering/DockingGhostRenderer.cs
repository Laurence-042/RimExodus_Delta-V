using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 对接幽灵船（2026-09-21 阶段4 定案）：预定降落点上的半透明自己船位置提示。
    /// 本质只是提示——不需要也不应有可降落、推进器火焰、拾取/聚焦等能力：
    /// - 派生渲染缓存（AGENTS §15/§12）：快照在计划建立时从船壳轮廓（引擎相连
    ///   基架连通域，与世界剪影同口径）生成一次，不追踪战损/建造变化；
    /// - **不入 <see cref="UniverseManager.objects"/> 权威列表**——不参与降落判定、
    ///   拾取、生命周期、星区记账与剪影扫描；
    /// - 位置提示 = 宿主位姿 ⊕ 计划局部偏移，随宿主运动每帧重算（随动目标的
    ///   显示投影，不是宇宙状态）；
    /// - 经 ShellRenderer 前台层（聚焦图之上）绘制：聚焦自己船时提示在空间中可见，
    ///   聚焦目标图时提示覆盖在落点上——两种视角都不被聚焦图内容吞掉。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class DockingGhostRenderer
    {
        private static Texture2D texture;
        private static Material material;
        private static float sideCells;

        private static readonly Color GhostTint = new Color(0.6f, 0.85f, 1f, 0.45f);

        /// <summary>对齐提示色（进入容差窗口即变绿：已对齐，保持设定 tick 后完成对接）。</summary>
        private static readonly Color AlignedTint = new Color(0.35f, 1f, 0.5f, 0.6f);

        public static bool Ready => material != null;

        /// <summary>计划建立时烘焙快照：格子集合 → 剪影纹理（ShipSilhouette 唯一入口）。
        /// 位姿始终直接读取 ActivePlan，本缓存只保存由船体派生的纹理。</summary>
        public static void BuildFor(LandingApproachController.DockingPlan plan)
        {
            Destroy();
            if (plan?.ship?.map == null || plan.host == null) return;
            Building_GravEngine engine = ShipStructureResolver.EngineOf(plan.ship);
            if (engine == null) return;
            // 剪影锚 = 位姿锚（口袋图几何中心）：剪影内容与未来真实脚印逐格重合——
            // 玩家看到的剪影重叠就是对齐判据本身（2026-09-21 修复：旧实现按脚印
            // 包围盒居中，两个中心不重合时手操视觉对准但落位偏移）。
            texture = ShipSilhouette.BuildTexture(engine.AllConnectedSubstructure,
                "DeltaV_DockingGhost", SpaceTransform.ShipLocalOrigin(plan.ship.map));
            if (texture == null) return;
            sideCells = texture.width; // 正方形（内容居中，旋转绕中心即绕船体中心）
            material = new Material(ShaderDatabase.Transparent)
            {
                name = "DeltaV_DockingGhost",
                mainTexture = texture,
                color = GhostTint,
                renderQueue = 3000
            };
        }

        /// <summary>
        /// 前台层发射（ShellRenderer.DrawNonFocusedShells 在悬停壳之后调用）：单四边形
        /// 命令，中心 = 提示位姿锚（宿主 ⊕ 偏移）在聚焦帧的投影，边长 = 快照方形边长。
        /// </summary>
        internal static bool TryEmit(UniverseManager universe, SpaceObject frame, Map frameMap, ShellLayer.Slot slot)
        {
            LandingApproachController approach = universe?.Approach;
            LandingApproachController.DockingPlan plan = approach?.ActivePlan;
            if (material == null || plan?.host == null) return false;
            // 对齐反馈（2026-09-21 用户要求）：进入容差窗口（保持计数 > 0）停靠点变绿，
            // 提示玩家已对齐、保持设定 tick 后即完成对接；失准自动回蓝。
            material.color = approach.AlignedNow ? AlignedTint : GhostTint;
            plan.TargetPose(out Vector3 ghostDir, out float ghostAngle);
            Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, frameMap, ghostDir,
                AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc * 10f);
            Quaternion rot = SpaceTransform.FocusedMeshRotation(frame, ghostAngle);
            Matrix4x4 matrix = Matrix4x4.TRS(pos, rot, new Vector3(sideCells, 1f, sideCells));
            MapOverlayRenderer.CollectSingleQuad(MeshPool.plane10, matrix, material);
            slot.layer.Emit(slot);
            return true;
        }

        /// <summary>计划取消/完成/读档防御时释放；幂等。</summary>
        public static void Destroy()
        {
            sideCells = 0f;
            if (material != null)
            {
                Object.Destroy(material);
                material = null;
            }
            if (texture != null)
            {
                Object.Destroy(texture);
                texture = null;
            }
        }
    }
}
