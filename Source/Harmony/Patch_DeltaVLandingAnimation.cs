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

    /// <summary>
    /// Delta-V 战术降落的目标本来就是当前可见的 SpaceObject 内部图。原版 terrain
    /// curtain 会用降落前截图覆盖真实地图，并随 cinematic 做缩放/混合，表现为背景
    /// 扭曲；这里仅在 Delta-V 自定义降落期间不绘制 curtain，让已经放入当前图的
    /// 建筑与 wreck 直接由正常 Map renderer 显示。原版战略降落完全不受影响。
    /// </summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), "DrawTerrainCurtain")]
    public static class Patch_GravshipController_DrawTerrainCurtain
    {
        public static bool Prefix()
        {
            return !DeltaVLandingAnimator.Active;
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
