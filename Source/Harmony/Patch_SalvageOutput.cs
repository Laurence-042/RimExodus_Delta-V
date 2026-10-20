using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DeltaV
{
    [HarmonyPatch]
    internal static class Patch_SalvageOutput_Placement
    {
        // 两个重载最终汇入带 out 参数的入口，避免同一次放置重复回报。
        internal static MethodBase TargetMethod() => AccessTools.Method(typeof(GenPlace),
            nameof(GenPlace.TryPlaceThing), new[]
            {
                typeof(Thing), typeof(IntVec3), typeof(Map), typeof(ThingPlaceMode),
                typeof(Thing).MakeByRefType(), typeof(Action<Thing, int>),
                typeof(Predicate<IntVec3>), typeof(Rot4?), typeof(int)
            });

        private static void Postfix(Map map, bool __result) =>
            SalvageOutputScope.PlacementFinished(map, __result);
    }

    [HarmonyPatch(typeof(Log), nameof(Log.Warning), new[] { typeof(string) })]
    internal static class Patch_SalvageOutput_Warning
    {
        private static bool Prefix(string text) => !SalvageOutputScope.ReplacesWarning(text);
    }
}
