using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 月池（moon pool）工具：特殊基架地形（DeltaV_MoonPool = 原版 Substructure 的
    /// 逐字段镜像变体）——所在格**无顶也不流失空气**（Patch_MoonPool 在真空暴露
    /// 基础判定 District.ExposedVacuumCount 中剔除月池格；**不伪造 Roofed**，屋顶
    /// 恒为真实状态），且**误铺屋顶也不挡 shuttle 起飞**。月池存在性判定收口本类；
    /// 每图首次查询扫描一次，此后随原版地形写入事件维护，无月池的图快速返回。
    /// </summary>
    public static class MoonPoolUtility
    {
        private static TerrainDef moonPoolDef;

        /// <summary>月池地形 def（加载后解析）。</summary>
        public static TerrainDef MoonPoolTerrain =>
            moonPoolDef ?? (moonPoolDef = DefDatabase<TerrainDef>.GetNamed("DeltaV_MoonPool", false));

        public static bool IsMoonPoolAt(Map map, IntVec3 cell)
        {
            return map != null && cell.InBounds(map) && map.terrainGrid.TerrainAt(cell) == MoonPoolTerrain;
        }

        // ---- 每图存在性缓存（密封判定/补丁快速门）----

        private sealed class MapFlag
        {
            public readonly HashSet<IntVec3> cells = new HashSet<IntVec3>();
        }

        private static readonly Dictionary<Map, MapFlag> Flags = new Dictionary<Map, MapFlag>();

        /// <summary>
        /// 图上是否存在月池格（供 Patch_MoonPool 气密快速门使用）。
        /// </summary>
        public static bool MapHasMoonPool(Map map)
        {
            if (map == null || MoonPoolTerrain == null) return false;
            if (!Flags.TryGetValue(map, out MapFlag flag))
            {
                flag = new MapFlag();
                foreach (IntVec3 cell in CellRect.WholeMap(map).Cells)
                    if (IsMoonPoolAt(map, cell)) flag.cells.Add(cell);
                Flags[map] = flag;
            }
            return flag.cells.Count > 0;
        }

        /// <summary>原版地形写入后只更新受影响格；未建缓存的图留待首次查询扫描。</summary>
        public static void NotifyTerrainChanged(Map map, IntVec3 cell)
        {
            if (map == null || !Flags.TryGetValue(map, out MapFlag flag)) return;
            if (IsMoonPoolAt(map, cell)) flag.cells.Add(cell);
            else flag.cells.Remove(cell);
        }

        /// <summary>读档/换档清空（UniverseManager.ResetRuntimeServices）。</summary>
        public static void ClearCache()
        {
            Flags.Clear();
        }

        public static void Forget(Map map)
        {
            if (map != null) Flags.Remove(map);
        }
    }
}
