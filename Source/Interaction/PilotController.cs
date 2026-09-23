using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// 阶段2 驾驶交互（权威设计见 doc/阶段2-基础驾驶.md）。
    ///
    /// - 驾驶对象 = 当前聚焦船（含敌舰——两船对等，且驾驶敌舰是"非聚焦船连续运动"
    ///   表现的核心验证手段）；持久化状态只有 UniverseManager.pilotShipId；
    /// - 进入/退出：TogglePilotMode 键（KeyDownEvent）+ 屏幕按钮 + dev action；
    ///   聚焦离开驾驶船的地图或地图丢失时自动退出；
    /// - 输入每 tick 采样（KeyBindingDef.IsDown 轮询），与飞行辅助合成为 Command
    ///   后交给 ShipPhysics；辅助只在玩家该轴无输入时发反向指令；
    /// - SuppressVanillaKeys / ActivePilotKeys 供 Harmony 按键抑制 Prefix 使用：
    ///   快速通道是静态 bool，每帧由 UniverseManager.GameComponentUpdate 刷新，
    ///   键集合在进入驾驶时按当前实际绑定重建（支持任意一侧改键）。
    /// </summary>
    public sealed class PilotService
    {
        private readonly UniverseManager universe;
        private readonly HashSet<KeyBindingDef> loggedSuppressedDefs = new HashSet<KeyBindingDef>();
        private int autopilotInputShipId = -1;

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

        /// <summary>Harmony Prefix 的快速开关：驾驶激活时抑制与驾驶键重叠的原版按键。</summary>
        // ---- 状态 ----

        public bool IsActive(UniverseManager universe)
        {
            return universe.pilotShipId != -1 && universe.ShipById(universe.pilotShipId)?.map != null;
        }

        public ShipState PilotedShip(UniverseManager universe)
        {
            return universe.ShipById(universe.pilotShipId);
        }

        public ShipControlState GetControlState(ShipState ship)
        {
            if (ship == null) return ShipControlState.Uncontrolled;
            if (universe.Autopilot.IsActive(ship)) return ShipControlState.Autopilot;
            return universe.pilotShipId == ship.objectId
                ? ShipControlState.Manual
                : ShipControlState.Uncontrolled;
        }

        public bool IsPlayerInputArmed(ShipState ship)
        {
            ShipControlState state = GetControlState(ship);
            return state == ShipControlState.Manual
                || state == ShipControlState.Autopilot && autopilotInputShipId == ship.objectId;
        }

        public void ArmAutopilotInput(ShipState ship)
        {
            autopilotInputShipId = ship?.objectId ?? -1;
        }

        public void DisarmAutopilotInput(ShipState ship = null)
        {
            if (ship == null || autopilotInputShipId == ship.objectId) autopilotInputShipId = -1;
        }

        public void ToggleControl(UniverseManager universe)
        {
            ShipState focused = universe.ShipOnMap(Find.CurrentMap);
            if (focused == null)
            {
                Messages.Message("DeltaV_PilotNeedShipFocus".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (focused.IsDocked)
            {
                // 已停靠船没有可手操的独立刚体；自由飞行入口见 ShipFlightTransitionService。
                Messages.Message("DeltaV_PilotNeedShipFocus".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            ShipControlState state = GetControlState(focused);
            if (state == ShipControlState.Autopilot)
            {
                if (IsPlayerInputArmed(focused)) DisarmAutopilotInput(focused);
                else ArmAutopilotInput(focused);
            }
            else if (state == ShipControlState.Manual
                && universe.Approach.ActivePlan?.ship == focused)
            {
                universe.Approach.EngageAutopilot(universe);
            }
            else if (state == ShipControlState.Manual)
            {
                Exit(universe, "DeltaV_PilotOff".Translate(focused.label));
            }
            else if (!CanPlayerControl(focused))
            {
                // 派系门禁（2026-09-21 阶段4）：驾驶是完整控制权，限玩家方（dev 模式放行，
                // 保留双船对等测试工作流）。
                Messages.Message("DeltaV_PilotWrongFaction".Translate(focused.label), MessageTypeDefOf.RejectInput, false);
            }
            else if (universe.ShipTransitions.IsDockingFlowShip(focused)
                && !ReferenceEquals(focused, universe.Approach.ApproachShip))
            {
                // 选点中的船不可驾驶（计划船除外——手操打断自动驾驶但不取消计划）。
                Messages.Message("DeltaV_PilotNeedShipFocus".Translate(), MessageTypeDefOf.RejectInput, false);
            }
            else
            {
                Enter(universe, focused);
            }
        }

        /// <summary>驾驶/模式控制权门禁（2026-09-21 阶段4）：玩家方船只；开发者模式放行
        /// （测试场景需要驾驶敌舰验证相对运动表现）。</summary>
        public bool CanPlayerControl(ShipState ship)
        {
            return ship != null && (ship.faction == Faction.OfPlayer || DebugSettings.godMode);
        }

        public void Enter(UniverseManager universe, ShipState ship)
        {
            if (universe == null || ship?.map == null || ship.IsDocked) return;
            if (universe.ShipTransitions.IsDockingFlowShip(ship)
                && !ReferenceEquals(ship, universe.Approach.ApproachShip)) return;
            if (!CanPlayerControl(ship)) return;
            // 手操打断自动驾驶（不取消对接计划——完成判据是几何的，手飞到位照样对接）。
            if (universe.Autopilot.IsActive(ship))
            {
                universe.Autopilot.Detach(ship, "manual input");
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"autopilot interrupted by manual input: {ship.label} (pilot mode engaged)");
                Messages.Message("DeltaV_DockAutopilotStopped".Translate(), MessageTypeDefOf.NeutralEvent, false);
            }
            DisarmAutopilotInput(ship);
            universe.pilotShipId = ship.objectId;
            RebuildPilotKeySet();
            Log.Message($"[DeltaV] Pilot mode engaged: {ship.label} (#{ship.objectId}). Pilot keys: [{string.Join(", ", PilotKeyBridge.ActiveKeys)}].");
            DeltaVLog.Message(DeltaVLogModule.Core, $"engage details: speed={ship.pose.velocity} m/s, heading={SpaceTransform.WrapAngle(ship.pose.angle) * Mathf.Rad2Deg:F0} deg, omega={ship.pose.angularVelocity * Mathf.Rad2Deg:F1} deg/s");
            Messages.Message("DeltaV_PilotOn".Translate(ship.label), MessageTypeDefOf.PositiveEvent, false);
        }

        public void Exit(UniverseManager universe, string message = null)
        {
            if (universe.pilotShipId == -1)
            {
                return;
            }
            ShipState ship = PilotedShip(universe);
            Log.Message($"[DeltaV] Pilot mode disengaged (was #{universe.pilotShipId} '{ship?.label ?? "?"}'; suppressed reads: {SuppressedReads}).");
            universe.pilotShipId = -1;
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
            ShipState piloted = PilotedShip(universe);
            ShipState focusedShip = universe.ShipOnMap(Find.CurrentMap);
            ShipState autopilotShip = focusedShip != null && universe.Autopilot.IsActive(focusedShip)
                ? focusedShip
                : null;
            bool focusOnAutopilotShip = autopilotShip != null && autopilotShip.map != null
                && Find.CurrentMap == autopilotShip.map && IsPlayerInputArmed(autopilotShip);

            if (piloted?.map == null)
            {
                if (universe.pilotShipId != -1)
                {
                    Log.Warning($"[DeltaV] Piloted ship #{universe.pilotShipId} lost its map; exiting pilot mode.");
                    Exit(universe);
                }
                if (focusOnAutopilotShip)
                {
                    PilotInput input = SampleInput();
                    if (input.fx != 0f || input.fz != 0f || input.torque != 0f)
                    {
                        DeltaVLog.Message(DeltaVLogModule.Core,
                            $"autopilot interrupted by flight key: {autopilotShip.label} (direct takeover)");
                        Enter(universe, autopilotShip); // 打断自动驾驶并接管：同帧输入生效
                        piloted = PilotedShip(universe);
                        focusOnAutopilotShip = false;
                    }
                }
            }
            bool manualPiloting = piloted?.map != null && Find.CurrentMap == piloted.map;
            if (!manualPiloting && piloted?.map != null)
            {
                Exit(universe, "DeltaV_PilotExitAuto".Translate(piloted.label));
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
        private void GetBoundKeys(KeyBindingDef def, out KeyCode a, out KeyCode b)
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

        // ---- 输入采样与指令合成 ----

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

        public PilotCommand ComposeCommand(ShipState ship)
        {
            var result = new PilotCommand();
            PilotInput input = SampleInput();
            // W/S 与 A/D 定义在主驾驶台参考系；推进器分配器使用 Map 局部系。
            // 主台不是 East 时在这里集中旋转一次，避免把“船艏恒为 +X”的假设扩散到物理层。
            float consoleAngle = ShipOrientation.LocalForwardAngle(ship);
            Vector2 pilotTranslation = new Vector2(input.fx, input.fz);
            result.command.torque = input.torque;
            if (DeltaVLog.Enabled(DeltaVLogModule.Input) && HasInputChanged(input))
            {
                DeltaVLog.Message(DeltaVLogModule.Input,
                    $"input: fx={input.fx:+0;-0;0} fz={input.fz:+0;-0;0} torque={input.torque:+0;-0;0} (keys polled via IsDown)");
            }
            // 辅助生效 = 设置开启 或 按住刹车键（临时启用，辅助关闭时也可刹停）。
            bool brakeHeld = DeltaVDefOf.DeltaV_AssistBrake != null && DeltaVDefOf.DeltaV_AssistBrake.IsDown;
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
            Vector2 mapVelocity = SpaceTransform.RotateInverse(ship.pose.velocity, ship.pose.angle);
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
            if (input.torque == 0f && Mathf.Abs(ship.pose.angularVelocity) > AssistOmegaEpsilon)
            {
                result.assistW = true;
                if (Mathf.Abs(ship.pose.angularVelocity) > MinAssistOmega)
                {
                    result.command.torque = -Mathf.Sign(ship.pose.angularVelocity);
                }
            }
            Vector2 assistedMapTranslation = SpaceTransform.Rotate(pilotTranslation, consoleAngle);
            result.command.fx = assistedMapTranslation.x;
            result.command.fz = assistedMapTranslation.y;
            if (DeltaVLog.Enabled(DeltaVLogModule.Input) && HasAssistChanged(result))
            {
                DeltaVLog.Message(DeltaVLogModule.Input,
                    $"assist axes x={result.assistX} z={result.assistZ} w={result.assistW} (brake={brakeHeld}, setting={DeltaVMod.CurrentSettings.flightAssist}); cmd=({result.command.fx:+0;-0;0},{result.command.fz:+0;-0;0},{result.command.torque:+0;-0;0}); bodyV={bodyVelocity:F2} omega={ship.pose.angularVelocity:F3}");
            }
            return result;
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

        // ---- HUD 与屏幕按钮：绘制职责已移交 ShipStatusPanel（UniverseManager.GameComponentOnGUI 调用）----
    }
}
