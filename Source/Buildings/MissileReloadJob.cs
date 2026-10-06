using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DeltaV
{
    public class WorkGiver_LoadMissileLauncher : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest =>
            ThingRequest.ForDef(DeltaVDefOf.DeltaV_Weapon_MissileLauncher);
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override bool HasJobOnThing(Pawn pawn, Thing target, bool forced = false)
        {
            return FindMissile(pawn, target) != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing target, bool forced = false)
        {
            Thing missile = FindMissile(pawn, target);
            if (missile == null) return null;
            Job job = JobMaker.MakeJob(DeltaVDefOf.DeltaV_LoadMissileLauncher, target, missile);
            job.count = 1;
            return job;
        }

        private static Thing FindMissile(Pawn pawn, Thing target)
        {
            CompChangeableProjectile chamber = target?.TryGetComp<CompChangeableProjectile>();
            if (chamber == null || chamber.Loaded || !target.Spawned
                || target.Faction != pawn.Faction || target.IsForbidden(pawn)
                || !pawn.CanReserve(target)) return null;
            return GenClosest.ClosestThingReachable(target.Position, target.Map,
                ThingRequest.ForGroup(ThingRequestGroup.Shell), PathEndMode.OnCell,
                TraverseParms.For(pawn), 40f, t => t.def.GetModExtension<DefModExtension_SpaceMissileAmmo>() != null
                    && !t.IsForbidden(pawn) && pawn.CanReserve(t, 10, 1)
                    && chamber.allowedShellsSettings.AllowedToAccept(t));
        }
    }

    public class JobDriver_LoadMissileLauncher : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed)
                && pawn.Reserve(job.targetB, job, 1, 1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(TargetIndex.B);
            yield return Toils_Haul.StartCarryThing(TargetIndex.B);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil load = ToilMaker.MakeToil("LoadMissileLauncher");
            load.initAction = () =>
            {
                Thing launcher = job.targetA.Thing;
                Thing missile = pawn.carryTracker.CarriedThing;
                CompChangeableProjectile chamber = launcher?.TryGetComp<CompChangeableProjectile>();
                if (chamber == null || chamber.Loaded || missile == null
                    || !chamber.allowedShellsSettings.AllowedToAccept(missile))
                {
                    if (missile != null)
                        pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                    return;
                }
                chamber.LoadShell(missile.def, 1);
                pawn.carryTracker.innerContainer.ClearAndDestroyContents();
            };
            yield return load;
        }
    }
}
