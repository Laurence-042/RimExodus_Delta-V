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
    /// 星区地图即星球仪连续缩放（§12.2 定稿）：距离预设/滚轮拉近逐层显示星区网格、对象
    /// 图标与距离标注，图标可悬停/选择导航目标，展开地图提供地点列表。显示条件：聚焦图属 Delta-V 语境
    /// （FrameOf != null：口袋容器图 / 停靠宿主图 / 轨道图 / POI 内部）；
    /// 世界视图不叠加（剪影是其自己的显示通道，且可点击聚焦）。
    /// </summary>
    public static class ShipStatusPanel
    {
        private const float PanelWidth = 304f;
        private const float GlobeSize = 210f;
        private const float MaxPanelHeight = 660f;

        // 每图选中的结构归属或单建筑船；无主台不使结构选择失效。
        private static readonly Dictionary<Map, DriveSubject> selectedShipByMap = new Dictionary<Map, DriveSubject>();

        // 切图时重置滚动位置；有效的结构选择不会被派系默认覆盖。
        private static Map lastDrawnMap;
        private static Vector2 contentScroll;
        private static bool advancedFlightExpanded;
        private static bool advancedCombatExpanded;
        private static Rect inputRect;
        private static Map inputMap;
        internal static bool ContainsScreenPoint(Vector2 point) => inputMap == Find.CurrentMap
            && inputMap != null && WorldRendererUtility.DrawingMap && inputRect.Contains(point);

        public static void Forget(Map map)
        {
            if (map == null) return;
            selectedShipByMap.Remove(map);
            if (lastDrawnMap == map) lastDrawnMap = null;
            if (inputMap == map) inputMap = null;
        }

        public static void ClearRuntimeState()
        {
            selectedShipByMap.Clear();
            lastDrawnMap = null;
            contentScroll = Vector2.zero;
            advancedFlightExpanded = false;
            advancedCombatExpanded = false;
            inputMap = null;
        }

        /// <summary>面板当前选中的船（驾驶切换键等多船入口共用；无选择返回 false）。</summary>
        public static bool TryGetSelectedShip(Map map, out ShipBase ship)
        {
            ship = ShipBase.Invalid;
            if (!TryGetSelectedStructure(map, out DriveSubject ownerId)) return false;
            ShipBase resolved = ShipRegistry.ShipByIdentity(ownerId);
            if (!resolved.IsValid || resolved.Map != map) return false;
            ship = resolved;
            return true;
        }

        internal static bool TryGetSelectedStructure(Map map, out DriveSubject identity)
        {
            identity = default;
            if (map == null || map.Disposed) return false;
            if (selectedShipByMap.TryGetValue(map, out identity) && ShipRegistry.HasStructure(map, identity))
                return true;
            selectedShipByMap.Remove(map);
            ShipBase preferred = ShipRegistry.DefaultVesselOnMap(map);
            if (preferred.IsValid && ShipRegistry.HasStructure(map, preferred.Identity)) identity = preferred.Identity;
            else
            {
                var structures = ShipRegistry.StructuresOnMap(map).ToList();
                if (structures.Count == 0) return false;
                identity = structures.OrderByDescending(id => ShipRegistry.StructureCells(map, id).Count())
                    .ThenBy(id => id.key).First();
            }
            selectedShipByMap[map] = identity;
            return true;
        }

        internal static void SelectStructure(Map map, DriveSubject identity)
        {
            if (map != null && ShipRegistry.HasStructure(map, identity))
                selectedShipByMap[map] = identity;
        }

        private static string StructureLabel(UniverseManager universe, Map map, DriveSubject identity)
        {
            ShipBase ship = ShipRegistry.ShipByIdentity(identity);
            return ship.IsValid && ship.Map == map ? ship.Label : universe.LabelForOwner(identity.key);
        }

        /// <summary>外部写入口（点击选中物聚焦归属船，Patch_SelectorShipFocus）：只改面板选中状态。</summary>
        public static void SelectShip(ShipBase ship)
        {
            if (ship.IsValid && ship.Map != null && !ship.Map.Disposed)
            {
                SelectStructure(ship.Map, ship.Identity);
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
            inputMap = null;
            if (Find.WindowStack.IsOpen<Window_SectorNavigation>()) return;
            Map map = Find.CurrentMap;
            if (universe == null || map == null || !WorldRendererUtility.DrawingMap) return;
            SpaceObject frame = universe.FrameOf(map);
            if (frame == null) return;
            if (map != lastDrawnMap)
            {
                lastDrawnMap = map;
                contentScroll = Vector2.zero;
            }

            // 结构列表同时包含有主台船、无主台归属与单建筑船。
            List<DriveSubject> structures = ShipRegistry.StructuresOnMap(map).ToList();
            bool hasStructure = TryGetSelectedStructure(map, out DriveSubject selectedStructure);
            TryGetSelectedShip(map, out ShipBase selected);
            LandingApproachController.DriftSyncPlan plan = universe.Approach.ActivePlan;
            bool dockingPlan = plan != null && plan.Target.Identity == selected.Identity;
            bool hasShip = selected.IsValid;
            bool playerControlled = !hasShip || universe.Pilot.CanPlayerControl(selected);
            // 驾驶/认领入口只服务零重力图（重力图只能逆重战略发射）。
            bool zeroGravity = FreeFlightBootstrap.IsZeroGravity(universe, map);

            string title;
            if (hasShip)
            {
                title = "DeltaV_ShipPanelTitle".Translate(selected.Label);
            }
            else
            {
                title = "DeltaV_PanelSectorTitle".Translate(hasStructure
                    ? StructureLabel(universe, map, selectedStructure) : frame.label);
            }
            string titleTip = title;

            // ---- 内容行（先备好文本，再按真实高度布局：面板高度随内容变化）----
            var statusLines = new List<string>();
            var compactStats = new List<KeyValuePair<string, string>>();
            string compactState = null;
            ShipControlState controlState = universe.Pilot.GetControlState(selected);
            SpacePose shipPose = hasStructure ? ShipRegistry.StructurePose(map, selectedStructure) : frame.pose;
            Vector2 focusPoint = hasStructure
                ? (hasShip ? selected.HullCenter
                    : SpaceTransform.BoundsCenterOfCells(ShipRegistry.StructureCells(map, selectedStructure)))
                : SpaceTransform.ShipLocalOrigin(map);
            Vector3 cameraCenter = Find.CameraDriver.CurrentViewRect.CenterVector3;
            bool showRefocus = Vector2.Distance(new Vector2(cameraCenter.x, cameraCenter.z), focusPoint)
                > Mathf.Max(20f, Find.CameraDriver.RootSize * 0.6f);
            if (hasShip)
            {
                DriveInfo report = Current.Game?.GetComponent<UniverseManager>()?.FrameOf(selected.Map)?.MotionInfo;
                string flightState = (selected.State == ShipDerivedState.FreeFlight
                    ? "DeltaV_FlightStateFree" : "DeltaV_FlightStateDocked").Translate();
                string compactControl = controlState == ShipControlState.Manual
                    ? "DeltaV_ControlCompactManual".Translate()
                    : controlState == ShipControlState.Autopilot
                        ? "DeltaV_ControlCompactAutopilot".Translate()
                        : "DeltaV_ControlCompactUncontrolled".Translate();
                compactState = flightState + " · " + compactControl;
                bool brakeHeld = DeltaVDefOf.DeltaV_AssistBrake != null && DeltaVDefOf.DeltaV_AssistBrake.IsDown;
                string assist = brakeHeld ? "DeltaV_Braking".Translate()
                    : (DeltaVMod.CurrentSettings.flightAssist ? "DeltaV_On" : "DeltaV_Off").Translate();
                compactStats.Add(new KeyValuePair<string, string>("DeltaV_PanelStatSpeed".Translate(),
                    "DeltaV_PanelStatSpeedValue".Translate(shipPose.velocity.magnitude.ToString("F1"))));
                compactStats.Add(new KeyValuePair<string, string>("DeltaV_PanelStatHeading".Translate(),
                    "DeltaV_PanelStatHeadingValue".Translate((shipPose.angle * Mathf.Rad2Deg).ToString("F0"))));
                compactStats.Add(new KeyValuePair<string, string>("DeltaV_PanelStatTurnRate".Translate(),
                    "DeltaV_PanelStatTurnRateValue".Translate((shipPose.angularVelocity * Mathf.Rad2Deg).ToString("F1"))));
                compactStats.Add(new KeyValuePair<string, string>("DeltaV_PanelStatAssist".Translate(), assist));
                string detailedControl = universe.Controls.PlayerFor(selected)?.FlightStatusLabel
                    ?? (controlState == ShipControlState.Autopilot
                        ? "DeltaV_ControlAutopilot".Translate()
                        : "DeltaV_ControlUncontrolled".Translate());
                titleTip += "\n" + flightState + " · " + detailedControl
                    + "\n" + "DeltaV_HudMass".Translate(report == null ? "?" : ((int)report.mass).ToString());
            }
            else if (hasStructure)
            {
                statusLines.Add("DeltaV_PanelNeedsPrimaryConsole".Translate());
            }
            if (!hasShip)
            {
                statusLines.Add($"{"DeltaV_HudSpeed".Translate(shipPose.velocity.magnitude.ToString("F1"))} · "
                    + $"{"DeltaV_HudHeading".Translate((shipPose.angle * Mathf.Rad2Deg).ToString("F0"))} · "
                    + "DeltaV_HudTurnRate".Translate((shipPose.angularVelocity * Mathf.Rad2Deg).ToString("F1")));
            }
            if (dockingPlan)
            {
                statusLines.Add("DeltaV_DockPlanStatus".Translate(
                    plan.host.label, ((int)plan.Distance()).ToString()));
                if (universe.Approach.Ready) statusLines.Add("DeltaV_SyncReady".Translate());
            }

            Text.Font = GameFont.Small;
            // 留出滚动条宽度；有无滚动时文字换行和武器组位置保持一致。
            float contentWidth = PanelWidth - 36f;
            const float titleHeight = 27f;
            const float statRowHeight = 36f;
            int statRows = (compactStats.Count + 2) / 3;
            float statsHeight = statRows > 0 ? statRows * statRowHeight + 7f : 0f;
            Text.Font = GameFont.Tiny;
            float linesHeight = 0f;
            foreach (string line in statusLines)
            {
                linesHeight += Mathf.Max(20f, Text.CalcHeight(line, contentWidth));
            }
            const float shipListHeight = 0f;

            // 内容高度：星球仪 + 范围/导航控件 + 标题/状态行 + 船列表
            // + 当前模式的按钮行数（已停靠的船只有驾驶按钮——首次出力即分离）
            // + 战斗区块：武装、火控、战斗机动、武器组和 shuttle 侵入。
            bool shipHasWeapons = hasShip && selected.Weapons.Any();
            bool combatBlock = hasShip && playerControlled && zeroGravity;
            var combatActionsBefore = new List<PilotPanelAction>();
            var combatActionsAfter = new List<PilotPanelAction>();
            float combatBodyHeight = 0f;
            var panelActions = new List<PilotPanelAction>();
            var headingActions = new List<HeadingPanelAction>();
            if (zeroGravity)
            {
                if (hasShip)
                {
                    selected.CollectPanelActions(panelActions);
                    selected.CollectHeadingPanelActions(headingActions);
                }
            }
            if (combatBlock)
                selected.CollectCombatPanelActions(shipHasWeapons, combatActionsBefore, combatActionsAfter);
            if (universe.Scatter.PlayerPawns.Any())
                panelActions.Add(new PilotPanelAction("DeltaV_LocateScatteredPawns".Translate(),
                    () => universe.Scatter.ShowPlayerPawns(map)));
            float combatHeight = 0f;
            if (combatBlock)
            {
                // Common combat controls stay visible. Only strategy editing and temporary
                // engagement orders live in the bottom advanced foldout.
                combatBodyHeight = combatActionsBefore.Count * 33f;
                if (shipHasWeapons)
                {
                    combatBodyHeight += universe.Controls.WeaponsArmed(selected) ? 33f * 2f : 33f;
                    combatBodyHeight += 26f * 2f + 4f;
                }
                if (combatActionsAfter.Count > 0)
                    combatBodyHeight += 29f + (advancedCombatExpanded ? combatActionsAfter.Count * 33f : 0f);
                combatHeight = 29f + combatBodyHeight;
            }
            bool hasAdvancedFlight = showRefocus || headingActions.Count > 0;
            float advancedFlightBodyHeight = (showRefocus ? 33f : 0f)
                + (headingActions.Count > 0 ? 20f + 26f * 3f + 6f : 0f);
            int panelRows = panelActions.Count == 0 ? 0 : 1 + Mathf.Max(0, panelActions.Count - 2);
            float modeButtonsHeight = panelRows * 33f
                + (hasAdvancedFlight ? 29f + (advancedFlightExpanded ? advancedFlightBodyHeight : 0f) : 0f);
            modeButtonsHeight += combatHeight;
            float contentHeight = 8f + titleHeight + 4f + statsHeight + linesHeight + 8f + shipListHeight
                + modeButtonsHeight + 16f;
            const float sliderRowHeight = GlobeNavigationPanel.CompactHeight;
            float globeHeight = Mathf.Min(GlobeSize, Mathf.Max(120f,
                UI.screenHeight - GlobeNavigationPanel.CompactHeight - 184f));
            float headerHeight = globeHeight + sliderRowHeight;
            float maxPanelHeight = Mathf.Max(headerHeight + 80f,
                Mathf.Min(MaxPanelHeight, UI.screenHeight - 80f));
            float visibleContentHeight = Mathf.Min(contentHeight, maxPanelHeight - headerHeight);
            float outerHeight = headerHeight + visibleContentHeight;
            float y = Mathf.Clamp(UI.screenHeight * 0.45f - outerHeight * 0.5f, 48f,
                Mathf.Max(48f, UI.screenHeight - outerHeight - 32f));
            var panelRect = new Rect(8f, y, PanelWidth, outerHeight);
            inputRect = panelRect;
            inputMap = map;
            DeltaVPanelStyle.DrawPanel(panelRect);
            var inner = panelRect.ContractedBy(8f);

            var globeRect = new Rect(inner.x, inner.y, inner.width, globeHeight);
            PlanetGlobeRenderer.Draw(globeRect, universe, frame);
            GlobeNavigationPanel.Draw(new Rect(inner.x, inner.y + globeHeight,
                inner.width, GlobeNavigationPanel.CompactHeight), globeRect, universe, frame, false);

            // 文本与按钮区（相对坐标）。
            var contentRect = new Rect(inner.x, inner.y + headerHeight, inner.width, visibleContentHeight);
            Widgets.BeginScrollView(contentRect, ref contentScroll,
                new Rect(0f, 0f, contentWidth, contentHeight));
            float lineY = 8f;
            float drawWidth = contentWidth;

            float badgeWidth = 0f;
            DeltaVPanelStyle.DrawSurface(new Rect(0f, lineY, drawWidth, titleHeight), raised: true);
            if (!compactState.NullOrEmpty())
            {
                Text.Font = GameFont.Tiny;
                badgeWidth = Mathf.Min(drawWidth * .48f, Text.CalcSize(compactState).x + 12f);
                var badgeRect = new Rect(drawWidth - badgeWidth, lineY, badgeWidth, titleHeight);
                Text.Anchor = TextAnchor.MiddleRight;
                GUI.color = DeltaVPanelStyle.Muted;
                Widgets.Label(badgeRect.ContractedBy(4f),
                    PlanetGlobeRenderer.ShortText(compactState, badgeRect.width - 8f));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                TooltipHandler.TipRegion(badgeRect, titleTip);
            }
            float titleWidth = drawWidth - (badgeWidth > 0f ? badgeWidth + 8f : 0f);
            Text.Font = GameFont.Small;
            float desiredTitleWidth = Text.CalcSize(title + (structures.Count > 1 ? "  ▾" : "")).x + 20f;
            float compactTitleWidth = Mathf.Min(titleWidth, Mathf.Max(88f,
                Mathf.Min(drawWidth * .52f, desiredTitleWidth)));
            var titleRect = new Rect(0f, lineY, compactTitleWidth, titleHeight);
            if (structures.Count > 1)
            {
                if (DeltaVPanelStyle.Button(titleRect, title + "  ▾", DeltaVButtonTone.Quiet,
                    font: GameFont.Small))
                {
                    var options = new List<FloatMenuOption>();
                    foreach (DriveSubject identity in structures)
                    {
                        DriveSubject choice = identity;
                        ShipBase ship = ShipRegistry.ShipByIdentity(identity);
                        bool validShip = ship.IsValid && ship.Map == map;
                        string stateLabel = !validShip ? "DeltaV_StructureNoConsole".Translate()
                            : ship.State == ShipDerivedState.FreeFlight
                            ? "DeltaV_FlightStateFree".Translate()
                            : "DeltaV_FlightStateDocked".Translate();
                        options.Add(new FloatMenuOption($"{StructureLabel(universe, map, identity)} · {stateLabel}",
                            () => SelectStructure(map, choice)));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }
            else
            {
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(titleRect, title);
                Text.Anchor = TextAnchor.UpperLeft;
            }
            TooltipHandler.TipRegion(titleRect, titleTip);
            lineY += titleHeight + 4f;

            if (compactStats.Count > 0)
            {
                DeltaVPanelStyle.DrawSurface(new Rect(0f, lineY - 1f, drawWidth,
                    statRows * statRowHeight + 2f));
                float statWidth = (drawWidth - 8f) / 3f;
                for (int i = 0; i < compactStats.Count; i++)
                {
                    int row = i / 3, column = i % 3;
                    float statX = column * (statWidth + 4f);
                    float statY = lineY + row * statRowHeight;
                    Text.Font = GameFont.Tiny;
                    GUI.color = DeltaVPanelStyle.Muted;
                    Widgets.Label(new Rect(statX, statY, statWidth, 15f), compactStats[i].Key);
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                    Widgets.Label(new Rect(statX, statY + 14f, statWidth, 20f), compactStats[i].Value);
                }
                lineY += statRows * statRowHeight;
                Widgets.DrawLineHorizontal(0f, lineY + 1f, drawWidth);
                lineY += 7f;
            }
            Text.Font = GameFont.Tiny;
            foreach (string line in statusLines)
            {
                float h = Mathf.Max(20f, Text.CalcHeight(line, drawWidth));
                Widgets.Label(new Rect(0f, lineY, drawWidth, h), line);
                lineY += h;
            }
            lineY += 8f;

            // 模式按钮全部收进面板（不再分开放）。已停靠的船同样提供驾驶（2026-09-24
            // 统一分离定案）：首次推进器出力即触发分离，「脱离停靠」不再是独立按钮。
            if (panelActions.Count > 0)
            {
                int paired = Mathf.Min(2, panelActions.Count);
                float buttonWidth = paired == 1 ? drawWidth : (drawWidth - 4f) * .5f;
                for (int i = 0; i < paired; i++)
                {
                    PilotPanelAction action = panelActions[i];
                    var actionRect = new Rect(i * (buttonWidth + 4f), lineY, buttonWidth, 27f);
                    if (DeltaVPanelStyle.Button(actionRect, action.Label,
                        action.Danger ? DeltaVButtonTone.Danger : DeltaVButtonTone.Neutral,
                        selected: action.Selected, enabled: action.Enabled, font: GameFont.Small))
                        action.Execute();
                    if (!action.Tooltip.NullOrEmpty()) TooltipHandler.TipRegion(actionRect, action.Tooltip);
                }
                lineY += 33f;
                // EVA is a common flight operation and stays directly accessible. Only camera
                // refocus and the large heading compass are folded below it.
                for (int i = 2; i < panelActions.Count; i++)
                {
                    PilotPanelAction action = panelActions[i];
                    var actionRect = new Rect(0f, lineY, drawWidth, 27f);
                    if (DeltaVPanelStyle.Button(actionRect, action.Label,
                        action.Danger ? DeltaVButtonTone.Danger : DeltaVButtonTone.Neutral,
                        selected: action.Selected, enabled: action.Enabled, font: GameFont.Small))
                        action.Execute();
                    if (!action.Tooltip.NullOrEmpty()) TooltipHandler.TipRegion(actionRect, action.Tooltip);
                    lineY += 33f;
                }
            }
            if (hasAdvancedFlight)
            {
                var advancedFlightHeader = new Rect(0f, lineY + 2f, drawWidth, 25f);
                if (DeltaVPanelStyle.Button(advancedFlightHeader,
                    (advancedFlightExpanded ? "▼ " : "▶ ")
                        + "DeltaV_PanelAdvancedFlightOptions".Translate(), DeltaVButtonTone.Quiet))
                    advancedFlightExpanded = !advancedFlightExpanded;
                lineY += 29f;
            }
            if (hasAdvancedFlight && advancedFlightExpanded)
            {
                if (showRefocus)
                {
                    if (DeltaVPanelStyle.Button(new Rect(0f, lineY, drawWidth, 27f),
                        "DeltaV_PanelRefocus".Translate()))
                    {
                        var cell = new IntVec3(Mathf.FloorToInt(focusPoint.x), 0,
                            Mathf.FloorToInt(focusPoint.y)).ClampInsideMap(map);
                        CameraJumper.TryJump(cell, map, CameraJumper.MovementMode.Cut);
                    }
                    lineY += 33f;
                }
                if (hasShip && headingActions.Count > 0)
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
                    Rot4 currentFacing = selected.Anchor.Rotation;
                    void DirButton(Rot4 facing, Rect rect)
                    {
                        if (DeltaVPanelStyle.Button(rect, CompassLabel(facing),
                            selected: facing == currentFacing))
                            headingActions.Find(action => action.Facing == facing).Execute?.Invoke();
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

            // ---- 战斗区块：常用控制常驻，策略与临时命令收进底部高级选项 ----
            if (combatBlock)
            {
                Widgets.DrawLineHorizontal(0f, lineY + 1f, drawWidth);
                DeltaVPanelStyle.DrawSurface(new Rect(0f, lineY + 3f, drawWidth, 24f));
                var combatTitle = new Rect(2f, lineY + 4f, drawWidth * .5f, 22f);
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(combatTitle, "DeltaV_PanelCombat".Translate());
                if (shipHasWeapons)
                {
                    bool weaponsArmed = universe.Controls.WeaponsArmed(selected);
                    string armedState = weaponsArmed
                        ? "DeltaV_PanelWeaponsArmedState".Translate()
                        : "DeltaV_PanelWeaponsDisarmedState".Translate();
                    var stateRect = new Rect(drawWidth * .5f, lineY + 4f, drawWidth * .5f - 2f, 22f);
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleRight;
                    GUI.color = weaponsArmed ? DeltaVPanelStyle.AccentBorder : DeltaVPanelStyle.Muted;
                    Widgets.Label(stateRect, armedState);
                    GUI.color = Color.white;
                }
                Text.Anchor = TextAnchor.UpperLeft;
                lineY += 29f;
                if (shipHasWeapons)
                {
                    bool armed = universe.Controls.WeaponsArmed(selected);
                    if (!armed)
                    {
                        var armRect = new Rect(0f, lineY, drawWidth, 27f);
                        if (DeltaVPanelStyle.Button(armRect, "DeltaV_PanelWeaponsArm".Translate(),
                            DeltaVButtonTone.Primary, font: GameFont.Small))
                            universe.Controls.ArmWeapons(selected);
                        TooltipHandler.TipRegion(armRect, "DeltaV_PanelWeaponsArmTip".Translate());
                        lineY += 33f;
                    }
                    else
                    {
                        float half = (drawWidth - 4f) * .5f;
                        var reassignRect = new Rect(0f, lineY, half, 27f);
                        bool canReassign = selected.HasGunnerStations;
                        if (DeltaVPanelStyle.Button(reassignRect, "DeltaV_PanelGunnersReassign".Translate(),
                            enabled: canReassign, font: GameFont.Small))
                            universe.Controls.ReassignGunners(selected);
                        TooltipHandler.TipRegion(reassignRect, "DeltaV_PanelGunnersReassignTip".Translate());
                        var disarmRect = new Rect(half + 4f, lineY, half, 27f);
                        if (DeltaVPanelStyle.Button(disarmRect, "DeltaV_PanelWeaponsDisarm".Translate(),
                            DeltaVButtonTone.Danger, font: GameFont.Small))
                            universe.Controls.DisarmWeapons(selected);
                        TooltipHandler.TipRegion(disarmRect, "DeltaV_PanelWeaponsDisarmTip".Translate());
                        lineY += 33f;

                        ShipFireControlMode currentMode = universe.Controls.FireControlModeFor(selected);
                        ShipFireControlMode[] modes = { ShipFireControlMode.Automatic,
                            ShipFireControlMode.Manual, ShipFireControlMode.Disabled };
                        float modeWidth = (drawWidth - 8f) / 3f;
                        for (int i = 0; i < modes.Length; i++)
                        {
                            ShipFireControlMode mode = modes[i];
                            var modeRect = new Rect(i * (modeWidth + 4f), lineY, modeWidth, 27f);
                            bool supported = selected.SupportsFireControlMode(mode);
                            if (DeltaVPanelStyle.Button(modeRect, ("DeltaV_FireControl" + mode).Translate(),
                                selected: mode == currentMode, enabled: supported, font: GameFont.Small))
                                universe.Controls.SetFireControlMode(selected, mode);
                            TooltipHandler.TipRegion(modeRect, "DeltaV_PanelFireControlTip".Translate());
                        }
                        lineY += 33f;
                    }
                }
                if (shipHasWeapons)
                {
                    // 两排武器组方块（主/副各一排 1–6；点击切换激活——激活位存主驾驶台）。
                    ShipCombatState consoleComp = selected.Combat;
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
                        float square = (drawWidth - 26f) / 6f;
                        for (int group = 1; group <= 6; group++)
                        {
                            var rect = new Rect(26f + (group - 1) * square, lineY, square - 2f, 26f);
                            bool active = (mask & (1 << (group - 1))) != 0;
                            if (DeltaVPanelStyle.Button(rect, group.ToString(), selected: active))
                                consoleComp.SetWeaponGroupMask(set,
                                    active ? mask & ~(1 << (group - 1)) : mask | (1 << (group - 1)));
                            TooltipHandler.TipRegion(rect, "DeltaV_PanelWeaponGroupTip".Translate(
                                ("DeltaV_WeaponSet" + set).Translate(), group.ToString()));
                        }
                        lineY += 28f;
                    }
                    GroupRow(SpaceWeaponSalvoSet.Main, "DeltaV_WeaponSetMain".Translate());
                    GroupRow(SpaceWeaponSalvoSet.Secondary, "DeltaV_WeaponSetSecondary".Translate());
                    lineY += 2f;
                }
                foreach (PilotPanelAction action in combatActionsBefore)
                {
                    var actionRect = new Rect(0f, lineY, drawWidth, 27f);
                    if (DeltaVPanelStyle.Button(actionRect, action.Label, enabled: action.Enabled,
                        font: GameFont.Small)) action.Execute();
                    if (!action.Tooltip.NullOrEmpty()) TooltipHandler.TipRegion(actionRect, action.Tooltip);
                    lineY += 33f;
                }
                if (combatActionsAfter.Count > 0)
                {
                    var advancedHeader = new Rect(0f, lineY + 2f, drawWidth, 25f);
                    if (DeltaVPanelStyle.Button(advancedHeader,
                        (advancedCombatExpanded ? "▼ " : "▶ ")
                            + "DeltaV_PanelAdvancedCombatOptions".Translate(), DeltaVButtonTone.Quiet))
                        advancedCombatExpanded = !advancedCombatExpanded;
                    lineY += 29f;
                    if (advancedCombatExpanded)
                    {
                        foreach (PilotPanelAction action in combatActionsAfter)
                        {
                            var actionRect = new Rect(0f, lineY, drawWidth, 27f);
                            if (DeltaVPanelStyle.Button(actionRect, action.Label, enabled: action.Enabled,
                                font: GameFont.Small))
                                action.Execute();
                            if (!action.Tooltip.NullOrEmpty()) TooltipHandler.TipRegion(actionRect, action.Tooltip);
                            lineY += 33f;
                        }
                    }
                }
            }

            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
        }
    }
}
