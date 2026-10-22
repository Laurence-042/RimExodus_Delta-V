using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace DeltaV
{
    public class JobDriver_HackSoftDock : JobDriver
    {
        private Building_DockingPort Port => TargetThingA as Building_DockingPort;
        private CompHackable Hack => TargetThingB?.TryGetComp<CompHackable>();
        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(TargetA, job, 1, -1, null, errorOnFailed);
        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => Port?.SoftPeer(pawn, false) != TargetThingB || !Port.PawnCanOpen(pawn)
                || Hack == null || Hack.LockedOut || !HackUtility.IsCapableOfHacking(pawn)
                || pawn.skills.GetSkill(SkillDefOf.Intellectual).Level < Hack.Props.intellectualSkillPrerequisite);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil toil = ToilMaker.MakeToil("RemoteDockHack");
            toil.tickAction = () =>
            {
                Hack.Hack(pawn.GetStatValue(StatDefOf.HackingSpeed), pawn);
                pawn.skills.Learn(SkillDefOf.Intellectual, .1f);
                pawn.rotationTracker.FaceTarget(TargetA);
                if (Hack.IsHacked) ReadyForNextToil();
            };
            toil.defaultCompleteMode = ToilCompleteMode.Never;
            toil.activeSkill = () => SkillDefOf.Intellectual;
            toil.WithEffect(EffecterDefOf.Hacking, TargetIndex.A);
            toil.WithProgressBar(TargetIndex.A, () => Hack?.ProgressPercent ?? 0f);
            toil.PlaySoundAtStart(SoundDefOf.Hacking_Started);
            toil.PlaySustainerOrSound(SoundDefOf.Hacking_InProgress);
            toil.AddFinishAction(() => (Hack?.IsHacked == true ? SoundDefOf.Hacking_Completed : SoundDefOf.Hacking_Suspended).PlayOneShot(Port));
            yield return toil.FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch);
        }
    }
}
