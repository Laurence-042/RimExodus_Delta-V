using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 固定轨道 / 建造模式（星区架构 §7 状态机）。
    ///
    /// 移动 → 固定（TryDock）：
    /// - 可降落判定 = 飞船中心位于某**无动力** SpaceObject 内部地图投影范围内；
    ///   有动力飞船不可降落（进入它走后续跳帮流程），原版站点/空画布/空间站/残骸可降落；
    /// - 深空固定被拒绝，不再凭空生成空画布；
    /// - 落点 = 飞船轨道位姿在该对象内部投影上的对应位置——只是原版降落选点的
    ///   **初始落点**，玩家仍经原版选点、合法性预览与确认调整；Delta-V 不另做裁决；
    /// - 确认后由 LandingApproachController 接管为进场自动驾驶：船回飞行口袋图、
    ///   复用原版 cutscene 级联夺取控制权、目标图正常聚焦渲染且非聚焦船壳自动
    ///   显示；到达后重放原版 InitiateLanding（动画压零）完成真实转移。
    ///
    /// 固定 → 移动（TryLaunch）：起飞后的轨道位姿 = 宿主 SpaceObject 位姿 ⊕ 起飞点
    /// 的内部坐标；口袋图上的起飞以 TileFromDirection(船 pose) 为战略源地址（§6）。
    /// 落点移动、四向旋转、合法性预览、确认 UI 与实际放置由原版完成；起飞无动画。
    /// </summary>
    public static class DockController
    {
        private static ShipState pendingShip;
        private static Gravship pendingGravship;
        private static ShipState landedShipAwaitingPocketCleanup;
        private static ShipState pendingMobileShip;
        private static Building_GravEngine pendingMobileEngine;
        private static Map pendingMobileSourceMap;
        private static IntVec3 pendingMobileSourceRoot;
        private static Vector2 pendingMobileOldOrigin;
        private static float pendingMobileHeading;
        private static Building_GravEngine directCaptureEngine;
        private static Building_GravEngine landingCaptureEngine;
        private static HashSet<IntVec3> landingCaptureCells;

        private static readonly FieldInfo ControllerMapHasGravAnchor =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "mapHasGravAnchor");

        private static readonly FieldInfo ControllerGravship =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "gravship");

        private static readonly FieldInfo ControllerTimeLeft =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "timeLeft");

        private static readonly FieldInfo ControllerTakeoffTile =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "takeoffTile");

        public static bool MobileLaunchPending => pendingMobileShip != null;

        /// <summary>
        /// 指定引擎当前是否有 Delta-V 转换专用捕获范围。移动→固定的源端同步
        /// GenerateGravship 读取整个相连域；船放入目标图后的二次画面捕获改读冻结的
        /// Gravship foundation，避免相邻 wreck 污染动画。固定→移动从 InitiateTakeoff
        /// 到原版捕获/移除完成始终读取整个相连域。
        /// </summary>
        public static bool TryGetConversionCaptureScope(Building_GravEngine engine,
            out HashSet<IntVec3> scope)
        {
            scope = null;
            if (engine == null) return false;
            if (ReferenceEquals(engine, landingCaptureEngine) && landingCaptureCells != null)
            {
                scope = landingCaptureCells;
                return true;
            }
            if (ReferenceEquals(engine, directCaptureEngine)
                || (pendingMobileShip != null && ReferenceEquals(engine, pendingMobileEngine)))
            {
                scope = engine.AllConnectedSubstructure;
                return true;
            }
            return false;
        }

        /// <summary>
        /// InitiateLanding Prefix 调用：降落放置前冻结本船 foundation（最终 Map 坐标），
        /// 供降落过场的二次画面/遮罩读取。战术降落与原版战略到达两条路径统一在此覆盖。
        /// </summary>
        internal static void SetLandingCaptureScope(Building_GravEngine engine, HashSet<IntVec3> cells)
        {
            landingCaptureEngine = engine;
            landingCaptureCells = cells;
        }

        public static float SnapAngle(float angle)
        {
            float quarter = Mathf.PI * 0.5f;
            return SpaceTransform.WrapAngle(Mathf.Round(angle / quarter) * quarter);
        }

        // ---- 移动 → 固定 ----

        public static void TryDock(UniverseManager universe, ShipState ship)
        {
            if (universe == null || ship == null || ship.docked || ship.docking || ship.map == null) return;
            if (Find.GravshipController == null || Find.GravshipController.LandingAreaConfirmationInProgress
                || Find.GravshipController.IsGravshipTravelling || Current.Game.Gravship != null)
            {
                Messages.Message("DeltaV_DockFailBusy".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (!OrbitSphere.Valid)
            {
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            Building_GravEngine engine = FindEngineForShip(ship.map, ship.primaryConsole);
            if (engine == null)
            {
                Messages.Message("DeltaV_DockFailNoEngine".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            SpaceObject target = FindLandableTarget(universe, ship, engine);
            if (target == null)
            {
                // 深空固定被拒绝（星区架构 §7）：不再凭空生成空画布；目标尚未物化时
                // 继续接近，进入 activation radius 加载后即可正常降落（软限制）。
                Messages.Message("DeltaV_DockFailNoTarget".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            float snappedHeading = SnapAngle(ShipOrientation.HeadingAngle(ship));
            BeginOriginalLanding(universe, ship, engine, target, target.map, snappedHeading);
        }

        /// <summary>
        /// 可降落目标扫描（2026-09-17 修订：投影重叠的 tie-breaker 从「渲染靠后优先」
        /// 改为「可见船壳最近优先」——渲染顺序只对船壳重叠正确；当目标 A 船壳小而地图大
        /// 时，玩家看得见下层 B 的船壳、却仍会落到 A，与所见不符）。资格门槛不变：
        /// 飞船中心在对象内部地图投影内；距离 = 双方可渲染船壳（凸包）的最近距离，
        /// 无船壳数据的目标记无穷大（全部无穷大时保留渲染顺序兜底）。
        /// </summary>
        private static SpaceObject FindLandableTarget(UniverseManager universe, ShipState ship,
            Building_GravEngine playerEngine)
        {
            SpaceObject best = null;
            float bestDistance = float.MaxValue;
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == ship || obj.map == null || !IsLandableObject(obj)) continue;
                if (!ShipCenterInsideProjection(obj, ship.pose.position)) continue;
                float distance = VisibleShellDistance(universe, ship, playerEngine, obj);
                if (distance <= bestDistance)
                {
                    best = obj;
                    bestDistance = distance;
                }
            }
            return best;
        }

        /// <summary>
        /// 玩家船壳与目标可渲染船壳的最近距离（目标切空间系中计算；数据流与外壳渲染
        /// 同源——船壳格列表 + 双方位姿，凸包后求距离）。船壳口径与渲染一致：
        /// 船 = 引擎相连基架（引擎损毁退化为 ShipConfig 可见内容扫描），
        /// POI = PoiConfig 可见内容格。
        /// </summary>
        private static float VisibleShellDistance(UniverseManager universe, ShipState ship,
            Building_GravEngine playerEngine, SpaceObject target)
        {
            HashSet<IntVec3> playerCells = playerEngine?.AllConnectedSubstructure;
            if (playerCells == null || playerCells.Count == 0 || ship.map == null) return float.MaxValue;
            Map targetMap;
            HashSet<IntVec3> targetCells = null;
            if (target is ShipState targetShip)
            {
                targetMap = targetShip.docked ? targetShip.dockedMap : targetShip.map;
                Building_GravEngine engine = targetMap == null ? null : FindEngineForShip(targetMap, null);
                targetCells = engine?.AllConnectedSubstructure;
            }
            else
            {
                targetMap = target.map;
            }
            if (targetMap == null || targetMap.Disposed) return float.MaxValue;
            // 统一坐标系：目标格子所在图的显示帧（口袋图 = 对象自身；停靠宿主图 = 宿主
            // 帧——停靠船的格子在宿主图坐标系里，不能投到它自己的位姿帧）。
            SpaceObject targetFrame = universe.FrameOf(targetMap);
            if (targetFrame == null) return float.MaxValue;

            // 目标系坐标：目标格子即本系局部坐标；玩家格子经 位姿 → universe → 目标系。
            var targetPoints = new List<Vector2>();
            if (targetCells != null)
            {
                foreach (IntVec3 cell in targetCells)
                {
                    targetPoints.Add(SpaceTransform.MapToShipLocal(targetMap,
                        new Vector2(cell.x + 0.5f, cell.z + 0.5f)));
                }
            }
            else
            {
                MapOverlayRenderConfig config = target is ShipState
                    ? ShellRenderer.ShipConfig : ShellRenderer.PoiConfig;
                foreach (IntVec3 cell in CellRect.WholeMap(targetMap))
                {
                    if (MapOverlayRenderer.ContainsVisibleContent(targetMap, cell, config))
                    {
                        targetPoints.Add(SpaceTransform.MapToShipLocal(targetMap,
                            new Vector2(cell.x + 0.5f, cell.z + 0.5f)));
                    }
                }
            }
            if (targetPoints.Count == 0) return float.MaxValue;

            var playerPoints = new List<Vector2>(playerCells.Count);
            foreach (IntVec3 cell in playerCells)
            {
                Vector2 local = SpaceTransform.MapToShipLocal(ship.map,
                    new Vector2(cell.x + 0.5f, cell.z + 0.5f));
                playerPoints.Add(SpaceTransform.UniverseToFocused(targetFrame,
                    SpaceTransform.ShipLocalToUniverse(ship.pose, local)));
            }
            return PlanarGeometry.HullDistance(
                PlanarGeometry.ConvexHull(playerPoints), PlanarGeometry.ConvexHull(targetPoints));
        }

        /// <summary>
        /// 无动力判定（星区架构 §7）：飞船类对象没有可工作推进器，或全部驾驶台被毁 →
        /// 视为无动力（无动力飞船等价于普通空间站；被打瘫的敌舰自动转为可降落目标）。
        /// 动态口径：经 ThrusterPower 读当前实际可用动力，不缓存静态标志。
        /// 非飞船对象（站点/空画布/空间站/残骸）天然无动力。
        /// </summary>
        private static bool IsLandableObject(SpaceObject obj)
        {
            if (obj is ShipState)
            {
                return !ThrusterPower.HasWorkingThruster(obj.map)
                    || !ThrusterPower.HasWorkingConsole(obj.map);
            }
            // 战术空画布降落退役（2026-09-17 用户决策）：空画布锚对象（真实 tile 的空白
            // 轨道图）不再作为可固定目标——降落到空无一物的地图属历史遗留场景。留有
            // 任何内容的画布按原版语义保留可降落；原版战略发射到达按需生成空画布锚
            // 的路径不受影响（原版 launch 需要落点图）。
            if (obj is PoiState poi && poi.nativeParent != null
                && SpaceMapGovernance.IsBlankCanvasAnchor(poi.nativeParent)
                && SpaceMapGovernance.IsBlankLandingCanvas(obj.map))
            {
                return false;
            }
            return true;
        }

        /// <summary>飞船中心（位姿锚 = 口袋图几何中心）是否落在对象内部地图投影范围内。</summary>
        private static bool ShipCenterInsideProjection(SpaceObject obj, Vector3 universeDir)
        {
            Map map = obj.map;
            Vector2 p = SpaceTransform.UniverseToMapPoint(obj, universeDir);
            return p.x >= 0f && p.x <= map.Size.x && p.y >= 0f && p.y <= map.Size.z;
        }

        private static void BeginOriginalLanding(UniverseManager universe, ShipState ship,
            Building_GravEngine engine, SpaceObject target, Map targetMap, float snappedHeading)
        {
            ship.preDockPose = ship.pose;
            ship.pocketRoot = engine.Position;
            ship.pose.velocity = Vector2.zero;
            ship.pose.angularVelocity = 0f;
            if (engine.launchInfo == null)
            {
                // 原版通常由发射仪式/对话创建；Delta-V 跳过起飞流程，但 LandingEnded 会读取它。
                engine.launchInfo = new LaunchInfo { quality = 1f, doNegativeOutcome = false };
            }

            pendingShip = ship;
            Gravship gravship;
            // GenerateGravship 是同步的；只让源端读取 AllConnectedSubstructure。目标图上
            // 的降落过场（curtain/遮罩）读取 InitiateLanding 冻结的 foundation 集合
            // （Patch_GravshipController_InitiateLanding_FreezeFoundation），避免吞入 wreck。
            directCaptureEngine = engine;
            try
            {
                gravship = GravshipUtility.GenerateGravship(engine);
            }
            finally
            {
                directCaptureEngine = null;
            }
            if (gravship == null)
            {
                pendingShip = null;
                Log.Error($"[DeltaV] Docking '{ship.label}': GenerateGravship returned null; aborting.");
                return;
            }
            Rot4 sourceFacing = gravship.PilotConsole?.Rotation ?? Rot4.East;
            // 选点发生在宿主图坐标系：初始建议朝向按宿主帧角换算（角 0 宿主上不变），
            // 与玩家当前看到的船壳姿态一致。
            SpaceObject landingHostFrame = universe.FrameOf(targetMap);
            Rot4 desiredFacing = ShipOrientation.Rot4FromAngle(SpaceTransform.WrapAngle(
                snappedHeading - (landingHostFrame != null ? landingHostFrame.pose.angle : 0f)));
            gravship.Rotation = ShipOrientation.PlacementRotation(sourceFacing, desiredFacing);
            // 口袋图目标（POI/无动力船内部）没有 tile：以船位姿所在星区为地址，
            // 保证选点期间的任何 world 查询与中途存档都落在合法 tile 上。
            PlanetTile addressTile = targetMap.Tile.Valid
                ? targetMap.Tile
                : OrbitSphere.TileFromDirection(ship.pose.position);
            gravship.Tile = addressTile;
            gravship.destinationTile = addressTile;

            pendingGravship = gravship;
            ship.docking = true;
            PilotController.Exit(universe);

            // 自持选点流程（原版 ArriveExistingMap 经 WorldObjects tile 查图，口袋图不通）：
            // 复刻其主体（锁 landingMap、构造 marker、进入确认状态），初始落点放到
            // 悬停位置在目标对象内部投影上的对应格（星区架构 §7）。designator 必须在
            // landingMap 仍非空时构造（其构造器缓存 map），随后置 null 隐藏原版 Abort——
            // AbortLanding 依赖 landingMap.Tile 做坠毁改道，口袋图没有可用 tile。
            Find.GravshipController.landingMap = targetMap;
            GravshipLandingMarker marker = ThingMaker.MakeThing(ThingDefOf.GravshipLandingMarker) as GravshipLandingMarker;
            marker.gravship = gravship;
            marker.CacheCells();
            Find.GravshipController.Notify_LandingAreaConfirmationStarted(marker);
            Designator_MoveGravship designator = Find.GravshipController.MoveDesignator();
            IntVec3 initialCell = InitialLandingCell(ship, target, targetMap, gravship, desiredFacing);
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                if (Prefs.PauseOnLoad)
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                }
                CameraJumper.TryJump(initialCell.IsValid ? initialCell : targetMap.Center, targetMap,
                    CameraJumper.MovementMode.Cut);
                Find.DesignatorManager.Select(designator);
                // 初始落点只是原版选点的起点（DesignateSingleCell 会放置 marker 并取消
                // 选中；屏幕下方按钮可重开选点）。已按脚印 clamp，但图比船小时仍可能
                // 不合法：只在原版门禁放行时自动放置，否则留给玩家手动选点，
                // 杜绝 marker 越界生成（2026-09-17 实测事故）。
                if (initialCell.IsValid && designator != null
                    && designator.CanDesignateCell(initialCell).Accepted)
                {
                    designator.DesignateSingleCell(initialCell);
                }
            });
            Find.GravshipController.landingMap = null;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: landing on '{target.label}' via vanilla selector; initial cell={initialCell}, heading={snappedHeading * Mathf.Rad2Deg:F0} deg");
        }

        /// <summary>
        /// 悬停位置 → 目标对象内部投影上的初始落点：飞船中心映射为目标图格子，再按
        /// gravship 尺寸/旋转折算 designator 锚点格（GetSizeRotAdjustedCell 与
        /// PrefabUtility.GetRoot 互逆，传锚点格等价于鼠标悬停该处点击）。
        /// 中心按脚印半跨度 clamp 保证整只脚印入界——DesignateSingleCell 会把锚点折算成
        /// root，大船投影点贴边时 root 可为负 → marker 越界生成；图比船小时 clamp 退化为
        /// 半图宽，由调用处的 CanDesignateCell 门禁兜底。
        /// </summary>
        private static IntVec3 InitialLandingCell(ShipState ship, SpaceObject target, Map targetMap,
            Gravship gravship, Rot4 facing)
        {
            Vector2 mapPoint = SpaceTransform.UniverseToMapPoint(target, ship.pose.position);
            // OccupiedRect = 中心 ± 半跨度（水平朝向时宽高互换）；留 2 格边距满足原版
            // InBounds(map, 1) 的禁建边检查。
            IntVec2 size = gravship.Bounds.Size;
            int spanX = facing.IsHorizontal ? size.z : size.x;
            int spanZ = facing.IsHorizontal ? size.x : size.z;
            int minX = Mathf.Min(spanX / 2 + 2, targetMap.Size.x / 2);
            int maxX = Mathf.Max(minX, targetMap.Size.x - spanX / 2 - 3);
            int minZ = Mathf.Min(spanZ / 2 + 2, targetMap.Size.z / 2);
            int maxZ = Mathf.Max(minZ, targetMap.Size.z - spanZ / 2 - 3);
            IntVec3 center = new IntVec3(
                Mathf.Clamp(Mathf.RoundToInt(mapPoint.x), minX, maxX), 0,
                Mathf.Clamp(Mathf.RoundToInt(mapPoint.y), minZ, maxZ));
            CellRect footprint = GenAdj.OccupiedRect(center, facing, gravship.Bounds.Size);
            return PrefabUtility.GetRoot(footprint, facing);
        }

        public static void RestorePendingAfterLoad(UniverseManager universe)
        {
            pendingShip = null;
            pendingGravship = null;
            landedShipAwaitingPocketCleanup = null;
            directCaptureEngine = null;
            landingCaptureEngine = null;
            landingCaptureCells = null;
            ClearPendingMobileLaunch();
            Gravship gravship = Current.Game?.Gravship;
            if (universe == null || gravship == null || Find.GravshipController == null) return;
            if (!Find.GravshipController.LandingAreaConfirmationInProgress)
            {
                // BeginLanding 之后保存：进场阶段原版禁存档，此分支只覆盖旧版本存档
                // （选点确认后、LandingEnded 前的历史中间态），保留旧图供
                // LandingEnded Postfix 再销毁。
                landedShipAwaitingPocketCleanup = universe.Ships
                    .FirstOrDefault(s => s.docked && s.dockedMap == Find.CurrentMap);
                return;
            }
            ShipState ship = universe.Ships.FirstOrDefault(s => s.docking);
            if (ship == null) return;
            pendingShip = ship;
            pendingGravship = gravship;
        }

        /// <summary>选点/进场流程中的战术降落船（BeginOriginalLanding 置位，WriteDockedState 清除）。</summary>
        internal static ShipState PendingDockingShip => pendingShip;

        /// <summary>
        /// 落点反向映射（进场目标与最终停靠写入共用同一公式，保证船壳收敛帧与真船落位
        /// 严格重合）：落点 cell/旋转 → 轨道层位姿 = 宿主 SpaceObject 位姿 ⊕ 落点局部
        /// （星区架构 §4 坐标桥）；角度 = 宿主帧角 ⊕ 最终放置朝向——ThingPlacements
        /// getter 用**当前** gravship.Rotation 合成最终朝向，调用时它必须是玩家确认的
        /// 落点旋转（详见 LandingApproachController.TryConsume 的时序约束）。
        /// </summary>
        internal static void ComputeLandingPose(ShipState ship, Gravship gravship, Map map,
            IntVec3 landingPos, Rot4 landingRot, out Vector3 direction, out float angle,
            out Vector2 dockedOrigin)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject hostFrame = universe?.FrameOf(map);
            if (hostFrame == null)
            {
                direction = ship.pose.position; // 宿主未纳管的防御分支：位姿不动
                angle = ship.pose.angle;
                dockedOrigin = new Vector2(landingPos.x, landingPos.z);
                return;
            }
            float hostAngle = hostFrame.pose.angle;
            Rot4 landedFacing = gravship.PilotConsole != null
                && gravship.ThingPlacements.TryGetValue(gravship.PilotConsole, out PositionData.Data data)
                ? data.rotation
                // 无主台防御：以当前航向（换算到宿主图坐标系）为落地朝向。
                : ShipOrientation.Rot4FromAngle(SpaceTransform.WrapAngle(
                    ShipOrientation.HeadingAngle(ship) - hostAngle));
            angle = SpaceTransform.WrapAngle(hostAngle + ShipOrientation.AngleFromRot4(landedFacing));

            Vector2 pocketCenterFromRoot = ship.map != null
                ? SpaceTransform.ShipLocalOrigin(ship.map) - new Vector2(ship.pocketRoot.x, ship.pocketRoot.z)
                : Vector2.zero;
            dockedOrigin = new Vector2(landingPos.x, landingPos.z)
                + SpaceTransform.RotatePlacementOffset(pocketCenterFromRoot, landingRot);
            direction = SpaceTransform.FocusedToUniverse(hostFrame,
                SpaceTransform.MapToShipLocal(map, dockedOrigin));
        }

        /// <summary>
        /// 战术降落的真转移状态写入（LandingApproachController 到达时调用）：docked 字段、
        /// 落点反算 universe 位姿（"落在投影的哪个位置"成为真正的宇宙位置变化，其他对象
        /// 绝对状态不受影响）、剪影失效与口袋图清理登记。
        /// </summary>
        internal static void WriteDockedState(ShipState ship, Gravship gravship, Map map,
            IntVec3 landingPos, Rot4 landingRot)
        {
            ship.dockedRotation = landingRot;
            ship.dockedMap = map;
            ComputeLandingPose(ship, gravship, map, landingPos, landingRot,
                out Vector3 direction, out float angle, out Vector2 dockedOrigin);
            ship.dockedOrigin = dockedOrigin;
            ship.pose.position = direction;
            ship.pose.angle = angle;
            ship.pose.velocity = Vector2.zero;
            ship.pose.angularVelocity = 0f;
            ship.docking = false;
            ship.docked = true;
            // 剪影快照失效：宿主图新增了停靠船体（星球仪按 Substructure 重扫）。
            PlanetGlobeRenderer.InvalidateSilhouette(map);
            HostColonistBarVisibility.NotifyDockingStateChanged();
            landedShipAwaitingPocketCleanup = ship;
            pendingShip = null;
            pendingGravship = null;
            Messages.Message("DeltaV_DockSuccess".Translate(ship.label), MessageTypeDefOf.PositiveEvent, false);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: docking confirmed; rot={landingRot}, heading={angle * Mathf.Rad2Deg:F0} deg");
        }

        /// <summary>原版 LandingEnded Postfix：动画与 Gravship 已彻底释放后销毁空口袋图。</summary>
        public static void NotifyVanillaLandingEnded()
        {
            landingCaptureEngine = null;
            landingCaptureCells = null;
            ShipState ship = landedShipAwaitingPocketCleanup;
            landedShipAwaitingPocketCleanup = null;
            DestroyDockedPocketMap(ship);
        }

        /// <summary>旧版本存档迁移：停靠船若仍引用空口袋图，在加载收尾后销毁。</summary>
        public static void CleanupObsoleteDockedPocketMaps(UniverseManager universe)
        {
            if (universe == null || Current.Game?.Gravship != null) return;
            List<ShipState> ships = universe.Ships
                .Where(s => s.docked && s.map != null && s.map != s.dockedMap)
                .ToList();
            if (ships.Count == 0) return;
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                foreach (ShipState ship in ships) DestroyDockedPocketMap(ship);
            });
        }

        private static void DestroyDockedPocketMap(ShipState ship)
        {
            Map pocketMap = ship?.map;
            if (ship == null || !ship.docked || pocketMap == null || pocketMap == ship.dockedMap) return;
            // 旧存档可能尚无 dockedOrigin；必须在 map 引用消失前从原口袋中心重建。
            _ = DockedOriginMapPoint(ship);
            if (Find.CurrentMap == pocketMap && ship.dockedMap != null)
            {
                Current.Game.CurrentMap = ship.dockedMap;
            }
            ShipPhysics.ClearCache();
            // 进场期间口袋图经 MapOverlayRenderer 绘制过船壳 section，销毁前释放缓存。
            MapOverlayRenderer.Release(pocketMap);
            PocketMapUtility.DestroyPocketMap(pocketMap);
            ship.map = null;
            ship.pocketRoot = IntVec3.Invalid;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: obsolete flight pocket map destroyed after landing");
        }

        // ---- 固定 → 移动 ----

        public static void TryLaunch(UniverseManager universe, ShipState ship)
        {
            if (universe == null || ship == null || !ship.docked) return;
            Map sourceMap = ship.dockedMap;
            if (sourceMap == null || !Find.Maps.Contains(sourceMap))
            {
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            Building_GravEngine engine = FindEngineForShip(sourceMap, ship.primaryConsole);
            if (engine == null)
            {
                Messages.Message("DeltaV_DockFailNoEngine".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            // 门禁与移动模式捕获同口径：控制台所属引擎的整个相连基架域。
            // 没有任何一台可工作推进器（ThrusterPower 实际状态）则拒绝进入移动模式。
            if (!ThrusterPower.HasWorkingThruster(sourceMap, engine.AllConnectedSubstructure))
            {
                Messages.Message("DeltaV_LaunchFailNoThruster".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (MobileLaunchPending || Find.GravshipController == null
                || Find.GravshipController.LandingAreaConfirmationInProgress
                || Find.GravshipController.IsGravshipTravelling || Current.Game.Gravship != null)
            {
                Messages.Message("DeltaV_DockFailBusy".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (engine.launchInfo == null)
            {
                engine.launchInfo = new LaunchInfo { quality = 1f, doNegativeOutcome = false };
            }
            Building console = ship.primaryConsole;
            pendingMobileShip = ship;
            pendingMobileEngine = engine;
            pendingMobileSourceMap = sourceMap;
            pendingMobileSourceRoot = engine.Position;
            pendingMobileOldOrigin = DockedOriginMapPoint(ship);
            // 起飞后的宇宙角 = 宿主帧角 ⊕ 主台在宿主图上的朝向（与 ComputeLandingPose
            // 的落点反向映射互逆，斜置口袋宿主上 dock/undock 姿态连续；角 0 宿主数值不变）。
            SpaceObject launchHostFrame = universe.FrameOf(sourceMap);
            pendingMobileHeading = console != null && console.Spawned
                ? SpaceTransform.WrapAngle((launchHostFrame != null ? launchHostFrame.pose.angle : 0f)
                    + ShipOrientation.AngleFromRot4(console.Rotation))
                : ship.pose.angle;

            // 固定 → 移动必须从原版起飞入口开始，完整复用捕获、PreSwapMap、
            // GenerateGravship、Map 清理和 TakeoffEnded。目标 tile 只是满足原版协议；
            // TravelTo Prefix 会在起飞收尾点把 Gravship 接入口袋图。口袋图宿主
            // （POI/无动力船内部）无 tile：战略源地址 = TileFromDirection(船 pose)（§6）。
            PilotController.Exit(universe);
            Find.GravshipController.InitiateTakeoff(engine, StrategicSourceAddress(universe, sourceMap));
            // 降落图是持久画布，不能让原版 TakeoffEnded 因没有 GravAnchor 而 AbandonMap；
            // 这只改变源图保留策略，不绕过任何船体搬运步骤。
            ControllerMapHasGravAnchor?.SetValue(Find.GravshipController, true);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: mobile conversion handed to vanilla InitiateTakeoff");
        }

        /// <summary>
        /// 口袋图起飞的战略源地址（星区架构 §6/§9）：口袋图无 tile，原版起飞管线中
        /// 一切以源图 tile 为输入的环节改接该地址。真实轨道图原样返回自身 tile。
        /// </summary>
        internal static PlanetTile StrategicSourceAddress(UniverseManager universe, Map map)
        {
            if (map != null && map.Tile.Valid) return map.Tile;
            if (!OrbitSphere.Valid) return PlanetTile.Invalid;
            ShipState ship = universe?.ShipOnMap(map)
                ?? universe?.Ships.FirstOrDefault(s => s.dockedMap == map);
            if (ship == null) return PlanetTile.Invalid;
            return OrbitSphere.TileFromDirection(ship.pose.position);
        }

        /// <summary>
        /// InitiateTakeoff Postfix 的执行体：源图与 Delta-V 相关时的原版战略起飞适配
        /// （星区架构 §6/§9）。本 mod 自己的 TryLaunch 流程（MobileLaunchPending）不经过这里。
        /// - 口袋图源：原版记录的 takeoffTile 无效（TakeoffEnded → TravelTo 会拿它做跨层
        ///   换算并登记进 WorldObjects），改写为战略源地址；同时保护口袋图不被原版
        ///   AbandonMap 抛弃（口袋 parent 不在原版语义内，去留由 Delta-V 决定）。
        /// - 源图承载的 ShipState 交还原版旅行： unregister 身份（再次进入移动模式时由
        ///   bootstrap 重建）；移动模式的自有口袋图若彻底为空则随后销毁。
        /// </summary>
        internal static void HandleVanillaTakeoffFromDeltaVSource(Building_GravEngine engine,
            WorldComponent_GravshipController controller)
        {
            if (engine?.Map == null || MobileLaunchPending) return;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            Map sourceMap = engine.Map;
            bool pocketSource = !sourceMap.Tile.Valid;
            if (pocketSource)
            {
                PlanetTile address = StrategicSourceAddress(universe, sourceMap);
                if (address.Valid)
                {
                    ControllerTakeoffTile?.SetValue(controller, address);
                }
                ControllerMapHasGravAnchor?.SetValue(controller, true);
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"vanilla strategic takeoff from pocket map: source address sector {address}, source map protected");
            }

            ShipState ship = universe?.ShipOnMap(sourceMap)
                ?? universe?.Ships.FirstOrDefault(s => s.docked && s.dockedMap == sourceMap);
            if (ship == null) return;
            bool wasMobileOwner = ReferenceEquals(universe?.ShipOnMap(sourceMap), ship);
            PilotController.Exit(universe);
            HostColonistBarVisibility.NotifyDockingStateChanged();
            universe.UnregisterObject(ship);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: handed to vanilla strategic travel; Delta-V identity released (re-bootstrap on arrival)");
            if (pocketSource && wasMobileOwner && SpaceMapGovernance.IsBlankLandingCanvas(sourceMap))
            {
                LongEventHandler.ExecuteWhenFinished(delegate
                {
                    if (Find.Maps.Contains(sourceMap) && sourceMap != Find.CurrentMap
                        && SpaceMapGovernance.IsBlankLandingCanvas(sourceMap)
                        && universe.SpaceObjectOnMap(sourceMap) == null)
                    {
                        PocketMapUtility.DestroyPocketMap(sourceMap);
                        DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                            "ownerless emptied flight pocket map removed after vanilla takeoff");
                    }
                });
            }
        }

        /// <summary>原版捕获完成后跳过可见起飞动画，但仍让 TakeoffEnded 执行全部收尾。</summary>
        public static void SuppressMobileLaunchAnimation()
        {
            if (!MobileLaunchPending) return;
            ForceCutsceneTimeZero(Find.GravshipController);
        }

        /// <summary>把原版 cutscene 时钟压零（移动模式起飞 / 战术降落到达重放共用）。</summary>
        internal static void ForceCutsceneTimeZero(WorldComponent_GravshipController controller)
        {
            if (controller == null || ControllerTimeLeft == null) return;
            ControllerTimeLeft.SetValue(controller, 0f);
        }

        /// <summary>兜底：若 BeginCutscene 发生在其他更新边界，下一帧仍强制完成。</summary>
        public static void BeforeGravshipControllerUpdate(WorldComponent_GravshipController controller)
        {
            if (!MobileLaunchPending || !controller.IsGravshipTravelling) return;
            ForceCutsceneTimeZero(controller);
        }

        /// <summary>到达重放的源端再捕获：与 BeginOriginalLanding 同口径读取整个相连基架域。</summary>
        internal static Gravship CaptureShipForArrival(ShipState ship)
        {
            Building_GravEngine engine = FindEngineForShip(ship.map, ship.primaryConsole);
            if (engine == null) return null;
            directCaptureEngine = engine;
            try
            {
                return GravshipUtility.GenerateGravship(engine);
            }
            finally
            {
                directCaptureEngine = null;
            }
        }

        /// <summary>
        /// 原版 TakeoffEnded 调用 TravelTo 的扩展点：船已完成原生捕获/移除与源图收尾，
        /// 此处只把世界旅行目的地改为独占口袋图。返回 true 表示已消费该次 TravelTo。
        /// </summary>
        public static bool TryCompleteMobileLaunch(Gravship gravship)
        {
            ShipState ship = pendingMobileShip;
            Building_GravEngine engine = pendingMobileEngine;
            Map sourceMap = pendingMobileSourceMap;
            if (ship == null || engine == null || sourceMap == null
                || gravship == null || gravship.Engine != engine) return false;

            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null)
            {
                Log.Error("[DeltaV] Mobile launch reached TravelTo without UniverseManager; vanilla travel retained.");
                ClearPendingMobileLaunch();
                return false;
            }

            // GenerateGravship 的电力注销是 PowerNetManager 延迟动作。原版十秒动画期间
            // 会自然结算；我们把动画压到零，因此在新图 Spawn/PowerBuildings 前显式走
            // 同一个原生结算入口，避免旧 PowerNet 上重复 RegisterConnector。
            sourceMap.powerNetManager.UpdatePowerNetsAndConnections_First();

            // 起飞后的轨道位姿 = 宿主 SpaceObject 位姿 ⊕ 起飞点的内部坐标（星区架构 §7）：
            // FrameOf 对停靠图解析为宿主对象（worldtile 宿主 = tile 中心切空间帧的特例，
            // 与旧式 "船位姿 + 地图坐标差" 数值等价），对任意角度的口袋宿主（残骸、
            // 空间站内部）同样成立。固定模式期间允许非对称扩建——新口袋图以当前船体
            // 中心为局部原点，起飞后每个建筑的绝对位置保持不变。
            Vector2 sourceStructureCenter = new Vector2(
                pendingMobileSourceRoot.x + gravship.engineToCenter.x,
                pendingMobileSourceRoot.z + gravship.engineToCenter.z);
            SpaceObject hostFrame = universe.FrameOf(sourceMap);
            Vector3 launchDir = hostFrame != null
                ? SpaceTransform.FocusedToUniverse(hostFrame, SpaceTransform.MapToShipLocal(sourceMap, sourceStructureCenter))
                : OrbitSphere.Move(ship.pose.position, sourceStructureCenter - pendingMobileOldOrigin);

            // 主台在口袋图规范到 East（局部 +X），避免 180° 往返时将降落旋转重复计入。
            gravship.Rotation = gravship.PilotConsole != null
                ? ShipOrientation.PlacementRotation(gravship.PilotConsole.Rotation, Rot4.East)
                : new Rot4(-ship.dockedRotation.AsInt);
            if (!ShipPocketMapFactory.TryGenerateForGravship(gravship, universe,
                out Map pocketMap, out IntVec3 pocketRoot))
            {
                // 新容器生成失败时不能吞船：用捕获时的 North 布局原样放回停靠图。
                gravship.Rotation = Rot4.North;
                PlaceIntoMap(gravship, pendingMobileSourceRoot, sourceMap);
                FinishConsumedVanillaTravel();
                ClearPendingMobileLaunch();
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                Log.Error($"[DeltaV] Launching '{ship.label}': failed to generate replacement pocket map; restored ship to docked map.");
                return true;
            }

            ship.map = pocketMap;
            ship.pocketRoot = pocketRoot;
            PlaceIntoMap(gravship, pocketRoot, pocketMap);
            Current.Game.Gravship = null;

            ship.pose.position = launchDir;
            ship.pose.velocity = Vector2.zero;
            ship.pose.angularVelocity = 0f;
            ship.pose.angle = SpaceTransform.WrapAngle(pendingMobileHeading);
            ship.docked = false;
            ship.docking = false;
            ship.dockedMap = null;
            ship.dockedOrigin = Vector2.zero;
            // 世界视图标记是“进入移动模式时的船体快照”，不追踪后续战损/建造变化。
            WorldShipMarkerRenderer.RebuildFor(ship);
            // 剪影快照失效：源图（宿主）已移除船体、新口袋图是全新键——宿主侧不得
            // 残留已起飞飞船的剪影（2026-09-17 实测事故）。
            PlanetGlobeRenderer.InvalidateSilhouette(sourceMap);
            HostColonistBarVisibility.NotifyDockingStateChanged();
            universe.SetFocused(ship);
            CameraJumper.TryJump(ship.pocketRoot, pocketMap, CameraJumper.MovementMode.Cut);
            FinishConsumedVanillaTravel();
            ClearPendingMobileLaunch();
            QueueEmptyDepartedMapCleanup(universe, sourceMap);
            Messages.Message("DeltaV_MobileSuccess".Translate(ship.label), MessageTypeDefOf.PositiveEvent, false);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label} entered mobile mode without takeoff cutscene; heading={ship.pose.angle * Mathf.Rad2Deg:F0} deg");
            return true;
        }

        /// <summary>
        /// 延后到帧边界清点离港地图：此时已完全脱离原版 TakeoffEnded 调用栈，
        /// DeinitAndRemoveMap 不会踩到上层仍在使用的引用。
        /// </summary>
        private static void QueueEmptyDepartedMapCleanup(UniverseManager universe, Map map)
        {
            if (map == null) return;
            LongEventHandler.ExecuteWhenFinished(delegate { TryDeleteEmptyDepartedMap(universe, map); });
        }

        /// <summary>
        /// 离港后的空降落图清理（星区架构 §5 保留「空降落图离港清理」）：空画布锚对象
        /// （Space def / DeltaV_SpaceHostParent）的图若已彻底为空（无人、无建筑/物品、
        /// 无其他停靠船，只剩 Filth/Gas/Mote 残迹）则直接删除并销毁锚对象——下一次次
        /// 到达按需重生成。其他治理对象（留下真实内容的站点）与非纳管地图照旧判定；
        /// 有任何遗留内容一律保留（原版持久语义）。口袋图宿主（POI 内部）同理：彻底
        /// 为空则移除整个对象与图，有内容交给 SpaceLifecycle 超距快照收纳。
        /// </summary>
        internal static void TryDeleteEmptyDepartedMap(UniverseManager universe, Map map)
        {
            if (universe == null || map == null || map.Disposed || !Find.Maps.Contains(map)) return;
            if (map == Find.CurrentMap) return; // 聚焦/降落选点中的图绝不删
            if (SpaceLifecycle.IsUniverseHost(map)) return;
            if (map.mapPawns.AllPawnsSpawned.Any()) return;
            if (universe.Ships.Any(s => s.dockedMap == map)) return;
            if (!SpaceMapGovernance.IsBlankLandingCanvas(map)) return;

            if (map.Tile.Valid && map.Tile.LayerDef.isSpace)
            {
                MapParent parent = map.Parent;
                if (SpaceMapGovernance.IsGoverned(map, universe)
                    && !SpaceMapGovernance.IsBlankCanvasAnchor(parent))
                {
                    return; // 有真实身份的治理站点：持久语义
                }
                string label = parent?.Label ?? map.ToString();
                Current.Game.DeinitAndRemoveMap(map, false);
                parent?.Destroy();
                ShipPhysics.ClearCache();
                DeltaVLog.Message(DeltaVLogModule.Lifecycle, $"{label}: empty departed landing map removed");
                return;
            }
            SpaceObject owner = universe.SpaceObjectOnMap(map);
            if (owner is PoiState poi)
            {
                MapDormancyService.Forget(map);
                MapOverlayRenderer.Release(map);
                PocketMapUtility.DestroyPocketMap(map);
                universe.UnregisterObject(poi);
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{poi.label}: emptied pocket interior removed with its object");
            }
        }

        private static void FinishConsumedVanillaTravel()
        {
            Current.Game.Gravship = null;
            ControllerGravship?.SetValue(Find.GravshipController, null);
        }

        private static void ClearPendingMobileLaunch()
        {
            pendingMobileShip = null;
            pendingMobileEngine = null;
            pendingMobileSourceMap = null;
            pendingMobileSourceRoot = IntVec3.Invalid;
            pendingMobileOldOrigin = Vector2.zero;
            pendingMobileHeading = 0f;
        }

        /// <summary>停靠图中代表该船口袋图几何中心的 Map 坐标；旧存档缺字段时可重建。</summary>
        public static Vector2 DockedOriginMapPoint(ShipState ship)
        {
            if (ship?.dockedMap == null) return Vector2.zero;
            if (ship.dockedOrigin != Vector2.zero) return ship.dockedOrigin;
            Building_GravEngine engine = FindEngineForShip(ship.dockedMap, ship.primaryConsole);
            if (engine == null) return SpaceTransform.ShipLocalOrigin(ship.dockedMap);
            if (ship.map != null && ship.pocketRoot.IsValid)
            {
                Vector2 pocketCenterFromRoot = SpaceTransform.ShipLocalOrigin(ship.map)
                    - new Vector2(ship.pocketRoot.x, ship.pocketRoot.z);
                ship.dockedOrigin = new Vector2(engine.Position.x, engine.Position.z)
                    + SpaceTransform.RotatePlacementOffset(pocketCenterFromRoot, ship.dockedRotation);
            }
            else
            {
                // 极旧存档的最后兜底：以当前有效船体的几何中心作为局部原点。
                Vector3 center = CellRect.FromCellList(engine.ValidSubstructure).CenterVector3;
                ship.dockedOrigin = new Vector2(center.x, center.z);
            }
            return ship.dockedOrigin;
        }

        internal static Building_GravEngine FindEngineForShip(Map map, Building primaryConsole)
        {
            // 原版 facility comp 保留其实际引擎引用；停靠到另一艘带引擎的船旁时，
            // 不能只靠共享基架连通性扫描，否则两台引擎都会“包含”主驾驶台。
            CompPilotConsole consoleComp = primaryConsole?.TryGetComp<CompPilotConsole>();
            if (consoleComp?.engine != null && consoleComp.engine.Spawned && consoleComp.engine.Map == map)
            {
                return consoleComp.engine;
            }
            Building_GravEngine fallback = null;
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.GravEngine))
            {
                var engine = thing as Building_GravEngine;
                if (engine == null) continue;
                if (fallback == null) fallback = engine;
                // 归属圈定用相连基架连通域（与转换捕获同口径）：超出引擎力场的
                // 驾驶台/结构仍属于该船，只要基架相连。
                if (primaryConsole != null && primaryConsole.Spawned && primaryConsole.Map == map
                    && engine.AllConnectedSubstructure.Contains(primaryConsole.Position)) return engine;
            }
            return fallback;
        }

        /// <summary>
        /// 判断宿主图上的 Thing 是否属于指定停靠船。停靠后 ship.map 已销毁，不能再靠
        /// ShipOnMap 反查；引擎的相连基架连通域是此时实际船体归属的权威集合
        /// （与转换捕获同口径，超出力场的相连结构同样计入）。
        /// </summary>
        internal static bool ContainsDockedThing(ShipState ship, Thing thing)
        {
            if (ship == null || thing == null || !ship.docked || ship.dockedMap == null
                || thing.MapHeld != ship.dockedMap)
            {
                return false;
            }
            Building_GravEngine engine = FindEngineForShip(ship.dockedMap, ship.primaryConsole);
            return engine != null && engine.AllConnectedSubstructure.Contains(thing.PositionHeld);
        }

        /// <summary>把捕获好的 Gravship 放回指定地图（真实放置 + 原版收尾钩子）；
        /// 战术降落进场恢复（North 布局 + pocketRoot）与起飞失败回退共用。</summary>
        internal static void PlaceIntoMap(Gravship gravship, IntVec3 root, Map map)
        {
            GravshipPlacementUtility.PlaceGravshipInMap(gravship, root, map, out List<Thing> spawned);
            GravshipPlacementUtility.ApplyTemperatureVacuumFromBase(gravship, root, map);
            map.listerFilthInHomeArea.RebuildAll();
            map.resourceCounter.UpdateResourceCounts();
            map.wealthWatcher.ForceRecount(allowDuringInit: true);
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            foreach (Thing thing in spawned)
            {
                if (thing != null && !thing.Destroyed) thing.PostSwapMap();
            }
            foreach (Pawn pawn in gravship.Pawns)
            {
                if (pawn != null && !pawn.Destroyed) pawn.PostSwapMap();
            }
        }
    }
}
