using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 在已生成的飞船地图上铺设测试船体：逆重基架（Substructure）甲板、外墙、门、内部隔墙、
    /// 全向推进器组。玩家船布局复刻自 doc/tmp/新移民114.rws 的 map2：40×24 的“日”字船体
    /// 与其中设备的 Thing.Position 锚点。甲板必须用逆重基架：grav 语义里
    /// "只有基架上的东西才能被引擎力场抬起"，
    /// 后续质量近似（story：用基架数量近似质量）与结构判定都以它为准。
    ///
    /// 阶段2：推进器 = LargeThruster（Odyssey 逆重飞船大型推进器，2×2 方形脚印不随旋转变化，
    /// rotatable、terrainAffordanceNeeded=Substructure，正好装在基架甲板上）；
    /// 推力方向约定见 ShipPhysics.ThrustDir（推力沿 Rot4 朝向，与 def 的 exhaust 喷口偏移一致）。
    /// ShipPhysics 始终按实际占用矩形中心计算作用点；map2 的非对称布局与后续玩家扩建
    /// 都由联合分配器按真实几何差动油门，尽量抵消平移诱导力矩。
    /// 阶段1 简化沿用：不盖屋顶、不供电（Orbit biome + MapGeneratorDef 恒温 21°C）。
    /// </summary>
    public static class ShipStructureStamp
    {
        /// <summary>ownerId = 结构归属 ID（与容器对象 ID 解耦）：甲板基架从生成起归属该结构。
        /// prefabLayout = 居住区预制驱动（HomePrefab）：跳过标准内隔墙与写死设备表，
        /// 推进器照旧（物理表归 XML），返回 hull 矩形供预制居中回放。</summary>
        public static CellRect StampPlayerShip(Map map, int ownerId, bool prefabLayout = false)
        {
            CellRect hull = CenteredRect(map, 40, 24);
            StampHull(map, hull, doorsOnEast: 2, interiorWall: !prefabLayout, colonists: 0, Faction.OfPlayer, ownerId);
            if (prefabLayout)
            {
                StampPlayerThrusters(map, hull);
            }
            else
            {
                StampPlayerEquipment(map, hull);
            }
            // 跳帮 shuttle（阶段5 测试，§7：原版 PassengerShuttle 的装甲变体，3×5）：船艉内侧首个可站立脚印位。
            for (int z = hull.minZ + 1; z <= hull.maxZ - 5; z++)
            {
                IntVec3 cell = new IntVec3(hull.minX + 2, 0, z);
                if (cell.Standable(map) && GenAdj.OccupiedRect(cell, Rot4.North, new IntVec2(3, 5)).Cells.All(c => c.InBounds(map) && c.Standable(map)))
                {
                    MapContentSpawner.SpawnRecord(map,
                        DefDatabase<ThingDef>.GetNamed("DeltaV_ShuttleBoarding"), null, cell,
                        Rot4.North, 0, 0, Faction.OfPlayer);
                    break;
                }
            }
            // 殖民者最后落位：设备已占的格被 Standable 过滤，也不会被后续 Vanish 波及。
            SpawnColonists(map, hull, 2);
            return hull;
        }

        /// <summary>敌舰（可驾驶对等船）：需要驾驶台使其成为有效 NPC 船（有主台的归属）。
        /// prefabLayout 语义同玩家船（推进器照旧、驾驶台交给预制）。</summary>
        public static CellRect StampEnemyShip(Map map, int ownerId, Faction faction, bool prefabLayout = false)
        {
            CellRect hull = CenteredRect(map, 24, 14);
            StampHull(map, hull, doorsOnEast: 1, interiorWall: false, colonists: 0, faction, ownerId);
            // 敌舰 24×14：单台线性推进器 + 双向力偶，对称于船体中心；z 带 1-2 / 3-4 / 6-7 / 9-10 / 11-12 互不重叠。
            SpawnThruster(map, hull, 1, 7, Rot4.East, faction);            // 前进（艉、朝东）
            SpawnThruster(map, hull, 22, 6, Rot4.West, faction);           // 减速（艏、朝西）
            SpawnThruster(map, hull, 11, 1, Rot4.North, faction);          // 左平移（南缘、朝北）
            SpawnThruster(map, hull, 12, 12, Rot4.South, faction);         // 右平移（北缘、朝南）
            SpawnThruster(map, hull, 2, 10, Rot4.South, faction);          // 逆时针力偶
            SpawnThruster(map, hull, 21, 9, Rot4.North, faction);
            SpawnThruster(map, hull, 1, 3, Rot4.North, faction);           // 顺时针力偶
            SpawnThruster(map, hull, 22, 4, Rot4.South, faction);
            // 驾驶台（主台）：使该归属成为有效 NPC 船；朝向 East（船帧对齐图帧）。
            if (!prefabLayout)
            {
                SpawnAnchored(map, ThingDefOf.PilotConsole, hull, 11, 6, Rot4.East, faction);
            }
            return hull;
        }

        private static CellRect CenteredRect(Map map, int width, int height)
        {
            return new CellRect(map.Size.x / 2 - width / 2, map.Size.z / 2 - height / 2, width, height);
        }

        private static void StampHull(Map map, CellRect hull, int doorsOnEast, bool interiorWall,
            int colonists, Faction faction, int ownerId)
        {
            MapContentSpawner.WithOwnershipStamp(map, hull.Cells, ownerId, delegate
            {
                foreach (IntVec3 cell in hull)
                {
                    map.terrainGrid.SetTerrain(cell, DeltaVDefOf.Substructure);
                }
            });
            foreach (IntVec3 cell in hull.EdgeCells)
            {
                SpawnWall(map, cell, faction);
            }

            // 舱顶（2026-09-26 补）：测试船此前不盖屋顶，增压舱内装载/月池流程无法
            // 验证。整块 hull 盖建造顶（含门格——气闸语义），舱外照旧无顶。
            foreach (IntVec3 cell in hull)
            {
                map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
            }

            // 船艏门：东边墙中段。
            int midZ = hull.minZ + hull.Height / 2;
            for (int i = 0; i < doorsOnEast; i++)
            {
                int z = midZ + (i % 2 == 0 ? i / 2 : -(i - i / 2)) * 2;
                SpawnDoor(map, new IntVec3(hull.maxX, 0, z), faction);
            }

            if (interiorWall)
            {
                int wallX = hull.minX + hull.Width / 2;
                for (int z = hull.minZ + 1; z <= hull.maxZ - 1; z++)
                {
                    if (z == midZ)
                    {
                        SpawnDoor(map, new IntVec3(wallX, 0, z), faction);
                    }
                    else
                    {
                        SpawnWall(map, new IntVec3(wallX, 0, z), faction);
                    }
                }
            }

            if (colonists > 0)
            {
                SpawnColonists(map, hull, colonists);
            }
        }

        /// <summary>
        /// 复刻存档 map2 的设备布局。偏移量全部是 Thing.Position 锚点相对于 hull.minX/minZ
        /// 的偏移，不是贴图中心，也不是 OccupiedRect 的最小角。尤其 2×2 建筑的锚点会随
        /// Rot4 改变占地偏置；3×3 GravEngine 的存档锚点则是 (17,12)。
        /// </summary>
        private static void StampPlayerEquipment(Map map, CellRect hull)
        {
            // 引擎必须先作为玩家建筑生成；后生成的设施会在 CompFacility.PostSpawnSetup
            // 立刻找到这个殖民者引擎并建立双向 link，CanBeActive 才会成立。
            SpawnAnchored(map, ThingDefOf.GravEngine, hull, 17, 12, Rot4.North, Faction.OfPlayer);
            SpawnAnchored(map, ThingDefOf.GravFieldExtender, hull, 1, 3, Rot4.North, Faction.OfPlayer);
            SpawnAnchored(map, ThingDefOf.GravFieldExtender, hull, 1, 20, Rot4.North, Faction.OfPlayer);
            SpawnAnchored(map, ThingDefOf.GravFieldExtender, hull, 33, 3, Rot4.North, Faction.OfPlayer);
            SpawnAnchored(map, ThingDefOf.GravFieldExtender, hull, 33, 21, Rot4.North, Faction.OfPlayer);

            // 直接使用存档中的原版 PilotConsole；XML patch 为它追加 Delta-V gizmo，
            // 同时保留原版 Gravship 对 ThingDefOf.PilotConsole 的身份识别。
            SpawnAnchored(map, ThingDefOf.PilotConsole, hull, 14, 12, Rot4.East, Faction.OfPlayer);

            StampPlayerThrusters(map, hull);
            RelinkGravshipComponents(map);
        }

        /// <summary>推进器组（prefab 布局也照旧 stamp：推进器物理表归 XML，不进预制）。</summary>
        private static void StampPlayerThrusters(Map map, CellRect hull)
        {
            SpawnThruster(map, hull, 0, 23, Rot4.East, Faction.OfPlayer);
            SpawnThruster(map, hull, 3, 23, Rot4.South, Faction.OfPlayer);
            SpawnThruster(map, hull, 0, 1, Rot4.East, Faction.OfPlayer);
            SpawnThruster(map, hull, 2, 0, Rot4.North, Faction.OfPlayer);
            SpawnThruster(map, hull, 39, 13, Rot4.West, Faction.OfPlayer);
            SpawnThruster(map, hull, 39, 8, Rot4.West, Faction.OfPlayer);
        }

        /// <summary>
        /// 批量盖章结束后的 relink/dirty 收尾：避免生成顺序或同 tick facility 缓存让
        /// 延展器停留在未连接状态。设备来源无关（写死表或居住区预制），回放后统一执行。
        /// </summary>
        internal static void RelinkGravshipComponents(Map map)
        {
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.GravEngine))
            {
                if (thing is Building_GravEngine engine)
                {
                    engine.AffectedByFacilities.Notify_ThingChanged();
                    engine.ForceSubstructureDirty();
                    _ = engine.GravshipComponents;
                }
            }
        }

        /// <summary>按 Thing.Position 锚点生成 2×2 LargeThruster。</summary>
        private static void SpawnThruster(Map map, CellRect hull, int anchorDx, int anchorDz,
            Rot4 rotation, Faction faction = null)
        {
            SpawnAnchored(map, DeltaVDefOf.LargeThruster, hull, anchorDx, anchorDz, rotation, faction);
        }

        private static Thing SpawnAnchored(Map map, ThingDef def, CellRect hull, int anchorDx, int anchorDz,
            Rot4 rotation, Faction faction = null)
        {
            return MapContentSpawner.SpawnRecord(map, def, null,
                new IntVec3(hull.minX + anchorDx, 0, hull.minZ + anchorDz), rotation, 0, 0, faction);
        }

        private static void SpawnWall(Map map, IntVec3 cell, Faction faction)
        {
            MapContentSpawner.SpawnRecord(map, ThingDefOf.Wall, ThingDefOf.Steel, cell, Rot4.North, 0, 0, faction);
        }

        private static void SpawnDoor(Map map, IntVec3 cell, Faction faction)
        {
            // WipeMode.Vanish 顺带清掉同格已铺的墙。
            MapContentSpawner.SpawnRecord(map, ThingDefOf.Door, ThingDefOf.Steel, cell, Rot4.North, 0, 0, faction);
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
                // Orbit 环境的 EVA 常备：真空服 + 头盔（Odyssey Apparel_Vacsuit*）。
                Wear(pawn, "Apparel_Vacsuit");
                Wear(pawn, "Apparel_VacsuitHelmet");
            }
        }

        private static void Wear(Pawn pawn, string defName)
        {
            ThingDef def = ThingDef.Named(defName);
            if (def == null)
            {
                return;
            }
            var apparel = (Apparel)ThingMaker.MakeThing(def);
            if (ApparelUtility.HasPartsToWear(pawn, def))
            {
                pawn.apparel.Wear(apparel);
            }
        }
    }
}
