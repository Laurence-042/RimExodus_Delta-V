using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 阶段3 最简太空 POI 模板：残骸飞船——逆重基架甲板 + 破损边缘墙 + 散落物资，
    /// 全部由 seed 决定（同 seed 重生成结果一致；玩家改动则由 PoiSnapshot 恢复，
    /// 优先级高于模板）。阶段3 的 POI 不放 pawn（快照不保 pawn 的前提）。
    /// </summary>
    public static class PoiTemplate
    {
        private const int Width = 22;
        private const int Height = 13;

        public static void Stamp(Map map, int seed)
        {
            Rand.PushState(seed);
            try
            {
                CellRect hull = new CellRect(map.Size.x / 2 - Width / 2, map.Size.z / 2 - Height / 2, Width, Height);
                foreach (IntVec3 cell in hull)
                {
                    map.terrainGrid.SetTerrain(cell, DeltaVDefOf.Substructure);
                }
                // 破损边缘墙：约四成缺口，其余钢墙（部分残血，表现为可拆的残骸结构）。
                foreach (IntVec3 cell in hull.EdgeCells)
                {
                    if (Rand.Value < 0.4f)
                    {
                        continue;
                    }
                    Building wall = (Building)ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel);
                    GenSpawn.Spawn(wall, cell, map, WipeMode.Vanish);
                    if (Rand.Value < 0.5f)
                    {
                        wall.HitPoints = Mathf.CeilToInt(wall.MaxHitPoints * Rand.Range(0.2f, 0.6f));
                    }
                }
                // 散落物资与一台残破推进器（外壳代理的视觉锚点）。
                SpawnItemStack(map, hull, ThingDefOf.Steel, Rand.RangeInclusive(40, 90), Rand.RangeInclusive(2, 4));
                SpawnItemStack(map, hull, ThingDefOf.ComponentIndustrial, Rand.RangeInclusive(5, 15), Rand.RangeInclusive(1, 2));
                IntVec3 thrusterCell = RandomStandableCell(hull, map);
                if (thrusterCell.IsValid)
                {
                    GenSpawn.Spawn(DeltaVDefOf.LargeThruster, thrusterCell, map, Rot4.Invalid, WipeMode.Vanish);
                }
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static void SpawnItemStack(Map map, CellRect hull, ThingDef def, int totalCount, int stacks)
        {
            for (int i = 0; i < stacks; i++)
            {
                int count = Mathf.Max(1, totalCount / stacks);
                IntVec3 cell = RandomStandableCell(hull, map);
                if (!cell.IsValid)
                {
                    return;
                }
                Thing item = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
                item.stackCount = Mathf.Min(count, def.stackLimit);
                GenSpawn.Spawn(item, cell, map, WipeMode.Vanish);
            }
        }

        private static readonly List<IntVec3> CellBuffer = new List<IntVec3>(64);

        private static IntVec3 RandomStandableCell(CellRect hull, Map map)
        {
            CellBuffer.Clear();
            foreach (IntVec3 cell in hull.ContractedBy(1))
            {
                if (cell.Standable(map))
                {
                    CellBuffer.Add(cell);
                }
            }
            return CellBuffer.Count == 0 ? IntVec3.Invalid : CellBuffer.RandomElement();
        }
    }
}
