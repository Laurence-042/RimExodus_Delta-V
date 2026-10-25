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
}
