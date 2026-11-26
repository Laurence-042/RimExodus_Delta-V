using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed class CompProperties_PawnGrapple : CompProperties
    {
        public CompProperties_PawnGrapple() { compClass = typeof(CompPawnGrapple); }
    }

    /// <summary>Personal grapple weapon capability. Runtime links live in TractorLinkService.</summary>
    public sealed class CompPawnGrapple : ThingComp, ISpaceWeaponFireGate, ISpaceWeaponLaunch,
        ILightTractorEmitter
    {
        internal const float Range = 32f;
        internal const float ImpulsePerTick = 0.8f;
        internal const float ContactRadius = 0.65f;

        public bool CanFire(SpaceWeaponShot shot) => shot?.manualFire == true
            && shot.container is MapSpaceObject { PersonnelPawn: { } pawn,
                PersonnelSnapshot: { } snapshot } && pawn == parent
            && snapshot.CanMove(pawn);

        public bool TryLaunch(SpaceWeaponShot shot)
            => Current.Game?.GetComponent<UniverseManager>()?.TractorLinks.Trigger(shot) == true;

        float ILightTractorEmitter.ContactRadius => ContactRadius;
        float ILightTractorEmitter.ImpulsePerTick => ImpulsePerTick;
        bool ILightTractorEmitter.ValidSource(UniverseManager universe, SpaceObject source,
            CompSpaceWeapon weapon) => source is MapSpaceObject { PersonnelPawn: { } pawn,
                PersonnelSnapshot: { } snapshot } && weapon?.parent == pawn && parent == pawn
                && snapshot.CanMove(pawn);
        bool ILightTractorEmitter.AllowsTarget(SpaceObject target) => target != null
            && (target.map != null || target.IsScattered || target.HasTag(SpaceObjectTag.Personnel));
        bool ILightTractorEmitter.MoveTarget(SpaceObject target) => target?.IsScattered == true
            || target?.HasTag(SpaceObjectTag.Personnel) == true;
        TractorReceiveResult ILightTractorEmitter.TryReceive(UniverseManager universe,
            SpaceObject source, SpaceObject target)
        {
            Pawn carrier = (source as MapSpaceObject)?.PersonnelPawn;
            if (carrier == null) return TractorReceiveResult.Rejected;
            if (target is MapSpaceObject { PersonnelPawn: Pawn active })
                return CanCarry(carrier, active)
                    && universe.Personnel.TryCarryActive(carrier, (MapSpaceObject)target)
                    ? TractorReceiveResult.Complete : TractorReceiveResult.Rejected;
            AbstractThing payload = target?.Payload;
            Thing entity = payload?.Entity;
            if (payload?.Recoverable != true || entity == null) return TractorReceiveResult.Rejected;
            TractorReceiveResult result;
            if (entity is Pawn pawn)
            {
                if (!CanCarry(carrier, pawn)) return TractorReceiveResult.Rejected;
                result = payload.Handoff(thing => carrier.carryTracker.TryStartCarry(thing));
            }
            else
            {
                int count = CargoCount(carrier, entity);
                result = count > 0
                    ? payload.Handoff(carrier.inventory.innerContainer, count)
                    : TractorReceiveResult.Rejected;
            }
            if (result == TractorReceiveResult.Complete) universe.UnregisterObject(target);
            return result;
        }

        void ILightTractorEmitter.NotifyBlocked() { }

        internal static int CargoCount(Pawn pawn, Thing thing)
        {
            if (pawn?.inventory?.innerContainer == null || thing?.def.category != ThingCategory.Item) return 0;
            float unit = Mathf.Max(0f, thing.GetStatValue(StatDefOf.Mass));
            if (unit <= 0f) return thing.stackCount;
            float remaining = Mathf.Max(0f, MassUtility.Capacity(pawn) - MassUtility.GearAndInventoryMass(pawn));
            return Mathf.Min(thing.stackCount, Mathf.FloorToInt((remaining + 0.0001f) / unit));
        }

        internal static bool CanCarry(Pawn carrier, Pawn target) => carrier?.carryTracker != null
            && carrier.carryTracker.CarriedThing == null && target != null && target != carrier
            && !target.Destroyed;
    }
}
