using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DeltaV;
using RimWorld;
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
        string collision = File.ReadAllText(Path.Combine(repo,
            "Source/Core/SpaceCollision.cs"));
        string foundationDamage = File.ReadAllText(Path.Combine(repo,
            "Source/Core/SubstructureDamageUtility.cs"));
        string foundationPatch = File.ReadAllText(Path.Combine(repo,
            "Source/Harmony/Patch_SubstructureDamage.cs"));
        string nativeProjectileDefs = File.ReadAllText(Path.Combine(repo,
            "1.6/Defs/SpaceCombat/DeltaV_PawnSpaceflight.xml"));
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
        Check(turretBridge.Contains("TryProtocolCell(turret, verb, shot.launchMapAngle")
            && turretBridge.Contains("EffectiveMinRange(localTarget, turret)")
            && turretBridge.Contains("cellDistance <= verb.EffectiveRange")
            && turretBridge.Contains("fallback.IsValid")
            && turretBridge.Contains("CancelNativeFire(verb, weapon)"),
            "protocol cells encode the launch bearing, prefer source range bands and never fail on map edges or long minimum ranges");
        Check(turretPatch.Contains("class Patch_BuildingTurretGun_AutoSpaceTick")
            && turretPatch.Contains("PrepareAuthorizedTick(__instance)")
            && turretPatch.Contains("RestoreAuthorizedTraverse(__instance)")
            && turretPatch.Contains("RefreshNativeFireDirection(__instance.AttackVerb")
            && nativeService.Contains("warmupReadyTick")
            && nativeService.Contains("TryGetNativeWarmupRemaining")
            && nativeService.Contains("authorization.warmupReadyTick + burstWindow"),
            "adapted turret orientation and warmup use authorized Traverse state instead of the protocol cell");
        int castGateStart = nativePatch.IndexOf("class Patch_NativeSpaceWeapon_TryStartCastOn", StringComparison.Ordinal);
        int exemptionStart = nativePatch.IndexOf("class Patch_Verb_ProtocolCellExemption", StringComparison.Ordinal);
        string castGate = castGateStart >= 0 && exemptionStart > castGateStart
            ? nativePatch.Substring(castGateStart, exemptionStart - castGateStart) : "";
        Check(castGate.Contains("RefreshNativeFireDirection(__instance, traverse.CurrentAngle)")
            && castGate.Contains("ExtendNativeAuthorization(__instance)")
            && castGate.Contains("ReauthorizeNativeCast(rebuild, __instance)")
            && turretBridge.Contains("internal static bool ReauthorizeNativeCast(Building_Turret turret, Verb verb)")
            && turretBridge.Contains("verb.state == VerbState.Bursting || weapon.nativeLaunchUnsupported")
            && nativeService.Contains("ExtendNativeAuthorization"),
            "the cast gate renews direction and expiry on site and rebuilds an expired grant from live turret state");
        Check(exemptionStart >= 0
            && nativePatch.Contains("TryGetNativeProtocolCell(__instance, out IntVec3 cell)")
            && nativePatch.Contains("targ.Cell != cell")
            && nativePatch.Contains("new ShootLine(root, cell)")
            && nativeService.Contains("TryGetNativeProtocolCell")
            && nativeService.Contains("authorization.protocolCell = new IntVec3("),
            "only the registered placeholder cell bypasses local range and sight validation at the shoot-line choke point");
        MethodInfo shootLine = typeof(Verb).GetMethod("TryFindShootLineFromTo",
            BindingFlags.Instance | BindingFlags.Public, null,
            new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(ShootLine).MakeByRefType(), typeof(bool) }, null);
        Check(shootLine != null && shootLine.GetParameters()[2].IsOut
            && nativePatch.Contains("VerbShootLineMethods()"),
            "the exemption hook binds the real TryFindShootLineFromTo declaration of the running assembly");
        Check(turretPatch.Contains("typeof(Building_Turret).AllSubclasses()")
            && turretPatch.Contains("method.IsAbstract")
            && turretPatch.Contains("Prefix(Building_Turret __instance)")
            && !turretPatch.Contains("Prefix(Building_TurretGun __instance)"),
            "turret patches cover every Building_Turret implementation with contract-filtered name matches");
        string heatVisuals = File.ReadAllText(Path.Combine(repo,
            "Source/Rendering/WeaponHeatVisuals.cs"));
        string motionVisuals = File.ReadAllText(Path.Combine(repo,
            "Source/Interaction/SpaceProjectileService.MotionVisuals.cs"));
        Check(heatVisuals.Contains(
                "internal static void EmitCoolingSmoke(Building_Turret turret, float shellTopAngle,")
            && heatVisuals.Contains("bool cannon)")
            && heatVisuals.Contains("ShouldSpawnMotesAt")
            && turretBridge.Contains("IsHashIntervalTick(15)")
            && turretBridge.Contains("NativeCooldownTicksLeft(turret) <= 0")
            && turretBridge.Contains("EmitGunCoolingSmoke(weaponComp)")
            && turretBridge.Contains("if (turret is Building_TurretGun gun) return BurstCooldown(gun);")
            && turretBridge.Contains("\"burstCooldownTicksLeft\"")
            && !turretBridge.Contains("weaponComp?.CooldownTicksLeft")
            && motionVisuals.Contains("weapon.parent is not Building_Turret turret")
            && motionVisuals.Contains("SpaceTurretTopRenderer.SourceFor(turret)?.ShellTopAngle"),
            "adapted turrets vent heavy-cannon cooling smoke through the shared ship-bound particle stream gated by the turret's own countdown");
        Check(turretPatch.Contains("class Patch_BuildingTurretGun_AutoSpaceSelectionOverlay")
            && turretPatch.Contains("nameof(Building_TurretGun.DrawExtraSelectionOverlays)")
            && turretPatch.Contains("HideProtocolTargetForSelectionOverlay(__instance)")
            && turretPatch.Contains("RestoreProtocolTargetAfterSelectionOverlay(")
            && turretPatch.Contains("public static Exception Finalizer")
            && turretBridge.Contains("ForcedTarget(gun) = LocalTargetInfo.Invalid")
            && turretBridge.Contains("CurrentTarget(gun) = LocalTargetInfo.Invalid")
            && turretBridge.Contains("BurstWarmup(gun) = 0")
            && turretBridge.Contains("ForcedTarget(gun) = state.forcedTarget")
            && turretBridge.Contains("CurrentTarget(gun) = state.currentTarget")
            && turretBridge.Contains("BurstWarmup(gun) = state.warmupTicks"),
            "protocol targets are hidden only for source selection overlays and restored even when drawing throws");
        Check(nativeService.Contains("warmupStartedTick")
            && nativeService.Contains("warmupDurationTicks")
            && nativeService.Contains("TryGetNativeWarmupProgress")
            && nativeService.Contains("authorization.warmupReadyTick - now")
            && nativeService.Contains("HasNativeWarmupIndicators")
            && nativeService.Contains("if (nativeWarmupIndicatorCount <= 0) return false")
            && turretPatch.Contains("if (!SpaceProjectileService.HasNativeWarmupIndicators) return")
            && turretPatch.Contains("DrawNativeWarmupIndicator(gun)")
            && !turretPatch.Contains("DrawNativeWarmupIndicator(__instance, __state)")
            && turretBridge.Contains("SpaceWeaponGeometry.MuzzleMapPointAtAngle(weapon, traverse.CurrentAngle)")
            && turretBridge.Contains("SpaceTransform.Rotate(Vector2.right, traverse.CurrentAngle)")
            && turretBridge.Contains("Find.TickManager.TicksGame % period")
            && turretBridge.Contains("warmupIndicatorGlow, .32f")
            && turretBridge.Contains("warmupIndicatorCore, .10f")
            && !turretBridge.Contains("Time.time")
            && !turretBridge.Contains("state.currentTarget.Cell"),
            "visible native turret warmup has an idle integer gate and uses game-tick progress plus the authoritative muzzle and Traverse ray");
        Check(nativeService.Contains("TransientHandoffResult.Returned")
            && nativeService.Contains("TransientHandoffDisposition.Placed")
            && nativeService.Contains("TransientHandoffDisposition.Return"),
            "native impact distinguishes terminal destruction, placed delayed effects and surviving penetration");
        Check(!collision.Contains("body.onStructureImpact != null || ray.kind")
            && collision.Contains("SpaceImpactEffects.HitTest(body.projectileDef, ray.kind")
            && collision.Contains("rejected.foundations"),
            "native structure callbacks share building and deck hit tests; a missed deck remains a pass-through contact");
        Check(nativeService.Contains("ApplyNativeFoundationDirectDamage")
            && nativeService.Contains("projectile is not Bullet")
            && nativeService.Contains("projectile is Projectile_Explosive")
            && nativeService.Contains("props.explosionRadius > 0f")
            && foundationDamage.Contains("internal static bool TryDamage"),
            "ordinary native bullets add one stateless deck damage roll while standard explosives defer to their real explosion");
        Check(foundationPatch.Contains("private static void Postfix(Explosion explosion, IntVec3 c)")
            && foundationPatch.Contains("DamageWorker_SpaceHighExplosive")
            && foundationPatch.Contains("explosion.GetDamageAmountAt(c)")
            && !foundationPatch.Contains("Dictionary<") && !foundationPatch.Contains("Scribe_"),
            "standard immediate and delayed explosions damage exposed decks without persistent hit proxies or duplicate Delta-V HE rolls");
        Check(nativeService.Contains("flight.damageScale = Mathf.Max(0f, projectile.DamageAmount)")
            && nativeProjectileDefs.Contains("<kind>ShieldDrain</kind><damage>1</damage><incidenceMinFactor>1</incidenceMinFactor>")
            && !nativeService.Contains("record.weapon != null ? projectile.DamageAmount")
            && !nativeService.Contains("record.launcher is Pawn"),
            "all captured native rounds spend their actual instance damage on guard shields without launcher identity branches");

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

        string weaponModel = File.ReadAllText(Path.Combine(repo,
            "Source/Interaction/SpaceWeaponModel.cs"));
        Check(nativePatch.Contains("class Patch_NativeSpaceWeapon_ShotResult")
            && nativePatch.Contains("\"TryCastShot\"")
            && nativePatch.Contains("if (!__result || !SpaceProjectileService.HasNativeAuthorizations) return;")
            && nativePatch.Contains("NoteNativeShot(__instance)")
            && nativeService.Contains("authorization.shotReported && !authorization.launched")
            && nativeService.Contains("authorization.launched = true")
            && nativeService.Contains("AutoSpaceWeaponTurretBridge.NotifyNativeLaunchUnsupported(turret)")
            && nativeService.Contains("NotifyNativeCustomFlight(record.weapon, projectile)")
            && turretBridge.Contains("&& !shot.weapon.nativeLaunchUnsupported"),
            "a consumed source shot that registers no Launch is detected from the shot result alone and stops space fire for that turret");
        Type shotPatch = typeof(SpaceProjectileService).Assembly.GetType("DeltaV.Patch_NativeSpaceWeapon_ShotResult");
        var shotTargets = (shotPatch?.GetMethod("TargetMethods", BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, null) as System.Collections.IEnumerable)?.Cast<MethodBase>().ToList();
        Check(shotTargets?.Any(method => method.DeclaringType == typeof(Verb_LaunchProjectile)
                && method.Name == "TryCastShot") == true
            && shotTargets.All(method => !method.IsAbstract && typeof(Verb).IsAssignableFrom(method.DeclaringType)),
            "the shot result hook targets the real TryCastShot overrides of the production assembly");
        Check(turretPatch.Contains("class Patch_VerbUtility_AutoSpaceOverheadFire")
            && turretPatch.Contains("nameof(VerbUtility.ProjectileFliesOverhead)")
            && turretPatch.Contains("AutoSpaceWeaponTurretBridge.SpaceMode(turret)")
            && typeof(VerbUtility).GetMethod("ProjectileFliesOverhead",
                BindingFlags.Static | BindingFlags.Public) is MethodInfo overhead
            && overhead.GetParameters()[0].ParameterType == typeof(Verb),
            "space mode replaces the source's local overhead roof rule on the real VerbUtility extension");
        Check(weaponModel.Contains("weapon.NativeProjectileSpeed ??")
            && turretBridge.Contains("verb is Verb_LaunchProjectile launch")
            && turretBridge.Contains("launch.Projectile : verb?.verbProps?.defaultProjectile")
            && Math.Abs(AutoSpaceWeaponTurretBridge.SpeedCellsPerSecond(new ThingDef
                { projectile = new ProjectileProperties { speed = 70f } }).Value - 42f) < 1e-4f
            && Math.Abs(AutoSpaceWeaponTurretBridge.SpeedCellsPerSecond(new ThingDef
                { projectile = new ProjectileProperties { speed = 41f } }).Value - 24.6f) < 1e-4f
            && AutoSpaceWeaponTurretBridge.SpeedCellsPerSecond(new ThingDef
                { projectile = new ProjectileProperties { speed = 0f } }) == null
            && AutoSpaceWeaponTurretBridge.SpeedCellsPerSecond(null) == null,
            "adapted turrets lead with the round their source Verb will launch at its real tiles-per-tick speed");
        MethodInfo baseSpeed = typeof(CompSpaceWeapon).Assembly.GetType("DeltaV.SpaceWeaponBallistics")
            ?.GetMethod("BaseSpeed", BindingFlags.Static | BindingFlags.NonPublic);
        FieldInfo adaptedFlag = typeof(CompSpaceWeapon).GetField("nativeAdapted",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var adapted = new CompSpaceWeapon { parent = new Building_TurretGun(), props = new CompProperties_SpaceWeapon() };
        var ordinary = new CompSpaceWeapon { parent = new Building_TurretGun(), props = new CompProperties_SpaceWeapon() };
        adaptedFlag?.SetValue(adapted, true);
        Check(baseSpeed != null && adaptedFlag != null
            && (float)baseSpeed.Invoke(null, new object[] { adapted, null, 60f }) == 60f
            && (float)baseSpeed.Invoke(null, new object[] { ordinary, null, 48f }) == 48f
            && !ordinary.nativeLaunchUnsupported,
            "a native source with no resolvable round and an ordinary weapon both keep their proxy speed");
        foreach (string language in new[] { "ChineseSimplified", "English" })
        {
            string keyed = File.ReadAllText(Path.Combine(repo, $"1.6/Languages/{language}/Keyed/DeltaV.xml"));
            Check(keyed.Contains("<DeltaV_AutoWeaponWarningCustomFlight>")
                && keyed.Contains("<DeltaV_AutoWeaponWarningRuntimeProjectile>")
                && keyed.Contains("<DeltaV_AutoWeaponWarningNonKineticDamage>")
                && keyed.Contains("<DeltaV_AutoWeaponNoCapturableProjectile>")
                && keyed.Contains("<DeltaV_AutoWeaponLaunchUnsupported>"),
                language + " translations name every adaptation warning and the uncapturable-shot notice");
        }

        Check(AutoSpaceWeaponPolicy.Enabled(AutoSpaceWeaponModPolicy.Recommended,
                AutoSpaceWeaponOverride.Inherit, true,
                AutoSpaceWeaponWarning.CustomVerb | AutoSpaceWeaponWarning.ExplosiveProjectile, false)
            && !AutoSpaceWeaponPolicy.Enabled(AutoSpaceWeaponModPolicy.Recommended,
                AutoSpaceWeaponOverride.Inherit, true, AutoSpaceWeaponWarning.CustomFlight, false),
            "recommended policy accepts replayable custom verbs and impacts but rejects unpreserved custom flight ticks");
    }
}
