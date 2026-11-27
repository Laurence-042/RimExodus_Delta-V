using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using UnityEngine;

namespace DeltaV
{
    /// <summary>
    /// Patch roots span every Building_Turret implementation, not only Building_TurretGun
    /// subclasses: source mods may rebuild turret logic straight on the abstract base. Name
    /// matches stay constrained by return type and parameters so a same-named third-party
    /// member with a different contract can never break patch assembly.
    /// </summary>
    internal static class AutoSpaceWeaponTurretPatchTargets
    {
        private static IEnumerable<System.Type> TurretTypes()
        {
            yield return typeof(Building_Turret);
            foreach (System.Type type in typeof(Building_Turret).AllSubclasses()) yield return type;
        }

        internal static IEnumerable<MethodBase> DeclaredMethods(string name, Type returnType,
            params Type[] parameters)
        {
            foreach (System.Type type in TurretTypes())
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.IsAbstract || method.Name != name
                        || method.ReturnType != returnType) continue;
                    ParameterInfo[] actual = method.GetParameters();
                    if (actual.Length != parameters.Length) continue;
                    bool mismatch = false;
                    for (int index = 0; index < parameters.Length; index++)
                    {
                        if (actual[index].ParameterType != parameters[index])
                        {
                            mismatch = true;
                            break;
                        }
                    }
                    if (mismatch) continue;
                    yield return method;
                }
            }
        }

        internal static IEnumerable<MethodBase> DeclaredGetters(string name)
        {
            foreach (System.Type type in TurretTypes())
            {
                MethodInfo getter = AccessTools.DeclaredProperty(type, name)?.GetGetMethod(true);
                if (getter != null && !getter.IsAbstract && getter.ReturnType == typeof(bool))
                    yield return getter;
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
            AutoSpaceWeaponTurretPatchTargets.DeclaredMethods("BeginBurst", typeof(void));

        public static bool Prefix(Building_Turret __instance)
        {
            if (AutoSpaceWeaponTurretBridge.IsAdapted(__instance))
            {
                if (!AutoSpaceWeaponTurretBridge.SpaceMode(__instance)) return true;
                SpaceProjectileService projectiles = Current.Game?.GetComponent<UniverseManager>()?.Projectiles;
                CompSpaceWeaponTraverse adaptedTraverse = __instance.TryGetComp<CompSpaceWeaponTraverse>();
                if (projectiles == null || adaptedTraverse == null) return true;
                // 授权判据统一在来源 Verb 的发射门执行：这里只顺带续期方向，放行 BeginBurst，
                // 过期授权由发射门现场重建或拦截；先拦 BeginBurst 会挡住非 TurretGun 来源
                // 到达发射门的路径，重现授权死锁。
                if (projectiles.NativeCastAuthorized(__instance.AttackVerb))
                    projectiles.RefreshNativeFireDirection(__instance.AttackVerb,
                        adaptedTraverse.CurrentAngle);
                return true;
            }
            CompSpaceWeaponTraverse traverse = __instance.TryGetComp<CompSpaceWeaponTraverse>();
            if (traverse == null || !__instance.CurrentTarget.IsValid) return true;
            Verb verb = __instance.AttackVerb;
            if (verb == null) return true;
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
            AutoSpaceWeaponTurretPatchTargets.DeclaredMethods("Tick", typeof(void));

        public static void Prefix(Building_Turret __instance) =>
            AutoSpaceWeaponTurretBridge.PrepareAuthorizedTick(__instance);

        public static void Postfix(Building_Turret __instance) =>
            AutoSpaceWeaponTurretBridge.RestoreAuthorizedTraverse(__instance);
    }

    [HarmonyPatch]
    internal static class Patch_BuildingTurretGun_AutoSpaceSelectionOverlay
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            AutoSpaceWeaponTurretPatchTargets.DeclaredMethods(
                nameof(Building_TurretGun.DrawExtraSelectionOverlays), typeof(void));

        public static void Prefix(Building_Turret __instance,
            out AutoSpaceWeaponTurretBridge.SelectionOverlayState __state) =>
            __state = AutoSpaceWeaponTurretBridge.HideProtocolTargetForSelectionOverlay(__instance);

        public static Exception Finalizer(Building_Turret __instance,
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
            AutoSpaceWeaponTurretPatchTargets.DeclaredMethods(nameof(Building_TurretGun.TryFindNewTarget),
                typeof(LocalTargetInfo));

        public static bool Prefix(Building_Turret __instance, ref LocalTargetInfo __result)
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
            AutoSpaceWeaponTurretPatchTargets.DeclaredMethods(nameof(Building_TurretGun.OrderAttack),
                typeof(void), typeof(LocalTargetInfo));

        public static bool Prefix(Building_Turret __instance, LocalTargetInfo targ) =>
            !AutoSpaceWeaponTurretBridge.SpaceMode(__instance) || !targ.IsValid
            || Current.Game?.GetComponent<UniverseManager>()?.Projectiles
                .NativeCastAuthorized(__instance.AttackVerb) == true;
    }

    [HarmonyPatch]
    public static class Patch_BuildingTurretGun_AutoSpaceCanForceTarget
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            AutoSpaceWeaponTurretPatchTargets.DeclaredGetters("CanSetForcedTarget");

        public static void Postfix(Building_Turret __instance, ref bool __result)
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

        public static void Postfix(Building_Turret __instance, ref bool __result)
        {
            if (AutoSpaceWeaponTurretBridge.SpaceMode(__instance)) __result = true;
        }
    }

    [HarmonyPatch]
    public static class Patch_BuildingTurretGun_AutoSpaceGizmos
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            AutoSpaceWeaponTurretPatchTargets.DeclaredMethods(nameof(Building_TurretGun.GetGizmos),
                typeof(IEnumerable<Gizmo>));

        public static void Postfix(Building_Turret __instance, ref IEnumerable<Gizmo> __result)
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

    /// <summary>
    /// 太空模式下曲射弹由宇宙弹道接管，来源“屋顶下不得开火”的本地规则不再适用。
    /// 地面模式与未适配炮塔保持原版判断。
    /// </summary>
    [HarmonyPatch(typeof(VerbUtility), nameof(VerbUtility.ProjectileFliesOverhead))]
    public static class Patch_VerbUtility_AutoSpaceOverheadFire
    {
        public static bool Prefix(Verb verb, ref bool __result)
        {
            if (verb?.Caster is not Building_Turret turret
                || !AutoSpaceWeaponTurretBridge.SpaceMode(turret)) return true;
            __result = false;
            return false;
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
