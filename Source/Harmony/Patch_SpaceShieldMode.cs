using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>Delta-V 容量门禁与盾模式适配；守卫绘制走联合轮廓，原版发生器保持不变。</summary>
    [HarmonyPatch]
    public static class Patch_SpaceShieldMode
    {
        // ThingDef.ConnectToPower requires the vanilla CompPowerTrader class exactly.
        [HarmonyPatch(typeof(CompPowerTrader), nameof(CompPowerTrader.SetUpPowerVars))]
        [HarmonyPostfix]
        public static void SetUpPowerVars(CompPowerTrader __instance)
        {
            CompSpaceShield shield = __instance.parent?.TryGetComp<CompSpaceShield>();
            if (shield != null) { __instance.PowerOutput = -shield.WantedPowerWatts; return; }
            var weapon = CompSpaceWeapon.ForComponent(__instance);
            var segment = __instance.parent?.TryGetComp<CompSpaceRailgun>();
            if (weapon != null || segment != null)
                Current.Game?.GetComponent<UniverseManager>()?.Weapons
                    .ApplyPowerRequest(__instance, weapon ?? segment.BoundWeapon);
        }

        [HarmonyPatch(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.Active), MethodType.Getter)]
        [HarmonyPostfix]
        public static void Active(ref bool __result, CompProjectileInterceptor __instance)
        {
            CompSpaceShield shield = __instance.parent?.TryGetComp<CompSpaceShield>();
            if (shield != null && (!shield.HasGuardCapacity || !shield.FlickedOn))
                __result = false;
        }

        [HarmonyPatch(typeof(CompRefuelable), nameof(CompRefuelable.ConsumeFuel), new[] { typeof(float) })]
        [HarmonyPostfix]
        public static void ConsumeFuel(CompRefuelable __instance)
        {
            if (__instance.parent?.TryGetComp<CompSpaceShield>() is { } shield)
                Current.Game?.GetComponent<UniverseManager>()?.NotifyShield(shield);
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
            shield?.UpdateFieldColor();
            if (shield == null) return true;
            GuardShieldRenderer.Draw(shield, __instance);
            return false;
        }

        [HarmonyPatch(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.PostDrawExtraSelectionOverlays))]
        [HarmonyPrefix]
        public static bool PostDrawExtraSelectionOverlays(CompProjectileInterceptor __instance)
        {
            CompSpaceShield shield = __instance.parent?.TryGetComp<CompSpaceShield>();
            return shield == null || shield.State == SpaceShieldState.Guard;
        }

        [HarmonyPatch(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.CompGetGizmosExtra))]
        [HarmonyPostfix]
        public static void CompGetGizmosExtra(CompProjectileInterceptor __instance,
            ref IEnumerable<Gizmo> __result)
        {
            CompSpaceShield shield = __instance.parent?.TryGetComp<CompSpaceShield>();
            if (shield != null) __result = WithoutNativeCapacity(__result);
        }

        private static IEnumerable<Gizmo> WithoutNativeCapacity(IEnumerable<Gizmo> gizmos)
        {
            foreach (Gizmo gizmo in gizmos)
                if (!(gizmo is Gizmo_ProjectileInterceptorHitPoints)) yield return gizmo;
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
