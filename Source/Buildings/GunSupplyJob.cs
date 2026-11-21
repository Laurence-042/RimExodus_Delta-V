using System.Linq;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DeltaV
{
    // Vanilla refueling work still schedules the job. Only selecting the compartment differs.
    public static class GunSupplyUtility
    {
        internal static bool CanLoad(CompRefuelable tank, bool forced) => tank != null && !tank.IsFull
            && (tank.Fuel <= 0f || tank.Props.allowRefuelIfNotEmpty)
            && (forced || tank.allowAutoRefuel && tank.ShouldAutoRefuelNow);

        public static Thing FindSupply(Pawn pawn, Thing target, bool forced, bool afterburner = false)
        {
            if (!(target is ThingWithComps receiver) || !target.Spawned || target.Fogged()
                || target.Faction != pawn.Faction || target.IsForbidden(pawn)
                || !pawn.CanReserve(target, 1, -1, null, forced)) return null;
            foreach (ThingComp comp in receiver.AllComps)
            {
                CompRefuelable supply = afterburner ? comp as CompAfterburnerTank : comp as CompGunSupply;
                if (!CanLoad(supply, forced)) continue;
                ThingFilter filter = supply.Props.fuelFilter;
                Thing found = GenClosest.ClosestThingReachable(pawn.Position, target.Map,
                    filter.BestThingRequest, PathEndMode.ClosestTouch, TraverseParms.For(pawn), 9999f,
                    t => filter.Allows(t) && !t.IsForbidden(pawn) && pawn.CanReserve(t));
                if (found != null) return found;
            }
            return null;
        }

        internal static Job MakeSupplyJob(Thing target, Thing supply) =>
            JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("DeltaV_LoadGunSupply"), target, supply);

        internal static bool TryReceiveMissile(Thing receiver, Thing missile)
        {
            if (missile?.def.GetModExtension<DefModExtension_SpaceMissileAmmo>() == null) return false;
            if (receiver.TryGetComp<CompSpaceWeaponMagazine>()?.ChangeableProjectile is { Loaded: false } chamber
                && chamber.allowedShellsSettings.AllowedToAccept(missile))
                chamber.LoadShell(missile.def, 1);
            else if (receiver.TryGetComp<CompMissileMagazineFacility>()?.TryStore(missile.def) != true)
                return false;
            if (missile.stackCount == 1) missile.Destroy(DestroyMode.Vanish);
            else missile.SplitOff(1).Destroy(DestroyMode.Vanish);
            return true;
        }

        public static Job RefuelJob(Pawn pawn, Thing target, bool forced, bool afterburner = false)
        {
            Thing fuel = FindSupply(pawn, target, forced, afterburner);
            return fuel == null ? null : MakeSupplyJob(target, fuel);
        }
    }

    public class JobDriver_LoadGunSupply : JobDriver_Refuel
    {
        private CompRefuelable Supply
        {
            get
            {
                if (Fuel == null) return null;
                if (Refuelable.TryGetComp<CompAfterburnerTank>() is { } afterburner)
                    return afterburner;
                // A job holding the previous head type loses its destination when the magazine changes.
                return Refuelable.TryGetComp<CompSpaceWeaponMagazine>()?.SupplyFor(Fuel.def);
            }
        }

        private bool LoadingMissile => Fuel?.def.GetModExtension<DefModExtension_SpaceMissileAmmo>() != null;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return LoadingMissile
                ? pawn.Reserve(Refuelable, job, 1, -1, null, errorOnFailed)
                    && pawn.Reserve(Fuel, job, 1, 1, null, errorOnFailed)
                : base.TryMakePreToilReservations(errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOnDestroyedOrNull(TargetIndex.B);
            AddEndCondition(() => LoadingMissile ? JobCondition.Ongoing : Supply == null ? JobCondition.Incompletable
                : Supply.IsFull ? JobCondition.Succeeded : JobCondition.Ongoing);
            AddFailCondition(() => !LoadingMissile && !job.playerForced
                && (Supply == null || !Supply.allowAutoRefuel || !Supply.ShouldAutoRefuelNowIgnoringFuelPct));
            yield return Toils_General.DoAtomic(() => job.count = LoadingMissile ? 1 : Supply.GetFuelCountToFullyRefuel());
            Toil reserveFuel = Toils_Reserve.Reserve(TargetIndex.B, stackCount: LoadingMissile ? 1 : -1);
            yield return reserveFuel;
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(TargetIndex.B).FailOnSomeonePhysicallyInteracting(TargetIndex.B);
            yield return Toils_Haul.StartCarryThing(TargetIndex.B, putRemainderInQueue: false, subtractNumTakenFromJobCount: true)
                .FailOnDestroyedNullOrForbidden(TargetIndex.B);
            var goToReceiver = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return Toils_Jump.JumpIf(goToReceiver, () => LoadingMissile);
            yield return Toils_Haul.CheckForGetOpportunityDuplicate(reserveFuel, TargetIndex.B, TargetIndex.None, takeFromValidStorage: true);
            yield return goToReceiver;
            var deliver = Toils_General.Do(() =>
            {
                if (LoadingMissile)
                {
                    if (Refuelable.Faction == pawn.Faction && !Refuelable.IsForbidden(pawn))
                        GunSupplyUtility.TryReceiveMissile(Refuelable, pawn.carryTracker.CarriedThing);
                }
                else
                {
                    CompRefuelable supply = Supply;
                    if (supply != null && !supply.IsFull
                        && (job.playerForced || supply.allowAutoRefuel && supply.ShouldAutoRefuelNowIgnoringFuelPct))
                        supply.Refuel(new List<Thing> { Fuel });
                }
                if (pawn.carryTracker.CarriedThing != null)
                    pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
            });
            yield return Toils_Jump.JumpIf(deliver, () => LoadingMissile);
            yield return Toils_General.Wait(RefuelingDuration)
                .FailOnDestroyedNullOrForbidden(TargetIndex.B).FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch)
                .WithProgressBarToilDelay(TargetIndex.A);
            yield return deliver.FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch);
        }
    }

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
            Job job = GunSupplyUtility.MakeSupplyJob(target, missile);
            job.count = 1;
            return job;
        }

        private static Thing FindMissile(Pawn pawn, Thing target)
        {
            CompChangeableProjectile chamber = target?.TryGetComp<CompChangeableProjectile>();
            if (chamber == null || chamber.Loaded || !target.Spawned
                || target.Faction != pawn.Faction || target.IsForbidden(pawn)
                || !pawn.CanReserveAndReach(target, PathEndMode.Touch, Danger.Deadly)) return null;
            return FindReachableMissile(pawn, target, t => chamber.allowedShellsSettings.AllowedToAccept(t));
        }

        internal static Thing FindReachableMissile(Pawn pawn, Thing target, System.Predicate<Thing> accepts)
        {
            return GenClosest.ClosestThingReachable(pawn.Position, target.Map,
                ThingRequest.ForGroup(ThingRequestGroup.Shell), PathEndMode.OnCell,
                TraverseParms.For(pawn), 40f, t => t.def.GetModExtension<DefModExtension_SpaceMissileAmmo>() != null
                    && !t.IsForbidden(pawn) && pawn.CanReserve(t, 10, 1)
                    && accepts(t));
        }
    }

    public class WorkGiver_LoadMissileMagazine : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest =>
            ThingRequest.ForDef(DeltaVDefOf.DeltaV_Building_MissileMagazine);
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override bool HasJobOnThing(Pawn pawn, Thing target, bool forced = false) =>
            FindMissile(pawn, target) != null;

        public override Job JobOnThing(Pawn pawn, Thing target, bool forced = false)
        {
            Thing missile = FindMissile(pawn, target);
            if (missile == null) return null;
            Job job = GunSupplyUtility.MakeSupplyJob(target, missile);
            job.count = 1;
            return job;
        }

        private static Thing FindMissile(Pawn pawn, Thing target)
        {
            CompMissileMagazineFacility magazine = target?.TryGetComp<CompMissileMagazineFacility>();
            if (magazine == null || !target.Spawned || target.Faction != pawn.Faction
                || target.IsForbidden(pawn)
                || !pawn.CanReserveAndReach(target, PathEndMode.Touch, Danger.Deadly)
                || magazine.StoredCount >= magazine.TotalCapacity) return null;
            List<CompChangeableProjectile> directLoads = EmptyReachableLaunchers(target, pawn);
            return WorkGiver_LoadMissileLauncher.FindReachableMissile(pawn, target,
                t => magazine.CanAccept(t.def)
                    && !directLoads.Exists(chamber =>
                        chamber.allowedShellsSettings.AllowedToAccept(t.def)));
        }

        private static List<CompChangeableProjectile> EmptyReachableLaunchers(Thing magazine, Pawn pawn)
        {
            var result = new List<CompChangeableProjectile>();
            int ownerId = SpaceWeaponGeometry.OwnerIdOf(magazine);
            IEnumerable<CompSpaceWeaponMagazine> candidates;
            var index = Current.Game?.GetComponent<UniverseManager>()?.ShipStructure;
            if (index?.Tracks(magazine.Map) == true)
                candidates = index.For(magazine.Map).MissileChambers(ownerId);
            else
                candidates = magazine.Map.listerThings.ThingsOfDef(DeltaVDefOf.DeltaV_Weapon_MissileLauncher)
                    .Select(launcher => launcher.TryGetComp<CompSpaceWeaponMagazine>())
                    .Where(chamber => chamber != null);
            foreach (CompSpaceWeaponMagazine candidate in candidates)
            {
                Thing launcher = candidate.parent;
                if (!launcher.Spawned || launcher.Faction != magazine.Faction
                    || ownerId > 0 && SpaceWeaponGeometry.OwnerIdOf(launcher) != ownerId)
                    continue;
                if (candidate.ChangeableProjectile is { Loaded: false } chamber
                    && !launcher.IsForbidden(pawn)
                    && pawn.CanReach(launcher, PathEndMode.Touch, Danger.Deadly))
                    result.Add(chamber);
            }
            return result;
        }
    }

}
