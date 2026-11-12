using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    internal static class PrefabArchitect
    {
        private const string CategoryDefName = "DeltaV_ShipPlanning";
        private static readonly MethodInfo CacheCategories = AccessTools.Method(typeof(MainTabWindow_Architect), "CacheDesPanels");
        private static readonly MethodInfo CacheSearch = AccessTools.Method(typeof(MainTabWindow_Architect), "CacheSearchState");

        private static bool IsPrefabCategory(DesignationCategoryDef category) => category?.defName == CategoryDefName;

        private static IEnumerable<DesignationCategoryDef> ArchitectCategories()
            => DebugSettings.godMode ? DefDatabase<DesignationCategoryDef>.AllDefs
                : DefDatabase<DesignationCategoryDef>.AllDefs.Where(category => !IsPrefabCategory(category));

        [HarmonyPatch(typeof(MainTabWindow_Architect), "CacheDesPanels")]
        private static class BuildCategoryList
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo allDefs = AccessTools.PropertyGetter(typeof(DefDatabase<DesignationCategoryDef>),
                    nameof(DefDatabase<DesignationCategoryDef>.AllDefs));
                MethodInfo categories = AccessTools.Method(typeof(PrefabArchitect), nameof(ArchitectCategories));
                var code = instructions.ToList();
                CodeInstruction source = code.Find(instruction => instruction.opcode == OpCodes.Call && allDefs.Equals(instruction.operand));
                if (source == null)
                {
                    Log.Error("[DeltaV] Could not locate the Architect category list source.");
                    return code;
                }
                source.operand = categories;
                return code;
            }

            private static void Postfix(MainTabWindow_Architect __instance, List<ArchitectCategoryTab> ___desPanelsCached)
            {
                DesignationCategoryDef selected = __instance.selectedDesPanel?.def;
                if (selected != null)
                    __instance.selectedDesPanel = ___desPanelsCached.Find(panel => panel.def == selected);
            }
        }

        [HarmonyPatch(typeof(MainTabWindow_Architect), nameof(MainTabWindow_Architect.PreOpen))]
        private static class OpenArchitect
        {
            private static void Prefix(MainTabWindow_Architect __instance) => CacheCategories.Invoke(__instance, null);
        }

        [HarmonyPatch(typeof(DebugWindowsOpener), "ToggleGodMode")]
        private static class ToggleGodMode
        {
            private static void Postfix() => RefreshOpenArchitect(rebuildCategories: true);
        }

        [HarmonyPatch(typeof(Prefs), nameof(Prefs.DevMode), MethodType.Setter)]
        private static class ToggleDeveloperMode
        {
            private static void Prefix(out bool __state) => __state = DebugSettings.godMode;

            private static void Postfix(bool __state)
            {
                if (__state != DebugSettings.godMode) RefreshOpenArchitect(rebuildCategories: true);
            }
        }

        internal static void RefreshOpenArchitect(bool rebuildCategories)
        {
            if (Find.UIRoot is not UIRoot_Play uiRoot) return;
            DesignatorManager designators = uiRoot.mapUI?.designatorManager;
            if (designators?.SelectedDesignator is Designator_Build build &&
                IsPrefabCategory(build.PlacingDef.designationCategory) && !build.Visible)
                designators.Deselect();

            MainTabWindow_Architect architect = Find.WindowStack?.WindowOfType<MainTabWindow_Architect>();
            if (architect == null) return;
            if (rebuildCategories)
            {
                CacheCategories.Invoke(architect, null);
                architect.Notify_ResolutionChanged();
            }
            CacheSearch.Invoke(architect, null);
        }

        // God mode short-circuits the vanilla designator getter before it calls place workers.
        [HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.Visible), MethodType.Getter)]
        private static class MarkerVisibility
        {
            private static bool Prefix(Designator_Build __instance, ref bool __result)
            {
                BuildableDef def = __instance.PlacingDef;
                if (!IsPrefabCategory(def.designationCategory)) return true;
                PlaceWorker_PrefabMarker worker = def.PlaceWorkers?.OfType<PlaceWorker_PrefabMarker>().FirstOrDefault();
                if (worker == null) return true;
                __result = worker.IsBuildDesignatorVisible(def);
                return false;
            }
        }

        // Architect search enumerates this list directly, without the gizmo visibility check.
        [HarmonyPatch(typeof(DesignationCategoryDef), nameof(DesignationCategoryDef.ResolvedAllowedDesignators), MethodType.Getter)]
        private static class MarkerSearch
        {
            private static void Postfix(DesignationCategoryDef __instance, ref IEnumerable<Designator> __result)
            {
                if (IsPrefabCategory(__instance)) __result = __result.Where(designator => designator.Visible);
            }
        }
    }
}
