using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>Freshly generated humanlike crew receive Odyssey's complete vacuum outfit.</summary>
    public static class SpacePawnEquipment
    {
        public static void EnsureVacuumSuit(Pawn pawn)
        {
            if (pawn?.apparel == null || !pawn.RaceProps.Humanlike)
                return;

            // Children need the separate suit Def; the helmet accepts both stages.
            bool suit = TryWear(pawn, "Apparel_Vacsuit")
                || TryWear(pawn, "Apparel_VacsuitChildren");
            bool helmet = TryWear(pawn, "Apparel_VacsuitHelmet");
            if (!suit || !helmet)
            {
                Log.Warning($"[DeltaV] Could not equip complete vacsuit for generated crew "
                    + $"{pawn.LabelShortCap} (suit={suit}, helmet={helmet}).");
            }
        }

        private static bool TryWear(Pawn pawn, string defName)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
                return false;
            foreach (Apparel worn in pawn.apparel.WornApparel)
            {
                if (worn.def == def)
                    return true;
            }
            if (!ApparelUtility.HasPartsToWear(pawn, def))
                return false;

            Apparel apparel = (Apparel)ThingMaker.MakeThing(def);
            if (!apparel.PawnCanWear(pawn, ignoreGender: true))
            {
                apparel.Destroy(DestroyMode.Vanish);
                return false;
            }
            // The pawn is not spawned yet. Replace conflicting generated clothing
            // without scattering it around the prefab map.
            pawn.apparel.Wear(apparel, dropReplacedApparel: false);
            return true;
        }
    }
}
