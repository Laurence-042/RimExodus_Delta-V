using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DeltaV
{
    [HarmonyPatch]
    internal static class Patch_GlobePopulation
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MapPawns), nameof(MapPawns.RegisterPawn));
            yield return AccessTools.Method(typeof(MapPawns), nameof(MapPawns.DeRegisterPawn));
        }
        private static void Postfix(Map ___map) =>
            Current.Game?.GetComponent<UniverseManager>()?.GlobeDisplay.NotifyPopulationChanged(___map);
    }

    [HarmonyPatch]
    internal static class Patch_GlobeContainedPopulation
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ThingOwner), "NotifyAdded");
            yield return AccessTools.Method(typeof(ThingOwner), "NotifyRemoved");
        }
        private static void Postfix(ThingOwner __instance, Thing item)
        {
            if (item is Pawn)
                Current.Game?.GetComponent<UniverseManager>()?.GlobeDisplay.NotifyPopulationChanged(
                    ThingOwnerUtility.GetRootMap(__instance.Owner));
        }
    }
}
