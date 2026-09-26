using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 基架归属网格（2026-09-18 设计；2026-09-24 解耦重构修订）：每个基架格记录归属
    /// **结构 ID**。归属身份与 SpaceObject 对象身份分离（解耦架构 §2）——ID 仍经
    /// UniverseManager.AllocateOwnerId 同一计数器发放（共用号段防撞号），但不再通过数值
    /// 相等建立关系，不得用 ObjectById(ownerId) 推断船或地图归属。飞船引擎的范围/承载、
    /// 逆重发射与自由飞行捕获只按「自己的 ID」过滤；剪影按 ID 过滤；同图各归属各自保有
    /// 独立身份。归属格数据是存档事实（无法从地形推导）。
    ///
    /// 写入口唯一收口在 Harmony patch（Patch_SubstructureOwnership 的 TerrainGrid.
    /// SetFoundation postfix）：显式盖章（生成/降落/快照恢复管线）优先，其余按邻接规则
    /// （四邻同为基架且归属一致 → 继承并吸收相连未占有域；无邻接或多归属邻接 → 保持
    /// 未占有，交由玩家划分——多归属邻接**不拒建**，解耦架构 §4 已定）。仅 Delta-V
    /// 参考系图参与（RimExodus 地表零影响）。
    ///
    /// owner 值只在「当前有基架的格」上有意义：拆除基架不清写 owner（消费者一律先判
    /// 地形再读归属），SetFoundation 恒重写，因此残留值不可能被读到。
    /// </summary>
    public class SubstructureOwnership : MapComponent
    {
        public const int Unassigned = 0;

        /// <summary>
        /// 全局结构版本（单调递增）：任何归属/基架写入后 +1。ShipRegistry 控制域缓存与
        /// SpaceObject 拼接名缓存据此失效——只读派生查询不重复扫描事实源。
        /// </summary>
        public static int GlobalStructureVersion { get; private set; }

        public static void BumpStructureVersion()
        {
            GlobalStructureVersion++;
        }

        private int[] ownerGrid;

        [System.NonSerialized] private HashSet<IntVec3> pendingStampCells;
        [System.NonSerialized] private int pendingStampOwner;

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
                BumpStructureVersion();
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
                    if (map.terrainGrid.TerrainAt(cell)?.IsSubstructure == true
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
                if (map.terrainGrid.TerrainAt(cell)?.IsSubstructure == true
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
                if (map.terrainGrid.TerrainAt(cell)?.IsSubstructure == true
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
                if (map.terrainGrid.TerrainAt(cell)?.IsSubstructure == true)
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
                if (map.terrainGrid.TerrainAt(cell)?.IsSubstructure == true
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
                if (map.terrainGrid.TerrainAt(cell)?.IsSubstructure == true)
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
                    if (map.terrainGrid.TerrainAt(cell)?.IsSubstructure == true)
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
                if (!c.InBounds(map) || map.terrainGrid.TerrainAt(c)?.IsSubstructure != true)
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
            BumpStructureVersion();
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

        /// <summary>
        /// 从指定格认领相连的「本 ID 或未占有」基架域。驾驶台重生、自愈与未来其他
        /// 身份锚点必须复用此入口，不得在调用点另写 flood-fill。
        /// </summary>
        public void ClaimConnectedDomain(IntVec3 start, int ownerId)
        {
            if (ownerId <= 0 || !start.InBounds(map)) return;
            var cells = new List<IntVec3>();
            map.floodFiller.FloodFill(start, delegate (IntVec3 cell)
            {
                if (!cell.InBounds(map)) return false;
                TerrainDef foundation = map.terrainGrid.FoundationAt(cell);
                if (foundation == null || !foundation.IsSubstructure) return false;
                int current = OwnerAt(cell);
                return current == ownerId || current == Unassigned;
            }, delegate (IntVec3 cell)
            {
                cells.Add(cell);
                return false;
            });
            SetOwner(cells, ownerId);
        }

        /// <summary>
        /// 整图清除指定归属的全部格子（脱离停靠时清除源图旧域）。
        /// 不做地形过滤——要清的正是基架已随船走（捕获剥离）或断连遗留的陈旧值；
        /// 基架不重叠 ⇒ 按 ID 整清不会误伤他主域。清除后照常通知引擎重算与剪影失效。
        /// </summary>
        public void ClearOwner(int ownerId)
        {
            if (ownerId <= 0 || ownerGrid == null)
            {
                return;
            }
            bool changed = false;
            for (int i = 0; i < ownerGrid.Length; i++)
            {
                if (ownerGrid[i] == ownerId)
                {
                    ownerGrid[i] = Unassigned;
                    changed = true;
                }
            }
            if (changed)
            {
                BumpStructureVersion();
                NotifyOwnershipChanged();
            }
        }

        /// <summary>批写并返回是否有实际变化（不通知；建造钩子收尾统一通知）。</summary>
        private bool NotifyIfChanged(IEnumerable<IntVec3> cells, int ownerId)
        {
            bool changed = false;
            foreach (IntVec3 cell in cells)
            {
                if (!cell.InBounds(map) || map.terrainGrid.TerrainAt(cell)?.IsSubstructure != true)
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
        /// 建造完成时的归属落位（解耦架构 §4 已定语义）：新基架继承唯一有效邻接归属，
        /// 并把与之相连的**整片未占有（Unassigned）基架区域**一并同步为该归属（多行建造
        /// 时无论施工顺序如何，最终整片与船同归属）。区域若同时邻接其他有效归属
        /// （争议桥），只写新格本身、区域保持未占有，交由「修改基架归属」命令人工裁决。
        /// 孤立建造（无有效邻接）不分配 ID：保持未占有，等待后续相连吸收或经驾驶台
        /// 认领成船。**同时邻接多个不同归属不拒建**：保持未占有，交由玩家划分
        /// （2026-09-24 解耦重构取代旧「多归属拒建」）。
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
            // count >= 2：多归属邻接 → 同样保持未占有，玩家经「修改基架归属」划分；
            // 引擎连通域按归属过滤，未占有域不会被任何现有船吞并。
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
                    && map.terrainGrid.TerrainAt(neighbor)?.IsSubstructure == true
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
                        && map.terrainGrid.TerrainAt(neighbor)?.IsSubstructure == true
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
                        && map.terrainGrid.TerrainAt(neighbor)?.IsSubstructure == true)
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
                if (map.terrainGrid.TerrainAt(cell)?.IsSubstructure == true
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
                if (map.terrainGrid.TerrainAt(cell)?.IsSubstructure == true
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

        public void SetPendingStamp(IEnumerable<IntVec3> cells, int ownerId)
        {
            pendingStampCells = new HashSet<IntVec3>(cells);
            pendingStampOwner = ownerId;
        }

        /// <summary>该格是否命中待盖章（生成/降落管线优先于邻接规则）。</summary>
        public bool TryGetStamp(IntVec3 cell, out int ownerId)
        {
            ownerId = Unassigned;
            return pendingStampOwner != Unassigned
                && pendingStampCells != null
                && pendingStampCells.Contains(cell)
                && (ownerId = pendingStampOwner) != Unassigned;
        }

        public void ClearPendingStamp()
        {
            pendingStampCells = null;
            pendingStampOwner = Unassigned;
        }
    }
}
