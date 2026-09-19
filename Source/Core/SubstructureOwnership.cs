using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 基架归属网格（2026-09-18 设计）：每个基架格记录归属 SpaceObject ID。飞船引擎的
    /// 范围/承载、逆重发射与移动模式捕获只按「自己的 ID」过滤；星球仪剪影按 ID 过滤；
    /// 停靠在 wreck 上的飞船与 wreck 各自保有独立身份。归属是存档事实（无法从地形推导）。
    ///
    /// ID 语义：与 SpaceObject.objectId 同一 ID 空间（UniverseManager 统一分配）。裸 ID
    /// （没有对应 SpaceObject 的归属域，如独立新建的平台）合法存在，经「修改基架归属」
    /// 命令引用；在其上建引擎 + 驾驶台 bootstrap 成船时由 MobileShipBootstrap 继承为船 ID。
    ///
    /// 写入口唯一收口在 Harmony patch（Patch_SubstructureOwnership 的 TerrainGrid.
    /// SetFoundation postfix）：显式盖章（生成/降落/快照恢复管线）优先，其余按邻接规则
    /// （四邻同为基架且归属一致 → 继承；无邻接基架 → 新 ID；邻接多个不同归属 → 拒建由
    /// PlaceWorker_DeltaVSubstructureOwnership 把关，防御性落到 Unassigned）。
    /// 仅 Delta-V 参考系图参与（RimExodus 地表零影响）。
    ///
    /// owner 值只在「当前有基架的格」上有意义：拆除基架不清写 owner（消费者一律先判
    /// 地形再读归属），SetFoundation 恒重写，因此残留值不可能被读到。
    /// </summary>
    public class SubstructureOwnership : MapComponent
    {
        public const int Unassigned = 0;

        private int[] ownerGrid;

        public SubstructureOwnership(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            // 稀疏序列化（cellIndex → owner）：基架格只占全图一小部分，整网格序列化浪费。
            Dictionary<int, int> sparse = null;
            if (Scribe.mode == LoadSaveMode.Saving && ownerGrid != null)
            {
                sparse = new Dictionary<int, int>();
                for (int i = 0; i < ownerGrid.Length; i++)
                {
                    if (ownerGrid[i] != Unassigned)
                    {
                        sparse[i] = ownerGrid[i];
                    }
                }
            }
            Scribe_Collections.Look(ref sparse, "owners", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                ownerGrid = new int[map.cellIndices.NumGridCells];
                if (sparse != null)
                {
                    foreach (KeyValuePair<int, int> pair in sparse)
                    {
                        if (pair.Key >= 0 && pair.Key < ownerGrid.Length)
                        {
                            ownerGrid[pair.Key] = pair.Value;
                        }
                    }
                }
            }
        }

        // ---- 查询 ----

        /// <summary>格子的基架归属；无组件/未指派返回 Unassigned。调用方须自行保证格上有基架。</summary>
        public int OwnerAt(IntVec3 cell)
        {
            if (ownerGrid == null)
            {
                return Unassigned;
            }
            int index = map.cellIndices.CellToIndex(cell);
            return index >= 0 && index < ownerGrid.Length ? ownerGrid[index] : Unassigned;
        }

        /// <summary>图中现存基架格的归属 ID 并集（升序；「修改基架归属」命令的候选列表）。</summary>
        public List<int> OwnersOnMap()
        {
            var owners = new HashSet<int>();
            if (ownerGrid != null)
            {
                foreach (IntVec3 cell in CellRect.WholeMap(map))
                {
                    if (map.terrainGrid.TerrainAt(cell) == DeltaVDefOf.Substructure
                        && ownerGrid[map.cellIndices.CellToIndex(cell)] != Unassigned)
                    {
                        owners.Add(ownerGrid[map.cellIndices.CellToIndex(cell)]);
                    }
                }
            }
            var sorted = new List<int>(owners);
            sorted.Sort();
            return sorted;
        }

        /// <summary>归属 ownerId 的基架格（剪影扫描口径；地形与归属双重过滤）。</summary>
        public List<IntVec3> CellsOwnedBy(int ownerId)
        {
            var cells = new List<IntVec3>();
            if (ownerId == Unassigned || ownerGrid == null)
            {
                return cells;
            }
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                if (map.terrainGrid.TerrainAt(cell) == DeltaVDefOf.Substructure
                    && ownerGrid[map.cellIndices.CellToIndex(cell)] == ownerId)
                {
                    cells.Add(cell);
                }
            }
            return cells;
        }

        /// <summary>是否存在归属已指派的基架格（迁移前的快速探测）。</summary>
        public bool HasAssignedSubstructure()
        {
            if (ownerGrid == null)
            {
                return false;
            }
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                if (map.terrainGrid.TerrainAt(cell) == DeltaVDefOf.Substructure
                    && ownerGrid[map.cellIndices.CellToIndex(cell)] != Unassigned)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>图上是否存在任何基架格。</summary>
        public bool HasAnySubstructure()
        {
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                if (map.terrainGrid.TerrainAt(cell) == DeltaVDefOf.Substructure)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>归属 ownerId 的基架格数。</summary>
        public int CountCellsOwnedBy(int ownerId)
        {
            if (ownerGrid == null)
            {
                return 0;
            }
            int count = 0;
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                if (map.terrainGrid.TerrainAt(cell) == DeltaVDefOf.Substructure
                    && ownerGrid[map.cellIndices.CellToIndex(cell)] == ownerId)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// 整图基架改归 ownerId，返回格数。读档自愈与 dev 修复动作用：不做引擎/剪影
        /// 通知（调用点在加载收尾，引擎缓存本就待重建）。
        /// </summary>
        public int ClaimAllSubstructure(int ownerId)
        {
            int count = 0;
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                if (map.terrainGrid.TerrainAt(cell) == DeltaVDefOf.Substructure)
                {
                    Assign(cell, ownerId);
                    count++;
                }
            }
            return count;
        }

        /// <summary>图上现存基架的归属域清单（显示用：按 owner 分组的格子）。</summary>
        public List<KeyValuePair<int, List<IntVec3>>> OwnerDomains()
        {
            var domains = new Dictionary<int, List<IntVec3>>();
            if (ownerGrid != null)
            {
                foreach (IntVec3 cell in CellRect.WholeMap(map))
                {
                    if (map.terrainGrid.TerrainAt(cell) == DeltaVDefOf.Substructure)
                    {
                        int owner = ownerGrid[map.cellIndices.CellToIndex(cell)];
                        if (!domains.TryGetValue(owner, out List<IntVec3> cells))
                        {
                            cells = new List<IntVec3>();
                            domains[owner] = cells;
                        }
                        cells.Add(cell);
                    }
                }
            }
            return domains.ToList();
        }

        /// <summary>归属的显示色（HSV 黄金角散列；未占有 = 灰）。显示层统一取色入口。</summary>
        public static Color OwnerColor(int ownerId)
        {
            return ownerId == Unassigned
                ? new Color(0.55f, 0.55f, 0.55f, 0.55f)
                : Color.HSVToRGB(Mathf.Repeat(ownerId * 0.618f, 1f), 0.75f, 1f);
        }

        /// <summary>
        /// 建造规则探测：四邻基架的不同非零归属数。0 = 孤立建造（分配新 ID）；
        /// 1 = 继承唯一归属（singleOwner）；≥2 = 违规（PlaceWorker 拒绝）。
        /// </summary>
        public int DistinctNeighborOwners(IntVec3 cell, out int singleOwner)
        {
            int first = Unassigned;
            int count = 0;
            foreach (IntVec3 neighbor in GenAdj.CardinalDirectionsAround)
            {
                IntVec3 c = cell + neighbor;
                if (!c.InBounds(map) || map.terrainGrid.TerrainAt(c) != DeltaVDefOf.Substructure)
                {
                    continue;
                }
                int owner = OwnerAt(c);
                if (owner == Unassigned)
                {
                    continue; // 未指派（迁移前防御态）不构成归属约束
                }
                if (count == 0)
                {
                    first = owner;
                    count = 1;
                }
                else if (owner != first)
                {
                    count = 2;
                    break;
                }
            }
            singleOwner = first;
            return count;
        }

        // ---- 写入（本组件是基架归属的唯一权威类；外部写路径只走 SetPendingStamp /
        // SetOwner / ResolveBuilt / ClaimAllSubstructure / MigrateUnassigned 这些语义
        // 入口，逐格裸写 Assign 收窄为组件内部使用，owner ID 一律经
        // UniverseManager.AllocateOwnerId 单一计数器分配——2026-09-20 收拢） ----

        /// <summary>写单格归属（不通知；管线批写时逐格调用，由调用方收尾通知）。组件内部使用。</summary>
        internal void Assign(IntVec3 cell, int ownerId)
        {
            if (ownerGrid == null)
            {
                ownerGrid = new int[map.cellIndices.NumGridCells];
            }
            ownerGrid[map.cellIndices.CellToIndex(cell)] = ownerId;
        }

        /// <summary>图中已盖出的最大归属 ID（读档 ID 下限对齐用；无数据返回 0）。</summary>
        public int MaxAssignedOwner()
        {
            int max = 0;
            if (ownerGrid == null)
            {
                return max;
            }
            foreach (int owner in ownerGrid)
            {
                if (owner > max)
                {
                    max = owner;
                }
            }
            return max;
        }

        /// <summary>
        /// 玩家命令级批写（「修改基架归属」）：通知图上全部引擎重算连通域、失效星球仪
        /// 剪影。SubstructureGrid.MarkDirty 只扫殖民者引擎，wreck 引擎也要失效，这里显式遍历。
        /// </summary>
        public void SetOwner(IEnumerable<IntVec3> cells, int ownerId)
        {
            if (NotifyIfChanged(cells, ownerId))
            {
                NotifyOwnershipChanged();
            }
        }

        /// <summary>批写并返回是否有实际变化（不通知；建造钩子收尾统一通知）。</summary>
        private bool NotifyIfChanged(IEnumerable<IntVec3> cells, int ownerId)
        {
            bool changed = false;
            foreach (IntVec3 cell in cells)
            {
                if (!cell.InBounds(map) || map.terrainGrid.TerrainAt(cell) != DeltaVDefOf.Substructure)
                {
                    continue;
                }
                if (OwnerAt(cell) == ownerId)
                {
                    continue;
                }
                Assign(cell, ownerId);
                changed = true;
            }
            return changed;
        }

        /// <summary>归属变化的通知收尾：全部引擎 dirty（含非玩家引擎）+ 星球仪剪影失效。</summary>
        private void NotifyOwnershipChanged()
        {
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.GravEngine))
            {
                if (thing is Building_GravEngine engine)
                {
                    engine.ForceSubstructureDirty();
                }
            }
            PlanetGlobeRenderer.InvalidateSilhouette(map);
        }

        /// <summary>
        /// 建造完成时的归属落位（2026-09-18 用户决策的「未占有」语义）：新基架继承
        /// 唯一有效邻接归属，并把与之相连的**整片未占有（Unassigned）基架区域**一并
        /// 同步为该归属（多行建造时无论施工顺序如何，最终整片与船同归属）。区域若
        /// 同时邻接其他有效归属（争议桥），只写新格本身、区域保持未占有，交由
        /// 「修改基架归属」命令人工裁决。孤立建造（无有效邻接）不分配 ID：保持未
        /// 占有，等待后续相连吸收或经驾驶台 bootstrap 认领成船。
        /// </summary>
        public void ResolveBuilt(IntVec3 cell)
        {
            int count = DistinctNeighborOwners(cell, out int single);
            if (count == 1)
            {
                var cells = new List<IntVec3> { cell };
                cells.AddRange(CollectUnownedRegion(cell, single));
                if (NotifyIfChanged(cells, single))
                {
                    NotifyOwnershipChanged();
                }
            }
            // count == 0：孤立建造 → 保持未占有（Unassigned 为默认值，无需写入）。
            // count >= 2：PlaceWorker 已在放置门禁拒绝；脚本/破坏性路径漏到这里时同样
            // 保持未占有（引擎连通域按 0 过滤，不会越界并入任何现有域）。
        }

        /// <summary>
        /// 收集与 cell 四连通、且区域不邻接其他有效归属的未占有基架区域（争议区域
        /// 返回空集）。
        /// </summary>
        private List<IntVec3> CollectUnownedRegion(IntVec3 cell, int owner)
        {
            var region = new List<IntVec3>();
            var queue = new Queue<IntVec3>();
            var seen = new HashSet<IntVec3>();
            foreach (IntVec3 delta in GenAdj.CardinalDirectionsAround)
            {
                IntVec3 neighbor = cell + delta;
                if (neighbor.InBounds(map)
                    && map.terrainGrid.TerrainAt(neighbor) == DeltaVDefOf.Substructure
                    && OwnerAt(neighbor) == Unassigned
                    && seen.Add(neighbor))
                {
                    queue.Enqueue(neighbor);
                }
            }
            while (queue.Count > 0)
            {
                IntVec3 current = queue.Dequeue();
                region.Add(current);
                foreach (IntVec3 delta in GenAdj.CardinalDirectionsAround)
                {
                    IntVec3 neighbor = current + delta;
                    if (neighbor.InBounds(map)
                        && map.terrainGrid.TerrainAt(neighbor) == DeltaVDefOf.Substructure
                        && OwnerAt(neighbor) == Unassigned
                        && seen.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }
            // 争议检查：区域内任一格邻接其他有效归属 → 整片保持未占有。
            foreach (IntVec3 regionCell in region)
            {
                foreach (IntVec3 delta in GenAdj.CardinalDirectionsAround)
                {
                    IntVec3 neighbor = regionCell + delta;
                    if (neighbor.InBounds(map)
                        && map.terrainGrid.TerrainAt(neighbor) == DeltaVDefOf.Substructure)
                    {
                        int neighborOwner = OwnerAt(neighbor);
                        if (neighborOwner != Unassigned && neighborOwner != owner)
                        {
                            return new List<IntVec3>();
                        }
                    }
                }
            }
            return region;
        }

        /// <summary>
        /// 旧档自愈（AGENTS §15）：未指派基架格 → 地图默认归属（宿主 SpaceObject 的 ID；
        /// 无宿主对象时分配新 ID）。返回迁移格数。
        /// </summary>
        public int MigrateUnassigned(int defaultOwner)
        {
            if (ownerGrid != null && !HasUnassignedSubstructure())
            {
                return 0;
            }
            int migrated = 0;
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                if (map.terrainGrid.TerrainAt(cell) == DeltaVDefOf.Substructure
                    && OwnerAt(cell) == Unassigned)
                {
                    Assign(cell, defaultOwner);
                    migrated++;
                }
            }
            return migrated;
        }

        /// <summary>是否存在未指派归属的基架格（读档迁移的快速探测）。</summary>
        public bool HasUnassignedSubstructure()
        {
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                if (map.terrainGrid.TerrainAt(cell) == DeltaVDefOf.Substructure
                    && OwnerAt(cell) == Unassigned)
                {
                    return true;
                }
            }
            return false;
        }

        // ---- 组件获取 ----

        /// <summary>获取或创建归属组件（写路径用）。</summary>
        public static SubstructureOwnership For(Map map)
        {
            return map.GetComponent<SubstructureOwnership>();
        }

        /// <summary>只读探测（不创建；引擎连通域 patch 等高频读者用）。</summary>
        public static SubstructureOwnership TryGet(Map map)
        {
            for (int i = 0; i < map.components.Count; i++)
            {
                if (map.components[i] is SubstructureOwnership ownership)
                {
                    return ownership;
                }
            }
            return null;
        }

        // ---- 降落/生成管线的显式盖章 ----
        // 放置发生在 InitiateLanding 内部的 LongEvent 中，push/pop 式作用域无法覆盖，
        // 改用「待盖章」登记：管线预先登记 (map, cells, ownerId)，SetFoundation postfix
        // 消费；LandingEnded 后统一清除。同一图的后续登记直接覆盖，残登记无副作用。

        private static readonly Dictionary<Map, KeyValuePair<HashSet<IntVec3>, int>> PendingStamps =
            new Dictionary<Map, KeyValuePair<HashSet<IntVec3>, int>>();

        public static void SetPendingStamp(Map map, IEnumerable<IntVec3> cells, int ownerId)
        {
            PendingStamps[map] = new KeyValuePair<HashSet<IntVec3>, int>(
                new HashSet<IntVec3>(cells), ownerId);
        }

        /// <summary>该格是否命中待盖章（生成/降落管线优先于邻接规则）。</summary>
        public static bool TryGetStamp(Map map, IntVec3 cell, out int ownerId)
        {
            ownerId = Unassigned;
            return PendingStamps.TryGetValue(map, out KeyValuePair<HashSet<IntVec3>, int> stamp)
                && stamp.Value != Unassigned
                && stamp.Key.Contains(cell)
                && (ownerId = stamp.Value) != Unassigned;
        }

        public static void ClearAllStamps()
        {
            PendingStamps.Clear();
        }

        /// <summary>移除某图的待盖章登记（同步生成管线收尾；降落管线用 ClearAllStamps 统一清）。</summary>
        public static void RemovePendingStamp(Map map)
        {
            PendingStamps.Remove(map);
        }

        /// <summary>读档/场景重置：静态登记随流程状态一并清理。</summary>
        public static void ResetStatic()
        {
            PendingStamps.Clear();
        }
    }
}
