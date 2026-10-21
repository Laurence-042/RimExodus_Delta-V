using System.Collections.Generic;
using System.Linq;
using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
public enum ShipControlState : byte
{
    Uncontrolled,
    Manual,
    Autopilot
}

internal enum PilotEntryBlockReason : byte
{
    None,
    InvalidTarget,
    NoUniverseFrame,
    GravityRestricted,
    ApproachConflict,
    NoAuthority
}

/// <summary>
/// 驾驶目标句柄：普通船与单建筑船的共同事实只通过 ShipBase 读取。
/// 具体布局与特殊操作能力由船体提供。
/// </summary>
public readonly struct PilotTarget
{
    public readonly ShipBase Vessel;

    private PilotTarget(ShipBase vessel) { Vessel = vessel; }

    public static PilotTarget Of(ShipBase vessel) => new PilotTarget(vessel);
    public static PilotTarget Of(Ship ship) => new PilotTarget(ship);
    public static PilotTarget Of(Building shuttleBody) => new PilotTarget(shuttleBody?.TryGetComp<CompSpaceShuttle>()?.Vessel);

    public bool IsValid => Vessel?.IsValid == true;
    public static readonly PilotTarget Invalid = default;

    /// <summary>目标所在图（shuttle = 舱体所在图，含 MiniShip 口袋图与停靠宿主图）。</summary>
    public Map Map => Vessel?.Map;

    /// <summary>船体帧角 σ（船 = 主台朝向；shuttle = 舱体朝向）。</summary>
    public float FrameAngle => Vessel?.FrameAngle ?? 0f;

    /// <summary>目标船体帧锚（连续 Map 坐标；船 = 控制域包围盒中心，shuttle = 舱体脚印中心）。</summary>
    public Vector2 HullCenter => Vessel?.HullCenter ?? Vector2.zero;

    public Faction Faction => Vessel?.Faction;

    public DriveSubject Identity => Vessel?.Identity ?? default;

    /// <summary>流程/诊断标签（船 = 归属名，shuttle = 舱体名；仅显示用）。</summary>
    public string Label => Vessel?.Label ?? "";
}

internal readonly struct PilotPanelAction
{
    internal readonly string Label;
    internal readonly string Tooltip;
    internal readonly Action Execute;

    internal PilotPanelAction(string label, Action execute, string tooltip = null)
    {
        Label = label;
        Execute = execute;
        Tooltip = tooltip;
    }
}

