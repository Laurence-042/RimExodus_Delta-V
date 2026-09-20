using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace DeltaV
{
    /// <summary>宇宙对象变换适配层；实际 Map 内容绘制由通用 MapOverlayRenderer 完成。</summary>
    public static class ShellRenderer
    {
        private const string ShellBufferName = "DeltaV Shell Layering";
        private const string ShellForegroundBufferName = "DeltaV Shell Layering (approach)";

        private static readonly List<SpaceObject> LayerOrderScratch = new List<SpaceObject>();
        private static readonly List<SpaceObject> ForegroundScratch = new List<SpaceObject>();

        // 双命令缓冲：背景 = 非进场壳（BeforeForwardOpaque，先于聚焦图渲染，画完清
        // 深度让聚焦图无条件覆盖）；前台 = 进场/选点悬停壳（AfterForwardAlpha，聚焦图
        // 画完之后，组前清深度临时覆盖聚焦图）。同一收集/发射管线，仅挂载层不同。
        private static CommandBuffer shellBuffer;
        private static CommandBuffer shellForegroundBuffer;
        private static Camera bufferCamera;

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
                // 不画的帧必须清空缓冲命令，否则旧命令在相机上逐帧重放
                // （RimExodus SeamlessTileRenderer 同款教训）。
                if (shellBuffer != null) shellBuffer.Clear();
                if (shellForegroundBuffer != null) shellForegroundBuffer.Clear();
                return;
            }
            EnsureShellBuffers();
            shellBuffer.Clear();
            shellForegroundBuffer.Clear();
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
                bool drew = DrawObjectShell(LayerOrderScratch[i], frame, current, shellBuffer);
                anyBuffered |= drew;
                if (logBeat) AppendLayerObj("b" + i + "/" + LayerOrderScratch.Count, LayerOrderScratch[i], drew);
            }
            if (anyBuffered)
            {
                // 只清深度、保留颜色：聚焦图自身内容随后无条件覆盖全部背景壳。
                shellBuffer.ClearRenderTarget(true, false, Color.clear, 1f);
            }
            // 前台层：进场/选点悬停壳（ship.docking）——切固定模式期间聚焦图（降落
            // 目标图）临时被悬停壳覆盖（2026-09-20 用户决策）；悬停壳 + 火焰盖在
            // 目标甲板之上。
            ForegroundScratch.Clear();
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == frame) continue;
                ShipState ship = obj as ShipState;
                if (ship != null && ship.docking) ForegroundScratch.Add(obj);
            }
            ForegroundScratch.Sort(CompareById);
            bool anyForeground = false;
            for (int i = 0; i < ForegroundScratch.Count; i++)
            {
                bool drew = DrawObjectShell(ForegroundScratch[i], frame, current, shellForegroundBuffer);
                anyForeground |= drew;
                if (logBeat) AppendLayerObj("f" + i + "/" + ForegroundScratch.Count, ForegroundScratch[i], drew);
            }
            if (anyForeground)
            {
                // 防御性收尾：不向前台层之后的其他缓冲遗留本组深度。
                shellForegroundBuffer.ClearRenderTarget(true, false, Color.clear, 1f);
            }
            if (logBeat) DeltaVLog.Message(DeltaVLogModule.Render, LayerLogBuf.ToString());
        }

        private static int CompareById(SpaceObject a, SpaceObject b)
        {
            return a.objectId.CompareTo(b.objectId);
        }

        /// <summary>
        /// 船壳绘制与拾取共用的整壳排序（objectId 升序 = 自下而上；拾取倒序遍历，
        /// 视觉最上层优先命中）。进场/选点船不参与背景组：绘制侧走前台缓冲
        /// （聚焦图临时被悬停壳覆盖），拾取侧由原版 marker 处理。
        /// </summary>
        internal static void OrderShellsById(UniverseManager universe, SpaceObject frame, List<SpaceObject> results)
        {
            results.Clear();
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == frame) continue;
                ShipState ship = obj as ShipState;
                if (ship != null && ship.docking) continue;
                results.Add(obj);
            }
            results.Sort(CompareById);
        }

        /// <summary>双缓冲挂当前地图相机（RimExodus SeamlessTileRenderer.EnsureCommandBuffer
        /// 同款）：背景 BeforeForwardOpaque（先于聚焦图）、前台 AfterForwardAlpha（聚焦图
        /// 画完之后）。RimWorld 全程复用同一相机对象；相机身份变化时重挂，防跨相机残留。</summary>
        private static void EnsureShellBuffers()
        {
            Camera camera = Find.Camera;
            if (shellBuffer != null && bufferCamera == camera) return;
            DetachShellBuffers();
            shellBuffer = new CommandBuffer { name = ShellBufferName };
            shellForegroundBuffer = new CommandBuffer { name = ShellForegroundBufferName };
            bufferCamera = camera;
            bufferCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, shellBuffer);
            bufferCamera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, shellForegroundBuffer);
        }

        private static void DetachShellBuffers()
        {
            if (bufferCamera != null)
            {
                if (shellBuffer != null)
                    bufferCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, shellBuffer);
                if (shellForegroundBuffer != null)
                    bufferCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, shellForegroundBuffer);
            }
            shellBuffer?.Release();
            shellForegroundBuffer?.Release();
            shellBuffer = null;
            shellForegroundBuffer = null;
            bufferCamera = null;
        }

        private static bool DrawObjectShell(SpaceObject obj, SpaceObject frame, Map frameMap, CommandBuffer buffer)
        {
            // 选点/进场（docking）期间船仍在活口袋图上（快照不搬运），非停靠分支
            // 直接可画——聚焦目标图上持续显示悬停船壳；到达停靠后走停靠分支，
            // 由 dockedMap == frameMap 跳过防重复绘制。
            ShipState ship = obj as ShipState;
            bool docked = ship != null && ship.docked;
            Map map = docked ? ship.dockedMap : obj.map;
            if (map == null || map.Disposed || IncrementalMapGenerationService.IsGenerating(map)
                || docked && map == frameMap) return false;

            HashSet<IntVec3> allowedCells = null;
            Quaternion rotation;
            Vector3 drawPos;
            Vector2 sourceOrigin;
            if (docked)
            {
                Building_GravEngine engine = DockController.FindEngineForShip(map, ship.primaryConsole);
                if (engine == null) return false;
                allowedCells = engine.AllConnectedSubstructure;
                rotation = SpaceTransform.FocusedMeshRotation(frame, 0f);
                drawPos = SpaceTransform.FocusedDrawPosition(frame, frameMap, obj.pose.position, 0f);
                sourceOrigin = DockController.DockedOriginMapPoint(ship);
            }
            else
            {
                rotation = SpaceTransform.FocusedMeshRotation(frame, obj.pose.angle);
                drawPos = SpaceTransform.FocusedDrawPosition(frame, frameMap, obj.pose.position, 0f);
                sourceOrigin = SpaceTransform.ShipLocalOrigin(map);
            }

            Vector3 translation = drawPos - rotation * new Vector3(sourceOrigin.x, 0f, sourceOrigin.y);
            Matrix4x4 matrix = Matrix4x4.TRS(translation, rotation, Vector3.one);
            CellRect localView = TransformRectInverse(matrix, Find.CameraDriver.CurrentViewRect).ExpandedBy(4);
            bool drew = MapOverlayRenderer.CollectShellCommands(map, matrix, rotation, localView, allowedCells);
            if (drew)
            {
                // 火焰与壳内容同表同缓冲：renderQueue 3201 排在本壳地形之上，随本壳
                // 走背景（聚焦图之下）或前台（进场时盖住目标甲板）。POI 与停靠船
                // 不参与（不出力）。
                if (ship != null && !docked)
                {
                    ThrusterEffects.CollectShellFlames(ship, matrix, rotation);
                }
                MapOverlayRenderer.EmitShellCommands(buffer);
            }
            return drew;
        }

        /// <summary>分层摘要追加一壳：槽位（b=背景 / f=前台进场）、ID、标签、状态、
        /// 源图与提交四边形数；drew=false 时只记 SKIP（原因 = 图空/销毁/生成中/
        /// 停靠本图/无引擎，可由状态字段推断）。</summary>
        private static void AppendLayerObj(string slot, SpaceObject obj, bool drew)
        {
            LayerLogBuf.Append(" | ").Append(slot)
                .Append(" obj").Append(obj.objectId);
            if (obj.label != null) LayerLogBuf.Append('(').Append(obj.label).Append(')');
            ShipState ship = obj as ShipState;
            if (ship != null && ship.docked) LayerLogBuf.Append(" docked");
            if (ship != null && ship.docking) LayerLogBuf.Append(" docking");
            if (!drew)
            {
                LayerLogBuf.Append(" SKIP");
                return;
            }
            Map map = ship != null && ship.docked ? ship.dockedMap : obj.map;
            LayerLogBuf.Append(" map#").Append(map.uniqueID)
                .Append('(').Append(map.Size.x).Append('x').Append(map.Size.z).Append(')')
                .Append(" q=").Append(MapOverlayRenderer.QuadsDrawnLastPass);
        }

        public static void ClearRuntimeCaches()
        {
            DetachShellBuffers();
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
