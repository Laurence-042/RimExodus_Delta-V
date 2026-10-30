using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    // The ground projectile freezes the same load and effect budget as the universe shot.
    public sealed class GunImpactSnapshot : IExposable
    {
        public float damage, penetration, speed, duration = 1f;
        public void Capture(Thing launcher, Thing equipment)
        {
            var weapon = launcher.TryGetComp<CompSpaceWeapon>() ?? equipment.TryGetComp<CompSpaceWeapon>();
            if (weapon == null) return;
            var magazine = weapon.SelectMagazine();
            var projectile = magazine?.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef;
            var part = SpaceImpactEffects.Resolve(projectile, ThingDefOf.Wall)?.defenderEffect;
            damage = SpaceWeaponBallistics.ExpectedDamage(weapon, projectile);
            penetration = part == null ? 0f : part.armorPenetration * damage / Mathf.Max(.001f, part.damage)
                * (magazine?.MagazineProps.armorPenetrationScale ?? 1f);
            speed = SpaceWeaponBallistics.BaseSpeed(weapon, magazine, SpaceWeaponService.ProjectileSpeedOf(projectile));
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
