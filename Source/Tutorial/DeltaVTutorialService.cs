using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    internal sealed class DeltaVTutorialService
    {
        private readonly UniverseManager universe;
        private bool resumeQueued;
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
                if (Run.stage != TutorialStage.AwaitingWake) return;
                if (Run.station == null || Run.station.Disposed || Run.caskets == null || Run.colonists == null)
                { Fail("DeltaV_Tutorial_MissingArrival"); return; }
                // The arrival owns these exact pawns. Never create replacement colonists on reload.
                foreach (var casket in Run.caskets)
                    if (casket != null && casket.Spawned && casket.HasAnyContents) casket.Open();
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
                if (Run.briefingTable?.Spawned == true) yield return Run.briefingTable;
                if (Run.mentor?.Spawned == true) yield return Run.mentor;
                foreach (Pawn pawn in Run.colonists ?? Enumerable.Empty<Pawn>())
                    if (pawn?.Spawned == true) yield return pawn;
                foreach (var casket in Run.caskets ?? Enumerable.Empty<Building_CryptosleepCasket>())
                    if (casket?.Spawned == true) yield return casket;
            }
        }

        internal bool CanShowFor(ISelectable target) => Active && target is Thing thing
            && ((thing.Faction == Faction.OfPlayer && thing.MapHeld == Run.station)
                || thing == Run.mentor || Run.colonists.Contains(thing as Pawn));

        internal void ShowBriefing()
        {
            if (!Active || Run.stage == TutorialStage.AwaitingWake) return;
            // Vanilla Dialog closes and emits a fresh signal; phase-specific replies reject stale windows.
            if (Find.WindowStack.IsOpen<Dialog_NodeTree>()) return;
            Find.SignalManager.SendSignal(new Signal(Signal(Run.quest, "Show" + Run.stage)));
        }

        internal void Receive(Quest quest, string tag)
        {
            if (!Active || Run.quest != quest) return;
            if (tag == Signal(quest, "AcceptWelcome") && Run.stage == TutorialStage.Welcome)
            {
                Run.stage = TutorialStage.Briefing;
                // Opening a new dialog inside the old option's close callback would stack windows.
                universe.QueueFrameAction(ShowBriefing);
            }
            else if (tag == Signal(quest, "ReportForTraining") && Run.stage == TutorialStage.Briefing)
            {
                Thing table = Run.briefingTable;
                if (table == null || !table.Spawned)
                { Fail("DeltaV_Tutorial_BriefingLost"); return; }
                bool present = Run.colonists.Any(p => p != null && p.Spawned && !p.Dead && !p.Downed
                    && p.Map == table.Map && p.Position.InHorDistOf(table.Position, 5f));
                if (!present)
                {
                    Messages.Message("DeltaV_Tutorial_ComeToBriefing".Translate(), table, MessageTypeDefOf.RejectInput, false);
                    return;
                }
                Run.stage = TutorialStage.ReadyForTraining;
                universe.QueueFrameAction(ShowBriefing);
            }
            else if (tag == Signal(quest, "Leave" + Run.stage))
                Find.SignalManager.SendSignal(new Signal(Signal(quest, "Ended")));
        }

        internal void Ended(Quest quest)
        {
            if (Run?.quest != quest) return;
            Run.stage = TutorialStage.Ended;
            Run.caskets?.Clear();
            Run.colonists?.Clear();
            Run.station = null;
            Run.briefingTable = null;
            Run.mentor = null;
            Run.mentors = null;
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