internal readonly struct HeadingPanelAction
{
    internal readonly Rot4 Facing;
    internal readonly Action Execute;
    internal HeadingPanelAction(Rot4 facing, Action execute)
    {
        Facing = facing;
        Execute = execute;
    }
}

    /// <summary>
    /// 统一驾驶契约的共同基类（解耦架构 §5.4 **强制继承架构**）：NPC controller 与玩家
    /// controller 必须是本类的两个不同子类——明确的类型边界是后续敌舰 AI 接入的架构
    /// 前提，不得用派系、布尔标志或控制模式分支模拟这两种角色。
    ///
    /// 基类定义双方一致的驾驶入口与生命周期；公共执行流程（驾驶门禁、任务交接、出力）
    /// 统一处理。两子类不得分别实现一套结构查询、拆分、推力分配或物理——拆分是
    /// controller 之外的公共机制（TransitionService），子类只负责交互边界：
    /// 玩家子类在执行前加确认交互，NPC 子类由内部 AI 组件自动发起请求、直接执行。
    /// controller 与 AI/驾驶任务的运行时状态遵守 universe 实例归属纪律，不新增跨档静态。
    /// </summary>
    public abstract class ShipController
    {
        protected readonly UniverseManager universe;
        public readonly DriveSubject OwnerId;
        internal readonly ShipFireControl FireControl;
        internal readonly ShipAutoManeuverControl AutoManeuver;

        /// <summary>当前驾驶目标（任一 ShipBase 实现）。无效 = 未驾驶。</summary>
        internal PilotTarget target = PilotTarget.Invalid;

        protected ShipController(UniverseManager universe, DriveSubject ownerId)
        {
            this.universe = universe;
            OwnerId = ownerId;
            FireControl = new ShipFireControl(universe, ownerId);
            AutoManeuver = new ShipAutoManeuverControl(universe, ownerId);
        }

        internal abstract bool GunnersEnabled { get; }
        internal abstract bool WantsVolley { get; }
        internal void BindShip(ShipBase ship)
        {
            FireControl.PrunePointDefense(ship);
            // 驾驶会话也按身份跟随；旧主台或旧 Map 的快照可能已经失效。
            if (target.Identity == OwnerId) target = PilotTarget.Of(ship);
        }

        // The target's controller owns its sidebar operations. Common piloting and
        // docking actions live here; target-specific actions belong to subclasses.
        internal virtual void CollectPanelActions(PilotTarget panelTarget, List<PilotPanelAction> actions)
        {
            if (!panelTarget.IsValid || !HasAuthority(panelTarget)) return;
            if (universe.FrameOf(panelTarget.Map)?.projectileRoleOverride == true) return;
            ShipControlState state = universe.Pilot.GetControlState(panelTarget);
            bool hasPlan = universe.Approach.ActivePlan?.Target.Identity.Equals(panelTarget.Identity) == true;
            string pilotLabel = state == ShipControlState.Manual && hasPlan
                ? "DeltaV_ControlSwitchAutopilot".Translate()
                : universe.Pilot.IsPlayerInputArmed(panelTarget)
                    ? "DeltaV_PilotButtonExit".Translate()
                    : "DeltaV_PilotButtonEnter".Translate();
            actions.Add(new PilotPanelAction(pilotLabel,
                () => universe.Pilot.ToggleControl(universe, panelTarget)));
            if (hasPlan)
                actions.Add(new PilotPanelAction("DeltaV_DockCancelButton".Translate(),
                    () => universe.Approach.CancelSelectionOrPlan(universe, "user")));
            else if (panelTarget.Vessel.State == ShipDerivedState.FreeFlight)
                actions.Add(new PilotPanelAction("DeltaV_SyncButton".Translate(),
                    () => universe.Worksites.SelectSyncTarget(panelTarget)));
            if (panelTarget.Vessel.State == ShipDerivedState.FreeFlight)
                actions.Add(new PilotPanelAction("DeltaV_StartEVA".Translate(),
                    () => universe.Worksites.Start(panelTarget)));

        }

        internal virtual void CollectCombatPanelActions(ShipBase ship, bool hasWeapons,
            List<PilotPanelAction> beforeGroups, List<PilotPanelAction> afterGroups) { }
        internal virtual void CollectHeadingPanelActions(ShipBase ship,
            List<HeadingPanelAction> actions) { }

        /// <summary>驾驶与战斗共用船归属，但未驾驶仍可瞄准或自动点防。</summary>
        internal void TickCombat()
        {
            if (OwnerId.key <= 0) return;
            ShipBase ship = ShipRegistry.ShipByIdentity(OwnerId);
            if (!ship.IsValid || universe.FrameOf(ship.Map) is not SpaceObject container
                || container.HasTag(SpaceObjectTag.Ship) != true
                || !HasAuthority(PilotTarget.Of(ship))) return;
            if (!WantsVolley && !universe.Weapons.HasPointDefenseWeapons(ship.Map)) return;
            if (TryGetVolleyIntent(ship, container, out ShipVolleyIntent intent))
                FireControl.TickVolley(ship, container, intent);
            FireControl.TickPointDefense(ship, container);
        }

        protected abstract bool TryGetVolleyIntent(ShipBase ship, SpaceObject container,
            out ShipVolleyIntent intent);

        public bool Active => target.IsValid && target.Map != null;

        /// <summary>该方是否对目标有控制权（玩家 = 派系门禁；NPC = AI 归属）。</summary>
        protected internal abstract bool HasAuthority(PilotTarget target);

        /// <summary>
        /// 公共驾驶门禁（两子类同一流程）：目标有效、所在图有宇宙容器且零重力、非流程
        /// 互斥。与其他船同图的船同样可进入驾驶（2026-09-24 统一分离定案）——首次推进器出力
        /// 即触发控制域提取（分离 gate 在 RequestThrust 收口）；无容器/重力图
        /// 只能逆重战略发射，无驾驶。shuttle 目标同门禁（独占口袋图或停靠宿主图）。
        /// </summary>
        public virtual bool CanEnter(PilotTarget target)
        {
            return EntryBlockReason(target) == PilotEntryBlockReason.None;
        }

        internal PilotEntryBlockReason EntryBlockReason(PilotTarget target)
        {
            if (!target.IsValid) return PilotEntryBlockReason.InvalidTarget;
            Map map = target.Map;
            if (map == null || universe.FrameOf(map) == null || !target.Vessel.CanDriveIn(universe.FrameOf(map)))
                return PilotEntryBlockReason.NoUniverseFrame;
            if (!FreeFlightBootstrap.IsZeroGravity(universe, map))
                return PilotEntryBlockReason.GravityRestricted;
            if (universe.Approach.AnyActive
                && !universe.Approach.ApproachTarget.Identity.Equals(target.Identity))
                return PilotEntryBlockReason.ApproachConflict;
            return HasAuthority(target)
                ? PilotEntryBlockReason.None : PilotEntryBlockReason.NoAuthority;
        }

        public virtual void Enter(PilotTarget target)
        {
            this.target = target;
        }

        public virtual void Exit()
        {
            target = PilotTarget.Invalid;
        }

        /// <summary>
        /// 合成本 tick 驾驶指令（归一化 command + 辅助轴标志）。玩家子类 = 手操输入 +
        /// 飞行辅助；NPC 子类 = 内部 AI 组件产生的机动请求。指令最终都经同一分配器/
        /// 积分链执行——子类不得自行写油门或积分。
        /// </summary>
        public abstract PilotService.PilotCommand ComposeCommand();

        // ---- 确认边界（§8.2：提示属于玩家 controller；分离属于公共机制）----

        /// <summary>
        /// 出力前分离确认（拆分/脱离两方向共用，文案由公共机制按方向备好）。NPC 不弹
        /// 窗，直接执行。
        /// </summary>
        internal abstract void PresentSeparationConfirmation(PilotTarget target, string text,
            System.Action onConfirm, System.Action onCancel);
    }

    public readonly struct ShipVolleyIntent
    {
        internal readonly CombatTarget target;
        internal readonly bool main, secondary, fireAll, predictIntercept;
        internal ShipVolleyIntent(Vector3 point, bool main, bool secondary)
        { target = CombatTarget.At(point); this.main = main; this.secondary = secondary; fireAll = predictIntercept = false; }
        internal ShipVolleyIntent(CombatTarget target, bool predictIntercept)
        { this.target = target; this.predictIntercept = predictIntercept; main = secondary = false; fireAll = true; }
    }

    /// <summary>
    /// 玩家 controller：手操输入与自动驾驶两种驱动来源都在本子类（切入自动驾驶不把
    /// 玩家 controller 换成 NPC controller），并承担按键接管、拆船及留置确认等交互。
    /// </summary>
    public sealed class PlayerShipController : ShipController
    {
        private SpaceAimController aim;
        private bool battleStations;
        private bool autoManeuverEnabled;

        internal PlayerShipController(UniverseManager universe, DriveSubject ownerId) : base(universe, ownerId)
        {
        }

        internal override void CollectPanelActions(PilotTarget panelTarget, List<PilotPanelAction> actions)
        {
            base.CollectPanelActions(panelTarget, actions);
            if (!panelTarget.IsValid || !panelTarget.Vessel.CanBoard
                || !HasAuthority(panelTarget)) return;
            SpaceObject frame = universe.FrameOf(panelTarget.Map);
            if (frame == null) return;
            if (frame.projectileRoleOverride)
            {
                actions.Add(new PilotPanelAction("DeltaV_ShuttleBoardCancel".Translate(),
                    () => universe.Shuttles.BreakIntrusion(frame, dropTarget: true),
                    "DeltaV_ShuttleBoardCancelTip".Translate()));
                return;
            }
            actions.Add(new PilotPanelAction(
                "DeltaV_PanelBoardArm".Translate() + ": "
                    + (universe.Shuttles.IntrusionArmed(frame) ? "DeltaV_On" : "DeltaV_Off").Translate(),
                () => universe.Shuttles.ToggleIntrusionArmed(frame),
                "DeltaV_PanelBoardArmTip".Translate()));
            if (universe.Shuttles.IntrusionArmed(frame))
                actions.Add(new PilotPanelAction("DeltaV_ShuttleBoardGizmo".Translate(),
                    () => universe.Shuttles.BeginBoardTargeting(panelTarget),
                    "DeltaV_ShuttleBoardGizmoTip".Translate()));
        }

        internal override void CollectCombatPanelActions(ShipBase ship, bool hasWeapons,
            List<PilotPanelAction> beforeGroups, List<PilotPanelAction> afterGroups)
        {
            if (!ship.IsValid || !HasAuthority(PilotTarget.Of(ship))) return;
            beforeGroups.Add(new PilotPanelAction(
                "DeltaV_PanelFireControl".Translate(
                    (universe.Controls.FireControlModeFor(ship) == ShipFireControlMode.Automatic
                        ? "DeltaV_FireControlAutomatic" : "DeltaV_FireControlManual").Translate()),
                () => universe.Controls.ToggleFireControl(ship),
                "DeltaV_PanelFireControlTip".Translate()));
            beforeGroups.Add(new PilotPanelAction(
                "DeltaV_PanelAutoManeuver".Translate(
                    universe.Controls.AutoManeuverActiveFor(ship)
                        ? "DeltaV_On".Translate() : "DeltaV_Off".Translate()),
                () => universe.Controls.ToggleAutoManeuver(ship),
                "DeltaV_PanelAutoManeuverTip".Translate()));
            if (!hasWeapons) return;
            afterGroups.Add(new PilotPanelAction("DeltaV_PanelBattleStationsOn".Translate(),
                () => universe.Controls.EnterBattleStations(ship.Map),
                "DeltaV_PanelBattleStationsOnTip".Translate()));
            afterGroups.Add(new PilotPanelAction("DeltaV_PanelBattleStationsOff".Translate(
                    universe.Weapons.BattleStationGunnerCount(ship.Map)),
                () => universe.Controls.ExitBattleStations(ship.Map),
                "DeltaV_PanelBattleStationsOffTip".Translate()));
        }

        internal override void CollectHeadingPanelActions(ShipBase ship,
            List<HeadingPanelAction> actions)
        {
            if (!ship.IsValid || !HasAuthority(PilotTarget.Of(ship))
                || ship.State != ShipDerivedState.FreeFlight
                || universe.Approach.FlowTarget.Identity == ship.Identity
                || universe.FrameOf(ship.Map)?.projectileRoleOverride == true) return;
            foreach (Rot4 facing in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
            {
                Rot4 choice = facing;
                actions.Add(new HeadingPanelAction(choice,
                    () => universe.ShipTransitions.RotateShipHeading(universe, ship, choice)));
            }
        }

        internal override bool GunnersEnabled => battleStations;
        internal override bool WantsVolley => FireControlMode == ShipFireControlMode.Manual
            ? universe.Controls.ManualAimOwner(OwnerId) : FireControl.Target.HasLock;
        internal void SetBattleStations(bool active) { battleStations = active; }
        internal ShipFireControlMode FireControlMode =>
            ShipRegistry.ShipByIdentity(OwnerId).Combat?.fireControlMode ?? ShipFireControlMode.Automatic;
        internal bool AutoManeuverEnabled => autoManeuverEnabled;
        internal void SetAutoManeuver(bool enabled)
        {
            autoManeuverEnabled = enabled;
            if (!enabled) AutoManeuver.Clear(ShipRegistry.ShipByIdentity(OwnerId));
        }

        internal void DecideAutoCombat(ShipBase ship)
        {
            SpaceObject own = universe.FrameOf(ship.Map);
            if (own == null || universe.Lifecycle.IsFrozen(ship.Map)) return;
            if (FireControlMode == ShipFireControlMode.Automatic || autoManeuverEnabled)
                FireControl.Decide(ship, own);
            else FireControl.Clear();
            if (autoManeuverEnabled && ship.CanDriveIn(own) && !own.projectileRoleOverride)
                AutoManeuver.Decide(ship, own, FireControl.Target, null,
                    allowBoarding: false, playerInitiated: true);
        }
        internal bool AimActive => aim?.Active == true;
        internal bool AimSessionOnFocusedMap => aim?.SessionOnFocusedMap == true;
        internal void ProcessAimInputEvent() => aim?.ProcessInputEvent();

        internal void BeginAim()
        {
            aim = new SpaceAimController(universe, OwnerId, FireControl);
            if (DeltaVLog.Enabled(DeltaVLogModule.Input))
                DeltaVLog.Message(DeltaVLogModule.Input, $"aim session engaged (owner #{OwnerId})");
        }

        internal void EndAim(string reason)
        {
            aim?.Dispose(reason);
            aim = null;
        }

        internal int EmitAimMarkers(SpaceObject frame, Map frameMap, ShellLayer layer, int baseSlot)
            => aim != null ? aim.EmitMarkers(frame, frameMap, layer, baseSlot) : baseSlot;

        protected override bool TryGetVolleyIntent(ShipBase ship, SpaceObject container,
            out ShipVolleyIntent intent)
        {
            intent = default;
            if (FireControlMode == ShipFireControlMode.Automatic)
                return FireControl.TryGetIntent(ship, container, false, out intent);
            if (!universe.Controls.ManualAimOwner(OwnerId)) return false;
            if (aim == null) return false;
            if (!aim.Active)
            {
                aim = null;
                return false;
            }
            aim.Tick();
            if (!aim.Active)
            {
                aim = null;
                return false;
            }
            if (!aim.HasInput) return false;
            intent = new ShipVolleyIntent(aim.AimPoint, aim.FireMain, aim.FireSecondary);
            return true;
        }

        protected internal override bool HasAuthority(PilotTarget target)
        {
            // 控制权门禁：玩家方目标（船 = 主台派系，shuttle = 舱体派系）；开发者模式放行。
            return target.Faction == Faction.OfPlayer || DebugSettings.godMode;
        }

        public override PilotService.PilotCommand ComposeCommand()
        {
            return universe.Pilot.ComposePlayerCommand(target);
        }

        internal override void PresentSeparationConfirmation(PilotTarget target, string text,
            System.Action onConfirm, System.Action onCancel)
        {
            // 复用原版暂停确认窗（Dialog_MessageBox 构造器 forcePause = true，
            // WindowStack 汇总该标志——窗口存在时游戏不 tick，§8.2 已定）。
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                text, onConfirm, onCancel, destructive: true));
        }
    }

    /// <summary>
    /// NPC controller：内部持有 AI 组件，由该组件自动触发驾驶行为；AI 决定驾驶意图/任务，
    /// 通过共同驾驶契约执行，不直接搬运结构、写推进器油门或积分轨道位姿。
    /// 敌舰战术由 NpcShipAiService 持有，目标选择由每船 ShipFireControl 持有。
    /// </summary>
    public sealed class NpcShipController : ShipController
    {
        private bool combatReady;
        /// <summary>内部 AI 组件；默认空实现，NpcShipAiService 为敌舰注入战术实现。</summary>
        public INpcShipAi Ai { get; set; } = new NoopNpcShipAi();

        internal NpcShipController(UniverseManager universe, DriveSubject ownerId) : base(universe, ownerId)
        {
        }

        internal override bool GunnersEnabled => combatReady;
        internal override bool WantsVolley => combatReady && FireControl.Target.HasLock;

        internal void SetCombatReady(bool ready)
        {
            if (!ready)
            {
                FireControl.Clear();
                AutoManeuver.Clear(ShipRegistry.ShipByIdentity(OwnerId));
            }
            if (combatReady == ready) return;
            combatReady = ready;
            if (ready) universe.Controls.NotifyGunnerAvailabilityChanged(
                ShipRegistry.ShipByIdentity(OwnerId).Map);
        }

        protected override bool TryGetVolleyIntent(ShipBase ship, SpaceObject container,
            out ShipVolleyIntent intent)
        {
            intent = default;
            return combatReady && FireControl.TryGetIntent(ship, container,
                AutoManeuver.Doctrine == NpcDoctrine.Boarder, out intent);
        }

        protected internal override bool HasAuthority(PilotTarget target)
        {
            // NPC 控制权由 AI 组件声明；派系仅用于敌我关系与权限，
            // 不替代 controller 类型。
            return target.IsValid && Ai != null && Ai.HasAuthority(target.Vessel);
        }

        public override PilotService.PilotCommand ComposeCommand()
        {
            var command = default(PilotService.PilotCommand);
            if (Ai != null && target.IsValid)
            {
                Ai.ComposeCommand(target.Vessel, ref command);
            }
            return command;
        }

        internal override void PresentSeparationConfirmation(PilotTarget target, string text,
            System.Action onConfirm, System.Action onCancel)
        {
            // NPC 不向玩家请求批准（§8.2）：直接执行同一公共分离。
            onConfirm?.Invoke();
        }
    }

    /// <summary>
    /// NPC 内部 AI 组件契约：AI 决定**何时**选择、发起或停止驾驶行为（何时进入/退出
    /// 控制、下发什么机动请求），**如何**执行一律经共同驾驶契约（ComposeCommand 出指令、
    /// ShipAutopilot 接任务）——不直接搬运结构、写油门或积分位姿。
    /// </summary>
    public interface INpcShipAi
    {
        /// <summary>该 AI 是否对目标船有控制权。</summary>
        bool HasAuthority(ShipBase ship);

        /// <summary>每 tick 驱动（AI 决定驾驶意图、发起/停止任务）。</summary>
        void Tick(NpcShipController controller);

        /// <summary>手操指令合成（NPC 机动请求；经统一分配器执行）。</summary>
        void ComposeCommand(ShipBase ship, ref PilotService.PilotCommand command);

    }

    /// <summary>空实现（本轮接入边界；后续敌舰 AI 在此挂接）。</summary>
    public sealed class NoopNpcShipAi : INpcShipAi
    {
        public bool HasAuthority(ShipBase ship) => false;

        public void Tick(NpcShipController controller)
        {
        }

        public void ComposeCommand(ShipBase ship, ref PilotService.PilotCommand command)
        {
        }

    }

    /// <summary>
    /// 驾驶路由服务（universe 实例持有）：控制状态派生（Uncontrolled/Manual/Autopilot）、
    /// 玩家按键维护、Harmony 按键抑制与 controller 生命周期。
    ///
    /// - 驾驶对象 = ShipBase（船体身份/图）；驾驶状态为运行时数据不跨档（§16.15：
    ///   临时驾驶不随读档恢复——读档后无人驾驶，船按已保存速度漂移）；
    /// - 进入/退出：TogglePilotMode 键 + 屏幕按钮 + dev action；聚焦离开驾驶船地图
    ///   或地图丢失时自动退出；
    /// - 输入每 tick 采样（KeyBindingDef.IsDown 轮询），与飞行辅助合成为 Command 后
    ///   交给 ShipPhysics；辅助只在玩家该轴无输入时发反向指令；
    /// - SuppressVanillaKeys / ActivePilotKeys 供 Harmony 按键抑制 Prefix 使用：
    ///   快速通道是静态 bool，每帧由 UniverseManager.GameComponentUpdate 刷新，
    ///   键集合在进入驾驶时按当前实际绑定重建（支持任意一侧改键）。
    /// </summary>
    public sealed class PilotService
    {
        private readonly UniverseManager universe;
        private readonly HashSet<KeyBindingDef> loggedSuppressedDefs = new HashSet<KeyBindingDef>();

        /// <summary>自动驾驶输入武装的船（按任意驾驶键直接接管的对象）。</summary>
        private DriveSubject autopilotInputOwnerId;

        /// <summary>玩家当前手操的 controller；全部船体均取同一身份注册表。</summary>
        private PlayerShipController activePlayerController;

        public long SuppressedReads { get; private set; }

        internal PilotService(UniverseManager universe)
        {
            this.universe = universe;
        }

        /// <summary>低于此速度/角速度不再发辅助反推指令（推进器只负责主要减速）。</summary>
        private const float MinAssistSpeed = 0.05f;

        private const float MinAssistOmega = 0.01f;

        /// <summary>残差判定阈值：任意高于此值的漂移都置 assist 标志（残差由 Integrate 直接衰减到零）。</summary>
        private const float AssistEpsilon = 0.001f;

        private const float AssistOmegaEpsilon = 0.0005f;

        internal PlayerShipController ControllerFor(PilotTarget target)
        {
            if (!target.IsValid) return null;
            return universe.Controls.PlayerFor(target.Vessel, debugTakeover: true);
        }

        /// <summary>当前活动 controller（供门禁/确认路由；无玩家驾驶时为 null 语义）。</summary>
        internal ShipController ActiveController => activePlayerController?.Active == true
            ? activePlayerController : null;

        internal void ForgetController(ShipController controller)
        {
            if (activePlayerController != controller) return;
            activePlayerController.Exit();
            activePlayerController = null;
            PilotKeyBridge.SuppressVanillaKeys = false;
        }

        // ---- 状态 ----

        public PilotTarget PilotedTarget(UniverseManager universe)
        {
            return activePlayerController?.Active == true
                ? activePlayerController.target : PilotTarget.Invalid;
        }

        /// <summary>旧入口（ShipBase 口径）：驾驶目标是船时返回之（面板/gizmo 消费）。</summary>
        public ShipBase PilotedShip(UniverseManager universe)
        {
            PilotTarget target = PilotedTarget(universe);
            return target.Vessel ?? ShipBase.Invalid;
        }

        public ShipControlState GetControlState(PilotTarget target)
        {
            if (!target.IsValid) return ShipControlState.Uncontrolled;
            if (universe.Autopilot.IsActive(DriveSubject.Of(target))) return ShipControlState.Autopilot;
            return ActiveController != null && ActiveController.target.Identity.Equals(target.Identity)
                ? ShipControlState.Manual
                : ShipControlState.Uncontrolled;
        }

        public ShipControlState GetControlState(ShipBase ship) => GetControlState(PilotTarget.Of(ship));

        public bool IsPlayerInputArmed(PilotTarget target)
        {
            ShipControlState state = GetControlState(target);
            return state == ShipControlState.Manual
                || state == ShipControlState.Autopilot && autopilotInputOwnerId.Equals(target.Identity);
        }

        public bool IsPlayerInputArmed(ShipBase ship) => IsPlayerInputArmed(PilotTarget.Of(ship));

        public void ArmAutopilotInput(PilotTarget target)
        {
            autopilotInputOwnerId = target.Identity;
        }

        public void DisarmAutopilotInput(PilotTarget target = default)
        {
            if (!target.IsValid || autopilotInputOwnerId.Equals(target.Identity)) autopilotInputOwnerId = default;
        }

        public void ArmAutopilotInput(ShipBase ship) => ArmAutopilotInput(PilotTarget.Of(ship));

        public void DisarmAutopilotInput(ShipBase ship) => DisarmAutopilotInput(PilotTarget.Of(ship));

        /// <summary>
        /// 驾驶切换统一入口（面板按钮/驾驶台 gizmo/快捷键共用）：目标船由调用方显式传入
        /// ——gizmo 传驾驶台自己的船、面板传当前选中船、快捷键/dev action 传
        /// <see cref="SelectedShipOnMap"/> 的推导结果（不得在内部反推，两个入口会错位）。
        /// </summary>
        public void ToggleControl(UniverseManager universe, PilotTarget target)
        {
            if (!target.IsValid)
            {
                Messages.Message("DeltaV_PilotNeedShipFocus".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            ShipControlState state = GetControlState(target);
            if (state == ShipControlState.Autopilot)
            {
                if (IsPlayerInputArmed(target)) DisarmAutopilotInput(target);
                else ArmAutopilotInput(target);
            }
            else if (state == ShipControlState.Manual
                && universe.Approach.ActivePlan?.Target.Identity.Equals(target.Identity) == true)
            {
                universe.Approach.EngageAutopilot(universe);
            }
            else if (state == ShipControlState.Manual)
            {
                Exit(universe, "DeltaV_PilotOff".Translate(target.Label));
            }
            else if (ControllerFor(target)?.HasAuthority(target) != true)
            {
                Messages.Message("DeltaV_PilotWrongFaction".Translate(
                        target.Label),
                    MessageTypeDefOf.RejectInput, false);
            }
            else
            {
                Enter(universe, target);
            }
        }

        public void ToggleControl(UniverseManager universe, ShipBase ship) => ToggleControl(universe, PilotTarget.Of(ship));

        /// <summary>快捷键共用面板选择；选中无主台结构时不得回退操作另一艘船。</summary>
        public ShipBase SelectedShipOnMap(Map map)
        {
            if (ShipStatusPanel.TryGetSelectedShip(map, out ShipBase selected) && selected.IsValid)
            {
                return selected;
            }
            return ShipBase.Invalid;
        }

        /// <summary>驾驶/模式控制权门禁（玩家侧）：玩家方目标（船/shuttle）；开发者模式放行。</summary>
        public bool CanPlayerControl(ShipBase ship)
        {
            return ship.IsValid && (ship.Faction == Faction.OfPlayer
                || DebugSettings.godMode);
        }

        public void Enter(UniverseManager universe, PilotTarget target)
        {
            PlayerShipController controller = ControllerFor(target);
            if (controller == null || !controller.CanEnter(target))
            {
                // 可读反馈（原先静默拒绝）：重力图不可驾驶，只能逆重战略发射。
                Map map = target.Map;
                if (target.IsValid && map != null && universe.FrameOf(map) != null
                    && !FreeFlightBootstrap.IsZeroGravity(universe, map))
                {
                    Messages.Message("DeltaV_PilotGravityRestricted".Translate(),
                        MessageTypeDefOf.RejectInput, false);
                }
                return;
            }
            // 手操打断自动驾驶（不取消对接计划——完成判据是几何的，手飞到位照样对接）。
            if (universe.Autopilot.IsActive(DriveSubject.Of(target)))
            {
                universe.Autopilot.Detach(DriveSubject.Of(target), "manual input");
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"autopilot interrupted by manual input: target key #{target.Identity} (pilot mode engaged)");
                Messages.Message("DeltaV_DockAutopilotStopped".Translate(), MessageTypeDefOf.NeutralEvent, false);
            }
            controller.SetAutoManeuver(false);
            DisarmAutopilotInput(target);
            if (activePlayerController != null && activePlayerController != controller)
            {
                DriveSubject previousOwner = activePlayerController.OwnerId;
                activePlayerController.Exit();
                activePlayerController = null;
                if (previousOwner.key > 0) universe.Controls.ReleaseDebugControl(previousOwner);
            }
            activePlayerController = controller;
            controller.Enter(target);
            RebuildPilotKeySet();
            string label = target.Label;
            Log.Message($"[DeltaV] Pilot mode engaged: {label} (#{target.Identity}). Pilot keys: [{string.Join(", ", PilotKeyBridge.ActiveKeys)}].");
            Messages.Message("DeltaV_PilotOn".Translate(label), MessageTypeDefOf.PositiveEvent, false);
        }

        public void Enter(UniverseManager universe, ShipBase ship) => Enter(universe, PilotTarget.Of(ship));

        public void Exit(UniverseManager universe, string message = null)
        {
            if (activePlayerController == null)
            {
                return;
            }
            DriveSubject ownerId = activePlayerController.OwnerId;
            string label = activePlayerController.target.Label;
            Log.Message($"[DeltaV] Pilot mode disengaged (was '{label}'; suppressed reads: {SuppressedReads}).");
            activePlayerController.Exit();
            activePlayerController = null;
            if (ownerId.key > 0) universe.Controls.ReleaseDebugControl(ownerId);
            // 瞄准会话留在本船 controller（独立于驾驶与自动驾驶，对接中可用），
            // 退出驾驶不再连带退出瞄准。
            PilotKeyBridge.SuppressVanillaKeys = false;
            if (message != null)
            {
                Messages.Message(message, MessageTypeDefOf.NeutralEvent, false);
            }
        }

        /// <summary>每帧由 UniverseManager.GameComponentUpdate 调用：维护自动退出与抑制开关。
        /// 自动驾驶 = 「驾驶此船」的特殊状态（2026-09-21 用户定案）：计划船被随动自动
        /// 驾驶且聚焦其图时占用驾驶位——驾驶键抑制生效，且按下任意 W/A/S/D/Q/E 直接
        /// 打断自动驾驶并接管手操（Enter 内部 Detach；输入为轮询采样，同帧即生效）。
        /// 抑制从关闭翻转为开启时输出一次性摘要（哪些原版绑定将被抑制）——必要日志，常开。</summary>
        public void UpdateFrameState(UniverseManager universe)
        {
            PilotTarget piloted = PilotedTarget(universe);
            Map focusedMap = Find.CurrentMap;
            PilotTarget autopilotTarget = PilotTarget.Invalid;
            if (focusedMap != null)
            {
                foreach (ShipBase ship in ShipRegistry.AllShipsOnMap(focusedMap))
                {
                    if (universe.Autopilot.IsActive(DriveSubject.Of(ship)))
                    {
                        autopilotTarget = PilotTarget.Of(ship);
                        break;
                    }
                }

            }
            bool focusOnAutopilotShip = autopilotTarget.IsValid && autopilotTarget.Map == focusedMap
                && IsPlayerInputArmed(autopilotTarget);

            if (!piloted.IsValid)
            {
                if (focusOnAutopilotShip)
                {
                    PilotInput input = SampleInput();
                    if (input.fx != 0f || input.fz != 0f || input.torque != 0f)
                    {
                        DeltaVLog.Message(DeltaVLogModule.Core,
                            $"autopilot interrupted by flight key: target key #{autopilotTarget.Identity} (direct takeover)");
                        Enter(universe, autopilotTarget); // 打断自动驾驶并接管：同帧输入生效
                        piloted = PilotedTarget(universe);
                        focusOnAutopilotShip = false;
                    }
                }
            }
            bool manualPiloting = piloted.IsValid && focusedMap == piloted.Map;
            if (!manualPiloting && piloted.IsValid)
            {
                Exit(universe, "DeltaV_PilotExitAuto".Translate(
                    piloted.Label));
            }
            bool suppress = manualPiloting || focusOnAutopilotShip;
            if (suppress)
            {
                if (PilotKeyBridge.ActiveKeys.Count == 0)
                {
                    RebuildPilotKeySet();
                }
                if (!PilotKeyBridge.SuppressVanillaKeys)
                {
                    List<string> overlaps = CountOverlappingVanillaDefs();
                    Log.Message("[DeltaV] Key suppression ACTIVE. Overlapping vanilla bindings: " +
                                (overlaps.Count == 0
                                    ? "(none found - WASD/QE will keep their vanilla function!)"
                                    : string.Join(", ", overlaps)));
                    DeltaVLog.Message(DeltaVLogModule.Harmony, "suppression flag false->true; overlap scan across all KeyBindingDefs done");
                }
            }
            PilotKeyBridge.SuppressVanillaKeys = suppress;
        }

        private List<string> CountOverlappingVanillaDefs()
        {
            var overlaps = new List<string>();
            foreach (KeyBindingDef def in DefDatabase<KeyBindingDef>.AllDefs)
            {
                if (def.category == DeltaVDefOf.DeltaV)
                {
                    continue;
                }
                if (OverlapsActiveKeys(def))
                {
                    overlaps.Add($"{def.defName}({BoundKeysLabel(def)})");
                }
            }
            return overlaps;
        }

        /// <summary>按当前实际绑定重建驾驶键集合（进入驾驶 / 键集合为空时调用）。
        /// 含切换键：驾驶中按切换键不应触发与它重叠的原版绑定。
        /// keyPrefs 缺项（播种异常）时回退 def 默认键，保证集合非空、抑制可用。</summary>
        public void RebuildPilotKeySet()
        {
            var set = new HashSet<KeyCode>();
            foreach (KeyBindingDef def in SuppressionKeyDefs())
            {
                if (def == null)
                {
                    continue;
                }
                GetBoundKeys(def, out KeyCode a, out KeyCode b);
                if (a != KeyCode.None)
                {
                    set.Add(a);
                }
                if (b != KeyCode.None)
                {
                    set.Add(b);
                }
            }
            PilotKeyBridge.ActiveKeys = set;
            DeltaVLog.Message(DeltaVLogModule.Harmony, $"pilot key set rebuilt: [{string.Join(", ", set)}]");
        }

        /// <summary>取 def 的实际绑定；keyPrefs 无条目时回退默认键（A/B 槽）。</summary>
        private static void GetBoundKeys(KeyBindingDef def, out KeyCode a, out KeyCode b)
        {
            if (KeyPrefs.KeyPrefsData?.keyPrefs.TryGetValue(def, out KeyBindingData data) == true)
            {
                a = data.keyBindingA;
                b = data.keyBindingB;
                return;
            }
            a = def.defaultKeyCodeA;
            b = def.defaultKeyCodeB;
        }

        private string BoundKeysLabel(KeyBindingDef def)
        {
            GetBoundKeys(def, out KeyCode a, out KeyCode b);
            if (a != KeyCode.None && b != KeyCode.None)
            {
                return $"{a}/{b}";
            }
            return a != KeyCode.None ? a.ToString() : b.ToString();
        }

        private IEnumerable<KeyBindingDef> FlightKeyDefs()
        {
            yield return DeltaVDefOf.DeltaV_ThrustForward;
            yield return DeltaVDefOf.DeltaV_ThrustBackward;
            yield return DeltaVDefOf.DeltaV_StrafeLeft;
            yield return DeltaVDefOf.DeltaV_StrafeRight;
            yield return DeltaVDefOf.DeltaV_RotateLeft;
            yield return DeltaVDefOf.DeltaV_RotateRight;
        }

        private IEnumerable<KeyBindingDef> SuppressionKeyDefs()
        {
            foreach (KeyBindingDef def in FlightKeyDefs())
            {
                yield return def;
            }
            yield return DeltaVDefOf.DeltaV_TogglePilotMode;
            yield return DeltaVDefOf.DeltaV_AssistBrake; // Ctrl 刹停时抑制原版 Ctrl 用途
            // 火控键（默认 B）：驾驶期抑制其原版功能（建筑菜单等）。
            yield return DeltaVDefOf.DeltaV_AimMode;
        }

        /// <summary>非本 mod 分类的绑定是否与当前驾驶键重叠（供 Harmony Prefix 判定抑制）。</summary>
        public bool OverlapsActiveKeys(KeyBindingDef def)
        {
            if (def.category == DeltaVDefOf.DeltaV || PilotKeyBridge.ActiveKeys.Count == 0)
            {
                return false;
            }
            GetBoundKeys(def, out KeyCode a, out KeyCode b);
            return (a != KeyCode.None && PilotKeyBridge.ActiveKeys.Contains(a))
                || (b != KeyCode.None && PilotKeyBridge.ActiveKeys.Contains(b));
        }

        internal bool TrySuppressBinding(KeyBindingDef def)
        {
            if (!OverlapsActiveKeys(def)) return false;
            SuppressedReads++;
            if (DeltaVLog.Enabled(DeltaVLogModule.Harmony) && loggedSuppressedDefs.Add(def))
                DeltaVLog.Message(DeltaVLogModule.Harmony,
                    $"suppressing vanilla binding '{def.defName}' (reads so far will not trigger it)");
            return true;
        }

        // ---- 输入采样与指令合成（PlayerShipController 的实现体；服务持有共享缓存）----

        public struct PilotInput
        {
            public float fx; // W = +1（主驾驶台正面）/ S = −1
            public float fz; // A = +1（左舷 +Z）/ D = −1
            public float torque; // Q = +1（逆时针）/ E = −1
        }

        public PilotInput SampleInput()
        {
            var input = new PilotInput();
            if (DeltaVDefOf.DeltaV_ThrustForward == null)
            {
                return input; // 按键 def 加载失败（XML 缺失）时不 NRE 刷屏。
            }
            if (DeltaVDefOf.DeltaV_ThrustForward.IsDown)
            {
                input.fx += 1f;
            }
            if (DeltaVDefOf.DeltaV_ThrustBackward.IsDown)
            {
                input.fx -= 1f;
            }
            if (DeltaVDefOf.DeltaV_StrafeLeft.IsDown)
            {
                input.fz += 1f;
            }
            if (DeltaVDefOf.DeltaV_StrafeRight.IsDown)
            {
                input.fz -= 1f;
            }
            if (DeltaVDefOf.DeltaV_RotateLeft.IsDown)
            {
                input.torque += 1f;
            }
            if (DeltaVDefOf.DeltaV_RotateRight.IsDown)
            {
                input.torque -= 1f;
            }
            return input;
        }

        /// <summary>玩家输入 + 飞行辅助合成的最终指令；assist* 标记辅助正在反推的轴（供积分收口）。</summary>
        public struct PilotCommand
        {
            public ShipPhysics.Command command;
            public bool assistX;
            public bool assistZ;
            public bool assistW;
        }

        /// <summary>玩家手操指令合成（PlayerShipController.ComposeCommand 的实现体；
        /// 船与 shuttle 同一实现——帧角经 PilotTarget.FrameAngle 派生）。</summary>
        internal PilotCommand ComposePlayerCommand(PilotTarget target)
        {
            var result = new PilotCommand();
            if (!target.IsValid)
            {
                return result;
            }
            PilotInput input = SampleInput();
            // W/S 与 A/D 定义在驾驶目标参考系（船 = 主台朝向；shuttle = 舱体朝向）；
            // 推进器分配器使用 Map 局部系，在此集中旋转一次。
            float consoleAngle = target.FrameAngle;
            Vector2 pilotTranslation = new Vector2(input.fx, input.fz);
            result.command.torque = input.torque;
            if (DeltaVLog.Enabled(DeltaVLogModule.Input) && HasInputChanged(input))
            {
                DeltaVLog.Message(DeltaVLogModule.Input,
                    $"input: fx={input.fx:+0;-0;0} fz={input.fz:+0;-0;0} torque={input.torque:+0;-0;0} (keys polled via IsDown)");
            }
            // 辅助生效 = 设置开启 或 按住刹车键（临时启用，辅助关闭时也可刹停）。
            bool brakeHeld = DeltaVDefOf.DeltaV_AssistBrake != null && DeltaVDefOf.DeltaV_AssistBrake.IsDown;
            SpaceObject frame = universe.FrameOf(target.Map);
            SpacePose pose = frame != null ? frame.pose : new SpacePose();
            if (!DeltaVMod.CurrentSettings.flightAssist && !brakeHeld)
            {
                Vector2 mapTranslation = SpaceTransform.Rotate(pilotTranslation, consoleAngle);
                result.command.fx = mapTranslation.x;
                result.command.fz = mapTranslation.y;
                return result;
            }
            // 辅助在船体参考系里抵消速度（推进器固连船体）；与玩家指令共轴时不参与。
            // 任意高于 AssistEpsilon 的漂移都置 assist* 标志（残差交给 ShipPhysics.Integrate
            // 直接衰减到零）；推进器反推指令只在漂移超过 MinAssistSpeed 时下发。
            Vector2 mapVelocity = SpaceTransform.RotateInverse(pose.velocity, pose.angle);
            Vector2 bodyVelocity = SpaceTransform.RotateInverse(mapVelocity, consoleAngle);
            if (input.fx == 0f && Mathf.Abs(bodyVelocity.x) > AssistEpsilon)
            {
                result.assistX = true;
                if (Mathf.Abs(bodyVelocity.x) > MinAssistSpeed)
                {
                    pilotTranslation.x = -Mathf.Sign(bodyVelocity.x);
                }
            }
            if (input.fz == 0f && Mathf.Abs(bodyVelocity.y) > AssistEpsilon)
            {
                result.assistZ = true;
                if (Mathf.Abs(bodyVelocity.y) > MinAssistSpeed)
                {
                    pilotTranslation.y = -Mathf.Sign(bodyVelocity.y);
                }
            }
            if (input.torque == 0f && Mathf.Abs(pose.angularVelocity) > AssistOmegaEpsilon)
            {
                result.assistW = true;
                if (Mathf.Abs(pose.angularVelocity) > MinAssistOmega)
                {
                    result.command.torque = -Mathf.Sign(pose.angularVelocity);
                }
            }
            Vector2 assistedMapTranslation = SpaceTransform.Rotate(pilotTranslation, consoleAngle);
            result.command.fx = assistedMapTranslation.x;
            result.command.fz = assistedMapTranslation.y;
            if (DeltaVLog.Enabled(DeltaVLogModule.Input) && HasAssistChanged(result))
            {
                DeltaVLog.Message(DeltaVLogModule.Input,
                    $"assist axes x={result.assistX} z={result.assistZ} w={result.assistW} (brake={brakeHeld}, setting={DeltaVMod.CurrentSettings.flightAssist}); cmd=({result.command.fx:+0;-0;0},{result.command.fz:+0;-0;0},{result.command.torque:+0;-0;0}); bodyV={bodyVelocity:F2} omega={pose.angularVelocity:F3}");
            }
            return result;
        }

        /// <summary>tick 循环的驾驶指令入口（controller 路由：手操或 NPC AI）。</summary>
        public PilotCommand ComposeCommand(PilotTarget target)
        {
            if (ActiveController != null && ActiveController.target.Identity.Equals(target.Identity))
            {
                return ActiveController.ComposeCommand();
            }
            return default;
        }

        // 变更检测缓存：全部输入埋点只在变化沿输出（触发式，避免持续辅助反推每 tick 刷爆日志）。
        private float lastLoggedFx;
        private float lastLoggedFz;
        private float lastLoggedTorque = -999f;

        private bool HasInputChanged(PilotInput input)
        {
            if (input.fx == lastLoggedFx && input.fz == lastLoggedFz && input.torque == lastLoggedTorque)
            {
                return false;
            }
            lastLoggedFx = input.fx;
            lastLoggedFz = input.fz;
            lastLoggedTorque = input.torque;
            return true;
        }

        private int lastAssistFlags = -1;

        private bool HasAssistChanged(PilotCommand command)
        {
            int flags = (command.assistX ? 1 : 0) | (command.assistZ ? 2 : 0) | (command.assistW ? 4 : 0);
            if (flags == lastAssistFlags)
            {
                return false;
            }
            lastAssistFlags = flags;
            return true;
        }
    }
}
