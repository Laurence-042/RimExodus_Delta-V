using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DeltaV
{
    // Vanilla refueling work still schedules the job. Only selecting the compartment differs.
    public static class GunSupplyUtility
    {
        public static Thing FindSupply(Pawn pawn, Thing target, bool forced)
        {
            if (!(target is ThingWithComps receiver) || !target.Spawned || target.Fogged()
                || target.Faction != pawn.Faction || target.IsForbidden(pawn)
                || !pawn.CanReserve(target, 1, -1, null, forced)) return null;
            foreach (ThingComp comp in receiver.AllComps)
            {
                if (!(comp is CompGunSupply supply) || !supply.CanLoad(forced)) continue;
                ThingFilter filter = supply.Props.fuelFilter;
                Thing found = GenClosest.ClosestThingReachable(pawn.Position, target.Map,
                    filter.BestThingRequest, PathEndMode.ClosestTouch, TraverseParms.For(pawn), 9999f,
                    t => filter.Allows(t) && !t.IsForbidden(pawn) && pawn.CanReserve(t));
                if (found != null) return found;
            }
            return null;
        }

        public static Job RefuelJob(Pawn pawn, Thing target, bool forced)
        {
            Thing fuel = FindSupply(pawn, target, forced);
            return fuel == null ? null : JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("DeltaV_LoadGunSupply"), target, fuel);
        }
    }

    public class JobDriver_LoadGunSupply : JobDriver_Refuel
    {
        private CompGunSupply Supply => Fuel == null ? null
            : Refuelable.TryGetComp<CompSpaceWeaponMagazine>()?.SupplyFor(Fuel.def);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOnDestroyedOrNull(TargetIndex.B);
            AddEndCondition(() => Supply == null ? JobCondition.Incompletable
                : Supply.IsFull ? JobCondition.Succeeded : JobCondition.Ongoing);
            AddFailCondition(() => !job.playerForced
                && (Supply == null || !Supply.allowAutoRefuel || !Supply.ShouldAutoRefuelNowIgnoringFuelPct));
            yield return Toils_General.DoAtomic(() => job.count = Supply.GetFuelCountToFullyRefuel());
            Toil reserveFuel = Toils_Reserve.Reserve(TargetIndex.B);
            yield return reserveFuel;
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(TargetIndex.B).FailOnSomeonePhysicallyInteracting(TargetIndex.B);
            yield return Toils_Haul.StartCarryThing(TargetIndex.B, putRemainderInQueue: false, subtractNumTakenFromJobCount: true)
                .FailOnDestroyedNullOrForbidden(TargetIndex.B);
            yield return Toils_Haul.CheckForGetOpportunityDuplicate(reserveFuel, TargetIndex.B, TargetIndex.None, takeFromValidStorage: true);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return Toils_General.Wait(RefuelingDuration)
                .FailOnDestroyedNullOrForbidden(TargetIndex.B).FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch)
                .WithProgressBarToilDelay(TargetIndex.A);
            yield return Toils_General.Do(() =>
            {
                CompGunSupply supply = Supply;
                if (supply != null && !supply.IsFull
                    && (job.playerForced || supply.allowAutoRefuel && supply.ShouldAutoRefuelNowIgnoringFuelPct))
                    supply.Refuel(new List<Thing> { Fuel });
                if (pawn.carryTracker.CarriedThing != null)
                    pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
            });
        }
    }
}
