using HarmonyLib;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(Thing), nameof(Thing.DeSpawn))]
    internal static class Patch_ScatteredContents
    {
        private static void Prefix(Thing __instance, out Map __state) => __state = __instance.Map;
        private static void Postfix(Map __state) =>
            Current.Game?.GetComponent<UniverseManager>()?.Scatter.NotifyContentRemoved(__state);
    }
}
