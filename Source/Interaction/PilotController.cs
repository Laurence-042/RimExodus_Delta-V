using System.Collections.Generic;
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
/// 驾驶目标（太空战架构 §7.2 泛化）：Ship（主台归属）或在飞/停靠的 shuttle 舱体
/// 二选一。飞船与 shuttle 不得有两套驾驶实现——本结构是两条链共用的目标句柄；
/// 容器/帧角/派系/控制键的派生全部收口在此，调用方不得自行分叉。
/// </summary>
public readonly struct PilotTarget
{
    public readonly Ship ship;
    public readonly Building shuttle;

    private PilotTarget(Ship ship, Building shuttle)
    {
        this.ship = ship;
        this.shuttle = shuttle;
    }

    public static PilotTarget Of(Ship ship) => new PilotTarget(ship, null);
    public static PilotTarget Of(Building shuttleBody) => new PilotTarget(Ship.Invalid, shuttleBody);

    public bool IsValid => ship.IsValid || (shuttle != null && shuttle.Spawned && !shuttle.Destroyed);
    public static readonly PilotTarget Invalid = default;

    /// <summary>目标所在图（shuttle = 舱体所在图，含 MiniShip 口袋图与停靠宿主图）。</summary>
    public Map Map => ship.IsValid ? ship.map : shuttle?.Map;

    /// <summary>驾驶控制键（身份比较用；船 = 归属 ID，shuttle = thingID）。</summary>
    public int ControlKey => ship.IsValid ? ship.ownerId : (shuttle?.thingIDNumber ?? 0);

    /// <summary>船体帧角 σ（船 = 主台朝向；shuttle = 舱体朝向）。</summary>
    public float FrameAngle => ship.IsValid
        ? ShipRegistry.LocalFrameAngle(ship)
        : (shuttle != null ? ShipOrientation.AngleFromRot4(shuttle.Rotation) : 0f);

    /// <summary>目标船体帧锚（连续 Map 坐标；船 = 控制域包围盒中心，shuttle = 舱体脚印中心）。</summary>
    public Vector2 HullCenter => ship.IsValid
        ? ShipRegistry.HullCenter(ship)
        : (shuttle != null ? SpaceTransform.BoundsCenterOfCells(shuttle.OccupiedRect().Cells) : Vector2.zero);

    public Faction Faction => ship.IsValid ? ShipRegistry.FactionOf(ship) : shuttle?.Faction;

    public bool IsShuttle => !ship.IsValid;

    /// <summary>流程/诊断标签（船 = 归属名，shuttle = 舱体名；仅显示用）。</summary>
    public string Label => ship.IsValid
        ? Current.Game?.GetComponent<UniverseManager>()?.LabelForOwner(ship.ownerId) ?? $"structure #{ship.ownerId}"
        : shuttle.LabelCap;
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
        public readonly int OwnerId;
        private Ship cachedShip;

        /// <summary>当前驾驶目标（Ship 或 shuttle 舱体，二选一）。无效 = 未驾驶。</summary>
        internal PilotTarget target = PilotTarget.Invalid;

        protected ShipController(UniverseManager universe, int ownerId)
        {
            this.universe = universe;
            OwnerId = ownerId;
        }

        internal abstract bool GunnersEnabled { get; }
        internal abstract bool WantsVolley { get; }
        internal void BindShip(Ship ship) { cachedShip = ship; }

        /// <summary>驾驶与战斗共用船归属，但未驾驶仍可瞄准或自动点防。</summary>
        internal void TickCombat()
        {
            if (OwnerId <= 0) return; // shuttle 只有驾驶层
            if (!WantsVolley && !cachedShip.IsValid) return;
            if (!WantsVolley && cachedShip.IsValid
                && !universe.Weapons.HasPointDefenseWeapons(cachedShip.map)) return;
            Ship ship = cachedShip.IsValid ? cachedShip : ShipRegistry.ShipByOwner(OwnerId);
            if (!ship.IsValid || universe.FrameOf(ship.map) is not SpaceObject container
                || container.Tag != SpaceObjectTag.Ship
                || !HasAuthority(PilotTarget.Of(ship))) return;
            cachedShip = ship;
            if (TryGetVolleyIntent(ship, container, out ShipVolleyIntent intent))
                TickVolley(ship, container, intent);
            TickPointDefense(ship, container);
        }

        protected abstract bool TryGetVolleyIntent(Ship ship, SpaceObject container,
            out ShipVolleyIntent intent);

        private void TickVolley(Ship ship, SpaceObject container, ShipVolleyIntent intent)
        {
            int now = Find.TickManager.TicksGame;
            foreach (CompSpaceWeapon weapon in SpaceWeaponService.WeaponsOn(ship.map, OwnerId))
            {
                if (weapon.aimMode != SpaceWeaponAimMode.Volley || !weapon.GroupActive) continue;
                Vector3 aim = intent.point;
                if (intent.target != null)
                {
                    if (intent.system != null && intent.system.Spawned
                        && intent.system.Map == intent.target.map)
                        aim = SpaceTransform.MapPointToUniverse(intent.target, intent.target.map,
                            new Vector2(intent.system.DrawPos.x, intent.system.DrawPos.z));
                    else if (!intent.predictIntercept)
                        aim = intent.target.pose.position;
                    else if (!SpaceWeaponService.TrySolveIntercept(weapon, container, intent.target,
                        SpaceWeaponService.ProjectileSpeedOf(weapon), out aim))
                        aim = intent.target.pose.position;
                }
                AimWeapon(weapon, container, aim);
                bool fire = intent.fireAll || intent.main && weapon.salvoSet == SpaceWeaponSalvoSet.Main
                    || intent.secondary && weapon.salvoSet == SpaceWeaponSalvoSet.Secondary;
                if (fire && now >= weapon.cooldownUntilTick)
                    universe.Weapons.TryFire(weapon, container, aim, intent.target);
            }
        }

        private void TickPointDefense(Ship ship, SpaceObject container)
        {
            if (!universe.Weapons.TryGetPointDefenseWeapons(ship.map,
                out HashSet<CompSpaceWeapon> weapons)) return;
            int now = Find.TickManager.TicksGame;
            foreach (CompSpaceWeapon weapon in weapons)
            {
                if (weapon?.parent == null || weapon.parent.Map != ship.map
                    || weapon.aimMode != SpaceWeaponAimMode.PointDefense
                    || SpaceWeaponGeometry.OwnerIdOf(weapon.parent) != OwnerId
                    || !weapon.parent.Spawned || weapon.parent.Destroyed || !weapon.GroupActive
                    || now < weapon.cooldownUntilTick || weapon.Container != container) continue;
                if (now >= weapon.pdRetargetAtTick || !universe.Weapons.PdTargetValid(weapon))
                {
                    weapon.pdRetargetAtTick = now + 30;
                    weapon.pdTarget = universe.Weapons.FindPdTarget(weapon);
                }
                SpaceObject target = weapon.pdTarget;
                if (target == null) continue;
                ThingDef projectile = weapon.SelectMagazine()?.SelectedProjectileDef
                    ?? weapon.WeaponProps.projectileDef;
                var props = projectile?.GetCompProperties<CompProperties_SpaceProjectile>();
                Vector3 aim;
                if (props != null && props.laserRange > 0f)
                    aim = target.pose.position;
                else if (!SpaceWeaponService.TrySolveIntercept(weapon, container, target,
                    SpaceWeaponService.ProjectileSpeedOf(weapon), out aim))
                    continue;
                AimWeapon(weapon, container, aim);
                universe.Weapons.TryFire(weapon, container, aim, target);
            }
        }

        private static void AimWeapon(CompSpaceWeapon weapon, SpaceObject container, Vector3 aim)
        {
            CompSpaceWeaponTraverse traverse = weapon.parent.TryGetComp<CompSpaceWeaponTraverse>();
            if (traverse == null) return;
            Vector2 muzzle = SpaceWeaponGeometry.MuzzleMapPoint(weapon);
            Vector2 point = SpaceTransform.UniverseToMapPoint(container, weapon.parent.Map, aim);
            Vector2 delta = point - muzzle;
            if (delta.sqrMagnitude > 1e-6f)
                traverse.AimAt(Mathf.Atan2(delta.y, delta.x));
        }

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
            if (map == null || universe.FrameOf(map) == null)
                return PilotEntryBlockReason.NoUniverseFrame;
            if (!FreeFlightBootstrap.IsZeroGravity(universe, map))
                return PilotEntryBlockReason.GravityRestricted;
            if (universe.Approach.AnyActive
                && universe.Approach.ApproachTarget.ControlKey != target.ControlKey)
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
        internal readonly Vector3 point;
        internal readonly SpaceObject target;
        internal readonly Thing system;
        internal readonly bool main;
        internal readonly bool secondary;
        internal readonly bool fireAll;
        internal readonly bool predictIntercept;

        internal ShipVolleyIntent(Vector3 point, bool main, bool secondary)
        {
            this.point = point;
            target = null;
            system = null;
            this.main = main;
            this.secondary = secondary;
            fireAll = false;
            predictIntercept = false;
        }

        internal ShipVolleyIntent(SpaceObject target, Thing system, bool predictIntercept)
        {
            point = target.pose.position;
            this.target = target;
            this.system = system;
            main = secondary = false;
            fireAll = true;
            this.predictIntercept = predictIntercept;
        }
    }

    /// <summary>
    /// 玩家 controller：手操输入与自动驾驶两种驱动来源都在本子类（切入自动驾驶不把
    /// 玩家 controller 换成 NPC controller），并承担按键接管、拆船及留置确认等交互。
    /// </summary>
    public sealed class PlayerShipController : ShipController
    {
        private SpaceAimController aim;
        private bool battleStations;

        internal PlayerShipController(UniverseManager universe, int ownerId) : base(universe, ownerId)
        {
        }

        internal override bool GunnersEnabled => battleStations;
        internal override bool WantsVolley => AimActive;
        internal void SetBattleStations(bool active) { battleStations = active; }
        internal bool AimActive => aim?.Active == true;
        internal bool AimSessionOnFocusedMap => aim?.SessionOnFocusedMap == true;

        internal void BeginAim()
        {
            aim = new SpaceAimController(universe, OwnerId);
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

        protected override bool TryGetVolleyIntent(Ship ship, SpaceObject container,
            out ShipVolleyIntent intent)
        {
            intent = default;
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
    /// 敌舰战术与目标选择由 NpcShipAiService 持有，本类保持驾驶边界。
    /// </summary>
    public sealed class NpcShipController : ShipController
    {
        private bool combatReady;
        /// <summary>内部 AI 组件；默认空实现，NpcShipAiService 为敌舰注入战术实现。</summary>
        public INpcShipAi Ai { get; set; } = new NoopNpcShipAi();

        internal NpcShipController(UniverseManager universe, int ownerId) : base(universe, ownerId)
        {
        }

        internal override bool GunnersEnabled => combatReady;
        internal override bool WantsVolley => combatReady;

        internal void SetCombatReady(bool ready)
        {
            if (!ready) Ai?.ClearCombatTarget();
            if (combatReady == ready) return;
            combatReady = ready;
            if (ready) universe.Controls.NotifyGunnerAvailabilityChanged(
                ShipRegistry.ShipByOwner(OwnerId).map);
        }

        protected override bool TryGetVolleyIntent(Ship ship, SpaceObject container,
            out ShipVolleyIntent intent)
        {
            intent = default;
            if (!combatReady || Ai == null
                || !Ai.TryGetVolleyTarget(ship, container, out SpaceObject victim,
                    out Thing system, out bool suppressSystems))
                return false;
            intent = new ShipVolleyIntent(victim, system, !suppressSystems);
            return true;
        }

        protected internal override bool HasAuthority(PilotTarget target)
        {
            // NPC 控制权由 AI 组件声明；派系仅用于敌我关系与权限，
            // 不替代 controller 类型。
            return Ai != null && Ai.HasAuthority(target.ship);
        }

        public override PilotService.PilotCommand ComposeCommand()
        {
            var command = default(PilotService.PilotCommand);
            if (Ai != null && target.ship.IsValid)
            {
                Ai.ComposeCommand(target.ship, ref command);
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
        bool HasAuthority(Ship ship);

        /// <summary>每 tick 驱动（AI 决定驾驶意图、发起/停止任务）。</summary>
        void Tick(NpcShipController controller);

        /// <summary>手操指令合成（NPC 机动请求；经统一分配器执行）。</summary>
        void ComposeCommand(Ship ship, ref PilotService.PilotCommand command);

        bool TryGetVolleyTarget(Ship ship, SpaceObject own, out SpaceObject target,
            out Thing system, out bool suppressSystems);

        void ClearCombatTarget();
    }

    /// <summary>空实现（本轮接入边界；后续敌舰 AI 在此挂接）。</summary>
    public sealed class NoopNpcShipAi : INpcShipAi
    {
        public bool HasAuthority(Ship ship) => false;

        public void Tick(NpcShipController controller)
        {
        }

        public void ComposeCommand(Ship ship, ref PilotService.PilotCommand command)
        {
        }

        public bool TryGetVolleyTarget(Ship ship, SpaceObject own, out SpaceObject target,
            out Thing system, out bool suppressSystems)
        {
            target = null;
            system = null;
            suppressSystems = false;
            return false;
        }

        public void ClearCombatTarget() { }
    }

    /// <summary>
    /// 驾驶路由服务（universe 实例持有）：控制状态派生（Uncontrolled/Manual/Autopilot）、
    /// 玩家按键维护、Harmony 按键抑制与 controller 生命周期。
    ///
    /// - 驾驶对象 = Ship（主台/归属/图）；驾驶状态为运行时数据不跨档（§16.15：
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
        private int autopilotInputOwnerId = -1;

        /// <summary>玩家当前手操的 controller；普通船取归属注册表，shuttle 使用临时实例。</summary>
        private PlayerShipController activePlayerController;
        private PlayerShipController shuttleController;

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

        private PlayerShipController ControllerFor(PilotTarget target)
        {
            if (!target.IsValid) return null;
            if (!target.IsShuttle) return universe.Controls.PlayerFor(target.ship, debugTakeover: true);
            return shuttleController ?? (shuttleController = new PlayerShipController(universe, 0));
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

        /// <summary>旧入口（Ship 口径）：驾驶目标是船时返回之（面板/gizmo 消费）。</summary>
        public Ship PilotedShip(UniverseManager universe)
        {
            PilotTarget target = PilotedTarget(universe);
            return target.IsShuttle ? Ship.Invalid : target.ship;
        }

        public ShipControlState GetControlState(PilotTarget target)
        {
            if (!target.IsValid) return ShipControlState.Uncontrolled;
            if (universe.Autopilot.IsActive(target.ControlKey)) return ShipControlState.Autopilot;
            return ActiveController != null && ActiveController.target.ControlKey == target.ControlKey
                ? ShipControlState.Manual
                : ShipControlState.Uncontrolled;
        }

        public ShipControlState GetControlState(Ship ship) => GetControlState(PilotTarget.Of(ship));

        public bool IsPlayerInputArmed(PilotTarget target)
        {
            ShipControlState state = GetControlState(target);
            return state == ShipControlState.Manual
                || state == ShipControlState.Autopilot && autopilotInputOwnerId == target.ControlKey;
        }

        public bool IsPlayerInputArmed(Ship ship) => IsPlayerInputArmed(PilotTarget.Of(ship));

        public void ArmAutopilotInput(PilotTarget target)
        {
            autopilotInputOwnerId = target.IsValid ? target.ControlKey : -1;
        }

        public void DisarmAutopilotInput(PilotTarget target = default)
        {
            if (!target.IsValid || autopilotInputOwnerId == target.ControlKey) autopilotInputOwnerId = -1;
        }

        public void ArmAutopilotInput(Ship ship) => ArmAutopilotInput(PilotTarget.Of(ship));

        public void DisarmAutopilotInput(Ship ship) => DisarmAutopilotInput(PilotTarget.Of(ship));

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
                && universe.Approach.ActivePlan?.Target.ControlKey == target.ControlKey)
            {
                universe.Approach.EngageAutopilot(universe);
            }
            else if (state == ShipControlState.Manual)
            {
                Exit(universe, "DeltaV_PilotOff".Translate(universe.LabelForOwner(target.ControlKey)));
            }
            else if (ControllerFor(target)?.HasAuthority(target) != true)
            {
                Messages.Message("DeltaV_PilotWrongFaction".Translate(
                        target.IsShuttle ? target.shuttle.LabelCap : universe.LabelForOwner(target.ControlKey)),
                    MessageTypeDefOf.RejectInput, false);
            }
            else
            {
                Enter(universe, target);
            }
        }

        public void ToggleControl(UniverseManager universe, Ship ship) => ToggleControl(universe, PilotTarget.Of(ship));

        /// <summary>多船图的「当前查看/操作的船」（快捷键/dev action 的推导入口）：面板已
        /// 选择则用之；否则默认船（玩家派系船优先、唯一船直取，DefaultShipOnMap）。</summary>
        public Ship SelectedShipOnMap(Map map)
        {
            if (ShipStatusPanel.TryGetSelectedShip(map, out Ship selected) && selected.IsValid)
            {
                return selected;
            }
            return ShipRegistry.DefaultShipOnMap(map);
        }

        /// <summary>驾驶/模式控制权门禁（玩家侧）：玩家方目标（船/shuttle）；开发者模式放行。</summary>
        public bool CanPlayerControl(Ship ship)
        {
            return ship.IsValid && (ShipRegistry.FactionOf(ship) == Faction.OfPlayer
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
            if (universe.Autopilot.IsActive(target.ControlKey))
            {
                universe.Autopilot.Detach(target, "manual input");
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"autopilot interrupted by manual input: target key #{target.ControlKey} (pilot mode engaged)");
                Messages.Message("DeltaV_DockAutopilotStopped".Translate(), MessageTypeDefOf.NeutralEvent, false);
            }
            DisarmAutopilotInput(target);
            if (activePlayerController != null && activePlayerController != controller)
            {
                int previousOwner = activePlayerController.OwnerId;
                activePlayerController.Exit();
                activePlayerController = null;
                if (previousOwner > 0) universe.Controls.ReleaseDebugControl(previousOwner);
            }
            activePlayerController = controller;
            controller.Enter(target);
            RebuildPilotKeySet();
            string label = target.IsShuttle ? target.shuttle.LabelCap : universe.LabelForOwner(target.ControlKey);
            Log.Message($"[DeltaV] Pilot mode engaged: {label} (#{target.ControlKey}). Pilot keys: [{string.Join(", ", PilotKeyBridge.ActiveKeys)}].");
            Messages.Message("DeltaV_PilotOn".Translate(label), MessageTypeDefOf.PositiveEvent, false);
        }

        public void Enter(UniverseManager universe, Ship ship) => Enter(universe, PilotTarget.Of(ship));

        public void Exit(UniverseManager universe, string message = null)
        {
            if (activePlayerController == null)
            {
                return;
            }
            int ownerId = activePlayerController.OwnerId;
            string label = ownerId > 0 ? universe.LabelForOwner(ownerId)
                : activePlayerController.target.shuttle?.LabelCap ?? "shuttle";
            Log.Message($"[DeltaV] Pilot mode disengaged (was '{label}'; suppressed reads: {SuppressedReads}).");
            activePlayerController.Exit();
            activePlayerController = null;
            if (ownerId > 0) universe.Controls.ReleaseDebugControl(ownerId);
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
                foreach (Ship ship in ShipRegistry.ShipsOnMap(focusedMap))
                {
                    if (universe.Autopilot.IsActive(ship.ownerId))
                    {
                        autopilotTarget = PilotTarget.Of(ship);
                        break;
                    }
                }
                // 在飞 shuttle 的自动驾驶任务（对接计划随动）同样占用驾驶位。
                if (!autopilotTarget.IsValid)
                {
                    Building shuttle = universe.Shuttles.InFlightBodyOn(focusedMap);
                    if (shuttle != null && universe.Autopilot.IsActive(shuttle.thingIDNumber))
                        autopilotTarget = PilotTarget.Of(shuttle);
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
                            $"autopilot interrupted by flight key: target key #{autopilotTarget.ControlKey} (direct takeover)");
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
                    piloted.IsShuttle ? piloted.shuttle.LabelCap : universe.LabelForOwner(piloted.ControlKey)));
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
            // 瞄准键（默认 B，§6.1）：驾驶期抑制其原版功能（建筑菜单等）；
            // 不含空格/暂停——瞄准模式不占用暂停。
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
            if (ActiveController != null && ActiveController.target.ControlKey == target.ControlKey)
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
