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
    /// 恒为真实状态），且**误铺屋顶也不挡 shuttle 起飞**（CanLaunch Postfix 豁免
    /// 屋顶项；降落管线不触碰屋顶）。月池存在性判定收口本类（单实现）；性能护栏 =
    /// 每图「是否存在月池格」标志（周期重扫 + 结构版本失效），无月池的图近零开销。
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
            public bool hasMoonPool;
            public readonly List<IntVec3> cells = new List<IntVec3>();
            public int scannedAtTick = -1;
            public int structureVersion = -1;
        }

        private static readonly Dictionary<Map, MapFlag> Flags = new Dictionary<Map, MapFlag>();

        /// <summary>
        /// 图上是否存在月池格（供 Patch_MoonPool 快速门与聚焦绘制使用）；
        /// 重扫时顺带缓存格列表（<see cref="MoonPoolCells"/>）。
        /// </summary>
        public static bool MapHasMoonPool(Map map)
        {
            return map != null && MoonPoolTerrain != null && RescanIfNeeded(map).hasMoonPool;
        }

        /// <summary>图上月池格列表（只读；与存在性标志同一缓存周期刷新）。</summary>
        public static List<IntVec3> MoonPoolCells(Map map)
        {
            return map != null && MoonPoolTerrain != null ? RescanIfNeeded(map).cells : EmptyCells;
        }

        private static readonly List<IntVec3> EmptyCells = new List<IntVec3>();

        private static MapFlag RescanIfNeeded(Map map)
        {
            if (!Flags.TryGetValue(map, out MapFlag flag))
            {
                flag = new MapFlag();
                Flags[map] = flag;
            }
            int now = Find.TickManager.TicksGame;
            int version = SubstructureOwnership.GlobalStructureVersion;
            if (flag.scannedAtTick < 0 || now - flag.scannedAtTick >= 120 || flag.structureVersion != version)
            {
                flag.hasMoonPool = false;
                flag.cells.Clear();
                foreach (IntVec3 c in CellRect.WholeMap(map).Cells)
                {
                    if (map.terrainGrid.TerrainAt(c) == MoonPoolTerrain)
                    {
                        flag.hasMoonPool = true;
                        flag.cells.Add(c);
                    }
                }
                flag.scannedAtTick = now;
                flag.structureVersion = version;
            }
            return flag;
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
