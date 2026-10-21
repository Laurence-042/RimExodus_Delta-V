using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 右下角 PlaySettings 开关行追加（原版「开关屋顶区显示」同款机制，2026-09-26
    /// 用户需求）：推进器/船体、武器射界与碰撞各有独立视图开关。
    /// 仅地图视图（worldView = false）显示。
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
            row.ToggleableIcon(ref ThrusterDebugOverlay.Show, TexCommand.SquadAttack,
                "DeltaV_DebugThrustersToggleButton".Translate(), SoundDefOf.Mouseover_ButtonToggle);
            bool showWeaponArcs = WeaponArcOverlay.Show;
            row.ToggleableIcon(ref showWeaponArcs, TexCommand.FireAtWill,
                "DeltaV_DebugWeaponArcsToggleButton".Translate(), SoundDefOf.Mouseover_ButtonToggle);
            WeaponArcOverlay.SetEnabled(showWeaponArcs);
            row.ToggleableIcon(ref CollisionDebugOverlay.Show, TexCommand.Attack,
                "DeltaV_DebugCollisionToggleButton".Translate(), SoundDefOf.Mouseover_ButtonToggle);
        }
    }

}
