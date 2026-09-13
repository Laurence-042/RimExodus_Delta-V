using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 在已生成的飞船地图上铺设测试船体：逆重基架（Substructure）甲板、外墙、门、内部隔墙、
    /// 全向推进器组。甲板必须用逆重基架：grav 语义里"只有基架上的东西才能被引擎力场抬起"，
    /// 后续质量近似（story：用基架数量近似质量）与结构判定都以它为准。
    ///
    /// 阶段2：推进器 = LargeThruster（Odyssey 逆重飞船大型推进器，2×2 方形脚印不随旋转变化，
    /// rotatable、terrainAffordanceNeeded=Substructure，正好装在基架甲板上）；
    /// 推力方向约定见 ShipPhysics.ThrustDir（推力沿 Rot4 朝向，与 def 的 exhaust 喷口偏移一致）。
    /// 布局对称于船体中心：纯线性指令不产生净力矩、转向力偶不产生净力——
    /// 非对称扩建后 ShipPhysics 会如实保留推力诱导力矩（质心物理）。
    /// 阶段1 简化沿用：不盖屋顶、不供电（Orbit biome + MapGeneratorDef 恒温 21°C）。
    /// </summary>
    public static class ShipStructureStamp
    {
        public static void StampPlayerShip(Map map)
        {
            CellRect hull = CenteredRect(map, 40, 24);
            StampHull(map, hull, doorsOnEast: 2, interiorWall: true, colonists: 2);
            // 玩家船 40×24：推进器 2×2，z 带 3-4 / 9-10 / 13-14 / 19-20 互不重叠；
            // x 1-2（艉）/7-8/31-32/37-38（艏），对称于船体中心。
            SpawnThruster(map, hull, 1, 3, Rot4.East);            // 前进 ×2（艉、朝东）
            SpawnThruster(map, hull, 1, 19, Rot4.East);
            SpawnThruster(map, hull, 37, 3, Rot4.West);           // 减速 ×2（艏、朝西）
            SpawnThruster(map, hull, 37, 19, Rot4.West);
            SpawnThruster(map, hull, 7, 1, Rot4.North);           // 左平移 ×2（南缘、朝北）
            SpawnThruster(map, hull, 31, 1, Rot4.North);
            SpawnThruster(map, hull, 7, 21, Rot4.South);          // 右平移 ×2（北缘、朝南）
            SpawnThruster(map, hull, 31, 21, Rot4.South);
            SpawnThruster(map, hull, 1, 9, Rot4.South);           // 逆时针力偶（艉朝南 + 艏朝北）
            SpawnThruster(map, hull, 37, 9, Rot4.North);
            SpawnThruster(map, hull, 1, 13, Rot4.North);          // 顺时针力偶（艉朝北 + 艏朝南）
            SpawnThruster(map, hull, 37, 13, Rot4.South);
        }

        public static void StampEnemyShip(Map map)
        {
            CellRect hull = CenteredRect(map, 24, 14);
            StampHull(map, hull, doorsOnEast: 1, interiorWall: false, colonists: 0);
            // 敌舰 24×14：单台线性推进器 + 双向力偶，对称于船体中心；z 带 1-2 / 3-4 / 6-7 / 9-10 / 11-12 互不重叠。
            SpawnThruster(map, hull, 1, 6, Rot4.East);            // 前进（艉、朝东）
            SpawnThruster(map, hull, 21, 6, Rot4.West);           // 减速（艏、朝西）
            SpawnThruster(map, hull, 11, 1, Rot4.North);          // 左平移（南缘、朝北）
            SpawnThruster(map, hull, 11, 11, Rot4.South);         // 右平移（北缘、朝南）
            SpawnThruster(map, hull, 1, 9, Rot4.South);           // 逆时针力偶
            SpawnThruster(map, hull, 21, 9, Rot4.North);
            SpawnThruster(map, hull, 1, 3, Rot4.North);           // 顺时针力偶
            SpawnThruster(map, hull, 21, 3, Rot4.South);
        }

        private static CellRect CenteredRect(Map map, int width, int height)
        {
            return new CellRect(map.Size.x / 2 - width / 2, map.Size.z / 2 - height / 2, width, height);
        }

        private static void StampHull(Map map, CellRect hull, int doorsOnEast, bool interiorWall, int colonists)
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

        /// <summary>按朝向生成推进器（LargeThruster 2×2 方形脚印；origin 为脚印最小角）。</summary>
        private static void SpawnThruster(Map map, CellRect hull, int dx, int dz, Rot4 rotation)
        {
            GenSpawn.Spawn(DeltaVDefOf.LargeThruster, new IntVec3(hull.minX + dx, 0, hull.minZ + dz), map, rotation,
                WipeMode.Vanish);
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
