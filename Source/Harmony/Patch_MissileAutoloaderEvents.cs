using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>原版储物格与弹种筛选变化，唤醒已链接发射架的补弹请求。</summary>
    [HarmonyPatch]
    internal static class Patch_MissileAutoloaderEvents
    {
        [HarmonyPatch(typeof(Building_Storage), nameof(Building_Storage.Notify_ReceivedThing))]
        [HarmonyPostfix]
        private static void StockArrived(Building_Storage __instance) =>
            __instance.TryGetComp<CompMissileMagazineFacility>()?.NotifyStockChanged();

        [HarmonyPatch(typeof(Building_Storage), nameof(Building_Storage.Notify_LostThing))]
        [HarmonyPostfix]
        private static void StockLeft(Building_Storage __instance) =>
            __instance.TryGetComp<CompMissileMagazineFacility>()?.NotifyStockChanged();

        [HarmonyPatch(typeof(Building_Storage), nameof(Building_Storage.Notify_SettingsChanged))]
        [HarmonyPostfix]
        private static void MagazineSettingsChanged(Building_Storage __instance) =>
            __instance.TryGetComp<CompMissileMagazineFacility>()?.NotifyStockChanged();

        [HarmonyPatch(typeof(CompChangeableProjectile), nameof(CompChangeableProjectile.Notify_SettingsChanged))]
        [HarmonyPostfix]
        private static void LauncherSettingsChanged(CompChangeableProjectile __instance) =>
            __instance.parent.TryGetComp<CompMissileAutoloader>()?.QueueReload();
    }

    // Vanilla's facility adjacency includes corner-touching buildings. Keep the facility
    // link and its placement preview aligned with the launcher's declared two sockets.
    [HarmonyPatch(typeof(CompAffectedByFacilities),
        nameof(CompAffectedByFacilities.CanPotentiallyLinkTo_Static),
        new Type[] { typeof(ThingDef), typeof(IntVec3), typeof(Rot4),
            typeof(ThingDef), typeof(IntVec3), typeof(Rot4), typeof(Map) })]
    internal static class Patch_MissileFacilityGeometry
    {
        [HarmonyPostfix]
        private static void Postfix(ThingDef facilityDef, IntVec3 facilityPos, Rot4 facilityRot,
            ThingDef myDef, IntVec3 myPos, Rot4 myRot, ref bool __result)
        {
            if (__result && myDef?.GetCompProperties<CompProperties_MissileAutoloader>() != null)
                __result = SpaceMissileMagazineGeometry.Fits(myDef, myPos, myRot,
                    facilityDef, facilityPos, facilityRot);
        }
    }
}
