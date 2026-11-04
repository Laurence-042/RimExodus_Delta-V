using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    internal enum TutorialStage { AwaitingWake, Welcome, Briefing, ReadyForTraining, Ended }

    // One persistent course record; QuestPart holds only vanilla quest activation state.
    internal sealed class TutorialRunState : IExposable
    {
        internal TutorialStage stage;
        internal Quest quest;
        internal Map station;
        internal List<Pawn> colonists = new List<Pawn>();
        internal List<Building_CryptosleepCasket> caskets = new List<Building_CryptosleepCasket>();
        internal Pawn mentor;
        internal Thing briefingTable;
        internal Faction mentors;

        public void ExposeData()
        {
            Scribe_Values.Look(ref stage, "stage");
            Scribe_References.Look(ref quest, "quest");
            Scribe_References.Look(ref station, "station");
            Scribe_Collections.Look(ref colonists, "colonists", LookMode.Reference);
            Scribe_Collections.Look(ref caskets, "caskets", LookMode.Reference);
            Scribe_References.Look(ref mentor, "mentor");
            Scribe_References.Look(ref briefingTable, "briefingTable");
            Scribe_References.Look(ref mentors, "mentors");
        }
    }
}
