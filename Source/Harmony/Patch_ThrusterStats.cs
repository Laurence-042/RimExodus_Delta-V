using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(ThingDef), nameof(ThingDef.SpecialDisplayStats))]
    public static class Patch_ThrusterStats
    {
        [HarmonyPostfix]
        public static IEnumerable<StatDrawEntry> Postfix(IEnumerable<StatDrawEntry> values, ThingDef __instance)
        {
            foreach (StatDrawEntry entry in values) yield return entry;
            if (!ThrusterPower.IsThrusterDef(__instance)) yield break;
            yield return new StatDrawEntry(DefDatabase<StatCategoryDef>.GetNamed("DeltaV_General"),
                "DeltaV_StatBaseThrust".Translate(), ThrusterPower.BaseThrust(__instance).ToString("G"),
                "DeltaV_StatBaseThrustDesc".Translate(), 500);
        }
    }
}
