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
        /// 拉远是全局态势：南北轴（星区网格淡入时同步渐隐）+ 自适应单位经纬网格（背面剔除）、
        /// 玩家船光标三角 + 速度矢量；拉近显示真实 tile 顶点的星区网格。
        /// 对象按 GlobeDisplayInfo 的实际面积决定出现和名称标注；图标/投影按视图尺度切换。
        /// 原版 POI 与玩家船常驻，未加载时保留图标，残骸隐藏常规标注。
        /// 名称与距离统一居中于图例（屏幕方位）下方。
        ///
        /// 图标可悬停与选择；查看内部地图由独立按钮显式触发。
        /// 距离缩放与导航状态归 UniverseManager；小图和展开地图复用同一绘制与选择入口。
        /// 数据同源 SpacePose / OrbitSphere.Basis，不引入第二套坐标。
        /// 显示层只是投影，不是宇宙状态的事实源。本交互层与分帧地图生成均计划抽公共库
        ///（doc/TODO.md 长期项）。
        /// </summary>
    public static partial class PlanetGlobeRenderer
    {
        /// <summary>星区网格绘制的 tile 数上限（BFS 防失控）。</summary>
        private const int MaxHexTiles = 400;

        /// <summary>星区网格淡入阈值（× CellsPerTile）。</summary>
        private const float HexLodFactor = 6f;

        private static Material lineMaterial;

        internal sealed class Legend
        {
            internal GlobeDisplayEntry entry;
            internal Vector2 position;
            internal Rect rect;
            internal bool projection, focused, important, edge, behind;
            internal Rect hitRect, labelRect;
            internal readonly List<Legend> members = new List<Legend>();
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

        public static void Draw(Rect rect, UniverseManager universe, SpaceObject contextFrame, bool draggable = false)
        {
            if (universe == null || !OrbitSphere.Valid) return;
            var state = universe.GlobeNavigation;
            state.Refresh(universe);
            GlobeView view = GlobeView.Create(rect, universe, contextFrame);
            var reserved = draggable ? new List<Rect> { ScaleRect(rect, true) }
                : GlobeNavigationPanel.CompactReservedRects(rect);
            Vector2? click = state.HandleMapInput(view, draggable, reserved);
            if (draggable) view = GlobeView.Create(rect, universe, contextFrame);
            if (Event.current.type == EventType.ScrollWheel && Mouse.IsOver(rect))
            {
                state.Width = GlobeNavigation.Zoom(view.Width, Event.current.delta.y, view.MaximumWidth);
                view = view.WithWidth(state.Width);
                Event.current.Use();
            }
            state.Width = view.Width;
            Vector2 center = rect.center;
            Vector3 referenceDir = view.Center, shipDir = view.Center;
            Vector3 up = view.Up, right = view.Right;
            float pixelsPerRad = view.PixelsPerRad;
            float viewHalfCells = view.Width * .5f;
            float cellsPerRadian = view.CellsPerRadian;
            float cellsPerTile = OrbitSphere.CellsPerTile;
            float hexAlpha = Mathf.Clamp01((HexLodFactor * cellsPerTile - viewHalfCells)
                / (HexLodFactor * cellsPerTile * .5f));
            bool repaint = Event.current.type == EventType.Repaint;
            if (repaint)
            {
                EnsureMaterial();
                lineMaterial.SetPass(0);
                GL.PushMatrix();
#if !RIMWORLD_1_7
                // GL uses absolute UI units; a Window's GUI origin is returned in physical pixels.
                Vector2 origin = GUIUtility.GUIToScreenPoint(Vector2.zero) / Prefs.UIScale;
                GL.LoadPixelMatrix(-origin.x, UI.screenWidth - origin.x, UI.screenHeight - origin.y, -origin.y);
#endif
                // Waylith's relative GL path already applies the same group and UI
                // transform as labels/textures. Loading a pixel matrix bypasses it.
            }

            // 世界方向 → GUI 屏幕偏移：垂直分量取负（GUI y 向下，"上"是 -y）。
            Vector2 Project(Vector3 dir)
            {
                return view.Project(dir);
            }

            if (repaint)
            {
                // ---- 自适应单位经纬网格：任何缩放都保持约 50px 间距，局部视图不再因
                // 固定 30° 间距而呈现为空白；只绘当前视域附近的球面经纬线。----
                if (universe.GlobeFilters.showCoordinateGrid)
                    DrawCoordinateGrid(view, shipDir, Project, rect);

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

            Legends.Clear();
            foreach (GlobeDisplayEntry entry in state.Entries)
            {
                if (!state.IsDisplayed(entry)) continue;
                bool important = state.Important(entry);
                bool station = entry.station && view.Width <= cellsPerTile * 6f;
                bool reference = entry.ship != null && view.ReferenceShip?.IsValid == true
                    && entry.ship.key == view.ReferenceShip.Identity.key && entry.ship.shuttle == (view.ReferenceShip is ShuttleShip);
                if (!entry.Minimal && !important && !station && !reference && !GlobeDisplayRules.Visible(entry.area, view.PixelsPerCell, entry.AlwaysVisible)) continue;
                Legend legend = LayoutLegend(view, entry, important);
                if (legend == null) continue;
                Legends.Add(legend);
            }
            RemoveRedundantNativeIcons(Legends);
            Aggregate(Legends);
            if (repaint)
            {
                foreach (Legend legend in Legends)
                {
                    if (legend.entry.Minimal)
                    {
                        if (legend.entry.projectile == null) DrawDiamond(legend.position, 1.5f, legend.color);
                        continue;
                    }
                    if (legend.projection || legend.members.Count > 0) continue;
                    SpacePose pose = legend.entry.Pose;
                    OrbitSphere.Basis(pose.position, out Vector3 east, out Vector3 north);
                    Vector2 heading = ScreenDir(east, north, pose.angle, right, up);
                    if (legend.edge)
                    {
                        Vector2 direction = view.Direction(pose.position);
                        if (direction.sqrMagnitude > 1e-12f)
                            DrawArrow(legend.position - direction.normalized * 10f, direction, 15f, legend.color, rect);
                        else DrawDiamond(legend.position, 5f, legend.color);
                    }
                    else if (legend.entry.native) { }
                    else if (legend.entry.craft && !legend.entry.station) DrawCursorTriangle(legend.position, 8f, heading, legend.color);
                    else DrawDiamond(legend.position, 6f, legend.color);
                    if (!legend.edge && legend.entry.ship != null && pose.velocity.sqrMagnitude > .01f
                        && (legend.focused || GlobeDisplayRules.ShowLabel(legend.entry.area, view.PixelsPerCell, legend.entry.wreck)))
                        DrawArrow(legend.position + heading * 12f,
                            ScreenDir(east, north, Mathf.Atan2(pose.velocity.y, pose.velocity.x), right, up),
                            legend.focused ? 26f : 14f, new Color(.5f, 1f, .6f), rect);
                }
                GL.PopMatrix();
            }
            DrawLegends(view, state, universe, reserved, click);
            DrawScale(view, draggable);
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
            legends.RemoveAll(legend => legend.entry.native && !legend.projection && !legend.important
                && projected.Contains(legend.entry.owner));
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
        }

        public static void InvalidateSilhouette(Map map)
        {
            if (map == null) return;
            Legends.Clear();
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

        private static readonly float[] CoordinateGridSteps =
            { .1f, .25f, .5f, 1f, 2f, 5f, 10f, 15f, 30f, 45f, 90f };

        internal static float CoordinateGridStepDegrees(GlobeView view)
        {
            float desired = 52f / Mathf.Max(1f, view.PixelsPerRad) * Mathf.Rad2Deg;
            foreach (float step in CoordinateGridSteps)
                if (step >= desired) return step;
            return 90f;
        }

        private static void DrawCoordinateGrid(GlobeView view, Vector3 shipDir,
            System.Func<Vector3, Vector2> project, Rect clip)
        {
            float step = CoordinateGridStepDegrees(view);
            float centerLat = Mathf.Asin(Mathf.Clamp(view.Center.y, -1f, 1f)) * Mathf.Rad2Deg;
            float centerLon = Mathf.Atan2(view.Center.z, view.Center.x) * Mathf.Rad2Deg;
            float horizontal = Mathf.Min(Mathf.PI, view.Width * .62f / view.CellsPerRadian);
            float vertical = horizontal * clip.height / Mathf.Max(1f, clip.width);
            float range = Mathf.Min(180f, Mathf.Max(horizontal, vertical) * Mathf.Rad2Deg * 1.45f + step * 2f);
            float latMin = Mathf.Max(-89.9f, centerLat - range);
            float latMax = Mathf.Min(89.9f, centerLat + range);
            float lonRange = Mathf.Min(180f, range / Mathf.Max(.18f,
                Mathf.Abs(Mathf.Cos(centerLat * Mathf.Deg2Rad))));
            float lonMin = centerLon - lonRange, lonMax = centerLon + lonRange;
            float lonStep = step;
            while ((lonMax - lonMin) / lonStep > 30f) lonStep *= 2f;
            float sample = Mathf.Clamp(step * .25f, .2f, 5f);
            Color ordinary = new Color(.34f, .62f, .72f, .24f);
            Color axis = new Color(.48f, .82f, .92f, .4f);

            GL.Begin(GL.LINES);
            int firstLat = Mathf.CeilToInt(latMin / step), lastLat = Mathf.FloorToInt(latMax / step);
            for (int index = firstLat; index <= lastLat; index++)
            {
                float lat = index * step;
                GL.Color(Mathf.Abs(lat) < step * .25f ? axis : ordinary);
                Vector3 previous = Spherical(lat, lonMin);
                for (float lon = lonMin + sample; lon <= lonMax + .001f; lon += sample)
                {
                    Vector3 next = Spherical(lat, Mathf.Min(lon, lonMax));
                    if (Vector3.Dot((previous + next).normalized, shipDir) > .02f)
                        ClippedLine(project(previous), project(next), clip);
                    previous = next;
                }
            }
            int firstLon = Mathf.CeilToInt(lonMin / lonStep), lastLon = Mathf.FloorToInt(lonMax / lonStep);
            for (int index = firstLon; index <= lastLon; index++)
            {
                float lon = index * lonStep;
                float wrapped = Mathf.Repeat(lon + 180f, 360f) - 180f;
                GL.Color(Mathf.Abs(wrapped) < lonStep * .25f ? axis : ordinary);
                Vector3 previous = Spherical(latMin, lon);
                for (float lat = latMin + sample; lat <= latMax + .001f; lat += sample)
                {
                    Vector3 next = Spherical(Mathf.Min(lat, latMax), lon);
                    if (Vector3.Dot((previous + next).normalized, shipDir) > .02f)
                        ClippedLine(project(previous), project(next), clip);
                    previous = next;
                }
            }
            GL.End();
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
