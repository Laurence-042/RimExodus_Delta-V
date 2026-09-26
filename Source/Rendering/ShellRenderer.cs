using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>宇宙对象变换适配层；实际 Map 内容绘制由通用 MapOverlayRenderer 完成。</summary>
    public static class ShellRenderer
    {
        // 挂载层（ShellLayer）：背景 = 非进场壳（聚焦图随后无条件覆盖）；前台 =
        // 进场/选点悬停壳（聚焦图画完之后再画，临时覆盖目标图）。同一收集/发射管线，
        // 层对象各自持有相机事件与收尾语义；绘制目的地经 (层, 槽位) 显式传入。
        private static readonly ShellBackgroundLayer backgroundLayer = new ShellBackgroundLayer();
        private static readonly ShellForegroundLayer foregroundLayer = new ShellForegroundLayer();
        private static readonly ShellLayer[] allLayers = { backgroundLayer, foregroundLayer };

        private static readonly List<SpaceObject> LayerOrderScratch = new List<SpaceObject>();
        private static readonly List<SpaceObject> ForegroundScratch = new List<SpaceObject>();

        // Render 模块诊断：每秒最多 1 条整壳分层摘要（用户 2026-09-20 约束）。
        // 热路径纪律——先查 DeltaVLog.Enabled 再碰 StringBuilder；节流用
        // unscaledTime（暂停时也走表）；q= 由 MapOverlayRenderer.QuadsDrawnLastPass
        // 提供的本壳最近一次提交四边形数。
        private static float nextLayerLogTime;
        private static readonly System.Text.StringBuilder LayerLogBuf = new System.Text.StringBuilder(256);

        public static void DrawNonFocusedShells(UniverseManager universe)
        {
            Map current = Find.CurrentMap;
            SpaceObject frame = current == null ? null : universe.FrameOf(current);
            if (current == null || frame == null || !WorldRendererUtility.DrawingMap)
            {
                foreach (ShellLayer layer in allLayers) layer.BeginFrame();
                return;
            }
            foreach (ShellLayer layer in allLayers) layer.EnsureAttached(Find.Camera);
            foreach (ShellLayer layer in allLayers) layer.BeginFrame();
            bool logBeat = DeltaVLog.Enabled(DeltaVLogModule.Render) && Time.unscaledTime >= nextLayerLogTime;
            if (logBeat)
            {
                nextLayerLogTime = Time.unscaledTime + 1f;
                LayerLogBuf.Length = 0;
                LayerLogBuf.Append("frame=map#").Append(current.uniqueID).Append("/obj").Append(frame.objectId);
            }
            // 整壳分层（2026-09-20 用户决策）：全部船壳同一条命令缓冲管线，按 objectId
            // 升序自下而上整组绘制——命令顺序即层序，不依赖 3D 深度。负海拔高度带
            // 方案已实测证伪（正交俯视相机下 Graphics.DrawMesh 透明队列按 bounds 距离
            // 排序、x/z 分量压过海拔差），勿回退。机制 = RimExodus
            // SeamlessTileRenderer 同款。
            // 背景层：非进场壳 → 聚焦图随后无条件覆盖（玩家关注聚焦图内部）。
            OrderShellsById(universe, frame, LayerOrderScratch);
            bool anyBuffered = false;
            for (int i = 0; i < LayerOrderScratch.Count; i++)
            {
                bool drew = DrawObjectShell(universe, LayerOrderScratch[i], frame, current, backgroundLayer.At(i));
                anyBuffered |= drew;
                if (logBeat) AppendLayerObj("b" + i + "/" + LayerOrderScratch.Count, LayerOrderScratch[i], drew);
            }
            backgroundLayer.Finish(anyBuffered);
            // 前台层：对接进场/选点悬停壳——聚焦目标图时临时前置。
            // 目标图）临时被悬停壳覆盖（2026-09-20 用户决策）；悬停壳 + 火焰盖在
            // 目标甲板之上。
            ForegroundScratch.Clear();
            PilotTarget flowTarget = universe.Approach.FlowTarget;
            Ship pendingShip = universe.ShipTransitions.PendingDockingShip(universe);
            Building pendingShuttle = universe.ShipTransitions.pendingShuttleBody;
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == frame || !obj.ParticipatesInOverlays) continue;
                // 进场/选点船的容器壳走前台（聚焦目标图时临时被悬停壳覆盖）。
                if ((flowTarget.IsValid && universe.FrameOf(flowTarget.Map) == obj)
                    || (pendingShip.IsValid && universe.FrameOf(pendingShip.map) == obj)
                    || (pendingShuttle != null && universe.FrameOf(pendingShuttle.Map) == obj))
                {
                    ForegroundScratch.Add(obj);
                }
            }
            ForegroundScratch.Sort(CompareById);
            bool anyForeground = false;
            for (int i = 0; i < ForegroundScratch.Count; i++)
            {
                bool drew = DrawObjectShell(universe, ForegroundScratch[i], frame, current, foregroundLayer.At(i));
                anyForeground |= drew;
                if (logBeat) AppendLayerObj("f" + i + "/" + ForegroundScratch.Count, ForegroundScratch[i], drew);
            }
            // 对接幽灵船（预定落点提示）：计划期常显，随悬停壳之后走前台层——聚焦
            // 自己船时提示在空间中可见，聚焦目标图时覆盖在落点上（不被聚焦图吞掉）。
            int nextSlot = ForegroundScratch.Count;
            if (DockingGhostRenderer.TryEmit(universe, frame, current, foregroundLayer.At(nextSlot)))
            {
                anyForeground = true;
                nextSlot++;
                if (logBeat) LayerLogBuf.Append(" | ghost");
            }
            // 在飞弹体 + 激光线段（太空战架构 §4.4：弹体 def 图形投影，同管线）。
            nextSlot = universe.Projectiles.TryEmitProjectiles(frame, current, foregroundLayer, nextSlot);
            // 预瞄指示器 + 瞄准位置标记（§6.3，纯显示派生；驾驶中才出现）。
            Ship pilotedForMarkers = universe.Pilot.PilotedShip(universe);
            nextSlot = AimMarkersRenderer.TryEmitMarkers(universe, frame, current, pilotedForMarkers,
                foregroundLayer, nextSlot);
            if (nextSlot > ForegroundScratch.Count) anyForeground = true;
            foregroundLayer.Finish(anyForeground);
            if (logBeat) DeltaVLog.Message(DeltaVLogModule.Render, LayerLogBuf.ToString());
        }

        private static int CompareById(SpaceObject a, SpaceObject b)
        {
            return a.objectId.CompareTo(b.objectId);
        }

        /// <summary>
        /// 船壳绘制与拾取共用的整壳排序（objectId 升序 = 自下而上；拾取倒序遍历，
        /// 视觉最上层优先命中）。**每容器一壳**（解耦重构：容器图 = 整壳，同图多船
        /// 不分壳）；进场/选点船的容器不参与背景组：绘制侧走前台缓冲（聚焦图临时被
        /// 悬停壳覆盖），拾取侧由原版 marker 处理。
        /// </summary>
        internal static void OrderShellsById(UniverseManager universe, SpaceObject frame, List<SpaceObject> results)
        {
            results.Clear();
            PilotTarget flowTarget = universe.Approach.FlowTarget;
            Ship pendingShip = universe.ShipTransitions.PendingDockingShip(universe);
            Building pendingShuttle = universe.ShipTransitions.pendingShuttleBody;
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == frame || !obj.ParticipatesInOverlays) continue;
                if ((flowTarget.IsValid && universe.FrameOf(flowTarget.Map) == obj)
                    || (pendingShip.IsValid && universe.FrameOf(pendingShip.map) == obj)
                    || (pendingShuttle != null && universe.FrameOf(pendingShuttle.Map) == obj)) continue;
                results.Add(obj);
            }
            results.Sort(CompareById);
        }

        private static bool DrawObjectShell(UniverseManager universe, SpaceObject obj,
            SpaceObject frame, Map frameMap, ShellLayer.Slot target)
        {
            // 每容器一壳：整图内容（含停靠船与残骸归属的格）统一绘制，allowedCells
            // 恒为 null（不再按船裁剪）；选点/进场期间计划船留在其容器图上，
            // 其壳由前台缓冲承载。
            Map map = obj.map;
            if (map == null || map.Disposed || universe.Generation?.IsGenerating(map) == true) return false;

            Quaternion rotation = SpaceTransform.FocusedMeshRotation(frame, obj.pose.angle);
            Vector3 drawPos = SpaceTransform.FocusedDrawPosition(frame, frameMap, obj.pose.position, 0f);
            Vector2 sourceOrigin = SpaceTransform.ShipLocalOrigin(map);

            Vector3 translation = drawPos - rotation * new Vector3(sourceOrigin.x, 0f, sourceOrigin.y);
            Matrix4x4 matrix = Matrix4x4.TRS(translation, rotation, Vector3.one);
            CellRect localView = TransformRectInverse(matrix, Find.CameraDriver.CurrentViewRect).ExpandedBy(4);
            bool drew = MapOverlayRenderer.CollectShellCommands(map, matrix, rotation, localView, null);
            if (drew)
            {
                // 火焰与壳内容同表同层：renderQueue 3201 排在本壳地形之上，随本壳
                // 走背景（聚焦图之下）或前台（进场时盖住目标甲板）。油门只由分配器
                // 写（无人出力的残骸/POI 恒为零，天然无火焰）。
                ThrusterEffects.CollectShellFlames(obj, map, matrix, rotation);
                target.layer.Emit(target);
            }
            return drew;
        }

        /// <summary>分层摘要追加一壳：槽位（b=背景 / f=前台进场）、ID、标签、状态、
        /// 源图与提交四边形数；drew=false 时只记 SKIP（原因 = 图空/销毁/生成中，
        /// 可由状态字段推断）。</summary>
        private static void AppendLayerObj(string slot, SpaceObject obj, bool drew)
        {
            LayerLogBuf.Append(" | ").Append(slot)
                .Append(" obj").Append(obj.objectId);
            if (obj.label != null) LayerLogBuf.Append('(').Append(obj.label).Append(')');
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            PilotTarget flowTarget = universe?.Approach.FlowTarget ?? PilotTarget.Invalid;
            if (flowTarget.IsValid && universe.FrameOf(flowTarget.Map) == obj)
                LayerLogBuf.Append(" docking");
            if (!drew)
            {
                LayerLogBuf.Append(" SKIP");
                return;
            }
            Map map = obj.map;
            LayerLogBuf.Append(" map#").Append(map.uniqueID)
                .Append('(').Append(map.Size.x).Append('x').Append(map.Size.z).Append(')')
                .Append(" q=").Append(MapOverlayRenderer.QuadsDrawnLastPass);
        }

        public static void ClearRuntimeCaches()
        {
            foreach (ShellLayer layer in allLayers) layer.Detach();
            MapOverlayRenderer.ClearRuntimeCaches();
        }

        /// <summary>显示帧矩阵的逆变换：把相机视野矩形换回源 Map 局部坐标（供裁剪）。</summary>
        internal static CellRect TransformRectInverse(Matrix4x4 matrix, CellRect rect)
        {
            Matrix4x4 inverse = matrix.inverse;
            Vector3 a = inverse.MultiplyPoint3x4(new Vector3(rect.minX, 0f, rect.minZ));
            Vector3 b = inverse.MultiplyPoint3x4(new Vector3(rect.maxX, 0f, rect.minZ));
            Vector3 c = inverse.MultiplyPoint3x4(new Vector3(rect.maxX, 0f, rect.maxZ));
            Vector3 d = inverse.MultiplyPoint3x4(new Vector3(rect.minX, 0f, rect.maxZ));
            return CellRect.FromLimits(
                Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x))),
                Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z))),
                Mathf.CeilToInt(Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x))),
                Mathf.CeilToInt(Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z))));
        }
    }
}
