using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Mod 入口。阶段2 起由统一 Harmony 实例挂载驾驶输入、POI 休眠、背景/降落表现及
    /// Colonist Bar 等运行时桥接。补丁初始化失败只记录错误，不杀死主 Mod（AGENTS §17）。
    /// 补丁挂载结果（方法数）属必要日志，常开输出。
    /// </summary>
    public class DeltaVMod : Mod
    {
        public DeltaVSettings Settings { get; private set; }

        public static DeltaVSettings CurrentSettings =>
            LoadedModManager.GetMod<DeltaVMod>()?.Settings;

        /// <summary>诊断用：本 mod 的 Harmony 实例（DebugActions 检查补丁挂载状态）。</summary>
        public Harmony HarmonyInstance { get; private set; }

        public DeltaVMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DeltaVSettings>();
            Settings.EnsureSectorRulesSeeded();
            InitializeHarmony();
            Log.Message("[DeltaV] Star sector model build loaded (sector-scale setting, projection-based tactical docking, sector content bookkeeping, unified status panel with globe).");
        }

        private void InitializeHarmony()
        {
            try
            {
                HarmonyInstance = new Harmony("RimExodus.DeltaV");
                HarmonyInstance.PatchAll(typeof(DeltaVMod).Assembly);
                ShellDrawCapture.Install(HarmonyInstance);
                int patched = HarmonyInstance.GetPatchedMethods().Count();
                if (patched == 0)
                {
                    Log.Error("[DeltaV] Harmony PatchAll reported 0 patched methods - runtime integrations will NOT work. Check patch annotations.");
                }
                else
                {
                    Log.Message($"[DeltaV] Harmony patches applied to {patched} method(s): " +
                        string.Join(", ", HarmonyInstance.GetPatchedMethods().Select(m => m.DeclaringType.Name + "." + m.Name).OrderBy(n => n)) +
                        " (runtime integration patches expected).");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] Harmony patch init failed; vanilla key suppression and POI dormancy will not work: {ex}");
            }
        }

        /// <summary>设置页 tab（顺序即显示顺序）。</summary>
        private enum SettingsTab
        {
            General,
            Docking,
            Physics,
            World,
            Generation,
            Logging
        }

        private SettingsTab settingsTab = SettingsTab.General;

        public override void DoSettingsWindowContents(Rect inRect)
        {
            // tab 悬挂在内容面板上沿（原版 TabDrawer 语义：tab 画在传入矩形上方 32px）。
            var tabRect = new Rect(inRect) { y = inRect.y + 40f };
            var mainRect = new Rect(inRect) { height = inRect.height - 40f, y = inRect.y + 40f };
            Widgets.DrawMenuSection(mainRect);
            var tabs = new List<TabRecord>
            {
                new TabRecord("DeltaV_SettingsTabGeneral".Translate(),
                    () => settingsTab = SettingsTab.General, settingsTab == SettingsTab.General),
                new TabRecord("DeltaV_SettingsTabDocking".Translate(),
                    () => settingsTab = SettingsTab.Docking, settingsTab == SettingsTab.Docking),
                new TabRecord("DeltaV_SettingsTabPhysics".Translate(),
                    () => settingsTab = SettingsTab.Physics, settingsTab == SettingsTab.Physics),
                new TabRecord("DeltaV_SettingsTabWorld".Translate(),
                    () => settingsTab = SettingsTab.World, settingsTab == SettingsTab.World),
                new TabRecord("DeltaV_SettingsTabGeneration".Translate(),
                    () => settingsTab = SettingsTab.Generation, settingsTab == SettingsTab.Generation),
                new TabRecord("DeltaV_SettingsTabLogging".Translate(),
                    () => settingsTab = SettingsTab.Logging, settingsTab == SettingsTab.Logging),
            };
            TabDrawer.DrawTabs(tabRect, tabs);
            Rect content = mainRect.ContractedBy(15f);
            switch (settingsTab)
            {
                case SettingsTab.General:
                    DrawGeneralTab(content);
                    break;
                case SettingsTab.Docking:
                    DrawDockingTab(content);
                    break;
                case SettingsTab.Physics:
                    DrawPhysicsTab(content);
                    break;
                case SettingsTab.World:
                    DrawWorldTab(content);
                    break;
                case SettingsTab.Generation:
                    SectorSettingsUI.Draw(content, Settings);
                    break;
                case SettingsTab.Logging:
                    DrawLoggingTab(content);
                    break;
            }
            // 原版 Dialog_ModSettings.PreClose 调用 Mod.WriteSettings；绘制事件不写盘。
        }

        private void DrawGeneralTab(Rect rect)
        {
            var list = new Listing_Standard();
            list.Begin(rect);
            list.CheckboxLabeled("DeltaV_SettingsFlightAssist".Translate(), ref Settings.flightAssist,
                "DeltaV_SettingsFlightAssistTip".Translate());
            list.Gap(6f);
            TippedRow(list, rect, "DeltaV_SettingsUniverseZoomMaxTip",
                () => list.TextFieldNumericLabeled("DeltaV_SettingsUniverseZoomMax".Translate(), ref Settings.universeZoomMax,
                    ref universeZoomMaxBuffer, 60f, 5000f));
            list.End();
        }

        private void DrawDockingTab(Rect rect)
        {
            var list = new Listing_Standard();
            list.Begin(rect);
            TippedRow(list, rect, "DeltaV_SettingsDockingHoldTicksTip",
                () => list.TextFieldNumericLabeled("DeltaV_SettingsDockingHoldTicks".Translate(), ref Settings.dockingHoldTicks,
                    ref dockingHoldTicksBuffer, 1, 600));
            TippedRow(list, rect, "DeltaV_SettingsDockingPosWindowTip",
                () => list.TextFieldNumericLabeled("DeltaV_SettingsDockingPosWindow".Translate(), ref Settings.dockingPositionWindow,
                    ref dockingPosWindowBuffer, 0.05f, 5f));
            TippedRow(list, rect, "DeltaV_SettingsDockingAngleWindowTip",
                () => list.TextFieldNumericLabeled("DeltaV_SettingsDockingAngleWindow".Translate(), ref Settings.dockingAngleWindowDeg,
                    ref dockingAngleWindowBuffer, 0.1f, 15f));
            TippedRow(list, rect, "DeltaV_SettingsDockingCancelDistanceTip",
                () => list.TextFieldNumericLabeled("DeltaV_SettingsDockingCancelDistance".Translate(), ref Settings.dockingCancelDistance,
                    ref dockingCancelDistanceBuffer, 50f, 10000f));
            list.End();
        }

        private void DrawPhysicsTab(Rect rect)
        {
            // 被动阻力（解耦架构 §11.1）：开关 + 平移/角速度半衰期（tick）；即时生效——
            // 实际积分与 MPC 预测同一因子，参数变化同时反映到两者。
            var list = new Listing_Standard();
            list.Begin(rect);
            list.CheckboxLabeled("DeltaV_SettingsPassiveDrag".Translate(), ref Settings.passiveDrag,
                "DeltaV_SettingsPassiveDragTip".Translate());
            TippedRow(list, rect, "DeltaV_SettingsDragLinearHalfLifeTip",
                () => list.TextFieldNumericLabeled("DeltaV_SettingsDragLinearHalfLife".Translate(),
                    ref Settings.dragLinearHalfLifeTicks, ref dragLinearHalfLifeBuffer, 1, 60000));
            TippedRow(list, rect, "DeltaV_SettingsDragAngularHalfLifeTip",
                () => list.TextFieldNumericLabeled("DeltaV_SettingsDragAngularHalfLife".Translate(),
                    ref Settings.dragAngularHalfLifeTicks, ref dragAngularHalfLifeBuffer, 1, 60000));
            // 真空传声是物理问题——太空战命中音效开关归物理页（2026-09-30 用户决策）。
            list.Gap(6f);
            list.CheckboxLabeled("DeltaV_SettingsSpaceImpactSound".Translate(), ref Settings.spaceImpactSound,
                "DeltaV_SettingsSpaceImpactSoundTip".Translate());
            list.End();
        }

        private void DrawWorldTab(Rect rect)
        {
            var list = new Listing_Standard();
            list.Begin(rect);
            TippedRow(list, rect, "DeltaV_SettingsCellsPerTileTip",
                () => list.TextFieldNumericLabeled("DeltaV_SettingsCellsPerTile".Translate(), ref Settings.cellsPerTile,
                    ref cellsPerTileBuffer, 100, 100000));
            ApplyCellsPerTileChangeIfAny();
            list.Gap(6f);
            TippedRow(list, rect, "DeltaV_SettingsMapGenBudgetTip",
                () => list.TextFieldNumericLabeled("DeltaV_SettingsMapGenBudget".Translate(), ref Settings.incrementalMapGenBudgetMs,
                    ref mapGenBudgetBuffer, 1f, 16f));
            list.Gap(6f);
            TippedRow(list, rect, "DeltaV_SettingsPoiActiveRadiusTip",
                () => list.TextFieldNumericLabeled("DeltaV_SettingsPoiActiveRadius".Translate(), ref Settings.poiActiveRadius,
                    ref poiActiveBuffer, 50f, 10000f));
            list.Gap(6f);
            TippedRow(list, rect, "DeltaV_SettingsPoiDeleteRadiusTip",
                () => list.TextFieldNumericLabeled("DeltaV_SettingsPoiDeleteRadius".Translate(), ref Settings.poiDeleteRadius,
                    ref poiDeleteBuffer, 100f, 50000f));
            list.Gap(6f);
            TippedRow(list, rect, "DeltaV_SettingsNpcDecisionIntervalTip",
                () => list.TextFieldNumericLabeled("DeltaV_SettingsNpcDecisionInterval".Translate(),
                    ref Settings.npcDecisionIntervalTicks, ref npcDecisionIntervalBuffer, 1, 600));
            TippedRow(list, rect, "DeltaV_SettingsLifecycleScanIntervalTip",
                () => list.TextFieldNumericLabeled("DeltaV_SettingsLifecycleScanInterval".Translate(),
                    ref Settings.lifecycleScanIntervalTicks, ref lifecycleScanIntervalBuffer, 1, 600));
            list.End();
        }

        private void DrawLoggingTab(Rect rect)
        {
            var list = new Listing_Standard();
            list.Begin(rect);
            Text.Font = GameFont.Small;
            list.Label("DeltaV_SettingsLogSection".Translate());
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            list.Label("DeltaV_SettingsLogNote".Translate());
            GUI.color = Color.white;
            list.CheckboxLabeled("DeltaV_SettingsLogCore".Translate(), ref Settings.logCore);
            list.CheckboxLabeled("DeltaV_SettingsLogInput".Translate(), ref Settings.logInput);
            list.CheckboxLabeled("DeltaV_SettingsLogPhysics".Translate(), ref Settings.logPhysics);
            list.CheckboxLabeled("DeltaV_SettingsLogHarmony".Translate(), ref Settings.logHarmony);
            list.CheckboxLabeled("DeltaV_SettingsLogLifecycle".Translate(), ref Settings.logLifecycle);
            list.CheckboxLabeled("DeltaV_SettingsLogRender".Translate(), ref Settings.logRender);
            list.CheckboxLabeled("DeltaV_SettingsLogCombat".Translate(), ref Settings.logCombat);
            list.End();
        }

        /// <summary>
        /// 数值行挂悬停说明。TextFieldNumericLabeled 返回 void，拿不到行矩形，
        /// 这里用 CurHeight 前后差拼出该行区域；矩形是 listing 组内局部坐标，
        /// 与原版 SliderLabeled 内 TipRegion 的用法一致。
        /// </summary>
        private static void TippedRow(Listing_Standard list, Rect containingRect, string tipKey, Action drawRow)
        {
            float y = list.CurHeight;
            drawRow();
            TooltipHandler.TipRegion(new Rect(0f, y, containingRect.width, list.CurHeight - y),
                tipKey.Translate());
        }

        // TextFieldNumeric 的文本缓冲需要跨帧保存；值只经本字段修改，无需外部同步。
        private string cellsPerTileBuffer;

        private string universeZoomMaxBuffer;

        private string poiActiveBuffer;

        private string poiDeleteBuffer;
        private string npcDecisionIntervalBuffer;
        private string lifecycleScanIntervalBuffer;

        private string mapGenBudgetBuffer;

        private string dockingHoldTicksBuffer;

        private string dockingPosWindowBuffer;

        private string dockingAngleWindowBuffer;

        private string dockingCancelDistanceBuffer;

        private string dragLinearHalfLifeBuffer;

        private string dragAngularHalfLifeBuffer;

        /// <summary>上次已应用的尺度值；用于检测设置变化并防止被拒绝的值回写。</summary>
        private int appliedCellsPerTile;

        private bool scaleInitialized;

        /// <summary>
        /// 星区架构 §8：尺度调整即时生效。停靠/起飞流程进行中禁止修改（回退值与输入缓冲），
        /// 防止 pending 位姿计算被中途换尺度；主菜单等无宇宙状态下只记录新值，
        /// 读档时由 UniverseManager 按存档尺度差异统一重标定。
        /// </summary>
        private void ApplyCellsPerTileChangeIfAny()
        {
            if (!scaleInitialized)
            {
                appliedCellsPerTile = Settings.cellsPerTile;
                scaleInitialized = true;
                return;
            }
            if (Settings.cellsPerTile == appliedCellsPerTile) return;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null)
            {
                // 无进行中的游戏（主菜单改设置）：位姿重标定推迟到读档（UniverseManager.LoadedGame）。
                appliedCellsPerTile = Settings.cellsPerTile;
                return;
            }
            if (!universe.CanApplyScaleChange())
            {
                Settings.cellsPerTile = appliedCellsPerTile;
                cellsPerTileBuffer = appliedCellsPerTile.ToString();
                Messages.Message("DeltaV_ScaleChangeBlocked".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            universe.ApplyCellsPerTileScale(appliedCellsPerTile, Settings.cellsPerTile);
            appliedCellsPerTile = Settings.cellsPerTile;
        }

        public override string SettingsCategory()
        {
            return "RimExodus: Delta-V";
        }
    }
}
