using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 在已生成的飞船地图上铺设测试船体：逆重基架（Substructure）甲板、外墙、门、内部隔墙、引擎。
    /// 甲板必须用逆重基架：grav 语义里"只有基架上的东西才会被引擎力场抬起"，
    /// 后续质量近似（story：用基架数量近似质量）与结构判定都以它为准。
    /// 阶段1 简化：不盖屋顶、不供电（Orbit biome + MapGeneratorDef 恒温 21°C，
    /// 无顶棚白天光照便于观察），敌我船共用同一铺设逻辑。
    /// </summary>
    public static class ShipStructureStamp
    {
        public static void StampPlayerShip(Map map)
        {
            CellRect hull = CenteredRect(map, 40, 24);
            // 引擎在船艉（西侧）内列，门在船艏（东侧）边墙，中部一道带门隔墙，2 名殖民者。
            Stamp(map, hull, engineCount: 2, doorsOnEast: 2, interiorWall: true, colonists: 2);
        }

        public static void StampEnemyShip(Map map)
        {
            CellRect hull = CenteredRect(map, 24, 14);
            Stamp(map, hull, engineCount: 1, doorsOnEast: 1, interiorWall: false, colonists: 0);
        }

        private static CellRect CenteredRect(Map map, int width, int height)
        {
            return new CellRect(map.Size.x / 2 - width / 2, map.Size.z / 2 - height / 2, width, height);
        }

        private static void Stamp(Map map, CellRect hull, int engineCount, int doorsOnEast, bool interiorWall, int colonists)
        {
            foreach (IntVec3 cell in hull)
            {
                map.terrainGrid.SetTerrain(cell, DeltaVDefOf.Substructure);
            }
            foreach (IntVec3 cell in hull.EdgeCells)
            {
                SpawnWall(map, cell);
            }

            // 船艏门：东边墙中段。
            int midZ = hull.minZ + hull.Height / 2;
            for (int i = 0; i < doorsOnEast; i++)
            {
                int z = midZ + (i % 2 == 0 ? i / 2 : -(i - i / 2)) * 2;
                SpawnDoor(map, new IntVec3(hull.maxX, 0, z));
            }

            // 船艏引擎：西墙内侧一列，避开上下角。
            int sternX = hull.minX + 1;
            for (int i = 0; i < engineCount; i++)
            {
                int z = hull.minZ + 2 + (hull.Height - 4) * (engineCount == 1 ? 1 : i) / (engineCount == 1 ? 2 : engineCount - 1);
                Spawn(map, ThingMaker.MakeThing(DeltaVDefOf.Ship_Engine), new IntVec3(sternX, 0, z));
            }

            if (interiorWall)
            {
                int wallX = hull.minX + hull.Width / 2;
                for (int z = hull.minZ + 1; z <= hull.maxZ - 1; z++)
                {
                    if (z == midZ)
                    {
                        SpawnDoor(map, new IntVec3(wallX, 0, z));
                    }
                    else
                    {
                        SpawnWall(map, new IntVec3(wallX, 0, z));
                    }
                }
            }

            if (colonists > 0)
            {
                SpawnColonists(map, hull, colonists);
            }
        }

        private static void SpawnWall(Map map, IntVec3 cell)
        {
            Spawn(map, ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel), cell);
        }

        private static void SpawnDoor(Map map, IntVec3 cell)
        {
            // WipeMode.Vanish 顺带清掉同格已铺的墙。
            Spawn(map, ThingMaker.MakeThing(ThingDefOf.Door, ThingDefOf.Steel), cell);
        }

        private static void Spawn(Map map, Thing thing, IntVec3 cell)
        {
            GenSpawn.Spawn(thing, cell, map, WipeMode.Vanish);
        }

        private static void SpawnColonists(Map map, CellRect hull, int count)
        {
            var candidates = new List<IntVec3>();
            foreach (IntVec3 cell in hull.ContractedBy(2))
            {
                if (cell.Standable(map))
                {
                    candidates.Add(cell);
                }
            }
            for (int i = 0; i < count && candidates.Count > 0; i++)
            {
                // 从候选中取靠中间的格子，避免刷在墙角。
                IntVec3 cell = candidates[Mathf.Clamp(candidates.Count / 2 + i, 0, candidates.Count - 1)];
                candidates.Remove(cell);
                Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer));
                GenSpawn.Spawn(pawn, cell, map, WipeMode.Vanish);
            }
        }
    }
}
