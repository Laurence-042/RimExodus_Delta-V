using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DeltaV
{
    [HarmonyPatch]
    internal static class Patch_PawnSpaceWeapon_AimAngleOverride
    {
        private static IEnumerable<MethodBase> TargetMethods() => GenTypes.AllTypes
            .Where(type => typeof(Verb).IsAssignableFrom(type))
            .Select(type => type.GetProperty(nameof(Verb.AimAngleOverride),
                BindingFlags.Instance | BindingFlags.Public)?.GetMethod)
            .Where(method => method != null)
            .GroupBy(method => (method.Module, method.MetadataToken))
            .Select(group => group.First());

        private static void Postfix(Verb __instance, ref float? __result)
        {
            if (PawnSpaceWeaponBridge.TryAimAngle(__instance, out float angle)) __result = angle;
        }
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAiming))]
    internal static class Patch_PawnSpaceWeapon_DrawEquipmentAiming
    {
        // NewRatkinPlus (Solaris.RatkinRaceMod) replaces the global equipment draw flow in
        // SYS.DrawEquipment_WeaponBackPatch. Its replacement passes 0/143/217 degrees here
        // without consulting Verb.AimAngleOverride; for the overlapping local placeholder it
        // also places the aimed weapon 0.4 cells north before this call. The final shared draw
        // entry must therefore keep angle and reach aligned. Keep this independent of SYS types:
        // other draw replacements may make the same otherwise-valid choice for a local target.
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(Thing eq, ref Vector3 drawLoc, ref float aimAngle)
        {
            PawnSpaceWeaponBridge.TryAimPose(eq, ref drawLoc, ref aimAngle);
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.CanHitTarget))]
    internal static class Patch_PawnSpaceWeapon_CanHitTarget
    {
        private static void Postfix(Verb __instance, ref bool __result)
        {
            if (PawnSpaceWeaponBridge.DirectCastActive(__instance)) __result = true;
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.WarmupTime), MethodType.Getter)]
    internal static class Patch_PawnSpaceWeapon_WarmupTime
    {
        private static void Postfix(Verb __instance, ref float __result)
        {
            if (PawnSpaceWeaponBridge.DirectCastActive(__instance)) __result = 0f;
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.TryFindShootLineFromTo))]
    internal static class Patch_PawnSpaceWeapon_ShootLine
    {
        private static bool Prefix(Verb __instance, IntVec3 root, LocalTargetInfo targ,
            ref ShootLine resultingLine, ref bool __result)
        {
            if (!PawnSpaceWeaponBridge.BypassShootLine(__instance)) return true;
            resultingLine = new ShootLine(root, targ.Cell);
            __result = true;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PawnSpaceWeapon_BurstShot
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(Verb), "TryCastNextBurstShot");

        private static void Prefix(Verb __instance) => PawnSpaceWeaponBridge.BeginShot(__instance);
        private static void Postfix(Verb __instance) => PawnSpaceWeaponBridge.CompleteShot(__instance);
        private static Exception Finalizer(Verb __instance, Exception __exception)
        {
            if (__exception != null) PawnSpaceWeaponBridge.AbortShot(__instance);
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PawnSpaceWeapon_ProjectileLaunch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(Verse.Projectile),
            nameof(Verse.Projectile.Launch), new[]
            {
                typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo),
                typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef)
            });

        private static void Postfix(Verse.Projectile __instance, Thing launcher, Vector3 origin,
            LocalTargetInfo usedTarget, Thing equipment)
        {
            PawnSpaceWeaponBridge.NoteProjectile(__instance, launcher as Pawn, equipment, origin, usedTarget);
        }
    }
}
