using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Mod 入口。阶段2 起初始化 Harmony：唯一补丁是驾驶模式的原版按键抑制
    /// （Source/Harmony/Patch_KeyBindingSuppression.cs）。补丁初始化失败只记录错误，
    /// 不杀死主 Mod（AGENTS §17）。补丁挂载结果（方法数）属必要日志，常开输出。
    /// </summary>
    public class DeltaVMod : Mod
    {
        public static DeltaVSettings Settings { get; private set; }

        /// <summary>诊断用：本 mod 的 Harmony 实例（DebugActions 检查补丁挂载状态）。</summary>
        public static Harmony HarmonyInstance { get; private set; }

        public DeltaVMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DeltaVSettings>();
            InitializeHarmony();
            Log.Message("[DeltaV] Phase 3 prototype loaded (POI lifecycle, dynamic anchor, pilot console, fixed orbit).");
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
                    Log.Error("[DeltaV] Harmony PatchAll reported 0 patched methods - key suppression and POI dormancy will NOT work. Check patch annotations.");
                }
                else
                {
                    Log.Message($"[DeltaV] Harmony patches applied to {patched} method(s): " +
                        string.Join(", ", HarmonyInstance.GetPatchedMethods().Select(m => m.DeclaringType.Name + "." + m.Name).OrderBy(n => n)) +
                        " (KeyBindingDef getters + Map tick/update + CurrentMap setter + Storyteller targets expected).");
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
            list.TextFieldNumericLabeled("DeltaV_SettingsHostMapSize".Translate(), ref Settings.hostMapSize,
                ref hostMapSizeBuffer, 100, 1000);
            list.Gap(6f);
            list.TextFieldNumericLabeled("DeltaV_SettingsUniverseZoomMax".Translate(), ref Settings.universeZoomMax,
                ref universeZoomMaxBuffer, 60f, 5000f);
            list.Gap(6f);
            list.TextFieldNumericLabeled("DeltaV_SettingsOriginShiftThreshold".Translate(), ref Settings.originShiftThreshold,
                ref originShiftBuffer, 100f, 100000f);
            list.Gap(6f);
            list.TextFieldNumericLabeled("DeltaV_SettingsPoiActiveRadius".Translate(), ref Settings.poiActiveRadius,
                ref poiActiveBuffer, 50f, 10000f);
            list.Gap(6f);
            list.TextFieldNumericLabeled("DeltaV_SettingsPoiDeleteRadius".Translate(), ref Settings.poiDeleteRadius,
                ref poiDeleteBuffer, 100f, 50000f);
            list.Gap(6f);
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            list.Label("DeltaV_SettingsHostMapSizeNote".Translate());
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
        private static string hostMapSizeBuffer;

        private static string universeZoomMaxBuffer;

        private static string originShiftBuffer;

        private static string poiActiveBuffer;

        private static string poiDeleteBuffer;

        public override string SettingsCategory()
        {
            return "RimExodus: Delta-V";
        }
    }
}
