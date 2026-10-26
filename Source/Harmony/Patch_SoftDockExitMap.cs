using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DeltaV
{
    [HarmonyPatch(typeof(JobGiver_ExitMap), "TryGiveJob")]
    internal static class Patch_SoftDockExitMap
    {
        // Ship crew can reach the Humanlike fallback exit giver without a duty.
        // Both ends are pocket maps, so its portal search repeats after transfer.
        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (pawn?.Faction == Faction.OfPlayer || __result?.def != JobDefOf.EnterPortal
                || !(__result.targetA.Thing is SoftDockPortal)) return;
            __result = null;
        }
    }
}
