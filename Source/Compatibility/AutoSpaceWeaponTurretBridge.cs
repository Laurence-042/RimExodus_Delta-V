using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed class CompProperties_AutoSpaceWeaponTurretBridge : CompProperties
    {
        public CompProperties_AutoSpaceWeaponTurretBridge()
        {
            compClass = typeof(CompAutoSpaceWeaponTurretBridge);
        }
    }

    /// <summary>Exposes the original turret top to the transformed shell renderer.</summary>
    public sealed class CompAutoSpaceWeaponTurretBridge : ThingComp, IShellTurretTopSource,
        ISpaceWeaponFireGate, ISpaceWeaponLaunch, ISpaceWeaponOwnsCommit
    {
        private Building_Turret Turret => parent as Building_Turret;
        private CompSpaceWeapon weaponComp;
        public Material ShellTopMaterial => Turret?.TurretTopMaterial;

        public float ShellTopAngle
        {
            get
            {
                // In space a map-local AimAngleOverride describes the protocol cell or another
                // local artifact, never the universe ray; only the Traverse angle is the aim truth.
                weaponComp ??= parent.TryGetComp<CompSpaceWeapon>();
                float? localOverride = weaponComp?.SpaceModeEnabled == true
                    ? (float?)null : Turret?.CurrentEffectiveVerb?.AimAngleOverride;
                return localOverride ?? (Turret as Building_TurretGun)?.Top?.CurRotation
                    ?? parent.Rotation.AsAngle;
            }
        }

        public bool CanFire(SpaceWeaponShot shot)
        {
            if (!AutoSpaceWeaponTurretBridge.IsNativeAdapted(parent.def)) return true;
            Verb verb = Turret?.AttackVerb;
            return shot?.weapon?.parent == parent && verb != null && verb.state != VerbState.Bursting
                && !Turret.CurrentTarget.IsValid && verb.Available()
                && !shot.weapon.nativeLaunchUnsupported;
        }

        public bool TryLaunch(SpaceWeaponShot shot) =>
            AutoSpaceWeaponTurretBridge.IsNativeAdapted(parent.def)
                ? AutoSpaceWeaponTurretBridge.TryLaunchNative(Turret, shot)
                : shot?.LaunchProjectile() == true;

        public bool OwnsCommit(SpaceWeaponShot shot) =>
            AutoSpaceWeaponTurretBridge.IsNativeAdapted(parent.def);

        public override void CompTick()
        {
            base.CompTick();
            // Space-mode cooldown vents the same heavy-cannon smoke the native guns use: the
            // shared combat-particle stream while attached to a ship, map flecks as fallback.
            // The turret's own countdown is the fact source; unmirrored sources stay smokeless.
            if (!parent.IsHashIntervalTick(15)) return;
            Building_Turret turret = Turret;
            if (turret == null || !AutoSpaceWeaponTurretBridge.SpaceMode(turret)
                || AutoSpaceWeaponTurretBridge.NativeCooldownTicksLeft(turret) <= 0) return;
            weaponComp ??= parent.TryGetComp<CompSpaceWeapon>();
            if (weaponComp?.Container != null)
                weaponComp.Universe?.Projectiles.EmitGunCoolingSmoke(weaponComp);
            else if (turret.Spawned)
                WeaponHeatVisuals.EmitCoolingSmoke(turret, ShellTopAngle, cannon: true);
        }
    }

    /// <summary>
    /// Keeps vanilla target/warmup/cooldown state mutually exclusive with Delta-V fire control.
    /// The generated Def marker is the only compatibility identity; no per-instance mirror is saved.
    /// </summary>
    public static class AutoSpaceWeaponTurretBridge
    {
        private static readonly AccessTools.FieldRef<Building_TurretGun, int> BurstCooldown =
            AccessTools.FieldRefAccess<Building_TurretGun, int>("burstCooldownTicksLeft");
        private static readonly AccessTools.FieldRef<Building_TurretGun, int> BurstWarmup =
            AccessTools.FieldRefAccess<Building_TurretGun, int>("burstWarmupTicksLeft");
        private static readonly AccessTools.FieldRef<Building_TurretGun, LocalTargetInfo> CurrentTarget =
            AccessTools.FieldRefAccess<Building_TurretGun, LocalTargetInfo>("currentTargetInt");
        private static readonly AccessTools.FieldRef<Building_Turret, LocalTargetInfo> ForcedTarget =
            AccessTools.FieldRefAccess<Building_Turret, LocalTargetInfo>("forcedTarget");
        private static readonly AccessTools.FieldRef<CompSpaceWeaponTraverse, float> TraverseAngle =
            AccessTools.FieldRefAccess<CompSpaceWeaponTraverse, float>("currentAngle");
        private static readonly AccessTools.FieldRef<CompSpaceWeaponTraverse, int> TraverseTick =
            AccessTools.FieldRefAccess<CompSpaceWeaponTraverse, int>("rotationTick");
        private static readonly Dictionary<Type, FieldInfo> NativeCooldownFields =
            new Dictionary<Type, FieldInfo>();
        private static Material warmupIndicatorGlow;
        private static Material warmupIndicatorCore;

        public static bool IsAdapted(ThingDef def) =>
            def?.GetModExtension<DefModExtension_AutoSpaceWeaponResolved>() != null;

        public static bool IsNativeAdapted(ThingDef def) =>
            def?.GetModExtension<DefModExtension_AutoSpaceWeaponResolved>()?.mode
                == AutoSpaceWeaponMode.NativeProjectile;

        public static bool IsAdapted(Building_Turret turret) => turret != null && IsAdapted(turret.def);

        public static bool SpaceMode(Building_Turret turret) => IsAdapted(turret)
            && turret.TryGetComp<CompSpaceWeapon>()?.SpaceModeEnabled == true;

        /// <summary>
        /// Remaining ticks of the turret's own burst cooldown. Building_TurretGun uses its field
        /// ref; other implementations resolve a vanilla-mirrored countdown field per concrete
        /// type, and sources without one simply report no cooldown.
        /// </summary>
        internal static int NativeCooldownTicksLeft(Building_Turret turret)
        {
            if (turret is Building_TurretGun gun) return BurstCooldown(gun);
            Type type = turret.GetType();
            if (!NativeCooldownFields.TryGetValue(type, out FieldInfo field))
            {
                for (Type scan = type; scan != null && scan != typeof(Building_Turret);
                    scan = scan.BaseType)
                {
                    field = scan.GetField("burstCooldownTicksLeft", BindingFlags.Instance
                        | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (field != null && field.FieldType == typeof(int)) break;
                    field = null;
                }
                NativeCooldownFields[type] = field;
            }
            return field != null ? (int)field.GetValue(turret) : 0;
        }

        /// <summary>Speed of the round the source Verb will actually launch, in cells per second.</summary>
        internal static float? NativeProjectileSpeed(Building_Turret turret)
        {
            // Building_TurretGun.AttackVerb dereferences its gun; an unmade gun has no round yet.
            if (turret is Building_TurretGun { gun: null }) return null;
            Verb verb = turret?.AttackVerb;
            // The virtual Projectile property includes the shell loaded into CompChangeableProjectile.
            return SpeedCellsPerSecond(verb is Verb_LaunchProjectile launch
                ? launch.Projectile : verb?.verbProps?.defaultProjectile);
        }

        /// <summary>Cells per second of a projectile Def; null when the Def declares no finite speed.</summary>
        public static float? SpeedCellsPerSecond(ThingDef projectile)
        {
            float tilesPerTick = projectile?.projectile?.SpeedTilesPerTick ?? 0f;
            return tilesPerTick > 0f && !float.IsInfinity(tilesPerTick)
                ? tilesPerTick * 60f : (float?)null;
        }

        /// <summary>
        /// The source consumed a shot without launching anything capturable. Space fire stops
        /// for this turret; its ground-mode chain is untouched.
        /// </summary>
        internal static void NotifyNativeLaunchUnsupported(Building_Turret turret)
        {
            if (!SpaceMode(turret) || !(turret.TryGetComp<CompSpaceWeapon>() is { } weapon)
                || weapon.nativeLaunchUnsupported) return;
            weapon.nativeLaunchUnsupported = true;
            Log.Warning($"[DeltaV] Adapted turret '{turret.def.defName}' completed a space shot without "
                + "launching a capturable projectile. Space fire is disabled for this turret; ground mode is unaffected.");
            if (turret.Faction == Faction.OfPlayer)
                Messages.Message("DeltaV_AutoWeaponNoCapturableProjectile".Translate(turret.LabelShortCap),
                    turret, MessageTypeDefOf.CautionInput, false);
        }

        internal static void NotifyNativeCustomFlight(CompSpaceWeapon weapon, Verse.Projectile projectile)
        {
            if (weapon?.parent == null || projectile == null) return;
            Log.WarningOnce($"[DeltaV] Adapted turret '{weapon.parent.def.defName}' launched "
                + $"'{projectile.def.defName}' ({projectile.GetType().FullName}), which manages its own flight. "
                + "In space it flies a plain ballistic and only its Impact is replayed.",
                Gen.HashCombineInt(weapon.parent.thingIDNumber, projectile.def.shortHash));
        }

        internal static bool TryLaunchNative(Building_Turret turret, SpaceWeaponShot shot)
        {
            Verb verb = turret?.AttackVerb;
            CompSpaceWeapon weapon = shot?.weapon;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (verb == null || weapon?.parent != turret || universe == null || turret.Map == null)
                return false;

            Vector2 direction = SpaceTransform.Rotate(Vector2.right, shot.launchMapAngle);
            if (!TryProtocolCell(turret, verb, shot.launchMapAngle, out IntVec3 target))
            {
                universe.Projectiles.CancelNativeFire(verb, weapon);
                return false;
            }
            int warmup = Mathf.CeilToInt(Mathf.Max(0f,
                turret.def.building?.turretBurstWarmupTime.Average ?? 0f) * 60f);
            int duration = Mathf.Max(120, warmup + verb.verbProps.burstShotCount
                * Mathf.Max(1, verb.verbProps.ticksBetweenBurstShots) + 60);
            Vector3 placeholder = target.ToVector3Shifted();
            universe.Projectiles.AuthorizeNativeFire(verb, weapon, turret.Map, direction,
                Mathf.Max(weapon.EffectiveRange, verb.EffectiveRange),
                new Vector2(placeholder.x, placeholder.z), verb.EffectiveRange, duration);

            MethodInfo orderAttack = turret.GetType().GetMethod(nameof(Building_TurretGun.OrderAttack),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(LocalTargetInfo) }, null);
            if (orderAttack != null)
            {
                try
                {
                    orderAttack.Invoke(turret, new object[] { new LocalTargetInfo(target) });
                    bool accepted = turret.CurrentTarget.IsValid || verb.state == VerbState.Bursting;
                    if (accepted && turret is Building_TurretGun gun)
                        universe.Projectiles.SetNativeWarmupDeadline(verb, BurstWarmup(gun));
                    if (!accepted) universe.Projectiles.CancelNativeFire(verb, weapon);
                    return accepted;
                }
                catch (TargetInvocationException ex)
                {
                    Log.Error($"[DeltaV] Adapted turret could not enter its native firing cycle: {ex.InnerException ?? ex}");
                }
            }

            bool started = verb.TryStartCastOn(target, surpriseAttack: false,
                canHitNonTargetPawns: true, preventFriendlyFire: false,
                nonInterruptingSelfCast: false);
            if (!started) universe.Projectiles.CancelNativeFire(verb, weapon);
            return started;
        }

        /// <summary>
        /// Rebuilds a fire authorization from the turret's live state when a source Verb
        /// reaches its cast gate after the original grant expired during a long third-party
        /// warmup or head sweep. Space-mode current targets only ever come from fire control,
        /// so this can never conjure a shot the universe did not intend.
        /// </summary>
        internal static bool ReauthorizeNativeCast(Building_Turret turret, Verb verb)
        {
            CompSpaceWeapon weapon = turret?.TryGetComp<CompSpaceWeapon>();
            CompSpaceWeaponTraverse traverse = turret?.TryGetComp<CompSpaceWeaponTraverse>();
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (weapon?.parent != turret || traverse == null || universe == null || turret.Map == null
                || verb == null || verb.state == VerbState.Bursting || weapon.nativeLaunchUnsupported
                || !turret.CurrentTarget.IsValid) return false;
            Vector3 placeholder = turret.CurrentTarget.Cell.ToVector3Shifted();
            int warmup = Mathf.CeilToInt(Mathf.Max(0f,
                turret.def.building?.turretBurstWarmupTime.Average ?? 0f) * 60f);
            int duration = Mathf.Max(120, warmup + verb.verbProps.burstShotCount
                * Mathf.Max(1, verb.verbProps.ticksBetweenBurstShots) + 60);
            universe.Projectiles.AuthorizeNativeFire(verb, weapon, turret.Map,
                SpaceTransform.Rotate(Vector2.right, traverse.CurrentAngle),
                Mathf.Max(weapon.EffectiveRange, verb.EffectiveRange),
                new Vector2(placeholder.x, placeholder.z), verb.EffectiveRange, duration);
            if (turret is Building_TurretGun gun)
                universe.Projectiles.SetNativeWarmupDeadline(verb, BurstWarmup(gun));
            return true;
        }

        private static bool TryProtocolCell(Building_Turret turret, Verb verb,
            float launchMapAngle, out IntVec3 target)
        {
            Map map = turret.Map;
            target = IntVec3.Invalid;
            if (map == null) return false;
            int limit = Mathf.Min(Mathf.CeilToInt(Mathf.Max(1f, verb.EffectiveRange)),
                Mathf.CeilToInt(Mathf.Sqrt(map.Size.x * map.Size.x + map.Size.z * map.Size.z)) + 1);
            Vector2 origin = new Vector2(turret.Position.x + .5f, turret.Position.z + .5f);
            IntVec3 fallback = IntVec3.Invalid;
            // The cell is a bearing proxy for the universe aim ray so self-aiming turret heads
            // turn toward the real target. Distance only ranks candidates: source range bands
            // come first, but map edges or a long source minimum range never refuse the shot.
            for (int spoke = 0; spoke <= 16; spoke++)
            {
                float spread = spoke * (Mathf.PI / 8f);
                for (int side = 0; side < (spoke == 0 ? 1 : 2); side++)
                {
                    Vector2 ray = SpaceTransform.Rotate(Vector2.right,
                        launchMapAngle + (side == 0 ? spread : -spread));
                    for (int distance = limit; distance >= 1; distance--)
                    {
                        Vector2 point = origin + ray * distance;
                        IntVec3 cell = new IntVec3(Mathf.FloorToInt(point.x), 0,
                            Mathf.FloorToInt(point.y));
                        if (!cell.InBounds(map) || cell == turret.Position) continue;
                        float cellDistance = (cell - turret.Position).LengthHorizontal;
                        var localTarget = new LocalTargetInfo(cell);
                        if (cellDistance >= verb.verbProps.EffectiveMinRange(localTarget, turret)
                            && cellDistance <= verb.EffectiveRange)
                        {
                            target = cell;
                            return true;
                        }
                        if (!fallback.IsValid) fallback = cell;
                    }
                }
            }
            if (!fallback.IsValid) return false;
            target = fallback;
            return true;
        }

        internal static void PrepareAuthorizedTick(Building_Turret turret)
        {
            // Only Building_TurretGun exposes the warmup field Delta-V drives back; other
            // source implementations keep their own warmup state untouched.
            if (turret is not Building_TurretGun gun || !SpaceMode(gun)) return;
            SpaceProjectileService projectiles = Current.Game?.GetComponent<UniverseManager>()?.Projectiles;
            if (projectiles == null || !projectiles.TryGetNativeWarmupRemaining(
                gun.AttackVerb, out int remaining)) return;
            if (remaining >= 0 && gun.AttackVerb.state != VerbState.Bursting
                && gun.CurrentTarget.IsValid)
                BurstWarmup(gun) = Mathf.Max(1, remaining);
        }

        internal static void RestoreAuthorizedTraverse(Building_Turret turret)
        {
            if (turret is not Building_TurretGun gun || !SpaceMode(gun)
                || gun.TryGetComp<CompSpaceWeaponTraverse>() is not { } traverse) return;
            gun.Top.CurRotation = 90f - traverse.CurrentAngle * Mathf.Rad2Deg;
        }

        internal readonly struct SelectionOverlayState
        {
            internal readonly bool hidden;
            internal readonly LocalTargetInfo forcedTarget;
            internal readonly LocalTargetInfo currentTarget;
            internal readonly int warmupTicks;

            internal SelectionOverlayState(LocalTargetInfo forcedTarget,
                LocalTargetInfo currentTarget, int warmupTicks)
            {
                hidden = true;
                this.forcedTarget = forcedTarget;
                this.currentTarget = currentTarget;
                this.warmupTicks = warmupTicks;
            }
        }

        internal static SelectionOverlayState HideProtocolTargetForSelectionOverlay(
            Building_Turret turret)
        {
            // The vanilla field swap only exists for Building_TurretGun. Other source
            // implementations keep their native overlays; with bearing-aligned protocol
            // cells those visuals point at the real target instead of misleading.
            if (turret is not Building_TurretGun gun || !SpaceMode(gun)) return default;
            var state = new SelectionOverlayState(ForcedTarget(gun), CurrentTarget(gun),
                BurstWarmup(gun));
            // Protocol cells exist only to drive the source firing transaction. Selection
            // overlays must not expose their arbitrary local position as an aim direction.
            ForcedTarget(gun) = LocalTargetInfo.Invalid;
            CurrentTarget(gun) = LocalTargetInfo.Invalid;
            BurstWarmup(gun) = 0;
            return state;
        }

        internal static void RestoreProtocolTargetAfterSelectionOverlay(Building_Turret turret,
            SelectionOverlayState state)
        {
            if (!state.hidden || turret is not Building_TurretGun gun) return;
            ForcedTarget(gun) = state.forcedTarget;
            CurrentTarget(gun) = state.currentTarget;
            BurstWarmup(gun) = state.warmupTicks;
        }

        internal static void DrawNativeWarmupIndicator(Building_TurretGun turret)
        {
            if (!SpaceMode(turret)
                || turret.TryGetComp<CompSpaceWeapon>() is not { } weapon
                || turret.TryGetComp<CompSpaceWeaponTraverse>() is not { } traverse) return;
            SpaceProjectileService projectiles = Current.Game?.GetComponent<UniverseManager>()?.Projectiles;
            if (projectiles == null || !projectiles.TryGetNativeWarmupProgress(
                turret.AttackVerb, out int remaining, out float progress) || remaining <= 0) return;

            float eased = progress * progress * (3f - 2f * progress);
            int period = Mathf.Max(3, Mathf.RoundToInt(Mathf.Lerp(36f, 3f, eased)));
            int onTicks = Mathf.Clamp(Mathf.CeilToInt(period * Mathf.Lerp(.22f, .92f, eased)),
                1, period);
            if (Find.TickManager.TicksGame % period >= onTicks) return;

            Vector2 direction = SpaceTransform.Rotate(Vector2.right, traverse.CurrentAngle);
            Vector2 muzzle = SpaceWeaponGeometry.MuzzleMapPointAtAngle(weapon, traverse.CurrentAngle);
            Vector2 end = muzzle + direction * 4f;
            float altitude = AltitudeLayer.MetaOverlays.AltitudeFor();
            var start3 = new Vector3(muzzle.x, altitude, muzzle.y);
            var end3 = new Vector3(end.x, altitude, end.y);
            warmupIndicatorGlow ??= MaterialPool.MatFrom(GenDraw.LineTexPath,
                ShaderDatabase.Transparent, new Color(1f, .08f, .03f, .72f));
            warmupIndicatorCore ??= MaterialPool.MatFrom(GenDraw.LineTexPath,
                ShaderDatabase.Transparent, new Color(1f, .78f, .62f, 1f));
            GenDraw.DrawLineBetween(start3, end3, warmupIndicatorGlow, .32f);
            GenDraw.DrawLineBetween(start3, end3, warmupIndicatorCore, .10f);
        }

        public static void NotifyEngagementModeChanged(CompSpaceWeapon weapon)
        {
            if (weapon?.parent is Building_SpaceTurretGun native)
            {
                native.NotifyEngagementModeChanged();
                return;
            }
            if (weapon?.parent is not Building_Turret turret || !IsAdapted(turret)) return;

            if (turret is not Building_TurretGun gun)
            {
                if (weapon.GroundModeEnabled) turret.AttackVerb?.Reset();
                return;
            }

            int now = Find.TickManager.TicksGame;
            if (weapon.GroundModeEnabled)
            {
                BurstCooldown(gun) = weapon.Tuning != null
                    ? weapon.CooldownTicksLeft
                    : Math.Max(BurstCooldown(gun), weapon.CooldownTicksLeft);
                CurrentTarget(gun) = LocalTargetInfo.Invalid;
                BurstWarmup(gun) = 0;
                return;
            }

            if (turret.TryGetComp<CompSpaceWeaponTraverse>() is { } traverse)
            {
                TraverseAngle(traverse) = SpaceTransform.WrapAngle(
                    (90f - gun.Top.CurRotation) * Mathf.Deg2Rad);
                TraverseTick(traverse) = -1;
            }
            int cooldown = Math.Max(1, Math.Max(BurstCooldown(gun), weapon.CooldownTicksLeft));
            Verb attackVerb = gun.AttackVerb;
            if (attackVerb?.state == VerbState.Bursting)
            {
                cooldown = Math.Max(cooldown,
                    gun.def.building.turretBurstCooldownTime.SecondsToTicks());
                attackVerb.Reset();
            }
            if (weapon.Tuning != null)
                weapon.Tuning.CommitShot(0f, cooldown);
            else
                weapon.cooldownUntilTick = Math.Max(weapon.cooldownUntilTick, now + cooldown);
            gun.OrderAttack(LocalTargetInfo.Invalid);
            CurrentTarget(gun) = LocalTargetInfo.Invalid;
            BurstWarmup(gun) = 0;
        }
    }
}
