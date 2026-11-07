using RimWorld;
using Verse;

namespace DeltaV
{
    public class CompProperties_SpaceWeaponCapacitor : CompProperties_SpaceWeaponAttachment
    {
        public CompProperties_SpaceWeaponCapacitor()
        {
            compClass = typeof(CompSpaceWeaponCapacitor);
        }
    }

    /// <summary>武器挂件只负责直供接口；储能、充放电和电网模式由同建筑的电池 Comp 管理。</summary>
    public class CompSpaceWeaponCapacitor : CompSpaceWeaponAttachment
    {
        private CompSwitchableBattery Battery => parent.TryGetComp<CompSwitchableBattery>();

        public override float AvailableShotEnergy => Battery?.StoredEnergy ?? 0f;
        public override CompPowerBattery ShotEnergyBattery => Battery;

        public override bool CanSupplyShotEnergy(float wattDays) => Enabled && wattDays > 0f
            && Battery?.CanSupply(wattDays) == true;

        public override void ConsumeShotEnergy(float wattDays) => Battery?.Consume(wattDays);
    }
}
