using HarmonyLib;
using RimWorld;
using RimWorld.Planet;

namespace DeltaV
{
    [HarmonyPatch(typeof(GravshipLandingMarker), nameof(GravshipLandingMarker.BeginLanding))]
    public static class Patch_GravshipLanding
    {
        public static void Prefix(GravshipLandingMarker __instance)
        {
            DockController.NotifyVanillaLandingBegun(__instance);
        }
    }

    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.TravelTo))]
    public static class Patch_GravshipUtility_TravelTo_DeltaVMobile
    {
        public static bool Prefix(Gravship gravship)
        {
            return !DockController.TryCompleteMobileLaunch(gravship);
        }
    }
}
