using RimWorld;
using Verse;

namespace DeltaV
{
    public class CompProperties_RailgunCapacitor : CompProperties_SpaceWeaponAttachment
    {
        public int capacity = 3;
        public int rechargeTicks = 4800;
        public float chargingWatts = 1000f;

        public CompProperties_RailgunCapacitor()
        {
            compClass = typeof(CompRailgunCapacitor);
        }
    }

    /// <summary>加速段侧挂电容：每枚仅储三次该段脉冲，缓慢从原版电网回充。</summary>
    public class CompRailgunCapacitor : CompSpaceWeaponAttachment
    {
        private int charges = -1;
        private int rechargeProgress;
        private CompProperties_RailgunCapacitor CapacitorProps => (CompProperties_RailgunCapacitor)props;
        public int Charges => charges < 0 ? CapacitorProps.capacity : charges;
        public bool HasShot => Enabled && Charges > 0;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (charges < 0) charges = CapacitorProps.capacity;
        }

        public override void CompTick()
        {
            base.CompTick();
            CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
            if (power == null) return;
            bool charging = Charges < CapacitorProps.capacity && Enabled;
            power.PowerOutput = charging ? -CapacitorProps.chargingWatts : 0f;
            if (!charging || !power.PowerOn || power.PowerNet == null) return;
            if (power.PowerNet.CurrentStoredEnergy()
                + power.PowerNet.CurrentEnergyGainRate() < -0.0001f) return;
            if (++rechargeProgress < CapacitorProps.rechargeTicks) return;
            rechargeProgress = 0;
            charges++;
        }

        public void ConsumeShot()
        {
            if (Charges <= 0) return;
            charges--;
            CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
            if (power != null) power.PowerOutput = -CapacitorProps.chargingWatts;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref charges, "charges", -1);
            Scribe_Values.Look(ref rechargeProgress, "rechargeProgress");
        }

        public override string CompInspectStringExtra()
        {
            return "DeltaV_RailCapacitorCharges".Translate(Charges, CapacitorProps.capacity);
        }
    }
}
