using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    public class CompProperties_SpaceWeaponCapacitor : CompProperties_SpaceWeaponAttachment
    {
        public float capacityWattDays;
        public float chargingWatts;
        public float initialEnergyPercent = 1f;

        public CompProperties_SpaceWeaponCapacitor()
        {
            compClass = typeof(CompSpaceWeaponCapacitor);
        }

        public override IEnumerable<StatDrawEntry> SpecialDisplayStats(StatRequest req)
        {
            yield return SpaceCombatStats.Entry("DeltaV_StatCapCapacity",
                SpaceCombatStats.WattDays(capacityWattDays), 5100);
            yield return SpaceCombatStats.Entry("DeltaV_StatCapChargingPower",
                SpaceCombatStats.Watts(chargingWatts), 5090);
            int ticks = chargingWatts > 0f
                ? UnityEngine.Mathf.CeilToInt(capacityWattDays / (chargingWatts * CompPower.WattsToWattDaysPerTick))
                : 0;
            yield return SpaceCombatStats.Entry("DeltaV_StatCapFullCharge",
                SpaceCombatStats.Ticks(ticks), 5080);
        }
    }

    /// <summary>附件电容按实际 W·d 储能；炮塔与加速段共用，不消耗材料。</summary>
    public class CompSpaceWeaponCapacitor : CompSpaceWeaponAttachment
    {
        private float storedEnergy = -1f;
        public CompProperties_SpaceWeaponCapacitor CapacitorProps =>
            (CompProperties_SpaceWeaponCapacitor)props;
        public float StoredEnergy => storedEnergy < 0f
            ? CapacitorProps.capacityWattDays * CapacitorProps.initialEnergyPercent : storedEnergy;

        public bool CanSupply(float wattDays) => Enabled && wattDays > 0f
            && StoredEnergy + 0.0001f >= wattDays;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (storedEnergy < 0f) storedEnergy = CapacitorProps.capacityWattDays
                * UnityEngine.Mathf.Clamp01(CapacitorProps.initialEnergyPercent);
        }

        public override void CompTick()
        {
            base.CompTick();
            CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
            if (power == null) return;
            bool charging = Enabled && StoredEnergy + 0.0001f < CapacitorProps.capacityWattDays;
            power.PowerOutput = charging ? -CapacitorProps.chargingWatts : 0f;
            if (charging && power.PowerOn)
                storedEnergy = UnityEngine.Mathf.Min(CapacitorProps.capacityWattDays,
                    StoredEnergy + CapacitorProps.chargingWatts * CompPower.WattsToWattDaysPerTick);
        }

        public void Consume(float wattDays)
        {
            storedEnergy = UnityEngine.Mathf.Max(0f, StoredEnergy - wattDays);
            CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
            if (power != null) power.PowerOutput = -CapacitorProps.chargingWatts;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref storedEnergy, "storedEnergy", -1f);
        }

        public override string CompInspectStringExtra() =>
            "DeltaV_CapacitorStored".Translate(StoredEnergy.ToString("0.##"),
                CapacitorProps.capacityWattDays.ToString("0.##"));
    }
}
