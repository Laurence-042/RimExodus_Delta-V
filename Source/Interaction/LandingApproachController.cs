using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 对接计划编排（2026-09-21 阶段4 定案；2026-09-24 解耦重构：完成路径改为直接放置，
    /// 不再重放原版 InitiateLanding——不进 cutscene/mask/LandingEnded 链路）。战术降落
    /// （移动 → 固定）= 运行时计划 + 几何完成判据：
    ///
    /// - 选点阶段（ShipFlightTransitionService.BeginOriginalLanding）：**零消费**
    ///   （游戏照常运行）——预览用空洞 Gravship（私有构造器 + 只读填充），原版选点/
    ///   合法性/禁触链路照常工作，船体/zone/区域数据全程不动；
    /// - 玩家确认落点后，InitiateLanding Prefix 把流程接到这里：**原子发布一个完整的
    ///   <see cref="DockingPlan"/>**（ship/host/targetMap/落点/偏移/转角构造时全部有效
    ///   ——「对接中但目标未知」不可表示），聚焦切回自己船、生成幽灵船位置提示、挂
    ///   随动自动驾驶（ShipAutopilot.TrackTo）；选点 marker 使命完成即销毁；
    /// - 每 tick（UniverseManager.GameComponentTick）：宿主/目标图一致性复核（失效即
    ///   整体拆除）→ 取消距离 → 位姿窗口累计保持 tick；手操打断自动驾驶不取消计划
    ///   ——完成判据是几何的，与驾驶方式无关；玩家同时最多一个计划；
    /// - 满足保持判据 → 新鲜脚印合法性复核（零消费，不合法可无损取消）→ **完成时刻的
    ///   真实捕获**（CaptureDockingTargetForCompletion：唯一消费点，zone/地板指令/区域限制/储物
    ///   组此刻收缴）→ 剥离源图 → 直接放入目标图（原版放置收尾；落点 root/旋转/盖章
    ///   归属全部读注册值）→ 帧边界处置源口袋图（FinishDocking）。
    ///
    /// 状态模型纪律：对外的「对接计划」语义唯一来源是 <see cref="ActivePlan"/>（null =
    /// 无计划；非空 ⇒ 全字段有效）。消费方（面板/门禁/补丁）一律查询本控制器，不从
    /// 派生状态推断计划存在。计划为运行时数据不存档——**本类是 UniverseManager 持有的
    /// 实例服务**（universe.Approach）：计划随组件实例存亡，读档 = 反序列化器重建组件 =
    /// 计划自动丢弃；**飞行阶段可正常存档**（存档不含计划，读档后船按已保存速度漂移、
    /// 不自动续接）；选点窗口仍禁存档（Patch_LandingApproach 门禁：marker 深序列化）。
    /// </summary>
    public class LandingApproachController
    {
        /// <summary>
        /// 一个进行中的对接计划。字段在构造时全部有效（构造即完整）；host ==
        /// FrameOf(targetMap) 的一致性由 Tick 每刻复核，失效即整体拆除——不存在
        /// 部分初始化或目标未知的计划。
        /// </summary>
        public sealed class DockingPlan
        {
            public readonly PilotTarget Target;
            public readonly int hostId;
            public readonly int hostVersion;
            public SpaceObject host => Current.Game?.GetComponent<UniverseManager>()?.ObjectById(hostId);
            public readonly Map targetMap;
            public readonly IntVec3 landingCell;
            public readonly Rot4 landingRotation;

            /// <summary>落点基架盖章的归属 ID（= 计划船的结构身份，单一数据源）。</summary>
            public readonly int structureId;

            /// <summary>选点确认时注册的宿主图连续船体中心；最终落位原样消费。</summary>
            public readonly Vector2 dockedOrigin;

            /// <summary>宿主帧局部坐标：宿主位姿 ⊕ 此偏移 = 预定落点位姿锚（绘制/判据共用）。</summary>
            public readonly Vector2 hostOffset;

            /// <summary>目标宇宙角 = 宿主角 ⊕ 此相对角。</summary>
            public readonly float relativeAngle;

            /// <summary>位姿窗口内的连续保持计数（运行时，达到设置值即完成）。</summary>
            public int holdTicks;

            public DockingPlan(PilotTarget target, SpaceObject host, Map targetMap,
                IntVec3 landingCell, Rot4 landingRotation,
                Vector2 dockedOrigin, float relativeAngle)
            {
                Target = target;
                this.hostId = host?.objectId ?? 0;
                hostVersion = host?.RepresentationVersion ?? -1;
                this.targetMap = targetMap;
                this.landingCell = landingCell;
                this.landingRotation = landingRotation;
                structureId = target.Vessel.Transfer.ownerId;
                this.dockedOrigin = dockedOrigin;
                hostOffset = SpaceTransform.MapToShipLocal(targetMap, dockedOrigin);
                this.relativeAngle = SpaceTransform.WrapAngle(relativeAngle);
            }


            /// <summary>计划脚印（唯一来源：船 = 当前控制域；shuttle = 舱体 def 脚印，§7.4）。</summary>
            public HashSet<IntVec3> PlanFootprint()
            {
                return Target.IsValid ? new HashSet<IntVec3>(Target.Vessel.HullCells) : new HashSet<IntVec3>();
            }

            /// <summary>预定落点当前位姿（随宿主运动重算：宿主位姿 ⊕ 计划偏移）。</summary>
            public void TargetPose(out Vector3 direction, out float angle)
            {
                direction = SpaceTransform.MapPointToUniverse(host, targetMap, dockedOrigin);
                angle = SpaceTransform.WrapAngle(host.pose.angle + relativeAngle);
            }

            /// <summary>计划船与目标中心的当前距离（格）。</summary>
            public float Distance()
            {
                return OrbitSphere.ArcCells(shipContainerPose().position, host.pose.position);
            }

            /// <summary>计划主体控制/物理直接消费其容器位姿（自由飞行船/shuttle，§11）。</summary>
            internal SpacePose shipContainerPose()
            {
                UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
                Map bodyMap = Target.Map;
                SpaceObject frame = universe?.FrameOf(bodyMap);
                return frame?.pose ?? new SpacePose(Vector3.right, 0f);
            }
        }

        // ---- 状态（运行时，不存档）----
        // plan 非 null ⇒ 全字段有效。confirmMarker 是选点期捕获的 vanilla 对象，
        // 计划发布时即销毁（幽灵船接管视觉提示）。
        private DockingPlan plan;
        private GravshipLandingMarker confirmMarker;

        /// <summary>对齐容差（唯一读口）：完成时按目标位姿精确对齐，容差只需覆盖
        /// 手操可达精度（自动驾驶的虚空动力终段天然收敛到窗口内）；可在设置中调整。
        /// 角度设置以度存储、此处换算弧度。</summary>
        internal static float HoldPositionWindow =>
            Mathf.Max(0.05f, DeltaVMod.CurrentSettings?.dockingPositionWindow ?? 0.35f);

        internal static float HoldAngleWindow =>
            Mathf.Max(0.0017f, (DeltaVMod.CurrentSettings?.dockingAngleWindowDeg ?? 1.15f) * Mathf.Deg2Rad);

        /// <summary>当前是否处于对齐窗口内（保持计数 > 0）——幽灵船变色提示的依据；
        /// 对齐后保持 dockingHoldTicks 即完成对接。</summary>
        public bool AlignedNow => ActivePlan?.holdTicks > 0;

        /// <summary>当前对接计划；无计划为 null。非空 ⇒ 全字段有效。</summary>
        public DockingPlan ActivePlan => plan;

        /// <summary>计划飞行阶段。</summary>
        public bool ApproachActive => plan != null;

        /// <summary>计划相关流程存在。</summary>
        public bool AnyActive => plan != null;



        /// <summary>计划主体（船或 shuttle）的统一句柄。</summary>
        internal PilotTarget ApproachTarget => ActivePlan?.Target ?? PilotTarget.Invalid;



        /// <summary>计划飞行主体（船或 shuttle）的统一句柄。</summary>
        internal PilotTarget FlowTarget => plan?.Target
            ?? Current.Game?.GetComponent<UniverseManager>()?.ShipTransitions.PendingTarget ?? PilotTarget.Invalid;

        /// <summary>计划船的图（发射封锁的地图键）。</summary>
        internal Map ApproachShipMap => plan?.Target.Map;

        /// <summary>原版 GravshipLandingMarker.BeginLanding Prefix：缓存 marker 引用（Destroy 前），
        /// 供选点确认时核对发起者身份。</summary>
        public void CaptureMarker(GravshipLandingMarker marker)
        {
            confirmMarker = marker;
        }

        /// <summary>
        /// InitiateLanding Prefix 的接管判定：是本 mod 的战术降落（选点阶段的捕获匹配）则
        /// 原子发布对接计划并拦截原版动画管线。口袋图意外失效时降级为取消——此刻仍未
        /// 消费任何数据，零损失；仍拦截原方法（预览清单不能进原版降落管线）。
        /// </summary>
        public bool TryConsume(UniverseManager universe, Gravship gravship, Map map,
            IntVec3 landingPos, Rot4 landingRot)
        {
            PilotTarget target = universe.ShipTransitions.PendingTarget;
            if (target.Identity.key <= 0 || confirmMarker == null)
            {
                if (target.Identity.key <= 0) confirmMarker = null;
                return false;
            }
            if (!ReferenceEquals(confirmMarker.gravship, gravship)) return false;
            SpaceObject own = universe.FrameOf(target.Map);
            SpaceObject host = universe.FrameOf(map);
            if (!target.Vessel.CanDriveIn(own) || host == null
                || !universe.ShipTransitions.ComputeLandingPose(universe, target, gravship,
                    map, landingPos, landingRot, out _, out float targetAngle, out Vector2 dockedOrigin))
            {
                CancelSelectionOrPlan(universe, "landing pose or ship lost", silent: true);
                Messages.Message("DeltaV_ApproachAborted".Translate(), MessageTypeDefOf.RejectInput, false);
                return true;
            }
            // 唯一计算与发布；复核、幽灵和最终放置只读此计划。
            plan = new DockingPlan(target, host, map, landingPos, landingRot,
                dockedOrigin, SpaceTransform.WrapAngle(targetAngle - host.pose.angle));
            universe.ShipTransitions.pendingSelectionWasManual = false;
            Find.GravshipController?.Notify_LandingAreaConfirmationStarted(null);
            universe.SetFocused(own);
            DockingGhostRenderer.BuildFor(plan);
            EngageAutopilot(universe);
            Messages.Message("DeltaV_DockPlanStarted".Translate(target.Label, host.label),
                MessageTypeDefOf.PositiveEvent, false);
            return true;
        }

        /// <summary>（重）挂随动自动驾驶：计划建立时与面板「继续自动驾驶」共用。
        /// 手操驾驶中挂自动驾驶先退出手操（自动驾驶接管驾驶位；打断路径 = 按驾驶键）。</summary>
        public void EngageAutopilot(UniverseManager universe)
        {
            DockingPlan active = ActivePlan;
            if (active == null) return;
            PilotTarget target = active.Target;
            if (universe != null && universe.Pilot.PilotedTarget(universe).Identity == target.Identity)
            {
                universe.Pilot.Exit(universe);
            }
            universe.Autopilot.TrackTo(DriveSubject.Of(target), active.host, active.hostOffset, active.relativeAngle,
                OnNavigationFailed, allowVoidThrust: true);
            universe.Pilot.ArmAutopilotInput(target);
        }

        /// <summary>随动任务当前是否在驾驶计划主体。</summary>
        public bool AutopilotEngaged(UniverseManager universe)
        {
            return ActivePlan != null && universe.Autopilot.IsActive(DriveSubject.Of(ActivePlan.Target));
        }

        private static void OnNavigationFailed()
        {
            // 随动任务异常退出（船图失效等）或被手动停止：计划保留——完成判据是几何的，
            // 可手操对接或经面板重新开启自动驾驶。船图/目标失效由 Tick 的守卫拆除计划。
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.Approach.ApproachActive == true)
            {
                universe.Pilot.DisarmAutopilotInput(universe.Approach.ActivePlan.Target);
                Messages.Message("DeltaV_DockAutopilotStopped".Translate(), MessageTypeDefOf.NeutralEvent, false);
            }
        }

        /// <summary>
        /// UniverseManager.GameComponentTick 每 tick 驱动：宿主/目标图一致性复核、
        /// 取消距离、位姿窗口与保持计数（对接条件 = 与预定落点位姿重合保持可配置 tick 数）。
        /// </summary>
        public void Tick(UniverseManager universe)
        {
            DockingPlan active = plan;
            if (active == null) return;
            if (universe == null || Current.Game == null)
            {
                CancelSelectionOrPlan(universe, "universe lost", silent: true);
                return;
            }
            if (!active.Target.IsValid || active.Target.Map?.Disposed != false)
            {
                CancelSelectionOrPlan(universe, "approach ship state lost");
                return;
            }
            if (active.host?.RepresentationVersion != active.hostVersion || active.targetMap == null || active.targetMap.Disposed || !Find.Maps.Contains(active.targetMap)
                || !ReferenceEquals(universe.FrameOf(active.targetMap), active.host))
            {
                // 目标图失效或换帧（被收纳、战略离场等）。
                CancelSelectionOrPlan(universe, "docking target lost");
                return;
            }
            // 只取一次当前容器位姿；落点与角度始终读确认时固定的计划值。
            SpacePose shipPose = active.shipContainerPose();
            if (OrbitSphere.ArcCells(shipPose.position, active.host.pose.position)
                > DeltaVMod.CurrentSettings.dockingCancelDistance)
            {
                CancelSelectionOrPlan(universe, "left docking range");
                return;
            }

            active.TargetPose(out Vector3 targetDir, out float targetAngle);
            Vector2 posErr = OrbitSphere.Offset(targetDir, shipPose.position);
            float angleErr = Mathf.Abs(SpaceTransform.WrapAngle(targetAngle - shipPose.angle));
            bool inWindow = posErr.magnitude <= HoldPositionWindow && angleErr <= HoldAngleWindow;
            if (inWindow)
            {
                active.holdTicks++;
            }
            else if (active.holdTicks > 0)
            {
                active.holdTicks = 0;
            }
            if (active.holdTicks >= Mathf.Max(1, DeltaVMod.CurrentSettings.dockingHoldTicks))
            {
                CompleteDocking(universe);
            }
        }

        /// <summary>
        /// 对接条件满足：新鲜脚印合法性复核（零消费，不合法可无损取消）→ **完成时刻的
        /// 真实捕获**（唯一消费点，与移除/放置同一调用栈）→ 直接放入目标图 →
        /// 帧边界处置源口袋图。完成后双方进入对接状态（同图多船，派生），共享容器
        /// 沿用宿主运动状态继续惯性运动（§6 已定）。
        /// </summary>
        private void CompleteDocking(UniverseManager universe)
        {
            DockingPlan active = plan;
            if (active.host?.RepresentationVersion != active.hostVersion || active.targetMap == null || active.targetMap.Disposed)
            {
                CancelSelectionOrPlan(universe, "target map lost");
                return;
            }
            if (!LandingSiteStillValid(active))
            {
                Messages.Message("DeltaV_DockPlanSiteInvalid".Translate(), MessageTypeDefOf.RejectInput, false);
                CancelSelectionOrPlan(universe, "landing site no longer valid", silent: true);
                return;
            }

            universe.Autopilot.Detach(DriveSubject.Of(active.Target), "docking complete");
            universe.Pilot.DisarmAutopilotInput(active.Target);

            if (!ReferenceEquals(universe.FrameOf(active.targetMap), active.host))
            {
                CancelSelectionOrPlan(universe, "docking target lost");
                return;
            }

            // 飞船与 shuttle 共用同一完成链：当前脚印 → CaptureDomain → StripDomain →
            // PlaceIntoMap。shuttle 是无驾驶台与基架的单建筑域，转移差异是
            // includeTerrain=false、ownerId=0，以及放置后的原版发射冷却。
            PilotTarget dockingTarget = PilotTarget.Of(ShipRegistry.ShipByIdentity(active.Target.Identity));
            if (!dockingTarget.IsValid)
            {
                Abort(universe, "approach ship lost before capture");
                return;
            }
            Map sourceMap = dockingTarget.Map;
            // 聚焦交接在完成 tick 同步捕获（飞船与 shuttle 同一调用）：host 取计划快照
            // targetMap（Tick/CompleteDocking 已双重校验 FrameOf(targetMap) == active.host），
            // 玩家不在源图时 CaptureDockingFocusHandoff 自然返回 default。
            var handoff = universe.ShipTransitions.CaptureDockingFocusHandoff(
                universe, sourceMap, active.targetMap);
            Gravship gravship = universe.ShipTransitions.CaptureDockingTargetForCompletion(
                dockingTarget, out HashSet<IntVec3> capturedCells, out bool includeTerrain);
            if (gravship == null)
            {
                Abort(universe, "docking body capture failed");
                return;
            }
            gravship.Rotation = active.landingRotation;
            DockingGhostRenderer.Destroy();

            if (!universe.ShipTransitions.CompleteTransferIntoHost(universe, dockingTarget,
                    gravship, capturedCells, includeTerrain, active.targetMap,
                    active.landingCell, active.landingRotation, active.structureId,
                    out Map transferredSource))
            {
                Abort(universe, "transfer into host map failed");
                return;
            }
            plan = null;
            // 船体注入落地能力；公共源图收尾仍在帧边界执行。
            var completion = new DockingCompletion(universe, dockingTarget.Vessel,
                active.targetMap, transferredSource, handoff);
            completion.OnTransferedIntoHost();
            universe.QueueFrameAction(completion.Finish);
        }

        /// <summary>
        /// 落点合法性复核（同选点门禁全链，含禁触规则）：以**当前**脚印重建零消费预览
        /// 刷新 marker 缓存——对接期间的扩建会改变脚印，旧缓存不可信。
        /// 原版判定与禁触补丁全部读 Find.CurrentMap（2026-09-21 实测事故）——复核期间
        /// 同帧临时聚焦目标图，结束后切回；相机位置由 CurrentMap setter 的记忆/恢复
        /// 机制保持不变。
        /// </summary>
        private bool LandingSiteStillValid(DockingPlan active)
        {
            if (!active.landingCell.IsValid || confirmMarker == null) return false;
            if (!active.Target.IsValid) return false;
            Map originalMap = Find.CurrentMap;
            if (originalMap == active.targetMap) return ValidateLandingSite(active);
            Current.Game.CurrentMap = active.targetMap;
            try
            {
                return ValidateLandingSite(active);
            }
            finally
            {
                Current.Game.CurrentMap = originalMap;
            }
        }

        private bool ValidateLandingSite(DockingPlan active)
        {
            try
            {
                PilotTarget target = PilotTarget.Of(ShipRegistry.ShipByIdentity(active.Target.Identity));
                Gravship preview = GravshipInterop.BuildPreview(target);
                if (preview == null) return false;
                // 复核必须按选点朝向旋转脚印（2026-09-21 实测事故：漏设 Rotation 时
                // Foundations/锚格换算按默认朝向错位，脚印整体偏移，复核必败）。
                preview.Rotation = active.landingRotation;
                confirmMarker.gravship = preview;
                confirmMarker.CacheCells();
                var designator = new Designator_MoveGravship(active.targetMap, confirmMarker);
                // 计划保存放置 root；原版 CanDesignateCell 接收鼠标输入格。
                // 偶数尺寸时两者不恒等，必须经唯一 Gravship 适配入口反编码。
                AcceptanceReport report = GravshipInterop.CanDesignateRoot(designator,
                    active.landingCell, preview.Bounds.Size, active.landingRotation);
                return report.Accepted;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 取消对接（选点阶段或计划阶段通用）：选点阶段清除 marker/确认状态（此刻零
        /// 消费，取消无损失）；计划阶段取消后继续自由飞行，速度保持现状。均可重新
        /// 发起。silent 供守卫性收尾。
        /// </summary>
        public void CancelSelectionOrPlan(UniverseManager universe, string reason = null, bool silent = false)
        {
            PilotTarget target = FlowTarget;
            bool selectionPhase = plan == null && universe?.ShipTransitions.pendingSelectionMarker != null;
            bool restoreManual = selectionPhase && universe?.ShipTransitions.pendingSelectionWasManual == true;
            plan = null;
            if (target.IsValid)
            {
                universe?.Autopilot.Detach(DriveSubject.Of(target), "plan cancelled"); // 静默摘除：取消路径不需要失败反馈
                universe?.Pilot.DisarmAutopilotInput(target);
            }
            DockingGhostRenderer.Destroy();
            if (selectionPhase)
            {
                CancelSiteSelection(universe);
                if (restoreManual && target.IsValid)
                {
                    universe.SetFocused(universe.FrameOf(target.Map));
                    universe.Pilot.Enter(universe, target);
                }
            }
            universe?.ShipTransitions.DiscardPendingDocking(universe);
            confirmMarker = null;
            if (!silent)
            {
                Messages.Message("DeltaV_DockPlanCancelled".Translate(), MessageTypeDefOf.NeutralEvent, false);
            }
        }

        /// <summary>选点阶段取消的收尾：销毁本流程的 marker、结束原版确认状态。
        /// 只使用创建时记录的 marker 引用，不按船型反查。</summary>
        private static void CancelSiteSelection(UniverseManager universe)
        {
            Find.GravshipController?.Notify_LandingAreaConfirmationStarted(null);
            Find.DesignatorManager.Deselect();
            GravshipLandingMarker own = universe?.ShipTransitions.pendingSelectionMarker;
            if (own != null && !own.Destroyed) own.Destroy();
        }

        /// <summary>异常出口（真转移失败）：取消导航、丢弃快照。若捕获已发生但放置
        /// 失败，原版已消费船体——放回源图根位置；否则船回自由飞行，可重新发起。</summary>
        private void Abort(UniverseManager universe, string reason)
        {
            Log.Error($"[DeltaV] Docking completion aborted: {reason}.");
            PilotTarget target = plan?.Target ?? PilotTarget.Invalid;
            plan = null;
            if (target.IsValid)
            {
                universe?.Autopilot.Detach(DriveSubject.Of(target), "aborted");
                universe?.Pilot.DisarmAutopilotInput(target);
            }
            universe?.ShipTransitions.DiscardPendingDocking(universe);
            confirmMarker = null;
            DockingGhostRenderer.Destroy();
            Messages.Message("DeltaV_ApproachAborted".Translate(), MessageTypeDefOf.RejectInput, false);
        }

        /// <summary>同实例上的流程清零（dev ResetObjects 重建场景用；读档路径 = 组件
        /// 重建，天然无需调用）。静默丢弃计划与选点状态，不弹消息。</summary>
        public void ResetFlow(UniverseManager universe)
        {
            plan = null;
            confirmMarker = null;
            universe?.ShipTransitions.DiscardPendingDocking(universe);
            DockingGhostRenderer.Destroy();
        }
    }
}
