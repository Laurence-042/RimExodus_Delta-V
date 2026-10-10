using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    // 原版地形变更效果覆盖正常建造、拆除和临时地形切换；GravshipUnsafe 绕过该效果。
    [HarmonyPatch(typeof(TerrainGrid), "DoTerrainChangedEffects")]
    public static class Patch_MoonPool_TerrainChanged
    {
        public static void Prefix(TerrainGrid __instance, IntVec3 c)
        {
            MoonPoolUtility.NotifyTerrainChanged(TerrainGridOwnershipAccess.MapOf(__instance), c);
        }
    }

    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.RemoveGravshipTerrainUnsafe))]
    public static class Patch_MoonPool_GravshipTerrainRemoved
    {
        public static void Postfix(TerrainGrid __instance, IntVec3 cell)
        {
            MoonPoolUtility.NotifyTerrainChanged(TerrainGridOwnershipAccess.MapOf(__instance), cell);
        }
    }

    /// <summary>
    /// 月池补丁（太空战架构 §7，用户定案 2026-09-26；2026-09-27 收根修订）：
    ///
    /// ① **屋顶豁免 patch 在唯一根层**——<see cref="RoofGrid.Roofed"/> 三个重载：
    ///    月池格恒视为**无顶**。放置（PlaceWorker_NotUnderRoof）、起飞
    ///    （CompLaunchable.AnyInGroupIsUnderRoof）、着陆选点
    ///    （Designator_MoveGravship.IsValidCell 的 GravshipBlockedByRoof）以及一切
    ///    现有/未来的屋顶消费者自动一致——**禁止再在任何消费者层单独 patch 屋顶**
    ///    （第一版在 CanLaunch/NotUnderRoof/IsValidCell 各打一个上层补丁，每漏一个
    ///    就多修一次；已全部撤除，本类是唯一挂点）。
    ///
    /// ② **气密独立于屋顶**——<see cref="District.ExposedVacuumCount"/> Prefix：月池格
    ///    从真空暴露计数中剔除（无顶也不流失空气）。不依赖 Roofed 的返回值（月池在
    ///    ①中恒为无顶），也不会因「无顶」漏成真空——密封语义只住在这一处。
    /// </summary>
    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.Roofed), new[] { typeof(IntVec3) })]
    public static class Patch_MoonPool_RoofedCell
    {
        public static bool Prefix(Map ___map, IntVec3 c, ref bool __result)
        {
            if (___map != null && MoonPoolUtility.IsMoonPoolAt(___map, c))
            {
                __result = false; // 月池格恒无顶（力场不是屋顶——不挡任何屋顶检查）
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.Roofed), new[] { typeof(int) })]
    public static class Patch_MoonPool_RoofedIndex
    {
        public static bool Prefix(Map ___map, int index, ref bool __result)
        {
            if (___map != null
                && MoonPoolUtility.IsMoonPoolAt(___map, ___map.cellIndices.IndexToCell(index)))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.Roofed), new[] { typeof(int), typeof(int) })]
    public static class Patch_MoonPool_RoofedXZ
    {
        public static bool Prefix(Map ___map, int x, int z, ref bool __result)
        {
            if (___map != null && MoonPoolUtility.IsMoonPoolAt(___map, new IntVec3(x, 0, z)))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(District), nameof(District.ExposedVacuumCount))]
    public static class Patch_MoonPool_VacuumSeal
    {
        public static bool Prefix(District __instance, int threshold, ref int __result)
        {
            Map map = __instance.Map;
            if (map == null || !MoonPoolUtility.MapHasMoonPool(map))
            {
                return true; // 无月池的图：原版计数原样执行（近零开销：一次性标志读）
            }
            // 月池格恒不计入真空暴露（密封与屋顶无关——月池在 Roofed 层恒为无顶，
            // 本 Prefix 是它「不流失空气」的唯一语义来源）。
            // 仅含月池的图走本路径；全量计数（气压为低频查询，含月池的图都是小图）。
            int count = 0;
            foreach (IntVec3 cell in __instance.Cells)
            {
                if (!MoonPoolUtility.IsMoonPoolAt(map, cell)
                    && (!map.roofGrid.Roofed(cell) || map.terrainGrid.TerrainAt(cell).exposesToVacuum))
                {
                    count++;
                    if (threshold != int.MaxValue && count >= threshold)
                    {
                        break; // 与原版 StopAt 语义一致（调用方只比较 >= threshold）
                    }
                }
            }
            __result = count;
            return false;
        }
    }
}
