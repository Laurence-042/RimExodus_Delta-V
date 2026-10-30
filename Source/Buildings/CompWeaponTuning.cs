using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public class CompProperties_WeaponTuning : CompProperties
    {
        public bool chemical;
        public int maxCharge = 1, maxMass = 1, defaultCharge = 1, defaultMass = 1;
        public int minInterval = 12, maxInterval = 12, defaultInterval = 12;
        public int mechanicalTicks;
        public float coolingPerSecond = 10f, heatCapacity = 100f, heatPerShot = 5f;
        public bool lockEveryShot;
        public int maxPower = 1;
        public float barrelStart = .64f;
        public CompProperties_WeaponTuning() { compClass = typeof(CompWeaponTuning); }
        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string error in base.ConfigErrors(parentDef)) yield return error;
            if (defaultCharge < 1 || defaultCharge > maxCharge || defaultMass < 1 || defaultMass > maxMass
                || minInterval < 1 || defaultInterval < minInterval || defaultInterval > maxInterval
                || coolingPerSecond <= 0f || heatCapacity <= 0f || heatPerShot < 0f || maxPower < 1
                || lockEveryShot && mechanicalTicks < 1 || barrelStart <= 0f || barrelStart >= 1f)
                yield return $"{parentDef.defName}: invalid weapon tuning limits or cooling parameters";
        }
    }

    public class CompWeaponTuning : ThingComp
    {
        public CompProperties_WeaponTuning Settings => (CompProperties_WeaponTuning)props;
        public int charge = 1, mass = 1, interval = 12, power = 1;
        public float heat;
        public bool overheated;
        private int remainingTicks;
        private bool initialized;
        public int RemainingTicks => Math.Max(remainingTicks, overheated
            ? Mathf.CeilToInt(heat * 60f / Settings.coolingPerSecond) : 0);
        public int ShotInterval => Settings.lockEveryShot ? Math.Max(Settings.mechanicalTicks,
            Mathf.CeilToInt(ShotHeat * 60f / Settings.coolingPerSecond)) : interval;
        public float ShotHeat => Settings.lockEveryShot ? Settings.heatPerShot * (charge + mass) : Settings.heatPerShot;
        public float HeatFraction => Mathf.Clamp01(heat / Settings.heatCapacity);

        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            if (!initialized)
            { charge = Settings.defaultCharge; mass = Settings.defaultMass; interval = Settings.defaultInterval; initialized = true; }
        }

        public int CycleTicks => Settings.lockEveryShot ? Settings.mechanicalTicks : interval;
        public void CommitShot() => CommitShot(ShotHeat, CycleTicks);
        internal void CommitShot(float shotHeat, int cycleTicks)
        {
            heat += shotHeat;
            if (Settings.lockEveryShot || heat >= Settings.heatCapacity) overheated = true;
            remainingTicks = cycleTicks;
        }

        public override void CompTick()
        {
            AdvanceThermalTick();
            if (overheated && Settings.chemical && parent.IsHashIntervalTick(15))
            {
                var weapon = parent.TryGetComp<CompSpaceWeapon>();
                if (weapon?.Container != null) weapon.Universe.Projectiles.EmitGunCoolingSmoke(weapon);
                else if (parent.Spawned) FleckMaker.ThrowSmoke(parent.DrawPos, parent.Map, .7f);
            }
        }

        internal void AdvanceThermalTick()
        {
            if (remainingTicks > 0) remainingTicks--;
            if (heat <= 0f) return;
            heat = Mathf.Max(0f, heat - Settings.coolingPerSecond / 60f);
            if (heat <= 0f) overheated = false;
        }

        public override void PostExposeData()
        {
            Scribe_Values.Look(ref charge, "propellantCharge", Settings.defaultCharge);
            Scribe_Values.Look(ref mass, "headUnits", Settings.defaultMass);
            Scribe_Values.Look(ref interval, "firingInterval", Settings.defaultInterval);
            Scribe_Values.Look(ref power, "pulsePower", 1);
            Scribe_Values.Look(ref heat, "weaponHeat");
            Scribe_Values.Look(ref overheated, "thermalLock");
            Scribe_Values.Look(ref remainingTicks, "mechanicalTicksLeft");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                charge = Mathf.Clamp(charge, 1, Settings.maxCharge); mass = Mathf.Clamp(mass, 1, Settings.maxMass);
                interval = Mathf.Clamp(interval, Settings.minInterval, Settings.maxInterval);
                power = Mathf.Clamp(power, 1, Settings.maxPower);
            }
        }

        private void Changed()
        {
            var w = parent.TryGetComp<CompSpaceWeapon>();
            w?.NotifyParametersChanged();
            ShipControlService.NotifyGunnerChange(parent.Map);
        }

        private Gizmo Setting(string key, int current, int min, int max, Action<CompWeaponTuning, int> set, bool rate = false)
        {
            return new Command_BatchSetting<CompWeaponTuning>(key, this, selected =>
            {
                var options = new List<FloatMenuOption>();
                for (int i = min; i <= max; i++)
                {
                    int value = i;
                    options.Add(new FloatMenuOption(rate ? (60f / i).ToString("0.##") + "/s" : i.ToString(), () =>
                    { foreach (var item in selected) { set(item, value); item.Changed(); } }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }) { defaultLabel = key.Translate(rate ? (60f / current).ToString("0.##") : current.ToString()),
                defaultDesc = (key + "Tip").Translate(), icon = TexCommand.Install };
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent.Faction != Faction.OfPlayer && !DebugSettings.godMode) yield break;
            if (Settings.maxCharge > 1) yield return Setting("DeltaV_TuneCharge", charge, 1, Settings.maxCharge,
                (x, v) => x.charge = Mathf.Clamp(v, 1, x.Settings.maxCharge));
            if (Settings.maxMass > 1) yield return Setting("DeltaV_TuneMass", mass, 1, Settings.maxMass,
                (x, v) => x.mass = Mathf.Clamp(v, 1, x.Settings.maxMass));
            if (Settings.minInterval < Settings.maxInterval) yield return Setting("DeltaV_TuneRate", interval,
                Settings.minInterval, Settings.maxInterval,
                (x, v) => x.interval = Mathf.Clamp(v, x.Settings.minInterval, x.Settings.maxInterval), true);
            if (Settings.maxPower > 1) yield return Setting("DeltaV_TunePower", power, 1, Settings.maxPower,
                (x, v) => x.power = Mathf.Clamp(v, 1, x.Settings.maxPower));
        }

        public override string CompInspectStringExtra()
        {
            var weapon = parent.TryGetComp<CompSpaceWeapon>();
            string state = Settings.chemical ? "DeltaV_WeaponThermal".Translate(heat.ToString("0.0"),
                RemainingTicks / 60f, overheated ? "DeltaV_ThermalLocked".Translate() : "DeltaV_ThermalReady".Translate()).ToString() : "";
            if (Settings.chemical)
            {
                var selected = weapon?.SelectMagazine() ?? parent.TryGetComp<CompSpaceWeaponMagazine>();
                float actualMass = mass * (selected?.MagazineProps.unitMass ?? 1f);
                state += "\n" + "DeltaV_ShotLoad".Translate(charge, mass, actualMass.ToString("0.##"),
                    SpaceKinetics.LaunchSpeed(charge, actualMass).ToString("0.0"), (ShotInterval / 60f).ToString("0.0"));
            }
            if (Settings.maxPower > 1) state += (state.Length > 0 ? "\n" : "") + "DeltaV_PulseEnergy".Translate(weapon?.ShotEnergy ?? 0f);
            return state;
        }
    }
}
