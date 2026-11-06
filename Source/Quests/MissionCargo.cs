using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    public sealed class MissionCargo : ThingWithComps
    {
        public int missionId, boxNumber, count;
        public ThingDef contents;
        public Faction issuer;
        private bool opened;
        public bool Sealed => !opened && !Destroyed;
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref missionId, "missionId"); Scribe_Values.Look(ref boxNumber, "boxNumber");
            Scribe_Values.Look(ref count, "contentCount"); Scribe_Defs.Look(ref contents, "contents");
            Scribe_References.Look(ref issuer, "issuer"); Scribe_Values.Look(ref opened, "opened");
        }
        public override string GetInspectString() => "DeltaV_Mission_CrateInfo".Translate(issuer?.Name ?? "—", missionId, boxNumber);
        public bool Open(Pawn pawn)
        {
            if (Destroyed || !Spawned || pawn?.Map != Map || !pawn.Position.AdjacentTo8WayOrInside(Position)
                || contents == null || count <= 0) return false;
            // Save the remaining manifest after each successful placement; retries cannot duplicate cargo.
            opened = true;
            while (count > 0)
            {
                Thing product = ThingMaker.MakeThing(contents);
                product.stackCount = System.Math.Min(count, contents.stackLimit);
                bool placed = GenPlace.TryPlaceThing(product, Position, Map, ThingPlaceMode.Near,
                    placedAction: (_, amount) => count -= amount);
                ShipPhysics.InvalidateMass(Map);
                // Native placement may merge a partial stack before returning false.
                if (!placed) { if (!product.Destroyed) product.Destroy(); return false; }
            }
            ShipPhysics.InvalidateMass(Map);
            Destroy();
            return true;
        }
    }

    public sealed class StatPart_MissionCargoMass : StatPart
    {
        public override void TransformValue(StatRequest req, ref float val)
        {
            if (req.Thing is MissionCargo cargo && cargo.contents != null)
                val += cargo.contents.GetStatValueAbstract(StatDefOf.Mass) * cargo.count;
        }
        public override string ExplanationPart(StatRequest req) => null;
    }
    public sealed class CompUseEffect_OpenMissionCargo : CompUseEffect
    {
        public override void DoEffect(Pawn usedBy)
        {
            base.DoEffect(usedBy);
            if (parent is MissionCargo cargo) cargo.Open(usedBy);
        }
        public override AcceptanceReport CanBeUsedBy(Pawn p) => p?.Faction == Faction.OfPlayer;
    }
    public sealed class CompUsable_MissionCargo : CompUsable
    {
        protected override string FloatMenuOptionLabel(Pawn pawn) => "DeltaV_Mission_OpenCrate".Translate();
    }
}
