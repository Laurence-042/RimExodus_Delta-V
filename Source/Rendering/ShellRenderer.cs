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
        // 所有非聚焦对象壳统一走背景层；前台层只承载弹体和瞄准效果。
        // 两层各自持有相机事件与收尾语义；绘制目的地经 (层, 槽位) 显式传入。
        private static readonly ShellBackgroundLayer backgroundLayer = new ShellBackgroundLayer();
        private static readonly ShellForegroundLayer foregroundLayer = new ShellForegroundLayer();
        private static readonly ShellLayer[] allLayers = { backgroundLayer, foregroundLayer };

        private static readonly List<SpaceObject> LayerOrderScratch = new List<SpaceObject>();

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
            // 背景层：所有非聚焦壳 → 聚焦图随后无条件覆盖。
            OrderShellsById(universe, frame, LayerOrderScratch);
            long gridStart = universe.Performance.Start();
            int motionSlots = SpaceCoordinateGrid.Emit(universe, frame, current, backgroundLayer, 0);
            universe.Performance.Record(PerformanceStage.Grid, gridStart);
            motionSlots = universe.Projectiles.EmitMotionVisuals(frame, current, backgroundLayer, motionSlots);
            long shellStart = universe.Performance.Start();
            bool anyBuffered = motionSlots > 0;
            for (int i = 0; i < LayerOrderScratch.Count; i++)
            {
                bool drew = universe.UseNativeMapShellRendering
                    ? DrawNativeMapExtras(universe, LayerOrderScratch[i], frame, current,
                        backgroundLayer.At(i + motionSlots))
                    : DrawObjectShell(universe, LayerOrderScratch[i], frame, current,
                        backgroundLayer.At(i + motionSlots));
                anyBuffered |= drew;
                if (logBeat) AppendLayerObj("b" + i + "/" + LayerOrderScratch.Count, LayerOrderScratch[i], drew);
            }
            backgroundLayer.Finish(anyBuffered);
            universe.Performance.Record(PerformanceStage.Shells, shellStart);
            // 前台层承载冷却烟、弹体、激光和瞄准标记，不改变对象壳的层级。
            long foregroundStart = universe.Performance.Start();
            int nextSlot = 0;
            nextSlot = universe.Projectiles.EmitGunCoolingVisuals(frame, current, foregroundLayer, nextSlot);
            // 在飞弹体 + 激光线段（太空战架构 §4.4：弹体 def 图形投影，同管线）。
            nextSlot = universe.Projectiles.TryEmitProjectiles(frame, current, foregroundLayer, nextSlot);
            // 瞄准标记（§6.3）：解算与「画不画」都在瞄准会话 controller 内，无会话
            // 即零标记（ShellRenderer 只转发，不持瞄准状态）。
            nextSlot = universe.Controls.EmitAimMarkers(frame, current, foregroundLayer, nextSlot);
            foregroundLayer.Finish(nextSlot > 0);
            universe.Performance.Record(PerformanceStage.Foreground, foregroundStart);
            if (logBeat) DeltaVLog.Message(DeltaVLogModule.Render, LayerLogBuf.ToString());
        }

        private static int CompareById(SpaceObject a, SpaceObject b)
        {
            return a.objectId.CompareTo(b.objectId);
        }

        /// <summary>
        /// 船壳绘制与拾取共用的背景层排序（objectId 升序 = 自下而上；拾取倒序遍历）。
        /// 每容器一壳，任务状态不改变壳的层级。
        /// </summary>
        internal static void OrderShellsById(UniverseManager universe, SpaceObject frame, List<SpaceObject> results)
        {
            results.Clear();
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == frame || !obj.ParticipatesInOverlays) continue;
                results.Add(obj);
            }
            results.Sort(CompareById);
        }

        private static bool DrawObjectShell(UniverseManager universe, SpaceObject obj,
            SpaceObject frame, Map frameMap, ShellLayer.Slot target)
        {
            // 每容器一壳：整图内容（含停靠船与残骸归属的格）统一绘制，allowedCells
            // 恒为 null（不再按船裁剪）。
            if (obj.Payload != null)
            {
                if (!obj.DrawPayload(frame, frameMap, foreground: false)) return false;
                target.layer.Emit(target);
                return true;
            }
            if (obj is MapSpaceObject { PersonnelPawn: Pawn pawn } && pawn.Spawned)
            {
                Vector3 point = SpaceTransform.FocusedDrawPosition(frame, frameMap, obj.pose.position, 0f);
                if (!Find.CameraDriver.CurrentViewRect.ExpandedBy(2).Contains(point.ToIntVec3())) return false;
                Quaternion bodyRotation = SpaceTransform.FocusedMeshRotation(frame, obj.pose.angle);
                MapOverlayRenderer.BeginQuadBatch();
                ShellDrawCapture.Capture(() =>
                {
                    pawn.DynamicDrawPhaseAt(DrawPhase.EnsureInitialized, Vector3.zero);
                    pawn.DynamicDrawPhaseAt(DrawPhase.Draw, Vector3.zero);
                }, Matrix4x4.TRS(point, bodyRotation, Vector3.one), ShellSurfaceLayer.Exposed, 0f);
                if (TryGetMapDisplayTransform(frame, frameMap, obj, out Matrix4x4 personnelMatrix,
                    out Quaternion personnelRotation))
                    ThrusterEffects.CollectShellFlames(obj, obj.map, personnelMatrix, personnelRotation);
                target.layer.Emit(target, mapShell: true);
                return true;
            }
            if (!HasDrawableMap(universe, obj)) return false;
            Map map = obj.map;
            if (!TryGetMapDisplayTransform(frame, frameMap, obj, out Matrix4x4 matrix,
                out Quaternion rotation)) return false;
            CellRect localView = TransformRectInverse(matrix, Find.CameraDriver.CurrentViewRect).ExpandedBy(4);
            Vector2 localSun = ShellHullVisuals.LocalSun(obj.pose);
            bool drew = MapOverlayRenderer.CollectShellCommands(map, matrix, rotation, localView, null,
                localSun);
            if (drew)
            {
                // 火焰与壳内容同表同层：renderQueue 3201 排在本壳地形之上，
                // 随本壳走背景。油门只由分配器
                // 写（无人出力的残骸/POI 恒为零，天然无火焰）。
                ThrusterEffects.CollectShellFlames(obj, map, matrix, rotation);
                target.layer.Emit(target, mapShell: true);
            }
            return drew;
        }

        /// <summary>
        /// Native REF rendering replaces only map contents. Payload proxies and Delta-V's
        /// universe-space thruster flames remain in the established shell layer.
        /// </summary>
        private static bool DrawNativeMapExtras(UniverseManager universe, SpaceObject obj,
            SpaceObject frame, Map frameMap, ShellLayer.Slot target)
        {
            if (obj.Payload != null) return DrawObjectShell(universe, obj, frame, frameMap, target);
            if (obj.HasTag(SpaceObjectTag.Personnel))
                return DrawObjectShell(universe, obj, frame, frameMap, target);
            if (!HasDrawableMap(universe, obj)
                || !TryGetMapDisplayTransform(frame, frameMap, obj, out Matrix4x4 matrix,
                    out Quaternion rotation)) return false;
            MapOverlayRenderer.BeginQuadBatch();
            if (!ThrusterEffects.CollectShellFlames(obj, obj.map, matrix, rotation)) return false;
            target.layer.Emit(target, mapShell: true);
            return true;
        }

        /// <summary>Single display transform shared by custom shells and REF native map views.</summary>
        internal static bool TryGetMapDisplayTransform(SpaceObject frame, Map frameMap, SpaceObject obj,
            out Matrix4x4 matrix, out Quaternion rotation)
        {
            matrix = default;
            rotation = default;
            Map map = obj?.map;
            if (frame == null || frameMap == null || map == null || map.Disposed) return false;
            rotation = SpaceTransform.FocusedMeshRotation(frame, obj.pose.angle);
            Vector3 drawPos = SpaceTransform.FocusedDrawPosition(frame, frameMap, obj.pose.position, 0f);
            Vector2 sourceOrigin = SpaceTransform.ShipLocalOrigin(map);
            Vector3 translation = drawPos - rotation * new Vector3(sourceOrigin.x, 0f, sourceOrigin.y);
            matrix = Matrix4x4.TRS(translation, rotation, Vector3.one);
            return true;
        }

        internal static bool HasDrawableMap(UniverseManager universe, SpaceObject obj)
            => obj?.map is Map map && !map.Disposed && universe.Generation?.IsGenerating(map) != true;

        /// <summary>分层摘要追加一壳：背景槽位、ID、标签、状态、
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
            if (map == null) return;
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
            return new RimExodus.Maps.MapTransform(matrix).LocalView(rect);
        }
    }
}
