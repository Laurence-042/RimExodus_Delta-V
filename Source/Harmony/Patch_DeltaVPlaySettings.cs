using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 右下角 PlaySettings 开关行追加（原版「开关屋顶区显示」同款机制，2026-09-26
    /// 用户需求）：推进器/船体、武器射界与碰撞各有独立视图开关。
    /// 仅地图视图（worldView = false）显示；状态由游戏组件按原版 Scribe 流程保存。
    /// </summary>
    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    public static class Patch_DeltaVPlaySettings_Thrusters
    {
        public static void Postfix(WidgetRow row, bool worldView)
        {
            if (worldView) return;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe != null)
            {
                bool soft = universe.SoftDock.Enabled;
                row.ToggleableIcon(ref soft, TexCommand.Install, "DeltaV_SoftDockToggle".Translate(), SoundDefOf.Mouseover_ButtonToggle);
                universe.SoftDock.SetEnabled(soft);
            }
            if (universe == null) return;
            row.ToggleableIcon(ref universe.ConfirmSplitWarnings, TexButton.Info,
                "DeltaV_SplitWarningToggle".Translate(), SoundDefOf.Mouseover_ButtonToggle);
            if (Find.CurrentMap != null && universe.FrameOf(Find.CurrentMap) != null)
                row.ToggleableIcon(ref universe.ShowCoordinateGrid, TexButton.ShowTerrainAffordanceOverlay,
                    "DeltaV_CoordinateGridToggle".Translate(), SoundDefOf.Mouseover_ButtonToggle);
            row.ToggleableIcon(ref universe.ShowThrusterDebugOverlay, TexCommand.SquadAttack,
                "DeltaV_DebugThrustersToggleButton".Translate(), SoundDefOf.Mouseover_ButtonToggle);
            bool showWeaponArcs = universe.ShowWeaponArcOverlay;
            row.ToggleableIcon(ref showWeaponArcs, TexCommand.FireAtWill,
                "DeltaV_DebugWeaponArcsToggleButton".Translate(), SoundDefOf.Mouseover_ButtonToggle);
            if (universe.ShowWeaponArcOverlay && !showWeaponArcs) WeaponArcOverlay.ClearRuntimeCaches();
            universe.ShowWeaponArcOverlay = showWeaponArcs;
            row.ToggleableIcon(ref universe.ShowCollisionDebugOverlay, TexCommand.Attack,
                "DeltaV_DebugCollisionToggleButton".Translate(), SoundDefOf.Mouseover_ButtonToggle);
        }
    }

}
