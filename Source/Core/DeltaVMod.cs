using System;
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
        public static DeltaVSettings Settings { get; private set; }

        /// <summary>诊断用：本 mod 的 Harmony 实例（DebugActions 检查补丁挂载状态）。</summary>
        public static Harmony HarmonyInstance { get; private set; }

        public DeltaVMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DeltaVSettings>();
            IncrementalMapGenerationService.FrameBudgetMsProvider =
                () => Settings?.incrementalMapGenBudgetMs ?? 8f;
            MapDormancyService.Logger = message =>
                DeltaVLog.Message(DeltaVLogModule.Lifecycle, message);
            IncrementalMapGenerationService.Logger = message =>
                DeltaVLog.Message(DeltaVLogModule.Lifecycle, message);
            IncrementalMapGenerationService.RegisterAdapterFactory(step =>
                step.def.genStep is RimWorld.GenStep_Space && ModsConfig.OdysseyActive
                    ? new IncrementalMapGenerationService.TerrainFillGenStepAdapter(
                        RimWorld.TerrainDefOf.Space, disableRegions: true)
                    : null);
            IncrementalMapGenerationService.RegisterAdapterFactory(step =>
                step.def.genStep is GenStep_DeltaVShipFill
                    ? new IncrementalMapGenerationService.TerrainFillGenStepAdapter(
                        RimWorld.TerrainDefOf.Space, disableRegions: false)
                    : null);
            InitializeHarmony();
            Log.Message("[DeltaV] Star sector model build loaded (sector-scale setting, projection-based tactical docking, sector content bookkeeping, unified status panel with globe).");
        }

        private static void InitializeHarmony()
        {
            try
            {
                HarmonyInstance = new Harmony("RimExodus.DeltaV");
                HarmonyInstance.PatchAll(typeof(DeltaVMod).Assembly);
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

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);
            list.CheckboxLabeled("DeltaV_SettingsFlightAssist".Translate(), ref Settings.flightAssist,
                "DeltaV_SettingsFlightAssistTip".Translate());
            list.Gap(6f);
            list.TextFieldNumericLabeled("DeltaV_SettingsCellsPerTile".Translate(), ref Settings.cellsPerTile,
                ref cellsPerTileBuffer, 100, 100000);
            ApplyCellsPerTileChangeIfAny();
            list.Gap(6f);
            list.TextFieldNumericLabeled("DeltaV_SettingsUniverseZoomMax".Translate(), ref Settings.universeZoomMax,
                ref universeZoomMaxBuffer, 60f, 5000f);
            list.Gap(6f);
            list.TextFieldNumericLabeled("DeltaV_SettingsPoiActiveRadius".Translate(), ref Settings.poiActiveRadius,
                ref poiActiveBuffer, 50f, 10000f);
            list.Gap(6f);
            list.TextFieldNumericLabeled("DeltaV_SettingsPoiDeleteRadius".Translate(), ref Settings.poiDeleteRadius,
                ref poiDeleteBuffer, 100f, 50000f);
            list.Gap(6f);
            list.TextFieldNumericLabeled("DeltaV_SettingsMapGenBudget".Translate(), ref Settings.incrementalMapGenBudgetMs,
                ref mapGenBudgetBuffer, 1f, 16f);
            list.Gap(6f);
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            list.Label("DeltaV_SettingsCellsPerTileNote".Translate());
            GUI.color = Color.white;

            list.Gap(12f);
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
            list.End();
            Settings.Write();
        }

        // TextFieldNumeric 的文本缓冲需要跨帧保存；值只经本字段修改，无需外部同步。
        private static string cellsPerTileBuffer;

        private static string universeZoomMaxBuffer;

        private static string poiActiveBuffer;

        private static string poiDeleteBuffer;

        private static string mapGenBudgetBuffer;

        /// <summary>上次已应用的尺度值；用于检测设置变化并防止被拒绝的值回写。</summary>
        private static int appliedCellsPerTile;

        private static bool scaleInitialized;

        /// <summary>
        /// 星区架构 §8：尺度调整即时生效。停靠/起飞流程进行中禁止修改（回退值与输入缓冲），
        /// 防止 pending 位姿计算被中途换尺度；主菜单等无宇宙状态下只记录新值，
        /// 读档时由 UniverseManager 按存档尺度差异统一重标定。
        /// </summary>
        private static void ApplyCellsPerTileChangeIfAny()
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
