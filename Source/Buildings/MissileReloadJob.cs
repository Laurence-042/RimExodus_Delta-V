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
            Job job = JobMaker.MakeJob(DeltaVDefOf.DeltaV_LoadMissileLauncher, target, missile);
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
            foreach (Thing launcher in magazine.Map.listerThings
                .ThingsOfDef(DeltaVDefOf.DeltaV_Weapon_MissileLauncher))
            {
                if (!launcher.Spawned || launcher.Faction != magazine.Faction
                    || ownerId > 0 && SpaceWeaponGeometry.OwnerIdOf(launcher) != ownerId)
                    continue;
                if (launcher.TryGetComp<CompChangeableProjectile>() is { Loaded: false } chamber
                    && !launcher.IsForbidden(pawn)
                    && pawn.CanReach(launcher, PathEndMode.Touch, Danger.Deadly))
                    result.Add(chamber);
            }
            return result;
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
                CompMissileMagazineFacility magazine = launcher?.TryGetComp<CompMissileMagazineFacility>();
                bool loaded = missile != null && missile.stackCount == 1
                    && (chamber != null && !chamber.Loaded
                        && chamber.allowedShellsSettings.AllowedToAccept(missile)
                        || magazine != null && magazine.TryStore(missile.def));
                if (!loaded)
                {
                    if (missile != null)
                        pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                    return;
                }
                if (chamber != null) chamber.LoadShell(missile.def, 1);
                pawn.carryTracker.innerContainer.ClearAndDestroyContents();
            };
            yield return load;
        }
    }
}
