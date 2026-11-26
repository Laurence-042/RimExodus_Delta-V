using System;
using System.IO;
using System.Reflection;
using DeltaV;
using UnityEngine;
using Verse;

internal static partial class Program
{
    private sealed class SourceOwnedLaunch : ISpaceWeaponLaunch, ISpaceWeaponOwnsCommit
    {
        internal int launches;
        public bool TryLaunch(SpaceWeaponShot shot) { launches++; return true; }
        public bool OwnsCommit(SpaceWeaponShot shot) => true;
    }

    static void RunNativeProjectileAdaptationChecks(string repo)
    {
        string nativeService = File.ReadAllText(Path.Combine(repo,
            "Source/Interaction/SpaceProjectileService.Native.cs"));
        string nativePatch = File.ReadAllText(Path.Combine(repo,
            "Source/Harmony/Patch_NativeSpaceProjectile.cs"));
        string pawnBridge = File.ReadAllText(Path.Combine(repo,
            "Source/Interaction/PawnSpaceWeaponBridge.cs"));
        string turretBridge = File.ReadAllText(Path.Combine(repo,
            "Source/Compatibility/AutoSpaceWeaponTurretBridge.cs"));
        string turretPatch = File.ReadAllText(Path.Combine(repo,
            "Source/Harmony/Patch_SpaceWeaponVanillaTurret.cs"));

        Check(nativeService.Contains("RegisterNativeLaunch")
            && nativeService.Contains("TryCapturePendingNativeProjectile")
            && pawnBridge.Contains("AuthorizeNativeFire")
            && turretBridge.Contains("AuthorizeNativeFire")
            && !nativeService.Contains("CapturePawnProjectile"),
            "personnel weapons and adapted turrets converge on one native launch record and first-tick capture path");
        Check(nativePatch.Contains("HasPendingNativeProjectiles")
            && nativePatch.Contains("ProjectileMethods(\"Tick\"")
            && nativePatch.Contains("ProjectileMethods(\"TickInterval\"")
            && !nativeService.Contains("RegisteredWeapons(")
            && !nativeService.Contains("QueueFrameAction(() => RegisterNativeLaunch"),
            "native capture is event-driven and its idle projectile hook has a constant quick gate without collection scans");
        int tickPatchStart = nativePatch.IndexOf("class Patch_NativeSpaceProjectile_Tick", StringComparison.Ordinal);
        int intervalPatchStart = nativePatch.IndexOf("class Patch_NativeSpaceProjectile_TickInterval", StringComparison.Ordinal);
        int castPatchStart = nativePatch.IndexOf("class Patch_NativeSpaceWeapon_TryStartCastOn", StringComparison.Ordinal);
        string tickPatch = tickPatchStart >= 0 && intervalPatchStart > tickPatchStart
            ? nativePatch.Substring(tickPatchStart, intervalPatchStart - tickPatchStart) : "";
        string intervalPatch = intervalPatchStart >= 0 && castPatchStart > intervalPatchStart
            ? nativePatch.Substring(intervalPatchStart, castPatchStart - intervalPatchStart) : "";
        Check(tickPatch.Contains("IsPendingNativeProjectile")
            && !tickPatch.Contains("TryCapturePendingNativeProjectile")
            && intervalPatch.Contains("TryCapturePendingNativeProjectile")
            && tickPatch.Contains("HarmonyPriority(Priority.First)")
            && intervalPatch.Contains("HarmonyPriority(Priority.First)"),
            "native Tick suppresses local work while TickInterval alone performs the same-DoTick handoff");
        Check(nativeService.Contains("ResolveNativeLaunchDirection")
            && nativeService.Contains("BuildDegenerateSpreadAngles")
            && nativeService.Contains("props.sprayWidth")
            && nativeService.Contains("authorization.placeholderCenter")
            && nativeService.Contains("pendingNativeProjectiles.ContainsKey(projectile)) return")
            && !nativeService.Contains("record.launcher is Pawn"),
            "all launchers resolve center aim and source-range-normalized emission offsets without identity branches");
        MethodInfo resolveDirection = typeof(SpaceProjectileService).GetMethod(
            "ResolveNativeLaunchDirection", BindingFlags.Static | BindingFlags.NonPublic);
        Vector2 jitterDirection = (Vector2)resolveDirection.Invoke(null,
            new object[] { Vector2.right, new Vector2(0f, .3f), 30f, 0f });
        Vector2 sprayDirection = (Vector2)resolveDirection.Invoke(null,
            new object[] { Vector2.right, new Vector2(0f, 5f), 50f, 0f });
        float jitterAngle = Mathf.Atan2(jitterDirection.y, jitterDirection.x) * Mathf.Rad2Deg;
        float sprayAngle = Mathf.Atan2(sprayDirection.y, sprayDirection.x) * Mathf.Rad2Deg;
        Check(Mathf.Abs(jitterAngle - Mathf.Atan2(.3f, 30f) * Mathf.Rad2Deg) < .001f
            && Mathf.Abs(sprayAngle - Mathf.Atan2(5f, 50f) * Mathf.Rad2Deg) < .001f,
            "placeholder size never amplifies launch jitter or spray beyond the source-range angular scale");
        Check(nativePatch.Contains("GenTypes.AllTypes")
            && !nativePatch.Contains("Dead Man") && !nativePatch.Contains("Monolyn")
            && !nativePatch.Contains("联合重工") && !nativePatch.Contains("packageId"),
            "launch and tick interception is structural and contains no representative-mod identity checks");
        int forceTargetPatchStart = turretPatch.IndexOf(
            "class Patch_BuildingTurretGun_AutoSpaceCanForceTarget", StringComparison.Ordinal);
        int hideTargetPatchStart = turretPatch.IndexOf(
            "class Patch_BuildingTurretGun_AutoSpaceHideForceTarget", StringComparison.Ordinal);
        string forceTargetPatch = forceTargetPatchStart >= 0 && hideTargetPatchStart > forceTargetPatchStart
            ? turretPatch.Substring(forceTargetPatchStart, hideTargetPatchStart - forceTargetPatchStart) : "";
        Check(forceTargetPatch.Contains("NativeCastAuthorized(__instance.AttackVerb) == true")
            && forceTargetPatch.Contains("__result ="),
            "authorized native turret targets survive source warmup while unauthorized forced targets remain disabled");
        Check(turretBridge.Contains("TryTargetCell(turret, verb")
            && turretBridge.Contains("EffectiveMinRange(localTarget, turret)")
            && turretBridge.Contains("cellDistance > verb.EffectiveRange")
            && turretBridge.Contains("CancelNativeFire(verb, weapon)"),
            "native turret placeholders satisfy the source OrderAttack minimum and maximum range contract");
        Check(nativeService.Contains("TransientHandoffResult.Returned")
            && nativeService.Contains("TransientHandoffDisposition.Placed")
            && nativeService.Contains("TransientHandoffDisposition.Return"),
            "native impact distinguishes terminal destruction, placed delayed effects and surviving penetration");

        var sourceOwned = new SourceOwnedLaunch();
        var weapon = new CompSpaceWeapon
        {
            parent = new ThingWithComps(),
            props = new CompProperties_SpaceWeapon()
        };
        var shot = new SpaceWeaponShot(null, weapon, null, default);
        typeof(SpaceWeaponShot).GetField("emitter", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(shot, sourceOwned);
        bool launched = (bool)typeof(SpaceWeaponShot)
            .GetMethod("LaunchPrepared", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(shot, null);
        Check(launched && sourceOwned.launches == 1,
            "a native emitter can own its source transaction without invoking the synthetic Delta-V commit");

        Check(AutoSpaceWeaponPolicy.Enabled(AutoSpaceWeaponModPolicy.Recommended,
                AutoSpaceWeaponOverride.Inherit, true,
                AutoSpaceWeaponWarning.CustomVerb | AutoSpaceWeaponWarning.ExplosiveProjectile, false)
            && !AutoSpaceWeaponPolicy.Enabled(AutoSpaceWeaponModPolicy.Recommended,
                AutoSpaceWeaponOverride.Inherit, true, AutoSpaceWeaponWarning.CustomFlight, false),
            "recommended policy accepts replayable custom verbs and impacts but rejects unpreserved custom flight ticks");
    }
}
