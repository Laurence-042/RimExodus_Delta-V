using Verse;

namespace DeltaV
{
    public class PlaceWorker_TradeTerminal : PlaceWorker
    {
        public override bool IsBuildDesignatorVisible(BuildableDef def) => DebugSettings.godMode;

        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot,
            Map map, Thing thingToIgnore = null, Thing thing = null)
            => DebugSettings.godMode ? AcceptanceReport.WasAccepted : "DeltaV_TradeDevBuildOnly".Translate();
    }
}
