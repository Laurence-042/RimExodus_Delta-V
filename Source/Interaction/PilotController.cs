using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
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
    public static class PilotController
    {
        /// <summary>低于此速度/角速度不再发辅助反推指令（推进器只负责主要减速）。</summary>
        private const float MinAssistSpeed = 0.05f;

        private const float MinAssistOmega = 0.01f;

        /// <summary>残差判定阈值：任意高于此值的漂移都置 assist 标志（残差由 Integrate 直接衰减到零）。</summary>
        private const float AssistEpsilon = 0.001f;

        private const float AssistOmegaEpsilon = 0.0005f;

        /// <summary>Harmony Prefix 的快速开关：驾驶激活时抑制与驾驶键重叠的原版按键。</summary>
        public static volatile bool SuppressVanillaKeys;

        /// <summary>当前驾驶键（含 A/B 槽）的实际绑定；进入驾驶时重建。</summary>
        public static HashSet<KeyCode> ActivePilotKeys = new HashSet<KeyCode>();

        /// <summary>驾驶 HUD 的运行时滚动位置；不属于游戏状态，不存档。</summary>
        private static Vector2 hudScrollPosition = Vector2.zero;

        // ---- 状态 ----

        public static bool IsActive(UniverseManager universe)
        {
            return universe.pilotShipId != -1 && universe.ShipById(universe.pilotShipId)?.map != null;
        }

        public static ShipState PilotedShip(UniverseManager universe)
        {
            return universe.ShipById(universe.pilotShipId);
        }

        public static void Toggle(UniverseManager universe)
        {
            ShipState focused = universe.ShipOnMap(Find.CurrentMap);
            if (focused == null)
            {
                Messages.Message("DeltaV_PilotNeedShipFocus".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (focused.docked)
            {
                // 停靠中的船没有可驾驶的船体（真实建筑在宿主图）；起飞入口见 DockController。
                Messages.Message("DeltaV_PilotNeedShipFocus".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (universe.pilotShipId == focused.objectId)
            {
                Exit(universe, "DeltaV_PilotOff".Translate(focused.label));
            }
            else
            {
                universe.pilotShipId = focused.objectId;
                hudScrollPosition = Vector2.zero;
                RebuildPilotKeySet();
                Log.Message($"[DeltaV] Pilot mode engaged: {focused.label} (#{focused.objectId}). Pilot keys: [{string.Join(", ", ActivePilotKeys)}].");
                DeltaVLog.Message(DeltaVLogModule.Core, $"engage details: speed={focused.pose.velocity} m/s, heading={SpaceTransform.WrapAngle(focused.pose.angle) * Mathf.Rad2Deg:F0} deg, omega={focused.pose.angularVelocity * Mathf.Rad2Deg:F1} deg/s");
                Messages.Message("DeltaV_PilotOn".Translate(focused.label), MessageTypeDefOf.PositiveEvent, false);
            }
        }

        public static void Exit(UniverseManager universe, string message = null)
        {
            if (universe.pilotShipId == -1)
            {
                return;
            }
            ShipState ship = PilotedShip(universe);
            Log.Message($"[DeltaV] Pilot mode disengaged (was #{universe.pilotShipId} '{ship?.label ?? "?"}'; suppressed reads: {Patch_KeyBindingSuppression.SuppressedReads}).");
            universe.pilotShipId = -1;
            hudScrollPosition = Vector2.zero;
            SuppressVanillaKeys = false;
            if (message != null)
            {
                Messages.Message(message, MessageTypeDefOf.NeutralEvent, false);
            }
        }

        /// <summary>每帧由 UniverseManager.GameComponentUpdate 调用：维护自动退出与抑制开关。
        /// 抑制从关闭翻转为开启时输出一次性摘要（哪些原版绑定将被抑制）——必要日志，常开。</summary>
        public static void UpdateFrameState(UniverseManager universe)
        {
            ShipState piloted = PilotedShip(universe);
            if (piloted?.map == null)
            {
                if (universe.pilotShipId != -1)
                {
                    Log.Warning($"[DeltaV] Piloted ship #{universe.pilotShipId} lost its map; exiting pilot mode.");
                    Exit(universe);
                }
                SuppressVanillaKeys = false;
                return;
            }
            if (Find.CurrentMap != piloted.map)
            {
                Exit(universe, "DeltaV_PilotExitAuto".Translate(piloted.label));
                SuppressVanillaKeys = false;
                return;
            }
            if (ActivePilotKeys.Count == 0)
            {
                RebuildPilotKeySet();
            }
            if (!SuppressVanillaKeys)
            {
                List<string> overlaps = CountOverlappingVanillaDefs();
                Log.Message("[DeltaV] Key suppression ACTIVE. Overlapping vanilla bindings: " +
                    (overlaps.Count == 0
                        ? "(none found - WASD/QE will keep their vanilla function!)"
                        : string.Join(", ", overlaps)));
                DeltaVLog.Message(DeltaVLogModule.Harmony, "suppression flag false->true; overlap scan across all KeyBindingDefs done");
            }
            SuppressVanillaKeys = true;
        }

        private static List<string> CountOverlappingVanillaDefs()
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
        public static void RebuildPilotKeySet()
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
            ActivePilotKeys = set;
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

        private static string BoundKeysLabel(KeyBindingDef def)
        {
            GetBoundKeys(def, out KeyCode a, out KeyCode b);
            if (a != KeyCode.None && b != KeyCode.None)
            {
                return $"{a}/{b}";
            }
            return a != KeyCode.None ? a.ToString() : b.ToString();
        }

        private static IEnumerable<KeyBindingDef> FlightKeyDefs()
        {
            yield return DeltaVDefOf.DeltaV_ThrustForward;
            yield return DeltaVDefOf.DeltaV_ThrustBackward;
            yield return DeltaVDefOf.DeltaV_StrafeLeft;
            yield return DeltaVDefOf.DeltaV_StrafeRight;
            yield return DeltaVDefOf.DeltaV_RotateLeft;
            yield return DeltaVDefOf.DeltaV_RotateRight;
        }

        private static IEnumerable<KeyBindingDef> SuppressionKeyDefs()
        {
            foreach (KeyBindingDef def in FlightKeyDefs())
            {
                yield return def;
            }
            yield return DeltaVDefOf.DeltaV_TogglePilotMode;
            yield return DeltaVDefOf.DeltaV_AssistBrake; // Ctrl 刹停时抑制原版 Ctrl 用途
        }

        /// <summary>非本 mod 分类的绑定是否与当前驾驶键重叠（供 Harmony Prefix 判定抑制）。</summary>
        public static bool OverlapsActiveKeys(KeyBindingDef def)
        {
            if (def.category == DeltaVDefOf.DeltaV || ActivePilotKeys.Count == 0)
            {
                return false;
            }
            GetBoundKeys(def, out KeyCode a, out KeyCode b);
            return (a != KeyCode.None && ActivePilotKeys.Contains(a))
                || (b != KeyCode.None && ActivePilotKeys.Contains(b));
        }

        // ---- 输入采样与指令合成 ----

        public struct PilotInput
        {
            public float fx; // W = +1（主驾驶台正面）/ S = −1
            public float fz; // A = +1（左舷 +Z）/ D = −1
            public float torque; // Q = +1（逆时针）/ E = −1
        }

        public static PilotInput SampleInput()
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

        public static PilotCommand ComposeCommand(ShipState ship)
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
            if (!DeltaVMod.Settings.flightAssist && !brakeHeld)
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
                    $"assist axes x={result.assistX} z={result.assistZ} w={result.assistW} (brake={brakeHeld}, setting={DeltaVMod.Settings.flightAssist}); cmd=({result.command.fx:+0;-0;0},{result.command.fz:+0;-0;0},{result.command.torque:+0;-0;0}); bodyV={bodyVelocity:F2} omega={ship.pose.angularVelocity:F3}");
            }
            return result;
        }

        // 变更检测缓存：全部输入埋点只在变化沿输出（触发式，避免持续辅助反推每 tick 刷爆日志）。
        private static float lastLoggedFx;
        private static float lastLoggedFz;
        private static float lastLoggedTorque = -999f;

        private static bool HasInputChanged(PilotInput input)
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

        private static int lastAssistFlags = -1;

        private static bool HasAssistChanged(PilotCommand command)
        {
            int flags = (command.assistX ? 1 : 0) | (command.assistZ ? 2 : 0) | (command.assistW ? 4 : 0);
            if (flags == lastAssistFlags)
            {
                return false;
            }
            lastAssistFlags = flags;
            return true;
        }

        // ---- HUD 与屏幕按钮（UniverseManager.GameComponentOnGUI 调用）----

        public static void DrawHud(UniverseManager universe)
        {
            ShipState piloted = PilotedShip(universe);
            if (piloted?.map == null || Find.CurrentMap != piloted.map)
            {
                return;
            }
            // 停靠中的船（聚焦其空口袋图）没有驾驶入口；出口在实际停靠图按钮/驾驶台 gizmo。
            if (piloted.docked)
            {
                return;
            }
            bool active = universe.pilotShipId == piloted.objectId;
            float y = UI.screenHeight * 0.42f;
            if (!active)
            {
                Rect enterRect = new Rect(8f, y, 170f, 30f);
                if (Widgets.ButtonText(enterRect, "DeltaV_PilotButtonEnter".Translate()))
                {
                    Toggle(universe);
                }
                return;
            }

            ShipPhysics.MassReport report = ShipPhysics.GetReport(piloted);
            var rect = new Rect(8f, y, 240f, 150f);
            Widgets.DrawMenuSection(rect);
            var inner = rect.ContractedBy(8f);

            string title = "DeltaV_PilotHudTitle".Translate(piloted.label);
            string speed = "DeltaV_HudSpeed".Translate(piloted.pose.velocity.magnitude.ToString("F1"));
            string heading = "DeltaV_HudHeading".Translate(
                (ShipOrientation.HeadingAngle(piloted) * Mathf.Rad2Deg).ToString("F0"));
            string turnRate = "DeltaV_HudTurnRate".Translate(
                (piloted.pose.angularVelocity * Mathf.Rad2Deg).ToString("F1"));
            string mass = "DeltaV_HudMass".Translate(report == null ? "?" : ((int)report.mass).ToString());
            bool brakeHeld = DeltaVDefOf.DeltaV_AssistBrake != null && DeltaVDefOf.DeltaV_AssistBrake.IsDown;
            string assist = "DeltaV_HudAssist".Translate(
                brakeHeld
                    ? "DeltaV_Braking".Translate()
                    : (DeltaVMod.Settings.flightAssist
                        ? "DeltaV_On".Translate()
                        : "DeltaV_Off".Translate()));

            // 为滚动条预留 16px，并按翻译后的真实文本高度计算内容；长船名和语言换行
            // 不再把底部状态/退出按钮裁掉。
            float contentWidth = inner.width - 16f;
            Text.Font = GameFont.Small;
            float titleHeight = Mathf.Max(24f, Text.CalcHeight(title, contentWidth));
            Text.Font = GameFont.Tiny;
            string[] statusLines = { speed, heading, turnRate, mass, assist };
            float contentHeight = titleHeight;
            foreach (string statusLine in statusLines)
            {
                contentHeight += Mathf.Max(20f, Text.CalcHeight(statusLine, contentWidth));
            }
            contentHeight += 30f;

            var viewRect = new Rect(0f, 0f, contentWidth, contentHeight);
            Widgets.BeginScrollView(inner, ref hudScrollPosition, viewRect);
            float lineY = 0f;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, lineY, contentWidth, titleHeight), title);
            lineY += titleHeight;
            Text.Font = GameFont.Tiny;
            foreach (string statusLine in statusLines)
            {
                float lineHeight = Mathf.Max(20f, Text.CalcHeight(statusLine, contentWidth));
                Widgets.Label(new Rect(0f, lineY, contentWidth, lineHeight), statusLine);
                lineY += lineHeight;
            }
            if (Widgets.ButtonText(new Rect(0f, lineY + 3f, contentWidth, 27f), "DeltaV_PilotButtonExit".Translate()))
            {
                Exit(universe, "DeltaV_PilotOff".Translate(piloted.label));
            }
            Widgets.EndScrollView();
        }
    }
}
