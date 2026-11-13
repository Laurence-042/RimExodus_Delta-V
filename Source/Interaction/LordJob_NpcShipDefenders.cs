using RimWorld;
using Verse;
using Verse.AI.Group;

namespace DeltaV
{
    internal sealed class LordJob_NpcShipDefenders : LordJob
    {
        private int shipKey;
        private bool shuttle;

        internal DriveSubject Ship => new DriveSubject(0, shipKey, shuttle);
        public override bool AddFleeToil => false;

        public LordJob_NpcShipDefenders() { }

        internal LordJob_NpcShipDefenders(DriveSubject ship)
        {
            shipKey = ship.key;
            shuttle = ship.shuttle;
        }

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();
            graph.AddToil(new LordToil_HuntDownColonists());
            return graph;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref shipKey, "shipKey");
            Scribe_Values.Look(ref shuttle, "shuttle");
        }
    }
}
