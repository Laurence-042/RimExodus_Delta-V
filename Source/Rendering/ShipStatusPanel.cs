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
        private const float MaxPanelHeight = 660f;
        private const float MaxCombatHeight = 165f;

        // 每图选中的结构归属或单建筑船；无主台不使结构选择失效。
        private static readonly Dictionary<Map, DriveSubject> selectedShipByMap = new Dictionary<Map, DriveSubject>();

        // 切图时重置滚动位置；有效的结构选择不会被派系默认覆盖。
        private static Map lastDrawnMap;
        private static Vector2 contentScroll;
        private static Vector2 combatScroll;
        private static bool combatExpanded = true;
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
            combatScroll = Vector2.zero;
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
            LandingApproachController.DockingPlan plan = universe.Approach.ActivePlan;
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

            // ---- 内容行（先备好文本，再按真实高度布局：面板高度随内容变化）----
            var statusLines = new List<string>();
            ShipControlState controlState = universe.Pilot.GetControlState(selected);
            SpacePose shipPose = hasShip ? ShipRegistry.ShipPose(selected) : default;
            if (hasShip)
            {
                DriveInfo report = Current.Game?.GetComponent<UniverseManager>()?.FrameOf(selected.Map)?.MotionInfo;
                string controlLabel = controlState == ShipControlState.Autopilot
                    ? "DeltaV_ControlAutopilot".Translate()
                    : controlState == ShipControlState.Manual
                        ? "DeltaV_ControlManual".Translate()
                        : "DeltaV_ControlUncontrolled".Translate();
                statusLines.Add("DeltaV_PanelFlightState".Translate(
                    selected.State == ShipDerivedState.FreeFlight
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
            else if (hasStructure)
            {
                statusLines.Add("DeltaV_PanelNeedsPrimaryConsole".Translate());
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
            float shipListHeight = structures.Count > 1 ? structures.Count * ShipRowHeight + 4f : 0f;

            // 内容高度：星球仪 + 缩放滑条 + 标题/状态行 + 船列表
            // + 当前模式的按钮行数（已停靠的船只有驾驶按钮——首次出力即分离）
            // + 战斗区块：火控、战斗机动、武器组和战斗位置。
            bool mapHasWeapons = SpaceWeaponService.AllWeaponsOn(map).Any(w =>
                w.parent.Faction == Faction.OfPlayer);
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
                selected.CollectCombatPanelActions(mapHasWeapons, combatActionsBefore, combatActionsAfter);
            float combatHeight = 0f;
            if (combatBlock)
            {
                combatHeight = 29f;
                if (combatExpanded)
                {
                    combatBodyHeight = combatActionsBefore.Count * 33f;
                    if (mapHasWeapons)
                        combatBodyHeight += 26f * 2f + 4f;
                    combatBodyHeight += combatActionsAfter.Count * 33f;
                    combatHeight += Mathf.Min(combatBodyHeight, MaxCombatHeight);
                }
            }
            float modeButtonsHeight = panelActions.Count * 33f
                + (headingActions.Count > 0 ? 20f + 26f * 3f + 6f : 0f);
            modeButtonsHeight += combatHeight;
            float contentHeight = 8f + titleHeight + 4f + linesHeight + 8f + shipListHeight
                + modeButtonsHeight + 8f;
            const float sliderRowHeight = 30f;
            float headerHeight = GlobeSize + 8f + sliderRowHeight;
            float maxPanelHeight = Mathf.Max(headerHeight + 80f,
                Mathf.Min(MaxPanelHeight, UI.screenHeight - 80f));
            float visibleContentHeight = Mathf.Min(contentHeight, maxPanelHeight - headerHeight);
            float outerHeight = headerHeight + visibleContentHeight;
            float y = Mathf.Clamp(UI.screenHeight * 0.45f - outerHeight * 0.5f, 48f,
                Mathf.Max(48f, UI.screenHeight - outerHeight - 32f));
            var panelRect = new Rect(8f, y, PanelWidth, outerHeight);
            inputRect = panelRect;
            inputMap = map;
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

            // ---- 结构列表：查看选择不要求结构具有驾驶能力 ----
            if (structures.Count > 1)
            {
                foreach (DriveSubject identity in structures)
                {
                    ShipBase ship = ShipRegistry.ShipByIdentity(identity);
                    bool validShip = ship.IsValid && ship.Map == map;
                    string label = StructureLabel(universe, map, identity);
                    string stateLabel = !validShip ? "DeltaV_StructureNoConsole".Translate()
                        : ship.State == ShipDerivedState.FreeFlight
                        ? "DeltaV_FlightStateFree".Translate()
                        : "DeltaV_FlightStateDocked".Translate();
                    string rowLabel = $"{label} · {stateLabel}";
                    var rowRect = new Rect(0f, lineY, drawWidth, ShipRowHeight);
                    bool isSelected = hasStructure && identity == selectedStructure;
                    if (isSelected)
                    {
                        Widgets.DrawHighlight(rowRect);
                    }
                    if (Widgets.ButtonInvisible(rowRect, false))
                    {
                        SelectStructure(map, identity);
                    }
                    Text.Font = GameFont.Tiny;
                    GUI.color = isSelected ? Color.white : new Color(1f, 1f, 1f, 0.75f);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(rowRect.ContractedBy(4f), rowLabel);
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(rowRect,
                        "DeltaV_PanelShipRowTip".Translate(label));
                    lineY += ShipRowHeight;
                }
                lineY += 4f;
            }

            // 模式按钮全部收进面板（不再分开放）。已停靠的船同样提供驾驶（2026-09-24
            // 统一分离定案）：首次推进器出力即触发分离，「脱离停靠」不再是独立按钮。
            foreach (PilotPanelAction action in panelActions)
            {
                var actionRect = new Rect(0f, lineY, drawWidth, 27f);
                if (Widgets.ButtonText(actionRect, action.Label)) action.Execute();
                if (!action.Tooltip.NullOrEmpty()) TooltipHandler.TipRegion(actionRect, action.Tooltip);
                lineY += 33f;
            }
            if (hasShip)
            {
                if (headingActions.Count > 0)
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
                        if (facing == currentFacing)
                        {
                            Widgets.DrawHighlight(rect);
                        }
                        if (Widgets.ButtonText(rect, CompassLabel(facing)))
                        {
                            headingActions.Find(action => action.Facing == facing).Execute?.Invoke();
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

            // ---- 战斗区块：火控与机动分别控制，炮手岗位独立 ----
            if (combatBlock)
            {
                var combatHeader = new Rect(0f, lineY + 2f, drawWidth, 25f);
                if (Widgets.ButtonText(combatHeader,
                    (combatExpanded ? "▼ " : "▶ ") + "DeltaV_PanelCombatOptions".Translate()))
                    combatExpanded = !combatExpanded;
                lineY += 29f;
                if (combatExpanded)
                {
                    float combatOuterY = lineY;
                    float combatVisibleHeight = Mathf.Min(combatBodyHeight, MaxCombatHeight);
                    Widgets.BeginScrollView(new Rect(0f, lineY, drawWidth, combatVisibleHeight),
                        ref combatScroll, new Rect(0f, 0f, drawWidth - 16f, combatBodyHeight));
                    lineY = 0f;
                    foreach (PilotPanelAction action in combatActionsBefore)
                    {
                        var actionRect = new Rect(0f, lineY, drawWidth - 16f, 27f);
                        if (Widgets.ButtonText(actionRect, action.Label)) action.Execute();
                        if (!action.Tooltip.NullOrEmpty()) TooltipHandler.TipRegion(actionRect, action.Tooltip);
                        lineY += 33f;
                    }
                    if (mapHasWeapons)
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
                            const float square = 27f;
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
                    }
                    foreach (PilotPanelAction action in combatActionsAfter)
                    {
                        var actionRect = new Rect(0f, lineY, drawWidth - 16f, 27f);
                        if (Widgets.ButtonText(actionRect, action.Label)) action.Execute();
                        if (!action.Tooltip.NullOrEmpty()) TooltipHandler.TipRegion(actionRect, action.Tooltip);
                        lineY += 33f;
                    }
                    Widgets.EndScrollView();
                    lineY = combatOuterY + combatVisibleHeight;
                }
            }

            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
        }
    }
}
