using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    internal enum TutorialStage { AwaitingWake, Welcome, Inquiry, Briefing, PreparingFlight, Design, Flight, Salvage, Range, Visit, Ended }

    // One persistent course record; QuestPart holds only vanilla quest activation state.
    internal sealed class TutorialRunState : IExposable
    {
        internal TutorialStage stage;
        internal Quest quest;
        internal Map station;
        internal List<Pawn> colonists = new List<Pawn>();
        internal List<Building_CryptosleepCasket> caskets = new List<Building_CryptosleepCasket>();
        internal Building_CommsConsole comms;
        internal Faction mentors;
        internal Building_Door berthDoor;
        internal Thing trainingConsole, giftConsole;
        internal Thing rangeConsole, rangeTarget;
        internal Building_Storage cargoShelf;
        internal Building_DockingPort trainingPort, yardPort, rangePort;
        internal Building_GravEngine giftEngine;
        internal int trainingShipId, salvageObjectId;
        internal int forwardTicks, lateralTicks, turnTicks;
        internal bool recoveryDocked;

        public void ExposeData()
        {
            Scribe_Values.Look(ref stage, "stage");
            Scribe_References.Look(ref quest, "quest");
            Scribe_References.Look(ref station, "station");
            Scribe_Collections.Look(ref colonists, "colonists", LookMode.Reference);
            Scribe_Collections.Look(ref caskets, "caskets", LookMode.Reference);
            Scribe_References.Look(ref comms, "comms");
            Scribe_References.Look(ref mentors, "mentors");
            Scribe_References.Look(ref berthDoor, "berthDoor");
            Scribe_References.Look(ref trainingConsole, "trainingConsole");
            Scribe_References.Look(ref giftConsole, "giftConsole");
            Scribe_References.Look(ref rangeConsole, "rangeConsole");
            Scribe_References.Look(ref rangeTarget, "rangeTarget");
            Scribe_References.Look(ref cargoShelf, "cargoShelf");
            Scribe_References.Look(ref trainingPort, "trainingPort");
            Scribe_References.Look(ref yardPort, "yardPort");
            Scribe_References.Look(ref rangePort, "rangePort");
            Scribe_References.Look(ref giftEngine, "giftEngine");
            Scribe_Values.Look(ref trainingShipId, "trainingShipId");
            Scribe_Values.Look(ref salvageObjectId, "salvageObjectId");
            Scribe_Values.Look(ref forwardTicks, "forwardTicks");
            Scribe_Values.Look(ref lateralTicks, "lateralTicks");
            Scribe_Values.Look(ref turnTicks, "turnTicks");
            Scribe_Values.Look(ref recoveryDocked, "recoveryDocked");
        }
    }
}
