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
    /// 内容按可用屏幕高度滚动；内嵌**可缩放星球仪**（PlanetGlobeRenderer）——
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

        // 上一次 Draw 的聚焦图（检测聚焦切图时刻，应用「优先玩家船」的重新默认）。
        private static Map lastDrawnMap;
        private static Vector2 contentScroll;

        public static void Forget(Map map)
        {
            if (map == null) return;
            selectedShipByMap.Remove(map);
            if (lastDrawnMap == map) lastDrawnMap = null;
        }

        public static void ClearRuntimeState()
        {
            selectedShipByMap.Clear();
            lastDrawnMap = null;
            contentScroll = Vector2.zero;
        }

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

        /// <summary>该图的当前操作船：面板已选择则用之；否则默认船（玩家派系船优先、
        /// 唯一船直取，DefaultShipOnMap 单一实现）并写入记忆，行高亮保持稳定。</summary>
        private static Ship SelectedOrOnlyShip(UniverseManager universe, Map map)
        {
            if (TryGetSelectedShip(map, out Ship selected)) return selected;
            Ship defaulted = ShipRegistry.DefaultShipOnMap(map);
            if (defaulted.IsValid)
            {
                selectedShipByMap[map] = defaulted.ownerId;
                return defaulted;
            }
            return Ship.Invalid;
        }

        /// <summary>
        /// 聚焦切到该图时刻的「优先玩家船」（需求 1 完整语义）：当前记忆已是玩家船则不动；
        /// 否则图上有玩家船就覆盖陈旧的非玩家记忆——典型场景：对接完成切到宿主图时，把
        /// 选点阶段（当时宿主图唯一船）自动记忆的宿主船换成刚停靠进来的玩家船。玩家显式
        /// 点选在本图期间始终有效，离开再回来按本规则重新默认；无玩家船则保持现状。
        /// </summary>
        private static void ApplyFocusPreference(Map map)
        {
            if (TryGetSelectedShip(map, out Ship memo) && memo.IsValid
                && ShipRegistry.FactionOf(memo) == Faction.OfPlayer)
            {
                return;
            }
            Ship preferred = ShipRegistry.DefaultShipOnMap(map);
            if (preferred.IsValid && ShipRegistry.FactionOf(preferred) == Faction.OfPlayer)
            {
                selectedShipByMap[map] = preferred.ownerId;
            }
        }

        /// <summary>外部写入口（点击选中物聚焦归属船，Patch_SelectorShipFocus）：只改面板选中状态。</summary>
        public static void SelectShip(Ship ship)
        {
            if (ship.IsValid && ship.map != null && !ship.map.Disposed)
            {
                selectedShipByMap[ship.map] = ship.ownerId;
            }
        }

        /// <summary>罗盘方向名（Keyed 单实现；面板朝向罗盘按钮与旋转完成消息共用）。</summary>
        public static string CompassLabel(Rot4 facing)
        {
            return facing == Rot4.North ? "DeltaV_CompassNorth".Translate()
                : facing == Rot4.East ? "DeltaV_CompassEast".Translate()
                : facing == Rot4.South ? "DeltaV_CompassSouth".Translate()
                : "DeltaV_CompassWest".Translate();
        }

        public static void Draw(UniverseManager universe)
        {
            Map map = Find.CurrentMap;
            if (universe == null || map == null || !WorldRendererUtility.DrawingMap) return;
            SpaceObject frame = universe.FrameOf(map);
            if (frame == null) return;
            if (map != lastDrawnMap)
            {
                lastDrawnMap = map;
                contentScroll = Vector2.zero;
                ApplyFocusPreference(map);
            }

            // 图上的船列表（§4：多船同图各自列出，可切换查看/操作）。
            List<Ship> ships = ShipRegistry.ShipsOnMap(map);
            Ship selected = SelectedOrOnlyShip(universe, map);
            LandingApproachController.DockingPlan plan = universe.Approach.ActivePlan;
            bool dockingPlan = plan != null && plan.ship.ownerId == selected.ownerId;
            bool hasShip = selected.IsValid;
            bool freeFlight = hasShip
                && ShipRegistry.StateOf(selected) == ShipDerivedState.FreeFlight;
            bool playerControlled = !hasShip || universe.Pilot.CanPlayerControl(selected);
            // 驾驶/认领入口只服务零重力图（重力图只能逆重战略发射）。
            bool zeroGravity = FreeFlightBootstrap.IsZeroGravity(universe, map);

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
            // 留出滚动条宽度；有无滚动时文字换行和武器组位置保持一致。
            float contentWidth = PanelWidth - 36f;
            float titleHeight = Mathf.Max(24f, Text.CalcHeight(title, contentWidth));
            Text.Font = GameFont.Tiny;
            float linesHeight = 0f;
            foreach (string line in statusLines)
            {
                linesHeight += Mathf.Max(20f, Text.CalcHeight(line, contentWidth));
            }
            float shipListHeight = ships.Count > 1 ? ships.Count * ShipRowHeight + 4f : 0f;

            // 内容高度：星球仪 + 缩放滑条 + 标题/状态行 + 船列表
            // + 当前模式的按钮行数（已停靠的船只有驾驶按钮——首次出力即分离）
            // + 战斗区块：火控、战斗机动、武器组和战斗位置。
            bool mapHasWeapons = SpaceWeaponService.AllWeaponsOn(map).Any(w =>
                w.parent.Faction == Faction.OfPlayer);
            // 在飞 shuttle 列表（§7.2）：聚焦图是 shuttle 口袋图时该行也是「当前查看对象」。
            List<SpaceObject> shuttles = universe.Shuttles.InFlightContainers();
            bool shuttleFocused = frame.baseTag == SpaceObjectTag.Shuttle;
            float shuttleListHeight = shuttles.Count > 0 ? shuttles.Count * ShipRowHeight + 4f : 0f;
            bool combatBlock = hasShip && playerControlled && zeroGravity;
            bool fireControlButton = combatBlock && Find.CurrentMap == selected.map;
            float combatHeight = 0f;
            if (combatBlock)
            {
                combatHeight += 8f;
                if (fireControlButton) combatHeight += 27f + 6f;
                combatHeight += 27f + 6f;
                if (mapHasWeapons)
                {
                    combatHeight += 26f * 2f + 4f; // 主/副两排武器组方块
                    combatHeight += (27f + 4f) * 2f + 2f; // 进入/重新协调 + 退出战斗位置
                }
            }
            float modeButtonsHeight = hasShip
                ? (playerControlled && zeroGravity ? 27f + 6f : 0f)
                  + (dockingPlan || (playerControlled && freeFlight) ? 27f + 8f : 0f)
                  + (playerControlled && freeFlight && !dockingPlan ? 20f + 26f * 3f + 6f : 0f)
                : (bootstrapEntry || multiConsoleAmbiguous) && zeroGravity ? 27f + 6f : 0f;
            Building shuttleHull = shuttleFocused ? SpaceShuttleService.HullOf(frame) : null;
            bool shuttleControls = shuttleFocused && zeroGravity && shuttleHull != null
                && (shuttleHull.Faction == Faction.OfPlayer || DebugSettings.godMode);
            bool shuttleDockButton = shuttleControls && !frame.projectileRoleOverride;
            bool shuttleBoardButton = shuttleControls && shuttleHull.TryGetComp<CompSpaceShuttle>()?.CanBoard == true
                && universe.Shuttles.IntrusionArmed(frame);
            if (shuttleControls)
            {
                modeButtonsHeight += 27f + 6f;
                if (shuttleDockButton) modeButtonsHeight += 27f + 8f;
                if (shuttleBoardButton) modeButtonsHeight += 27f + 8f;
            }
            modeButtonsHeight += combatHeight;
            float contentHeight = 8f + titleHeight + 4f + linesHeight + 8f + shipListHeight
                + shuttleListHeight + modeButtonsHeight + 8f;
            const float sliderRowHeight = 30f;
            float headerHeight = GlobeSize + 8f + sliderRowHeight;
            float maxPanelHeight = Mathf.Max(headerHeight + 80f, UI.screenHeight - 80f);
            float visibleContentHeight = Mathf.Min(contentHeight, maxPanelHeight - headerHeight);
            float outerHeight = headerHeight + visibleContentHeight;
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
            var contentRect = new Rect(inner.x, inner.y + headerHeight, inner.width, visibleContentHeight);
            Widgets.BeginScrollView(contentRect, ref contentScroll,
                new Rect(0f, 0f, contentWidth, contentHeight));
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

            // ---- 在飞 shuttle 列表（§7.2）：点击聚焦其口袋图；跳帮 shuttle 提供侵入开关 ----
            if (shuttles.Count > 0)
            {
                foreach (SpaceObject shuttleContainer in shuttles)
                {
                    var rowRect = new Rect(0f, lineY, drawWidth, ShipRowHeight);
                    bool focused = shuttleContainer == frame;
                    if (focused)
                    {
                        Widgets.DrawHighlight(rowRect);
                    }
                    string rowLabel = $"'{shuttleContainer.label}' · shuttle";
                    if (Widgets.ButtonInvisible(rowRect, false))
                    {
                        universe.SetFocused(shuttleContainer);
                    }
                    Text.Font = GameFont.Tiny;
                    GUI.color = focused ? Color.white : new Color(1f, 1f, 1f, 0.75f);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(rowRect.ContractedBy(4f), rowLabel);
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;
                        // 侵入武装开关（canBoard 的在飞 shuttle）；目标指定按钮在下方 shuttle 区块。
                    Building hull = SpaceShuttleService.HullOf(shuttleContainer);
                    if (hull?.TryGetComp<CompSpaceShuttle>()?.CanBoard == true)
                    {
                        var armRect = new Rect(drawWidth - 64f, lineY + 2f, 60f, ShipRowHeight - 4f);
                        bool armed = universe.Shuttles.IntrusionArmed(shuttleContainer);
                        if (armed)
                        {
                            Widgets.DrawHighlight(armRect);
                        }
                        if (Widgets.ButtonInvisible(armRect, false))
                        {
                            universe.Shuttles.ToggleIntrusionArmed(shuttleContainer);
                        }
                        Text.Font = GameFont.Tiny;
                        Text.Anchor = TextAnchor.MiddleCenter;
                        Widgets.Label(armRect, "DeltaV_PanelBoardArm".Translate());
                        Text.Anchor = TextAnchor.UpperLeft;
                        TooltipHandler.TipRegion(armRect, "DeltaV_PanelBoardArmTip".Translate());
                    }
                    lineY += ShipRowHeight;
                }
                lineY += 4f;
            }

            // 模式按钮全部收进面板（不再分开放）。已停靠的船同样提供驾驶（2026-09-24
            // 统一分离定案）：首次推进器出力即触发分离，「脱离停靠」不再是独立按钮。
            if (hasShip)
            {
                if (playerControlled && zeroGravity)
                {
                    string pilotButton = controlState == ShipControlState.Manual && dockingPlan
                        ? "DeltaV_ControlSwitchAutopilot".Translate()
                        : universe.Pilot.IsPlayerInputArmed(selected)
                            ? "DeltaV_PilotButtonExit".Translate()
                            : "DeltaV_PilotButtonEnter".Translate();
                    if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), pilotButton))
                    {
                        universe.Pilot.ToggleControl(universe, selected);
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
                else if (playerControlled && freeFlight)
                {
                    // 自由飞行专属：选择对接（已停靠的船先经出力分离回到自由飞行）。
                    if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), "DeltaV_BeginDockingButton".Translate()))
                    {
                        universe.ShipTransitions.BeginDocking(selected);
                    }
                    lineY += 27f + 8f;
                }
                if (playerControlled && freeFlight && !dockingPlan)
                {
                    // 自由飞行专属：驾驶台朝向罗盘（十字排布四向，一次点击指定主台屏上
                    // 朝向）——整图搬入与当前位置重合的新口袋图并旋转建筑，容器角反调
                    // 使轨道坐标朝向、位置与速度保持不变（RotateShipHeading）。
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(0f, lineY, drawWidth, 20f),
                        "DeltaV_PanelHeadingCaption".Translate());
                    lineY += 20f;
                    const float dirBtnW = 64f;
                    const float dirBtnH = 26f;
                    float centerX = (drawWidth - dirBtnW) * 0.5f;
                    Rot4 currentFacing = selected.console.parent.Rotation;
                    void DirButton(Rot4 facing, Rect rect)
                    {
                        if (facing == currentFacing)
                        {
                            Widgets.DrawHighlight(rect);
                        }
                        if (Widgets.ButtonText(rect, CompassLabel(facing)))
                        {
                            universe.ShipTransitions.RotateShipHeading(universe, selected, facing);
                        }
                    }
                    DirButton(Rot4.North, new Rect(centerX, lineY, dirBtnW, dirBtnH));
                    DirButton(Rot4.West, new Rect(4f, lineY + dirBtnH, dirBtnW, dirBtnH));
                    DirButton(Rot4.East, new Rect(drawWidth - dirBtnW - 4f, lineY + dirBtnH, dirBtnW, dirBtnH));
                    DirButton(Rot4.South, new Rect(centerX, lineY + dirBtnH * 2f, dirBtnW, dirBtnH));
                    TooltipHandler.TipRegion(new Rect(0f, lineY, drawWidth, dirBtnH * 3f),
                        "DeltaV_PanelHeadingTip".Translate());
                    lineY += dirBtnH * 3f + 6f;
                }
            }
            else if (bootstrapEntry && zeroGravity)
            {
                // 首次降落/发射到达：图上唯一的玩家驾驶台——认领并直接进入驾驶
                //（首次出力触发分离；重力图无驾驶入口，只能逆重战略发射）。
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
                    && Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), "DeltaV_PilotButtonEnter".Translate()))
                {
                    CompDeltaVPilotConsole comp = console.TryGetComp<CompDeltaVPilotConsole>();
                    if (!comp.isMain)
                    {
                        comp.MakeMainConsole();
                    }
                    Ship claimed = comp.Ship;
                    if (claimed.IsValid)
                    {
                        universe.Pilot.Enter(universe, claimed);
                    }
                }
                lineY += 27f + 6f;
            }
            else if (multiConsoleAmbiguous && zeroGravity)
            {
                // 多艘逆重飞船共泊：不能任选一台驾驶台，指引选中具体驾驶台后用
                // 「设为主驾驶台」gizmo 认领（该 gizmo 是侧边栏未覆盖的保留项）。
                var pickRect = new Rect(0f, lineY, drawWidth, 27f);
                Text.Font = GameFont.Small;
                GUI.color = new Color(1f, 1f, 1f, 0.4f);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(pickRect, "DeltaV_PilotButtonEnter".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                TooltipHandler.TipRegion(pickRect, "DeltaV_PanelPickConsoleTip".Translate());
                Text.Font = GameFont.Tiny;
                lineY += 27f + 6f;
            }

            // 聚焦在飞 shuttle 口袋图：驾驶切换（PilotTarget 泛化入口，§7.2）。
            if (shuttleControls)
            {
                Building hull = shuttleHull;
                if (hull != null)
                {
                    bool manual = universe.Pilot.GetControlState(PilotTarget.Of(hull)) == ShipControlState.Manual;
                    if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f),
                        manual ? "DeltaV_PilotButtonExit".Translate() : "DeltaV_PilotButtonEnter".Translate()))
                    {
                        universe.Pilot.ToggleControl(universe, PilotTarget.Of(hull));
                    }
                    lineY += 27f + 6f;
                    // 选择对接（在飞、非侵入时可发起；与船按钮同一管线入口，§7.4）。
                    if (shuttleDockButton)
                    {
                        if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f),
                            "DeltaV_BeginDockingButton".Translate()))
                        {
                            universe.ShipTransitions.BeginDockingShuttle(hull);
                        }
                        lineY += 27f + 8f;
                    }
                    // 侵入目标指定（在飞 + canBoard + 武装开启）：唯一入口，点击敌舰外壳。
                    if (shuttleBoardButton)
                    {
                        var boardRect = new Rect(0f, lineY, drawWidth, 27f);
                        if (Widgets.ButtonText(boardRect, "DeltaV_ShuttleBoardGizmo".Translate()))
                        {
                            universe.Shuttles.BeginBoardTargeting(PilotTarget.Of(hull));
                        }
                        TooltipHandler.TipRegion(boardRect, "DeltaV_ShuttleBoardGizmoTip".Translate());
                        lineY += 27f + 8f;
                    }
                }
            }

            // ---- 战斗区块：火控与机动分别控制，炮手岗位独立 ----
            if (combatBlock)
            {
                lineY += 8f;
                if (fireControlButton)
                {
                    string fireLabel = "DeltaV_PanelFireControl".Translate(
                        (universe.Controls.FireControlModeFor(selected) == ShipFireControlMode.Automatic
                            ? "DeltaV_FireControlAutomatic" : "DeltaV_FireControlManual").Translate());
                    if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), fireLabel))
                    {
                        universe.Controls.ToggleFireControl(selected);
                    }
                    TooltipHandler.TipRegion(new Rect(0f, lineY, drawWidth, 27f),
                        "DeltaV_PanelFireControlTip".Translate());
                    lineY += 27f + 6f;
                }
                var maneuverRect = new Rect(0f, lineY, drawWidth, 27f);
                string maneuverLabel = "DeltaV_PanelAutoManeuver".Translate(
                    universe.Controls.AutoManeuverActiveFor(selected)
                        ? "DeltaV_On".Translate() : "DeltaV_Off".Translate());
                if (Widgets.ButtonText(maneuverRect, maneuverLabel))
                    universe.Controls.ToggleAutoManeuver(selected);
                TooltipHandler.TipRegion(maneuverRect, "DeltaV_PanelAutoManeuverTip".Translate());
                lineY += 27f + 6f;
                if (mapHasWeapons)
                {
                    // 两排武器组方块（主/副各一排 1–6；点击切换激活——激活位存主驾驶台）。
                    CompDeltaVPilotConsole consoleComp = selected.console;
                    void GroupRow(SpaceWeaponSalvoSet set, string rowLabel)
                    {
                        var labelRect = new Rect(0f, lineY, 24f, 26f);
                        Text.Font = GameFont.Tiny;
                        GUI.color = new Color(1f, 1f, 1f, 0.7f);
                        Text.Anchor = TextAnchor.MiddleCenter;
                        Widgets.Label(labelRect, rowLabel);
                        Text.Anchor = TextAnchor.UpperLeft;
                        GUI.color = Color.white;
                        int mask = consoleComp.WeaponGroupMask(set);
                        const float square = 30f;
                        for (int group = 1; group <= 6; group++)
                        {
                            var rect = new Rect(26f + (group - 1) * square, lineY, square - 2f, 26f);
                            bool active = (mask & (1 << (group - 1))) != 0;
                            if (active)
                            {
                                Widgets.DrawHighlight(rect);
                            }
                            else
                            {
                                Widgets.DrawBoxSolid(rect, new Color(1f, 1f, 1f, 0.08f));
                            }
                            Text.Font = GameFont.Tiny;
                            Text.Anchor = TextAnchor.MiddleCenter;
                            GUI.color = active ? Color.white : new Color(1f, 1f, 1f, 0.55f);
                            Widgets.Label(rect, group.ToString());
                            Text.Anchor = TextAnchor.UpperLeft;
                            GUI.color = Color.white;
                            if (Widgets.ButtonInvisible(rect, false))
                            {
                                consoleComp.SetWeaponGroupMask(set, active ? mask & ~(1 << (group - 1)) : mask | (1 << (group - 1)));
                            }
                            TooltipHandler.TipRegion(rect, "DeltaV_PanelWeaponGroupTip".Translate(
                                ("DeltaV_WeaponSet" + set).Translate(), group.ToString()));
                        }
                        lineY += 26f + 2f;
                    }
                    GroupRow(SpaceWeaponSalvoSet.Main, "DeltaV_WeaponSetMain".Translate());
                    GroupRow(SpaceWeaponSalvoSet.Secondary, "DeltaV_WeaponSetSecondary".Translate());
                    lineY += 2f;
                    // 进入与退出是两个独立动作；再次进入会按最新预留完整重排。
                    if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f),
                        "DeltaV_PanelBattleStationsOn".Translate()))
                    {
                        universe.Controls.EnterBattleStations(map);
                    }
                    TooltipHandler.TipRegion(new Rect(0f, lineY, drawWidth, 27f),
                        "DeltaV_PanelBattleStationsOnTip".Translate());
                    lineY += 27f + 4f;
                    int stationCount = universe.Weapons.BattleStationGunnerCount(map);
                    if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f),
                        "DeltaV_PanelBattleStationsOff".Translate(stationCount)))
                    {
                        universe.Controls.ExitBattleStations(map);
                    }
                    TooltipHandler.TipRegion(new Rect(0f, lineY, drawWidth, 27f),
                        "DeltaV_PanelBattleStationsOffTip".Translate());
                    lineY += 27f + 6f;
                }
            }

            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
        }
    }
}
