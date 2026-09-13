using HarmonyLib;
using RimWorld;

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
}
