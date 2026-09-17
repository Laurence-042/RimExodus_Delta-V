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
    /// 拉远是全局态势：南北轴 + 经纬网格（背面剔除）、玩家船光标三角 + 速度矢量；
    /// 拉近按视场半宽（格）逐层显示：星区六边形网格（真实 tile 顶点，GetTileVertices）
    /// → 对象图标（原版 ExpandingIcon + 派系色，Dialog_WorldSearch 同款画法；残骸为
    /// 菱形标记）→ 名称与距离标注。LOD 阈值以 CellsPerTile 为单位，随尺度设置联动。
    ///
    /// 图标可悬停（名称/距离/未物化注记）、可点击聚焦（SetFocused + 相机跳转）；
    /// 未物化对象点击提示「尚未进入可视范围」。缩放经 Zoom01（滑条，面板提供）与
    /// 星球仪区域内滚轮调整；数据同源 SpacePose / OrbitSphere.Basis，不引入第二套坐标。
    /// 显示层只是投影，不是宇宙状态的事实源。本交互层与分帧地图生成均计划抽公共库
    ///（doc/TODO.md 长期项）。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class PlanetGlobeRenderer
    {
        /// <summary>缩放滑条位置 [0,1]：0 = 整球，1 = 最近（会话 UI 态，不持久化）。</summary>
        public static float Zoom01;

        /// <summary>最近档视场半宽 = 聚焦船最长边 × 此系数（视场全宽 = 6× 最长边，对接引导档）。</summary>
        private const float MinViewHalfShipSides = 3f;

        /// <summary>剪影层的视场阈值：聚焦船最长边 × 此系数以内时，物化对象按真实比例画剪影。</summary>
        private const float SilhouetteLodShipSides = 20f;

        /// <summary>剪影/最小视场的最长边兜底（无聚焦船或无法测定时）。</summary>
        private const float FallbackSideCells = 100f;

        /// <summary>星区网格绘制的 tile 数上限（BFS 防失控）。</summary>
        private const int MaxHexTiles = 400;

        /// <summary>LOD 阈值（× CellsPerTile）：网格 / 图标 / 标注。</summary>
        private const float HexLodFactor = 6f;
        private const float IconLodFactor = 2.5f;
        private const float LabelLodFactor = 1f;

        private static readonly Material lineMaterial;

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

        // 剪影覆盖层（GUI 阶段统一旋转绘制；GL/收集阶段只登记）。
        private readonly struct Overlay
        {
            public readonly Rect rect;
            public readonly Texture2D texture;
            public readonly float angleDeg;
            public readonly Color tint;

            public Overlay(Rect rect, Texture2D texture, float angleDeg, Color tint)
            {
                this.rect = rect;
                this.texture = texture;
                this.angleDeg = angleDeg;
                this.tint = tint;
            }
        }

        private static readonly List<Overlay> Overlays = new List<Overlay>(16);

        // 物化对象剪影缓存：按 Substructure 地形扫描（船甲板与残骸甲板同口径；
        // 原版站点地图没有 Substructure → 空 → 继续用原版图标）。快照语义：不追踪战损，
        // 与世界视图剪影一致；可随时销毁重建，不序列化。
        private static readonly Dictionary<Map, Texture2D> SilhouetteCache = new Dictionary<Map, Texture2D>();
        private static readonly HashSet<Map> SilhouetteEmpty = new HashSet<Map>();

        // 六边形网格 BFS / 顶点缓冲（复用，避免每帧分配）。
        private static readonly List<PlanetTile> HexQueue = new List<PlanetTile>(MaxHexTiles);
        private static readonly HashSet<PlanetTile> HexSeen = new HashSet<PlanetTile>();
        private static readonly List<PlanetTile> HexNeighbors = new List<PlanetTile>(8);
        private static readonly List<Vector3> TileVerts = new List<Vector3>(10);

        static PlanetGlobeRenderer()
        {
            // 材质必须在主线程创建（StaticConstructorOnStartup 时机 = play data 载入后）。
            lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored"))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            lineMaterial.SetInt("_ZWrite", 0);
        }

        public static void Draw(Rect rect, UniverseManager universe, SpaceObject contextFrame)
        {
            if (universe == null || !OrbitSphere.Valid) return;
            ShipState reference = universe.Focused ?? universe.PrimaryShip;
            // 首次进入太空（尚未 bootstrap 出任何 ShipState）时以当前图帧位置为相机锚点：
            // 球心—当前所在位置连线，仍是"飞船（所在处）—球心"语义的合理退化。
            Vector3 referenceDir = reference != null
                ? reference.pose.position
                : contextFrame?.pose.position ?? Vector3.up;
            if (referenceDir.sqrMagnitude < 1e-6f) return;

            Vector2 center = rect.center;
            float radius = Mathf.Min(rect.width, rect.height) * 0.5f - 4f;
            HandleWheelZoom(rect);

            // 最近档视场半宽 = 3×聚焦船最长边（全宽 6×，对接前航向调整用）；剪影层阈值同理
            // 以最长边为单位，随船体尺度自适应。
            float sideCells = SideCellsOf(reference);
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
            bool iconLayer = viewHalfCells < IconLodFactor * cellsPerTile;
            bool labelLayer = viewHalfCells < LabelLodFactor * cellsPerTile;
            bool silhouetteLayer = viewHalfCells < SilhouetteLodShipSides * sideCells;

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

            lineMaterial.SetPass(0);
            GL.PushMatrix();
            // GUI 空间是左上原点、y 向下；无参 LoadPixelMatrix 是左下原点、y 向上，
            // 直接用会把整幅 GL 绘制垂直镜像到面板外的镜像位置。显式翻转底/顶对齐 GUI 坐标。
            GL.LoadPixelMatrix(0f, UI.screenWidth, UI.screenHeight, 0f);

            // 世界方向 → GUI 屏幕偏移：垂直分量取负（GUI y 向下，"上"是 -y）。
            Vector2 Project(Vector3 dir)
            {
                return center + new Vector2(Vector3.Dot(dir, right), -Vector3.Dot(dir, up)) * pixelsPerRad;
            }

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

            // ---- 南北轴：极点向外延长的可见段（裁剪到矩形；深拉近距离轴通常在视场外）----
            GL.Begin(GL.LINES);
            GL.Color(new Color(0.7f, 0.9f, 1f, 0.75f));
            if (Vector3.Dot(Vector3.up, shipDir) > 0.2f)
            {
                ClippedLine(center, Project(Vector3.up * (1f + 18f / pixelsPerRad)), rect);
            }
            if (Vector3.Dot(-Vector3.up, shipDir) > 0.2f)
            {
                ClippedLine(center, Project(-Vector3.up * (1f + 18f / pixelsPerRad)), rect);
            }
            GL.End();

            Clickables.Clear();
            Overlays.Clear();

            // ---- 对象点（常驻：全对象小点，远距离态势参考）----
            foreach (SpaceObject obj in universe.objects)
            {
                Vector2 pos = Project(obj.pose.position);
                if (!rect.Contains(pos)) continue;
                if (Vector3.Dot(obj.pose.position, shipDir) > 0f)
                {
                    DrawDot(pos, 3f, new Color(1f, 1f, 1f, 0.55f));
                }
            }

            // ---- POI：残骸菱形（图标层；未物化或未到剪影层时）----
            if (iconLayer)
            {
                foreach (SpaceObject obj in universe.objects)
                {
                    if (obj is PoiState poi && poi.kind == PoiKind.SnapshotPocket
                        && Vector3.Dot(obj.pose.position, shipDir) > 0f)
                    {
                        // 已物化且进入剪影层：GUI 段画真实比例剪影，不再叠菱形。
                        if (silhouetteLayer && poi.map != null) continue;
                        Vector2 pos = Project(obj.pose.position);
                        if (!rect.ExpandedBy(8f).Contains(pos)) continue;
                        DrawDiamond(pos, 6f, new Color(1f, 0.85f, 0.55f, 0.95f));
                        RegisterClickable(new Rect(pos.x - 9f, pos.y - 9f, 18f, 18f), universe, obj, referenceDir);
                    }
                }
            }

            // ---- 飞船标记（底边内凹三角）----
            float focusedSize = Mathf.Lerp(9f, 14f, Zoom01);
            float otherSize = Mathf.Lerp(6.5f, 10f, Zoom01);
            foreach (ShipState ship in universe.Ships)
            {
                if (ship.docking) continue;
                bool focused = ReferenceEquals(ship, reference);
                Vector2 pos = Project(ship.pose.position);
                if (!rect.Contains(pos)) continue;
                bool front = Vector3.Dot(ship.pose.position, shipDir) > 0f;
                float alpha = front ? 1f : 0.35f;
                OrbitSphere.Basis(ship.pose.position, out Vector3 east, out Vector3 north);
                Vector2 heading = ScreenDir(east, north, ship.pose.angle, right, up);
                Color color;
                if (focused)
                {
                    color = new Color(1f, 0.95f, 0.4f, alpha);
                }
                else if (iconLayer)
                {
                    // 图标层起全部飞船可见：玩家船白色、其余按派系色（无派系红）。
                    Color baseColor = ship.isPlayerShip
                        ? new Color(1f, 1f, 1f, 0.85f)
                        : (ship.faction != null ? ship.faction.Color : new Color(1f, 0.4f, 0.35f));
                    color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha * (front ? 1f : 0.6f));
                }
                else if (ship.isPlayerShip)
                {
                    color = new Color(1f, 1f, 1f, alpha * 0.8f);
                }
                else
                {
                    continue; // 远档只显示玩家船
                }
                // 剪影层：物化飞船按真实比例（格→像素）与位姿角度画剪影，替代三角/圆点；
                // 点击命中区域也随剪影大小——对接前航向调整的主要依据。
                Texture2D silhouette = silhouetteLayer && front
                    ? GetSilhouette(ship.docked ? ship.dockedMap : ship.map)
                    : null;
                if (silhouette != null)
                {
                    float sizePx = Mathf.Max(silhouette.width * pixelsPerCell, 8f);
                    var silhouetteRect = new Rect(pos.x - sizePx * 0.5f, pos.y - sizePx * 0.5f, sizePx, sizePx);
                    Color tint = focused
                        ? new Color(1f, 0.95f, 0.4f)
                        : ship.isPlayerShip ? Color.white
                        : (ship.faction != null ? ship.faction.Color : new Color(1f, 0.4f, 0.35f));
                    Overlays.Add(new Overlay(silhouetteRect, silhouette,
                        SilhouetteScreenAngle(ship.pose.position, ship.pose.angle, right, up), tint));
                    RegisterClickable(silhouetteRect, universe, ship, referenceDir);
                }
                else if (ship.docked)
                {
                    DrawDot(pos, 3f, new Color(1f, 0.95f, 0.4f, front ? 0.8f : 0.25f));
                }
                else
                {
                    DrawCursorTriangle(pos, focused ? focusedSize : otherSize, heading, color);
                }
                // 速度矢量：聚焦船常驻；标注层起所有船。
                if (!ship.docked && ship.pose.velocity.sqrMagnitude > 0.01f && (focused || labelLayer))
                {
                    Vector2 velocity = ScreenDir(east, north,
                        Mathf.Atan2(ship.pose.velocity.y, ship.pose.velocity.x), right, up);
                    DrawArrow(pos + heading * (focused ? 12f : 10f), velocity, focused ? 26f : 14f,
                        new Color(0.5f, 1f, 0.6f, alpha), rect);
                }
                if (silhouette == null && iconLayer && front)
                {
                    RegisterClickable(new Rect(pos.x - 9f, pos.y - 9f, 18f, 18f), universe, ship, referenceDir);
                }
            }

            GL.PopMatrix();

            // ---- GUI 层：剪影覆盖、原版图标、标注、北极 N、tooltip 与点击 ----
            if (iconLayer)
            {
                foreach (SpaceObject obj in universe.objects)
                {
                    if (!(obj is PoiState poi)) continue;
                    if (Vector3.Dot(obj.pose.position, shipDir) <= 0f) continue;
                    // 已物化且进入剪影层：真实比例剪影替代图标（点击区域随剪影大小）。
                    Texture2D silhouette = silhouetteLayer && poi.map != null ? GetSilhouette(poi.map) : null;
                    if (silhouette != null)
                    {
                        Vector2 pos = Project(obj.pose.position);
                        float sizePx = Mathf.Max(silhouette.width * pixelsPerCell, 8f);
                        var silhouetteRect = new Rect(pos.x - sizePx * 0.5f, pos.y - sizePx * 0.5f, sizePx, sizePx);
                        if (!silhouetteRect.Overlaps(rect)) continue;
                        Overlays.Add(new Overlay(silhouetteRect, silhouette,
                            SilhouetteScreenAngle(obj.pose.position, poi.pose.angle, right, up),
                            new Color(1f, 0.85f, 0.55f)));
                        RegisterClickable(silhouetteRect, universe, poi, referenceDir);
                        continue;
                    }
                    if (poi.kind != PoiKind.NativeParent
                        || poi.nativeParent == null || poi.nativeParent.Destroyed) continue;
                    Vector2 iconPos = Project(obj.pose.position);
                    var iconRect = new Rect(iconPos.x - 9f, iconPos.y - 9f, 18f, 18f);
                    if (!iconRect.Overlaps(rect)) continue;
                    GUI.color = poi.nativeParent.ExpandingIconColor;
                    GUI.DrawTexture(iconRect, poi.nativeParent.ExpandingIcon);
                    GUI.color = Color.white;
                    RegisterClickable(iconRect, universe, poi, referenceDir);
                }
            }
            foreach (Overlay overlay in Overlays)
            {
                // DrawTextureRotated 正角逆时针（UI.RotateAroundPivot）；角度已按 GUI y 向下换算。
                GUI.color = overlay.tint;
                Widgets.DrawTextureRotated(overlay.rect, overlay.texture, overlay.angleDeg);
            }
            GUI.color = Color.white;
            if (labelLayer)
            {
                Text.Font = GameFont.Tiny;
                foreach (SpaceObject obj in universe.objects)
                {
                    if (obj is ShipState ship && ship.docking) continue;
                    if (Vector3.Dot(obj.pose.position, shipDir) <= 0f) continue;
                    Vector2 pos = Project(obj.pose.position);
                    float distance = OrbitSphere.ArcCells(referenceDir, obj.pose.position);
                    string text = $"{obj.label} · " + "DeltaV_PanelGlobeDistance".Translate(((int)distance).ToString("N0"));
                    var labelRect = new Rect(Mathf.Min(pos.x + 12f, rect.xMax - 130f), pos.y - 8f, 130f, 16f);
                    if (!labelRect.Overlaps(rect)) continue;
                    GUI.color = new Color(1f, 1f, 1f, 0.85f);
                    Widgets.Label(labelRect, text);
                    GUI.color = Color.white;
                }
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

            // 层级状态诊断（状态变化沿输出）：缩放/LOD/可点条目一旦异常，日志立刻能定位断点。
            string state = $"zoom={Zoom01:F1} viewHalf={viewHalfCells:F0} side={sideCells:F0}" +
                $" icon={iconLayer} sil={silhouetteLayer} label={labelLayer}" +
                $" hex={hexAlpha:F1} objs={universe.objects.Count} clickable={Clickables.Count}";
            if (state != lastStateLog)
            {
                lastStateLog = state;
                DeltaVLog.Message(DeltaVLogModule.Core, $"globe: {state}");
            }
        }

        private static string lastStateLog;

        // ---- 缩放交互 ----

        private static void HandleWheelZoom(Rect rect)
        {
            if (Event.current.type != EventType.ScrollWheel || !Mouse.IsOver(rect)) return;
            Zoom01 = Mathf.Clamp01(Zoom01 - Event.current.delta.y * 0.05f);
            Event.current.Use();
        }

        // ---- 点击聚焦 ----

        private static void RegisterClickable(Rect rect, UniverseManager universe, SpaceObject obj,
            Vector3 referenceDir)
        {
            float distance = OrbitSphere.ArcCells(referenceDir, obj.pose.position);
            Map map = obj is ShipState ship && ship.docked ? ship.dockedMap : obj.map;
            string tip = $"{obj.label}\n" + "DeltaV_PanelGlobeDistance".Translate(((int)distance).ToString("N0"));
            if (map == null)
            {
                tip += "\n" + "DeltaV_PanelObjectNotLoaded".Translate();
            }
            Clickables.Add(new Clickable(rect, obj, tip));
        }

        private static void FocusObject(UniverseManager universe, SpaceObject obj)
        {
            Map map = obj is ShipState ship && ship.docked ? ship.dockedMap : obj.map;
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
                $" frozen={SpaceLifecycle.IsFrozen(map)} rootSize={Find.CameraDriver?.RootSize:F0}");
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

        // ---- 剪影（物化对象；按 Substructure 地形，与世界视图剪影同快照语义）----

        private static Texture2D GetSilhouette(Map map)
        {
            if (map == null || map.Disposed) return null;
            if (SilhouetteCache.TryGetValue(map, out Texture2D cached)) return cached;
            if (SilhouetteEmpty.Contains(map)) return null;

            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                if (map.terrainGrid.TerrainAt(cell) != DeltaVDefOf.Substructure) continue;
                minX = Mathf.Min(minX, cell.x);
                maxX = Mathf.Max(maxX, cell.x);
                minZ = Mathf.Min(minZ, cell.z);
                maxZ = Mathf.Max(maxZ, cell.z);
            }
            if (minX > maxX)
            {
                // 无 Substructure（原版站点等）：没有可剪影的甲板，继续用图标。
                SilhouetteEmpty.Add(map);
                return null;
            }
            const int padding = 1;
            int side = Mathf.Max(maxX - minX, maxZ - minZ) + 1 + padding * 2;
            var pixels = new Color32[side * side];
            Color32 opaque = new Color32(255, 255, 255, 255);
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                if (map.terrainGrid.TerrainAt(cell) != DeltaVDefOf.Substructure) continue;
                int x = cell.x - minX + padding;
                // 垂直翻转：地图 +z 是北（屏幕向上），纹理 y 向下。
                int y = side - 1 - (cell.z - minZ + padding);
                pixels[x + y * side] = opaque;
            }
            var texture = new Texture2D(side, side, TextureFormat.RGBA32, false)
            {
                name = $"DeltaV_GlobeSilhouette_{map.Index}",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            SilhouetteCache[map] = texture;
            return texture;
        }

        /// <summary>聚焦船最长边（格）：优先剪影侧长，兜底 FallbackSideCells。缩放最深档与剪影层阈值都以它为单位。</summary>
        private static float SideCellsOf(ShipState reference)
        {
            if (reference == null) return FallbackSideCells;
            Map map = reference.docked ? reference.dockedMap : reference.map;
            Texture2D silhouette = GetSilhouette(map);
            return silhouette != null ? silhouette.width : FallbackSideCells;
        }

        /// <summary>
        /// 剪影旋转角（度）：forward 世界方向经 SpaceTransform.MarkerForwardWorld 导出
        /// （与世界视图剪影同一条旋转链，两处显示不分叉）；RotateAroundPivot 在 GUI
        /// （y 向下）中正角 = 视觉顺时针，故 φ = Atan2(+f.y, f.x)——此前按逆时针推导
        /// 符号写反，产生 2×朝向角的偏差（2026-09-17 实测约 45°）。
        /// </summary>
        private static float SilhouetteScreenAngle(Vector3 universeDir, float universeAngle,
            Vector3 right, Vector3 up)
        {
            Vector3 forwardWorld = SpaceTransform.MarkerForwardWorld(universeDir, universeAngle);
            Vector2 forwardScreen = new Vector2(
                Vector3.Dot(forwardWorld, right), -Vector3.Dot(forwardWorld, up));
            return Mathf.Atan2(forwardScreen.y, forwardScreen.x) * Mathf.Rad2Deg;
        }

        /// <summary>读档 / 场景重置时释放剪影缓存（按 Map 重扫自愈）。</summary>
        public static void ClearRuntimeCaches()
        {
            foreach (Texture2D texture in SilhouetteCache.Values)
            {
                if (texture != null) Object.Destroy(texture);
            }
            SilhouetteCache.Clear();
            SilhouetteEmpty.Clear();
            lastStateLog = null;
        }

        /// <summary>
        /// 地图内容变化后失效剪影（快照语义，显式失效点）：降落实置/起飞移除船体、
        /// 结构扩建等。下次取用按 Substructure 地形重扫。
        /// </summary>
        public static void InvalidateSilhouette(Map map)
        {
            if (map == null) return;
            if (SilhouetteCache.TryGetValue(map, out Texture2D texture) && texture != null)
            {
                Object.Destroy(texture);
            }
            SilhouetteCache.Remove(map);
            SilhouetteEmpty.Remove(map);
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

        private static void DrawDot(Vector2 c, float radiusPx, Color color)
        {
            GL.Begin(GL.TRIANGLES);
            GL.Color(color);
            const int segments = 8;
            for (int i = 0; i < segments; i++)
            {
                float a0 = i / (float)segments * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)segments * Mathf.PI * 2f;
                Vertex(c);
                Vertex(c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * radiusPx);
                Vertex(c + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radiusPx);
            }
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
