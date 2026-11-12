using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    public sealed class CompCuttingBombPack : ThingComp
    {
        private bool cutFoundation;
        internal bool CutFoundation => cutFoundation;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref cutFoundation, "cutFoundation");
        }

        public override IEnumerable<Gizmo> CompGetWornGizmosExtra()
        {
            if (parent.ParentHolder is not Pawn_ApparelTracker apparel || !apparel.pawn.IsColonistPlayerControlled)
                yield break;

            yield return new Command_Action
            {
                defaultLabel = (cutFoundation ? "DeltaV_CuttingBombModeFoundation" : "DeltaV_CuttingBombModeWalls").Translate(),
                defaultDesc = "DeltaV_CuttingBombModeDesc".Translate(),
                icon = parent.def.uiIcon,
                groupable = false,
                action = () => cutFoundation = !cutFoundation
            };
        }
    }
}
