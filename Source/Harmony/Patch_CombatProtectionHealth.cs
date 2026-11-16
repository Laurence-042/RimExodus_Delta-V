using HarmonyLib;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(Building), nameof(Building.HitPoints), MethodType.Setter)]
    internal static class Patch_CombatProtectionHealth
    {
        private static void Prefix(Building __instance, int value, out (Map Map, int Before) __state)
        {
            __state = default;
            if (__instance.Spawned && value != __instance.HitPoints)
                __state = (__instance.Map, __instance.HitPoints);
        }

        private static void Postfix(Building __instance, (Map Map, int Before) __state)
        {
            if (__state.Map != null)
                Current.Game?.GetComponent<UniverseManager>()?.NotifyProtectionHealth(__state.Map, __instance, __state.Before);
        }
    }
}
