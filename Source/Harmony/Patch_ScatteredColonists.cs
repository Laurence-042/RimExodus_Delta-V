using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(GameEnder), nameof(GameEnder.CheckOrUpdateGameOver))]
    internal static class Patch_ScatteredColonistSurvival
    {
        private static void Postfix(GameEnder __instance)
        {
            if (__instance.gameEnding && Current.Game?.GetComponent<UniverseManager>() is UniverseManager universe
                && (universe.TransferringPawn is Pawn { Dead: false, Destroyed: false, IsFreeColonist: true }
                    || universe.Scatter.PlayerPawns.Any(p => p.IsFreeColonist)))
                __instance.gameEnding = false;
        }
    }

    [HarmonyPatch(typeof(GameEnder), nameof(GameEnder.CanSpawnNewWanderers))]
    internal static class Patch_ScatteredColonistWanderers
    {
        private static void Postfix(ref bool __result)
        {
            if (__result && Current.Game?.GetComponent<UniverseManager>() is UniverseManager universe
                && (universe.TransferringPawn is Pawn { Dead: false } pawn && pawn.Faction == Faction.OfPlayer
                    && pawn.RaceProps.Humanlike || universe.Scatter.PlayerPawns.Any(p => p.RaceProps.Humanlike)))
                __result = false;
        }
    }
}
