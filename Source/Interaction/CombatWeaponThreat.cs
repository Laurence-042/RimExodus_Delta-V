using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    // A weapon's reachable firing region is the maneuver planner's threat source.
    // Projectile instances are deliberately absent: the estimate is stable between volleys.
    internal readonly struct CombatWeaponThreat
    {
        internal Vector3 SourcePosition => position;
        private readonly Vector3 position;
        private readonly Vector2 velocity;
        private readonly float shipAngle, initialShipAngle, shipTurnReach, shipTurnRate;
        private readonly float mountAngle, halfArc, turretAngle, turretRate;
        private readonly float minRange, range, speed, spreadSigmaRad;
        private readonly float horizon;
        private readonly float cooldownSeconds, damagePerSecond;
        private readonly bool laser, guided, turret;

        private CombatWeaponThreat(SpacePose pose, float turnReach, float turnRate,
            CompSpaceWeapon weapon, ThingDef projectile, int skill, float damage,
            float projectileSpeed, float horizon)
        {
            position = OrbitSphere.Move(pose.position, pose.velocity * horizon);
            this.horizon = horizon;
            velocity = pose.velocity;
            initialShipAngle = pose.angle;
            shipAngle = SpaceTransform.WrapAngle(pose.angle + pose.angularVelocity * horizon);
            shipTurnReach = turnReach;
            shipTurnRate = turnRate;
            weapon.CoverageSector(out float sectorCenter, out float sectorArc);
            mountAngle = sectorCenter;
            CompSpaceWeaponTraverse traverse = weapon.parent.TryGetComp<CompSpaceWeaponTraverse>();
            turret = traverse != null;
            turretAngle = traverse?.CurrentAngle ?? mountAngle;
            turretRate = traverse?.TraverseProps.turnRateDegPerSec * Mathf.Deg2Rad ?? 0f;
            float arc = sectorArc;
            if (traverse != null) arc = Mathf.Min(arc, traverse.TraverseProps.mechanicalArcDeg);
            halfArc = Mathf.Clamp(arc, 0f, 360f) * 0.5f * Mathf.Deg2Rad;
            minRange = weapon.WeaponProps.minRange;
            range = weapon.EffectiveRange;
            CompProperties_SpaceProjectile props = projectile.GetCompProperties<CompProperties_SpaceProjectile>();
            laser = props?.laserRange > 0f;
            guided = props?.guided == true;
            speed = laser ? 0f : projectileSpeed;
            spreadSigmaRad = 0.4f * weapon.WeaponProps.baseSpreadDeg
                * SpaceWeaponService.SpreadFactorForSkill(skill) * Mathf.Deg2Rad;
            cooldownSeconds = weapon.CooldownTicksLeft / 60f;
            damagePerSecond = damage * 60f
                / Mathf.Max(1, weapon.ShotInterval);
        }

        internal static void Capture(ShipBase enemy, SpaceObject frame, float horizon,
            List<CombatWeaponThreat> destination)
        {
            if (enemy?.IsValid != true || frame == null) return;
            var control = Current.Game?.GetComponent<UniverseManager>()?.Controls.ExistingFor(enemy)?.FireControl;
            if (control != null && Mathf.Abs(horizon - 3f) < .001f)
                destination.AddRange(control.Threats(enemy, frame));
            else CaptureRaw(enemy, frame, horizon, destination);
        }

        internal static void CaptureRaw(ShipBase enemy, SpaceObject frame, float horizon,
            List<CombatWeaponThreat> destination)
        {
            if (enemy?.IsValid != true || frame == null) return;
            frame.TryGetDrive(enemy.Identity, out DriveInfo drive);
            float angular = drive == null ? 0f
                : Mathf.Max(drive.PreviewAngular(-1), drive.PreviewAngular(1));
            // The known angular velocity is already included in the future pose.
            float turnReach = 0.35f * angular * horizon * horizon;
            float turnRate = Mathf.Abs(frame.pose.angularVelocity) + angular * horizon * 0.5f;
            foreach (CompSpaceWeapon weapon in enemy.Weapons)
            {
                if (weapon?.parent?.Spawned != true || !weapon.Armed || !weapon.SpaceModeEnabled
                    || weapon.WeaponProps.manualOnly || !weapon.GroupActive
                    || weapon.aimMode != SpaceWeaponAimMode.Volley
                    || !weapon.Operational
                    || !weapon.TryGetSkillLevel(out int skill)) continue;
                CompSpaceWeaponMagazine magazine = weapon.SelectMagazine();
                ThingDef projectile = magazine?.SelectedProjectileDef
                    ?? weapon.WeaponProps.projectileDef;
                if (projectile?.GetCompProperties<CompProperties_SpaceProjectile>() == null
                    || !weapon.MagazineAvailable(magazine)) continue;
                float projectileSpeed = SpaceWeaponBallistics.ProjectedSpeed(weapon,
                    SpaceWeaponService.ProjectileSpeedOf(projectile));
                if (!weapon.QueryEnergy.ready) continue;
                float damage = SpaceWeaponBallistics.ExpectedDamage(weapon, projectile);
                if (damage <= 0f || weapon.WeaponProps.range <= 0f) continue;
                SpacePose muzzlePose = SpaceTransform.RigidPointPose(frame.pose,
                    SpaceTransform.MapToShipLocal(enemy.Map, SpaceWeaponGeometry.MuzzleMapPoint(weapon)), 0f);
                destination.Add(new CombatWeaponThreat(muzzlePose, turnReach, turnRate,
                    weapon, projectile, skill, damage, projectileSpeed, horizon));
            }
        }

        internal void Score(Vector3 targetPosition, Vector2 targetVelocity,
            Vector2 targetHalfExtents, float targetAngle, Vector2 acceleration,
            out float damageRate, out float firstImpactSeconds)
        {
            damageRate = 0f;
            firstImpactSeconds = 8f;
            Vector2 offset = OrbitSphere.Offset(position, targetPosition);
            float distance = offset.magnitude;
            if (distance < minRange || distance > range || distance < 0.01f) return;
            float edge = Mathf.Max(2f, range * 0.04f);
            float rangeFactor = Mathf.Min(1f, Mathf.Min((distance - minRange) / edge,
                (range - distance) / edge));
            if (rangeFactor <= 0f) return;
            float desiredMapAngle = SpaceTransform.WrapAngle(
                Mathf.Atan2(offset.y, offset.x) - shipAngle);
            float arcError = Mathf.Max(0f,
                Mathf.Abs(SpaceTransform.WrapAngle(desiredMapAngle - mountAngle)) - halfArc);
            float arcFactor = ReachFactor(arcError, shipTurnReach);
            if (arcFactor <= 0f) return;
            float initialMapAngle = SpaceTransform.WrapAngle(
                Mathf.Atan2(offset.y, offset.x) - initialShipAngle);
            float initialArcError = Mathf.Max(0f,
                Mathf.Abs(SpaceTransform.WrapAngle(initialMapAngle - mountAngle)) - halfArc);
            float turnDelay = initialArcError / Mathf.Max(0.01f, shipTurnRate);
            if (turret)
            {
                float turretError = Mathf.Abs(SpaceTransform.WrapAngle(desiredMapAngle - turretAngle));
                arcFactor *= ReachFactor(turretError, turretRate * horizon);
                turnDelay = Mathf.Max(turnDelay, turretError / Mathf.Max(0.01f, turretRate));
            }
            if (arcFactor <= 0f) return;
            Vector2 direction = offset / distance;
            Vector2 lateral = new Vector2(-direction.y, direction.x);
            Vector2 localLateral = SpaceTransform.RotateInverse(lateral, targetAngle);
            float targetRadius = Mathf.Abs(localLateral.x) * targetHalfExtents.x
                + Mathf.Abs(localLateral.y) * targetHalfExtents.y + 0.71f;
            float radialAway = Vector2.Dot(targetVelocity - velocity, direction);
            float flight = laser ? 0f : distance / Mathf.Max(5f, speed - radialAway);
            float lateralAcceleration = Mathf.Abs(Vector2.Dot(acceleration, lateral));
            float dodgeTime = Mathf.Min(flight, horizon);
            float lateralShift = guided ? 0f
                : 0.5f * lateralAcceleration * dodgeTime * dodgeTime;
            float sigma = Mathf.Max(0.5f, distance * spreadSigmaRad);
            float hit = guided ? 1f / (1f + 0.15f * lateralAcceleration)
                : HitProbability(targetRadius, sigma, lateralShift);
            float availability = 0.8f + 0.2f / (1f + cooldownSeconds / 3f);
            damageRate = damagePerSecond * rangeFactor * arcFactor * hit * availability;
            firstImpactSeconds = Mathf.Min(8f,
                Mathf.Max(cooldownSeconds, turnDelay) + flight);
        }

        internal static float ReachFactor(float error, float reach)
        {
            if (error <= 0f) return 1f;
            return reach <= 0f ? 0f : Mathf.Clamp01(1f - error / reach);
        }

        internal static float HitProbability(float radius, float sigma, float lateralShift)
        {
            float spread = Mathf.Max(0.5f, sigma);
            float staticHit = 1f - Mathf.Exp(-0.5f * radius * radius / (spread * spread));
            float beyond = Mathf.Max(0f, lateralShift - radius);
            return staticHit * Mathf.Exp(-0.5f * beyond * beyond / (spread * spread));
        }

        internal static Vector2 HullHalfExtents(ShipBase ship)
        {
            Vector2 center = ship.HullCenter;
            CellRect bounds = ShipRegistry.HullBounds(ship);
            if (bounds.Area == 0) return new Vector2(0.5f, 0.5f);
            return new Vector2(
                Mathf.Max(0.5f, center.x - bounds.minX, bounds.maxX + 1f - center.x),
                Mathf.Max(0.5f, center.y - bounds.minZ, bounds.maxZ + 1f - center.y));
        }
    }
}
