using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using Verse.Sound;

namespace DeltaV
{
    // MapPortal is a Building, whereas this endpoint must remain a Building_Door.
    // Reuse the portal's local toils and atomic handoff without a second spawned portal.
    public class JobDriver_EnterSoftDock : JobDriver
    {
        private Building_DockingPort Port => TargetThingA as Building_DockingPort;
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;
        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => Port?.SoftPeer(pawn) == null);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return Toils_General.Wait(90).FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch)
                .WithProgressBarToilDelay(TargetIndex.A);
            yield return Toils_General.Do(() =>
            {
                if (!Port.TryDestination(pawn, out Map map, out IntVec3 cell)) { EndJobWith(JobCondition.Incompletable); return; }
                Map source = pawn.Map; IntVec3 origin = pawn.Position; Rot4 facing = pawn.Rotation;
                bool drafted = pawn.Drafted, fireAtWill = pawn.drafter?.FireAtWill ?? false;
                var lord = pawn.GetLord();
                pawn.DeSpawnOrDeselect();
                try { GenSpawn.Spawn(pawn, cell, map, facing); pawn.PostSwapMap(); }
                catch
                {
                    if (pawn.Spawned) pawn.DeSpawn();
                    GenSpawn.Spawn(pawn, origin, source, facing);
                    throw;
                }
                if (pawn.inventory != null) pawn.inventory.UnloadEverything = !map.IsPocketMap;
                if (pawn.drafter != null) { pawn.drafter.Drafted = drafted; pawn.drafter.FireAtWill = fireAtWill; }
                if (pawn.carryTracker?.CarriedThing != null && !drafted)
                    pawn.carryTracker.TryDropCarriedThing(cell, ThingPlaceMode.Near, out _);
                pawn.mindState.priorityWork.ClearPrioritizedWorkAndJobQueue();
                lord?.Notify_PawnLost(pawn, PawnLostCondition.ExitedMap);
            });
        }
    }
    public class JobDriver_HaulSoftDock : JobDriver
    {
        private Building_DockingPort Port => TargetThingB as Building_DockingPort;
        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(TargetA, job, 1, -1, null, errorOnFailed);
        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.B);
            this.FailOn(() => Port?.SoftPeer(pawn) == null);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(TargetIndex.A);
            yield return Toils_Haul.StartCarryThing(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.Touch);
            yield return Toils_General.Wait(90).FailOnCannotTouch(TargetIndex.B, PathEndMode.Touch)
                .WithProgressBarToilDelay(TargetIndex.B);
            yield return Toils_General.Do(() =>
            {
                if (!Port.TryDestination(pawn, out Map map, out IntVec3 cell)) { EndJobWith(JobCondition.Incompletable); return; }
                Thing cargo = pawn.carryTracker.CarriedThing;
                if (cargo == null) { EndJobWith(JobCondition.Incompletable); return; }
                // Like PortalContainerProxy: GenDrop owns placement; failed placement retains the carried object.
                pawn.carryTracker.innerContainer.Remove(cargo);
                try
                {
                    if (!GenDrop.TryDropSpawn(cargo, cell, map, ThingPlaceMode.Near, out _))
                    { pawn.carryTracker.innerContainer.TryAdd(cargo); EndJobWith(JobCondition.Incompletable); }
                    else cargo.PostSwapMap();
                }
                catch { if (!cargo.Spawned && !cargo.Destroyed) pawn.carryTracker.innerContainer.TryAdd(cargo); throw; }
            });
        }
    }
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
