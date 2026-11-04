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
                combat.weaponGroupsSecondary = 1;
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
    public sealed class EvaShip : ShuttleShip
    {
        public EvaShip(CompSpaceShuttle hull) : base(hull) { }
        public override bool RequiresHeadingAlignment => false;
        public override bool RequiresAngularVelocityMatch => false;
        public override bool MatchDockingPointVelocity => true;
        public override bool AllowsAngularMotion => false;
        public override bool UsesCameraInput => true;
        public override int FlightMapMargin => 0;
        public override bool ReserveRotationBounds => true;
        public override bool HasGunnerStations => false;
        public override ShipFireControlMode DefaultFireControlMode => ShipFireControlMode.Manual;
        public override bool SupportsFireControlMode(ShipFireControlMode mode) => mode != ShipFireControlMode.Automatic;
        public override ShipAimHandler CreateAimHandler(UniverseManager universe) => new ArcadeShipAimHandler(universe, this);
    }

    public class CompProperties_VehicleCargo : CompProperties
    {
        public float cargoCapacity = 20f;
        public int crewCount = 1;
        public CompProperties_VehicleCargo() { compClass = typeof(CompVehicleCargo); }
    }
    public class CompVehicleCargo : ThingComp
    {
        public CompProperties_VehicleCargo CargoProps => (CompProperties_VehicleCargo)props;
        public CompTransporter Transporter => parent.TryGetComp<CompTransporter>();
        public float CargoMass => Transporter.innerContainer.Where(t => t is not Pawn).Sum(ItemMass);
        public float Remaining => Mathf.Max(0f, CargoProps.cargoCapacity - CargoMass);
        internal static float ItemMass(Thing item) => item.GetStatValue(StatDefOf.Mass) * item.stackCount;
        public AcceptanceReport CanDepart()
        {
            var crew = Transporter.innerContainer.OfType<Pawn>().ToList();
            if (crew.Count != CargoProps.crewCount || crew.Any(p => p.Dead || p.Downed))
                return "DeltaV_EvaCrewRequired".Translate(CargoProps.crewCount);
            if (CargoMass > CargoProps.cargoCapacity + .0001f) return "CommandLaunchGroupFailOverMassCapacity".Translate();
            return true;
        }
        public override string CompInspectStringExtra() => "DeltaV_EvaCargo".Translate(CargoMass.ToString("0.#"), CargoProps.cargoCapacity.ToString("0.#"));
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
            return parent.TryGetComp<CompVehicleCargo>()?.CanDepart() ?? AcceptanceReport.WasAccepted;
        }
    }

    [HarmonyPatch(typeof(CompLaunchable), nameof(CompLaunchable.TryLaunch))]
    internal static class Patch_LocalOnlyLaunch
    {
        private static bool Prefix(CompLaunchable __instance) => __instance is not CompLocalLaunchable;
    }
    [HarmonyPatch(typeof(CompTransporter), nameof(CompTransporter.MassCapacity), MethodType.Getter)]
    internal static class Patch_VehicleCargoCapacity
    {
        private static void Postfix(CompTransporter __instance, ref float __result)
        {
            CompVehicleCargo cargo = __instance.parent.TryGetComp<CompVehicleCargo>();
            if (cargo != null) __result = cargo.CargoProps.cargoCapacity + Mathf.Max(0f, __instance.MassUsage - cargo.CargoMass);
        }
    }
    [HarmonyPatch(typeof(Dialog_LoadTransporters), "get_MassUsage")]
    internal static class Patch_VehicleLoadingMass
    {
        private static void Postfix(List<CompTransporter> ___transporters, List<TransferableOneWay> ___transferables, ref float __result)
        {
            if (___transporters.Count != 1 || ___transporters[0].parent.TryGetComp<CompVehicleCargo>() == null) return;
            __result = ___transferables.Where(t => t.AnyThing is not Pawn)
                .Sum(t => t.CountToTransfer * t.AnyThing.GetStatValue(StatDefOf.Mass));
        }
    }
    [HarmonyPatch(typeof(Dialog_LoadTransporters), "get_MassCapacity")]
    internal static class Patch_VehicleLoadingCapacity
    {
        private static void Postfix(List<CompTransporter> ___transporters, ref float __result)
        {
            if (___transporters.Count == 1 && ___transporters[0].parent.TryGetComp<CompVehicleCargo>() is CompVehicleCargo cargo)
                __result = cargo.CargoProps.cargoCapacity;
        }
    }
    [HarmonyPatch(typeof(Dialog_LoadTransporters), "CheckForErrors")]
    internal static class Patch_VehicleLoadingCrew
    {
        private static bool Prefix(List<CompTransporter> ___transporters, List<Pawn> pawns, ref bool __result)
        {
            if (___transporters.Count != 1 || ___transporters[0].parent.TryGetComp<CompVehicleCargo>() is not CompVehicleCargo cargo
                || pawns.Count == cargo.CargoProps.crewCount) return true;
            Messages.Message("DeltaV_EvaCrewRequired".Translate(cargo.CargoProps.crewCount), MessageTypeDefOf.RejectInput, false);
            __result = false;
            return false;
        }
    }
}
