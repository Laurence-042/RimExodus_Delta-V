using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DeltaV
{
    internal static class NativeSpaceProjectilePatchTargets
    {
        private static readonly Type[] LaunchSignature =
        {
            typeof(Thing), typeof(UnityEngine.Vector3), typeof(LocalTargetInfo),
            typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool),
            typeof(Thing), typeof(ThingDef)
        };

        private static readonly Type[] CastSignature =
        {
            typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(bool), typeof(bool),
            typeof(bool), typeof(bool)
        };

        internal static IEnumerable<MethodBase> ProjectileMethods(string name, Type[] signature)
            => DeclaredVirtualMethods(typeof(Verse.Projectile), name, signature);

        internal static IEnumerable<MethodBase> VerbCastMethods()
            => DeclaredVirtualMethods(typeof(Verb), nameof(Verb.TryStartCastOn), CastSignature);

        internal static IEnumerable<MethodBase> VerbShotMethods()
            => DeclaredVirtualMethods(typeof(Verb), "TryCastShot", Type.EmptyTypes);

        private static IEnumerable<MethodBase> DeclaredVirtualMethods(Type root, string name,
            Type[] signature)
        {
            return GenTypes.AllTypes.Where(type => type != null && root.IsAssignableFrom(type))
                .Select(type => type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, signature, null))
                .Where(method => method != null && !method.IsAbstract)
                .GroupBy(method => (method.Module, method.MetadataToken))
                .Select(group => (MethodBase)group.First());
        }

        internal static Type[] LaunchParameters => LaunchSignature;
    }

    /// <summary>
    /// Observe every concrete override of the real Projectile.Launch signature. The projectile
    /// remains on its source map until the first Tick boundary, so derived Launch bodies and
    /// their callers may finish initializing it.
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_NativeSpaceProjectile_Launch
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            NativeSpaceProjectilePatchTargets.ProjectileMethods(nameof(Verse.Projectile.Launch),
                NativeSpaceProjectilePatchTargets.LaunchParameters);

        private static void Postfix(Verse.Projectile __instance, object[] __args)
        {
            Thing launcher = __args?.Length > 0 ? __args[0] as Thing : null;
            Thing equipment = __args?.Length > 6 ? __args[6] as Thing : null;
            Current.Game?.GetComponent<UniverseManager>()?.Projectiles
                .RegisterNativeLaunch(__instance, launcher, equipment);
        }
    }

    [HarmonyPatch]
    internal static class Patch_NativeSpaceProjectile_Tick
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            NativeSpaceProjectilePatchTargets.ProjectileMethods("Tick", Type.EmptyTypes);

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Verse.Projectile __instance)
        {
            if (!SpaceProjectileService.HasPendingNativeProjectiles) return true;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            // Thing.DoTick invokes Tick and may then invoke TickInterval in the same call. Do not
            // despawn here: doing so would let the following native TickInterval run with Map=null.
            // Suppress the local Tick body and leave the record for the interval boundary.
            return universe == null || !universe.Projectiles.IsPendingNativeProjectile(__instance);
        }
    }

    [HarmonyPatch]
    internal static class Patch_NativeSpaceProjectile_TickInterval
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            NativeSpaceProjectilePatchTargets.ProjectileMethods("TickInterval", new[] { typeof(int) });

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Verse.Projectile __instance)
        {
            if (!SpaceProjectileService.HasPendingNativeProjectiles) return true;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            return universe == null || !universe.Projectiles.TryCapturePendingNativeProjectile(__instance);
        }
    }

    /// <summary>Space-mode adapted turrets may start their real Verb only through Delta-V fire control.</summary>
    [HarmonyPatch]
    internal static class Patch_NativeSpaceWeapon_TryStartCastOn
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            NativeSpaceProjectilePatchTargets.VerbCastMethods();

        private static bool Prefix(Verb __instance, ref bool __result)
        {
            if (__instance?.Caster is not ThingWithComps caster) return true;
            CompSpaceWeapon weapon = caster.TryGetComp<CompSpaceWeapon>();
            if (weapon?.SpaceModeEnabled != true
                || !AutoSpaceWeaponTurretBridge.IsNativeAdapted(caster.def)) return true;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.Projectiles.NativeCastAuthorized(__instance) == true) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_NativeSpaceWeapon_BurstCompleted
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(Verb), "TryCastNextBurstShot");

        private static void Postfix(Verb __instance)
        {
            Current.Game?.GetComponent<UniverseManager>()?.Projectiles.CompleteNativeBurst(__instance);
        }
    }

    /// <summary>
    /// Observes every concrete TryCastShot. Only its result separates a consumed shot that
    /// launched nothing capturable from a shot the source Verb declined to fire.
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_NativeSpaceWeapon_ShotResult
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            NativeSpaceProjectilePatchTargets.VerbShotMethods();

        private static void Postfix(Verb __instance, bool __result)
        {
            // Harmony's verb shot hook reads only this process-wide quick gate while idle.
            if (!__result || !SpaceProjectileService.HasNativeAuthorizations) return;
            Current.Game?.GetComponent<UniverseManager>()?.Projectiles.NoteNativeShot(__instance);
        }
    }
}
