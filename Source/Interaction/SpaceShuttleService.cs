using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// shuttle comp（太空战架构 §7）：舱体建筑 = 原版 CompTransporter 装货 + 原版
    /// CompLaunchable 发射语义（起飞门禁 = 原版 CanLaunch 结果直取，月池屋顶通行
    /// 见 Patch_MoonPoolRoof）+ 虚拟推进器 XML 配置（默认 8 台；位置/推力矢量逐台定义）+ 侵入能力
    ///（per-def canBoard）。停靠时是宿主图内的船成员；起飞后持有独立的普通容器。
    /// 与基架船共用公共能力：飞行/驾驶/自动驾驶/受击/普通停靠全部复用同一实现；
    /// shuttle 特有的只有单建筑域属性、落地冷却与侵入碰撞编排。
    /// </summary>
    public class CompProperties_SpaceShuttle : CompProperties
    {
        /// <summary>具备侵入式对接能力（跳帮特化；普通 shuttle false）。</summary>
        public bool canBoard;

        /// <summary>
        /// 默认虚拟推进器布局。每项的位置/推力矢量都在 shuttle 船体帧中表达；
        /// DefModExtension_BoardingProfile.virtualThrusters 非空时整体覆盖本表。
        /// </summary>
        public List<ShuttleThrusterConfig> virtualThrusters;
        public ThrusterVisualProfile exhaust = new ThrusterVisualProfile();
        public ThrusterVisualProfile afterburner = new ThrusterVisualProfile
        { flame = true, flameSize = 1.4f, flameSpeed = 24f, flameLifetime = 20,
            smokeSize = 1.5f, smokeLifetime = 180, smokeGrowth = 3.5f };

        public CompProperties_SpaceShuttle()
        {
            compClass = typeof(CompSpaceShuttle);
        }
    }

    public class CompSpaceShuttle : ThingComp, IHardDockingExecutor
    {
        public ShipCombatState combat = new ShipCombatState();
        private const int DefaultBoostRestartPercent = 50;
        private ShipBase vessel;
        internal static double TransportedMass(Thing item)
        {
            double weight = CompStructureMass.StackMass(item);
            // Match CompTransporter.MassUsage for a single manifest entry.
            if (item is Pawn pawn
                && InventoryCalculatorsUtility.ShouldIgnoreInventoryOf(pawn, IgnorePawnsInventoryMode.IgnoreIfAssignedToUnload))
                weight -= MassUtility.InventoryMass(pawn) * item.stackCount;
            return weight;
        }

        private int lastBoostBurnTick = -1;
        private CompAfterburnerTank afterburnerTank;
        private bool boostRestartPending = true;
        private int boostRestartPercent = DefaultBoostRestartPercent;
        internal bool intrusionDriveActive;
        private CompAfterburnerTank AfterburnerTank => afterburnerTank ??= parent.GetComp<CompAfterburnerTank>();
        internal int BoostTicksRemaining => AfterburnerTank?.BurnTicksRemaining ?? 0;
        private bool WaitingForBoost => boostRestartPending
            && (AfterburnerTank?.FuelPercentOfMax ?? 0f) * 100f < boostRestartPercent;
        internal float BoostMultiplier => intrusionDriveActive
            && (lastBoostBurnTick == Find.TickManager.TicksGame || (BoostTicksRemaining > 0 && !WaitingForBoost))
            ? Mathf.Max(1f, BoardingProfile?.boostMultiplier ?? 1f) : 1f;
        internal void ConsumeBoostTick(ShipPhysics.MassReport report)
        {
            if (!intrusionDriveActive || AfterburnerTank == null) return;
            int tick = Find.TickManager.TicksGame;
            if (lastBoostBurnTick == tick) return;
            foreach (ShipPhysics.Thruster thruster in report.thrusters)
            {
                if (thruster.shuttle != this || !thruster.boostable || !thruster.operational
                    || thruster.thrust <= thruster.baseThrust || thruster.throttle < 0.0001f) continue;
                AfterburnerTank.Notify_UsedThisTick();
                lastBoostBurnTick = tick;
                boostRestartPending = BoostTicksRemaining == 0;
                return;
            }
        }
        internal void SetBoostRestartPercent(int percent)
        {
            bool wasWaiting = WaitingForBoost;
            boostRestartPercent = Mathf.Clamp(percent, 1, 100);
            boostRestartPending = wasWaiting && WaitingForBoost;
        }
        public override void ReceiveCompSignal(string signal)
        {
            if (signal != "Refueled" && signal != "RanOutOfFuel") return;
            if (AfterburnerTank == null) return;
            if (BoostTicksRemaining == 0) boostRestartPending = true;
            else if (!WaitingForBoost) boostRestartPending = false;
        }
        public virtual ShipBase Vessel => vessel ??= new ShuttleShip(this);
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            ShipStructureChanges.Notify(parent.Map, parent as Building);
            Current.Game?.GetComponent<UniverseManager>()?.Controls.For(Vessel);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            DriveSubject identity = Vessel.Identity;
            universe?.QueueFrameAction(() => universe.Controls.ReconcileOwner(identity, map));
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref combat, "combat");
            Scribe_Values.Look(ref boostRestartPending, "boostRestartPending", true);
            Scribe_Values.Look(ref boostRestartPercent, "boostRestartPercent", DefaultBoostRestartPercent);
            combat ??= new ShipCombatState();
        }

        public CompProperties_SpaceShuttle ShuttleProps => (CompProperties_SpaceShuttle)props;

        /// <summary>跳帮档位扩展（可空；真继承 def 把档位放 modExtensions，见 DefModExtension_BoardingProfile）。</summary>
        public DefModExtension_BoardingProfile BoardingProfile => parent?.def.GetModExtension<DefModExtension_BoardingProfile>();

        /// <summary>是否具备侵入式对接能力（唯一解析点：modExtension.canBoard ∨ comp.canBoard）。</summary>
        public bool CanBoard => BoardingProfile?.canBoard == true || ShuttleProps.canBoard;

        /// <summary>虚拟推进器布局唯一解析点：modExtension 非空表优先，否则用 comp 默认表。</summary>
        public IReadOnlyList<ShuttleThrusterConfig> ThrusterLayout =>
            BoardingProfile?.virtualThrusters != null && BoardingProfile.virtualThrusters.Count > 0
                ? BoardingProfile.virtualThrusters
                : ShuttleProps.virtualThrusters;

        public CompLaunchable Launchable => parent.TryGetComp<CompLaunchable>();

        /// <summary>所在图是否为宇宙容器图（太空侧语义适用）。</summary>
        public SpaceObject Container => Current.Game?.GetComponent<UniverseManager>()?.FrameOf(parent.Map);

        ShipBase IHardDockingExecutor.Vessel => Vessel;
        Thing IHardDockingExecutor.Body => parent;
        ThingDef IHardDockingExecutor.CollisionDef => parent.def;
        HardDockingCompletion IHardDockingExecutor.Completion => HardDockingCompletion.Collision;
        bool IHardDockingExecutor.RequiresAvoidanceDisabled => true;
        bool IHardDockingExecutor.UsesProjectileRole => true;
        float IHardDockingExecutor.ArrivalVelocityTolerance =>
            ShipAutopilot.DefaultArrivalVelocityTolerance;
        float IHardDockingExecutor.ArrivalApproachSpeed => 0f;
        float IHardDockingExecutor.NoseHalfWidth => parent.def.Size.x * 0.5f;
        float IHardDockingExecutor.NoseForwardOffset => (parent.def.Size.z - 1) * 0.5f;
        string IHardDockingExecutor.StartLabelKey => "DeltaV_ShuttleBoardGizmo";
        string IHardDockingExecutor.StartTipKey => "DeltaV_ShuttleBoardGizmoTip";
        string IHardDockingExecutor.CancelLabelKey => "DeltaV_ShuttleBoardCancel";
        string IHardDockingExecutor.CancelTipKey => "DeltaV_ShuttleBoardCancelTip";
        string IHardDockingExecutor.BlockReasonKey(UniverseManager universe, SpaceObject source,
            bool playerInitiated) => CanBoard && Vessel.CanDriveIn(source)
                ? null : "DeltaV_ShuttleBoardRequiresFlight";
        bool IHardDockingExecutor.ValidFor(SpaceObject source) => parent?.Spawned == true
            && !parent.Destroyed && source?.map == parent.Map && Vessel.CanDriveIn(source) && CanBoard;
        bool IHardDockingExecutor.TryResolveTarget(SpaceObject target, Vector2 targetMapPoint,
            out IntVec3 targetCell)
        {
            targetCell = new IntVec3(Mathf.FloorToInt(targetMapPoint.x), 0,
                Mathf.FloorToInt(targetMapPoint.y));
            return target?.map != null && targetCell.InBounds(target.map);
        }
        bool IHardDockingExecutor.TargetStillValid(SpaceObject target, IntVec3 targetCell)
            => target?.map != null && targetCell.InBounds(target.map);
        void IHardDockingExecutor.NotifyHardDockingActive(bool active) => intrusionDriveActive = active;
        void IHardDockingExecutor.CompleteHardDocking(UniverseManager universe, HardDockingContact contact)
        {
            Vector2 forwardAtHit = SpaceTransform.TransportTangent(
                SpaceTransform.Rotate(Vector2.right, contact.source.pose.angle
                    + ShipOrientation.AngleFromRot4(parent.Rotation)),
                contact.source.pose.position, contact.universePoint);
            Vector2 direction = SpaceTransform.UniverseTangentToMapDirection(
                contact.target, contact.target.map, contact.universePoint, forwardAtHit);
            Rot4 facing = ShipOrientation.Rot4FromAngle(Mathf.Atan2(direction.y, direction.x));
            universe.Shuttles.PlaceBoardedShuttle(contact.source, (Building)parent,
                contact.target, contact.targetCell, facing);
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            base.PostDestroy(mode, previousMap);
            CompTransporter transporter = parent.TryGetComp<CompTransporter>();
            List<Thing> released = transporter?.innerContainer?.ToList() ?? new List<Thing>();
            // 原版 transporter 在 lastLaunchTick == 当前 tick 时跳过 DeSpawn 卸货。
            // 若同 tick 已实际毁艇，复用原版卸货入口，再让帧边界清理检查幸存人员/物资。
            if (previousMap != null && !previousMap.Disposed)
                transporter?.innerContainer.TryDropAll(
                    parent.Position, previousMap, ThingPlaceMode.Near);
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe != null && previousMap != null && !previousMap.Disposed && released.Count > 0)
                universe.QueueFrameAction(() =>
                {
                    foreach (Thing thing in released)
                        if (thing?.Spawned == true && thing.Map == previousMap)
                            universe.Scatter.TryScatter(thing);
                });
            universe?.ShipTransitions.QueueEmptyDepartedMapCleanup(universe, previousMap);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            bool playerSide = parent.Faction == Faction.OfPlayer || DebugSettings.godMode;
            if (universe == null || !parent.Spawned || !playerSide)
            {
                yield break;
            }
            SpaceObject container = Container;
            if (AfterburnerTank != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DeltaV_ShuttleBoostRestart".Translate(boostRestartPercent),
                    defaultDesc = "DeltaV_ShuttleBoostRestartTip".Translate(
                        Mathf.FloorToInt(100f * AfterburnerTank.FuelPercentOfMax))
                        + "\n" + (WaitingForBoost ? "DeltaV_ShuttleBoostCharging" : "DeltaV_ShuttleBoostReady").Translate(),
                    icon = parent.def.uiIcon,
                    action = () => Find.WindowStack.Add(new Dialog_Slider(
                        value => "DeltaV_ShuttleBoostRestart".Translate(value),
                        1, 100, SetBoostRestartPercent, boostRestartPercent))
                };
            }
            // 起飞（停靠中）：门禁 = 原版 CanLaunch 结果直取（含装货/超重/冷却）；
            // 月池屋顶通行见 Patch_MoonPoolRoof。
            if (container != null && !Vessel.CanDriveIn(container) && Launchable != null)
            {
                CompLaunchable launchable = Launchable;
                var launch = new Command_Action
                {
                    defaultLabel = "DeltaV_ShuttleLaunchGizmo".Translate(),
                    defaultDesc = "DeltaV_ShuttleLaunchGizmoTip".Translate(),
                    icon = TexCommand.Attack,
                    action = delegate { universe.Shuttles.Launch((Building)parent); }
                };
                AcceptanceReport gate = universe.ShipTransitions.CanDepart(Vessel);
                if (!gate)
                {
                    launch.Disable(gate.Reason);
                }
                yield return launch;
            }
            // 驾驶切换、对接和侵入目标指定统一在 ShipStatusPanel。
        }
    }

    /// <summary>
    /// shuttle 服务（太空战架构 §7/§10，universe.Shuttles 实例服务）：起飞（原版校验
    /// 直取 + 单建筑捕获进口袋）、停靠复用飞船的 Gravship 捕获/剥离/放置链、
    /// 侵入编排（真实推进器截击 + 共同弹体碰撞）。
    /// 与其他飞船实现共用：SpaceObject 驾驶契约、ShipAutopilot（普通对接）、
    /// PilotService（PilotTarget）、SpaceCollision（联合效果）。运行时数据不序列化。
    /// </summary>
    public sealed class SpaceShuttleService
    {
        private readonly UniverseManager universe;



        public SpaceShuttleService(UniverseManager universe)
        {
            this.universe = universe;
        }

        // ---- 起飞（§7.1/§7.2）----

        /// <summary>
        /// 起飞门禁 = 原版 CompLaunchable.CanLaunch 结果直取（装货/燃料/超重/冷却；
        /// 月池屋顶通行在 Patch_MoonPoolRoof）。通过即把舱体建筑（含 held 内容）移入新
        /// 口袋容器：单建筑搬运 = DeSpawn/Spawn（不搬基架、不进控制域——舱体 held 的
        /// 乘员/货物随 CompTransporter 落盘照常）；位姿/速度 = 宿主刚体上舱体锚点
        ///（RigidPointPose，含自旋切向继承）。冷却写原版 lastLaunchTick（字段镜像
        /// 单点）；起飞与停靠回落共用（§7.4「触发原版降落冷却」同入口）。
        /// </summary>
        public void Launch(Building shuttleBody)
        {
            CompSpaceShuttle comp = shuttleBody?.TryGetComp<CompSpaceShuttle>();
            CompLaunchable launchable = comp?.Launchable;
            Map sourceMap = shuttleBody?.Map;
            SpaceObject host = sourceMap != null ? universe.FrameOf(sourceMap) : null;
            if (comp == null || launchable == null || host == null || sourceMap == null
                || comp.Vessel.CanDriveIn(host))
            {
                return;
            }
            AcceptanceReport gate = universe.ShipTransitions.CanDepart(comp.Vessel);
            if (!gate)
            {
                Messages.Message("DeltaV_ShuttleLaunchDenied".Translate(gate.Reason),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            // 统一起飞核心（§7.2「与船脱离同一套」）：TransferDomainToNewPocket 的舱体
            // 参数化调用——已装入的乘员/货物随舱体进入新口袋，地面物品按船体能力过滤；
            // 不搬基架（宿主甲板/屋顶原样保留）；聚焦交接在核心内完成
            //（与船脱离同一段 SetFocusedWithCamera 代码）。
            SpaceObject container = universe.ShipTransitions.LaunchShuttleToPocket(universe, shuttleBody);
            if (container == null)
            {
                Log.Error($"[DeltaV] Shuttle '{shuttleBody.LabelCap}': transfer-to-pocket failed.");
                return;
            }
            StartLaunchCooldown(launchable);
            DeltaVLog.Message(DeltaVLogModule.Combat,
                $"shuttle launched: '{shuttleBody.LabelCap}' -> MiniShip container #{container.objectId} " +
                $"({UniverseManager.MapTag(container.map)})");
            Messages.Message("DeltaV_ShuttleLaunched".Translate(shuttleBody.LabelCap),
                MessageTypeDefOf.PositiveEvent, false);
            // 转移、聚焦与玩家驾驶交接已由 ShipTransitions 的共同船体转移入口完成。
        }

        /// <summary>原版 CompLaunchable.lastLaunchTick 字段镜像（起飞与停靠回落共用的冷却单点）。</summary>
        private static readonly System.Reflection.FieldInfo LastLaunchTickField =
            HarmonyLib.AccessTools.Field(typeof(CompLaunchable), "lastLaunchTick");

        internal static void StartLaunchCooldown(CompLaunchable launchable)
        {
            if (launchable != null && LastLaunchTickField != null)
            {
                LastLaunchTickField.SetValue(launchable, Find.TickManager.TicksGame);
            }
        }

        // ---- 查询 ----

        /// <summary>本船或其控制域内可执行侵入的载员艇；战术选择与执行共用此门禁。</summary>
        internal CompSpaceShuttle BoardingCraftOf(ShipBase ship)
        {
            if (!ship.IsValid) return null;
            return ShipRegistry.AllShipsOnMap(ship.Map)
                .Where(vessel => vessel.CanBoard && ship.Contains(vessel.Anchor) && vessel.Faction == ship.Faction)
                .Select(vessel => vessel.Anchor.TryGetComp<CompSpaceShuttle>())
                .FirstOrDefault(comp => comp != null
                    && comp.parent.TryGetComp<CompTransporter>()?.innerContainer?.Any(t => t is Pawn) == true
                    && (comp.Vessel.CanDriveIn(comp.Container)
                        || comp.Launchable != null && comp.Launchable.CanLaunch()));
        }

        internal void TryBoardFrom(ShipBase ship, CombatTarget enemy)
        {
            CompSpaceShuttle craft = BoardingCraftOf(ship);
            if (craft == null) return;
            if (!craft.Vessel.CanDriveIn(craft.Container)) Launch((Building)craft.parent);
            if (craft.Vessel.CanDriveIn(craft.Container))
                EngageIntrusion((Building)craft.parent, enemy, playerInitiated: false);
        }

        // ---- 侵入式对接（§7.5 = 截击 MPC + 联合后效 + 共同降落链）----

        internal bool CanStartIntrusion(ShipBase vessel, SpaceObject container, bool playerInitiated)
            => IntrusionBlockReasonKey(vessel, container, playerInitiated) == null;

        internal string IntrusionBlockReasonKey(ShipBase vessel, SpaceObject container,
            bool playerInitiated) => universe.HardDocking.BlockReasonKey(
                vessel, container, playerInitiated);

        /// <summary>侵入指挥：点击敌舰外壳指定目标容器（Targeter 流程内经 ShellPicking 解析）。</summary>
        public void BeginBoardTargeting(PilotTarget shuttle)
            => universe.HardDocking.BeginTargeting(shuttle.Vessel);

        /// <summary>
        /// 进入侵入：弹体服务登记现有艇体并请求截击 MPC，不创建第二个容器。
        /// </summary>
        public void EngageIntrusion(Building body, CombatTarget handle, bool playerInitiated = true)
            => universe.HardDocking.Engage(body?.TryGetComp<CompSpaceShuttle>()?.Vessel,
                handle, playerInitiated);

        /// <summary>退出侵入（打断/目标失效/读档后）：移除弹体角色（目标默认保留可重入）。</summary>
        public void BreakIntrusion(SpaceObject container, bool dropTarget)
            => universe.HardDocking.Cancel(container);

        /// <summary>侵入命中后效：在帧边界以真正的 Gravship 搬运链降落。</summary>
        public void PlaceBoardedShuttle(SpaceObject container, Building hull, SpaceObject defender,
            IntVec3 entry, Rot4 facing)
        {
            int sourceId = container.objectId, sourceVersion = container.RepresentationVersion;
            int targetId = defender.objectId, targetVersion = defender.RepresentationVersion;
            DriveSubject identity = PilotTarget.Of(hull).Identity;
            universe.QueueFrameAction(delegate
            {
                container = universe.ObjectById(sourceId);
                defender = universe.ObjectById(targetId);
                if (container?.RepresentationVersion != sourceVersion || defender?.RepresentationVersion != targetVersion) return;
                Map targetMap = defender?.map;
                Map sourceMap = container?.map;
                ShipBase vessel = ShipRegistry.ShipByIdentity(identity);
                if (!vessel.IsValid || vessel.Anchor != hull || vessel.Map != sourceMap
                    || !vessel.CanDriveIn(container) || container.projectileRoleOverride
                    || sourceMap == null || targetMap == null || targetMap.Disposed
                    || !universe.objects.Contains(defender) || !universe.objects.Contains(container))
                    return;

                if (!entry.IsValid || !entry.InBounds(targetMap)) return;
                // Thing.Position 是原版多格建筑的中心锚，不是脚印左下角。
                IntVec3 desiredCenter = entry - facing.FacingCell * 2;
                CellRect occupied = GenAdj.OccupiedRect(desiredCenter, facing, hull.def.size);
                if (!SpaceImpactEffects.TryCollectBoardingBreach(
                    targetMap, entry, facing, occupied, hull.def, out HashSet<Building> breach))
                {
                    if (hull.Faction == Faction.OfPlayer)
                        Messages.Message("DeltaV_ShuttleBoardNoRoom".Translate(), MessageTypeDefOf.RejectInput, false);
                    return;
                }

                PilotTarget shuttle = PilotTarget.Of(hull);
                Gravship gravship = universe.ShipTransitions.CaptureDockingTargetForCompletion(
                    shuttle, out HashSet<IntVec3> capturedCells, out bool includeTerrain);
                if (gravship == null) return;
                Rot4 placementRotation = ShipOrientation.PlacementRotation(hull.Rotation, facing);
                gravship.Rotation = placementRotation;
                if (!gravship.ThingPlacements.TryGetValue(hull, out RimWorld.Planet.PositionData.Data placement))
                    return;
                if (placement.rotation != facing) return;
                IntVec3 landingRoot = desiredCenter - placement.local;
                var handoff = Find.CurrentMap == sourceMap
                    ? new ShipFlightTransitionService.DockingFocusHandoff(defender,
                        new Vector2(entry.x + 0.5f, entry.z + 0.5f),
                        Find.CameraDriver.RootSize, shuttle.Identity, hasSelection: true)
                    : default(ShipFlightTransitionService.DockingFocusHandoff);
                SpaceImpactEffects.BreachBoardingCorridor(targetMap, breach, hull);
                if (!universe.ShipTransitions.CompleteTransferIntoHost(universe, shuttle,
                    gravship, capturedCells, includeTerrain, targetMap, landingRoot,
                    placementRotation, 0, out Map transferredSource))
                {
                    Log.Error("[DeltaV] Boarding breach completed but shared Gravship transfer failed.");
                    return;
                }
                var completion = new DockingCompletion(
                    universe, shuttle.Vessel, targetMap, transferredSource, handoff);
                completion.OnTransferedIntoHost();
                universe.QueueFrameAction(completion.Finish);
            });
        }

    }
}
