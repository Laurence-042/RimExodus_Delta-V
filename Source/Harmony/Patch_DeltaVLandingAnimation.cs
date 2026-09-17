using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(GravshipRenderer), nameof(GravshipRenderer.BeginCutscene))]
    public static class Patch_GravshipRenderer_BeginCutscene
    {
        public static void Postfix(GravshipRenderer __instance)
        {
            DockController.SuppressMobileLaunchAnimation();
            if (DeltaVLandingAnimator.Active) DeltaVLandingAnimator.BeginDynamicTiming(__instance);
        }
    }

    [HarmonyPatch(typeof(GravshipRenderer), nameof(GravshipRenderer.UpdateLanding))]
    public static class Patch_GravshipRenderer_UpdateLanding
    {
        public static bool Prefix(GravshipRenderer __instance, float progress, bool isPollutedLanding)
        {
            return !DeltaVLandingAnimator.Draw(__instance, progress, isPollutedLanding);
        }
    }

    [HarmonyPatch(typeof(WorldComponent_GravshipController), "PanIf")]
    public static class Patch_GravshipController_PanIf
    {
        public static bool Prefix(PanCompletionCallback onComplete)
        {
            if (!DeltaVLandingAnimator.Active && !DockController.MobileLaunchPending) return true;
            onComplete?.Invoke();
            return false;
        }
    }

    [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.SetRootSize))]
    public static class Patch_CameraDriver_SetRootSize_Gravship
    {
        public static bool Prefix()
        {
            return !DeltaVLandingAnimator.Active;
        }
    }

    [HarmonyPatch(typeof(WorldComponent_GravshipController), "LandingEnded")]
    public static class Patch_GravshipController_LandingEnded
    {
        public static void Prefix(WorldComponent_GravshipController __instance)
        {
            DeltaVLandingAnimator.EnsureLaunchInfoBeforeLandingEnds(__instance);
        }

        public static void Postfix()
        {
            DockController.NotifyVanillaLandingEnded();
            DeltaVLandingAnimator.ResetTiming();
        }
    }

    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.WorldComponentUpdate))]
    public static class Patch_GravshipController_WorldComponentUpdate_DeltaVTiming
    {
        public static void Prefix(WorldComponent_GravshipController __instance)
        {
            DockController.BeforeGravshipControllerUpdate(__instance);
            DeltaVLandingAnimator.BeforeControllerUpdate(__instance);
        }

        public static void Postfix()
        {
            DeltaVLandingAnimator.AfterControllerUpdate();
        }
    }
}
