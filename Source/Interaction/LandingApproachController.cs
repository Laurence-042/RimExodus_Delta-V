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
            public readonly Ship ship;

            /// <summary>shuttle 舱体（§7.4 计划变体；null = 船计划。ship 与 shuttleBody 恒互斥）。</summary>
            public readonly Building shuttleBody;
            public readonly SpaceObject host;
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

            public DockingPlan(Ship ship, SpaceObject host, Map targetMap,
                IntVec3 landingCell, Rot4 landingRotation, int structureId,
                Vector2 dockedOrigin, float relativeAngle)
            {
                this.ship = ship;
                this.host = host;
                this.targetMap = targetMap;
                this.landingCell = landingCell;
                this.landingRotation = landingRotation;
                this.structureId = structureId;
                this.dockedOrigin = dockedOrigin;
                hostOffset = SpaceTransform.MapToShipLocal(targetMap, dockedOrigin);
                this.relativeAngle = SpaceTransform.WrapAngle(relativeAngle);
            }

            /// <summary>shuttle 计划（脚印来源 = 舱体 def 脚印；structureId 不盖章——舱体无归属）。</summary>
            public DockingPlan(Building shuttleBody, SpaceObject host, Map targetMap,
                IntVec3 landingCell, Rot4 landingRotation, Vector2 dockedOrigin,
                float relativeAngle)
            {
                ship = Ship.Invalid;
                this.shuttleBody = shuttleBody;
                this.host = host;
                this.targetMap = targetMap;
                this.landingCell = landingCell;
                this.landingRotation = landingRotation;
                structureId = 0;
                this.dockedOrigin = dockedOrigin;
                hostOffset = SpaceTransform.MapToShipLocal(targetMap, dockedOrigin);
                this.relativeAngle = SpaceTransform.WrapAngle(relativeAngle);
            }

            /// <summary>计划主体的驾驶目标句柄（船或 shuttle，二选一）。</summary>
            public PilotTarget Target => IsShuttlePlan ? PilotTarget.Of(shuttleBody) : PilotTarget.Of(ship);

            public bool IsShuttlePlan => shuttleBody != null;

            /// <summary>计划脚印（唯一来源：船 = 当前控制域；shuttle = 舱体 def 脚印，§7.4）。</summary>
            public HashSet<IntVec3> PlanFootprint()
            {
                if (IsShuttlePlan) return new HashSet<IntVec3>(shuttleBody.OccupiedRect().Cells);
                return ShipRegistry.ControlDomain(ship);
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
                Map bodyMap = IsShuttlePlan ? shuttleBody.Map : ship.map;
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

        /// <summary>计划船（无计划为 Invalid；供驾驶门禁做身份比较，不用于推断计划存在性）。</summary>
        internal Ship ApproachShip => ActivePlan?.ship ?? Ship.Invalid;

        /// <summary>计划主体（船或 shuttle）的统一句柄。</summary>
        internal PilotTarget ApproachTarget => ActivePlan?.Target ?? PilotTarget.Invalid;

        /// <summary>选点、计划飞行中的船。流程是运行时事实，不写回任何持久状态。</summary>
        internal Ship FlowShip => plan?.ship ?? Ship.Invalid;

        /// <summary>计划飞行主体（船或 shuttle）的统一句柄。</summary>
        internal PilotTarget FlowTarget => plan?.Target ?? PilotTarget.Invalid;

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
            Ship ship = universe.ShipTransitions.PendingDockingShip(universe);
            Building pendingShuttle = universe.ShipTransitions.pendingShuttleBody;
            // 接管语义：无 pending 战术目标（船或 shuttle）或未捕获 marker = 非本流程
            //（战略降落等）——清掉无关 marker 后放行原版；战术选点确认时目标必然有效。
            if ((!ship.IsValid && pendingShuttle == null) || confirmMarker == null)
            {
                if (!ship.IsValid && pendingShuttle == null) confirmMarker = null;
                return false;
            }
            if (!ReferenceEquals(confirmMarker.gravship, gravship))
            {
                return false;
            }
            if (pendingShuttle != null)
            {
                // ---- shuttle 计划发布（§7.4：同一发布语义，脚印来源 = 舱体） ----
                if (universe == null || !pendingShuttle.Spawned || pendingShuttle.Map == null
                    || pendingShuttle.Map.Disposed || !Find.Maps.Contains(pendingShuttle.Map)
                    || universe.FrameOf(pendingShuttle.Map)?.baseTag != SpaceObjectTag.Shuttle)
                {
                    Log.Error($"[DeltaV] Docking plan for shuttle '{pendingShuttle.LabelCap}': flight pocket lost; cancelling (nothing consumed).");
                    CancelSelectionOrPlan(universe, "shuttle pocket lost", silent: true);
                    Messages.Message("DeltaV_ApproachAborted".Translate(), MessageTypeDefOf.RejectInput, false);
                    return true;
                }
                SpaceObject shuttleHost = universe.FrameOf(map);
                if (shuttleHost == null)
                {
                    CancelSelectionOrPlan(universe, "target frame lost", silent: true);
                    Messages.Message("DeltaV_ApproachAborted".Translate(), MessageTypeDefOf.RejectInput, false);
                    return true;
                }
                if (!universe.ShipTransitions.ComputeLandingPose(universe,
                        PilotTarget.Of(pendingShuttle), gravship, map, landingPos, landingRot,
                        out _, out float shuttleAngle, out Vector2 shuttleOrigin))
                {
                    CancelSelectionOrPlan(universe, "shuttle landing pose lost", silent: true);
                    Messages.Message("DeltaV_ApproachAborted".Translate(), MessageTypeDefOf.RejectInput, false);
                    return true;
                }
                var shuttlePlan = new DockingPlan(pendingShuttle, shuttleHost, map,
                    landingPos, landingRot, shuttleOrigin,
                    SpaceTransform.WrapAngle(shuttleAngle - shuttleHost.pose.angle));
                plan = shuttlePlan;
                universe.ShipTransitions.pendingSelectionWasManual = false;
                Find.GravshipController?.Notify_LandingAreaConfirmationStarted(null);
                universe.SetFocused(universe.FrameOf(pendingShuttle.Map));
                DockingGhostRenderer.BuildFor(shuttlePlan);
                EngageAutopilot(universe);
                Messages.Message("DeltaV_DockPlanStarted".Translate(pendingShuttle.LabelCap, shuttleHost.label),
                    MessageTypeDefOf.PositiveEvent, false);
                // 选点期字段随发布清零（计划为权威状态；船流程的 pendingShip 保持
                // 到 FinishDocking 是历史形态，shuttle 不复制）。
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"shuttle '{pendingShuttle.LabelCap}': landing registered on '{shuttleHost.label}' "
                    + $"{UniverseManager.MapTag(map)}; cell={landingPos}, rot={landingRot.AsInt}, "
                    + $"dockedOrigin=({shuttleOrigin.x:F1},{shuttleOrigin.y:F1})");
                universe.ShipTransitions.DiscardPendingDocking(universe);
                return true;
            }
            if (universe == null || !ship.IsValid || ship.map == null || ship.map.Disposed
                || !Find.Maps.Contains(ship.map))
            {
                Log.Error($"[DeltaV] Docking plan for structure #{ship.ownerId}: flight pocket map lost; cancelling (nothing consumed).");
                CancelSelectionOrPlan(universe, "flight pocket map lost", silent: true);
                Messages.Message("DeltaV_ApproachAborted".Translate(), MessageTypeDefOf.RejectInput, false);
                return true;
            }
            SpaceObject host = universe.FrameOf(map);
            if (host == null)
            {
                Log.Error($"[DeltaV] Docking plan for structure #{ship.ownerId}: target frame lost; cancelling (nothing consumed).");
                CancelSelectionOrPlan(universe, "target frame lost", silent: true);
                Messages.Message("DeltaV_ApproachAborted".Translate(), MessageTypeDefOf.RejectInput, false);
                return true;
            }

            // 落点反向映射（落点注册的唯一计算点）：宿主 ⊕ 局部偏移、宿主角 ⊕ 相对转角
            // ——随动目标、幽灵船与完成窗口都按这两分量随宿主重算。
            if (!universe.ShipTransitions.ComputeLandingPose(universe, PilotTarget.Of(ship), gravship,
                    map, landingPos, landingRot,
                    out _, out float targetAngle, out Vector2 dockedOrigin))
            {
                CancelSelectionOrPlan(universe, "landing pose owner lost", silent: true);
                Messages.Message("DeltaV_ApproachAborted".Translate(), MessageTypeDefOf.RejectInput, false);
                return true;
            }
            var newPlan = new DockingPlan(ship, host, map, landingPos, landingRot, ship.ownerId,
                dockedOrigin, SpaceTransform.WrapAngle(targetAngle - host.pose.angle));

            // 原子发布：全部校验与构造完成之后才切换全局状态，发布前不产生任何可观测副作用。
            plan = newPlan;
            universe.ShipTransitions.pendingSelectionWasManual = false;
            if (DeltaVLog.Enabled(DeltaVLogModule.Core))
            {
                // 只在确认事件复走原版门禁一次，和完成期的同入口复核对照；
                // 不改变选点结果或计划状态。
                string acceptance = "not checked (target map not focused)";
                if (Find.CurrentMap == map)
                {
                    try
                    {
                        AcceptanceReport check = GravshipInterop.CanDesignateRoot(
                            new Designator_MoveGravship(map, confirmMarker), landingPos,
                            gravship.Bounds.Size, landingRot);
                        acceptance = check.Accepted ? "accepted" : check.Reason;
                    }
                    catch (System.Exception e)
                    {
                        acceptance = "diagnostic check threw " + e.GetType().Name;
                    }
                }
                IntVec3 designatorInput = GravshipInterop.RootToDesignatorInput(
                    landingPos, gravship.Bounds.Size, landingRot);
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"docking site selected: {Patch_DesignatorMoveGravship_AdjacentLanding.DescribeFootprint(confirmMarker, map, landingPos)} input={designatorInput} check={acceptance}");
            }

            // 结束原版确认状态；marker 引用保留（原版 BeginLanding 已将其销毁，但
            // 完成期复核需要它构造 Designator_MoveGravship——设计器只读其缓存字段，
            // 与旧到达重放流程同款用法）。取消/清零路径负责释放。
            Find.GravshipController?.Notify_LandingAreaConfirmationStarted(null);

            // 聚焦切回自己船（计划期玩家保留控制；目标以标准非聚焦壳呈现、真实油门
            // 火焰可见）。SetFocused 只切参考系，不触碰任何 pose。
            universe.SetFocused(universe.FrameOf(ship.map));

            // 幽灵船位置提示（派生缓存，不进 universe.objects）。
            DockingGhostRenderer.BuildFor(newPlan);

            EngageAutopilot(universe);
            if (Find.TickManager.CurTimeSpeed == TimeSpeed.Paused)
            {
                DeltaVLog.Message(DeltaVLogModule.Core,
                    "docking plan engaged while game paused; approach frozen until unpaused (motion is tick-driven)");
            }
            Messages.Message("DeltaV_DockPlanStarted".Translate(
                    universe.LabelForOwner(ship.ownerId), host.label),
                MessageTypeDefOf.PositiveEvent, false);
            Vector2 registeredOrigin = newPlan.dockedOrigin;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"structure #{ship.ownerId}: landing registered on '{host.label}' (hostMap#{map.uniqueID} {map.Size.x}x{map.Size.z}); cell={landingPos}, rot={landingRot.AsInt}, dockedOrigin=({registeredOrigin.x:F1},{registeredOrigin.y:F1}), offset=({newPlan.hostOffset.x:F1},{newPlan.hostOffset.y:F1}), relAngle={newPlan.relativeAngle * Mathf.Rad2Deg:F1} deg");
            return true;
        }

        /// <summary>（重）挂随动自动驾驶：计划建立时与面板「继续自动驾驶」共用。
        /// 手操驾驶中挂自动驾驶先退出手操（自动驾驶接管驾驶位；打断路径 = 按驾驶键）。</summary>
        public void EngageAutopilot(UniverseManager universe)
        {
            DockingPlan active = ActivePlan;
            if (active == null) return;
            PilotTarget target = active.Target;
            if (universe != null && universe.Pilot.PilotedTarget(universe).ControlKey == target.ControlKey)
            {
                universe.Pilot.Exit(universe);
            }
            universe.Autopilot.TrackTo(target, active.host, active.hostOffset, active.relativeAngle,
                OnNavigationFailed, allowVoidThrust: true);
            universe.Pilot.ArmAutopilotInput(target);
        }

        /// <summary>随动任务当前是否在驾驶计划主体。</summary>
        public bool AutopilotEngaged(UniverseManager universe)
        {
            return ActivePlan != null && universe.Autopilot.IsActive(ActivePlan.Target.ControlKey);
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
            if (active.IsShuttlePlan)
            {
                if (!active.shuttleBody.Spawned || active.shuttleBody.Destroyed
                    || active.shuttleBody.Map == null || active.shuttleBody.Map.Disposed)
                {
                    CancelSelectionOrPlan(universe, "approach shuttle lost");
                    return;
                }
            }
            else if (!active.ship.IsValid || active.ship.map == null || active.ship.map.Disposed)
            {
                CancelSelectionOrPlan(universe, "approach ship state lost");
                return;
            }
            if (active.targetMap == null || active.targetMap.Disposed || !Find.Maps.Contains(active.targetMap)
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
                if (active.holdTicks == 1)
                {
                    DeltaVLog.Message(DeltaVLogModule.Core,
                        $"docking window entered: posErr={posErr.magnitude:F3} cells, angleErr={angleErr * Mathf.Rad2Deg:F2} deg");
                }
            }
            else if (active.holdTicks > 0)
            {
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"docking window lost after {active.holdTicks} ticks (posErr={posErr.magnitude:F3}, angleErr={angleErr * Mathf.Rad2Deg:F2} deg)");
                active.holdTicks = 0;
            }
            if (active.holdTicks >= Mathf.Max(1, DeltaVMod.CurrentSettings.dockingHoldTicks))
            {
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"docking conditions met: window held {active.holdTicks} ticks; starting completion transfer");
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
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"docking completion: {(active.IsShuttlePlan ? $"'{active.shuttleBody.LabelCap}'" : $"structure #{active.ship.ownerId}")} -> host #{active.host.objectId} {UniverseManager.MapTag(active.targetMap)}; currentMap={UniverseManager.MapTag(Find.CurrentMap)}");
            if (active.targetMap == null || active.targetMap.Disposed)
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

            universe.Autopilot.Detach(active.Target, "docking complete");
            universe.Pilot.DisarmAutopilotInput(active.Target);

            if (!ReferenceEquals(universe.FrameOf(active.targetMap), active.host))
            {
                CancelSelectionOrPlan(universe, "docking target lost");
                return;
            }

            // 飞船与 shuttle 共用同一完成链：当前脚印 → CaptureDomain → StripDomain →
            // PlaceIntoMap。shuttle 只是「驾驶台与基架合一」的单建筑域，唯一差异是
            // includeTerrain=false、ownerId=0，以及放置后的原版发射冷却。
            PilotTarget dockingTarget;
            Ship ship = Ship.Invalid;
            if (active.IsShuttlePlan)
            {
                dockingTarget = PilotTarget.Of(active.shuttleBody);
            }
            else
            {
                ship = ShipRegistry.ShipByOwner(active.ship.ownerId);
                if (!ship.IsValid)
                {
                    Abort(universe, "approach ship lost before capture");
                    return;
                }
                dockingTarget = PilotTarget.Of(ship);
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
                    active.landingCell, active.landingRotation, active.structureId))
            {
                Abort(universe, "transfer into host map failed");
                return;
            }
            plan = null;
            // 完成流程工厂（唯一的类型分叉点）：公共收尾在 DockingCompletion 基类
            // 模板方法，船/shuttle 差异由落地冷却与标签钩子处理；运行时键由注销清理。
            // 帧上下文收尾（QueueFrameAction 统一机制）：CompleteDocking 在 Approach.Tick
            //（GameComponentTick）内执行，源图销毁/聚焦交接/通道铺设必须离开 tick
            // 调用栈再执行——旧 ExecuteWhenFinished 写法实际在 Tick 内联执行（碰巧
            // 位于积分循环之前才没炸），语义与本注释一直声称的「帧边界」不符。
            DockingCompletion completion = active.IsShuttlePlan
                ? new DockingCompletion.ShuttleDockingCompletion(
                    universe, active.shuttleBody, active.targetMap, handoff)
                : new DockingCompletion.ShipDockingCompletion(
                    universe, ship.ownerId, active.targetMap, handoff);
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
            if (active.IsShuttlePlan)
            {
                if (!active.shuttleBody.Spawned || active.shuttleBody.Destroyed) return false;
            }
            else if (!active.ship.IsValid) return false;
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
                Gravship preview;
                if (active.IsShuttlePlan)
                {
                    if (!active.shuttleBody.Spawned || active.shuttleBody.Map == null) return false;
                    preview = GravshipInterop.BuildPreview(
                        new HashSet<IntVec3>(active.shuttleBody.OccupiedRect().Cells),
                        null, active.shuttleBody.Map, active.shuttleBody);
                }
                else
                {
                    Ship ship = ShipRegistry.ShipByOwner(active.ship.ownerId);
                    if (!ship.IsValid) return false;
                    preview = GravshipInterop.BuildPreview(ShipRegistry.ControlDomain(ship),
                        ship.console.parent as Building);
                }
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
                if (DeltaVLog.Enabled(DeltaVLogModule.Core))
                {
                    IntVec3 designatorInput = GravshipInterop.RootToDesignatorInput(
                        active.landingCell, preview.Bounds.Size, active.landingRotation);
                    DeltaVLog.Message(DeltaVLogModule.Core,
                        $"docking site completion check: {Patch_DesignatorMoveGravship_AdjacentLanding.DescribeFootprint(confirmMarker, active.targetMap, active.landingCell)} input={designatorInput} check={(report.Accepted ? "accepted" : report.Reason)}");
                }
                if (!report.Accepted)
                {
                    DeltaVLog.Message(DeltaVLogModule.Core,
                        $"docking site revalidation failed: {report.Reason}");
                }
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
            Ship ship = plan != null && !plan.IsShuttlePlan
                ? plan.ship
                : universe?.ShipTransitions.PendingDockingShip(universe) ?? Ship.Invalid;
            Building shuttle = plan?.shuttleBody ?? universe?.ShipTransitions.pendingShuttleBody;
            PilotTarget target = plan != null ? plan.Target
                : (ship.IsValid ? PilotTarget.Of(ship)
                    : (shuttle != null ? PilotTarget.Of(shuttle) : PilotTarget.Invalid));
            bool selectionPhase = plan == null && target.IsValid;
            bool restoreManual = selectionPhase && universe?.ShipTransitions.pendingSelectionWasManual == true;
            plan = null;
            if (target.IsValid)
            {
                universe?.Autopilot.Detach(target, "plan cancelled"); // 静默摘除：取消路径不需要失败反馈
                universe?.Pilot.DisarmAutopilotInput(target);
            }
            universe.ShipTransitions.DiscardPendingDocking(universe);
            DockingGhostRenderer.Destroy();
            if (selectionPhase)
            {
                CancelSiteSelection(universe, ship);
                if (restoreManual && target.IsValid)
                {
                    universe.SetFocused(universe.FrameOf(target.Map));
                    universe.Pilot.Enter(universe, target);
                }
            }
            if (!silent)
            {
                Messages.Message("DeltaV_DockPlanCancelled".Translate(), MessageTypeDefOf.NeutralEvent, false);
            }
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"docking {(selectionPhase ? "site selection" : "plan")} cancelled: {reason ?? "user"}");
        }

        /// <summary>选点阶段取消的收尾：销毁本流程的 marker、结束原版确认状态。
        /// marker 经「预览 Gravship 的主台 = 计划船主台」识别，不误伤原版降落选点的
        /// marker。</summary>
        private static void CancelSiteSelection(UniverseManager universe, Ship ship)
        {
            Find.GravshipController?.Notify_LandingAreaConfirmationStarted(null);
            // 取消选点时退出设计器（选点中断不再持有鼠标模式）。
            Find.DesignatorManager.Deselect();
            // 本流程创建的 marker 直接销毁（shuttle 预览无主台可比，§7.4）；
            // 船流程再按主台身份兜底扫一遍（防多处残留）。
            GravshipLandingMarker own = universe?.ShipTransitions.pendingSelectionMarker;
            if (own != null && !own.Destroyed) own.Destroy();
            if (!ship.IsValid) return;
            foreach (Map map in Find.Maps)
            {
                foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.GravshipLandingMarker).ToList())
                {
                    if (thing is GravshipLandingMarker marker && marker != own
                        && marker.gravship?.PilotConsole == ship.console.parent)
                    {
                        marker.Destroy();
                    }
                }
            }
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
                universe?.Autopilot.Detach(target, "aborted");
                universe?.Pilot.DisarmAutopilotInput(target);
            }
            universe.ShipTransitions.DiscardPendingDocking(universe);
            DockingGhostRenderer.Destroy();
            Messages.Message("DeltaV_ApproachAborted".Translate(), MessageTypeDefOf.RejectInput, false);
        }

        /// <summary>同实例上的流程清零（dev ResetObjects 重建场景用；读档路径 = 组件
        /// 重建，天然无需调用）。静默丢弃计划与选点状态，不弹消息。</summary>
        public void ResetFlow(UniverseManager universe)
        {
            plan = null;
            confirmMarker = null;
            universe.ShipTransitions.DiscardPendingDocking(universe);
            DockingGhostRenderer.Destroy();
        }
    }
}
