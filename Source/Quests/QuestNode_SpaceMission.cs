using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;

namespace DeltaV
{
    public sealed class QuestNode_Root_SpaceMission : QuestNode
    {
        protected override bool TestRunInt(Slate slate) => slate.Get<int>("missionId") > 0;
        protected override void RunInt()
        {
            int id = QuestGen.slate.Get<int>("missionId");
            SpaceMission run = Current.Game.GetComponent<UniverseManager>().Missions.Find(id);
            Quest quest = QuestGen.quest;
            run.quest = quest;
            QuestGen.slate.Set("resolvedQuestName", run.Title);
            QuestGen.slate.Set("resolvedQuestDescription", "DeltaV_Mission_Description".Translate(run.faction.Name, run.quantity, run.rewardEach).ToString());
            quest.AddPart(new QuestPart_SpaceMission { missionId = id, inSignalEnable = quest.InitiateSignal });
            if (run.kind == SpaceMissionKind.Passengers)
            {
                var pawns = run.passengers.Select(p => p.pawn).ToList();
                quest.AddPart(new QuestPart_ExtraFaction
                {
                    extraFaction = new ExtraFaction(run.faction, ExtraFactionType.HomeFaction),
                    affectedPawns = pawns.ToList(), areHelpers = false, inSignalEnable = quest.InitiateSignal
                });
                quest.AddPart(new QuestPart_WorkDisabled
                {
                    pawns = pawns.ToList(), disabledWorkTags = WorkTags.AllWork, inSignalEnable = quest.InitiateSignal
                });
                quest.AddPart(new QuestPart_MissionPassengers { missionId = id });
                foreach (Pawn pawn in pawns)
                {
                    pawn.questTags ??= new List<string>();
                    pawn.questTags.Add("Quest" + quest.id + ".Passengers");
                }
            }
        }
    }

    public sealed class QuestPart_SpaceMission : QuestPartActivable
    {
        public int missionId;
        private SpaceMissionService Service => Current.Game?.GetComponent<UniverseManager>()?.Missions;
        public override string DescriptionPart => Service?.Describe(Service.Find(missionId));
        public override void QuestPartTick() => Service?.Tick(Service.Find(missionId));
        public override void Cleanup() => Service?.Cleanup(Service.Find(missionId));
        public override IEnumerable<Gizmo> ExtraGizmos(ISelectable target)
        {
            if (quest.Historical) yield break;
            SpaceMission run = Service?.Find(missionId);
            if (run == null) yield break;
            if (target is Thing thing && run.source?.Matches(Current.Game.GetComponent<UniverseManager>(), thing) != true
                && run.destination?.Matches(Current.Game.GetComponent<UniverseManager>(), thing) != true
                && !(thing is MissionCargo box && box.missionId == missionId)
                && !run.passengers.Any(p => p.pawn == thing)) yield break;
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_NavCourse".Translate(), icon = TexCommand.OpenLinkedQuestTex,
                defaultDesc = run.Title,
                action = () => Service?.Locate(Service.Find(missionId))
            };
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_Mission_Abandon".Translate(), icon = TexCommand.ClearPrioritizedWork,
                defaultDesc = run.Title,
                action = () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("DeltaV_Mission_AbandonConfirm".Translate(),
                    () => Service?.Fail(Service.Find(missionId))))
            };
        }
        public override void ExposeData() { base.ExposeData(); Scribe_Values.Look(ref missionId, "missionId"); }
    }

    public sealed class QuestPart_MissionPassengers : QuestPart
    {
        public int missionId;
        private SpaceMissionService Service => Current.Game?.GetComponent<UniverseManager>()?.Missions;
        public override IEnumerable<GlobalTargetInfo> QuestLookTargets
        {
            get
            {
                var run = Service?.Find(missionId);
                if (run?.Active != true) yield break;
                foreach (var p in run.passengers)
                    if (p.pawn?.SpawnedOrAnyParentSpawned == true)
                        yield return new GlobalTargetInfo(p.pawn.PositionHeld, p.pawn.MapHeld);
            }
        }
        public override void Notify_QuestSignalReceived(Signal signal)
        {
            if (!signal.tag.StartsWith("Quest" + quest.id + ".Passengers.")) return;
            if (!signal.args.TryGetArg("SUBJECT", out Pawn pawn)) return;
            string reason = signal.tag.Substring(signal.tag.LastIndexOf('.') + 1);
            if (reason == "Destroyed" || reason == "Kidnapped" || reason == "Banished" || reason == "LeftBehind"
                || reason == "SurgeryViolation" || reason == "Arrested" || reason == "Enslaved")
                Service?.FailPassenger(Service.Find(missionId), pawn, reason);
        }
        public override void Notify_PawnKilled(Pawn pawn, DamageInfo? dinfo)
            => Service?.FailPassenger(Service.Find(missionId), pawn, "Destroyed");
        public override void Notify_PawnDiscarded(Pawn pawn)
            => Service?.FailPassenger(Service.Find(missionId), pawn, "LeftBehind");
        public override void ExposeData() { base.ExposeData(); Scribe_Values.Look(ref missionId, "missionId"); }
    }
}
