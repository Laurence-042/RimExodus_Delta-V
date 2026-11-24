using System;
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
    public sealed class CompAutoSpaceWeaponTurretBridge : ThingComp, IShellTurretTopSource
    {
        private Building_TurretGun Turret => parent as Building_TurretGun;
        public Material ShellTopMaterial => Turret?.TurretTopMaterial;
        public float ShellTopAngle => Turret?.CurrentEffectiveVerb?.AimAngleOverride
            ?? Turret?.Top?.CurRotation ?? parent.Rotation.AsAngle;
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

        public static bool IsAdapted(Building_TurretGun turret) => turret != null && IsAdapted(turret.def);

        public static bool SpaceMode(Building_TurretGun turret) => IsAdapted(turret)
            && turret.TryGetComp<CompSpaceWeapon>()?.SpaceModeEnabled == true;

        public static void NotifyEngagementModeChanged(CompSpaceWeapon weapon)
        {
            if (weapon?.parent is Building_SpaceTurretGun native)
            {
                native.NotifyEngagementModeChanged();
                return;
            }
            if (weapon?.parent is not Building_TurretGun turret || !IsAdapted(turret)) return;

            int now = Find.TickManager.TicksGame;
            if (weapon.GroundModeEnabled)
            {
                BurstCooldown(turret) = weapon.Tuning != null
                    ? weapon.CooldownTicksLeft
                    : Math.Max(BurstCooldown(turret), weapon.CooldownTicksLeft);
                CurrentTarget(turret) = LocalTargetInfo.Invalid;
                BurstWarmup(turret) = 0;
                return;
            }

            if (turret.TryGetComp<CompSpaceWeaponTraverse>() is { } traverse)
            {
                TraverseAngle(traverse) = SpaceTransform.WrapAngle(
                    (90f - turret.Top.CurRotation) * Mathf.Deg2Rad);
                TraverseTick(traverse) = -1;
            }
            int cooldown = Math.Max(1, Math.Max(BurstCooldown(turret), weapon.CooldownTicksLeft));
            Verb attackVerb = turret.AttackVerb;
            if (attackVerb?.state == VerbState.Bursting)
            {
                cooldown = Math.Max(cooldown,
                    turret.def.building.turretBurstCooldownTime.SecondsToTicks());
                attackVerb.Reset();
            }
            if (weapon.Tuning != null)
                weapon.Tuning.CommitShot(0f, cooldown);
            else
                weapon.cooldownUntilTick = Math.Max(weapon.cooldownUntilTick, now + cooldown);
            turret.OrderAttack(LocalTargetInfo.Invalid);
            CurrentTarget(turret) = LocalTargetInfo.Invalid;
            BurstWarmup(turret) = 0;
        }
    }
}
