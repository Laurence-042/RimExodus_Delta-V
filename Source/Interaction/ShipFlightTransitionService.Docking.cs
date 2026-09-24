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
    /// 自由飞行 / 停靠转换与结构拆分（解耦架构 §6–§9）。全部可变流程状态收进
    /// <see cref="ShipFlightTransitionService"/>，由 UniverseManager 以实例持有；
    /// 「读其他档残留旧流程」不可表示；方法一律经 universe 参数读写。
    ///
    /// 搬运链（无引擎，§15.2 核对结论）：GravshipInterop.CaptureDomain（按 cells 捕获）
    /// → StripDomain（镜像移除段）→ 新口袋图 → PlaceIntoMap（原版放置收尾）。战术对接
    /// 完成、脱离与断连拆分共用同一条链——不另造搬运算法，不走 InitiateTakeoff/
    /// InitiateLanding 原版管线（那两条保留给战略发射与原版到达）。
    ///
    /// 对接（移动 → 固定，合图 §6）：A 的控制域整体落入目标 b 图；保留 A/B 各自归属、
    /// 名称与驾驶台身份；源口袋图与源容器删除（非空源图按发起时已确认的残留清单处置，
    /// 不在捕获中途询问）；完成后 A、B 进入对接状态（派生：同图多船），共享容器继续
    /// 惯性运动（沿用 b 的运动状态，不新增合并冲量求解）。
    ///
    /// 脱离（固定 → 移动，拆图 §7）：新建容器 c 及其口袋图，仅转移主台控制域（无引擎
    /// 要求）；c 的位姿/速度 = 宿主容器刚体上该船体点的位姿（含自旋切向）；收通道 =
    /// 脱离事务的一部分（通知两侧端口，只收本次脱离相关通道）；通道 Pawn/物品原格留在
    /// 宿主图，操作前汇总确认。
    ///
    /// 出力前公共检查与拆分（§8）：仅「将要产生推进器出力」的行为触发（手控输入、
    /// 主动刹车辅助、自动驾驶），被动阻力与纯漂移不拆；公共拆分独立于 controller，
    /// 玩家 controller 负责事前确认（forcePause 弹窗），NPC 直接执行。
    /// </summary>
    internal sealed partial class ShipFlightTransitionService
    {
        private static readonly FieldInfo ControllerMapHasGravAnchor =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "mapHasGravAnchor");

        private static readonly FieldInfo ControllerGravship =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "gravship");

        private static readonly FieldInfo ControllerTakeoffTile =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "takeoffTile");

        public bool FreeFlightTransitionPending()
        {
            return pendingShip.IsValid || landedShipAwaitingCleanup.IsValid;
        }

        // ---- 原版到达画面/遮罩的冻结捕获范围（Patch_GravshipCaptureScope 消费）----
        // 战略到达放置后原版会读取 engine.ValidSubstructure 制作降落画面/遮罩；读现场
        // 集合会把邻接且处于力场内的 wreck 误并入飞船形态。冻结集来自 Gravship 自身的
        // Foundations（捕获时已定格），与目标图现场拓扑无关。
        // （旧的「口袋图战略发射 = 整域绕过力场」例外已按解耦架构 §12 退役——战略发射
        // 捕获回到原版力场脚印语义；本范围只服务到达画面冻结。）

        internal Building_GravEngine landingCaptureEngine;
        internal Map landingCaptureMap;
        internal HashSet<IntVec3> landingCaptureCells;

        internal void SetLandingCaptureScope(Map map,
            Building_GravEngine engine, HashSet<IntVec3> cells)
        {
            landingCaptureEngine = engine;
            landingCaptureMap = map;
            landingCaptureCells = cells;
        }

        /// <summary>指定引擎当前是否有到达画面专用的冻结捕获范围。</summary>
        public bool TryGetLandingCaptureScope(Building_GravEngine engine, out HashSet<IntVec3> scope)
        {
            scope = null;
            if (engine == null || !ReferenceEquals(engine, landingCaptureEngine) || landingCaptureCells == null)
            {
                return false;
            }
            scope = landingCaptureCells;
            return true;
        }

        /// <summary>原版 LandingEnded 之后的到达冻结清理。</summary>
        public void ClearLandingCaptureScope()
        {
            SubstructureOwnership.TryGet(landingCaptureMap)?.ClearPendingStamp();
            landingCaptureEngine = null;
            landingCaptureMap = null;
            landingCaptureCells = null;
        }

        public float SnapAngle(float angle)
        {
            float quarter = Mathf.PI * 0.5f;
            return SpaceTransform.WrapAngle(Mathf.Round(angle / quarter) * quarter);
        }

        // ---- 出力前公共检查与拆分（§8；挂点 = 仅有的两个 Allocate 调用方）----

        /// <summary>
        /// 出力请求 gate（唯一入口；UniverseManager 手操路径与 ShipAutopilot 在施加推进器
        /// 出力之前调用）。返回 true = 本次允许出力；false = 本 tick 中止出力（对接状态、
        /// 待确认拆分或取消抑制期间）。断连拆分只在这里与脱离流程触发——纯惯性漂移与
        /// 被动阻力不经本入口。
        /// </summary>
        public bool RequestThrust(UniverseManager universe, Ship ship, bool playerInitiated)
        {
            if (!ship.IsValid) return false;
            // 对接中的所有船不能手控推进或执行主动自动驾驶（§5.2）。
            if (!ShipRegistry.IsFreeFlight(ship)) return false;
            // 取消后的抑制期：结构版本不变不再重复弹窗，出力保持拒绝。
            if (splitPromptSuppressedOwnerId == ship.ownerId
                && splitPromptSuppressedVersion == SubstructureOwnership.GlobalStructureVersion)
            {
                return false;
            }
            List<HashSet<IntVec3>> blocks = CollectResidualBlocks(ship);
            if (blocks.Count == 0)
            {
                return true;
            }
            if (pendingSplitOwnerId == ship.ownerId)
            {
                return false; // 确认窗已打开（游戏已暂停）：等待玩家决定，不重复弹。
            }
            string summary = BuildSplitSummary(universe, ship, blocks);
            if (playerInitiated)
            {
                pendingSplitOwnerId = ship.ownerId;
                pendingSplitFromAutopilot = false;
                ShipController controller = universe.Pilot.ActiveController;
                if (controller != null)
                {
                    controller.PresentSplitConfirmation(ship, summary);
                }
                else
                {
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "DeltaV_SplitConfirmText".Translate(summary),
                        () => ConfirmSplitAndResume(universe, ship),
                        () => CancelSplitPrompt(universe, ship),
                        destructive: true));
                }
            }
            else
            {
                // NPC（或无 controller 上下文）：直接执行同一公共拆分，不弹玩家确认（§8.2）。
                SplitResiduals(universe, ship);
            }
            return false; // 本 tick 不施加该次推进器动量；确认/拆分后的下一次请求再出力。
        }

        /// <summary>确认回调：复核（同一查询入口）后执行公共拆分。清单变化则按当前状态重拆。</summary>
        public void ConfirmSplitAndResume(UniverseManager universe, Ship ship)
        {
            pendingSplitOwnerId = -1;
            pendingSplitFromAutopilot = false;
            splitPromptSuppressedOwnerId = -1;
            if (!ship.IsValid) return;
            SplitResiduals(universe, ship);
        }

        /// <summary>取消回调：取消本次触发操作；自动驾驶撤销任务；结构版本不变期间不再弹窗。</summary>
        public void CancelSplitPrompt(UniverseManager universe, Ship ship)
        {
            bool fromAutopilot = pendingSplitFromAutopilot;
            pendingSplitOwnerId = -1;
            pendingSplitFromAutopilot = false;
            if (ship.IsValid)
            {
                splitPromptSuppressedOwnerId = ship.ownerId;
                splitPromptSuppressedVersion = SubstructureOwnership.GlobalStructureVersion;
                if (fromAutopilot)
                {
                    universe.Autopilot.Cancel(ship);
                }
            }
            Messages.Message("DeltaV_SplitCancelled".Translate(), MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>残留块 = 图上控制域之外的基架物理四连通块（同归属断片与异主结构都算）。</summary>
        private static List<HashSet<IntVec3>> CollectResidualBlocks(Ship ship)
        {
            HashSet<IntVec3> domain = ShipRegistry.ControlDomain(ship);
            return ShipRegistry.ResidualBlocks(ship.map, domain);
        }

        private string BuildSplitSummary(UniverseManager universe, Ship ship, List<HashSet<IntVec3>> blocks)
        {
            var parts = new List<string>();
            foreach (HashSet<IntVec3> block in blocks)
            {
                SubstructureOwnership ownership = SubstructureOwnership.TryGet(ship.map);
                int owner = ownership?.OwnerAt(block.First()) ?? SubstructureOwnership.Unassigned;
                string ownerLabel = owner == SubstructureOwnership.Unassigned
                    ? "DeltaV_OwnerUnassigned".Translate()
                    : universe.LabelForOwner(owner);
                parts.Add("DeltaV_SplitBlockLine".Translate(ownerLabel, block.Count));
            }
            return string.Join("\n", parts);
        }

        /// <summary>
        /// 公共拆分（§8.1，controller 之外的公共机制）：每片残留块成为独立 SpaceObject——
        /// 新归属 ID + 「残骸 #N」名、新口袋图、位置/速度继承原整体对应点（含自旋切向，
        /// RigidPointPose）。断片上的备用台不因搬图自动升级主台；原船（主台所在侧）保留
        /// 原归属与名称。分离不施加弹开冲量、也不凭空停住。
        /// </summary>
        public void SplitResiduals(UniverseManager universe, Ship ship)
        {
            if (!ship.IsValid) return;
            SpaceObject frame = universe.FrameOf(ship.map);
            if (frame == null) return;
            foreach (HashSet<IntVec3> block in CollectResidualBlocks(ship))
            {
                TransferDomainToNewPocket(universe, ship.map, frame, block, null,
                    wreckOwnership: true, retainedOwnerId: 0);
            }
            if (SubstructureOwnership.TryGet(ship.map) != null)
            {
                // 拆分后重算质量/惯量/控制模型（新 MassReport 重扫自动生效）；
                // 陈旧油门清零防幽灵火焰。
                ShipPhysics.ClearThrottles(ship.map);
            }
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"structure #{ship.ownerId}: residual blocks split off before thrust");
        }

        /// <summary>
        /// 转移核心（脱离/拆分/合图共用）：把 sourceMap 上 cells 集合整体搬入一张新口袋图，
        /// 并创建/注册新容器。wreckOwnership = true 时新域领取残骸归属与默认名；
        /// retainedOwnerId &gt; 0 时保留原归属（脱离本船）。返回新容器（失败 null，图不动）。
        /// </summary>
        private SpaceObject TransferDomainToNewPocket(UniverseManager universe, Map sourceMap,
            SpaceObject sourceFrame, HashSet<IntVec3> cells, Building mainConsole,
            bool wreckOwnership, int retainedOwnerId)
        {
            Faction faction = mainConsole != null ? mainConsole.Faction : null;
            Gravship gravship = GravshipInterop.CaptureDomain(sourceMap, cells, mainConsole, faction);
            if (gravship == null)
            {
                Log.Error("[DeltaV] Domain capture failed; transfer aborted.");
                return null;
            }
            Vector2 hullCenter = SpaceTransform.BoundsCenterOfCells(cells);
            float consoleAngle = mainConsole != null
                ? ShipOrientation.AngleFromRot4(mainConsole.Rotation)
                : 0f;
            // 位姿/速度继承：宿主刚体上船体中心点的完整位姿（含 v+ω×r 与自旋切向）。
            SpacePose launchPose = SpaceTransform.RigidPointPose(sourceFrame.pose,
                SpaceTransform.MapToShipLocal(sourceMap, hullCenter), consoleAngle);
            // 主台在口袋图规范到 East（局部 +X），避免往返时把降落旋转重复计入；
            // 残片无主台，保持 North（σ=0 = 地图帧对齐，与 launchPose 的 0 角一致）。
            gravship.Rotation = mainConsole != null
                ? ShipOrientation.PlacementRotation(mainConsole.Rotation, Rot4.East)
                : Rot4.North;
            if (!ShipPocketMapFactory.TryGenerateForGravship(gravship, universe,
                out Map pocketMap, out IntVec3 pocketRoot))
            {
                Log.Error("[DeltaV] Failed to generate replacement pocket map; transfer aborted "
                          + "(source untouched before placement).");
                return null;
            }
            int ownerId;
            if (wreckOwnership)
            {
                ownerId = universe.AllocateOwnerId();
                universe.NoteStructureName(ownerId, universe.NewWreckName(ownerId));
            }
            else
            {
                ownerId = retainedOwnerId;
            }
            GravshipInterop.StripDomain(gravship, sourceMap);
            GravshipInterop.PlaceIntoMap(gravship, pocketRoot, pocketMap, ownerId);
            var container = new SpaceObject(universe.AllocateOwnerId(), pocketMap, launchPose);
            universe.RegisterObject(container);
            // 源图旧域整清：基架已随捕获剥离，残留归属值全是陈旧数据（按 ID 清不误伤他主域）。
            SubstructureOwnership.TryGet(sourceMap)?.ClearOwner(ownerId);
            PlanetGlobeRenderer.InvalidateSilhouette(sourceMap);
            HostColonistBarVisibility.NotifyDockingStateChanged();
            ShipPhysics.ClearCache();
            return container;
        }

        // ---- 战术对接（移动 → 固定，合图 §6）----

        public void BeginDocking(Ship ship)
        {
            if (DockingFlowBlocked(ship)) return;
            List<(SpaceObject target, float distance)> candidates = CollectLandableTargets(universe, ship);
            if (candidates.Count == 0)
            {
                // 深空固定被拒绝（星区架构 §7）：不再凭空生成空画布；目标尚未物化时
                // 继续接近，进入 activation radius 加载后即可正常降落（软限制）。
                Messages.Message("DeltaV_DockFailNoTarget".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (candidates.Count == 1)
            {
                BeginDockingTargeted(ship, candidates[0].target);
                return;
            }
            // 多目标投影重叠（拥挤环境）时按外壳间距排序交给玩家选择（2026-09-24）。
            // 构建顺序即距离序：FloatMenu 构造器对同优先级稳定排序，保持插入序。
            var options = new List<FloatMenuOption>(candidates.Count);
            foreach ((SpaceObject target, float distance) in candidates)
            {
                SpaceObject captured = target;
                string label = distance < float.MaxValue
                    ? "DeltaV_DockTargetEntry".Translate(target.label, distance.ToString("0.#"))
                    : target.label;
                options.Add(new FloatMenuOption(label, delegate { BeginDockingTargeted(ship, captured); }));
            }
            Find.WindowStack.Add(new FloatMenu(options, "DeltaV_DockTargetMenuTitle".Translate()));
        }

        /// <summary>菜单选择 / 单候选直入的公共入口：重跑门禁与目标资格后进入残留确认。</summary>
        private void BeginDockingTargeted(Ship ship, SpaceObject target)
        {
            if (DockingFlowBlocked(ship)) return;
            // FloatMenu 不暂停：菜单打开期间对象可移动/失效，点击时按同一资格口径复核。
            if (target == null || target.map == null || target.map.Disposed
                || target == universe.FrameOf(ship.map) || !IsLandableObject(target)
                || !ShipCenterInsideProjection(target, universe.FrameOf(ship.map).pose.position))
            {
                Messages.Message("DeltaV_DockFailNoTarget".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            // 源图残留处置的事前确认（§6 方案建议）：口袋图上控制域之外的断连结构 /
            // Pawn / 物品将随源图处置——先列清单确认，不在消费式捕获中途询问。
            List<Thing> leftovers = CollectSourceLeftovers(ship.map, ShipRegistry.ControlDomain(ship));
            if (leftovers.Count > 0)
            {
                string summary = BuildLeftBehindSummary(ship.map, leftovers);
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "DeltaV_DockSourceLeftoverConfirm".Translate(
                        universe.LabelForOwner(ship.ownerId), summary),
                    () => BeginDockingConfirmed(universe, ship, target, leftovers),
                    null, destructive: true));
                return;
            }
            BeginDockingConfirmed(universe, ship, target, null);
        }

        /// <summary>对接发起门禁（编排入口与目标入口共用）。</summary>
        private bool DockingFlowBlocked(Ship ship)
        {
            if (universe == null || !ship.IsValid || !ShipRegistry.IsFreeFlight(ship)
                || IsDockingFlowShip(ship) || universe.FrameOf(ship.map) == null) return true;
            if (Find.GravshipController == null || Find.GravshipController.LandingAreaConfirmationInProgress
                || Find.GravshipController.IsGravshipTravelling || Current.Game.Gravship != null
                || universe.ShipTransitions.pendingShip.IsValid)
            {
                Messages.Message("DeltaV_DockFailBusy".Translate(), MessageTypeDefOf.RejectInput, false);
                return true;
            }
            if (!OrbitSphere.Valid)
            {
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                return true;
            }
            return false;
        }

        private void BeginDockingConfirmed(UniverseManager universe, Ship ship, SpaceObject target,
            List<Thing> confirmedLeftovers)
        {
            pendingLeftoverThingIds = confirmedLeftovers?.Select(t => t.thingIDNumber).ToList();
            pendingSelectionWasManual = universe.Pilot.GetControlState(ship) == ShipControlState.Manual;
            float snappedHeading = SnapAngle(ShipRegistry.HeadingAngle(ship));
            BeginOriginalLanding(universe, ship, target, target.map, snappedHeading);
        }

        /// <summary>
        /// 可降落目标枚举，按可见船壳距离升序返回（2026-09-24 修订：多目标投影重叠时
        /// 交给玩家按距离选择；**自身容器显式排除**——旧单选实现漏排时自己恒以凸包
        /// 包含距离 0 获胜，任何真实目标不可能被选中）。资格门槛：飞船中心在对象内部
        /// 地图投影内（任何已物化对象——含移动中的有动力飞船；空白空画布不可固定）；
        /// 距离 = 双方可渲染船壳（凸包）的最近距离，无船壳数据的目标记无穷大排尾。
        /// </summary>
        private List<(SpaceObject target, float distance)> CollectLandableTargets(
            UniverseManager universe, Ship ship)
        {
            SpaceObject ownContainer = universe.FrameOf(ship.map);
            var candidates = new List<(SpaceObject target, float distance)>();
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == ownContainer) continue;
                if (obj.map == null || !IsLandableObject(obj)) continue;
                if (!ShipCenterInsideProjection(obj, ownContainer.pose.position)) continue;
                candidates.Add((obj, VisibleShellDistance(universe, ship, obj)));
            }
            candidates.Sort((a, b) => a.distance.CompareTo(b.distance));
            return candidates;
        }

        /// <summary>
        /// 玩家船壳与目标可渲染船壳的最近距离（目标切空间系中计算；数据流与外壳渲染
        /// 同源——船壳格列表 + 双方位姿，凸包后求距离）。船壳口径 = 各自图上全部基架
        /// 格（多船图取整图凸包——容器承载全部成员），POI = 统一结构外观可见内容格。
        /// </summary>
        private float VisibleShellDistance(UniverseManager universe, Ship ship, SpaceObject target)
        {
            Map playerMap = ship.map;
            Map targetMap = target.map;
            if (playerMap == null || targetMap == null || targetMap.Disposed) return float.MaxValue;
            SpaceObject playerFrame = universe.FrameOf(playerMap);
            SpaceObject targetFrame = universe.FrameOf(targetMap);
            if (playerFrame == null || targetFrame == null) return float.MaxValue;

            var targetPoints = new List<Vector2>();
            SubstructureOwnership targetOwnership = SubstructureOwnership.TryGet(targetMap);
            if (targetOwnership != null && targetOwnership.HasAnySubstructure())
            {
                foreach (IntVec3 cell in CellRect.WholeMap(targetMap))
                {
                    if (targetMap.terrainGrid.FoundationAt(cell)?.IsSubstructure == true)
                    {
                        targetPoints.Add(SpaceTransform.MapToShipLocal(targetMap,
                            new Vector2(cell.x + 0.5f, cell.z + 0.5f)));
                    }
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

            var playerPoints = new List<Vector2>();
            foreach (IntVec3 cell in ShipRegistry.ControlDomain(ship))
            {
                Vector2 local = SpaceTransform.MapToShipLocal(playerMap,
                    new Vector2(cell.x + 0.5f, cell.z + 0.5f));
                playerPoints.Add(SpaceTransform.UniverseToFocused(targetFrame,
                    SpaceTransform.ShipLocalToUniverse(playerFrame.pose, local)));
            }
            return PlanarGeometry.HullDistance(
                PlanarGeometry.ConvexHull(playerPoints), PlanarGeometry.ConvexHull(targetPoints));
        }

        /// <summary>
        /// 可对接目标资格（2026-09-21 阶段4 修订 + §5.2）：任何已物化对象均可——有动力
        /// 飞船同样可对接；与无主台残骸对象合图后 A 仍自由飞行（残骸不构成对接）。
        /// 空白空画布保留不可固定。
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

        private void BeginOriginalLanding(UniverseManager universe, Ship ship,
            SpaceObject target, Map targetMap, float snappedHeading)
        {
            universe.ShipTransitions.pendingShip = ship;
            // 选点零消费（2026-09-21 阶段4 定案）：预览用空洞 Gravship——只读填充原版
            // 选点/合法性/禁触链路所需字段；真实搬运唯一发生在完成时刻（与移除同一
            // 调用栈）——对接期间的建造/扩建全部随完成时捕获带走。
            Gravship gravship = GravshipInterop.BuildPreview(ShipRegistry.ControlDomain(ship),
                ship.console.parent as Building);
            if (gravship == null)
            {
                universe.ShipTransitions.pendingShip = Ship.Invalid;
                universe.ShipTransitions.pendingLeftoverThingIds = null;
                Log.Error($"[DeltaV] Docking structure #{ship.ownerId}: preview gravship build failed; aborting.");
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
                : OrbitSphere.TileFromDirection(universe.FrameOf(ship.map).pose.position);
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
            IntVec3 initialCell = InitialLandingCell(universe, ship, target, targetMap, gravship, desiredFacing);
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                CameraJumper.TryJump(initialCell.IsValid ? initialCell : targetMap.Center, targetMap,
                    CameraJumper.MovementMode.Cut);
                Find.DesignatorManager.Select(designator);
                // 初始落点只是原版选点的起点。已按脚印 clamp，但图比船小时仍可能
                // 不合法：只在原版门禁放行时自动放置，否则留给玩家手动选点。
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
                $"structure #{ship.ownerId}: docking site selection on '{target.label}' (zero-consumption preview); initial cell={initialCell}, heading={snappedHeading * Mathf.Rad2Deg:F0} deg");
        }

        /// <summary>
        /// 悬停位置 → 目标对象内部投影上的初始落点：飞船中心映射为目标图格子，再按
        /// gravship 尺寸/旋转折算放置 root，再编码成 designator 输入格。
        /// </summary>
        private IntVec3 InitialLandingCell(UniverseManager universe, Ship ship, SpaceObject target, Map targetMap,
            Gravship gravship, Rot4 facing)
        {
            Vector2 mapPoint = SpaceTransform.UniverseToMapPoint(target, targetMap,
                universe.FrameOf(ship.map).pose.position);
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
        /// 落点合法性（含 Delta-V 禁触规则）失败时的环形外扩搜索（与旧实现一致，
        /// 经原版 CanDesignateCell 全门禁）。
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

        internal bool IsDockingFlowShip(Ship ship)
        {
            return ship.IsValid && (pendingShip.ownerId == ship.ownerId
                || universe?.Approach.FlowShip.ownerId == ship.ownerId);
        }

        /// <summary>选点/进场流程中的战术降落船。</summary>
        internal Ship PendingDockingShip(UniverseManager universe)
        {
            return universe?.ShipTransitions.pendingShip ?? Ship.Invalid;
        }

        /// <summary>
        /// 对接计划取消时的快照丢弃（LandingApproachController.Cancel/Abort 调用）：
        /// 清 pending 状态；计划为运行时数据，取消后船继续自由飞行，可重新发起对接。
        /// </summary>
        internal void DiscardPendingDocking(UniverseManager universe)
        {
            if (universe != null)
            {
                universe.ShipTransitions.pendingShip = Ship.Invalid;
                universe.ShipTransitions.pendingLeftoverThingIds = null;
            }
        }

        /// <summary>已放入目标图、等待收尾（源图处置）的对接完成窗口。</summary>
        internal Ship LandedShipAwaitingPocketCleanup(UniverseManager universe)
        {
            return universe?.ShipTransitions.landedShipAwaitingCleanup ?? Ship.Invalid;
        }

        /// <summary>
        /// 落点反向映射——**落点注册的唯一计算点**（2026-09-21 用户定案）：只在选点确认
        /// （LandingApproachController.TryConsume）调用一次，结果存入 DockingPlan；此后
        /// 幽灵船/对齐判据一律读注册值，**不得二次计算**。
        /// 角度 = 宿主帧角 ⊕ 落地主台朝向（preview 的 PlacementData）；dockedOrigin =
        /// 放置 root + 旋转后的「口袋图几何中心 - 域包围盒 min」偏移（船位姿锚的落点）。
        /// </summary>
        internal bool ComputeLandingPose(UniverseManager universe, Ship ship, Gravship gravship, Map map,
            IntVec3 landingPos, Rot4 landingRot, out Vector3 direction, out float angle,
            out Vector2 dockedOrigin)
        {
            SpaceObject hostFrame = universe?.FrameOf(map);
            if (hostFrame == null || !ship.IsValid)
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
                    ShipRegistry.HeadingAngle(ship) - hostAngle));
            angle = SpaceTransform.WrapAngle(hostAngle + ShipOrientation.AngleFromRot4(landedFacing));

            // 停放锚点唯一入口（SpaceTransform.cs「dockedOrigin 的唯一来源」；2026-09-24
            // 事故：此处曾手写「中心系恒零 − 地图系原点」的混用偏移，丢口袋图几何中心
            // 项致幽灵船/判据整体偏约半张口袋图）。pocketRoot = preview 原点（域包围盒
            // min，GravshipInterop.BuildPreview 同一约定）。
            dockedOrigin = SpaceTransform.ShipOriginFromPlacementRoot(
                ship.map, gravship.originalPosition, landingPos, landingRot);
            direction = SpaceTransform.MapPointToUniverse(hostFrame, map, dockedOrigin);
            return true;
        }

        /// <summary>
        /// 完成时刻的真实捕获（**唯一消费点**）：按当前控制域捕获（LandingApproachController
        /// 完成时调用，与移除/放置同一调用栈；对接期间的建造/扩建全部随本次捕获带走）。
        /// </summary>
        internal Gravship CaptureShipForCompletion(UniverseManager universe, Ship ship)
        {
            if (!ship.IsValid) return null;
            HashSet<IntVec3> domain = ShipRegistry.ControlDomain(ship);
            if (domain.Count == 0) return null;
            return GravshipInterop.CaptureDomain(ship.map, domain, ship.console.parent as Building,
                ShipRegistry.FactionOf(ship));
        }

        /// <summary>
        /// 战术降落的真转移（LandingApproachController 完成时调用，与捕获同一调用栈）：
        /// 剥离源图 → 直接放入目标图（原版放置收尾；落点 root/旋转与盖章归属全部读
        /// DockingPlan 注册值）。**不重放原版 InitiateLanding**——不进 cutscene/mask/
        /// LandingEnded 链路（§6 六步：完成时捕获 → 转移 → 发布新关系 → 清理旧容器）。
        /// </summary>
        internal bool CompleteTransferIntoHost(UniverseManager universe, Ship ship, Gravship gravship,
            Map targetMap, IntVec3 landingRoot, Rot4 landingRot, int structureId)
        {
            if (gravship == null || targetMap == null) return false;
            Map pocketMap = ship.map;
            if (pocketMap == null || pocketMap == targetMap) return false;
            // 源图残留复核（§6：确认后新增的残留不静默处置——中止并要求重新发起）。
            List<Thing> leftovers = CollectSourceLeftovers(pocketMap, ShipRegistry.ControlDomain(ship));
            List<int> confirmed = pendingLeftoverThingIds ?? new List<int>();
            foreach (Thing leftover in leftovers)
            {
                if (!confirmed.Contains(leftover.thingIDNumber))
                {
                    Messages.Message("DeltaV_DockSourceLeftoverChanged".Translate(),
                        MessageTypeDefOf.RejectInput, false);
                    return false;
                }
            }
            pendingDockingSourceMap = pocketMap;
            GravshipInterop.StripDomain(gravship, pocketMap);
            GravshipInterop.PlaceIntoMap(gravship, landingRoot, targetMap, structureId);
            landedShipAwaitingCleanup = ship;
            pendingShip = Ship.Invalid;
            pendingLeftoverThingIds = null;
            return true;
        }

        /// <summary>
        /// 对接完成收尾（CompleteTransferIntoHost 成功后、帧边界执行）：处置源口袋图
        /// 残留（已确认清单内的滞留 Pawn 遗弃 + 遗留基架转无主残骸）→ 删除源口袋图与
        /// 源容器 → 通知对接口铺设通道。A 不保留无图子对象（§6 步骤 4）。
        /// </summary>
        internal void FinishDocking(UniverseManager universe, Ship ship)
        {
            // 完成转移后船体已在宿主图（console.parent.Map = 宿主图），此处的
            // landedShipAwaitingCleanup 是转移前快照，其 Ship.IsValid（含「驾驶台
            // 在册于快照地图」校验）必然为假——完成期守卫只做身份匹配，不校验旧
            // 快照活性（2026-09-24 事故：旧守卫用 IsValid 致本方法静默早退——源图
            // 不删、聚焦不切、通道不铺、成功消息不发）。
            if (landedShipAwaitingCleanup.ownerId <= 0 || landedShipAwaitingCleanup.ownerId != ship.ownerId)
            {
                return;
            }
            landedShipAwaitingCleanup = Ship.Invalid;
            Map sourceMap = pendingDockingSourceMap;
            pendingDockingSourceMap = null;
            // 完成时的聚焦/相机交接（2026-09-24）：完成前聚焦在源口袋图，源图销毁会让
            // 原版 CurrentMap 回退到 maps[0]（通常是地表图）。玩家视点按宇宙参考系连续
            // 换算到宿主图（同外壳点击聚焦口径，SetFocusedWithCamera 单实现）；玩家中途
            // 看向别处（CurrentMap ≠ 源图）则不打扰。换算需在源容器注销前完成。
            SpaceObject handoffHost = null;
            Vector2 handoffPoint = default;
            float handoffZoom = 0f;
            if (sourceMap != null && !sourceMap.Disposed && Find.CurrentMap == sourceMap)
            {
                Ship settled = ShipRegistry.ShipByOwner(ship.ownerId);
                Map hostMap = settled.IsValid ? settled.map : null;
                SpaceObject hostFrame = hostMap != null && hostMap != sourceMap
                    ? universe.FrameOf(hostMap) : null;
                SpaceObject pocketFrame = universe.FrameOf(sourceMap);
                if (hostFrame != null && pocketFrame != null)
                {
                    Vector3 viewCenter = Find.CameraDriver.CurrentViewRect.CenterVector3;
                    Vector3 universeDir = SpaceTransform.MapPointToUniverse(pocketFrame, sourceMap,
                        new Vector2(viewCenter.x, viewCenter.z));
                    handoffPoint = SpaceTransform.UniverseToMapPoint(hostFrame, hostMap, universeDir);
                    handoffZoom = Find.CameraDriver.RootSize;
                    handoffHost = hostFrame;
                }
            }
            if (sourceMap != null && !sourceMap.Disposed && Find.Maps.Contains(sourceMap))
            {
                SpaceObject pocketContainer = universe.SpaceObjectOnMap(sourceMap);
                SpacePose sourcePose = pocketContainer?.pose ?? new SpacePose(Vector3.right, 0f);
                AbandonPawnsOn(sourceMap);
                ConvertLeftoverSubstructureToWreck(universe, sourceMap, sourcePose);
                MapOverlayRenderer.Release(sourceMap);
                PlanetGlobeRenderer.InvalidateSilhouette(sourceMap);
                if (pocketContainer != null)
                {
                    universe.UnregisterObject(pocketContainer);
                }
                // 先交接再销毁：CurrentMap 已在宿主图上，maps[0] 回退不会触发。
                if (handoffHost != null)
                {
                    universe.SetFocusedWithCamera(handoffHost, handoffPoint, handoffZoom);
                }
                universe.Lifecycle.DestroyPocketMap(sourceMap);
                ShipPhysics.ClearCache();
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    "docking source pocket map removed after merge (leftovers disposed per confirmation)");
            }
            HostColonistBarVisibility.NotifyDockingStateChanged();
            Building_DockingPort.NotifyLandingSettled();
            Messages.Message("DeltaV_DockSuccess".Translate(universe.LabelForOwner(ship.ownerId)),
                MessageTypeDefOf.PositiveEvent, false);
        }

        /// <summary>完成时刻记录的源口袋图（CompleteTransferIntoHost 置位，FinishDocking 消费）。</summary>
        internal Map pendingDockingSourceMap;

        internal List<int> pendingLeftoverThingIds;

        /// <summary>
        /// 源口袋图上控制域之外的滞留内容（§6 残留处置清单）：Pawn、建筑与物品
        /// （Mote/Filth 等非实体不计）。
        /// </summary>
        private static List<Thing> CollectSourceLeftovers(Map map, HashSet<IntVec3> domain)
        {
            var leftovers = new List<Thing>();
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (!thing.Spawned || thing is Mote || thing is Filth || thing is Gas
                    || domain.Contains(thing.Position))
                {
                    continue;
                }
                if (thing is Pawn || thing is Building || thing.def.category == ThingCategory.Item)
                {
                    leftovers.Add(thing);
                }
            }
            return leftovers;
        }

        private static string BuildLeftBehindSummary(Map map, List<Thing> leftovers)
        {
            var parts = new List<string>();
            foreach (IGrouping<string, Thing> group in leftovers.GroupBy(t => t.LabelCap))
            {
                parts.Add($"{group.Key} × {group.Count()}");
            }
            return string.Join("\n", parts);
        }

        // ---- 脱离（固定 → 移动，拆图 §7）----

        /// <summary>
        /// 已登记船的脱离入口（面板/驾驶台按钮）。
        /// </summary>
        public void BeginFreeFlight(UniverseManager universe, Ship ship)
        {
            if (universe == null || !ship.IsValid) return;
            Map sourceMap = ship.map;
            if (sourceMap == null || !Find.Maps.Contains(sourceMap) || universe.FrameOf(sourceMap) == null)
            {
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (!FreeFlightBootstrap.IsZeroGravity(sourceMap))
            {
                // 重力门禁：地表/重力图只能逆重战略发射（§5.2「地面上的 A」行）。
                Messages.Message("DeltaV_PilotGravityRestricted".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            HashSet<IntVec3> domain = ShipRegistry.ControlDomain(ship);
            if (domain.Count == 0
                || !ThrusterPower.HasWorkingThruster(sourceMap, domain))
            {
                Messages.Message("DeltaV_LaunchFailNoThruster".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (FreeFlightTransitionPending() || Find.GravshipController == null
                || Find.GravshipController.LandingAreaConfirmationInProgress
                || Find.GravshipController.IsGravshipTravelling || Current.Game.Gravship != null)
            {
                Messages.Message("DeltaV_DockFailBusy".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            // 断裂 + 留置汇总确认（§7 步骤 2、§9 方案建议）：同归属断连基架（将留在宿主图）
            // 与对接通道上的 Pawn/物品（通道收回后原格留置）一次列清；两者皆无的正常
            // 脱离不新增确认窗。
            List<Thing> channelLeftovers = Building_DockingPort.CollectChannelLeftBehind(sourceMap, domain);
            List<HashSet<IntVec3>> fragments = ShipRegistry.ResidualBlocks(sourceMap, domain)
                .Where(block =>
                {
                    SubstructureOwnership ownership = SubstructureOwnership.TryGet(sourceMap);
                    return ownership != null && ownership.OwnerAt(block.First()) == ship.ownerId;
                })
                .ToList();
            if (channelLeftovers.Count > 0 || fragments.Count > 0)
            {
                var summary = new List<string>();
                if (fragments.Count > 0)
                {
                    summary.Add("DeltaV_UndockFragmentsLine".Translate(fragments.Count));
                }
                if (channelLeftovers.Count > 0)
                {
                    summary.Add("DeltaV_LeftBehindLine".Translate(BuildLeftBehindSummary(sourceMap, channelLeftovers)));
                }
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "DeltaV_UndockConfirmText".Translate(string.Join("\n", summary)),
                    () => ExecuteUndock(universe, ship),
                    null, destructive: true));
                return;
            }
            ExecuteUndock(universe, ship);
        }

        /// <summary>未形成船的驾驶台入口（原 FreeFlightBootstrap 场景）：先认领身份再脱离。</summary>
        public void BeginFreeFlightFromConsole(UniverseManager universe, Building console)
        {
            if (universe == null || console == null || !console.Spawned) return;
            CompDeltaVPilotConsole comp = console.TryGetComp<CompDeltaVPilotConsole>();
            if (comp == null) return;
            if (!FreeFlightBootstrap.IsZeroGravity(console.Map))
            {
                Messages.Message("DeltaV_PilotGravityRestricted".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (!comp.isMain)
            {
                comp.MakeMainConsole();
            }
            Ship ship = comp.Ship;
            if (!ship.IsValid)
            {
                Messages.Message("DeltaV_PilotNeedShipFocus".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            BeginFreeFlight(universe, ship);
        }

        /// <summary>脱离执行体（确认弹窗之后的部分；无引擎搬运，不走原版起飞管线）。</summary>
        private void ExecuteUndock(UniverseManager universe, Ship ship)
        {
            Map sourceMap = ship.map;
            SpaceObject sourceFrame = universe.FrameOf(sourceMap);
            if (sourceFrame == null)
            {
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            HashSet<IntVec3> domain = ShipRegistry.ControlDomain(ship);
            if (domain.Count == 0) return;

            // 收通道 = 脱离事务的一部分（§9）：只收回与本次脱离相关的通道（端口在
            // 脱离域上，或通道格与脱离域四邻接）；B、C 之间无关通道保留。通道上的
            // Pawn/物品原格留置（不自动迁往安全格）。
            Building_DockingPort.RetractChannelsFor(sourceMap, domain);

            universe.Pilot.Exit(universe);
            universe.Autopilot.Cancel(ship);

            SpaceObject container = TransferDomainToNewPocket(universe, sourceMap, sourceFrame,
                domain, ship.console.parent as Building, wreckOwnership: false, retainedOwnerId: ship.ownerId);
            if (container == null)
            {
                Messages.Message("DeltaV_DockFailBusy".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            // 转移后 ship 的主台已在新图上；重解析句柄。
            Ship resolved = ShipRegistry.ShipByOwner(ship.ownerId);
            WorldShipMarkerRenderer.RebuildFor(resolved.IsValid ? resolved : ship);
            universe.SetFocused(container);
            if (resolved.IsValid)
            {
                CameraJumper.TryJump(resolved.console.parent.Position, resolved.map,
                    CameraJumper.MovementMode.Cut);
                if (universe.Pilot.CanPlayerControl(resolved))
                {
                    universe.Pilot.Enter(universe, resolved);
                }
            }
            QueueEmptyDepartedMapCleanup(universe, sourceMap);
            Messages.Message("DeltaV_FreeFlightSuccess".Translate(universe.LabelForOwner(ship.ownerId)),
                MessageTypeDefOf.PositiveEvent, false);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"structure #{ship.ownerId} undocked into new container #{container.objectId}; heading={container.pose.angle * Mathf.Rad2Deg:F0} deg");
        }

        /// <summary>
        /// 延后到帧边界清点离港地图：此时已完全脱离调用栈，DeinitAndRemoveMap
        /// 不会踩到上层仍在使用的引用。
        /// </summary>
        private void QueueEmptyDepartedMapCleanup(UniverseManager universe, Map map)
        {
            if (map == null) return;
            LongEventHandler.ExecuteWhenFinished(delegate { HandleDepartedSourceMap(universe, map); });
        }

        /// <summary>
        /// 脱离与读档清扫的源图善后：**worldtile 图不受影响**——保留，去留交给锚对象
        /// 配置与距离生命周期；非 worldtile 图空白则立刻删除（遗留基架转无主残骸），
        /// 非空白交给距离生命周期。不按对象身份特判。
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
            if (SpaceLifecycleService.IsUniverseHost(sourceMap)) return;
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

        // ---- 原版战略发射适配（§12：引擎范围例外退役；星区离场标准流程保留）----

        /// <summary>
        /// 口袋图起飞的战略源地址（星区架构 §6/§7/§9）：口袋图无 tile，原版起飞管线中
        /// 一切以源图 tile 为输入的环节改接该地址。真实轨道图原样返回自身 tile。
        /// </summary>
        internal PlanetTile StrategicSourceAddress(UniverseManager universe, Map map)
        {
            if (map != null && map.Tile.Valid) return map.Tile;
            if (!OrbitSphere.Valid) return PlanetTile.Invalid;
            SpaceObject owner = universe?.SpaceObjectOnMap(map);
            if (owner == null)
            {
                Ship ship = map != null ? ShipRegistry.ShipsOnMap(map).FirstOrDefault() : Ship.Invalid;
                owner = ship.IsValid ? universe.FrameOf(ship.map) : null;
            }
            if (owner == null) return PlanetTile.Invalid;
            return OrbitSphere.TileFromDirection(owner.pose.position);
        }

        /// <summary>
        /// InitiateTakeoff Postfix 的执行体：源图与 Delta-V 相关时的原版战略起飞适配。
        /// 本 mod 自己的脱离流程不经过这里（直接转移，不走原版管线）。
        /// - 口袋图源：原版记录的 takeoffTile 无效，改写为战略源地址；
        /// - 源图保留策略统一交 Delta-V：先保护，起飞完成（TravelTo 放行）后由
        ///   HandleStrategicTravelDeparture 统一执行；
        /// - 发射源 = 发射船的自有口袋容器（图上唯一船）时，记录该容器供离场销毁。
        /// </summary>
        internal void HandleVanillaTakeoffFromDeltaVSource(Building_GravEngine engine,
            WorldComponent_GravshipController controller)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (engine?.Map == null || universe == null) return;
            Map sourceMap = engine.Map;
            bool pocketSource = !sourceMap.Tile.Valid;
            // 只介入 Delta-V 语境的源图（口袋图或轨道层真实图）；地表 gravship 起飞
            // 完全走原版行为，不因本 mod 改变保留策略。
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
                // 发射船 = 引擎所在归属的船；其自有口袋容器 = 图上唯一船且图是口袋容器。
                SubstructureOwnership ownership = SubstructureOwnership.TryGet(sourceMap);
                int engineOwner = ownership?.OwnerAt(engine.Position) ?? SubstructureOwnership.Unassigned;
                List<Ship> shipsOnMap = ShipRegistry.ShipsOnMap(sourceMap);
                Ship launcher = shipsOnMap.FirstOrDefault(s => s.ownerId == engineOwner);
                SpaceObject sourceContainer = universe.SpaceObjectOnMap(sourceMap);
                flow.pendingStrategicOwnPocket = launcher.IsValid && shipsOnMap.Count == 1
                    && sourceContainer != null && !(sourceContainer is PoiState)
                    ? sourceContainer
                    : null;
                if (launcher.IsValid)
                {
                    universe.Pilot.Exit(universe);
                    universe.Autopilot.Cancel(launcher);
                    HostColonistBarVisibility.NotifyDockingStateChanged();
                }
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"vanilla strategic takeoff from {(pocketSource ? "pocket" : "tile")} map: " +
                    $"source address sector {flow.pendingStrategicSourceAddress}, own pocket recorded={flow.pendingStrategicOwnPocket != null}");
            }
        }

        /// <summary>
        /// 原版战略起飞放行 TravelTo 后的星区离场善后（Patch_GravshipUtility_TravelTo
        /// 的 Postfix 调用），预存的源图/战略地址/位姿经帧边界延迟执行（时序约束：
        /// 必须排在 TravelTo 放行后，排队过早会卡守卫——旧 bug 勿回退）。
        ///
        /// 统一标准流程（2026-09-20 用户决策，§19 保留）：
        /// ① 发射船的自有飞行口袋容器 → **总是销毁口袋图**（遗弃滞留者、遗留基架转无主
        ///    残骸——整船捕获已带走船体，容器无意义）；
        /// ② 归属星区（战略源地址 tile）的 worldtile 图 → **按逆重锚决定去留**
        ///    （无锚 → 原版 AbandonMap 弃置 + 基架转残骸；有锚/有其他停靠船保留），
        ///    任意源图类型统一执行；无 worldtile 图则跳过；
        /// ③ 星区内**其余 SpaceObject 地图** → 一律按超删除距离处理（休眠 + 快照收纳/
        ///    删除，守卫照常生效）——旅行期间距离评估因无玩家参考点整体跳过，离场时刻
        ///    必须显式执行。
        /// </summary>
        internal void HandleStrategicTravelDeparture()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            ShipFlightTransitionService flow = universe?.ShipTransitions;
            Map sourceMap = flow?.pendingStrategicSourceMap;
            PlanetTile address = flow?.pendingStrategicSourceAddress ?? PlanetTile.Invalid;
            SpacePose? pose = flow?.pendingStrategicSourcePose;
            SpaceObject ownPocket = flow?.pendingStrategicOwnPocket;
            if (flow != null)
            {
                flow.pendingStrategicSourceMap = null;
                flow.pendingStrategicSourceAddress = PlanetTile.Invalid;
                flow.pendingStrategicSourcePose = null;
                flow.pendingStrategicOwnPocket = null;
            }
            if (sourceMap == null) return;
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                if (universe == null || sourceMap.Disposed || !Find.Maps.Contains(sourceMap)) return;

                // ① 发射船的自有飞行口袋容器（战略发射源）。
                if (ownPocket != null && ownPocket.map == sourceMap && !sourceMap.Disposed)
                {
                    // 焦点还在源图上是战略航行的常态。先显式置空（战略航行 = 原版
                    // 「无当前图」形态），不能交给 DeinitAndRemoveMap 的 maps[0] 轮盘。
                    if (sourceMap == Find.CurrentMap)
                        Current.Game.CurrentMap = null;
                    AbandonPawnsOn(sourceMap);
                    ConvertLeftoverSubstructureToWreck(universe, sourceMap,
                        pose ?? new SpacePose(Vector3.right, 0f));
                    MapOverlayRenderer.Release(sourceMap);
                    PlanetGlobeRenderer.InvalidateSilhouette(sourceMap);
                    universe.Lifecycle.DestroyPocketMap(sourceMap);
                    universe.UnregisterObject(ownPocket);
                    ShipPhysics.ClearCache();
                    DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                        "flight pocket map removed after strategic takeoff (leftovers converted)");
                }

                // ② 归属星区 worldtile 图：按锚配置决定去留。将弃置前先放焦点（同 ①
                // 理由）；有锚保留时不动焦点（原版锚定前哨）。
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

                // ③ 星区其余 SpaceObject 地图：按超删除距离休眠 + 收纳。源图即将打包时
                // 先放焦点；作为锚保留 worldtile 图的源图不在此列。
                if (ownPocket == null && sourceMap != sectorMap && sourceMap == Find.CurrentMap)
                    Current.Game.CurrentMap = null;
                universe.Lifecycle.PackDepartedSectorPois(address, sectorMap);
            });
        }

        /// <summary>
        /// 逆重起飞的弃置目标图（2026-09-19 用户方案）：源图有 worldtile 且在轨道层
        /// 即源图本身；口袋图起飞 = 战略源地址 tile 上的图（严格按所在星区）。返回
        /// null = 无弃置目标。
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

        /// <summary>该图将在逆重起飞后按无锚语义弃置（无锚、无其他船）。</summary>
        internal bool SectorMapWillBeAbandoned(UniverseManager universe, Map map)
        {
            return map != null && Find.Maps.Contains(map)
                && !map.listerThings.AnyThingWithDef(ThingDefOf.GravAnchor)
                && universe != null && ShipRegistry.ShipsOnMap(map).Count == 0;
        }

        /// <summary>
        /// 无锚弃置（星区标准流程第 ② 段，任意发射源统一）：图上无逆重锚 → 遗留基架
        /// 先转无主残骸，再走原版 AbandonMap（含滞留 Pawn 遗弃——发射链路的确认弹窗
        /// 已警告）。有锚保留（原版锚定前哨）。
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
            if (ShipRegistry.ShipsOnMap(map).Count > 0)
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{mapLabel}: unanchored abandon skipped (docked ship present)");
                return;
            }
            if (map.listerThings.AnyThingWithDef(ThingDefOf.GravAnchor))
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{mapLabel}: unanchored abandon skipped (grav anchor present)");
                return;
            }
            // 遗留基架转无主残骸：必须在删图前收快照。
            ConvertLeftoverSubstructureToWreck(universe, map,
                new SpacePose(OrbitSphere.TileCenterDirection(map.Tile), 0f));
            GravshipUtility.AbandonMap(map);
            ShipPhysics.ClearCache();
            PlanetGlobeRenderer.InvalidateSilhouette(map);
            DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                $"{mapLabel}: unanchored sector map abandoned after strategic takeoff");
        }

        /// <summary>
        /// 起飞后被遗留的基架不随弃置消失：删除前收整图结构快照（此刻捕获域已剥除，
        /// 收到的即遗留内容），在原图位姿生成无主残骸 POI（SnapshotPocket，接近后按
        /// 快照物化）。无遗留基架则不生成。残骸名 = 「残骸 #N」（N = 其自身归属 ID）。
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
            int wreckId = universe.AllocateOwnerId();
            string wreckName = universe.NewWreckName(wreckId);
            var wreck = new PoiState(wreckId, wreckName, pose.position, pose.angle, Rand.Int);
            universe.NoteStructureName(wreckId, wreckName);
            wreck.snapshot = PoiSnapshot.Capture(map);
            universe.RegisterObject(wreck);
            DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                $"{wreckName} created from leftover substructure ({wreck.snapshot.ThingCount} things)");
        }

        /// <summary>
        /// 口袋图删除前的滞留 Pawn 遗弃（照原版 GravshipUtility.AbandonMap 的 Pawn
        /// 循环：Lost 思想 + Notify_LeftBehind + 销毁）。
        /// </summary>
        private static void AbandonPawnsOn(Map map)
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
    }
}
