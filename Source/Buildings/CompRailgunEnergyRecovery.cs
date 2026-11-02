using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    public class CompProperties_RailgunEnergyRecovery : CompProperties_SpaceWeaponAttachment
    {
        public float reduction = 0.25f;
        public CompProperties_RailgunEnergyRecovery() => compClass = typeof(CompRailgunEnergyRecovery);

        public override IEnumerable<StatDrawEntry> SpecialDisplayStats(StatRequest req)
        {
            yield return SpaceCombatStats.Entry("DeltaV_StatEnergyRecovery", reduction.ToStringPercent(), 5100);
        }
    }

    public class CompRailgunEnergyRecovery : CompSpaceWeaponAttachment
    {
        public override float SegmentEnergyReduction => ((CompProperties_RailgunEnergyRecovery)props).reduction;
    }
}
