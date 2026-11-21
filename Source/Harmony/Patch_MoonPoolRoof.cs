using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.SetRoof))]
    public static class Patch_MoonPool_SetRoof
    {
        private static void Prefix(RoofGrid __instance, IntVec3 c, out RoofDef __state) => __state = __instance.RoofAt(c);
        public static void Postfix(Map ___map, IntVec3 c, RoofDef __state)
        {
            MoonPoolUtility.SyncRoofAt(___map, c);
            Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.NotifyRoof(___map, c, __state, ___map.roofGrid.RoofAt(c));
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.ExposeData))]
    public static class Patch_MoonPool_LoadRoofs
    {
        public static void Postfix(Map __instance)
        {
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                MoonPoolUtility.SyncRoofsOnLoad(__instance);
        }
    }

    // RoofGrid remains the source of truth. A roof is ignored only while one
    // shuttle validation or lighting mesh call is on this thread's stack.
    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.Roofed), new[] { typeof(IntVec3) })]
    public static class Patch_MoonPool_RoofedCell
    {
        public static bool Prefix(Map ___map, IntVec3 c, ref bool __result)
        {
            if (!MoonPoolUtility.IgnoreRoofForCurrentCall(___map, c)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.Roofed), new[] { typeof(int) })]
    public static class Patch_MoonPool_RoofedIndex
    {
        public static bool Prefix(Map ___map, int index, ref bool __result)
        {
            if (___map == null || !MoonPoolUtility.IgnoreRoofForCurrentCall(___map, ___map.cellIndices.IndexToCell(index)))
                return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.Roofed), new[] { typeof(int), typeof(int) })]
    public static class Patch_MoonPool_RoofedXZ
    {
        public static bool Prefix(Map ___map, int x, int z, ref bool __result)
        {
            if (!MoonPoolUtility.IgnoreRoofForCurrentCall(___map, new IntVec3(x, 0, z))) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.RoofAt), new[] { typeof(int) })]
    public static class Patch_MoonPool_LightingRoofAt
    {
        public static void Postfix(Map ___map, int index, ref RoofDef __result)
        {
            if (MoonPoolUtility.LightingActive && __result == MoonPoolUtility.FieldRoof
                && MoonPoolUtility.IsMoonPoolAt(___map, ___map.cellIndices.IndexToCell(index)))
                __result = null;
        }
    }

    [HarmonyPatch(typeof(SectionLayer_LightingOverlay), "GenerateLightingOverlay")]
    public static class Patch_MoonPool_LightingOverlay
    {
        public static void Prefix(ref bool __state)
        {
            MoonPoolUtility.BeginLighting();
            __state = true;
        }

        public static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) MoonPoolUtility.EndLighting();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.GroundGlowAt))]
    public static class Patch_MoonPool_Sunlight
    {
        public static void Postfix(GlowGrid __instance, Map ___map, IntVec3 c, bool ignoreSky, ref float __result)
        {
            if (!ignoreSky && ___map?.skyManager != null && MoonPoolUtility.IsMoonPoolAt(___map, c)
                && MoonPoolUtility.IsFieldRoofAt(___map, c))
                __result = UnityEngine.Mathf.Max(__result, ___map.skyManager.CurSkyGlow);
        }
    }

    [HarmonyPatch(typeof(CompPowerPlantSolar), "get_RoofedPowerOutputFactor")]
    public static class Patch_MoonPool_SolarTransmission
    {
        public static void Prefix(ref bool __state)
        {
            MoonPoolUtility.BeginLighting();
            __state = true;
        }

        public static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) MoonPoolUtility.EndLighting();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Graphic_Shadow), nameof(Graphic_Shadow.DrawWorker))]
    public static class Patch_MoonPool_SunShadow
    {
        public static void Prefix(UnityEngine.Vector3 loc, ref bool __state)
        {
            Map map = Find.CurrentMap;
            if (!MoonPoolUtility.IsFieldRoofAt(map, loc.ToIntVec3())) return;
            MoonPoolUtility.BeginLighting();
            __state = true;
        }

        public static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) MoonPoolUtility.EndLighting();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(CompLaunchable), nameof(CompLaunchable.CanLaunch))]
    public static class Patch_MoonPool_ShuttleLaunch
    {
        public static void Prefix(CompLaunchable __instance, ref bool __state)
        {
            if (__instance.parent?.TryGetComp<CompShuttle>() == null) return;
            List<CompTransporter> group = __instance.parent.GetComp<CompTransporter>()
                ?.TransportersInGroup(__instance.parent.Map);
            if (group == null || group.Any(t => t?.parent?.TryGetComp<CompShuttle>() == null)) return;
            MoonPoolUtility.BeginShuttlePassage();
            __state = true;
        }

        public static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) MoonPoolUtility.EndShuttlePassage();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(PlaceWorker_NotUnderRoof), nameof(PlaceWorker_NotUnderRoof.AllowsPlacing))]
    public static class Patch_MoonPool_ShuttlePlacement
    {
        public static void Prefix(BuildableDef checkingDef, ref bool __state)
        {
            if ((checkingDef as ThingDef)?.GetCompProperties<CompProperties_Shuttle>() == null) return;
            MoonPoolUtility.BeginShuttlePassage();
            __state = true;
        }

        public static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) MoonPoolUtility.EndShuttlePassage();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Designator_AreaBuildRoof), nameof(Designator_AreaBuildRoof.CanDesignateCell))]
    public static class Patch_MoonPool_NoBuildRoofDesignation
    {
        public static bool Prefix(Designator_AreaBuildRoof __instance, IntVec3 c, ref AcceptanceReport __result)
        {
            if (!MoonPoolUtility.IsMoonPoolAt(Find.CurrentMap, c)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Designator_AreaNoRoof), nameof(Designator_AreaNoRoof.CanDesignateCell))]
    public static class Patch_MoonPool_NoFieldRemovalDesignation
    {
        public static bool Prefix(Designator_AreaNoRoof __instance, IntVec3 c, ref AcceptanceReport __result)
        {
            if (!MoonPoolUtility.IsFieldRoofAt(Find.CurrentMap, c)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(WorkGiver_RemoveRoof), nameof(WorkGiver_RemoveRoof.HasJobOnCell))]
    public static class Patch_MoonPool_NoFieldRemovalJob
    {
        public static bool Prefix(Pawn pawn, IntVec3 c, ref bool __result)
        {
            if (!MoonPoolUtility.IsFieldRoofAt(pawn?.Map, c)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(RoofCollapseBufferResolver), nameof(RoofCollapseBufferResolver.CollapseRoofsMarkedToCollapse))]
    public static class Patch_MoonPool_NoFieldCollapse
    {
        public static void Prefix(Map ___map)
        {
            ___map?.roofCollapseBuffer?.CellsMarkedToCollapse.RemoveAll(c => MoonPoolUtility.IsFieldRoofAt(___map, c));
        }
    }

    [HarmonyPatch(typeof(RoofCollapserImmediate), nameof(RoofCollapserImmediate.DropRoofInCells),
        new[] { typeof(IntVec3), typeof(Map), typeof(List<Thing>) })]
    public static class Patch_MoonPool_NoImmediateFieldCollapse
    {
        public static bool Prefix(IntVec3 c, Map map) => !MoonPoolUtility.IsFieldRoofAt(map, c);
    }

    [HarmonyPatch(typeof(RoofCollapserImmediate), nameof(RoofCollapserImmediate.DropRoofInCells),
        new[] { typeof(List<IntVec3>), typeof(Map), typeof(List<Thing>) })]
    public static class Patch_MoonPool_NoBulkFieldCollapse
    {
        public static void Prefix(ref List<IntVec3> cells, Map map)
        {
            cells = cells?.Where(c => !MoonPoolUtility.IsFieldRoofAt(map, c)).ToList();
        }
    }

    [HarmonyPatch(typeof(RoofCollapserImmediate), nameof(RoofCollapserImmediate.DropRoofInCells),
        new[] { typeof(IEnumerable<IntVec3>), typeof(Map), typeof(List<Thing>) })]
    public static class Patch_MoonPool_NoEnumerableFieldCollapse
    {
        public static void Prefix(ref IEnumerable<IntVec3> cells, Map map)
        {
            cells = cells?.Where(c => !MoonPoolUtility.IsFieldRoofAt(map, c)).ToList();
        }
    }
}
