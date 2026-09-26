using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 右下角 PlaySettings 开关行追加（原版「开关屋顶区显示」同款机制，2026-09-26
    /// 用户需求）：追加「显示推进器位置与方向以及船体范围」开关（聚焦图叠加，
    /// ThrusterDebugOverlay）。仅地图视图（worldView = false）显示。
    /// </summary>
    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    public static class Patch_DeltaVPlaySettings_Thrusters
    {
        public static void Postfix(WidgetRow row, bool worldView)
        {
            if (worldView) return;
            row.ToggleableIcon(ref ThrusterDebugOverlay.Show, TexCommand.SquadAttack,
                "DeltaV_DebugThrustersToggleButton".Translate(), SoundDefOf.Mouseover_ButtonToggle);
        }
    }

}
