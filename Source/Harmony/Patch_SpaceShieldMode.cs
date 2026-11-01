using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>仅目标发生器的太空模式关闭原版圆形同图拦截与圆顶绘制。</summary>
    [HarmonyPatch]
    public static class Patch_SpaceShieldMode
    {
        // ThingDef.ConnectToPower requires the vanilla CompPowerTrader class exactly.
        [HarmonyPatch(typeof(CompPowerTrader), nameof(CompPowerTrader.SetUpPowerVars))]
        [HarmonyPostfix]
        public static void SetUpPowerVars(CompPowerTrader __instance)
        {
            CompSpaceShield shield = __instance.parent?.TryGetComp<CompSpaceShield>();
            if (shield != null) __instance.PowerOutput = -shield.WantedPowerWatts;
        }

        [HarmonyPatch(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.Active), MethodType.Getter)]
        [HarmonyPostfix]
        public static void Active(ref bool __result, CompProjectileInterceptor __instance)
        {
            CompSpaceShield shield = __instance.parent?.TryGetComp<CompSpaceShield>();
            if (shield != null && (!shield.HasGuardCapacity || !shield.FlickedOn))
                __result = false;
        }

        // Native hitPoints=0 leaves geometry, hostility, EMP and impact effects intact,
        // but skips native HP subtraction. Both projectile paths debit the same balances.
        [HarmonyPatch(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.CheckIntercept))]
        [HarmonyPostfix]
        public static void CheckIntercept(CompProjectileInterceptor __instance, Projectile projectile, bool __result)
        {
            if (__result) __instance.parent.TryGetComp<CompSpaceShield>()?.ConsumeGuard(projectile.DamageAmount);
        }

        [HarmonyPatch(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.PostDraw))]
        [HarmonyPrefix]
        public static bool PostDraw(CompProjectileInterceptor __instance)
        {
            CompSpaceShield shield = __instance.parent?.TryGetComp<CompSpaceShield>();
            return shield == null || shield.State == SpaceShieldState.Guard;
        }

        [HarmonyPatch(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.PostDrawExtraSelectionOverlays))]
        [HarmonyPrefix]
        public static bool PostDrawExtraSelectionOverlays(CompProjectileInterceptor __instance)
        {
            CompSpaceShield shield = __instance.parent?.TryGetComp<CompSpaceShield>();
            return shield == null || shield.State == SpaceShieldState.Guard;
        }

        [HarmonyPatch(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.CompGetGizmosExtra))]
        [HarmonyPrefix]
        public static bool CompGetGizmosExtra(CompProjectileInterceptor __instance,
            ref IEnumerable<Gizmo> __result)
        {
            CompSpaceShield shield = __instance.parent?.TryGetComp<CompSpaceShield>();
            if (shield == null)
                return true;
            __result = System.Array.Empty<Gizmo>();
            return false;
        }

        [HarmonyPatch(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.CompInspectStringExtra))]
        [HarmonyPrefix]
        public static bool CompInspectStringExtra(CompProjectileInterceptor __instance, ref string __result)
        {
            CompSpaceShield shield = __instance.parent?.TryGetComp<CompSpaceShield>();
            if (shield == null)
                return true;
            __result = null;
            return false;
        }
    }
}
