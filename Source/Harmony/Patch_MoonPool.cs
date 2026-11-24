using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    // 原版地形变更效果覆盖正常建造、拆除和临时地形切换；GravshipUnsafe 绕过该效果。
    [HarmonyPatch(typeof(TerrainGrid), "DoTerrainChangedEffects")]
    public static class Patch_MoonPool_TerrainChanged
    {
        public static void Postfix(TerrainGrid __instance, IntVec3 c)
        {
            Map map = TerrainGridOwnershipAccess.MapOf(__instance);
            MoonPoolUtility.SyncRoofAt(map, c);
        }
    }

    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.RemoveGravshipTerrainUnsafe))]
    public static class Patch_MoonPool_GravshipTerrainRemoved
    {
        public static void Postfix(TerrainGrid __instance, IntVec3 cell)
        {
            Map map = TerrainGridOwnershipAccess.MapOf(__instance);
            MoonPoolUtility.SyncRoofAt(map, cell);
        }
    }

    // GravshipUtility and Delta-V's mirror remove the roof after removing the terrain layer.
    // If a normal ship roof covered restored moon-pool terrain, the terrain callback correctly
    // preserves that roof and cannot install the field roof until this final raw removal occurs.
    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.RemoveRoofUnsafe))]
    public static class Patch_MoonPool_GravshipRoofRemoved
    {
        public static void Postfix(Map ___map, int index)
        {
            if (___map == null) return;
            MoonPoolUtility.SyncRoofAt(___map, ___map.cellIndices.IndexToCell(index));
        }
    }
}
