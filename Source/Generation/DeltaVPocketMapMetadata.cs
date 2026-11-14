using Verse;

namespace DeltaV
{
    /// <summary>
    /// 持久记录真实口袋图的舱外作业用途。
    /// </summary>
    public sealed class DeltaVPocketMapMetadata : CustomMapComponent
    {
        public bool worksite;

        public DeltaVPocketMapMetadata(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref worksite, "worksite", false);
        }
    }
}
