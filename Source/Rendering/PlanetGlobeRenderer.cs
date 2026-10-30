using System.Linq;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
        /// <summary>
        /// 左侧面板星球仪（星区架构 §11.1；星区地图形态 = 星球仪连续缩放，§12.2 定稿）。
        ///
        /// 拉远是全局态势：南北轴（星区网格淡入时同步渐隐）+ 经纬网格（背面剔除）、
        /// 玩家船光标三角 + 速度矢量；拉近显示真实 tile 顶点的星区网格。
        /// 对象按 GlobeDisplayInfo 的实际面积决定出现和名称标注；图标/投影按视图尺度切换。
        /// 原版 POI 与玩家船常驻，未加载时保留图标，残骸隐藏常规标注。
        /// 名称与距离统一居中于图例（屏幕方位）下方。
        ///
        /// 图标可悬停（名称/距离/未物化注记）、可点击聚焦（SetFocused + 相机跳转）；
        /// 未物化对象点击提示「尚未进入可视范围」。缩放经 Zoom01（滑条，面板提供）与
        /// 星球仪区域内滚轮调整；数据同源 SpacePose / OrbitSphere.Basis，不引入第二套坐标。
        /// 显示层只是投影，不是宇宙状态的事实源。本交互层与分帧地图生成均计划抽公共库
        ///（doc/TODO.md 长期项）。
        /// </summary>
    public static class PlanetGlobeRenderer
    {
        /// <summary>缩放滑条位置 [0,1]：0 = 整球，1 = 最近（会话 UI 态，不持久化）。</summary>
        public static float Zoom01;

        /// <summary>最近档视场半宽 = 聚焦船最长边 × 此系数（视场全宽 = 6× 最长边，对接引导档）。</summary>
        private const float MinViewHalfShipSides = 3f;

        /// <summary>剪影/最小视场的最长边兜底（无聚焦船或无法测定时）。</summary>
        private const float FallbackSideCells = 100f;

        /// <summary>星区网格绘制的 tile 数上限（BFS 防失控）。</summary>
        private const int MaxHexTiles = 400;

        /// <summary>星区网格淡入阈值（× CellsPerTile）。</summary>
        private const float HexLodFactor = 6f;

        private static Material lineMaterial;

        /// <summary>极点退化时的显示 roll fallback：上一次稳定的屏幕基。</summary>
        private static Vector3 stableUp = Vector3.up;

        private static Vector3 stableRight = Vector3.right;

        // 可点击条目（每帧重建；图标/三角绘制时登记，GUI 阶段统一挂 tooltip 与按钮）。
        private readonly struct Clickable
        {
            public readonly Rect rect;
            public readonly SpaceObject obj;
            public readonly string tip;

            public Clickable(Rect rect, SpaceObject obj, string tip)
            {
                this.rect = rect;
                this.obj = obj;
                this.tip = tip;
            }
        }

        private static readonly List<Clickable> Clickables = new List<Clickable>(32);

        internal sealed class Legend
        {
            internal GlobeDisplayEntry entry;
            internal Vector2 position;
            internal Rect rect;
            internal Texture2D texture;
            internal bool projection, focused;
            internal Color color;
            internal float angle;
        }
        private static readonly List<Legend> Legends = new List<Legend>();
        private sealed class CachedSilhouette
        {
            internal GlobeDisplayEntry source;
            internal Texture2D texture;
        }
        private static readonly Dictionary<Map, Dictionary<DriveSubject, CachedSilhouette>> SilhouetteCache =
            new Dictionary<Map, Dictionary<DriveSubject, CachedSilhouette>>();

        // 六边形网格 BFS / 顶点缓冲（复用，避免每帧分配）。
        private static readonly List<PlanetTile> HexQueue = new List<PlanetTile>(MaxHexTiles);
        private static readonly HashSet<PlanetTile> HexSeen = new HashSet<PlanetTile>();
        private static readonly List<PlanetTile> HexNeighbors = new List<PlanetTile>(8);
        private static readonly List<Vector3> TileVerts = new List<Vector3>(10);

        private static void EnsureMaterial()
        {
            if (lineMaterial != null) return;
            // Only painting needs Unity resources; cache cleanup also works without a rendered globe.
            lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored"))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            // Shader property values are part of the Unity material contract; 1.7 omits the enums.
            lineMaterial.SetInt("_SrcBlend", 5);
            lineMaterial.SetInt("_DstBlend", 10);
            lineMaterial.SetInt("_Cull", 0);
            lineMaterial.SetInt("_ZWrite", 0);
        }

        public static void Draw(Rect rect, UniverseManager universe, SpaceObject contextFrame)
        {
            if (universe == null || !OrbitSphere.Valid) return;
            // 参考船（视图锚 = 船体位姿）：聚焦优先、回退任意玩家船；无船时以当前图
            // 帧位置为相机锚点（仍是"飞船（所在处）—球心"语义的合理退化）。
            ShipBase referenceShip = universe.PrimaryPlayerShip();
            SpaceObject reference = universe.Focused
                ?? (referenceShip.IsValid ? universe.FrameOf(referenceShip.Map) : null)
                ?? contextFrame;
            Vector3 referenceDir = referenceShip.IsValid
                ? ShipRegistry.ShipPose(referenceShip).position
                : reference?.pose.position ?? Vector3.up;
            if (referenceDir.sqrMagnitude < 1e-6f) return;

            Vector2 center = rect.center;
            float radius = Mathf.Min(rect.width, rect.height) * 0.5f - 4f;
            HandleWheelZoom(rect);

            // 最近档视场半宽 = 3×参考船最长边（全宽 6×，对接前航向调整用）。
            float sideCells = SideCellsOf(referenceShip);
            float minHalfCells = Mathf.Max(MinViewHalfShipSides * sideCells, 120f);

            // 缩放映射（对数）：Zoom01=0 时 pixelsPerRad == radius（整球），1 时视场半宽
            // 收窄到 minHalfCells。阈值以格为单位，尺度设置变化时自动联动。
            float cellsPerRadian = OrbitSphere.CellsPerRadian;
            float halfWidthPx = rect.width * 0.5f;
            float maxHalfCells = cellsPerRadian * halfWidthPx / radius;
            float viewHalfCells = maxHalfCells > minHalfCells
                ? Mathf.Exp(Mathf.Lerp(Mathf.Log(maxHalfCells), Mathf.Log(minHalfCells), Zoom01))
                : maxHalfCells;
            float pixelsPerRad = halfWidthPx * cellsPerRadian / viewHalfCells;
            float pixelsPerCell = pixelsPerRad / cellsPerRadian;
            float cellsPerTile = OrbitSphere.CellsPerTile;
            float hexAlpha = Mathf.Clamp01((HexLodFactor * cellsPerTile - viewHalfCells)
                / (HexLodFactor * cellsPerTile * 0.5f));

            // 视图基：相机位于飞船—球心连线的船侧、指向球心，屏幕向上 = 世界 up 的投影。
            Vector3 shipDir = referenceDir.normalized;
            Vector3 forward = -shipDir;
            Vector3 up = Vector3.up - forward * Vector3.Dot(Vector3.up, forward);
            if (up.sqrMagnitude < 1e-4f)
            {
                up = stableUp; // 极点：roll 不适定，保持上次稳定 roll
            }
            else
            {
                up.Normalize();
                stableUp = up;
            }
            Vector3 right = Vector3.Cross(up, forward);
            if (right.sqrMagnitude < 1e-4f)
            {
                right = stableRight;
            }
            else
            {
                right.Normalize();
                stableRight = right;
            }

            bool repaint = Event.current.type == EventType.Repaint;
            if (repaint)
            {
                EnsureMaterial();
                lineMaterial.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, UI.screenWidth, UI.screenHeight, 0f);
            }

            // 世界方向 → GUI 屏幕偏移：垂直分量取负（GUI y 向下，"上"是 -y）。
            Vector2 Project(Vector3 dir)
            {
                return center + new Vector2(Vector3.Dot(dir, right), -Vector3.Dot(dir, up)) * pixelsPerRad;
            }

            if (repaint)
            {
                // ---- 经纬网格（每 30° 一条；背面剔除；随拉近淡出；全部线段裁剪到矩形内）----
                GL.Begin(GL.LINES);
                GL.Color(new Color(1f, 1f, 1f, Mathf.Lerp(0.28f, 0.10f, Zoom01)));
                for (int lat = -60; lat <= 60; lat += 30)
                {
                    Vector3 prev = Spherical(lat, 0f);
                    for (int lon = 6; lon <= 360; lon += 6)
                    {
                        Vector3 next = Spherical(lat, lon);
                        if (Vector3.Dot((prev + next).normalized, shipDir) > 0.04f)
                        {
                            ClippedLine(Project(prev), Project(next), rect);
                        }
                        prev = next;
                    }
                }
                for (int lon2 = 0; lon2 < 360; lon2 += 30)
                {
                    Vector3 prev2 = Spherical(-84f, lon2);
                    for (int lat2 = -78; lat2 <= 84; lat2 += 6)
                    {
                        Vector3 next = Spherical(lat2, lon2);
                        if (Vector3.Dot((prev2 + next).normalized, shipDir) > 0.04f)
                        {
                            ClippedLine(Project(prev2), Project(next), rect);
                        }
                        prev2 = next;
                    }
                }
                GL.End();

                // ---- 星区六边形网格（真实 tile 顶点；LOD 淡入）----
                if (hexAlpha > 0.01f)
                {
                    float tileRadiusAngle = OrbitSphere.Layer.AverageTileSize * 2.5f / OrbitSphere.Radius;
                    float viewAngleDeg = (viewHalfCells / cellsPerRadian + tileRadiusAngle * 2.5f) * Mathf.Rad2Deg;
                    DrawSectorHexes(shipDir, referenceDir, Project, hexAlpha, viewAngleDeg, rect);
                }

                // ---- 南北轴：极点向外延长的可见段（裁剪到矩形）；星区网格淡入时同步
                // 渐隐——星区层级地轴不再提供态势信息（2026-09-18 用户决策），与 N 标签
                // 同用 hexAlpha 门控，轴用连续渐隐过渡。----
                if (hexAlpha < 0.999f)
                {
                    GL.Begin(GL.LINES);
                    GL.Color(new Color(0.7f, 0.9f, 1f, 0.75f * (1f - hexAlpha)));
                    if (Vector3.Dot(Vector3.up, shipDir) > 0.2f)
                    {
                        ClippedLine(center, Project(Vector3.up * (1f + 18f / pixelsPerRad)), rect);
                    }
                    if (Vector3.Dot(-Vector3.up, shipDir) > 0.2f)
                    {
                        ClippedLine(center, Project(-Vector3.up * (1f + 18f / pixelsPerRad)), rect);
                    }
                    GL.End();
                }

            }

            Clickables.Clear();
            Legends.Clear();
            foreach (SpaceObject obj in universe.objects)
            {
                foreach (GlobeDisplayEntry entry in universe.GlobeDisplay.Entries(obj))
                {
                    if (!GlobeDisplayRules.Visible(entry.area, pixelsPerCell, entry.AlwaysVisible)) continue;
                    SpacePose pose = entry.Pose;
                    if (Vector3.Dot(pose.position, shipDir) <= 0f) continue;
                    Vector2 pos = Project(pose.position);
                    bool projection = GlobeDisplayRules.UseProjection(sideCells, pixelsPerCell,
                        obj.map != null && entry.cells?.Count > 0);
                    Texture2D texture = projection ? GetSilhouette(entry) : null;
                    projection = texture != null;
                    bool focused = entry.ship != null && referenceShip.IsValid
                        && entry.ship.key == referenceShip.Identity.key
                        && entry.ship.shuttle == (referenceShip is ShuttleShip);
                    Color tint = focused ? new Color(1f, 0.95f, 0.4f)
                        : entry.Faction == Faction.OfPlayerSilentFail ? Color.white
                        : entry.Faction?.Color ?? new Color(1f, 0.85f, 0.55f);
                    if (!projection && entry.native && obj is PoiState native)
                    {
                        texture = native.nativeParent.ExpandingIcon;
                        tint = native.nativeParent.ExpandingIconColor;
                    }
                    float size = projection ? texture.width * pixelsPerCell / ShipSilhouette.AnchoredPixelsPerCell : 18f;
                    float angle = projection
                        ? SilhouetteScreenAngle(pose.position, obj.pose.angle, right, up) : 0f;
                    var legendRect = new Rect(pos.x - size * 0.5f, pos.y - size * 0.5f, size, size);
                    if (!legendRect.Overlaps(rect)) continue;
                    var legend = new Legend { entry = entry, position = pos, rect = legendRect,
                        texture = texture, projection = projection, focused = focused, angle = angle, color = tint };
                    Legends.Add(legend);
                }
            }
            RemoveRedundantNativeIcons(Legends);
            if (repaint)
            {
                foreach (Legend legend in Legends)
                {
                    // A projection replaces the entire symbol, including its vector arrow.
                    if (legend.projection) continue;
                    SpacePose pose = legend.entry.Pose;
                    OrbitSphere.Basis(pose.position, out Vector3 east, out Vector3 north);
                    Vector2 heading = ScreenDir(east, north, pose.angle, right, up);
                    if (legend.texture == null)
                    {
                        if (legend.entry.craft)
                        {
                            DrawCursorTriangle(legend.position, legend.focused ? 12f : 9f, heading, legend.color);
                        }
                        else
                        {
                            DrawDiamond(legend.position, 6f, legend.color);
                        }
                    }
                    if (legend.entry.ship != null && pose.velocity.sqrMagnitude > 0.01f
                        && (legend.focused || GlobeDisplayRules.ShowLabel(
                            legend.entry.area, pixelsPerCell, legend.entry.wreck)))
                    {
                        DrawArrow(legend.position + heading * 12f,
                            ScreenDir(east, north, Mathf.Atan2(pose.velocity.y, pose.velocity.x), right, up),
                            legend.focused ? 26f : 14f, new Color(0.5f, 1f, 0.6f), rect);
                    }
                }
            }
            if (repaint) GL.PopMatrix();

            // GUI events and painting consume exactly the same visible legends.
            foreach (Legend legend in Legends)
            {
                if (legend.texture != null)
                {
                    GUI.color = legend.color;
                    Widgets.DrawTextureRotated(legend.rect, legend.texture, legend.angle);
                    GUI.color = Color.white;
                }
                Rect hit = legend.rect;
                hit.xMin = Mathf.Max(hit.xMin, rect.xMin);
                hit.xMax = Mathf.Min(hit.xMax, rect.xMax);
                hit.yMin = Mathf.Max(hit.yMin, rect.yMin);
                hit.yMax = Mathf.Min(hit.yMax, rect.yMax);
                RegisterClickable(hit, legend.entry, referenceDir);
                if (!GlobeDisplayRules.ShowLabel(legend.entry.area, pixelsPerCell, legend.entry.wreck)) continue;
                Text.Font = GameFont.Tiny;
                float distance = OrbitSphere.ArcCells(referenceDir, legend.entry.Pose.position);
                string text = legend.entry.Label + " · "
                    + "DeltaV_PanelGlobeDistance".Translate(((int)distance).ToString("N0"));
                Vector2 textSize = Text.CalcSize(text);
                float extent = legend.projection
                    ? SilhouetteHalfHeightPx(legend.rect.width, legend.angle) : legend.rect.height * 0.5f;
                float x = Mathf.Clamp(legend.position.x - textSize.x * 0.5f, rect.xMin + 2f,
                    Mathf.Max(rect.xMin + 2f, rect.xMax - textSize.x - 2f));
                var labelRect = new Rect(x, legend.position.y + extent + 4f, textSize.x + 2f, textSize.y);
                if (labelRect.Overlaps(rect)) Widgets.Label(labelRect, text);
                Text.Font = GameFont.Small;
            }
            if (Vector3.Dot(Vector3.up, shipDir) > 0.2f && hexAlpha < 1f)
            {
                Vector2 n = Project(Vector3.up * (1f + 24f / pixelsPerRad));
                if (rect.ExpandedBy(20f).Contains(n))
                {
                    Text.Font = GameFont.Tiny;
                    GUI.color = new Color(0.7f, 0.9f, 1f, 0.9f);
                    Widgets.Label(new Rect(n.x - 10f, n.y - 8f, 20f, 16f), "N");
                    GUI.color = Color.white;
                }
            }

            foreach (Clickable clickable in Clickables)
            {
                TooltipHandler.TipRegion(clickable.rect, clickable.tip);
                if (Widgets.ButtonInvisible(clickable.rect, false))
                {
                    FocusObject(universe, clickable.obj);
                }
            }

            Text.Font = GameFont.Small;
            GUI.color = Color.white;
        }

        // A native parent icon is the container fallback. Once its geometry is represented
        // by a visible projection, keep that projection and the independent member markers.
        internal static void RemoveRedundantNativeIcons(List<Legend> legends)
        {
            var projected = new HashSet<SpaceObject>();
            foreach (Legend legend in legends)
                if (legend.projection) projected.Add(legend.entry.owner);
            legends.RemoveAll(legend => legend.entry.native && !legend.projection
                && projected.Contains(legend.entry.owner));
        }

        // ---- 缩放交互 ----

        private static void HandleWheelZoom(Rect rect)
        {
            if (Event.current.type != EventType.ScrollWheel || !Mouse.IsOver(rect)) return;
            Zoom01 = Mathf.Clamp01(Zoom01 - Event.current.delta.y * 0.05f);
            Event.current.Use();
        }

        // ---- 点击聚焦 ----

        private static void RegisterClickable(Rect rect, GlobeDisplayEntry entry, Vector3 referenceDir)
        {
            float distance = OrbitSphere.ArcCells(referenceDir, entry.Pose.position);
            string tip = entry.Label + "\n" + "DeltaV_PanelGlobeDistance".Translate(((int)distance).ToString("N0"));
            if (entry.owner.InteriorMap == null) tip += "\n" + "DeltaV_PanelObjectNotLoaded".Translate();
            Clickables.Add(new Clickable(rect, entry.owner, tip));
        }

        private static void FocusObject(UniverseManager universe, SpaceObject obj)
        {
            Map map = obj.InteriorMap;
            if (map == null)
            {
                // 未物化对象无法聚焦（内部尚未加载）——星区架构：接近后物化即可。
                Messages.Message("DeltaV_PanelObjectNotLoaded".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            universe.SetFocused(obj);
            CameraJumper.TryJump(map.Center, map, CameraJumper.MovementMode.Cut);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"globe focus '{obj.label}': map={map.Parent?.Label ?? map.ToString()} size={map.Size.x}x{map.Size.z}" +
                $" frozen={universe.Lifecycle.IsFrozen(map)} rootSize={Find.CameraDriver?.RootSize:F0}");
        }

        // ---- 星区六边形网格 ----

        private static void DrawSectorHexes(Vector3 shipDir, Vector3 referenceDir,
            System.Func<Vector3, Vector2> project, float alpha, float viewAngleDeg, Rect clipRect)
        {
            PlanetLayer layer = OrbitSphere.Layer;
            PlanetTile rootTile = OrbitSphere.TileFromDirection(referenceDir);
            if (layer == null || !rootTile.Valid) return;

            // BFS 收集视场角半径内的 tile（含边界余量），上限 MaxHexTiles。
            HexQueue.Clear();
            HexSeen.Clear();
            HexQueue.Add(rootTile);
            HexSeen.Add(rootTile);
            for (int cursor = 0; cursor < HexQueue.Count && HexQueue.Count < MaxHexTiles; cursor++)
            {
                PlanetTile tile = HexQueue[cursor];
                Vector3 tileCenter = layer.GetTileCenter(tile);
                if (Vector3.Dot(tileCenter.normalized, shipDir) < -0.2f) continue; // 深背面不再扩展
                if (Vector3.Angle(tileCenter, referenceDir) > viewAngleDeg) continue;
                HexNeighbors.Clear();
                layer.GetTileNeighbors(tile, HexNeighbors);
                foreach (PlanetTile neighbor in HexNeighbors)
                {
                    if (HexSeen.Add(neighbor))
                    {
                        HexQueue.Add(neighbor);
                    }
                }
            }

            GL.Begin(GL.LINES);
            GL.Color(new Color(0.35f, 1f, 0.9f, 0.45f * alpha));
            foreach (PlanetTile tile in HexQueue)
            {
                Vector3 tileCenter = layer.GetTileCenter(tile);
                if (Vector3.Dot(tileCenter.normalized, shipDir) < 0.05f) continue; // 背面 tile 不画
                TileVerts.Clear();
                layer.GetTileVertices(tile, TileVerts);
                if (TileVerts.Count < 3) continue;
                // GetTileVertices 返回世界空间顶点（模长 = 层半径）；Project 按单位方向
                // 设计——不归一化会把六边形投影到屏幕外百万像素处（星区边界消失的根因）。
                Vector2 first = project(TileVerts[0].normalized);
                Vector2 prevPoint = first;
                for (int i = 1; i <= TileVerts.Count; i++)
                {
                    Vector2 point = i == TileVerts.Count ? first : project(TileVerts[i].normalized);
                    ClippedLine(prevPoint, point, clipRect);
                    prevPoint = point;
                }
            }
            GL.End();
        }

        // ---- 剪影（物化对象；按 Map+归属 ID 扫描，纹理经 ShipSilhouette 唯一入口）----

        private static Texture2D GetSilhouette(GlobeDisplayEntry entry)
        {
            Map map = entry.owner.map;
            if (map == null || entry.cells == null || entry.cells.Count == 0) return null;
            DriveSubject key = entry.ship == null ? default
                : new DriveSubject(0, entry.ship.key, entry.ship.shuttle);
            if (!SilhouetteCache.TryGetValue(map, out var byShip))
                SilhouetteCache[map] = byShip = new Dictionary<DriveSubject, CachedSilhouette>();
            if (byShip.TryGetValue(key, out var previous) && !ReferenceEquals(previous.source, entry)
                && previous.source.mapAnchor == entry.mapAnchor
                && previous.source.cells.SequenceEqual(entry.cells)) previous.source = entry;
            if (!byShip.TryGetValue(key, out var cached) || !ReferenceEquals(cached.source, entry))
            {
                if (cached?.texture != null) Object.Destroy(cached.texture);
                cached = new CachedSilhouette { source = entry,
                    texture = ShipSilhouette.BuildTexture(entry.cells,
                        $"DeltaV_Globe_{map.uniqueID}_{key}", entry.mapAnchor) };
                byShip[key] = cached;
            }
            return cached.texture;
        }

        private static float SideCellsOf(ShipBase reference)
        {
            if (!reference.IsValid) return FallbackSideCells;
            CellRect bounds = CellRect.FromCellList(reference.HullCells.ToList());
            return Mathf.Max(bounds.Width, bounds.Height, 1f);
        }

        /// <summary>
        /// 剪影旋转角（度）：forward 世界方向经 SpaceTransform.MarkerForwardWorld 导出
        /// （与世界视图剪影同一条旋转链，两处显示不分叉）；RotateAroundPivot 在 GUI
        /// （y 向下）中正角 = 视觉顺时针，故 φ = Atan2(+f.y, f.x)——此前按逆时针推导
        /// 符号写反，产生 2×朝向角的偏差（2026-09-17 实测约 45°）。该公式对
        /// ShipSilhouette 标准朝向基底（北在上、东在右，旋转 0 时 forward=+x 指屏幕
        /// 右）成立：旋转 α 后纹理 +x 落在 φ、+y（北）落在 φ−90°，与世界系一致；
        /// 旧实现纹理上下颠倒时实测调符号只做了部分补偿（表现为左右反）。
        /// </summary>
        private static float SilhouetteScreenAngle(Vector3 universeDir, float universeAngle,
            Vector3 right, Vector3 up)
        {
            Vector3 forwardWorld = SpaceTransform.MarkerForwardWorld(universeDir, universeAngle);
            Vector2 forwardScreen = new Vector2(
                Vector3.Dot(forwardWorld, right), -Vector3.Dot(forwardWorld, up));
            return Mathf.Atan2(forwardScreen.y, forwardScreen.x) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// 剪影旋转后包围盒的半高（像素）：剪影是正方形纹理，旋转 θ 后包围盒高 =
        /// 边长 × (|cosθ| + |sinθ|)。是「下方」偏移的依据——屏幕方位（GUI y 向下），
        /// 不随极点 roll 变化。
        /// </summary>
        private static float SilhouetteHalfHeightPx(float sizePx, float angleDeg)
        {
            float rad = angleDeg * Mathf.Deg2Rad;
            return sizePx * 0.5f * (Mathf.Abs(Mathf.Cos(rad)) + Mathf.Abs(Mathf.Sin(rad)));
        }

        public static void ClearRuntimeCaches()
        {
            foreach (var byShip in SilhouetteCache.Values)
                foreach (var cached in byShip.Values)
                    if (cached.texture != null) Object.Destroy(cached.texture);
            SilhouetteCache.Clear();
            Legends.Clear();
            Clickables.Clear();
        }

        public static void InvalidateSilhouette(Map map)
        {
            if (map == null) return;
            if (SilhouetteCache.TryGetValue(map, out var byShip))
            {
                foreach (var cached in byShip.Values)
                    if (cached.texture != null) Object.Destroy(cached.texture);
                SilhouetteCache.Remove(map);
            }
        }

        // ---- 基础绘制 ----

        /// <summary>
        /// 画一条裁剪到 clip 矩形内的线段（Liang-Barsky）。GL 即时绘制不受 GUI 裁剪，
        /// 放大后投影点会跑到星球仪矩形外（2026-09-17 实测：经纬网格溢出面板）。
        /// 调用方必须已处于 GL.Begin(GL.LINES) 块中。
        /// </summary>
        private static void ClippedLine(Vector2 a, Vector2 b, Rect clip)
        {
            float t0 = 0f, t1 = 1f;
            float dx = b.x - a.x, dy = b.y - a.y;
            for (int edge = 0; edge < 4; edge++)
            {
                float p, q;
                switch (edge)
                {
                    case 0: p = -dx; q = a.x - clip.xMin; break;
                    case 1: p = dx; q = clip.xMax - a.x; break;
                    case 2: p = -dy; q = a.y - clip.yMin; break;
                    default: p = dy; q = clip.yMax - a.y; break;
                }
                if (Mathf.Abs(p) < 1e-10f)
                {
                    if (q < 0f) return; // 平行且在边界外
                    continue;
                }
                float r = q / p;
                if (p < 0f)
                {
                    if (r > t1) return;
                    if (r > t0) t0 = r;
                }
                else
                {
                    if (r < t0) return;
                    if (r < t1) t1 = r;
                }
            }
            Vector2 start = a + new Vector2(dx, dy) * t0;
            Vector2 end = a + new Vector2(dx, dy) * t1;
            Vertex(start);
            Vertex(end);
        }

        private static Vector2 ScreenDir(Vector3 east, Vector3 north, float angle, Vector3 right, Vector3 up)
        {
            Vector2 tangent = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector3 world = east * tangent.x + north * tangent.y;
            return new Vector2(Vector3.Dot(world, right), -Vector3.Dot(world, up)).normalized;
        }

        private static Vector3 Spherical(float latDeg, float lonDeg)
        {
            float lat = latDeg * Mathf.Deg2Rad;
            float lon = lonDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(lat) * Mathf.Cos(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Sin(lon));
        }

        private static void Vertex(Vector2 p)
        {
            GL.Vertex3(p.x, p.y, 0f);
        }

        /// <summary>底边内凹三角（类似鼠标光标）：尖朝 heading 方向。</summary>
        private static void DrawCursorTriangle(Vector2 c, float size, Vector2 heading, Color color)
        {
            Vector2 dir = heading.sqrMagnitude < 1e-6f ? Vector2.up : heading.normalized;
            Vector2 side = new Vector2(-dir.y, dir.x);
            Vector2 tip = c + dir * size;
            Vector2 baseRight = c - dir * (size * 0.62f) + side * (size * 0.72f);
            Vector2 baseLeft = c - dir * (size * 0.62f) - side * (size * 0.72f);
            Vector2 notch = c - dir * (size * 0.18f);
            GL.Begin(GL.TRIANGLES);
            GL.Color(color);
            Vertex(tip);
            Vertex(baseRight);
            Vertex(notch);
            Vertex(tip);
            Vertex(notch);
            Vertex(baseLeft);
            GL.End();
        }

        private static void DrawDiamond(Vector2 c, float size, Color color)
        {
            GL.Begin(GL.TRIANGLES);
            GL.Color(color);
            Vertex(c + Vector2.up * size);
            Vertex(c + Vector2.right * size);
            Vertex(c - Vector2.up * size);
            Vertex(c + Vector2.right * size);
            Vertex(c - Vector2.up * size);
            Vertex(c - Vector2.right * size);
            GL.End();
        }

        private static void DrawArrow(Vector2 from, Vector2 dir, float length, Color color, Rect clip)
        {
            if (dir.sqrMagnitude < 1e-6f) return;
            dir.Normalize();
            Vector2 tip = from + dir * length;
            Vector2 side = new Vector2(-dir.y, dir.x) * 5f;
            GL.Begin(GL.LINES);
            GL.Color(color);
            ClippedLine(from, tip, clip);
            ClippedLine(tip, tip - dir * 6f + side, clip);
            ClippedLine(tip, tip - dir * 6f - side, clip);
            GL.End();
        }
    }
}
