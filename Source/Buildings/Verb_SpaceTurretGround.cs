using Verse;

namespace DeltaV
{
    /// <summary>原版同图射击 Verb；只让武器实例的地面模式决定其可用性。</summary>
    public sealed class Verb_SpaceTurretGround : Verb_Shoot
    {
        public override bool Available()
        {
            return caster is Building_SpaceTurretGun turret
                && turret.TryGetComp<CompSpaceWeapon>() is CompSpaceWeapon weapon
                && weapon.GroundModeEnabled && weapon.CooldownTicksLeft == 0
                && weapon.MagazineAvailable(weapon.SelectMagazine())
                && SpaceWeaponBallistics.HasBaseShotEnergy(weapon)
                && base.Available();
        }

        protected override bool TryCastShot()
        {
            if (!Available()) return false;
            var weapon = caster.TryGetComp<CompSpaceWeapon>();
            var magazine = weapon.SelectMagazine();
            int heads = magazine?.RequiredHeads(weapon) ?? 0;
            int charge = weapon.Tuning?.Settings.chemical == true ? weapon.Tuning.charge : 0;
            float heat = weapon.Tuning?.ShotHeat ?? 0f;
            int cycle = weapon.Tuning?.CycleTicks ?? weapon.ShotInterval;
            var energy = new SpaceWeaponEnergy();
            if (!energy.Evaluate(weapon, false) || !base.TryCastShot()) return false;
            energy.Commit();
            magazine?.ConsumeHeads(heads);
            if (charge > 0) caster.TryGetComp<CompSpaceWeaponMagazine>().ConsumePropellant(charge);
            weapon.NotifyMagazineConsumed(magazine);
            if (weapon.Tuning != null) weapon.Tuning.CommitShot(heat, cycle);
            else weapon.cooldownUntilTick = Find.TickManager.TicksGame + weapon.ShotInterval;
            return true;
        }
    }
}
