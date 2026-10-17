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
    /// 分离（§7 脱离 + §8 拆分统一，2026-09-24 用户定案）：「将要出力的船与其余部分
    /// 分离」是唯一语义，出力请求（<see cref="RequestThrust"/>）是唯一触发点——
    /// 「脱离停靠」不再是独立命令/按钮，已停靠的船同样可进入驾驶，首次推进器出力即
    /// 触发分离。方向按当前内容分派：自由飞行且域外内容均落在基架块上时，残留块
    /// 逐块独立成残骸容器（船留原图）；已停靠或有无基架残留内容时，本船控制域
    /// 搬入新口袋容器（宿主图与其余内容不动，
    /// 位姿/速度 = 宿主刚体上该船体点的位姿，含自旋切向；收通道 = 分离事务的一部分，
    /// 只收相关通道，通道 Pawn/物品原格留宿主图）。干净分离（无断片无滞留）立即执行
    /// 不弹窗；有留置内容时玩家 controller 弹 forcePause 汇总确认（NPC 直接执行）；
    /// 取消 = 撤销自动驾驶任务 + 退出驾驶并提示。执行时序：弹窗确认回调（暂停中）
    /// 直接执行；立即执行与 NPC 路径排队到帧边界——手操路径的出力 gate 在容器积分
    /// foreach 内被调用，分离会注册/注销容器对象，禁止循环内执行。
    /// </summary>
    internal sealed partial class ShipFlightTransitionService
    {
        private static readonly FieldInfo ControllerMapHasGravAnchor =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "mapHasGravAnchor");

        private static readonly FieldInfo ControllerGravship =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "gravship");

        private static readonly FieldInfo ControllerTakeoffTile =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "takeoffTile");

        /// <summary>
        /// 原版 controller 的设计器缓存（moveDesignator）。原版只在 LandingEnded 复位它；
        /// 战术对接不走 LandingEnded 链路，陈旧实例（readonly map 绑定上一张目标图）会跨
        /// 对接存活——选点入口显式清空，任何后续使用（含原版世界视图降落确认 UI 的
        /// 「移动飞船」按钮）都重新按当前 marker 绑定。见 §21 MoveDesignator 教训。
        /// </summary>
        private static readonly FieldInfo ControllerMoveDesignator =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "moveDesignator");

        public bool FreeFlightTransitionPending()
        {
            return HasPendingDocking || HasLandedTargetAwaitingCleanup;
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
            if (universe.Approach.FlowTarget.Identity == target.Identity
                && universe.Approach.ActivePlan?.Target.Identity != target.Identity) return false;
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
            foreach (HashSet<IntVec3> block in CollectResidualBlocks(ship))
            {
                TransferDomainToNewPocket(universe, ship.map, frame, block, null,
                    wreckOwnership: true, retainedOwnerId: 0, targetConsoleFacing: Rot4.North);
            }
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
            Building bodyAnchor = null, IntVec2? pocketSize = null, bool includeTerrain = true)
        {
            Building anchor = mainConsole ?? bodyAnchor;
            Faction faction = anchor?.Faction;
            Gravship gravship = GravshipInterop.CaptureDomain(sourceMap, cells, mainConsole, faction,
                includeTerrain);
            if (gravship == null)
            {
                Log.Error("[DeltaV] Domain capture failed; transfer aborted.");
                return null;
            }
            Vector2 hullCenter = SpaceTransform.BoundsCenterOfCells(cells);
            float consoleAngle = anchor != null ? ShipOrientation.AngleFromRot4(anchor.Rotation) : 0f;
            // 位姿/速度继承：宿主刚体上船体中心点的完整位姿（含 v+ω×r 与自旋切向）。
            SpacePose launchPose = SpaceTransform.RigidPointPose(sourceFrame.pose,
                SpaceTransform.MapToShipLocal(sourceMap, hullCenter), consoleAngle);
            // 主台在口袋图规范到 targetConsoleFacing（图帧 = 聚焦图屏上方向）；容器角
            // 反调 σ−τ：放置后主台朝向 = 容器角 ⊕ τ = θ+σ（源图主台轨道朝向），跨搬运
            // 不变（τ=East 时与旧行为一致）。残片无主台，保持 North（σ=0 = 地图帧
            // 对齐，与 launchPose 的 0 角一致）。
            gravship.Rotation = anchor != null
                ? ShipOrientation.PlacementRotation(anchor.Rotation, targetConsoleFacing) : Rot4.North;
            if (anchor != null)
            {
                launchPose.angle = SpaceTransform.WrapAngle(launchPose.angle
                    - ShipOrientation.AngleFromRot4(targetConsoleFacing));
            }
            // shuttle（§7.2「与船脱离同一套搬运核心」的舱体参数化）：口袋图按舱体脚印
            // 小图（不走飞船 80 下限）；不搬基架（includeTerrain=false 已贯穿捕获/剥离）。
            Map pocketMap;
            IntVec3 pocketRoot;
            if (pocketSize.HasValue)
            {
                pocketMap = ShipPocketMapFactory.Generate(
                    new IntVec3(pocketSize.Value.x, 1, pocketSize.Value.z), universe);
                pocketRoot = new IntVec3((pocketMap.Size.x - gravship.Bounds.Size.x) / 2, 0,
                    (pocketMap.Size.z - gravship.Bounds.Size.z) / 2);
            }
            else if (!ShipPocketMapFactory.TryGenerateForGravship(gravship, universe,
                out pocketMap, out pocketRoot))
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
            GravshipInterop.StripDomain(gravship, sourceMap, cells, includeTerrain);
            GravshipInterop.PlaceIntoMap(gravship, pocketRoot, pocketMap, ownerId);
            var container = new MapSpaceObject(universe.AllocateOwnerId(), pocketMap, launchPose)
            {
                faction = faction,
            };
            universe.RegisterObject(container);
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

        // ---- 战术对接（移动 → 固定，合图 §6）----

        public void BeginDocking(Ship ship)
        {
            BeginDocking(PilotTarget.Of(ship));
        }

        /// <summary>shuttle 入口（面板/gizmo 同用；与船同一链，2026-09-27 单链化）。</summary>
        public void BeginDockingShuttle(Building hull)
        {
            BeginDocking(PilotTarget.Of(hull));
        }

        /// <summary>
        /// 对接唯一编排（船与 shuttle 同链，2026-09-27 用户定案「双入口同链」）：
        /// 目标枚举 → 残留确认（shuttle 独占口袋图天然空集、走同一代码）→ 选点。
        /// 脚印、预览锚与搬运差异均从 ShipBase 读取。
        /// </summary>
        public void BeginDocking(PilotTarget target)
        {
            if (DockingFlowBlocked(target)) return;
            List<(SpaceObject target, float distance)> candidates = CollectLandableTargets(universe, target);
            if (candidates.Count == 0)
            {
                // 深空固定被拒绝（星区架构 §7）：不再凭空生成空画布；目标尚未物化时
                // 继续接近，进入 activation radius 加载后即可正常降落（软限制）。
                Messages.Message("DeltaV_DockFailNoTarget".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (candidates.Count == 1)
            {
                BeginDockingTargeted(target, candidates[0].target);
                return;
            }
            // 多目标投影重叠（拥挤环境）时按外壳间距排序交给玩家选择（2026-09-24）。
            // 构建顺序即距离序：FloatMenu 构造器对同优先级稳定排序，保持插入序。
            var options = new List<FloatMenuOption>(candidates.Count);
            foreach ((SpaceObject dockTarget, float distance) in candidates)
            {
                int capturedId = dockTarget.objectId;
                PilotTarget flowTarget = target;
                string label = distance < float.MaxValue
                    ? "DeltaV_DockTargetEntry".Translate(dockTarget.label, distance.ToString("0.#"))
                    : dockTarget.label;
                options.Add(new FloatMenuOption(label, delegate { BeginDockingTargeted(flowTarget, universe.ObjectById(capturedId)); }));
            }
            Find.WindowStack.Add(new FloatMenu(options, "DeltaV_DockTargetMenuTitle".Translate()));
        }

        /// <summary>菜单选择 / 单候选直入的公共入口：重跑门禁与目标资格后进入残留确认。</summary>
        private void BeginDockingTargeted(PilotTarget flowTarget, SpaceObject target)
        {
            if (DockingFlowBlocked(flowTarget)) return;
            // FloatMenu 不暂停：菜单打开期间对象可移动/失效，点击时按同一资格口径复核
            //（自身排除同样用容器引用 + map 身份双条件）。
            if (target == null || target.map == null || target.map.Disposed
                || target == universe.FrameOf(flowTarget.Map) || target.map == flowTarget.Map
                || !IsLandableObject(target)
                || !ShipCenterInsideProjection(target, universe.FrameOf(flowTarget.Map).pose.position))
            {
                Messages.Message("DeltaV_DockFailNoTarget".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            // 源图残留处置的事前确认（§6 方案建议）：口袋图上控制域之外的断连结构 /
            // Pawn / 物品将随源图处置——先列清单确认，不在消费式捕获中途询问。
            var flowCells = new HashSet<IntVec3>(flowTarget.Vessel.HullCells);
            // 残留确认同一段代码（shuttle 独占口袋图 → 天然空集，不弹窗）。
            List<Thing> leftovers = CollectSourceLeftovers(flowTarget.Map, flowCells);
            if (leftovers.Count > 0)
            {
                string summary = BuildLeftBehindSummary(flowTarget.Map, leftovers);
                int targetId = target.objectId, version = target.RepresentationVersion;
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "DeltaV_DockSourceLeftoverConfirm".Translate(
                        flowTarget.Label, summary),
                    () => {
                        SpaceObject current = universe.ObjectById(targetId);
                        if (current?.RepresentationVersion == version)
                            BeginDockingConfirmed(universe, flowTarget, current, leftovers);
                    },
                    null, destructive: true));
                return;
            }
            BeginDockingConfirmed(universe, flowTarget, target, null);
        }

        /// <summary>
        /// 对接发起门禁（船与 shuttle 同一门禁，2026-09-27 单链化）：目标有效且在飞、
        /// 非流程互斥（选点/计划中不可再发起——对船同样生效）、宇宙层可用。
        /// </summary>
        private bool DockingFlowBlocked(PilotTarget target)
        {
            if (universe == null || !target.IsValid) return true;
            SpaceObject container = universe.FrameOf(target.Map);
            if (!target.Vessel.CanDriveIn(container) || target.Vessel.State != ShipDerivedState.FreeFlight
                || container.projectileRoleOverride) return true;
            if (universe.Approach.AnyActive
                || Find.GravshipController == null || Find.GravshipController.LandingAreaConfirmationInProgress
                || Find.GravshipController.IsGravshipTravelling || Current.Game.Gravship != null
                || universe.ShipTransitions.HasPendingDocking)
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

        private void BeginDockingConfirmed(UniverseManager universe, PilotTarget flowTarget, SpaceObject target,
            List<Thing> confirmedLeftovers)
        {
            pendingLeftoverThingIds = confirmedLeftovers?.Select(t => t.thingIDNumber).ToList();
            pendingSelectionWasManual = universe.Pilot.GetControlState(flowTarget) == ShipControlState.Manual;
            // 航向吸附（船 = 容器角 ⊕ 主台 σ；shuttle = 容器角 ⊕ 舱体 σ——同一公式经
            // PilotTarget.FrameAngle）。
            float snappedHeading = SnapAngle(SpaceTransform.WrapAngle(
                universe.FrameOf(flowTarget.Map).pose.angle + flowTarget.FrameAngle));
            // 目标确认日志（Core 模块，低频）：记录选点即将发生的图与承载对象，
            // 与枚举/物化日志的 map#uniqueID 可直接关联。
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"docking confirmed: {flowTarget.Label} -> target #{target.objectId}({target.label}) {target.GetType().Name} "
                + $"{UniverseManager.MapTag(target.map)}, holder #{universe.FrameOf(target.map)?.objectId}("
                + $"{universe.FrameOf(target.map)?.label}), heading={snappedHeading * Mathf.Rad2Deg:F0} deg");
            BeginOriginalLanding(universe, flowTarget, target, target.map, snappedHeading);
        }

        /// <summary>
        /// 可降落目标枚举，按可见船壳距离升序返回（2026-09-24 修订：多目标投影重叠时
        /// 交给玩家按距离选择；**自身容器显式排除**——旧单选实现漏排时自己恒以凸包
        /// 包含距离 0 获胜，任何真实目标不可能被选中）。资格门槛：飞船中心在对象内部
        /// 地图投影内（任何已物化对象——含移动中的有动力飞船；空白空画布不可固定）；
        /// 距离 = 双方可渲染船壳（凸包）的最近距离，无船壳数据的目标记无穷大排尾。
        /// </summary>
        private List<(SpaceObject target, float distance)> CollectLandableTargets(
            UniverseManager universe, Ship ship) => CollectLandableTargets(universe, PilotTarget.Of(ship));

        private List<(SpaceObject target, float distance)> CollectLandableTargets(
            UniverseManager universe, PilotTarget target)
        {
            var cells = new HashSet<IntVec3>(target.Vessel.HullCells);
            return CollectLandableTargets(universe, target.Map, cells, target.Label, cells);
        }

        private List<(SpaceObject target, float distance)> CollectLandableTargets(
            UniverseManager universe, Map ownMap, HashSet<IntVec3> ownCells, string ownLabel,
            HashSet<IntVec3> distanceCells)
        {
            SpaceObject ownContainer = universe.FrameOf(ownMap);
            var candidates = new List<(SpaceObject target, float distance)>();
            // 目标枚举日志（Core 模块，按钮触发的低频事件）：只记录有图对象与排除
            // 原因；无图 marker 跳过不记。图标识经 MapTag（uniqueID 与物化/性能
            // 日志可直接关联）。
            var verdicts = new List<string>();
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj is PoiState markerCheck && markerCheck.map == null) continue;
                // 自身排除 = 容器引用 + map 身份双条件（2026-09-25 事故加固）：引用
                // 相等在 FrameOf 取错对象/双持有时会漏（对接目标选成了自己的图）；
                // map 相等无条件兜底——自己的图永远不可能是自己的降落目标。
                if (obj == ownContainer || obj.map == ownMap)
                {
                    verdicts.Add($"#{obj.objectId}({obj.label}) OWN");
                    continue;
                }
                if (obj.map == null || !IsLandableObject(obj))
                {
                    verdicts.Add($"#{obj.objectId}({obj.label}) not-landable");
                    continue;
                }
                if (!ShipCenterInsideProjection(obj, ownContainer.pose.position))
                {
                    verdicts.Add($"#{obj.objectId}({obj.label}) {UniverseManager.MapTag(obj.map)} outside-projection");
                    continue;
                }
                float distance = VisibleShellDistance(universe, ownMap, distanceCells, obj);
                candidates.Add((obj, distance));
                verdicts.Add($"#{obj.objectId}({obj.label}) {UniverseManager.MapTag(obj.map)} CANDIDATE dist={distance:0.#}");
            }
            candidates.Sort((a, b) => a.distance.CompareTo(b.distance));
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"docking target scan (own {ownLabel}, container #{ownContainer?.objectId} {UniverseManager.MapTag(ownMap)}): "
                + $"{string.Join(" | ", verdicts)} => {candidates.Count} candidate(s)");
            return candidates;
        }

        /// <summary>
        /// 玩家船壳与目标可渲染船壳的最近距离（目标切空间系中计算；数据流与外壳渲染
        /// 同源——船壳格列表 + 双方位姿，凸包后求距离）。船壳口径 = 各自图上全部基架
        /// 格（多船图取整图凸包——容器承载全部成员），POI = 统一结构外观可见内容格。
        /// </summary>
        private float VisibleShellDistance(UniverseManager universe, Ship ship, SpaceObject target)
        {
            return VisibleShellDistance(universe, ship.map, ShipRegistry.ControlDomain(ship), target);
        }

        /// <summary>参数化版本（船控制域 / shuttle 舱体脚印同一实现，§7.4）。</summary>
        private float VisibleShellDistance(UniverseManager universe, Map playerMap,
            HashSet<IntVec3> playerCells, SpaceObject target)
        {
            Map targetMap = target.map;
            if (playerMap == null || targetMap == null || targetMap.Disposed) return float.MaxValue;
            SpaceObject playerFrame = universe.FrameOf(playerMap);
            SpaceObject targetFrame = universe.FrameOf(targetMap);
            if (playerFrame == null || targetFrame == null) return float.MaxValue;

            var targetPoints = new List<Vector2>();
            SubstructureOwnership targetOwnership = SubstructureOwnership.TryGet(targetMap);
            if (targetOwnership != null && targetOwnership.HasAnySubstructure())
            {
                foreach (IntVec3 cell in SubstructureOwnership.CellsOnMap(targetMap))
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

            var playerPoints = new List<Vector2>();
            foreach (IntVec3 cell in playerCells)
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
            BeginOriginalLanding(universe, PilotTarget.Of(ship), target, targetMap, snappedHeading);
        }

        private void BeginOriginalLanding(UniverseManager universe, Building shuttleBody,
            SpaceObject target, Map targetMap, float snappedHeading)
        {
            BeginOriginalLanding(universe, PilotTarget.Of(shuttleBody), target, targetMap, snappedHeading);
        }

        private void BeginOriginalLanding(UniverseManager universe, PilotTarget flowTarget,
            SpaceObject target, Map targetMap, float snappedHeading)
        {
            PendingTarget = flowTarget;
            // 选点零消费（2026-09-21 阶段4 定案）：预览用空洞 Gravship——只读填充原版
            // 选点/合法性/禁触链路所需字段；真实搬运唯一发生在完成时刻（与移除同一
            // 调用栈）——对接期间的建造/扩建全部随完成时捕获带走。
            Gravship gravship = GravshipInterop.BuildPreview(flowTarget);
            if (gravship == null)
            {
                universe.ShipTransitions.DiscardPendingDocking(universe);
                Log.Error($"[DeltaV] Docking {flowTarget.Label}: preview gravship build failed; aborting.");
                return;
            }
            BeginOriginalLandingCore(universe, flowTarget.Map, gravship,
                flowTarget.Vessel.Anchor.Rotation,
                target, targetMap, snappedHeading, flowTarget.Label);
        }

        private void BeginOriginalLandingCore(UniverseManager universe, Map ownMap, Gravship gravship,
            Rot4 sourceFacing, SpaceObject target, Map targetMap,
            float snappedHeading, string flowLabel)
        {
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
                : OrbitSphere.TileFromDirection(universe.FrameOf(ownMap).pose.position);
            gravship.Tile = addressTile;
            gravship.destinationTile = addressTile;

            universe.Pilot.Exit(universe);

            // 自持选点流程（原版 ArriveExistingMap 经 WorldObjects tile 查图，口袋图不通）：
            // 复刻其主体（锁 landingMap、构造 marker、进入确认状态），初始落点放到
            // 悬停位置在目标对象内部投影上的对应格（星区架构 §7）。设计器显式绑定
            // targetMap 新建（勿用 controller.MoveDesignator()——终身缓存陈旧 map 字段），
            // 随后 landingMap 置 null 隐藏原版 Abort——AbortLanding 依赖 landingMap.Tile
            // 做坠毁改道，口袋图没有可用 tile。
            // 不变量守卫（边沿触发，Core 模块）：上一次选点未清干净的 landingMap
            /// 只在异常时可见——正常流程选点结束即置 null。
            Map prevLandingMap = Find.GravshipController.landingMap;
            if (prevLandingMap != null)
            {
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"docking selection: STALE landingMap before set = {UniverseManager.MapTag(prevLandingMap)} "
                    + $"(about to overwrite with {UniverseManager.MapTag(targetMap)})");
            }
            Find.GravshipController.landingMap = targetMap;
            // 清掉原版的设计器缓存（用的时候实例化，2026-09-25 用户定案）：战术对接不走
            // LandingEnded，原版自己的复位永不触发；残留实例绑定上一张目标图，世界视图
            // 降落确认 UI 的「移动飞船」按钮会再次取到它。
            ControllerMoveDesignator?.SetValue(Find.GravshipController, null);
            GravshipLandingMarker marker = ThingMaker.MakeThing(ThingDefOf.GravshipLandingMarker) as GravshipLandingMarker;
            marker.gravship = gravship;
            marker.CacheCells();
            universe.ShipTransitions.pendingSelectionMarker = marker; // 取消时直接销毁（shuttle 预览无主台可比）
            Find.GravshipController.Notify_LandingAreaConfirmationStarted(marker);
            // 必须每次新建设计器（2026-09-25 修复）：原版 controller.MoveDesignator() 终身
            // 缓存首个实例，只刷新 marker 字段、不刷新 readonly 的 map 字段——连续对接
            // 不同目标图时复用陈旧实例，marker 被生成到上一张目标图，其 SpawnSetup 再把
            // 相机跳过去（实测：选点指示器与相机全落在上一次的敌舰图上）。原版自身
            // 1:1（控制器↔图）无此问题；显式绑定 targetMap 与本次 marker。
            var designator = new Designator_MoveGravship(targetMap, marker);
            IntVec3 initialCell = InitialLandingCell(universe, universe.FrameOf(ownMap).pose.position,
                target, targetMap, gravship, desiredFacing);
            // 有意保留 ExecuteWhenFinished（勿改成 QueueFrameAction）：本委托内的
            // DesignateSingleCell（自动放置）必须先于下面的 landingMap = null 执行——
            // GUI 上下文（面板按钮触发）下 ExecuteWhenFinished 立即内联，恰好保证了
            // 这个顺序；帧队列会把它颠倒到 landingMap 清空之后。
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                CameraJumper.TryJump(initialCell.IsValid ? initialCell : targetMap.Center, targetMap,
                    CameraJumper.MovementMode.Cut);
                Find.DesignatorManager.Select(designator);
                // 初始落点只是原版选点的起点。已按脚印 clamp，但图比船小时仍可能
                // 不合法：只在原版门禁放行时自动放置，否则留给玩家手动选点。
                IntVec3 cell = initialCell;
                string autoPlace = "skip (invalid initial cell)";
                if (cell.IsValid && designator != null)
                {
                    if (!designator.CanDesignateCell(cell).Accepted)
                    {
                        cell = SpiralSearchValidLandingCell(designator, cell, targetMap);
                    }
                    if (cell.IsValid && designator.CanDesignateCell(cell).Accepted)
                    {
                        designator.DesignateSingleCell(cell);
                        autoPlace = $"placed at {cell}";
                    }
                    else
                    {
                        autoPlace = $"left for manual (initial={initialCell}, probed={cell})";
                    }
                }
                // 选点 UI 落点日志（Core 模块）：相机与自动放置的最终去向。
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"docking selection UI: on {UniverseManager.MapTag(targetMap)}; auto-place: {autoPlace}; "
                    + $"designator={Find.DesignatorManager.SelectedDesignator?.GetType().Name ?? "none"}, "
                    + $"markerSpawned={marker?.Spawned == true}, markerCells={marker?.GravshipCells?.Count() ?? -1}");
            });
            Find.GravshipController.landingMap = null;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"docking {flowLabel}: site selection on '{target.label}' (zero-consumption preview); initial cell={initialCell}, heading={snappedHeading * Mathf.Rad2Deg:F0} deg"
                + $", targetMap={UniverseManager.MapTag(targetMap)}, bounds={gravship.Bounds.Size.x}x{gravship.Bounds.Size.z}, desiredFacing={desiredFacing.AsByte}");
        }

        /// <summary>
        /// 悬停位置 → 目标对象内部投影上的初始落点：飞船中心映射为目标图格子，再按
        /// gravship 尺寸/旋转折算放置 root，再编码成 designator 输入格。
        /// </summary>
        private IntVec3 InitialLandingCell(UniverseManager universe, Vector3 hoverPosition, SpaceObject target, Map targetMap,
            Gravship gravship, Rot4 facing)
        {
            Vector2 mapPoint = SpaceTransform.UniverseToMapPoint(target, targetMap, hoverPosition);
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



        /// <summary>
        /// 对接计划取消时的快照丢弃（LandingApproachController.Cancel/Abort 调用）：
        /// 清 pending 状态；计划为运行时数据，取消后船继续自由飞行，可重新发起对接。
        /// </summary>
        internal void DiscardPendingDocking(UniverseManager universe)
        {
            if (universe != null)
            {
                universe.ShipTransitions.PendingTarget = PilotTarget.Invalid;
                universe.ShipTransitions.pendingSelectionMarker = null;
                universe.ShipTransitions.pendingSelectionWasManual = false;
                universe.ShipTransitions.pendingLeftoverThingIds = null;
            }
        }



        /// <summary>
        /// 落点反向映射——**落点注册的唯一计算点**（2026-09-21 用户定案）：只在选点确认
        /// （LandingApproachController.TryConsume）调用一次，结果存入 DockingPlan；此后
        /// 幽灵船/对齐判据一律读注册值，**不得二次计算**。
        /// 角度 = 宿主帧角 ⊕ 落地主台朝向（preview 的 PlacementData）；dockedOrigin =
        /// 放置 root + 旋转后的「口袋图几何中心 - 域包围盒 min」偏移（船位姿锚的落点）。
        /// </summary>
        internal bool ComputeLandingPose(UniverseManager universe, PilotTarget target,
            Gravship gravship, Map map,
            IntVec3 landingPos, Rot4 landingRot, out Vector3 direction, out float angle,
            out Vector2 dockedOrigin)
        {
            SpaceObject hostFrame = universe?.FrameOf(map);
            Map sourceMap = target.Map;
            Thing placementAnchor = target.Vessel?.Anchor;
            if (hostFrame == null || !target.IsValid || sourceMap == null || placementAnchor == null)
            {
                direction = default(Vector3);
                angle = 0f;
                dockedOrigin = default(Vector2);
                return false;
            }
            gravship.Rotation = landingRot;
            float hostAngle = hostFrame.pose.angle;
            if (!GravshipInterop.TryPlacedRotation(gravship, placementAnchor, out Rot4 landedFacing))
            {
                direction = default(Vector3);
                angle = 0f;
                dockedOrigin = default(Vector2);
                return false;
            }
            // 对齐的是承载图刚体帧，不是建筑/驾驶台自身朝向。最终锚建筑朝向减去
            // 源图中的局部帧角，得到与原版 Gravship 放置完全相同的刚体旋转量。
            // 飞船与 shuttle 共用本式；因此 shuttle 不再把 landingRot.AsInt 误当角度。
            angle = SpaceTransform.WrapAngle(hostAngle
                + ShipOrientation.AngleFromRot4(landedFacing) - target.FrameAngle);

            // 停放锚点唯一入口（SpaceTransform.cs「dockedOrigin 的唯一来源」；2026-09-24
            // 事故：此处曾手写「中心系恒零 − 地图系原点」的混用偏移，丢口袋图几何中心
            // 项致幽灵船/判据整体偏约半张口袋图）。pocketRoot = preview 原点（域包围盒
            // min，GravshipInterop.BuildPreview 同一约定）。
            dockedOrigin = SpaceTransform.ShipOriginFromPlacementRoot(
                sourceMap, gravship.originalPosition, landingPos, landingRot);
            direction = SpaceTransform.MapPointToUniverse(hostFrame, map, dockedOrigin);
            return true;
        }

        internal readonly struct DockingFocusHandoff
        {
            internal readonly int hostId;
            public SpaceObject host => Current.Game?.GetComponent<UniverseManager>()?.ObjectById(hostId);
            private readonly int hostVersion;
            internal readonly Vector2 mapPoint;
            internal readonly float zoom;

            internal DockingFocusHandoff(SpaceObject host, Vector2 mapPoint, float zoom)
            {
                this.hostId = host?.objectId ?? 0;
                hostVersion = host?.RepresentationVersion ?? -1;
                this.mapPoint = mapPoint;
                this.zoom = zoom;
            }

            internal bool IsValid => host?.InteriorMap != null && host.RepresentationVersion == hostVersion;
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
            return new DockingFocusHandoff(host,
                SpaceTransform.UniverseToMapPoint(host, targetMap, direction),
                Find.CameraDriver.RootSize);
        }

        internal void ApplyDockingFocusHandoff(UniverseManager universe, DockingFocusHandoff handoff)
        {
            if (handoff.IsValid) universe.SetFocusedWithCamera(handoff.host, handoff.mapPoint, handoff.zoom);
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
            IntVec3 landingRoot, Rot4 landingRot, int structureId, out Map sourceMap,
            bool impactLanding = false)
        {
            sourceMap = null;
            if (gravship == null || targetMap == null) return false;
            Map pocketMap = target.Map;
            if (pocketMap == null || pocketMap == targetMap) return false;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"docking transfer: {target.Label} {UniverseManager.MapTag(pocketMap)} -> host "
                + $"{UniverseManager.MapTag(targetMap)} (root {landingRoot}, rot {landingRot})");
            // 源图残留复核（§6：确认后新增的残留不静默处置——中止并要求重新发起）。
            // domain 与完成时刻捕获（CaptureDockingTargetForCompletion）同调用栈、同一查询入口，
            // 兼作 StripDomain 的剥离格集合（禁止从 Foundations 旋转视图反推，见其注释）。
            if (!impactLanding)
            {
                List<Thing> leftovers = CollectSourceLeftovers(pocketMap, domain);
                List<int> confirmed = pendingLeftoverThingIds ?? new List<int>();
                foreach (Thing leftover in leftovers)
                {
                    if (!confirmed.Contains(leftover.thingIDNumber))
                    {
                        Messages.Message("DeltaV_DockSourceLeftoverChanged".Translate(),
                            MessageTypeDefOf.RejectInput, false);
                        DeltaVLog.Message(DeltaVLogModule.Core,
                            $"docking transfer rejected: unconfirmed leftover #{leftover.thingIDNumber} ({leftover.def?.defName}) on source map");
                        return false;
                    }
                }
            }
            GravshipInterop.StripDomain(gravship, pocketMap, domain, includeTerrain);
            GravshipInterop.PlaceIntoMap(gravship, landingRoot, targetMap, structureId);
            sourceMap = pocketMap;
            RegisterDockingCleanup(pocketMap);
            if (!impactLanding)
            {
                PendingTarget = PilotTarget.Invalid;
                pendingLeftoverThingIds = null;
            }
            return true;
        }

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
            if (ShipRegistry.IsFreeFlight(ship))
            {
                // 因零散内容而保留宿主时，留置清单不限于对接通道。
                channelLeftovers = sourceMap.listerThings.AllThings
                    .Where(t => t.Spawned && !t.Destroyed && !domain.Contains(t.Position)
                        && (t is Pawn || t.def.category == ThingCategory.Item)).ToList();
            }
            fragments = ShipRegistry.ResidualBlocks(sourceMap, domain)
                .Where(block =>
                {
                    SubstructureOwnership ownership = SubstructureOwnership.TryGet(sourceMap);
                    return ownership != null && ownership.OwnerAt(block.First()) == ship.ownerId;
                })
                .ToList();
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
        /// 重解析 → 世界剪影重建 → 玩家注视源图时相机连续交接（SetFocusedWithCamera
        /// 单实现，与对接完成同口径；换算需在源容器仍注册时完成，善后排在之后的帧
        /// 边界）→ 重新进入驾驶 → 帧边界源图善后。返回新容器（失败 null，图不动）。
        /// </summary>
        private SpaceObject TransferShipToPocket(UniverseManager universe, ShipBase ship,
            Map sourceMap, SpaceObject sourceFrame, HashSet<IntVec3> domain, Rot4 targetFacing)
        {
            DriveSubject identity = ship.Identity;
            bool wasPiloted = universe.Pilot.PilotedTarget(universe).Identity == identity;
            bool handoverFocus = Find.CurrentMap == sourceMap && (wasPiloted || ship.Faction == Faction.OfPlayer);
            Vector3 viewUniverse = default;
            float zoom = 0f;
            if (handoverFocus)
            {
                Vector3 viewCenter = Find.CameraDriver.CurrentViewRect.CenterVector3;
                viewUniverse = SpaceTransform.MapPointToUniverse(sourceFrame, sourceMap,
                    new Vector2(viewCenter.x, viewCenter.z));
                zoom = Find.CameraDriver.RootSize;
            }
            if (wasPiloted) universe.Pilot.Exit(universe);
            ShipTransferSpec transfer = ship.Transfer;
            SpaceObject container = TransferDomainToNewPocket(universe, sourceMap, sourceFrame,
                domain, transfer.console, wreckOwnership: false,
                retainedOwnerId: transfer.ownerId, targetConsoleFacing: targetFacing,
                bodyAnchor: transfer.body, pocketSize: transfer.pocketSize,
                includeTerrain: transfer.includeTerrain);
            if (container == null) return null;
            ShipBase resolved = ShipRegistry.ShipByIdentity(identity);
            WorldShipMarkerRenderer.RebuildFor(resolved);
            if (handoverFocus)
            {
                Vector2 mapPoint = SpaceTransform.UniverseToMapPoint(container, container.map, viewUniverse);
                universe.SetFocusedWithCamera(container, mapPoint, zoom);
                if (resolved.IsValid && universe.Pilot.CanPlayerControl(resolved))
                    universe.Pilot.Enter(universe, resolved);
            }
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
            // 原版“空降落画布”忽略污渍和屋顶；它们仍是刚才明确留给宿主的真实内容。
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
