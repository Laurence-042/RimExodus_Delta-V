using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public class CompProperties_LocalShuttle : CompProperties_Shuttle
    {
        public CompProperties_LocalShuttle() { compClass = typeof(CompLocalShuttle); }
    }
    // Local craft use transporter jobs, not TransportShip world travel or its timed passenger unloading.
    public class CompLocalShuttle : CompShuttle
    {
        public override void CompTick() { }
        public override bool IsAllowed(Thing thing) => thing.def.category == ThingCategory.Item || base.IsAllowed(thing);
        public override bool IsAllowedNow(Thing thing) => thing.def.category == ThingCategory.Item || base.IsAllowedNow(thing);
        public override IEnumerable<Gizmo> CompGetGizmosExtra() { yield break; }
    }
    public class CompProperties_EvaSuit : CompProperties_SpaceShuttle
    {
        public CompProperties_EvaSuit() { compClass = typeof(CompEvaSuit); }
    }
    public class CompEvaSuit : CompSpaceShuttle
    {
        private ShipBase eva;
        private bool initialized;
        public override ShipBase Vessel => eva ??= new EvaShip(this);
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            if (!initialized)
            {
                combat.fireControlMode = Vessel.DefaultFireControlMode;
                initialized = true;
            }
            base.PostSpawnSetup(respawningAfterLoad);
            var shuttle = parent.TryGetComp<CompShuttle>();
            if (shuttle != null)
            {
                shuttle.requiredColonistCount = 1;
                shuttle.maxColonistCount = 1;
                shuttle.acceptColonists = true;
                shuttle.onlyAcceptColonists = true;
                shuttle.onlyAcceptHealthy = true;
            }
            SpaceObject container = Container;
            if (Vessel.CanDriveIn(container)) container.pose.angularVelocity = 0f;
        }
        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref initialized, "evaInitialized", false);
        }
    }
    public sealed class EvaShip : ShuttleShip, IHardDockingExecutor
    {
        public EvaShip(CompSpaceShuttle hull) : base(hull) { }
        public override bool RequiresHeadingAlignment => false;
        public override bool RequiresAngularVelocityMatch => false;
        public override bool MatchDockingPointVelocity => true;
        public override bool AllowsAngularMotion => false;
        public override bool UsesCameraInput => true;
        public override bool CanDisableCollisionAvoidance => false;
        public override int FlightMapMargin => 0;
        public override bool ReserveRotationBounds => true;
        public override bool HasGunnerStations => false;
        public override ShipFireControlMode DefaultFireControlMode => ShipFireControlMode.Manual;
        public override bool SupportsFireControlMode(ShipFireControlMode mode) => mode != ShipFireControlMode.Automatic;
        public override ShipAimHandler CreateAimHandler(UniverseManager universe) => new ArcadeShipAimHandler(universe, this);

        private CompSpaceWeapon CaptureTether => Weapons
            .FirstOrDefault(weapon => weapon.Component<CompCargoTractor>() != null);

        internal override IHardDockingExecutor HardDockingExecutor => this;
        ShipBase IHardDockingExecutor.Vessel => this;
        Thing IHardDockingExecutor.Body => Hull.parent;
        ThingDef IHardDockingExecutor.CollisionDef => Hull.parent.def;
        HardDockingCompletion IHardDockingExecutor.Completion => HardDockingCompletion.Arrival;
        bool IHardDockingExecutor.RequiresAvoidanceDisabled => false;
        bool IHardDockingExecutor.UsesProjectileRole => false;
        float IHardDockingExecutor.ArrivalVelocityTolerance =>
            HardDockingService.LightArrivalVelocityTolerance;
        float IHardDockingExecutor.ArrivalApproachSpeed => HardDockingService.LightApproachSpeed;
        CompSpaceWeapon IHardDockingExecutor.CaptureTether => CaptureTether;
        float IHardDockingExecutor.NoseHalfWidth => 0f;
        float IHardDockingExecutor.NoseForwardOffset => 0f;
        string IHardDockingExecutor.StartLabelKey => "DeltaV_PersonnelLanding";
        string IHardDockingExecutor.StartTipKey => "DeltaV_PersonnelLandingDesc";
        string IHardDockingExecutor.CancelLabelKey => "DeltaV_PersonnelLandingCancel";
        string IHardDockingExecutor.CancelTipKey => "DeltaV_PersonnelLandingCancelDesc";
        string IHardDockingExecutor.BlockReasonKey(UniverseManager universe, SpaceObject source,
            bool playerInitiated) => CaptureTether != null && CanDriveIn(source)
                ? null : "DeltaV_PersonnelLandingFailed";
        bool IHardDockingExecutor.ValidFor(SpaceObject source) => IsValid && CanDriveIn(source)
            && CaptureTether != null;
        bool IHardDockingExecutor.TryResolveTarget(SpaceObject target, Vector2 targetMapPoint,
            out HardDockingDestination destination)
        {
            IntVec3 cell = new IntVec3(Mathf.FloorToInt(targetMapPoint.x), 0,
                Mathf.FloorToInt(targetMapPoint.y));
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            LandingApproachController.DockingPlan plan = universe == null ? null
                : LandingApproachController.PlacementAt(universe, PilotTarget.Of(this), target, cell);
            destination = new HardDockingDestination(cell, plan);
            return plan != null && LandingApproachController.LandingSiteStillValid(plan);
        }
        bool IHardDockingExecutor.TargetStillValid(SpaceObject target,
            HardDockingDestination destination)
            => destination.state is LandingApproachController.DockingPlan plan
            && plan.Target.Identity == Identity && plan.targetMap == target?.map
            && plan.landingCell == destination.cell && target.map?.Disposed == false;
        void IHardDockingExecutor.NotifyHardDockingActive(bool active) =>
            Hull.intrusionDriveActive = active;
        void IHardDockingExecutor.CompleteHardDocking(UniverseManager universe,
            HardDockingContact contact)
        {
            if (contact.targetState is LandingApproachController.DockingPlan plan
                && plan.targetMap == contact.target?.map
                && plan.landingCell == contact.targetCell
                && LandingApproachController.LandingSiteStillValid(plan)
                && universe.Worksites.Dock(PilotTarget.Of(this), plan)) return;
            Messages.Message("DeltaV_PersonnelLandingBlocked".Translate(),
                MessageTypeDefOf.RejectInput, false);
        }
    }

    public class CompProperties_VehicleCargo : CompProperties_Transporter
    {
        public int crewCount = 1;
        public CompProperties_VehicleCargo() { compClass = typeof(CompVehicleCargo); }
    }
    public class CompVehicleCargo : CompTransporter
    {
        public CompProperties_VehicleCargo CargoProps => (CompProperties_VehicleCargo)props;
        public float CargoMass => parent.GetComp<CompStructureMass>().CurrentMass;
        public float Remaining => Mathf.Max(0f, MassCapacity - CargoMass);
        internal double MassOf(Thing item) => item is Pawn ? 0d : CompStructureMass.StackMass(item);

        public AcceptanceReport CanDepart()
        {
            var crew = innerContainer.OfType<Pawn>().ToList();
            if (crew.Count != CargoProps.crewCount || crew.Any(p => p.Dead || p.Downed))
                return "DeltaV_EvaCrewRequired".Translate(CargoProps.crewCount);
            if (CargoMass > MassCapacity + .0001f) return "CommandLaunchGroupFailOverMassCapacity".Translate();
            return true;
        }
        public override string CompInspectStringExtra() => "DeltaV_EvaCargo".Translate(CargoMass.ToString("0.#"), MassCapacity.ToString("0.#"));
    }

    public class CompProperties_LocalLaunchable : CompProperties_Launchable
    {
        public CompProperties_LocalLaunchable() { compClass = typeof(CompLocalLaunchable); }
    }
    public class CompLocalLaunchable : CompLaunchable
    {
        public override float FuelLevel => 1f;
        public override float MaxFuelLevel => 1f;
        public override bool RequiresFuelingPort => false;
        public override IEnumerable<Gizmo> CompGetGizmosExtra() { yield break; }
        public override AcceptanceReport CanLaunch(float? overrideFuelLevel = null)
        {
            AcceptanceReport report = base.CanLaunch(overrideFuelLevel);
            if (!report) return report;
            return (parent.TryGetComp<CompTransporter>() as CompVehicleCargo)?.CanDepart() ?? AcceptanceReport.WasAccepted;
        }
    }

    [HarmonyPatch(typeof(CompLaunchable), nameof(CompLaunchable.TryLaunch))]
    internal static class Patch_LocalOnlyLaunch
    {
        private static bool Prefix(CompLaunchable __instance) => __instance is not CompLocalLaunchable;
    }
    [HarmonyPatch(typeof(CompTransporter), nameof(CompTransporter.MassUsage), MethodType.Getter)]
    internal static class Patch_VehicleCargoMass
    {
        private static bool Prefix(CompTransporter __instance, ref float __result)
        {
            if (__instance is not CompVehicleCargo eva) return true;
            __result = eva.CargoMass;
            return false;
        }
    }

    [HarmonyPatch(typeof(Dialog_LoadTransporters), "get_MassUsage")]
    internal static class Patch_VehicleLoadingMass
    {
        private static void Postfix(List<CompTransporter> ___transporters, List<TransferableOneWay> ___transferables, ref float __result)
        {
            if (___transporters.Count != 1 || ___transporters[0] is not CompVehicleCargo) return;
            __result = ___transferables.Where(t => t.AnyThing is not Pawn)
                .Sum(t => t.CountToTransfer * t.AnyThing.GetStatValue(StatDefOf.Mass));
        }
    }
    [HarmonyPatch(typeof(Dialog_LoadTransporters), "CheckForErrors")]
    internal static class Patch_VehicleLoadingCrew
    {
        private static bool Prefix(List<CompTransporter> ___transporters, List<Pawn> pawns, ref bool __result)
        {
            if (___transporters.Count != 1 || ___transporters[0] is not CompVehicleCargo cargo
                || pawns.Count == cargo.CargoProps.crewCount) return true;
            Messages.Message("DeltaV_EvaCrewRequired".Translate(cargo.CargoProps.crewCount), MessageTypeDefOf.RejectInput, false);
            __result = false;
            return false;
        }
    }
}
