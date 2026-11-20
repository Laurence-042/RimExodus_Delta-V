using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    // The ground projectile freezes the load and the ground direct-hit budget.
    public sealed class GunImpactSnapshot : IExposable
    {
        public float damage, penetration, speed, duration = 1f;
        public void Capture(Thing launcher, Thing equipment)
        {
            var weapon = launcher.TryGetComp<CompSpaceWeapon>() ?? equipment.TryGetComp<CompSpaceWeapon>();
            if (weapon == null) return;
            if (weapon.parent is Building_SpaceTurretGun turret
                && turret.CurrentEffectiveVerb is Verb_SpaceTurretGround { LaunchImpact: { } impact })
            {
                damage = impact.damage;
                penetration = impact.penetration;
                speed = impact.speed;
                return;
            }
            Capture(weapon, weapon.SelectMagazine());
        }

        internal void Capture(CompSpaceWeapon weapon, CompSpaceWeaponMagazine magazine)
        {
            // The ground gun uses the wall direct-hit profile even for steel ammunition.
            var projectile = weapon.WeaponProps.armorPiercingProjectileDef
                ?? magazine?.ResolveProjectile(weapon) ?? weapon.WeaponProps.projectileDef;
            var part = SpaceImpactEffects.Resolve(projectile, ThingDefOf.Wall)?.defenderEffect;
            speed = SpaceWeaponBallistics.BaseSpeed(weapon, magazine, SpaceWeaponService.ProjectileSpeedOf(projectile));
            damage = SpaceWeaponBallistics.GroundDirectDamage(weapon, part, magazine, speed);
            penetration = part == null ? 0f : part.armorPenetration * damage / Mathf.Max(.001f, part.damage)
                * (magazine?.PenetrationScale(weapon) ?? 1f);
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref damage, "damage"); Scribe_Values.Look(ref penetration, "penetration");
            Scribe_Values.Look(ref speed, "speed"); Scribe_Values.Look(ref duration, "duration", 1f);
        }
    }

    public sealed class Projectile_TunedGun : Bullet
    {
        private GunImpactSnapshot shot = new GunImpactSnapshot();
        public override int DamageAmount => Mathf.RoundToInt(shot.damage);
        public override float ArmorPenetration => shot.penetration;
        public override Vector3 ExactPosition => origin.Yto0() + (destination - origin).Yto0()
            * Mathf.Clamp01(1f - ticksToImpact / shot.duration) + Vector3.up * def.Altitude;
        public override void Launch(Thing launcher, Vector3 origin, LocalTargetInfo usedTarget,
            LocalTargetInfo intendedTarget, ProjectileHitFlags hitFlags, bool preventFriendlyFire = false,
            Thing equipment = null, ThingDef targetCoverDef = null)
        {
            shot.Capture(launcher, equipment);
            base.Launch(launcher, origin, usedTarget, intendedTarget, hitFlags, preventFriendlyFire, equipment, targetCoverDef);
            shot.duration = Mathf.Max(1f, (destination - origin).magnitude * 60f / Mathf.Max(.01f, shot.speed));
            lifetime = ticksToImpact = Mathf.CeilToInt(shot.duration);
        }
        public override void ExposeData() { base.ExposeData(); Scribe_Deep.Look(ref shot, "gunImpact"); }
    }

    public sealed class Beam_TunedGun : Beam
    {
        private GunImpactSnapshot shot = new GunImpactSnapshot();
        public override int DamageAmount => Mathf.RoundToInt(shot.damage);
        public override float ArmorPenetration => shot.penetration;
        public override void Launch(Thing launcher, Vector3 origin, LocalTargetInfo usedTarget,
            LocalTargetInfo intendedTarget, ProjectileHitFlags hitFlags, bool preventFriendlyFire = false,
            Thing equipment = null, ThingDef targetCoverDef = null)
        {
            shot.Capture(launcher, equipment);
            base.Launch(launcher, origin, usedTarget, intendedTarget, hitFlags, preventFriendlyFire, equipment, targetCoverDef);
        }
        public override void ExposeData() { base.ExposeData(); Scribe_Deep.Look(ref shot, "gunImpact"); }
    }
}
