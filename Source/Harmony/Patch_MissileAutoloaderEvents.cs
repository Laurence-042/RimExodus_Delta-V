using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>待发位弹种筛选变化，唤醒已链接发射架的补弹请求。</summary>
    [HarmonyPatch]
    internal static class Patch_MissileAutoloaderEvents
    {
        [HarmonyPatch(typeof(CompChangeableProjectile), nameof(CompChangeableProjectile.Notify_SettingsChanged))]
        [HarmonyPostfix]
        private static void LauncherSettingsChanged(CompChangeableProjectile __instance) =>
            __instance.parent.TryGetComp<CompSpaceWeaponMagazine>()?.QueueReload();
    }
}
