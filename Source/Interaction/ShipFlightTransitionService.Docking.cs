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
    /// <summary>唯一 Gravship 搬运链、真实出力前分离、原版战略发射适配与源图善后。</summary>
    internal sealed partial class ShipFlightTransitionService
    {
        private static readonly FieldInfo ControllerMapHasGravAnchor =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "mapHasGravAnchor");

        private static readonly FieldInfo ControllerTakeoffTile =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "takeoffTile");

        public bool FreeFlightTransitionPending()
        {
            return HasLandedTargetAwaitingCleanup;
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

        // ---- 出力前分离（§7 脱离 + §8 拆分统一；挂点 = 仅有的两个 Allocate 调用方）----

        /// <summary>
        /// 出力请求 gate（唯一入口；UniverseManager 手操路径与 ShipAutopilot 在施加推进器
        /// 出力之前调用）。「将要出力的船与其余部分分离」：自由飞行船 = 拆分方向（控制域
        /// 外残留块逐块独立成残骸容器，船留原图）；已有停靠或无基架残留内容时提取本船
        /// 控制域进入新口袋容器，宿主图与其余内容不动。返回 true = 本次允许出力；false = 本 tick
        /// 中止出力（待确认分离、分离已排队或门禁拒绝）。分离只在这里触发——纯惯性漂移
        /// 与被动阻力不经本入口。
        /// </summary>
        public bool RequestThrust(UniverseManager universe, PilotTarget target, bool playerInitiated)
        {
            if (!target.IsValid) return false;
            SpaceObject frame = universe.FrameOf(target.Map);
            if (!target.Vessel.CanDriveIn(frame) || frame.projectileRoleOverride) return false;
            if (!FreeFlightBootstrap.IsZeroGravity(universe, target.Map))
            {
                Messages.Message("DeltaV_PilotGravityRestricted".Translate(), MessageTypeDefOf.RejectInput, false);
                return false;
            }
            return target.Vessel.PrepareThrust(universe, playerInitiated);
        }

        internal bool RequestFoundationSeparation(UniverseManager universe, Ship ship, bool playerInitiated)
        {
            Map sourceMap = ship.map;
            // 确认窗已打开或分离已排队（游戏因 forcePause 暂停 / 帧边界未到）：等待结果。
            if (pendingSeparationOwnerIds.Contains(ship.ownerId))
            {
                return false;
            }

            if (!MustExtractOwnDomain(ship))
            {
                // 拆分方向：控制域外残留块（不分归属）必须离开本容器，否则会随船整体运动。
                List<HashSet<IntVec3>> blocks = CollectResidualBlocks(ship);
                if (blocks.Count == 0)
                {
                    return true;
                }
                if (!playerInitiated)
                {
                    // NPC（或无 controller 上下文）：直接执行同一公共拆分，不弹玩家确认（§8.2）。
                    QueueSeparation(universe, ship.ownerId);
                    return false;
                }
                PresentSeparationPrompt(universe, ship, "DeltaV_SplitConfirmText",
                    BuildSplitSummary(universe, ship, blocks));
                return false;
            }

            // 脱离方向：本船控制域搬出宿主图（2026-09-24 起「脱离停靠」不再是独立命令，
            // 门禁沿用旧脱离流程：零重力、可用推进器、流程繁忙互斥）。
            HashSet<IntVec3> domain = ShipRegistry.ControlDomain(ship);
            if (domain.Count == 0 || !((ShipBase)ship).BuildThrusters().Any(t => t.operational && t.thrust > 0f))
            {
                Messages.Message("DeltaV_LaunchFailNoThruster".Translate(), MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (FreeFlightTransitionPending() || Find.GravshipController == null
                || Find.GravshipController.LandingAreaConfirmationInProgress
                || Find.GravshipController.IsGravshipTravelling || Current.Game.Gravship != null)
            {
                Messages.Message("DeltaV_DockFailBusy".Translate(), MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (!playerInitiated)
            {
                QueueSeparation(universe, ship.ownerId);
                return false;
            }
            // 断裂 + 留置汇总确认（§7 步骤 2、§9 方案建议）：同归属断连基架（将留在宿主
            // 图）与对接通道上的 Pawn/物品（通道收回后原格留置）一次列清；两者皆无的
            // 干净脱离立即执行、不新增确认窗（2026-09-24 用户定案）。
            CollectUndockLeftBehinds(sourceMap, ship, domain,
                out List<Thing> channelLeftovers, out List<HashSet<IntVec3>> fragments);
            if (channelLeftovers.Count == 0 && fragments.Count == 0)
            {
                QueueSeparation(universe, ship.ownerId);
                return false;
            }
            PresentSeparationPrompt(universe, ship, "DeltaV_UndockConfirmText",
                BuildUndockSummary(sourceMap, channelLeftovers, fragments));
            return false; // 本 tick 不施加该次推进器动量；分离后的下一次请求再出力。
        }

        internal bool RequestShuttleSeparation(ShuttleShip ship, bool playerInitiated)
        {
            DriveSubject identity = ship.Identity;
            if (!pendingShuttleSeparations.Add(identity)) return false;
            Map source = ship.Map;
            SpaceObject frame = universe.FrameOf(source);
            int version = frame.RepresentationVersion;
            void Execute()
            {
                pendingShuttleSeparations.Remove(identity);
                ShipBase current = ShipRegistry.ShipByIdentity(identity);
                if (!current.IsValid || current.Map != source || frame.RepresentationVersion != version
                    || universe.FrameOf(source) != frame) return;
                TransferShipToPocket(universe, current, source, frame, new HashSet<IntVec3>(current.HullCells), current.Anchor.Rotation);
            }
            void Cancel()
            {
                pendingShuttleSeparations.Remove(identity);
                universe.Autopilot.Cancel(identity);
                if (universe.Pilot.PilotedTarget(universe).Identity == identity) universe.Pilot.Exit(universe);
            }
            var hull = new HashSet<IntVec3>(ship.HullCells);
            var cargo = CollectUnclaimedCargo(source, hull);
            if (playerInitiated && cargo.Count > 0)
            {
                string message = "DeltaV_UndockConfirmText".Translate(BuildLeftBehindSummary(source, cargo));
                if (universe.Pilot.ActiveController != null)
                    universe.Pilot.ActiveController.PresentSeparationConfirmation(PilotTarget.Of(ship), message, Execute, Cancel);
                else Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(message, Execute, Cancel, destructive: true));
            }
            else universe.QueueFrameAction(Execute);
            return false;
        }

        /// <summary>
        /// 玩家分离确认（经 ShipController 边界路由；NPC/无 controller 退化为原版确认窗）。
        /// 文案 key 按方向传入（拆分/脱离两套），回调直连统一执行体。
        /// </summary>
        private void PresentSeparationPrompt(UniverseManager universe, Ship ship, string textKey, string summary)
        {
            pendingSeparationOwnerIds.Add(ship.ownerId);
            string text = textKey.Translate(summary);
            void Confirm() => ConfirmSeparation(universe, ship.ownerId);
            void Cancel() => CancelSeparation(universe, ship.ownerId);
            ShipController controller = universe.Pilot.ActiveController;
            if (controller != null)
            {
                controller.PresentSeparationConfirmation(PilotTarget.Of(ship), text, Confirm, Cancel);
            }
            else
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, Confirm, Cancel, destructive: true));
            }
        }

        /// <summary>
        /// 分离执行排队：立即分离（干净脱离）与 NPC 路径不能在出力 gate 的调用栈里执行
        /// ——手操路径位于容器积分 foreach 循环内，分离会注册/注销容器对象（List 迭代中
        /// 修改即抛异常）。统一经 <see cref="UniverseManager.QueueFrameAction"/> 延迟到
        /// GameComponentUpdate 帧上下文（与旧按钮流程同上下文，地图生成/销毁、聚焦切换
        /// 在该上下文执行过）；排队期间由 pendingSeparationOwnerIds 占位防重复入队。
        /// 弹窗确认回调（forcePause 暂停中、GUI 上下文）不经此处，直接执行。
        /// </summary>
        private void QueueSeparation(UniverseManager universe, int ownerId)
        {
            pendingSeparationOwnerIds.Add(ownerId);
            Ship ship = ShipRegistry.ShipByOwner(ownerId);
            SpaceObject source = ship.IsValid ? universe.FrameOf(ship.map) : null;
            int sourceId = source?.objectId ?? 0, version = source?.RepresentationVersion ?? -1;
            universe.QueueFrameAction(delegate
            {
                SpaceObject currentSource = universe.ObjectById(sourceId);
                Ship currentShip = ShipRegistry.ShipByOwner(ownerId);
                if (currentSource?.RepresentationVersion != version || !currentShip.IsValid
                    || currentShip.map != currentSource.map)
                { pendingSeparationOwnerIds.Remove(ownerId); return; }
                ExecuteSeparation(universe, ownerId);
            });
        }

        /// <summary>确认回调：复核 = 重走主路径——执行体按 ownerId 现查当前派生态分派。</summary>
        private void ConfirmSeparation(UniverseManager universe, int ownerId)
        {
            pendingSeparationOwnerIds.Remove(ownerId);
            ExecuteSeparation(universe, ownerId);
        }

        /// <summary>
        /// 取消回调（2026-09-24 用户定案）：清待决；若自动驾驶任务仍在驱动该船则撤销
        /// （否则下一 tick 重新请求出力会再次弹窗循环）；退出驾驶模式并提示——重试 =
        /// 重新进入驾驶再按推进键。不设结构版本抑制：旧机制会让取消后永远无法出力且
        /// 无反馈（用户报障的根因），已删除。
        /// </summary>
        private void CancelSeparation(UniverseManager universe, int ownerId)
        {
            pendingSeparationOwnerIds.Remove(ownerId);
            Ship ship = ShipRegistry.ShipByOwner(ownerId);
            if (ship.IsValid && universe.Autopilot.IsActive(DriveSubject.Of(ship)))
            {
                universe.Autopilot.Cancel(DriveSubject.Of(ship));
            }
            string message = "DeltaV_SeparationCancelled".Translate();
            if (universe.Pilot.PilotedTarget(universe).Identity == new DriveSubject(0, ownerId))
            {
                universe.Pilot.Exit(universe, message);
            }
            else
            {
                Messages.Message(message, MessageTypeDefOf.NeutralEvent, false);
            }
        }

        /// <summary>
        /// 分离执行体（确认回调与帧边界排队共用）：按 ownerId 重解析句柄（§21：跨图搬运
        /// 后旧快照必失效，一律现查），复用预检的同一方向判定。
        /// </summary>
        private void ExecuteSeparation(UniverseManager universe, int ownerId)
        {
            pendingSeparationOwnerIds.Remove(ownerId);
            Ship ship = ShipRegistry.ShipByOwner(ownerId);
            if (!ship.IsValid)
            {
                return;
            }
            if (!MustExtractOwnDomain(ship))
            {
                SplitResiduals(universe, ship);
            }
            else
            {
                ExecuteUndockSeparation(universe, ship);
            }
        }

        /// <summary>残留块 = 图上控制域之外的基架物理四连通块（同归属断片与异主结构都算）。</summary>
        private static List<HashSet<IntVec3>> CollectResidualBlocks(Ship ship)
        {
            HashSet<IntVec3> domain = ShipRegistry.ControlDomain(ship);
            return ShipRegistry.ResidualBlocks(ship.map, domain);
        }

        private static bool MustExtractOwnDomain(Ship ship)
        {
            // 与无主残骸同图仍可派生为 FreeFlight；该状态不代表 Map 只含本船。
            // 若宿主有基架块之外的内容，提取本船即可完整保留环境，不猜测漂浮物归属。
            return !ShipRegistry.IsFreeFlight(ship)
                || ShipRegistry.HasLooseContents(ship.map, ShipRegistry.ControlDomain(ship));
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
            SplitFocus focus = SplitFocus.Capture(universe, ship.map);
            foreach (HashSet<IntVec3> block in CollectResidualBlocks(ship))
            {
                TransferDomainToNewPocket(universe, ship.map, frame, block, null,
                    wreckOwnership: true, retainedOwnerId: 0, targetConsoleFacing: Rot4.North,
                    focus: focus);
            }
            focus?.Complete(universe);
            QueueEmptyDepartedMapCleanup(universe, ship.map);
            if (SubstructureOwnership.TryGet(ship.map) != null)
            {
                // 拆分后重算质量/惯量/控制模型（新 MassReport 重扫自动生效）；
                // 陈旧油门清零防幽灵火焰。
                universe.FrameOf(ship.map)?.ClearDrive();
            }
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"structure #{ship.ownerId}: residual blocks split off before thrust");
        }

        /// <summary>
        /// 转移核心（脱离/拆分/朝向旋转共用）：把 sourceMap 上 cells 集合整体搬入一张新
        /// 口袋图，并创建/注册新容器。wreckOwnership = true 时新域领取残骸归属与默认名；
        /// retainedOwnerId &gt; 0 时保留原归属（脱离本船/旋转）。targetConsoleFacing = 主台
        /// 在新图中的朝向（脱离/拆分恒 East；朝向旋转传目标方向），容器角随之反调 σ−τ
        /// 使主台在轨道坐标的朝向跨搬运不变。返回新容器（失败 null，图不动）。
        /// </summary>
        private SpaceObject TransferDomainToNewPocket(UniverseManager universe, Map sourceMap,
            SpaceObject sourceFrame, HashSet<IntVec3> cells, Building mainConsole,
            bool wreckOwnership, int retainedOwnerId, Rot4 targetConsoleFacing,
            Building bodyAnchor = null, bool includeTerrain = true,
            SplitFocus focus = null)
        {
            Building anchor = mainConsole ?? bodyAnchor;
            Faction faction = anchor?.Faction;
            Gravship preview = GravshipInterop.BuildPreview(cells, mainConsole, sourceMap, bodyAnchor);
            if (preview == null) return null;
            Rot4 rotation = anchor != null
                ? ShipOrientation.PlacementRotation(anchor.Rotation, targetConsoleFacing) : Rot4.North;
            preview.Rotation = rotation;
            if (!ShipPocketMapFactory.TryGenerateForGravship(preview, universe, out Map pocketMap, out IntVec3 pocketRoot)) return null;
            // Generation and geometry preflight precede the consuming CaptureDomain call.
            Gravship gravship = GravshipInterop.CaptureDomain(sourceMap, cells, mainConsole, faction, includeTerrain);
            if (gravship == null)
            {
                universe.Lifecycle.DestroyPocketMap(pocketMap);
                return null;
            }
            gravship.Rotation = rotation;
            SpacePose launchPose = SpaceTransform.PoseAfterPlacement(sourceFrame.pose, sourceMap, gravship.originalPosition,
                pocketMap, pocketRoot, rotation);
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
            GravshipInterop.StripDomain(gravship, sourceMap, cells, includeTerrain);
            GravshipInterop.PlaceIntoMap(gravship, pocketRoot, pocketMap, ownerId);
            var container = new MapSpaceObject(universe.AllocateOwnerId(), pocketMap, launchPose)
            {
                faction = faction,
            };
            universe.RegisterObject(container);
            focus?.selection.Transferred(cells, pocketMap, ownerId);
            // 源图旧域整清：基架已随捕获剥离，残留归属值全是陈旧数据（按 ID 清不误伤他主域）。
            if (ownerId > 0)
            {
                SubstructureOwnership.TryGet(sourceMap)?.ClearOwner(ownerId);
            }
            PlanetGlobeRenderer.InvalidateSilhouette(sourceMap);
            HostColonistBarVisibility.NotifyDockingStateChanged();
            ShipPhysics.ClearCache();
            return container;
        }

        /// <summary>
        /// shuttle 起飞核心（§7.2：**与船脱离同一套搬运核心**——TransferDomainToNewPocket
        /// 的舱体参数化调用；舱内站立的乘员/货物随捕获清单进入新口袋，乘客不再被留在
        /// 起飞点）。聚焦交接与船脱离同口径（SetFocusedWithCamera 单实现）：玩家注视
        /// 源图时，视点按宇宙参考系连续换算到新口袋图——「飞船起飞自动聚焦」与
        /// 「shuttle 起飞自动聚焦」是同一段代码。
        /// </summary>
        internal SpaceObject LaunchShuttleToPocket(UniverseManager universe, Building hull)
        {
            ShipBase ship = hull.TryGetComp<CompSpaceShuttle>()?.Vessel;
            if (ship?.IsValid != true) return null;
            SpaceObject frame = universe.FrameOf(ship.Map);
            return frame == null ? null : TransferShipToPocket(universe, ship, ship.Map, frame,
                new HashSet<IntVec3>(ship.HullCells), hull.Rotation);
        }

        internal readonly struct DockingFocusHandoff
        {
            internal readonly int hostId;
            public SpaceObject host => Current.Game?.GetComponent<UniverseManager>()?.ObjectById(hostId);
            private readonly int hostVersion;
            internal readonly Vector2 mapPoint;
            internal readonly float zoom;
            private readonly DriveSubject selected;
            private readonly bool hasSelection;

            internal DockingFocusHandoff(SpaceObject host, Vector2 mapPoint, float zoom,
                DriveSubject selected, bool hasSelection)
            {
                this.hostId = host?.objectId ?? 0;
                hostVersion = host?.RepresentationVersion ?? -1;
                this.mapPoint = mapPoint;
                this.zoom = zoom;
                this.selected = selected;
                this.hasSelection = hasSelection;
            }

            internal bool IsValid => host?.InteriorMap != null && host.RepresentationVersion == hostVersion;
            internal void SelectTransferredStructure()
            {
                if (hasSelection) ShipStatusPanel.SelectStructure(host.map, selected);
            }
        }

        /// <summary>对接完成的聚焦交接单一入口：必须在源图销毁前捕获宇宙视点。</summary>
        internal DockingFocusHandoff CaptureDockingFocusHandoff(UniverseManager universe,
            Map sourceMap, Map targetMap)
        {
            if (universe == null || sourceMap == null || targetMap == null
                || sourceMap.Disposed || targetMap.Disposed || Find.CurrentMap != sourceMap)
            {
                return default(DockingFocusHandoff);
            }
            SpaceObject source = universe.FrameOf(sourceMap);
            SpaceObject host = universe.FrameOf(targetMap);
            if (source == null || host == null) return default(DockingFocusHandoff);
            Vector3 center = Find.CameraDriver.CurrentViewRect.CenterVector3;
            Vector3 direction = SpaceTransform.MapPointToUniverse(source, sourceMap,
                new Vector2(center.x, center.z));
            bool hasSelection = ShipStatusPanel.TryGetSelectedStructure(sourceMap, out DriveSubject selected);
            return new DockingFocusHandoff(host,
                SpaceTransform.UniverseToMapPoint(host, targetMap, direction),
                Find.CameraDriver.RootSize, selected, hasSelection);
        }

        internal void ApplyDockingFocusHandoff(UniverseManager universe, DockingFocusHandoff handoff)
        {
            if (!handoff.IsValid) return;
            handoff.SelectTransferredStructure();
            universe.SetFocusedWithCamera(handoff.host, handoff.mapPoint, handoff.zoom);
        }

        /// <summary>
        /// 完成时刻的真实捕获（**唯一消费点**）：按当前控制域捕获（LandingApproachController
        /// 完成时调用，与移除/放置同一调用栈；对接期间的建造/扩建全部随本次捕获带走）。
        /// </summary>
        internal Gravship CaptureDockingTargetForCompletion(PilotTarget target,
            out HashSet<IntVec3> domain, out bool includeTerrain)
        {
            domain = null;
            includeTerrain = false;
            if (!target.IsValid || target.Map == null) return null;
            ShipBase ship = ShipRegistry.ShipByIdentity(target.Identity);
            if (!ship.IsValid) return null;
            domain = new HashSet<IntVec3>(ship.HullCells);
            ShipTransferSpec transfer = ship.Transfer;
            includeTerrain = transfer.includeTerrain;
            if (domain.Count == 0) return null;
            return GravshipInterop.CaptureDomain(target.Map, domain, transfer.console,
                target.Faction, includeTerrain);
        }

        /// <summary>
        /// 战术降落的真转移（LandingApproachController 完成时调用，与捕获同一调用栈）：
        /// 剥离源图 → 直接放入目标图（原版放置收尾；落点 root/旋转与盖章归属全部读
        /// DockingPlan 注册值）。**不重放原版 InitiateLanding**——不进 cutscene/mask/
        /// LandingEnded 链路（§6 六步：完成时捕获 → 转移 → 发布新关系 → 清理旧容器）。
        /// </summary>
        internal bool CompleteTransferIntoHost(UniverseManager universe, PilotTarget target,
            Gravship gravship, HashSet<IntVec3> domain, bool includeTerrain, Map targetMap,
            IntVec3 landingRoot, Rot4 landingRot, int structureId, out Map sourceMap)
        {
            sourceMap = null;
            if (gravship == null || targetMap == null) return false;
            Map pocketMap = target.Map;
            if (pocketMap == null || pocketMap == targetMap) return false;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"docking transfer: {target.Label} {UniverseManager.MapTag(pocketMap)} -> host "
                + $"{UniverseManager.MapTag(targetMap)} (root {landingRoot}, rot {landingRot})");
            GravshipInterop.StripDomain(gravship, pocketMap, domain, includeTerrain);
            GravshipInterop.PlaceIntoMap(gravship, landingRoot, targetMap, structureId);
            sourceMap = pocketMap;
            RegisterDockingCleanup(pocketMap);
            return true;
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

        // ---- 脱离方向执行体（固定 → 移动，拆图 §7；出力 gate 触发，无独立入口）----

        /// <summary>
        /// 脱离方向的留置汇总输入：同归属断连基架（将留在宿主图）与对接通道上的
        /// Pawn/物品（通道收回后原格留置）。注意与拆分方向的口径差异是语义性的：
        /// 脱离时移动的是本船域，其余一切（含异主结构）留在宿主容器；拆分时移动的是
        /// 本船容器，域外一切必须逐块搬走。
        /// </summary>
        private static void CollectUndockLeftBehinds(Map sourceMap, Ship ship, HashSet<IntVec3> domain,
            out List<Thing> channelLeftovers, out List<HashSet<IntVec3>> fragments)
        {
            channelLeftovers = Building_DockingPort.CollectChannelLeftBehind(sourceMap, domain);
            if (ShipRegistry.IsFreeFlight(ship) || Current.Game?.GetComponent<UniverseManager>()?.FrameOf(sourceMap)?.IsWorksite == true)
            {
                // 因零散内容而保留宿主时，留置清单不限于对接通道。
                channelLeftovers = CollectUnclaimedCargo(sourceMap, domain);
            }
            fragments = ShipRegistry.ResidualBlocks(sourceMap, domain)
                .Where(block =>
                {
                    SubstructureOwnership ownership = SubstructureOwnership.TryGet(sourceMap);
                    return ownership != null && ownership.OwnerAt(block.First()) == ship.ownerId;
                })
                .ToList();
        }

        private static List<Thing> CollectUnclaimedCargo(Map map, HashSet<IntVec3> departing)
        {
            // Crew/cargo aboard another surviving ship are not abandonment risks.
            var retained = new HashSet<IntVec3>();
            SpaceObject container = Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map);
            if (container != null)
                foreach (ShipBase other in container.Ships)
                    if (other.IsValid && !other.HullCells.Any(departing.Contains)) retained.UnionWith(other.HullCells);
            return map.listerThings.AllThings.Where(t => t.Spawned && !t.Destroyed && SpaceScatterService.IsCargo(t)
                && !departing.Contains(t.Position) && !retained.Contains(t.Position)).ToList();
        }

        private static string BuildUndockSummary(Map sourceMap, List<Thing> channelLeftovers,
            List<HashSet<IntVec3>> fragments)
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
            return string.Join("\n", summary);
        }

        /// <summary>
        /// 脱离方向执行体（确认弹窗之后 / NPC 与干净脱离的帧边界排队；无引擎搬运，不走
        /// 原版起飞管线）。自动驾驶任务不在此撤销：手动触发的分离在重新 Enter 时已
        /// Detach；自动驾驶自触发的分离保留任务——Flight 按 ownerId 每 tick 现查船与
        /// 容器，脱离后在新容器上继续请求出力。
        /// </summary>
        private void ExecuteUndockSeparation(UniverseManager universe, Ship ship)
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

            // 收通道 = 脱离事务的一部分（§9）：只收回端口或实际对接面在脱离域上的
            // 通道；B、C 之间无关通道保留。通道上的
            // Pawn/物品原格留置（不自动迁往安全格）。
            Building_DockingPort.RetractChannelsFor(sourceMap, domain);

            SpaceObject container = TransferShipToPocket(universe, ship, sourceMap, sourceFrame, domain,
                Rot4.East);
            if (container == null)
            {
                Messages.Message("DeltaV_DockFailBusy".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            Messages.Message("DeltaV_FreeFlightSuccess".Translate(universe.LabelForOwner(ship.ownerId)),
                MessageTypeDefOf.PositiveEvent, false);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"structure #{ship.ownerId} undocked into new container #{container.objectId}; heading={container.pose.angle * Mathf.Rad2Deg:F0} deg");
        }

        /// <summary>
        /// 本船控制域 → 新口袋图的转移+交接公共编排（脱离/朝向旋转共用，2026-09-25 提取）：
        /// 退出本船驾驶位（句柄随图失效，§21）→ 搬运（主台规范到 targetFacing）→ 句柄
        /// 重解析 → 世界剪影重建 → 按侧边栏选中结构交接相机与选择 → 恢复原手操
        /// （shuttle 起飞沿用自动入驾驶）→ 帧边界源图善后。聚焦不按派系或驾驶状态选边。
        /// 返回新容器（失败 null，图不动）。
        /// </summary>
        private SpaceObject TransferShipToPocket(UniverseManager universe, ShipBase ship,
            Map sourceMap, SpaceObject sourceFrame, HashSet<IntVec3> domain, Rot4 targetFacing)
        {
            DriveSubject identity = ship.Identity;
            bool wasPiloted = universe.Pilot.PilotedTarget(universe).Identity == identity;
            SplitFocus focus = SplitFocus.Capture(universe, sourceMap);
            if (wasPiloted) universe.Pilot.Exit(universe);
            ShipTransferSpec transfer = ship.Transfer;
            SpaceObject container = TransferDomainToNewPocket(universe, sourceMap, sourceFrame,
                domain, transfer.console, wreckOwnership: false,
                retainedOwnerId: transfer.ownerId, targetConsoleFacing: targetFacing,
                bodyAnchor: transfer.body,
                includeTerrain: transfer.includeTerrain, focus: focus);
            if (container == null) return null;
            ShipBase resolved = ShipRegistry.ShipByIdentity(identity);
            WorldShipMarkerRenderer.RebuildFor(resolved);
            focus?.Complete(universe);
            if (Find.CurrentMap == container.map && resolved.IsValid && universe.Pilot.CanPlayerControl(resolved)
                && (wasPiloted || identity.shuttle))
                universe.Pilot.Enter(universe, resolved);
            QueueEmptyDepartedMapCleanup(universe, sourceMap);
            return container;
        }

        /// <summary>
        /// 玩家主动确认的飞船地图旋转（2026-09-25 定案，侧边栏罗盘入口）：自由飞行中的船
        /// 整体搬入与当前位置重合的新 SpaceObject（新口袋图），建筑旋转使主台朝向
        /// targetFacing（聚焦图 identity，图帧即屏上方向——一次点击指定主台屏上朝向）；
        /// 容器角反调 σ−τ 保证主台在轨道坐标的朝向不变，位姿/速度按船体中心连续继承
        ///（RigidPointPose）。GUI 点击触发（OnGUI 调用栈，与弹窗确认回调同口径直接执行）。
        /// </summary>
        public void RotateShipHeading(UniverseManager universe, ShipBase ship, Rot4 targetFacing)
        {
            if (!ship.IsValid) return;
            if (universe.Approach.FlowTarget.Identity == ship.Identity)
            {
                Messages.Message("DeltaV_RotateBlockedPlan".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (ship.State != ShipDerivedState.FreeFlight) return; // 罗盘只在自由飞行显示；防御性守卫
            if (ship.Anchor.Rotation == targetFacing) return; // 已是目标朝向，无操作
            Map sourceMap = ship.Map;
            SpaceObject sourceFrame = universe.FrameOf(sourceMap);
            if (sourceFrame == null || sourceFrame.projectileRoleOverride) return;
            var domain = new HashSet<IntVec3>(ship.HullCells);
            if (domain.Count == 0) return;
            // 自由飞行图上不应有对接通道；防御性收回与本域相关的通道。
            Building_DockingPort.RetractChannelsFor(sourceMap, domain);
            SpaceObject container = TransferShipToPocket(universe, ship, sourceMap, sourceFrame, domain,
                targetFacing);
            if (container == null)
            {
                Messages.Message("DeltaV_DockFailBusy".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            Messages.Message("DeltaV_ShipRotated".Translate(ShipStatusPanel.CompassLabel(targetFacing)),
                MessageTypeDefOf.PositiveEvent, false);
            ShipBase rotated = ShipRegistry.ShipByIdentity(ship.Identity);
            float headingDeg = rotated.IsValid ? ShipRegistry.ShipPose(rotated).angle * Mathf.Rad2Deg : float.NaN;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"ship #{ship.Identity} rotated: anchor faces {targetFacing} in container #{container.objectId}; orbital heading {headingDeg:F0} deg (preserved)");
        }

        /// <summary>
        /// 延后到帧上下文清点离港地图（QueueFrameAction 统一机制）：此时已完全脱离
        /// 调用栈，DeinitAndRemoveMap 不会踩到上层仍在使用的引用。
        /// </summary>
        internal void QueueEmptyDepartedMapCleanup(UniverseManager universe, Map map)
        {
            if (map == null) return;
            universe.QueueFrameAction(delegate { HandleDepartedSourceMap(universe, map); });
        }

        /// <summary>
        /// 脱离与读档清扫的源图善后：**worldtile 图不受影响**——保留，去留交给锚对象
        /// 配置与距离生命周期；非 worldtile 图空白则立刻删除（遗留基架转无主残骸），
        /// 无承载结构的普通图优先交给生命周期回收；仍有承载时才执行既有残留处置。
        /// </summary>
        internal void HandleDepartedSourceMap(UniverseManager universe, Map sourceMap)
        {
            if (universe == null || sourceMap == null || sourceMap.Disposed
                || !Find.Maps.Contains(sourceMap)) return;
            if (sourceMap.Tile.Valid) return; // worldtile 图：不受转换影响
            FinishEmptyWorksite(sourceMap);
            if (universe.Lifecycle.RecycleUnsupportedMap(sourceMap)) return;
            if (sourceMap == Find.CurrentMap)
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{sourceMap.Parent?.Label ?? sourceMap.ToString()}: pocket cleanup skipped (currently focused)");
                return;
            }
            if (SpaceLifecycleService.IsUniverseHost(sourceMap)) return;
            // 无承载图已先回收；有承载结构的宿主保留其零散内容。
            if (ShipRegistry.HasLooseContents(sourceMap)) return;
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
            else if (owner != null && SpaceMapGovernance.IsBlankLandingCanvas(sourceMap))
            {
                // 普通容器口袋同样「空白立刻删除」（§19 政策）：朝向旋转/脱离清空的自有
                // 口袋图与被搬空的物化容器——遗留基架转无主残骸快照后销图注销，不再
                // 等距离归档（空图与船同位同速漂移，永不触发距离清理 → 常驻加载泄漏）。
                // 非空白图（有滞留物/断片）仍交给距离生命周期。
                ConvertLeftoverSubstructureToWreck(universe, sourceMap, owner.pose);
                universe.Lifecycle.Forget(sourceMap);
                universe.Lifecycle.DestroyPocketMap(sourceMap);
                universe.UnregisterObject(owner);
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{owner.label}: emptied pocket interior removed with its object");
            }
        }

        internal void FinishEmptyWorksite(Map map)
        {
            SpaceObject owner = universe.FrameOf(map);
            if (owner?.IsWorksite != true || owner.Ships.Any(s => s.IsValid)
                || IsAwaitingDockingCleanup(map) || universe.IsResettingScene) return;
            foreach (HashSet<IntVec3> block in ShipRegistry.ResidualBlocks(map, new HashSet<IntVec3>()))
                if (TransferDomainToNewPocket(universe, map, owner, block, null, true, 0, Rot4.North) == null) return;
            universe.Scatter.ScatterLoose(map);
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
                List<Ship> shipsOnMap = ShipRegistry.FoundationShipsOnMap(sourceMap);
                Ship launcher = shipsOnMap.FirstOrDefault(s => s.ownerId == engineOwner);
                SpaceObject sourceContainer = universe.SpaceObjectOnMap(sourceMap);
                flow.pendingStrategicOwnPocket = launcher.IsValid && !ShipRegistry.AllShipsOnMap(sourceMap)
                    .Any(ship => ship.Identity != DriveSubject.Of(launcher) && !ShipRegistry.Contains(launcher, ship.Anchor))
                    && sourceContainer != null && !(sourceContainer is PoiState)
                    ? sourceContainer
                    : null;
                if (launcher.IsValid)
                {
                    universe.Pilot.Exit(universe);
                    universe.Autopilot.Cancel(DriveSubject.Of(launcher));
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
            int ownPocketId = flow?.pendingStrategicOwnPocket?.objectId ?? 0;
            int ownPocketVersion = flow?.pendingStrategicOwnPocket?.RepresentationVersion ?? -1;
            if (flow != null)
            {
                flow.pendingStrategicSourceMap = null;
                flow.pendingStrategicSourceAddress = PlanetTile.Invalid;
                flow.pendingStrategicSourcePose = null;
                flow.pendingStrategicOwnPocket = null;
            }
            if (sourceMap == null) return;
            // 善后经 QueueFrameAction 延迟到帧上下文（统一机制）；入队时机保持在
            // TravelTo 放行后的 Postfix 内——更早入队会卡守卫（2026-09-20 教训勿回退）。
            universe.QueueFrameAction(delegate
            {
                if (universe == null || sourceMap.Disposed || !Find.Maps.Contains(sourceMap)) return;

                SpaceObject ownPocket = universe.ObjectById(ownPocketId);
                if (ownPocketId != 0 && ownPocket?.RepresentationVersion != ownPocketVersion) return;
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
                && universe != null && !ShipRegistry.AllShipsOnMap(map).Any();
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
            if (ShipRegistry.AllShipsOnMap(map).Any())
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
        internal void ConvertLeftoverSubstructureToWreck(UniverseManager universe, Map map, SpacePose pose)
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
        internal static void AbandonPawnsOn(Map map)
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
