using System;
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
