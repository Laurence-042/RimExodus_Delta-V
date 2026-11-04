using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    internal sealed partial class DeltaVTutorialService
    {
        private readonly UniverseManager universe;
        private bool resumeQueued;
        // Only spans native new-game preparation and initial casket placement.
        internal List<Pawn> UnselectedCrew { get; } = new List<Pawn>();
        internal DeltaVTutorialService(UniverseManager universe) { this.universe = universe; }
        internal TutorialRunState Run => universe.tutorialRun;
        internal bool Active => Current.Game?.GetComponent<UniverseManager>() == universe
            && Run != null && Run.stage != TutorialStage.Ended && Run.quest != null && !Run.quest.Historical;
        internal static string Signal(Quest quest, string suffix) => $"Quest{quest.id}.DeltaVTutorial.{suffix}";

        internal void RegisterQuest(Quest quest)
        {
            if (Run == null || Run.quest != null) throw new InvalidOperationException("Tutorial quest requires one initialized arrival and cannot be duplicated.");
            Run.quest = quest;
        }

        internal void Resume()
        {
            if (Run == null || resumeQueued) return;
            var expected = Run;
            resumeQueued = true;
            universe.QueueFrameAction(() =>
            {
                resumeQueued = false;
                if (Current.Game?.GetComponent<UniverseManager>() != universe || Run != expected || !Active) return;
                if (Run.stage == TutorialStage.PreparingFlight) { CreateFlightCourse(); return; }
                if (Run.stage != TutorialStage.AwaitingWake) return;
                if (Run.station == null || Run.station.Disposed || Run.caskets == null || Run.colonists == null)
                { Fail("DeltaV_Tutorial_MissingArrival"); return; }
                if (Run.trainingConsole == null) { CreateTrainingBerth(); return; }
                if (!BerthConnected(Run.trainingPort, Run.berthDoor))
                { Fail("DeltaV_Tutorial_BerthUnavailable"); return; }
                // The arrival owns these exact pawns. Never create replacement colonists on reload.
                foreach (var casket in Run.caskets)
                    if (casket?.Spawned == true && casket.GetDirectlyHeldThings().Any(t => Run.colonists.Contains(t as Pawn))) casket.Open();
                if (Run.colonists.Any(p => p == null || p.Dead || !p.Spawned || p.Map != Run.station))
                { Fail("DeltaV_Tutorial_WakeFailed"); return; }
                Run.caskets.Clear();
                Run.stage = TutorialStage.Welcome;
                ShowBriefing();
            });
        }

        internal string Description => Run == null ? null : ("DeltaV_Tutorial_Stage_" + Run.stage).Translate();

        internal IEnumerable<GlobalTargetInfo> Targets
        {
            get
            {
                if (!Active) yield break;
                if (Run.comms?.Spawned == true) yield return Run.comms;
                foreach (Thing role in FlightRoles())
                    if (role?.Spawned == true) yield return role;
                foreach (Pawn pawn in Run.colonists ?? Enumerable.Empty<Pawn>())
                    if (pawn?.Spawned == true) yield return pawn;
                foreach (var casket in Run.caskets ?? Enumerable.Empty<Building_CryptosleepCasket>())
                    if (casket?.Spawned == true) yield return casket;
            }
        }

        internal bool CanShowFor(ISelectable target) => Active && target is Thing thing
            && ((thing.Faction == Faction.OfPlayer && thing.MapHeld == Run.station)
                || (thing.Faction == Faction.OfPlayer && FlightRoles().Any(t => t?.Spawned == true && t.Map == thing.MapHeld))
                || FlightRoles().Contains(thing) || Run.colonists.Contains(thing as Pawn));

        internal void ShowBriefing()
        {
            if (!Active || Run.stage == TutorialStage.AwaitingWake) return;
            if ((Run.stage == TutorialStage.Contact || Run.stage == TutorialStage.Inquiry) && Run.comms?.Spawned != true)
            { Fail("DeltaV_Tutorial_CommsLost"); return; }
            if (!CheckFlightAssets()) return;
            // Vanilla Dialog closes and emits a fresh signal; phase-specific replies reject stale windows.
            if (Find.WindowStack.IsOpen<Dialog_NodeTree>()) return;
            Find.SignalManager.SendSignal(new Signal(Signal(Run.quest, "Show" + Run.stage)));
        }

        internal void Receive(Quest quest, string tag)
        {
            if (!Active || Run.quest != quest) return;
            if (tag == Signal(quest, "AcceptWelcome") && Run.stage == TutorialStage.Welcome)
            {
                Run.stage = TutorialStage.Contact;
                // Opening a new dialog inside the old option's close callback would stack windows.
                universe.QueueFrameAction(ShowBriefing);
            }
            else if (Run.stage == TutorialStage.Inquiry && tag == Signal(quest, "AskPurpose"))
            {
                Run.stage = TutorialStage.Briefing;
                universe.QueueFrameAction(ShowBriefing);
            }
            else if (ReceiveFlight(tag)) { }
            else if (tag == Signal(quest, "Leave" + Run.stage))
                Find.SignalManager.SendSignal(new Signal(Signal(quest, "Ended")));
        }

        internal bool ReceiveComms(Faction faction, Pawn negotiator)
        {
            if (!Active || faction != Run.mentors || Run.comms?.Spawned != true
                || negotiator?.CurJobDef != JobDefOf.UseCommsConsole
                || negotiator.CurJob.targetA.Thing != Run.comms || !Run.colonists.Contains(negotiator)
                || !Run.comms.CanUseCommsNow || negotiator.Position != Run.comms.InteractionCell
                || (Run.stage != TutorialStage.Contact && Run.stage != TutorialStage.Inquiry && Run.stage != TutorialStage.Briefing)) return false;
            if (Run.stage == TutorialStage.Contact) Run.stage = TutorialStage.Inquiry;
            ShowBriefing();
            return true;
        }

        internal void Ended(Quest quest)
        {
            if (Run?.quest != quest) return;
            Run.stage = TutorialStage.Ended;
            Run.caskets?.Clear();
            Run.colonists?.Clear();
            Run.station = null;
            Run.suppliesTable = null;
            Run.comms = null;
            Run.mentors = null;
            Run.berthDoor = null;
            Run.trainingConsole = Run.giftConsole = null;
            Run.rangeConsole = Run.rangeTarget = null;
            Run.cargoShelf = Run.salvageShelf = null;
            Run.trainingPort = Run.yardPort = null;
            Run.giftEngine = null;
        }

        internal void EndForSceneReset()
        {
            if (!Active) return;
            Quest quest = Run.quest;
            quest.End(QuestEndOutcome.Unknown, sendLetter: false, playSound: false);
            Ended(quest);
        }

        private void Fail(string key)
        {
            Messages.Message(key.Translate(), MessageTypeDefOf.NegativeEvent, false);
            Find.SignalManager.SendSignal(new Signal(Signal(Run.quest, "Failed")));
        }
    }
}
