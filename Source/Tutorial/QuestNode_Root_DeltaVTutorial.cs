using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;

namespace DeltaV
{
    public sealed class QuestNode_Root_DeltaVTutorial : QuestNode
    {
        protected override bool TestRunInt(Slate slate) => Current.Game?.GetComponent<UniverseManager>()?.tutorialRun is TutorialRunState run
            && run.quest == null && run.station != null;

        protected override void RunInt()
        {
            Quest quest = QuestGen.quest;
            QuestGen.slate.Set("resolvedQuestName", "DeltaV_Tutorial_Title".Translate().ToString());
            QuestGen.slate.Set("resolvedQuestDescription", "DeltaV_Tutorial_Description".Translate().ToString());
            quest.AddPart(new QuestPart_DeltaVTutorial { inSignalEnable = quest.InitiateSignal });
            AddDialog(quest, TutorialStage.Welcome, "AcceptWelcome", "DeltaV_Tutorial_AcceptWelcome");
            AddDialog(quest, TutorialStage.Briefing, "ReportForTraining", "DeltaV_Tutorial_Report");
            AddDialog(quest, TutorialStage.ReadyForTraining, null, "OK");
            quest.AddPart(new QuestPart_QuestEnd
            { inSignal = DeltaVTutorialService.Signal(quest, "Ended"), outcome = QuestEndOutcome.Unknown, sendLetter = true });
            quest.AddPart(new QuestPart_QuestEnd
            { inSignal = DeltaVTutorialService.Signal(quest, "Failed"), outcome = QuestEndOutcome.Fail, sendLetter = true });
        }

        private static void AddDialog(Quest quest, TutorialStage stage, string reply, string replyKey)
        {
            var part = new QuestPart_Dialog
            {
                inSignal = DeltaVTutorialService.Signal(quest, "Show" + stage),
                title = "DeltaV_Tutorial_Title".Translate(),
                text = ("DeltaV_Tutorial_Dialog_" + stage).Translate(),
                relatedFaction = Current.Game.GetComponent<UniverseManager>().tutorialRun.mentors,
                addToArchive = false,
                options = new List<QuestPart_Dialog.Option>
                {
                    new QuestPart_Dialog.Option { text = replyKey.Translate(),
                        outSignal = DeltaVTutorialService.Signal(quest, reply ?? "Acknowledged") },
                    new QuestPart_Dialog.Option { text = "DeltaV_Tutorial_Leave".Translate(),
                        outSignal = DeltaVTutorialService.Signal(quest, "Leave" + stage) }
                }
            };
            quest.AddPart(part);
        }
    }

    public sealed class QuestPart_DeltaVTutorial : QuestPartActivable
    {
        private DeltaVTutorialService Service => Current.Game?.GetComponent<UniverseManager>()?.Tutorial;
        private bool IsCurrent => Service?.Run?.quest == quest;
        public override void PostQuestAdded() => Service.RegisterQuest(quest);
        public override string DescriptionPart => IsCurrent ? Service.Description : null;
        public override IEnumerable<GlobalTargetInfo> QuestLookTargets => IsCurrent ? Service.Targets : Enumerable.Empty<GlobalTargetInfo>();
        public override IEnumerable<GlobalTargetInfo> QuestSelectTargets => QuestLookTargets;
        public override string QuestSelectTargetsLabel => "DeltaV_Tutorial_Targets".Translate();
        protected override void ProcessQuestSignal(Signal signal) => Service?.Receive(quest, signal.tag);
        public override void Cleanup() => Service?.Ended(quest);
        public override IEnumerable<Gizmo> ExtraGizmos(ISelectable target)
        {
            if (!IsCurrent || !Service.CanShowFor(target)) yield break;
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_Tutorial_Open".Translate(),
                defaultDesc = "DeltaV_Tutorial_OpenDesc".Translate(),
                icon = TexCommand.OpenLinkedQuestTex,
                action = Service.ShowBriefing
            };
        }
    }
}
