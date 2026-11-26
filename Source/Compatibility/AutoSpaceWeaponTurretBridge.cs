using System;
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
        public Material ShellTopMaterial => Turret?.TurretTopMaterial;
        public float ShellTopAngle => Turret?.CurrentEffectiveVerb?.AimAngleOverride
            ?? (Turret as Building_TurretGun)?.Top?.CurRotation ?? parent.Rotation.AsAngle;

        public bool CanFire(SpaceWeaponShot shot)
        {
            if (!AutoSpaceWeaponTurretBridge.IsNativeAdapted(parent.def)) return true;
            Verb verb = Turret?.AttackVerb;
            return shot?.weapon?.parent == parent && verb != null && verb.state != VerbState.Bursting
                && !Turret.CurrentTarget.IsValid && verb.Available();
        }

        public bool TryLaunch(SpaceWeaponShot shot) =>
            AutoSpaceWeaponTurretBridge.IsNativeAdapted(parent.def)
                ? AutoSpaceWeaponTurretBridge.TryLaunchNative(Turret, shot)
                : shot?.LaunchProjectile() == true;

        public bool OwnsCommit(SpaceWeaponShot shot) =>
            AutoSpaceWeaponTurretBridge.IsNativeAdapted(parent.def);
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
        private static readonly AccessTools.FieldRef<CompSpaceWeaponTraverse, float> TraverseAngle =
            AccessTools.FieldRefAccess<CompSpaceWeaponTraverse, float>("currentAngle");
        private static readonly AccessTools.FieldRef<CompSpaceWeaponTraverse, int> TraverseTick =
            AccessTools.FieldRefAccess<CompSpaceWeaponTraverse, int>("rotationTick");

        public static bool IsAdapted(ThingDef def) =>
            def?.GetModExtension<DefModExtension_AutoSpaceWeaponResolved>() != null;

        public static bool IsNativeAdapted(ThingDef def) =>
            def?.GetModExtension<DefModExtension_AutoSpaceWeaponResolved>()?.mode
                == AutoSpaceWeaponMode.NativeProjectile;

        public static bool IsAdapted(Building_Turret turret) => turret != null && IsAdapted(turret.def);

        public static bool SpaceMode(Building_Turret turret) => IsAdapted(turret)
            && turret.TryGetComp<CompSpaceWeapon>()?.SpaceModeEnabled == true;

        internal static bool TryLaunchNative(Building_Turret turret, SpaceWeaponShot shot)
        {
            Verb verb = turret?.AttackVerb;
            CompSpaceWeapon weapon = shot?.weapon;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (verb == null || weapon?.parent != turret || universe == null || turret.Map == null)
                return false;

            Vector2 direction = SpaceTransform.Rotate(Vector2.right, shot.launchMapAngle);
            if (!TryTargetCell(turret, verb, direction, out IntVec3 target))
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
                    return turret.CurrentTarget.IsValid || verb.state == VerbState.Bursting;
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

        private static bool TryTargetCell(Building_Turret turret, Verb verb, Vector2 direction,
            out IntVec3 target)
        {
            Map map = turret.Map;
            target = turret.Position;
            bool found = false;
            int limit = Mathf.Max(2, Mathf.Min(Mathf.CeilToInt(Mathf.Max(2f, verb.EffectiveRange)),
                Mathf.Max(map.Size.x, map.Size.z) * 2));
            Vector2 origin = new Vector2(turret.Position.x + .5f, turret.Position.z + .5f);
            for (int distance = 1; distance <= limit; distance++)
            {
                Vector2 point = origin + direction * distance;
                IntVec3 cell = new IntVec3(Mathf.FloorToInt(point.x), 0, Mathf.FloorToInt(point.y));
                if (!cell.InBounds(map)) break;
                float cellDistance = (cell - turret.Position).LengthHorizontal;
                var localTarget = new LocalTargetInfo(cell);
                if (cellDistance < verb.verbProps.EffectiveMinRange(localTarget, turret)
                    || cellDistance > verb.EffectiveRange) continue;
                target = cell;
                found = true;
            }
            return found;
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
