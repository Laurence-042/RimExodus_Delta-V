using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using UnityEngine;

namespace DeltaV
{
    internal static class AutoSpaceWeaponTurretPatchTargets
    {
        internal static IEnumerable<MethodBase> DeclaredMethods(string name)
        {
            MethodInfo root = AccessTools.DeclaredMethod(typeof(Building_TurretGun), name);
            if (root != null) yield return root;
            foreach (System.Type type in typeof(Building_TurretGun).AllSubclasses())
            {
                MethodInfo method = AccessTools.DeclaredMethod(type, name);
                if (method != null) yield return method;
            }
        }

        internal static IEnumerable<MethodBase> DeclaredGetters(string name)
        {
            MethodInfo root = AccessTools.DeclaredPropertyGetter(typeof(Building_TurretGun), name);
            if (root != null) yield return root;
            foreach (System.Type type in typeof(Building_TurretGun).AllSubclasses())
            {
                MethodInfo method = AccessTools.DeclaredPropertyGetter(type, name);
                if (method != null) yield return method;
            }
        }
    }

    // RimWorld 1.6 TurretTop.DrawTurret already accepts a visual recoil offset.
    [HarmonyPatch(typeof(TurretTop), nameof(TurretTop.DrawTurret))]
    public static class Patch_TurretTop_SpaceWeaponRecoil
    {
        public static bool Prefix(Building_Turret ___parentTurret, Vector3 drawLoc,
            ref Vector3 recoilDrawOffset, float recoilAngleOffset)
        {
            CompSpaceWeapon weapon = ___parentTurret.TryGetComp<CompSpaceWeapon>();
            if (weapon?.SpaceModeEnabled == true) recoilDrawOffset = weapon.VisualRecoilOffset;
            IShellTurretTopSource source = SpaceTurretTopRenderer.SourceFor(___parentTurret);
            if (source == null) return true;
            SpaceTurretTopRenderer.Draw(___parentTurret, source, drawLoc, recoilDrawOffset, recoilAngleOffset);
            return false;
        }

        public static void Postfix(Building_Turret ___parentTurret)
        {
            if (!SpaceProjectileService.HasNativeWarmupIndicators) return;
            if (___parentTurret is Building_TurretGun gun)
                AutoSpaceWeaponTurretBridge.DrawNativeWarmupIndicator(gun);
        }
    }

    [HarmonyPatch(typeof(TurretTop), nameof(TurretTop.TurretTopTick))]
    public static class Patch_TurretTop_SpaceWeaponTraverse
    {
        public static bool Prefix(Building_Turret ___parentTurret)
        {
            CompSpaceWeaponTraverse traverse =
                (___parentTurret as ThingWithComps)?.TryGetComp<CompSpaceWeaponTraverse>();
            if (traverse == null) return true;
            if (___parentTurret is Building_Turret generated
                && AutoSpaceWeaponTurretBridge.IsAdapted(generated))
            {
                if (generated.TryGetComp<CompSpaceWeapon>()?.GroundModeEnabled == true) return true;
                return false;
            }
            LocalTargetInfo target = ___parentTurret.CurrentTarget;
            if (target.IsValid)
            {
                traverse.AimAtVanilla(CompSpaceWeaponTraverse.MapAngleTo(target, ___parentTurret));
            }
            return false;
        }
    }

    [HarmonyPatch]
    public static class Patch_BuildingTurretGun_SpaceWeaponAlignment
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            AutoSpaceWeaponTurretPatchTargets.DeclaredMethods("BeginBurst");

