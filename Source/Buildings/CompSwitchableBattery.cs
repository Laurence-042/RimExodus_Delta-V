using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public class CompProperties_SwitchableBattery : CompProperties_Power
    {
        public float capacityWattDays;
        public float chargingWatts;
        public float initialEnergyPercent;
        public float maxOutputWatts;

        public CompProperties_SwitchableBattery()
        {
            compClass = typeof(CompSwitchableBattery);
        }

        public override IEnumerable<StatDrawEntry> SpecialDisplayStats(StatRequest req)
        {
            yield return SpaceCombatStats.Entry("DeltaV_StatCapCapacity",
                SpaceCombatStats.WattDays(capacityWattDays), 5100);
            yield return SpaceCombatStats.Entry("DeltaV_StatCapChargingPower",
                SpaceCombatStats.Watts(chargingWatts), 5090);
            int ticks = chargingWatts > 0f
                ? Mathf.CeilToInt(capacityWattDays / (chargingWatts * CompPower.WattsToWattDaysPerTick))
                : 0;
            yield return SpaceCombatStats.Entry("DeltaV_StatCapFullCharge",
                SpaceCombatStats.Ticks(ticks), 5080);
        }
    }

    /// <summary>局部储能通过正负 PowerOutput 在充电和向电网发电之间切换。</summary>
    public class CompSwitchableBattery : CompPowerTrader
    {
        private float storedEnergy = -1f;
        private bool gridOutputEnabled;
        private int outputWatts;

        public new CompProperties_SwitchableBattery Props => (CompProperties_SwitchableBattery)props;
        public float StoredEnergy => storedEnergy < 0f
            ? Props.capacityWattDays * Mathf.Clamp01(Props.initialEnergyPercent) : storedEnergy;

        public bool CanSupply(float wattDays) => wattDays > 0f
            && StoredEnergy + 0.0001f >= wattDays;

        public void SetStoredEnergyPct(float pct)
        {
            storedEnergy = Props.capacityWattDays * Mathf.Clamp01(pct);
            RefreshPowerOutput();
        }

        public void Consume(float wattDays)
        {
            storedEnergy = Mathf.Max(0f, StoredEnergy - wattDays);
            RefreshPowerOutput();
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (storedEnergy < 0f)
                storedEnergy = Props.capacityWattDays * Mathf.Clamp01(Props.initialEnergyPercent);
            if (outputWatts <= 0) outputWatts = Mathf.RoundToInt(Props.maxOutputWatts);
            outputWatts = Mathf.Clamp(outputWatts, 1,
                Mathf.Max(1, Mathf.RoundToInt(Props.maxOutputWatts)));
            RefreshPowerOutput();
        }

        public override void CompTick()
        {
            base.CompTick();
            RefreshPowerOutput();
            if (!PowerOn || PowerNet == null
                || parent.Map.gameConditionManager.ElectricityDisabled(parent.Map)) return;
            if (PowerOutput > 0f)
                storedEnergy = Mathf.Max(0f, StoredEnergy
                    - PowerOutput * CompPower.WattsToWattDaysPerTick);
            else if (PowerOutput < 0f && GridCanPayForCharge())
                storedEnergy = Mathf.Min(Props.capacityWattDays, StoredEnergy
                    - PowerOutput * CompPower.WattsToWattDaysPerTick);
            // PowerNetTick runs after CompTick and uses this tick's advertised output.
        }

        private bool GridCanPayForCharge()
        {
            float stored = PowerNet.CurrentStoredEnergy();
            float usable = PowerNet.batteryComps.Count == 0 || stored < 0.1f
                ? stored : stored - 5f;
            return usable + PowerNet.CurrentEnergyGainRate() >= -0.0000001f;
        }

        private void RefreshPowerOutput()
        {
            if (gridOutputEnabled)
            {
                if (PowerNet != null && StoredEnergy > 0f && !parent.IsBrokenDown()
                    && FlickUtility.WantsToBeOn(parent)) PowerOn = true;
                PowerOutput = PowerNet == null ? 0f : Mathf.Min(outputWatts,
                    StoredEnergy / CompPower.WattsToWattDaysPerTick);
            }
            else
            {
                PowerOutput = StoredEnergy + 0.0001f < Props.capacityWattDays
                    ? -Props.chargingWatts : 0f;
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref storedEnergy, "storedEnergy", -1f);
            Scribe_Values.Look(ref gridOutputEnabled, "gridOutputEnabled", false);
            Scribe_Values.Look(ref outputWatts, "gridOutputWatts", 0);
        }

        public override string CompInspectStringExtra()
        {
            string stored = "DeltaV_BatteryStored".Translate(
                SpaceCombatStats.EnergyAmount(StoredEnergy),
                SpaceCombatStats.EnergyAmount(Props.capacityWattDays));
            string power = base.CompInspectStringExtra();
            return power.NullOrEmpty() ? stored : stored + "\n" + power;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra()) yield return gizmo;
            if (DebugSettings.ShowDevGizmos)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Fill",
                    action = () => SetStoredEnergyPct(1f),
                };
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Empty",
                    action = () => SetStoredEnergyPct(0f),
                };
            }
            if (parent.Faction != Faction.OfPlayer && !DebugSettings.godMode) yield break;
            yield return new Command_Toggle
            {
                defaultLabel = "DeltaV_BatteryGridOutput".Translate(),
                defaultDesc = "DeltaV_BatteryGridOutputTip".Translate(),
                icon = TexCommand.Install,
                isActive = () => gridOutputEnabled,
                toggleAction = () =>
                {
                    gridOutputEnabled = !gridOutputEnabled;
                    RefreshPowerOutput();
                },
            };
            if (!gridOutputEnabled) yield break;
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_BatteryOutputWatts".Translate(outputWatts),
                defaultDesc = "DeltaV_BatteryOutputWattsTip".Translate(),
                icon = TexCommand.Install,
                action = () => Find.WindowStack.Add(new Dialog_Slider(
                    value => "DeltaV_BatteryOutputWatts".Translate(value),
                    1, Mathf.Max(1, Mathf.RoundToInt(Props.maxOutputWatts)),
                    value =>
                    {
                        outputWatts = value;
                        RefreshPowerOutput();
                    }, outputWatts)),
            };
        }
    }
}
