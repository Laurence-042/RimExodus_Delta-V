using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    // Vanilla only exposes multi-selection tabs for IStorageGroupMember buildings.
    [HarmonyPatch(typeof(MainTabWindow_Inspect), nameof(MainTabWindow_Inspect.CurTabs), MethodType.Getter)]
    internal static class Patch_TractorInspectTabs
    {
        [HarmonyPostfix]
        internal static void Postfix(ref IEnumerable<InspectTabBase> __result)
        {
            if (__result != null || Find.ScreenshotModeHandler.Active || Find.Selector.SelectedObjects.Count < 2) return;
            if (ITab_TractorCargo.SelectedTractors(Find.Selector.SelectedObjects).Length > 0)
                __result = new[] { InspectTabManager.GetSharedInstance(typeof(ITab_TractorCargo)) };
        }
    }
}
