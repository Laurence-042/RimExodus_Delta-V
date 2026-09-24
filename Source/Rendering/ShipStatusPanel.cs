using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 左侧信息面板（星区架构 §11.1；解耦重构 §4：多船同图显示船只列表，可切换当前
    /// 查看/操作的船）：承载对象与成员、飞行状态、控制状态与操作收口的单一面板，
    /// 高度完全随内容自适应（不滚动）；内嵌**可缩放星球仪**（PlanetGlobeRenderer）——
    /// 星区地图即星球仪连续缩放（§12.2 定稿）：滑条/滚轮拉近逐层显示星区网格、对象
    /// 图标与距离标注，图标可悬停/点击聚焦。显示条件：聚焦图属 Delta-V 语境
    /// （FrameOf != null：口袋容器图 / 停靠宿主图 / 轨道图 / POI 内部）；
    /// 世界视图不叠加（剪影是其自己的显示通道，且可点击聚焦）。
    /// </summary>
    public static class ShipStatusPanel
    {
        private const float PanelWidth = 248f;
        private const float GlobeSize = 212f;
        private const float ShipRowHeight = 24f;

        // 每图选中的「当前查看/操作的船」（运行时 UI 状态；按归属 ID 记忆）。
        private static readonly Dictionary<Map, int> selectedShipByMap = new Dictionary<Map, int>();

        /// <summary>面板当前选中的船（驾驶切换键等多船入口共用；无选择返回 false）。</summary>
        public static bool TryGetSelectedShip(Map map, out Ship ship)
        {
            ship = Ship.Invalid;
            if (map == null) return false;
            if (!selectedShipByMap.TryGetValue(map, out int ownerId)) return false;
            Ship resolved = ShipRegistry.ShipByOwner(ownerId);
            if (!resolved.IsValid || resolved.map != map)
            {
                selectedShipByMap.Remove(map);
                return false;
            }
            ship = resolved;
            return true;
        }

        /// <summary>该图的当前操作船：面板已选择则用之；唯一船直取。</summary>
        private static Ship SelectedOrOnlyShip(UniverseManager universe, Map map)
        {
            if (TryGetSelectedShip(map, out Ship selected)) return selected;
            List<Ship> ships = ShipRegistry.ShipsOnMap(map);
            if (ships.Count == 1)
            {
                selectedShipByMap[map] = ships[0].ownerId;
                return ships[0];
            }
            return Ship.Invalid;
        }

        public static void Draw(UniverseManager universe)
        {
            Map map = Find.CurrentMap;
            if (universe == null || map == null || !WorldRendererUtility.DrawingMap) return;
            SpaceObject frame = universe.FrameOf(map);
            if (frame == null) return;

            // 图上的船列表（§4：多船同图各自列出，可切换查看/操作）。
            List<Ship> ships = ShipRegistry.ShipsOnMap(map);
            Ship selected = SelectedOrOnlyShip(universe, map);
            LandingApproachController.DockingPlan plan = universe.Approach.ActivePlan;
            bool dockingPlan = plan != null && plan.ship.ownerId == selected.ownerId;
            bool hasShip = selected.IsValid;
            bool playerControlled = !hasShip || universe.Pilot.CanPlayerControl(selected);

            // 未形成船的原版 Gravship（首次降落/发射到达）：唯一玩家驾驶台可直接
            // 进入自由飞行；多台须选中具体驾驶台（经其 gizmo）。
            bool bootstrapEntry = false;
            bool multiConsoleAmbiguous = false;
            if (!hasShip)
            {
                int playerConsoles = 0;
                foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole))
                {
                    if (thing.Faction == Faction.OfPlayer && thing.TryGetComp<CompDeltaVPilotConsole>() != null)
                    {
                        playerConsoles++;
                    }
                }
                bootstrapEntry = playerConsoles == 1;
                multiConsoleAmbiguous = playerConsoles > 1;
            }

            string title;
            if (hasShip)
            {
                title = "DeltaV_ShipPanelTitle".Translate(universe.LabelForOwner(selected.ownerId));
            }
            else if (bootstrapEntry || multiConsoleAmbiguous)
            {
                title = "DeltaV_DockedAreaTitle".Translate();
            }
            else
            {
                title = "DeltaV_PanelSectorTitle".Translate(frame.label);
            }

            // ---- 内容行（先备好文本，再按真实高度布局：面板高度随内容变化）----
            var statusLines = new List<string>();
            ShipControlState controlState = universe.Pilot.GetControlState(selected);
            SpacePose shipPose = hasShip ? ShipRegistry.ShipPose(selected) : default;
            if (hasShip)
            {
                ShipPhysics.MassReport report = ShipPhysics.GetReport(selected.map);
                string controlLabel = controlState == ShipControlState.Autopilot
                    ? "DeltaV_ControlAutopilot".Translate()
                    : controlState == ShipControlState.Manual
                        ? "DeltaV_ControlManual".Translate()
                        : "DeltaV_ControlUncontrolled".Translate();
                statusLines.Add("DeltaV_PanelFlightState".Translate(
                    ShipRegistry.StateOf(selected) == ShipDerivedState.FreeFlight
                        ? "DeltaV_FlightStateFree".Translate()
                        : "DeltaV_FlightStateDocked".Translate()));
                statusLines.Add("DeltaV_HudControl".Translate(controlLabel));
                statusLines.Add("DeltaV_HudSpeed".Translate(shipPose.velocity.magnitude.ToString("F1")));
                statusLines.Add("DeltaV_HudHeading".Translate(
                    (shipPose.angle * Mathf.Rad2Deg).ToString("F0")));
                statusLines.Add("DeltaV_HudTurnRate".Translate(
                    (shipPose.angularVelocity * Mathf.Rad2Deg).ToString("F1")));
                statusLines.Add("DeltaV_HudMass".Translate(report == null ? "?" : ((int)report.mass).ToString()));
                bool brakeHeld = DeltaVDefOf.DeltaV_AssistBrake != null && DeltaVDefOf.DeltaV_AssistBrake.IsDown;
                statusLines.Add("DeltaV_HudAssist".Translate(
                    brakeHeld
                        ? "DeltaV_Braking".Translate()
                        : (DeltaVMod.CurrentSettings.flightAssist
                            ? "DeltaV_On".Translate()
                            : "DeltaV_Off".Translate())));
            }
            else if (bootstrapEntry || multiConsoleAmbiguous)
            {
                statusLines.Add("DeltaV_PanelFlightState".Translate("DeltaV_FlightStateDocked".Translate()));
            }
            if (dockingPlan)
            {
                statusLines.Add("DeltaV_DockPlanStatus".Translate(
                    plan.host.label, ((int)plan.Distance()).ToString()));
            }

            Text.Font = GameFont.Small;
            float contentWidth = PanelWidth - 20f;
            float titleHeight = Mathf.Max(24f, Text.CalcHeight(title, contentWidth));
            Text.Font = GameFont.Tiny;
            float linesHeight = 0f;
            foreach (string line in statusLines)
            {
                linesHeight += Mathf.Max(20f, Text.CalcHeight(line, contentWidth));
            }
            float shipListHeight = ships.Count > 1 ? ships.Count * ShipRowHeight + 4f : 0f;

            // 高度完全随内容自适应（不滚动）：星球仪 + 缩放滑条 + 标题/状态行 + 船列表
            // + 当前模式的按钮行数。
            float modeButtonsHeight = hasShip
                ? (playerControlled ? 27f + 6f : 0f) + (dockingPlan || playerControlled ? 27f + 8f : 0f)
                : (bootstrapEntry || multiConsoleAmbiguous) ? 27f + 6f : 0f;
            float contentHeight = 8f + titleHeight + 4f + linesHeight + 8f + shipListHeight + modeButtonsHeight;
            const float sliderRowHeight = 30f;
            float outerHeight = GlobeSize + 8f + sliderRowHeight + contentHeight;
            float y = Mathf.Clamp(UI.screenHeight * 0.45f - outerHeight * 0.5f, 48f,
                Mathf.Max(48f, UI.screenHeight - outerHeight - 32f));
            var panelRect = new Rect(8f, y, PanelWidth, outerHeight);
            Widgets.DrawMenuSection(panelRect);
            var inner = panelRect.ContractedBy(8f);

            // 星球仪固定在面板顶部（GL 绘制使用绝对 GUI 屏幕坐标）；
            // 无任何飞船时（首次到达、尚未认领）以当前图帧位置为相机锚点。
            PlanetGlobeRenderer.Draw(new Rect(inner.x, inner.y, inner.width, GlobeSize), universe, frame);

            // 星球仪缩放滑条：拉远 = 整球态势，拉近 = 星区地图（§12.2 定稿）。
            PlanetGlobeRenderer.Zoom01 = Widgets.HorizontalSlider(
                new Rect(inner.x, inner.y + GlobeSize + 8f, inner.width, 22f),
                PlanetGlobeRenderer.Zoom01, 0f, 1f, false, null,
                "DeltaV_PanelGlobeZoomOut".Translate(), "DeltaV_PanelGlobeZoomIn".Translate());

            // 文本与按钮区（相对坐标）。
            var contentRect = new Rect(inner.x, inner.y + GlobeSize + 8f + sliderRowHeight, inner.width, contentHeight);
            GUI.BeginGroup(contentRect);
            float lineY = 0f;
            float drawWidth = contentWidth;

            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, lineY, drawWidth, titleHeight), title);
            lineY += titleHeight + 4f;
            Text.Font = GameFont.Tiny;
            foreach (string line in statusLines)
            {
                float h = Mathf.Max(20f, Text.CalcHeight(line, drawWidth));
                Widgets.Label(new Rect(0f, lineY, drawWidth, h), line);
                lineY += h;
            }
            lineY += 8f;

            // ---- 船只列表（多船同图，§4）：当前查看/操作的船可切换 ----
            if (ships.Count > 1)
            {
                foreach (Ship ship in ships)
                {
                    string stateLabel = ShipRegistry.StateOf(ship) == ShipDerivedState.FreeFlight
                        ? "DeltaV_FlightStateFree".Translate()
                        : "DeltaV_FlightStateDocked".Translate();
                    string rowLabel = $"{universe.LabelForOwner(ship.ownerId)} · {stateLabel}";
                    var rowRect = new Rect(0f, lineY, drawWidth, ShipRowHeight);
                    bool isSelected = ship.ownerId == selected.ownerId;
                    if (isSelected)
                    {
                        Widgets.DrawHighlight(rowRect);
                    }
                    if (Widgets.ButtonInvisible(rowRect, false))
                    {
                        selectedShipByMap[map] = ship.ownerId;
                    }
                    Text.Font = GameFont.Tiny;
                    GUI.color = isSelected ? Color.white : new Color(1f, 1f, 1f, 0.75f);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(rowRect.ContractedBy(4f), rowLabel);
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(rowRect,
                        "DeltaV_PanelShipRowTip".Translate(universe.LabelForOwner(ship.ownerId)));
                    lineY += ShipRowHeight;
                }
                lineY += 4f;
            }

            // 模式按钮全部收进面板（不再分开放）。
            if (hasShip)
            {
                if (playerControlled)
                {
                    string pilotButton = controlState == ShipControlState.Manual && dockingPlan
                        ? "DeltaV_ControlSwitchAutopilot".Translate()
                        : universe.Pilot.IsPlayerInputArmed(selected)
                            ? "DeltaV_PilotButtonExit".Translate()
                            : "DeltaV_PilotButtonEnter".Translate();
                    if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), pilotButton))
                    {
                        universe.Pilot.ToggleControl(universe);
                    }
                    lineY += 27f + 6f;
                }
                if (dockingPlan)
                {
                    if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), "DeltaV_DockCancelButton".Translate()))
                    {
                        universe.Approach.CancelSelectionOrPlan(universe, "user");
                    }
                    lineY += 27f + 8f;
                }
                else if (playerControlled)
                {
                    if (ShipRegistry.StateOf(selected) == ShipDerivedState.FreeFlight)
                    {
                        if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), "DeltaV_BeginDockingButton".Translate()))
                        {
                            universe.ShipTransitions.BeginDocking(selected);
                        }
                        lineY += 27f + 8f;
                    }
                    else
                    {
                        if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), "DeltaV_BeginFreeFlightButton".Translate()))
                        {
                            universe.ShipTransitions.BeginFreeFlight(universe, selected);
                        }
                        lineY += 27f + 8f;
                    }
                }
                else
                {
                    lineY += 8f;
                }
            }
            else if (bootstrapEntry)
            {
                // 首次降落/发射到达：图上唯一的玩家驾驶台，直接进入自由飞行（认领+脱离）。
                Building console = null;
                foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole))
                {
                    if (thing.Faction == Faction.OfPlayer && thing.TryGetComp<CompDeltaVPilotConsole>() != null)
                    {
                        console = thing as Building;
                        break;
                    }
                }
                if (console != null
                    && Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), "DeltaV_BeginFreeFlightButton".Translate()))
                {
                    universe.ShipTransitions.BeginFreeFlightFromConsole(universe, console);
                }
                lineY += 27f + 6f;
            }
            else if (multiConsoleAmbiguous)
            {
                // 多艘逆重飞船共泊：不能任选一台驾驶台，指引到具体驾驶台的 gizmo。
                var pickRect = new Rect(0f, lineY, drawWidth, 27f);
                Text.Font = GameFont.Small;
                GUI.color = new Color(1f, 1f, 1f, 0.4f);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(pickRect, "DeltaV_BeginFreeFlightButton".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                TooltipHandler.TipRegion(pickRect, "DeltaV_PanelPickConsoleTip".Translate());
                Text.Font = GameFont.Tiny;
                lineY += 27f + 6f;
            }

            GUI.EndGroup();
            Text.Font = GameFont.Small;
        }
    }
}
