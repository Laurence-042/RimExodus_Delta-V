using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 左侧信息面板（星区架构 §11.1）：固定模式、驾驶模式等按钮全部收进的**单一**面板，
    /// 高度完全随内容自适应（不滚动）；内嵌**可缩放星球仪**（PlanetGlobeRenderer）——
    /// 星区地图即星球仪连续缩放（§12.2 定稿）：滑条/滚轮拉近逐层显示星区网格、对象
    /// 图标与距离标注，图标可悬停/点击聚焦。显示条件：聚焦图属 Delta-V 语境
    /// （FrameOf != null：移动船口袋图 / 停靠船宿主图 / 轨道图 / POI 内部）；
    /// 世界视图不叠加（剪影是其自己的显示通道，且可点击聚焦）。
    /// </summary>
    public static class ShipStatusPanel
    {
        private const float PanelWidth = 248f;
        private const float GlobeSize = 212f;

        private static readonly List<Building> EmptyConsoles = new List<Building>();

        // 驾驶台扫描缓存（60 tick 重扫）：PlayerConsoles 遍历全图 listerThings，
        // 面板每帧取用，不能每帧全扫。
        private static Map consoleScanMap;
        private static int consoleScanTick = -99999;
        private static List<Building> consoleScanResult = EmptyConsoles;

        private static List<Building> ConsolesOn(Map map)
        {
            if (consoleScanMap != map || Find.TickManager.TicksGame - consoleScanTick >= 60)
            {
                consoleScanMap = map;
                consoleScanTick = Find.TickManager.TicksGame;
                consoleScanResult = MobileShipBootstrap.PlayerConsoles(map);
            }
            return consoleScanResult;
        }

        public static void Draw(UniverseManager universe)
        {
            Map map = Find.CurrentMap;
            if (universe == null || map == null || !WorldRendererUtility.DrawingMap) return;
            SpaceObject frame = universe.FrameOf(map);
            if (frame == null) return;

            ShipState mobileShip = universe.ShipOnMap(map);
            if (mobileShip != null && mobileShip.docked) mobileShip = null;
            // 「对接计划」语义唯一来源是 ActivePlan（null = 无计划或到达重放期，非空 ⇒
            // 全字段有效）；不从 ship.docking 等标志推断（选点中的船 docking 但无计划）。
            LandingApproachController.DockingPlan plan = LandingApproachController.ActivePlan;
            if (mobileShip != null && mobileShip.docking && plan?.ship != mobileShip)
            {
                mobileShip = null; // 选点/到达重放中的船：不显示移动 HUD
            }
            ShipState dockedShip = mobileShip == null
                ? universe.Ships.FirstOrDefault(s => s.docked && !s.docking && s.dockedMap == map)
                : null;
            // 计划 UI（计划船聚焦时）：状态行 + 自动驾驶开关 + 取消。
            bool dockingPlan = plan != null && plan.ship == mobileShip;
            // 控制权门禁（阶段4）：驾驶/模式按钮限玩家方（dev 放行）。
            bool playerControlled = mobileShip == null || PilotController.CanPlayerControl(mobileShip);

            // 固定模式 → 移动模式入口的另一形态：未 bootstrap 的原版 Gravship（首次降落/
            // 发射到达时还没有 ShipState）。唯一驾驶台可直接从面板进入；多艘船必须选中
            // 具体驾驶台（按其连接的引擎与 ValidSubstructure 捕获准确船体）。
            List<Building> consoles = dockedShip != null || mobileShip != null ? EmptyConsoles : ConsolesOn(map);
            bool bootstrapEntry = consoles.Count == 1;
            bool multiShipAmbiguous = consoles.Count > 1;

            string title;
            if (mobileShip != null)
            {
                title = "DeltaV_MobileHudTitle".Translate(mobileShip.label);
            }
            else if (dockedShip != null)
            {
                title = "DeltaV_PanelDockedTitle".Translate(dockedShip.label);
            }
            else if (bootstrapEntry || multiShipAmbiguous)
            {
                title = "DeltaV_PanelFixedTitle".Translate();
            }
            else
            {
                title = "DeltaV_PanelSectorTitle".Translate(frame.label);
            }

            // ---- 内容行（先备好文本，再按真实高度布局：面板高度随内容变化）----
            bool piloting = mobileShip != null && universe.pilotShipId == mobileShip.objectId;
            var statusLines = new System.Collections.Generic.List<string>();
            if (mobileShip != null)
            {
                ShipPhysics.MassReport report = ShipPhysics.GetReport(mobileShip);
                statusLines.Add("DeltaV_HudControl".Translate(
                    piloting ? "DeltaV_ControlPiloting".Translate() : "DeltaV_ControlInactive".Translate()));
                statusLines.Add("DeltaV_HudSpeed".Translate(mobileShip.pose.velocity.magnitude.ToString("F1")));
                statusLines.Add("DeltaV_HudHeading".Translate(
                    (ShipOrientation.HeadingAngle(mobileShip) * Mathf.Rad2Deg).ToString("F0")));
                statusLines.Add("DeltaV_HudTurnRate".Translate(
                    (mobileShip.pose.angularVelocity * Mathf.Rad2Deg).ToString("F1")));
                statusLines.Add("DeltaV_HudMass".Translate(report == null ? "?" : ((int)report.mass).ToString()));
                bool brakeHeld = DeltaVDefOf.DeltaV_AssistBrake != null && DeltaVDefOf.DeltaV_AssistBrake.IsDown;
                statusLines.Add("DeltaV_HudAssist".Translate(
                    brakeHeld
                        ? "DeltaV_Braking".Translate()
                        : (DeltaVMod.Settings.flightAssist
                            ? "DeltaV_On".Translate()
                            : "DeltaV_Off".Translate())));
            }
            else
            {
                statusLines.Add("DeltaV_PanelModeFixed".Translate());
            }
            if (dockingPlan)
            {
                statusLines.Add("DeltaV_DockPlanStatus".Translate(
                    plan.host.label, ((int)plan.Distance()).ToString()));
                statusLines.Add(LandingApproachController.AutopilotEngaged
                    ? "DeltaV_DockPlanAutopilotOn".Translate()
                    : "DeltaV_DockPlanAutopilotOff".Translate());
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

            // 高度完全随内容自适应（不滚动）：星球仪 + 缩放滑条 + 标题/状态行 + 当前模式的按钮行数。
            float modeButtonsHeight = mobileShip != null
                ? (dockingPlan
                    ? (playerControlled ? 27f + 6f : 0f) + 27f + 6f + 27f + 8f
                    : (playerControlled ? 27f + 6f + 27f + 8f : 0f))
                : (dockedShip != null || bootstrapEntry || multiShipAmbiguous) ? 27f + 6f : 0f;
            float contentHeight = 8f + titleHeight + 4f + linesHeight + 8f + modeButtonsHeight;
            const float sliderRowHeight = 30f;
            float outerHeight = GlobeSize + 8f + sliderRowHeight + contentHeight;
            float y = Mathf.Clamp(UI.screenHeight * 0.45f - outerHeight * 0.5f, 48f,
                Mathf.Max(48f, UI.screenHeight - outerHeight - 32f));
            var panelRect = new Rect(8f, y, PanelWidth, outerHeight);
            Widgets.DrawMenuSection(panelRect);
            var inner = panelRect.ContractedBy(8f);

            // 星球仪固定在面板顶部（GL 绘制使用绝对 GUI 屏幕坐标）；
            // 无任何飞船时（首次到达、尚未 bootstrap）以当前图帧位置为相机锚点。
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

            // 模式按钮全部收进面板（不再分开放）。
            if (mobileShip != null)
            {
                if (playerControlled)
                {
                    string pilotButton = piloting
                        ? "DeltaV_PilotButtonExit".Translate()
                        : "DeltaV_PilotButtonEnter".Translate();
                    if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), pilotButton))
                    {
                        PilotController.Toggle(universe);
                    }
                    lineY += 27f + 6f;
                }
                if (dockingPlan)
                {
                    // 计划期：自动驾驶开关（手操打断后可重新挂上）+ 取消对接。
                    string autopilotButton = LandingApproachController.AutopilotEngaged
                        ? "DeltaV_DockAutopilotStopButton".Translate()
                        : "DeltaV_DockAutopilotResumeButton".Translate();
                    if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), autopilotButton))
                    {
                        if (LandingApproachController.AutopilotEngaged)
                        {
                            ShipAutopilot.Cancel(mobileShip);
                        }
                        else
                        {
                            LandingApproachController.EngageAutopilot();
                        }
                    }
                    lineY += 27f + 6f;
                    if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), "DeltaV_DockCancelButton".Translate()))
                    {
                        LandingApproachController.CancelSelectionOrPlan("user");
                    }
                    lineY += 27f + 8f;
                }
                else if (playerControlled)
                {
                    if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), "DeltaV_FixedButtonEnter".Translate()))
                    {
                        DockController.TryDock(universe, mobileShip);
                    }
                    lineY += 27f + 8f;
                }
                else
                {
                    lineY += 8f;
                }
            }
            else if (dockedShip != null)
            {
                if (PilotController.CanPlayerControl(dockedShip)
                    && Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), "DeltaV_MobileButtonEnter".Translate()))
                {
                    DockController.TryLaunch(universe, dockedShip);
                }
                lineY += 27f + 6f;
            }
            else if (bootstrapEntry)
            {
                // 首次降落/发射到达：图上唯一的玩家驾驶台，直接 bootstrap 进入移动模式。
                if (Widgets.ButtonText(new Rect(0f, lineY, drawWidth, 27f), "DeltaV_MobileButtonEnter".Translate()))
                {
                    MobileShipBootstrap.TryEnter(universe, consoles[0]);
                }
                lineY += 27f + 6f;
            }
            else if (multiShipAmbiguous)
            {
                // 多艘逆重飞船共泊：不能任选一台驾驶台，指引到具体驾驶台的 gizmo。
                var pickRect = new Rect(0f, lineY, drawWidth, 27f);
                Text.Font = GameFont.Small;
                GUI.color = new Color(1f, 1f, 1f, 0.4f);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(pickRect, "DeltaV_MobileButtonEnter".Translate());
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
