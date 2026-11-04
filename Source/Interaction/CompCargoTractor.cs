using System.Collections.Generic;
using System.Linq;
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
    public class CompCargoTractor : CompSpaceTractor
    {
        public override bool ManualCargo => true;
        public override bool CanOperate(CompSpaceWeapon weapon) => parent.TryGetComp<CompVehicleCargo>() != null
            && !parent.IsForbidden(Faction.OfPlayer);
        internal override bool Allows(Thing thing) => thing.def.category == ThingCategory.Item;
        public override void PostExposeData() { } // Cargo receiver has no moonpool storage or pawn filters.
        public override IEnumerable<Gizmo> CompGetGizmosExtra() { yield break; }
        public override bool HasReachedReceiver(TractorReceiveContext context)
            => OrbitSphere.ArcCells(context.Target.pose.position, context.Destination)
                <= ((CompProperties_CargoTractor)props).collectionRadius;
        public override TractorReceiveResult TryReceive(TractorReceiveContext context)
        {
            Map source = context.Target.map;
            CompVehicleCargo cargo = parent.TryGetComp<CompVehicleCargo>();
            if (source == null || source.Disposed || cargo == null) return TractorReceiveResult.Rejected;
            bool moved = false;
            foreach (Thing thing in source.listerThings.AllThings.Where(SpaceScatterService.IsCargo).ToList())
            {
                if (!Allows(thing)) return moved ? TractorReceiveResult.Partial : TractorReceiveResult.Rejected;
                float unitMass = thing.GetStatValue(StatDefOf.Mass);
                int count = unitMass <= 0f ? thing.stackCount : Mathf.Min(thing.stackCount, Mathf.FloorToInt((cargo.Remaining + .0001f) / unitMass));
                if (count <= 0) return moved ? TractorReceiveResult.Partial : TractorReceiveResult.Rejected;
                IntVec3 previous = thing.Position;
                Thing part = thing.SplitOff(count);
                if (part.Spawned) part.DeSpawn();
                try
                {
                    int offered = part.stackCount;
                    if (!cargo.Transporter.innerContainer.TryAdd(part, canMergeWithExistingStacks: true))
                    {
                        moved |= part.Destroyed || part.stackCount < offered;
                        ShipPhysics.InvalidateMass(context.Receiver.map);
                        if (!part.Destroyed && !part.Spawned && part.holdingOwner == null) GenSpawn.Spawn(part, previous, source);
                        return moved ? TractorReceiveResult.Partial : TractorReceiveResult.Rejected;
                    }
                    moved = true;
                    ShipPhysics.InvalidateMass(context.Receiver.map);
                }
                catch { if (!part.Destroyed && !part.Spawned && part.holdingOwner == null) GenSpawn.Spawn(part, previous, source); throw; }
            }
            return source.listerThings.AllThings.Any(SpaceScatterService.IsCargo)
                ? TractorReceiveResult.Partial : TractorReceiveResult.Complete;
        }
    }
}
