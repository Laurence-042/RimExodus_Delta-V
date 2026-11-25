using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public class CompProperties_CargoTractor : CompProperties_SpaceTractor
    {
        public float collectionRadius = .5f;
        public CompProperties_CargoTractor() { compClass = typeof(CompCargoTractor); }
    }
    public class CompCargoTractor : CompSpaceTractor, ILightTractorEmitter
    {
        public override bool ManualCargo => true;
        public override bool CanOperate(CompSpaceWeapon weapon) => parent.TryGetComp<CompTransporter>() is CompVehicleCargo
            && !parent.IsForbidden(Faction.OfPlayer);
        internal override bool Allows(Thing thing) => thing.def.category == ThingCategory.Item;
        public override void PostExposeData() { } // Cargo receiver has no moonpool storage or pawn filters.
        public override IEnumerable<Gizmo> CompGetGizmosExtra() { yield break; }
        public override bool HasReachedReceiver(TractorReceiveContext context)
            => OrbitSphere.ArcCells(context.Target.pose.position, context.Destination)
                <= ((CompProperties_CargoTractor)props).collectionRadius;
        public override TractorReceiveResult TryReceive(TractorReceiveContext context)
        {
            AbstractThing payload = context.Target.Payload;
            CompVehicleCargo cargo = parent.TryGetComp<CompTransporter>() as CompVehicleCargo;
            if (payload?.Recoverable != true || cargo == null || !Allows(payload.Entity))
                return TractorReceiveResult.Rejected;
            Thing thing = payload.Entity;
            float unitMass = thing.GetStatValue(StatDefOf.Mass);
            int count = unitMass <= 0f ? thing.stackCount
                : Mathf.Min(thing.stackCount, Mathf.FloorToInt((cargo.Remaining + .0001f) / unitMass));
            return payload.Handoff(cargo.innerContainer, count);
        }

        float ILightTractorEmitter.ContactRadius =>
            ((CompProperties_CargoTractor)props).collectionRadius;
        float ILightTractorEmitter.ImpulsePerTick => CompPawnGrapple.ImpulsePerTick;
        bool ILightTractorEmitter.ValidSource(UniverseManager universe, SpaceObject source,
            CompSpaceWeapon weapon) => weapon?.parent == parent && source?.map == parent.Map
                && SpaceTractor.CanOperate(weapon);
        bool ILightTractorEmitter.AllowsTarget(SpaceObject target) =>
            (target?.map != null && !target.HasTag(SpaceObjectTag.Personnel))
            || (target?.IsScattered == true && target.Payload?.Recoverable == true
                && Allows(target.Payload.Entity));
        bool ILightTractorEmitter.MoveTarget(SpaceObject target) => target?.IsScattered == true;
        TractorReceiveResult ILightTractorEmitter.TryReceive(UniverseManager universe,
            SpaceObject source, SpaceObject target)
            => TryReceive(new TractorReceiveContext(universe,
                parent.TryGetComp<CompVirtualWeapons>()?.WeaponFor(this), target, source));
        void ILightTractorEmitter.NotifyBlocked() => NotifyBlocked();
    }
}
