using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(GameInitData), nameof(GameInitData.PrepForMapGen))]
    internal static class Patch_TutorialCandidateCrew
    {
        private static void Prefix(GameInitData __instance)
        {
            if (Find.Scenario?.AllParts.OfType<ScenPart_DeltaVTutorialArrival>().Any() != true) return;
            var crew = Current.Game.GetComponent<UniverseManager>().Tutorial.UnselectedCrew;
            crew.Clear();
            crew.AddRange(__instance.startingAndOptionalPawns.Skip(__instance.startingPawnCount));
            // Vanilla still prepares the selected captain and keeps these exact candidates in WorldPawns.
            // The scenario moves them out of WorldPawns into their actual caskets during map generation.
        }
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.TryOpenComms))]
    internal static class Patch_TutorialComms
    {
        private static bool Prefix(Faction __instance, Pawn negotiator)
        {
            var universe = Current.Game?.GetComponent<UniverseManager>();
            return universe?.tutorialRun == null || !universe.Tutorial.ReceiveComms(__instance, negotiator);
        }
    }
}
