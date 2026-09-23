using System;
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
    /// 自由飞行 / 已停靠转换（星区架构 §7）。全部
    /// 可变流程状态收进 <see cref="ShipFlightTransitionService"/>，由 UniverseManager 以实例持有。
    /// 「读其他档残留旧流程」不可表示；方法一律经 universe 参数读写。
    ///
    /// 自由飞行 → 已停靠（BeginDocking）：
    /// - 可对接判定 = 飞船中心位于某 SpaceObject 内部地图投影范围内（任何已物化对象，
    ///   含移动中的有动力飞船——速度匹配交给随动自动驾驶）；空白空画布不可固定；
    /// - 深空固定被拒绝，不再凭空生成空画布；
    /// - 落点 = 飞船轨道位姿在该对象内部投影上的对应位置——只是原版降落选点的
    ///   **初始落点**，玩家仍经原版选点、合法性预览与确认调整；Delta-V 不另做裁决；
    /// - 选点阶段只做不搬运的 Gravship 数据快照（船体/Substructure 全程留在口袋图）；
    ///   确认后由 LandingApproachController 建立对接计划：聚焦切回自己船、幽灵船
    ///   位置提示、（可选）随动自动驾驶接近；与预定落点位姿重合保持可配置 tick 数
    ///   即完成（手操打断自动驾驶不取消计划），走 FinalizeShipCapture + 重放原版
    ///   InitiateLanding（动画压零）完成真实转移。
    ///
    /// 已停靠 → 自由飞行（BeginFreeFlight）：脱离后的轨道位姿 = 宿主 SpaceObject 位姿 ⊕ 脱离点
    /// 的内部坐标；口袋图上的起飞以 TileFromDirection(船 pose) 为战略源地址（§6）。
    /// 落点移动、四向旋转、合法性预览、确认 UI 与实际放置由原版完成；起飞无动画。
    /// </summary>
    internal sealed partial class ShipFlightTransitionService
    {
        /// <summary>
        private static readonly FieldInfo ControllerMapHasGravAnchor =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "mapHasGravAnchor");

        private static readonly FieldInfo ControllerGravship =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "gravship");

        private static readonly FieldInfo ControllerTakeoffTile =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "takeoffTile");

        public bool FreeFlightTransitionPending()
        {
            return pendingFreeFlightShip != null;
        }

        /// <summary>
        /// 指定引擎当前是否有 Delta-V 转换专用捕获范围。移动→固定的源端同步
        /// GenerateGravship 读取整个相连域；船放入目标图后的二次画面捕获改读冻结的
        /// Gravship foundation，避免相邻 wreck 污染动画。固定→移动从 InitiateTakeoff
        /// 到原版捕获/移除完成始终读取整个相连域。自由飞行口袋图上的原版战略发射
        /// （2026-09-19 放开）同样整船捕获——按原版力场脚印会截断超出力场的船体
        /// （自由飞行允许扩建到力场外）；已停靠且 map=null 的船不命中，
        /// 仍为力场内口径。
        /// </summary>
        public bool TryGetConversionCaptureScope(Building_GravEngine engine,
            out HashSet<IntVec3> scope)
        {
            scope = null;
            if (engine == null || engine.Map == null) return false;
            if (ReferenceEquals(engine, landingCaptureEngine) && landingCaptureCells != null)
            {
                scope = landingCaptureCells;
                return true;
            }
            if (ReferenceEquals(engine, directCaptureEngine)
                || (pendingFreeFlightShip != null && ReferenceEquals(engine, pendingFreeFlightEngine)))
            {
                scope = engine.AllConnectedSubstructure;
                return true;
            }
            if (!engine.Map.Tile.Valid && universe.ShipOnMap(engine.Map) != null)
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
        internal void SetLandingCaptureScope(Map map,
            Building_GravEngine engine, HashSet<IntVec3> cells)
        {
            landingCaptureEngine = engine;
            landingCaptureMap = map;
            landingCaptureCells = cells;
        }

        public float SnapAngle(float angle)
        {
            float quarter = Mathf.PI * 0.5f;
            return SpaceTransform.WrapAngle(Mathf.Round(angle / quarter) * quarter);
        }

        // ---- 移动 → 固定 ----

        public void BeginDocking(ShipState ship)
        {
            if (universe == null || ship == null || ship.IsDocked
                || IsDockingFlowShip(ship) || ship.map == null) return;
            if (Find.GravshipController == null || Find.GravshipController.LandingAreaConfirmationInProgress
                || Find.GravshipController.IsGravshipTravelling || Current.Game.Gravship != null
                || universe.ShipTransitions.pendingShip != null)
            {
                Messages.Message("DeltaV_DockFailBusy".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (!OrbitSphere.Valid)
            {
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            Building_GravEngine engine = ShipStructureResolver.EngineOf(ship);
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
        private SpaceObject FindLandableTarget(UniverseManager universe, ShipState ship,
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
        /// 船 = 引擎相连基架（引擎损毁退化为统一结构外观扫描），
        /// POI = 统一结构外观可见内容格。
        /// </summary>
        private float VisibleShellDistance(UniverseManager universe, ShipState ship,
            Building_GravEngine playerEngine, SpaceObject target)
        {
            HashSet<IntVec3> playerCells = playerEngine?.AllConnectedSubstructure;
            if (playerCells == null || playerCells.Count == 0 || ship.map == null) return float.MaxValue;
            Map targetMap;
            HashSet<IntVec3> targetCells = null;
            if (target is ShipState targetShip)
            {
                targetMap = targetShip.InteriorMap;
                Building_GravEngine engine = targetMap == null ? null : ShipStructureResolver.EngineOn(targetMap, null);
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
                foreach (IntVec3 cell in CellRect.WholeMap(targetMap))
                {
                    if (MapOverlayRenderer.ContainsVisibleContent(targetMap, cell))
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
        /// 可对接目标资格（2026-09-21 阶段4 修订）：**任何已物化对象均可**——有动力
        /// 飞船同样可对接（随动自动驾驶负责速度/角速度匹配，完成判据为与预定落点位姿
        /// 重合保持可配置 tick 数；未来敌舰 AI 机动即天然反对接手段）。旧「无动力才可
        /// 降落、有动力走跳帮弹药流程」口径退役（跳帮舱仍是不入舱全船的轻型投放途径）；
        /// 打瘫敌舰不再是可降落的前置条件，但仍是消除其机动的战术手段。
        /// 空白空画布保留不可固定（2026-09-17 用户决策）：降落到空无一物的地图属历史
        /// 遗留场景；留有内容的画布按原版语义保留可降落，原版战略发射到达按需生成
        /// 空画布锚的路径不受影响。
        /// </summary>
        private bool IsLandableObject(SpaceObject obj)
        {
            if (obj is PoiState poi && poi.nativeParent != null
                && SpaceMapGovernance.IsBlankCanvasAnchor(poi.nativeParent)
                && SpaceMapGovernance.IsBlankLandingCanvas(obj.map))
            {
                return false;
            }
            return true;
        }

        /// <summary>飞船中心（位姿锚 = 口袋图几何中心）是否落在对象内部地图投影范围内。</summary>
        private bool ShipCenterInsideProjection(SpaceObject obj, Vector3 universeDir)
        {
            Map map = obj.map;
            Vector2 p = SpaceTransform.UniverseToMapPoint(obj, map, universeDir);
            return p.x >= 0f && p.x <= map.Size.x && p.y >= 0f && p.y <= map.Size.z;
        }

        private void BeginOriginalLanding(UniverseManager universe, ShipState ship,
            Building_GravEngine engine, SpaceObject target, Map targetMap, float snappedHeading)
        {
            ship.preDockPose = ship.pose;
            ship.pocketRoot = engine.Position;
            if (engine.launchInfo == null)
            {
                // 原版通常由发射仪式/对话创建；Delta-V 跳过起飞流程，但 LandingEnded 会读取它。
                engine.launchInfo = new LaunchInfo { quality = 1f, doNegativeOutcome = false };
            }

            universe.ShipTransitions.pendingShip = ship;
            universe.ShipTransitions.pendingSelectionWasManual =
                universe.Pilot.GetControlState(ship) == ShipControlState.Manual;
            // 选点零消费（2026-09-21 阶段4 定案）：预览用空洞 Gravship——只读填充原版
            // 选点/合法性/禁触链路所需字段；Gravship(engine) 的消费式冻结（zone 删除、
            // 地板指令收缴、区域限制清算、储物组解散、绑架好感结算）一概不发生。
            // 真实冻结唯一发生在完成时刻（CaptureShipForCompletion），与移除重建同一
            // 调用栈——对接期间的建造/扩建全部随完成时捕获带走。
            Gravship gravship = GravshipInterop.BuildPreview(ship, engine);
            if (gravship == null)
            {
                universe.ShipTransitions.pendingShip = null;
                universe.ShipTransitions.pendingSelectionWasManual = false;
                Log.Error($"[DeltaV] Docking '{ship.label}': preview gravship build failed; aborting.");
                return;
            }
            Rot4 sourceFacing = gravship.PilotConsole?.Rotation ?? Rot4.East;
            // 选点发生在宿主图坐标系：初始建议朝向按宿主帧角换算（角 0 宿主上不变），
            // 与玩家当前看到的船壳姿态一致。
            SpaceObject landingHostFrame = universe.FrameOf(targetMap);
            Rot4 desiredFacing = ShipOrientation.Rot4FromAngle(SpaceTransform.WrapAngle(
                snappedHeading - (landingHostFrame != null ? landingHostFrame.pose.angle : 0f)));
            gravship.Rotation = ShipOrientation.PlacementRotation(sourceFacing, desiredFacing);
            // 口袋图目标（POI/飞船内部）没有 tile：以船位姿所在星区为地址，保证选点
            // 期间任何 world 查询落在合法 tile 上。
            PlanetTile addressTile = targetMap.Tile.Valid
                ? targetMap.Tile
                : OrbitSphere.TileFromDirection(ship.pose.position);
            gravship.Tile = addressTile;
            gravship.destinationTile = addressTile;

            universe.Pilot.Exit(universe);

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
                CameraJumper.TryJump(initialCell.IsValid ? initialCell : targetMap.Center, targetMap,
                    CameraJumper.MovementMode.Cut);
                Find.DesignatorManager.Select(designator);
                // 初始落点只是原版选点的起点（DesignateSingleCell 会放置 marker 并取消
                // 选中；屏幕下方按钮可重开选点）。已按脚印 clamp，但图比船小时仍可能
                // 不合法：只在原版门禁放行时自动放置，否则留给玩家手动选点，
                // 杜绝 marker 越界生成（2026-09-17 实测事故）。
                // 禁触规则下悬停投影常落在 wreck 甲板上：先沿螺旋外扩找最近的合法落点
                //（与 wreck 保持基架间隔的空位），找不到再交给玩家手动选点。
                IntVec3 cell = initialCell;
                if (cell.IsValid && designator != null)
                {
                    if (!designator.CanDesignateCell(cell).Accepted)
                    {
                        cell = SpiralSearchValidLandingCell(designator, cell, targetMap);
                    }
                    if (cell.IsValid && designator.CanDesignateCell(cell).Accepted)
                    {
                        designator.DesignateSingleCell(cell);
                    }
                }
            });
            Find.GravshipController.landingMap = null;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: docking site selection on '{target.label}' (zero-consumption preview); initial cell={initialCell}, heading={snappedHeading * Mathf.Rad2Deg:F0} deg");
        }

        // ---- 空洞预览 Gravship（选点零消费）----
        // 原版选点链路对 Gravship 只读：Foundations（脚印）、Bounds.Size（锚格换算）、
        // ThrusterPlacements（排气区预览）、Rotation、Engine（禁触归属判定）、
        // PilotConsole/ThingPlacements（落点朝向换算）。私有无参构造器只初始化空容器，
        // MakeWorldObject(def, obj) 不做深拷贝（仅设置 def/ID/PostMake），反射填充全部
        // 为只读操作——严禁改回 Gravship(engine)（选点期消费 zone/区域数据）。

        /// <summary>
        /// 选点/合法性复核共用的零消费预览：脚印取引擎相连基架连通域（捕获同口径），
        /// 字段只读填充。选点期与完成期复核各建一次（复核要读**当前**脚印——对接期间
        /// 的扩建会改变它）。
        /// </summary>
        /// <summary>
        /// 完成时刻的真实冻结（**唯一消费点**，定案流程第 7 步）：等价原版起飞的
        /// Gravship(engine) 捕获——zone/地板指令/区域限制/储物组在此刻收缴进清单，
        /// 随即由 FinalizeShipCapture 移除、重放 InitiateLanding 重建（同一调用栈，
        /// 零陈旧窗口；对接期间的建造/扩建/zone 变更全部随本次捕获带走）。
        /// </summary>
        internal Gravship CaptureShipForCompletion(UniverseManager universe, Building_GravEngine engine)
        {
            if (engine == null || !engine.Spawned || engine.Map == null) return null;
            universe.ShipTransitions.directCaptureEngine = engine;
            GravshipUtility.generatingGravship = true;
            try
            {
                Gravship template = new Gravship(engine);
                Gravship gravship = WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Gravship, template) as Gravship;
                Current.Game.Gravship = gravship;
                return gravship;
            }
            finally
            {
                GravshipUtility.generatingGravship = false;
                universe.ShipTransitions.directCaptureEngine = null;
            }
        }

        /// <summary>
        /// 悬停位置 → 目标对象内部投影上的初始落点：飞船中心映射为目标图格子，再按
        /// gravship 尺寸/旋转折算放置 root，再编码成 designator 输入格。
        /// 中心按脚印半跨度 clamp 保证整只脚印入界——DesignateSingleCell 会把锚点折算成
        /// root，大船投影点贴边时 root 可为负 → marker 越界生成；图比船小时 clamp 退化为
        /// 半图宽，由调用处的 CanDesignateCell 门禁兜底。
        /// </summary>
        private IntVec3 InitialLandingCell(ShipState ship, SpaceObject target, Map targetMap,
            Gravship gravship, Rot4 facing)
        {
            Vector2 mapPoint = SpaceTransform.UniverseToMapPoint(target, targetMap, ship.pose.position);
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
            return GravshipInterop.CenterToDesignatorInput(center, gravship.Bounds.Size, facing);
        }

        /// <summary>
        /// 落点合法性（含 Delta-V 禁触规则）失败时的环形外扩搜索：以投影落点为中心，
        /// 沿逐步扩大的方形环采样（步长 2 控制探测成本，上限 30 格），返回首个合法
        /// 落点（悬停在 wreck 正上方时退到旁边的空位）。找不到返回 Invalid（交回玩家
        /// 手动选点）。每次探测经原版 CanDesignateCell 全门禁，只在此一次性执行。
        /// </summary>
        private IntVec3 SpiralSearchValidLandingCell(Designator_MoveGravship designator,
            IntVec3 origin, Map map)
        {
            for (int radius = 2; radius <= 30; radius += 2)
            {
                for (int dx = -radius; dx <= radius; dx += 2)
                {
                    for (int dz = -radius; dz <= radius; dz += 2)
                    {
                        bool onRing = Mathf.Abs(dx) == radius || Mathf.Abs(dz) == radius;
                        if (!onRing) continue;
                        IntVec3 probe = new IntVec3(origin.x + dx, 0, origin.z + dz);
                        if (probe.InBounds(map) && designator.CanDesignateCell(probe).Accepted)
                        {
                            return probe;
                        }
                    }
                }
            }
            return IntVec3.Invalid;
        }

        internal bool IsDockingFlowShip(ShipState ship)
        {
            return ship != null && (ReferenceEquals(pendingShip, ship)
                || ReferenceEquals(universe?.Approach.FlowShip, ship));
        }

        /// <summary>选点/进场流程中的战术降落船（BeginOriginalLanding 置位，WriteDockedState 清除）。</summary>
        internal ShipState PendingDockingShip(UniverseManager universe)
        {
            return universe?.ShipTransitions.pendingShip;
        }

        /// <summary>
        /// 对接计划取消时的快照丢弃（LandingApproachController.Cancel/Abort 调用）：
        /// 清 pending 对、释放 Current.Game.Gravship 与控制器引用——计划为运行时数据，
        /// 取消后船继续自由飞行，可重新发起对接。
        /// </summary>
        internal void DiscardPendingDocking(UniverseManager universe)
        {
            if (universe != null)
            {
                universe.ShipTransitions.pendingShip = null;
                universe.ShipTransitions.pendingSelectionWasManual = false;
            }
            if (Current.Game?.Gravship != null)
            {
                Current.Game.Gravship = null;
            }
            ControllerGravship?.SetValue(Find.GravshipController, null);
        }

        /// <summary>
        /// 已写入停靠状态、等待到达重放与口袋图清理的船（WriteDockedState 置位，
        /// NotifyVanillaLandingEnded 清除）。到达重放的 InitiateLanding Prefix 发生在
        /// pendingShip 清空之后，落点归属盖章按此窗口识别战术降落船。
        /// </summary>
        internal ShipState LandedShipAwaitingPocketCleanup(UniverseManager universe)
        {
            return universe?.ShipTransitions.landedShipAwaitingPocketCleanup;
        }

        /// <summary>
        /// 落点反向映射——**落点注册的唯一计算点**（2026-09-21 用户定案）：只在选点确认
        /// （LandingApproachController.TryConsume）调用一次，结果存入 DockingPlan；此后
        /// 幽灵船/对齐判据/最终落位（WriteDockedState）一律读注册值，**不得二次计算**
        /// （历史事故：完成时用真实清单重算一遍，两条计算链分叉导致落位偏移）。
        /// 落点 cell/旋转 → 轨道层位姿 = 宿主 SpaceObject 位姿 ⊕ 落点局部
        /// （星区架构 §4 坐标桥）；角度 = 宿主帧角 ⊕ 最终放置朝向——ThingPlacements
        /// getter 用**当前** gravship.Rotation 合成最终朝向，调用时它必须是玩家确认的
        /// 落点旋转（选点 marker 的旋转变更直接作用于快照）。
        /// </summary>
        internal bool ComputeLandingPose(ShipState ship, Gravship gravship, Map map,
            IntVec3 landingPos, Rot4 landingRot, out Vector3 direction, out float angle,
            out Vector2 dockedOrigin)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject hostFrame = universe?.FrameOf(map);
            if (hostFrame == null)
            {
                direction = default(Vector3);
                angle = 0f;
                dockedOrigin = default(Vector2);
                return false;
            }
            float hostAngle = hostFrame.pose.angle;
            Rot4 landedFacing = gravship.PilotConsole != null
                && gravship.ThingPlacements.TryGetValue(gravship.PilotConsole, out PositionData.Data data)
                ? data.rotation
                // 无主台防御：以当前航向（换算到宿主图坐标系）为落地朝向。
                : ShipOrientation.Rot4FromAngle(SpaceTransform.WrapAngle(
                    ShipOrientation.HeadingAngle(ship) - hostAngle));
            angle = SpaceTransform.WrapAngle(hostAngle + ShipOrientation.AngleFromRot4(landedFacing));

            // 停放锚点唯一入口：root 格 + 旋转 → 船体帧原点（dockedOrigin）。
            if (ship.map == null)
            {
                direction = default(Vector3);
                dockedOrigin = default(Vector2);
                return false;
            }
            dockedOrigin = SpaceTransform.ShipOriginFromPlacementRoot(
                ship.map, ship.pocketRoot, landingPos, landingRot);
            direction = SpaceTransform.MapPointToUniverse(hostFrame, map, dockedOrigin);
            return true;
        }

        /// <summary>
        /// 战术降落的真转移状态写入（LandingApproachController 完成时调用）：docked 字段、
        /// 位姿/停靠原点直接写 **DockingPlan 注册数据**（选点确认时经 ComputeLandingPose
        /// 算好的唯一一份——落点注册单一数据源，2026-09-21 用户定案：此后全流程读注册值，
        /// 不得二次计算，否则幽灵船/判据/落位会分叉）、剪影失效与口袋图清理登记。
        /// </summary>
        internal void WriteDockedState(UniverseManager universe, ShipState ship, Map map,
            IntVec3 landingPos, Rot4 landingRot, Vector3 direction, float angle, Vector2 dockedOrigin)
        {
            ship.dockedRotation = landingRot;
            ship.dockedMap = map;
            ship.dockedOrigin = dockedOrigin;
            SpaceObject host = universe.FrameOf(map);
            ship.dockedLocalAngle = host != null
                ? SpaceTransform.WrapAngle(angle - host.pose.angle)
                : 0f;
            if (host != null)
            {
                ship.pose = SpaceTransform.RigidPointPose(host.pose,
                    SpaceTransform.MapToShipLocal(map, dockedOrigin), ship.dockedLocalAngle);
            }
            else
            {
                SpacePose preserved = ship.pose;
                preserved.position = direction;
                preserved.angle = angle;
                ship.pose = preserved;
            }
            ship.flightState = ShipFlightState.Docked;
            // 剪影快照失效：宿主图新增了停靠船体（星球仪按 Substructure 重扫）。
            PlanetGlobeRenderer.InvalidateSilhouette(map);
            HostColonistBarVisibility.NotifyDockingStateChanged();
            universe.ShipTransitions.landedShipAwaitingPocketCleanup = ship;
            universe.ShipTransitions.pendingShip = null;
            Messages.Message("DeltaV_DockSuccess".Translate(ship.label), MessageTypeDefOf.PositiveEvent, false);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: docking confirmed; rot={landingRot}, heading={angle * Mathf.Rad2Deg:F0} deg");
        }

        /// <summary>原版 LandingEnded Postfix：动画与 Gravship 已彻底释放后销毁空口袋图。</summary>
        public void NotifyVanillaLandingEnded(UniverseManager universe)
        {
            ShipFlightTransitionService flow = universe?.ShipTransitions;
            if (flow != null)
            {
                SubstructureOwnership.TryGet(flow.landingCaptureMap)?.ClearPendingStamp();
                flow.landingCaptureEngine = null;
                flow.landingCaptureMap = null;
                flow.landingCaptureCells = null;
            }
            ShipState ship = flow?.landedShipAwaitingPocketCleanup;
            if (flow != null)
            {
                flow.landedShipAwaitingPocketCleanup = null;
            }
            DestroyDockedPocketMap(ship);
            // 二次剪影失效：WriteDockedState 的失效发生在落点放置之前，进场 cutscene 期间
            // 的星球仪查询会把「船 ID 在此图无格」缓存为空；放置完成后按新归属重扫。
            if (ship?.dockedMap != null)
            {
                PlanetGlobeRenderer.InvalidateSilhouette(ship.dockedMap);
            }
            Building_DockingPort.NotifyLandingSettled();
        }

        /// <summary>旧版本存档迁移：停靠船若仍引用空口袋图，在加载收尾后销毁。</summary>
        public void CleanupObsoleteDockedPocketMaps(UniverseManager universe)
        {
            if (universe == null || Current.Game?.Gravship != null) return;
            List<ShipState> ships = universe.Ships
                .Where(s => s.IsDocked && s.map != null && s.map != s.dockedMap)
                .ToList();
            if (ships.Count == 0) return;
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                foreach (ShipState ship in ships) DestroyDockedPocketMap(ship);
            });
        }

        private void DestroyDockedPocketMap(ShipState ship)
        {
            Map pocketMap = ship?.map;
            if (ship == null || !ship.IsDocked || pocketMap == null || pocketMap == ship.dockedMap) return;
            // 旧存档可能尚无 dockedOrigin；必须在 map 引用消失前从原口袋中心重建。
            _ = ShipStructureResolver.DockedOrigin(ship);
            if (Find.CurrentMap == pocketMap && ship.dockedMap != null)
            {
                Current.Game.CurrentMap = ship.dockedMap;
            }
            ShipPhysics.ClearCache();
            // 进场期间口袋图经 MapOverlayRenderer 绘制过船壳 section，销毁前释放缓存。
            Current.Game?.GetComponent<UniverseManager>()?.Lifecycle.DestroyPocketMap(pocketMap);
            ship.map = null;
            ship.pocketRoot = IntVec3.Invalid;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: obsolete flight pocket map destroyed after landing");
        }

        // ---- 固定 → 移动 ----

        public void BeginFreeFlight(UniverseManager universe, ShipState ship)
        {
            if (universe == null || ship == null || !ship.IsDocked) return;
            Map sourceMap = ship.dockedMap;
            if (sourceMap == null || !Find.Maps.Contains(sourceMap))
            {
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (universe.FrameOf(sourceMap) == null)
            {
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            Building_GravEngine engine = ShipStructureResolver.EngineOn(sourceMap, ship.primaryConsole);
            if (engine == null)
            {
                Messages.Message("DeltaV_DockFailNoEngine".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            // 门禁与自由飞行捕获同口径：控制台所属引擎的整个相连基架域。
            // 没有任何一台可工作推进器则拒绝开始自由飞行。
            if (!ThrusterPower.HasWorkingThruster(sourceMap, engine.AllConnectedSubstructure))
            {
                Messages.Message("DeltaV_LaunchFailNoThruster".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (FreeFlightTransitionPending() || pendingShip != null
                || Find.GravshipController == null
                || Find.GravshipController.LandingAreaConfirmationInProgress
                || Find.GravshipController.IsGravshipTravelling || Current.Game.Gravship != null)
            {
                Messages.Message("DeltaV_DockFailBusy".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            // 固定→移动不销毁任何 worldtile 图（2026-09-19 二次定稿）：源图保留，去留
            // 交给锚对象配置与距离生命周期；非 worldtile 源的空白删除在起飞后由
            // HandleDepartedSourceMap 统一处理。本转换无破坏性内容丢失，不需要确认弹窗。
            ExecuteLaunch(universe, ship);
        }

        /// <summary>BeginFreeFlight 的执行体：入口守卫与弃置确认弹窗之后的部分。</summary>
        private void ExecuteLaunch(UniverseManager universe, ShipState ship)
        {
            Map sourceMap = ship.dockedMap;
            Building_GravEngine engine = ShipStructureResolver.EngineOn(sourceMap, ship.primaryConsole);
            if (engine == null) return;
            if (engine.launchInfo == null)
            {
                engine.launchInfo = new LaunchInfo { quality = 1f, doNegativeOutcome = false };
            }
            ShipFlightTransitionService flow = universe.ShipTransitions;
            flow.pendingFreeFlightShip = ship;
            flow.pendingFreeFlightEngine = engine;
            flow.pendingFreeFlightSourceMap = sourceMap;
            flow.pendingFreeFlightSourceRoot = engine.Position;
            // 固定 → 移动必须从原版起飞入口开始，完整复用捕获、PreSwapMap、
            // GenerateGravship、Map 清理和 TakeoffEnded。目标 tile 只是满足原版协议；
            // TravelTo Prefix 会在起飞收尾点把 Gravship 接入口袋图。口袋图宿主
            // （POI/无动力船内部）无 tile：战略源地址 = TileFromDirection(船 pose)（§6）。
            universe.Pilot.Exit(universe);
            Find.GravshipController.InitiateTakeoff(engine, StrategicSourceAddress(universe, sourceMap));
            // 降落图是持久画布，不能让原版 TakeoffEnded 因没有 GravAnchor 而 AbandonMap；
            // 这只改变源图保留策略，不绕过任何船体搬运步骤。
            ControllerMapHasGravAnchor?.SetValue(Find.GravshipController, true);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: mobile conversion handed to vanilla InitiateTakeoff");
        }

        /// <summary>
        /// 口袋图起飞的战略源地址（星区架构 §6/§7/§9）：口袋图无 tile，原版起飞管线中
        /// 一切以源图 tile 为输入的环节改接该地址。真实轨道图原样返回自身 tile。
        /// </summary>
        internal PlanetTile StrategicSourceAddress(UniverseManager universe, Map map)
        {
            if (map != null && map.Tile.Valid) return map.Tile;
            if (!OrbitSphere.Valid) return PlanetTile.Invalid;
            // 口袋图起飞一律视为从所在星区起飞（§7）：船（自有/停靠）优先，其余
            // Delta-V 口袋图（如 POI 内自建引擎发射）回退图 owner 的星区投影。
            SpaceObject owner = universe?.ShipOnMap(map)
                ?? universe?.Ships.FirstOrDefault(s => s.IsDocked && s.dockedMap == map)
                ?? universe?.SpaceObjectOnMap(map);
            if (owner == null) return PlanetTile.Invalid;
            return OrbitSphere.TileFromDirection(owner.pose.position);
        }

        /// <summary>
        /// InitiateTakeoff Postfix 的执行体：源图与 Delta-V 相关时的原版战略起飞适配
        /// （星区架构 §6/§9）。本 mod 自己的 BeginFreeFlight 流程不经过这里。
        /// - 口袋图源：原版记录的 takeoffTile 无效（TakeoffEnded → TravelTo 会拿它做跨层
        ///   换算并登记进 WorldObjects），改写为战略源地址。
        /// - 源图保留策略统一交 Delta-V（2026-09-19 无锚起飞语义）：真实图源原本会因
        ///   无锚被原版 TakeoffEnded 直接 AbandonMap（遗留基架来不及转残骸），口袋图源
        ///   本就不在原版 Abandon 语义内——一律先保护，起飞完成（TravelTo 放行）后由
        ///   HandleStrategicTravelDeparture 按「有锚保留 / 无锚弃置 + 基架转残骸」统一执行。
        /// - 源图承载的 ShipState 交还原版旅行： unregister 身份（再次开始自由飞行时由
        ///   bootstrap 重建）。
        /// </summary>
        internal void HandleVanillaTakeoffFromDeltaVSource(Building_GravEngine engine,
            WorldComponent_GravshipController controller)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (engine?.Map == null || universe == null || FreeFlightTransitionPending()) return;
            Map sourceMap = engine.Map;
            bool pocketSource = !sourceMap.Tile.Valid;
            // 只介入 Delta-V 语境的源图（口袋图或轨道层真实图）；地表 gravship 起飞
            // 完全走原版行为（无锚地表起飞本就 AbandonMap + 留 GravshipLaunch 标记），
            // 不因本 mod 改变保留策略。
            if (pocketSource || sourceMap.Tile.LayerDef.isSpace)
            {
                ShipFlightTransitionService flow = universe.ShipTransitions;
                ControllerMapHasGravAnchor?.SetValue(controller, true);
                flow.pendingStrategicSourceMap = sourceMap;
                flow.pendingStrategicSourceAddress = pocketSource
                    ? StrategicSourceAddress(universe, sourceMap)
                    : sourceMap.Tile;
                flow.pendingStrategicSourcePose = universe.FrameOf(sourceMap)?.pose;
                if (pocketSource && flow.pendingStrategicSourceAddress.Valid)
                {
                    ControllerTakeoffTile?.SetValue(controller, flow.pendingStrategicSourceAddress);
                }
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"vanilla strategic takeoff from {(pocketSource ? "pocket" : "tile")} map: " +
                    $"source address sector {flow.pendingStrategicSourceAddress}, source map deferred to Delta-V settlement");
            }

            // 多船同图停靠时，引擎所在连通域的船才是发射船（防注销/留名到错误船）；
            // 找不到再回退任一停靠船（单船停靠的常态路径）。
            ShipState ship = universe?.ShipOnMap(sourceMap)
                ?? universe?.Ships.FirstOrDefault(s => s.IsDocked && s.dockedMap == sourceMap
                    && ShipStructureResolver.Contains(s, engine))
                ?? universe?.Ships.FirstOrDefault(s => s.IsDocked && s.dockedMap == sourceMap);
            if (ship == null) return;
            // 身份常驻驾驶台 comp（2026-09-20 设计，无需在此搬运）；注销前把当前船名
            // 写进注册表，战略飞行期间归属域名仍可解析。
            universe.NoteShipName(ship.objectId, ship.label);
            universe.Pilot.Exit(universe);
            HostColonistBarVisibility.NotifyDockingStateChanged();
            universe.UnregisterObject(ship);
            // 对象 ID 释放后回池（新星区生成物可能立即复用同一 ID），清掉 HUD 跟踪
            // 指针；getter 本就按 ShipState 过滤解析为 null，这里防的是陈旧 ID 与
            // 未来复用者混淆。到达 bootstrap 时重新登记。
            if (universe.focusedShipId == ship.objectId) universe.focusedShipId = -1;
            if (universe.primaryShipId == ship.objectId) universe.primaryShipId = -1;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: handed to vanilla strategic travel; Delta-V identity released (re-bootstrap on arrival)");
        }

        /// <summary>
        /// 原版战略起飞放行 TravelTo 后的星区离场善后（Patch_GravshipUtility_TravelTo
        /// 的 Postfix 调用），预存的源图/战略地址/位姿经帧边界延迟执行（时序约束：
        /// 必须排在 TravelTo 放行后，排队过早会卡守卫——旧 bug 勿回退）。此刻玩家在
        /// 世界视图看 gravship 航行，CurrentMap 通常仍是源图——我们阻止了原版
        /// TakeoffEnded 的 AbandonMap 级联，没有环节会把焦点移走；焦点处理见各段。
        ///
        /// 统一标准流程（2026-09-20 用户决策，取代按源图身份分支的多重实现）：
        /// ① 飞船自有飞行口袋图源 → **总是销毁口袋图**（遗弃滞留者、遗留基架转无主
        ///    残骸——整船捕获已带走船体，容器无意义）；
        /// ② 归属星区（战略源地址 tile）的 worldtile 图 → **按逆重锚决定去留**
        ///    （无锚 → 原版 AbandonMap 弃置 + 基架转残骸；有锚/有其他停靠船保留），
        ///    任意源图类型统一执行；无 worldtile 图则跳过；
        /// ③ 星区内**其余 SpaceObject 地图**（含 wreck 等停靠源图）→ 一律按超删除
        ///    距离处理（休眠 + 快照收纳/删除，守卫照常生效）——旅行期间距离评估因
        ///    无玩家参考点整体跳过（+Infinity continue），不能依赖自然距离触发。
        /// </summary>
        internal void HandleStrategicTravelDeparture()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            ShipFlightTransitionService flow = universe?.ShipTransitions;
            Map sourceMap = flow?.pendingStrategicSourceMap;
            PlanetTile address = flow?.pendingStrategicSourceAddress ?? PlanetTile.Invalid;
            SpacePose? pose = flow?.pendingStrategicSourcePose;
            if (flow != null)
            {
                flow.pendingStrategicSourceMap = null;
                flow.pendingStrategicSourceAddress = PlanetTile.Invalid;
                flow.pendingStrategicSourcePose = null;
            }
            if (sourceMap == null) return;
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                if (universe == null || sourceMap.Disposed || !Find.Maps.Contains(sourceMap)) return;

                // ① 飞船自有飞行口袋图（战略发射源，身份已释放故无 owner）。
                bool ownPocket = !sourceMap.Tile.Valid && universe.SpaceObjectOnMap(sourceMap) == null
                    && !SpaceLifecycleService.IsUniverseHost(sourceMap);
                if (ownPocket)
                {
                    // 焦点还在源图上是战略航行的常态。先显式置空（战略航行 = 原版
                    // 「无当前图」形态），不能交给 DeinitAndRemoveMap 的 maps[0] 轮盘：
                    // 可能命中宿主图被 CurrentMap patch 拒绝后按陈旧索引重定向，
                    // 也可能把休眠 POI 图选成当前图并唤醒。
                    if (sourceMap == Find.CurrentMap)
                        Current.Game.CurrentMap = null;
                    AbandonPawnsOn(sourceMap);
                    ConvertLeftoverSubstructureToWreck(universe, sourceMap,
                        pose ?? new SpacePose(Vector3.right, 0f));
                    MapOverlayRenderer.Release(sourceMap);
                    PlanetGlobeRenderer.InvalidateSilhouette(sourceMap);
                    universe.Lifecycle.DestroyPocketMap(sourceMap);
                    ShipPhysics.ClearCache();
                    DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                        "flight pocket map removed after strategic takeoff (leftovers converted)");
                }

                // ② 归属星区 worldtile 图：按锚配置决定去留（任意源统一；用预存地址，
                // 船已注销、DepartedSectorMapFor 的重算链此刻失效）。worldtile 源时
                // sectorMap == 源图本身。将弃置前先放焦点（同 ① 理由）；有锚保留时
                // 不动焦点（原版锚定前哨：玩家可继续留在图上）。
                Map sectorMap = address.Valid && address.LayerDef.isSpace
                    ? Find.WorldObjects.MapParentAt(address)?.Map
                    : null;
                if (sectorMap != null && !sectorMap.Disposed && Find.Maps.Contains(sectorMap)
                    && SectorMapWillBeAbandoned(universe, sectorMap))
                {
                    if (sectorMap == Find.CurrentMap)
                        Current.Game.CurrentMap = null;
                    AbandonUnanchoredSectorMap(universe, sectorMap);
                }

                // ③ 星区其余 SpaceObject 地图（含 wreck 等停靠源图）：按超删除距离
                // 休眠 + 收纳。源图即将打包时先放焦点；作为锚保留 worldtile 图的
                // 源图（sourceMap == sectorMap 且未被弃置）不在此列。
                if (!ownPocket && sourceMap != sectorMap && sourceMap == Find.CurrentMap)
                    Current.Game.CurrentMap = null;
                universe.Lifecycle.PackDepartedSectorPois(address, sectorMap);
            });
        }

        /// <summary>原版捕获完成后跳过可见起飞动画，但仍让 TakeoffEnded 执行全部收尾。</summary>
        public void SuppressFreeFlightTransitionAnimation(UniverseManager universe)
        {
            if (!FreeFlightTransitionPending()) return;
            GravshipInterop.ForceCutsceneTimeZero(Find.GravshipController);
        }

        /// <summary>兜底：若 BeginCutscene 发生在其他更新边界，下一帧仍强制完成。</summary>
        public void BeforeGravshipControllerUpdate(UniverseManager universe,
            WorldComponent_GravshipController controller)
        {
            if (!FreeFlightTransitionPending() || !controller.IsGravshipTravelling) return;
            GravshipInterop.ForceCutsceneTimeZero(controller);
        }

        /// <summary>
        /// 到达时执行原版 GenerateGravship 的搬运段（用户决策流程的"清除原图
        /// Substructure"）：数据清单已在选点快照冻结，此处只移除清单内的 Thing/Pawn
        /// 并剥除口袋图的 Substructure 地形/屋顶/区域标志等。逐字段镜像原版操作，
        /// 不得增删语义；口袋图随后由 LandingEnded Postfix 销毁。
        /// </summary>
        internal bool FinalizeShipCapture(UniverseManager universe, Gravship gravship)
        {
            Building_GravEngine engine = gravship?.Engine;
            Map map = engine?.Map;
            if (gravship == null || engine == null || map == null || !engine.Spawned)
            {
                return false;
            }

            IntVec3[] cells;
            universe.ShipTransitions.directCaptureEngine = engine;
            try
            {
                cells = engine.ValidSubstructure.ToArray();
            }
            finally
            {
                universe.ShipTransitions.directCaptureEngine = null;
            }

            // 移除清单内容（与原版同序：先全体 PreSwapMap，再 DeSpawn）。
            List<Thing> things = gravship.Things.ToList();
            things.SortByDescending(GravshipUtility.ThingSpawnPriority);
            foreach (Thing thing in things)
            {
                thing.PreSwapMap();
            }
            foreach (Thing thing in things)
            {
                if (thing.Spawned)
                {
                    thing.DeSpawn(DestroyMode.WillReplace);
                }
            }
            foreach (Pawn pawn in gravship.Pawns)
            {
                pawn.PreSwapMap();
                if (pawn.Spawned)
                {
                    pawn.DeSpawn(DestroyMode.WillReplace);
                }
            }

            // 剥除 Substructure 地形（镜像原版 GenerateGravship 的逐格清理）。
            ulong dirtyFlags = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Roofs
                | (ulong)MapMeshFlagDefOf.Snow | (ulong)MapMeshFlagDefOf.Sand | (ulong)MapMeshFlagDefOf.Gas;
            for (int i = 0; i < cells.Length; i++)
            {
                IntVec3 cell = cells[i];
                int index = map.cellIndices.CellToIndex(cell);
                map.terrainGrid.RemoveGravshipTerrainUnsafe(cell, index);
                map.roofGrid.RemoveRoofUnsafe(index);
                map.areaManager.BuildRoof[index] = false;
                map.areaManager.NoRoof[index] = false;
                map.glowGrid.DirtyCell(cell);
                map.snowGrid.SetDepth(cell, 0f);
                map.sandGrid.SetDepth(cell, 0f);
                map.gasGrid.ClearCellUnsafe(cell);
                foreach (Designation designation in map.designationManager.AllDesignationsAt(cell))
                {
                    map.designationManager.RemoveDesignation(designation);
                }
                IntVec2 cell2D = cell.ToIntVec2;
                foreach (FleckSystem system in map.flecks.Systems)
                {
                    system.RemoveAllFlecks(fleck => fleck.GetPosition().ToIntVec3().ToIntVec2 == cell2D);
                }
                map.mapDrawer.MapMeshDirty(cell, dirtyFlags, regenAdjacentCells: true, regenAdjacentSections: false);
                map.pathing.RecalculatePerceivedPathCostAt(cell);
            }
            map.roofGrid.Drawer.SetDirty();

            // 口袋图即将销毁：失效星球仪剪影缓存，避免任何读取者拿到已剥空的状态。
            PlanetGlobeRenderer.InvalidateSilhouette(map);
            return true;
        }

        /// <summary>
        /// 原版 TakeoffEnded 调用 TravelTo 的扩展点：船已完成原生捕获/移除与源图收尾，
        /// 此处只把世界旅行目的地改为独占口袋图。返回 true 表示已消费该次 TravelTo。
        /// </summary>
        public bool TryCompleteFreeFlight(Gravship gravship)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            ShipFlightTransitionService flow = universe?.ShipTransitions;
            ShipState ship = flow?.pendingFreeFlightShip;
            Building_GravEngine engine = flow?.pendingFreeFlightEngine;
            Map sourceMap = flow?.pendingFreeFlightSourceMap;
            if (ship == null || engine == null || sourceMap == null
                || gravship == null || gravship.Engine != engine) return false;

            if (universe == null)
            {
                Log.Error("[DeltaV] Mobile launch reached TravelTo without UniverseManager; vanilla travel retained.");
                ClearPendingFreeFlight(universe);
                return false;
            }

            // GenerateGravship 的电力注销是 PowerNetManager 延迟动作。原版十秒动画期间
            // 会自然结算；我们把动画压到零，因此在新图 Spawn/PowerBuildings 前显式走
            // 同一个原生结算入口，避免旧 PowerNet 上重复 RegisterConnector。
            sourceMap.powerNetManager.UpdatePowerNetsAndConnections_First();

            // 起飞后的轨道位姿 = 宿主 SpaceObject 位姿 ⊕ 起飞点的内部坐标（星区架构 §7）：
            // FrameOf 对停靠图解析为宿主对象；对任意角度的口袋宿主（残骸、
            // 空间站内部）同样成立。已停靠期间允许非对称扩建——新口袋图以当前船体
            // 中心为局部原点（GravshipBoundsCenterFromEngineRoot：原版 engineToCenter 语义，
            // ShipPocketMapFactory 居中放置后即口袋图几何中心），起飞后每个建筑的绝对位置保持不变。
            Vector2 sourceStructureCenter = SpaceTransform.GravshipBoundsCenterFromEngineRoot(
                gravship, flow.pendingFreeFlightSourceRoot);
            SpaceObject hostFrame = universe.FrameOf(sourceMap);
            if (hostFrame == null)
            {
                // 原版已经消费船体：恢复到源图，绝不能凭旧船位姿猜一个 tile 帧。
                gravship.Rotation = Rot4.North;
                GravshipInterop.PlaceIntoMap(gravship, flow.pendingFreeFlightSourceRoot, sourceMap, ship.objectId);
                FinishConsumedVanillaTravel();
                ClearPendingFreeFlight(universe);
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                Log.Error($"[DeltaV] Launching '{ship.label}': source map owner lost; restored ship to docked map.");
                return true;
            }
            Vector2 launchOffset = SpaceTransform.MapToShipLocal(sourceMap, sourceStructureCenter);
            float localHeading = gravship.PilotConsole != null
                ? ShipOrientation.AngleFromRot4(gravship.PilotConsole.Rotation)
                : ship.dockedLocalAngle;
            SpacePose launchPose = SpaceTransform.RigidPointPose(hostFrame.pose, launchOffset, localHeading);

            // 主台在口袋图规范到 East（局部 +X），避免 180° 往返时将降落旋转重复计入。
            gravship.Rotation = gravship.PilotConsole != null
                ? ShipOrientation.PlacementRotation(gravship.PilotConsole.Rotation, Rot4.East)
                : new Rot4(-ship.dockedRotation.AsInt);
            if (!ShipPocketMapFactory.TryGenerateForGravship(gravship, universe,
                out Map pocketMap, out IntVec3 pocketRoot))
            {
                // 新容器生成失败时不能吞船：用捕获时的 North 布局原样放回停靠图。
                gravship.Rotation = Rot4.North;
                GravshipInterop.PlaceIntoMap(gravship, flow.pendingFreeFlightSourceRoot, sourceMap, ship.objectId);
                FinishConsumedVanillaTravel();
                ClearPendingFreeFlight(universe);
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                Log.Error($"[DeltaV] Launching '{ship.label}': failed to generate replacement pocket map; restored ship to docked map.");
                return true;
            }

            ship.map = pocketMap;
            ship.pocketRoot = pocketRoot;
            GravshipInterop.PlaceIntoMap(gravship, pocketRoot, pocketMap, ship.objectId);
            Current.Game.Gravship = null;

            ship.pose = launchPose;
            ship.flightState = ShipFlightState.FreeFlight;
            ship.dockedMap = null;
            ship.dockedOrigin = Vector2.zero;
            // 世界视图标记是“开始自由飞行时的船体快照”，不追踪后续战损/建造变化。
            WorldShipMarkerRenderer.RebuildFor(ship);
            // 剪影快照失效：源图（宿主）已移除船体、新口袋图是全新键——宿主侧不得
            // 残留已起飞飞船的剪影（2026-09-17 实测事故）。
            PlanetGlobeRenderer.InvalidateSilhouette(sourceMap);
            // 脱离停靠时清除源图旧域：船体已被捕获剥离，源图上
            // 残留的本船归属值全是陈旧数据；基架不重叠，按 ID 整清不会误伤他主域。
            SubstructureOwnership.TryGet(sourceMap)?.ClearOwner(ship.objectId);
            HostColonistBarVisibility.NotifyDockingStateChanged();
            universe.SetFocused(ship);
            CameraJumper.TryJump(ship.pocketRoot, pocketMap, CameraJumper.MovementMode.Cut);
            if (universe.Pilot.CanPlayerControl(ship)) universe.Pilot.Enter(universe, ship);
            FinishConsumedVanillaTravel();
            ClearPendingFreeFlight(universe);
            QueueEmptyDepartedMapCleanup(universe, sourceMap);
            Messages.Message("DeltaV_FreeFlightSuccess".Translate(ship.label), MessageTypeDefOf.PositiveEvent, false);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label} began free flight without takeoff cutscene; heading={ship.pose.angle * Mathf.Rad2Deg:F0} deg");
            return true;
        }

        /// <summary>
        /// 延后到帧边界清点离港地图：此时已完全脱离原版 TakeoffEnded 调用栈，
        /// DeinitAndRemoveMap 不会踩到上层仍在使用的引用。
        /// </summary>
        private void QueueEmptyDepartedMapCleanup(UniverseManager universe, Map map)
        {
            if (map == null) return;
            LongEventHandler.ExecuteWhenFinished(delegate { HandleDepartedSourceMap(universe, map); });
        }

        /// <summary>
        /// 固定→移动转换与读档清扫的源图善后（战略发射离场已改走星区标准流程
        /// HandleStrategicTravelDeparture，2026-09-20）：**worldtile 图不受固定↔移动
        /// 转换影响**——保留，去留交给锚对象配置与距离生命周期；非 worldtile 图空白
        /// 则立刻删除（遗留基架转无主残骸），非空白交给距离生命周期（SpaceLifecycle
        /// 超距快照收纳）。不按对象身份特判。
        /// </summary>
        internal void HandleDepartedSourceMap(UniverseManager universe, Map sourceMap)
        {
            if (universe == null || sourceMap == null || sourceMap.Disposed
                || !Find.Maps.Contains(sourceMap)) return;
            if (sourceMap.Tile.Valid) return; // worldtile 图：不受转换影响
            if (sourceMap == Find.CurrentMap)
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{sourceMap.Parent?.Label ?? sourceMap.ToString()}: pocket cleanup skipped (currently focused)");
                return;
            }
            if (SpaceLifecycleService.IsUniverseHost(sourceMap)
                || universe.Ships.Any(s => s.IsDocked && s.dockedMap == sourceMap)) return;
            SpaceObject owner = universe.SpaceObjectOnMap(sourceMap);
            if (owner is PoiState poi && SpaceMapGovernance.IsBlankLandingCanvas(sourceMap))
            {
                ConvertLeftoverSubstructureToWreck(universe, sourceMap, poi.pose);
                universe.Lifecycle.Forget(sourceMap);
                universe.Lifecycle.DestroyPocketMap(sourceMap);
                universe.UnregisterObject(poi);
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{poi.label}: emptied pocket interior removed with its object");
            }
        }

        /// <summary>
        /// 逆重起飞的弃置目标图（2026-09-19 用户方案）：源图有 worldtile 且在轨道层
        /// 即源图本身；口袋图起飞 = 战略源地址 tile 上的图（严格按所在星区——玩家
        /// 认知中逆重降落产生的图由所在星区代表承载）。返回 null = 无弃置目标。
        /// </summary>
        internal Map DepartedSectorMapFor(UniverseManager universe, Map sourceMap)
        {
            if (sourceMap == null) return null;
            if (sourceMap.Tile.Valid)
                return sourceMap.Tile.LayerDef.isSpace ? sourceMap : null;
            PlanetTile address = StrategicSourceAddress(universe, sourceMap);
            if (!address.Valid || !address.LayerDef.isSpace) return null;
            return Find.WorldObjects.MapParentAt(address)?.Map;
        }

        /// <summary>
        /// 该图将在逆重起飞后按无锚语义弃置（无锚、无其他停靠船）。
        /// exclude = 即将起飞的船（入口判定时它自己还 docked 在源图上，不算阻挡）。
        /// 不检查 CurrentMap：入口判定时源图必然是聚焦图（玩家正对着它点起飞），
        /// 该检查会让确认弹窗永不弹出（2026-09-19 实测 home 静默弃置的根因）；
        /// 「不删聚焦图」的防御由 AbandonUnanchoredSectorMap 的执行守卫承担。
        /// </summary>
        internal bool SectorMapWillBeAbandoned(UniverseManager universe, Map map, ShipState exclude = null)
        {
            return map != null && Find.Maps.Contains(map)
                && !map.listerThings.AnyThingWithDef(ThingDefOf.GravAnchor)
                && universe != null && !universe.Ships.Any(s => s != exclude && s.IsDocked && s.dockedMap == map);
        }

        /// <summary>
        /// 无锚弃置（星区标准流程第 ② 段，任意发射源统一，2026-09-20）：图上无逆重
        /// 锚 → 遗留基架先转无主残骸，再走原版 AbandonMap（含滞留 Pawn 遗弃——发射
        /// 链路的确认弹窗已警告）。有锚保留（原版锚定前哨）。
        /// </summary>
        private void AbandonUnanchoredSectorMap(UniverseManager universe, Map map)
        {
            string mapLabel = map.Parent?.Label ?? map.ToString();
            if (map == Find.CurrentMap)
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{mapLabel}: unanchored abandon skipped (currently focused)");
                return;
            }
            if (SpaceLifecycleService.IsUniverseHost(map))
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{mapLabel}: unanchored abandon skipped (universe host)");
                return;
            }
            ShipState dockedBlocker = universe.Ships.FirstOrDefault(s => s.IsDocked && s.dockedMap == map);
            if (dockedBlocker != null)
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{mapLabel}: unanchored abandon skipped (docked ship: {dockedBlocker.label})");
                return;
            }
            if (map.listerThings.AnyThingWithDef(ThingDefOf.GravAnchor))
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{mapLabel}: unanchored abandon skipped (grav anchor present)");
                return;
            }
            // 遗留基架转无主残骸（2026-09-19 用户决策）：必须在删图前收快照。
            ConvertLeftoverSubstructureToWreck(universe, map,
                new SpacePose(OrbitSphere.TileCenterDirection(map.Tile), 0f));
            GravshipUtility.AbandonMap(map);
            ShipPhysics.ClearCache();
            PlanetGlobeRenderer.InvalidateSilhouette(map);
            DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                $"{mapLabel}: unanchored sector map abandoned after strategic takeoff");
        }

        /// <summary>
        /// 起飞后被遗留的基架不随弃置消失（2026-09-19 用户决策）：删除前收整图结构
        /// 快照（此刻捕获域已剥除，收到的即遗留内容），在原图位姿生成无主残骸
        /// SpaceObject（SnapshotPocket，接近后按快照物化）。无遗留基架则不生成——
        /// 普通杂物仍按原版语义随图失去。label 与 SectorContentService 残骸同款。
        /// </summary>
        private void ConvertLeftoverSubstructureToWreck(UniverseManager universe, Map map, SpacePose pose)
        {
            bool hasFoundation = false;
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                if (map.terrainGrid.FoundationAt(cell) != null)
                {
                    hasFoundation = true;
                    break;
                }
            }
            if (!hasFoundation) return;
            var wreck = new PoiState(universe.AllocateOwnerId(), "Derelict wreck",
                pose.position, pose.angle, Rand.Int);
            wreck.snapshot = PoiSnapshot.Capture(map);
            universe.RegisterObject(wreck);
            DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                $"{wreck.label} created from leftover substructure ({wreck.snapshot.ThingCount} things)");
        }

        /// <summary>
        /// 口袋图删除前的滞留 Pawn 遗弃（照原版 GravshipUtility.AbandonMap 的 Pawn
        /// 循环：Lost 思想 + Notify_LeftBehind + 销毁）——口袋 parent 不在原版 Abandon
        /// 语义内，故只取其 Pawn 处理，图删除走 PocketMapUtility。
        /// </summary>
        private void AbandonPawnsOn(Map map)
        {
            List<Pawn> pawns = map.mapPawns.AllPawnsSpawned.ToList();
            PawnDiedOrDownedThoughtsUtility.TryGiveThoughts(pawns, PawnDiedOrDownedThoughtsKind.Lost);
            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                pawns[i].Notify_LeftBehind();
                if (pawns[i].Spawned) pawns[i].DeSpawn();
                if (!pawns[i].Destroyed) pawns[i].DestroyOrPassToWorld();
            }
        }

        private void FinishConsumedVanillaTravel()
        {
            Current.Game.Gravship = null;
            ControllerGravship?.SetValue(Find.GravshipController, null);
        }

        private void ClearPendingFreeFlight(UniverseManager universe)
        {
            ShipFlightTransitionService flow = universe?.ShipTransitions;
            if (flow == null) return;
            flow.pendingFreeFlightShip = null;
            flow.pendingFreeFlightEngine = null;
            flow.pendingFreeFlightSourceMap = null;
            flow.pendingFreeFlightSourceRoot = IntVec3.Invalid;
        }

    }
}