        public static bool Prefix(Building_TurretGun __instance)
        {
            if (AutoSpaceWeaponTurretBridge.IsAdapted(__instance))
            {
                if (!AutoSpaceWeaponTurretBridge.SpaceMode(__instance)) return true;
                SpaceProjectileService projectiles = Current.Game?.GetComponent<UniverseManager>()?.Projectiles;
                CompSpaceWeaponTraverse adaptedTraverse = __instance.TryGetComp<CompSpaceWeaponTraverse>();
                return adaptedTraverse != null
                    && projectiles?.RefreshNativeFireDirection(__instance.AttackVerb,
                        adaptedTraverse.CurrentAngle) == true;
            }
            CompSpaceWeaponTraverse traverse = __instance.TryGetComp<CompSpaceWeaponTraverse>();
            if (traverse == null || !__instance.CurrentTarget.IsValid) return true;
            float angle = CompSpaceWeaponTraverse.MapAngleTo(__instance.CurrentTarget, __instance);
            traverse.AimAtVanilla(angle);
            CompSpaceWeapon weapon = __instance.TryGetComp<CompSpaceWeapon>();
            float tolerance = weapon?.fireDeviationDeg ?? traverse.TraverseProps.fireToleranceDeg;
            return SpaceWeaponGeometry.WithinFireDeviation(angle, traverse.CurrentAngle, tolerance);
        }
    }

    [HarmonyPatch]
    public static class Patch_BuildingTurretGun_AutoSpaceTick
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            AutoSpaceWeaponTurretPatchTargets.DeclaredMethods("Tick");

        public static void Prefix(Building_TurretGun __instance) =>
            AutoSpaceWeaponTurretBridge.PrepareAuthorizedTick(__instance);

        public static void Postfix(Building_TurretGun __instance) =>
            AutoSpaceWeaponTurretBridge.RestoreAuthorizedTraverse(__instance);
    }

    [HarmonyPatch]
    internal static class Patch_BuildingTurretGun_AutoSpaceSelectionOverlay
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            AutoSpaceWeaponTurretPatchTargets.DeclaredMethods(
                nameof(Building_TurretGun.DrawExtraSelectionOverlays));

        public static void Prefix(Building_TurretGun __instance,
            out AutoSpaceWeaponTurretBridge.SelectionOverlayState __state) =>
            __state = AutoSpaceWeaponTurretBridge.HideProtocolTargetForSelectionOverlay(__instance);

        public static Exception Finalizer(Building_TurretGun __instance,
            AutoSpaceWeaponTurretBridge.SelectionOverlayState __state, Exception __exception)
        {
            AutoSpaceWeaponTurretBridge.RestoreProtocolTargetAfterSelectionOverlay(
                __instance, __state);
            return __exception;
        }
    }

    [HarmonyPatch]
    public static class Patch_BuildingTurretGun_AutoSpaceTargetSearch
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            AutoSpaceWeaponTurretPatchTargets.DeclaredMethods(nameof(Building_TurretGun.TryFindNewTarget));

        public static bool Prefix(Building_TurretGun __instance, ref LocalTargetInfo __result)
        {
            if (!AutoSpaceWeaponTurretBridge.SpaceMode(__instance)) return true;
            __result = LocalTargetInfo.Invalid;
            return false;
        }
    }

    [HarmonyPatch]
    public static class Patch_BuildingTurretGun_AutoSpaceForcedTarget
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            AutoSpaceWeaponTurretPatchTargets.DeclaredMethods(nameof(Building_TurretGun.OrderAttack));

        public static bool Prefix(Building_TurretGun __instance, LocalTargetInfo targ) =>
            !AutoSpaceWeaponTurretBridge.SpaceMode(__instance) || !targ.IsValid
            || Current.Game?.GetComponent<UniverseManager>()?.Projectiles
                .NativeCastAuthorized(__instance.AttackVerb) == true;
    }

    [HarmonyPatch]
    public static class Patch_BuildingTurretGun_AutoSpaceCanForceTarget
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            AutoSpaceWeaponTurretPatchTargets.DeclaredGetters("CanSetForcedTarget");

        public static void Postfix(Building_TurretGun __instance, ref bool __result)
        {
            if (!AutoSpaceWeaponTurretBridge.SpaceMode(__instance)) return;
            __result = Current.Game?.GetComponent<UniverseManager>()?.Projectiles
                .NativeCastAuthorized(__instance.AttackVerb) == true;
        }
    }

    [HarmonyPatch]
    public static class Patch_BuildingTurretGun_AutoSpaceHideForceTarget
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            AutoSpaceWeaponTurretPatchTargets.DeclaredGetters("HideForceTargetGizmo");

        public static void Postfix(Building_TurretGun __instance, ref bool __result)
        {
            if (AutoSpaceWeaponTurretBridge.SpaceMode(__instance)) __result = true;
        }
    }

    [HarmonyPatch]
    public static class Patch_BuildingTurretGun_AutoSpaceGizmos
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            AutoSpaceWeaponTurretPatchTargets.DeclaredMethods(nameof(Building_TurretGun.GetGizmos));

        public static void Postfix(Building_TurretGun __instance, ref IEnumerable<Gizmo> __result)
        {
            if (AutoSpaceWeaponTurretBridge.SpaceMode(__instance))
                __result = WithoutVanillaHoldFire(__result);
        }

        private static IEnumerable<Gizmo> WithoutVanillaHoldFire(IEnumerable<Gizmo> gizmos)
        {
            foreach (Gizmo gizmo in gizmos)
            {
                if (gizmo is Command_Toggle toggle
                    && toggle.defaultLabel == "CommandHoldFire".Translate()) continue;
                yield return gizmo;
            }
        }
    }

    /// <summary>原版 ShotReport 仍负责距离、武器、天气、掩体；仅把炮塔射手能力换成炮手。</summary>
    [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.HitFactorFromShooter))]
    public static class Patch_ShotReport_SpaceWeaponCrewAccuracy
    {
        public static bool Prefix(Thing caster, float distance, float? acc, ref float __result)
        {
            if (acc.HasValue || caster is not ThingWithComps comps) return true;
            CompSpaceWeapon weapon = comps.TryGetComp<CompSpaceWeapon>();
            if (caster is Building_Turret turret
                && AutoSpaceWeaponTurretBridge.IsAdapted(turret)
                && weapon?.GroundModeEnabled == true) return true;
            if (weapon?.Mannable == null || !weapon.TryGetSkillLevel(out int skill)) return true;
            float equivalentAccuracy = UnityEngine.Mathf.Lerp(0.80f, 0.99f,
                UnityEngine.Mathf.Clamp01(skill / 20f));
            __result = ShotReport.HitFactorFromShooter(caster, distance, equivalentAccuracy);
            return false;
        }
    }
}
