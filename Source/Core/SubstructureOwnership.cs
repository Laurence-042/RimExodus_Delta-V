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
    /// 基架与归属同步移除（2026-09-27 用户决策，替代旧「拆基架不清写」设计）：任何
    /// 路径的基架移除（TerrainGrid.RemoveFoundation / RemoveGravshipTerrainUnsafe 的
    /// Harmony postfix）立即把该格归属清为 Unassigned——「归属仅在有基架的格上有意义」
    /// 恒成立，WeaponsOn/OwnerShip/ShipByThing 等裸读者不再读到陈旧值，起飞/降落的
    /// 区域判定（域缓存、碰撞面、月池）随版本 bump 即时失效。SetFoundation 恒重写。
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

        /// <summary>结构缓存失效条件：只认统一触发推进的版本，不再按时间重扫。</summary>
        public static bool CacheNeedsRefresh(int cachedVersion) => cachedVersion != GlobalStructureVersion;

        private int[] ownerGrid;

        [System.NonSerialized] private bool cachedSupport;
        [System.NonSerialized] private int cachedSupportVersion = -1;

        /// <summary>HasMapSupport 的版本缓存；结构变化后首次查询重建，其余查询 O(1)。</summary>
        public bool HasSupport
        {
            get
            {
                if (cachedSupportVersion == GlobalStructureVersion) return cachedSupport;
                cachedSupport = map.AllCells.Any(cell => HasFoundation(map, cell)
                    || IsSupportTerrain(map.terrainGrid.TerrainAt(cell)));
                cachedSupportVersion = GlobalStructureVersion;
                return cachedSupport;
            }
        }

        private static bool IsSupportTerrain(TerrainDef terrain) =>
            terrain != null && terrain != TerrainDefOf.Space && terrain != DeltaVDefOf.DeltaV_Void;

        [System.NonSerialized] private bool cachedNaturalGround;
        [System.NonSerialized] private int cachedNaturalGroundVersion = -1;

        /// <summary>整图底层天然地面；忽略船上地板、基架及临时通道，按结构版本失效。</summary>
        internal bool HasNaturalGround
        {
            get
            {
                if (cachedNaturalGroundVersion == GlobalStructureVersion) return cachedNaturalGround;
                cachedNaturalGround = map.AllCells.Any(cell =>
                {
                    TerrainDef terrain = map.terrainGrid.BaseTerrainAt(cell);
                    return IsSupportTerrain(terrain) && terrain.natural && !terrain.isFoundation;
                });
                cachedNaturalGroundVersion = GlobalStructureVersion;
                return cachedNaturalGround;
            }
        }

        [System.NonSerialized] private Dictionary<IntVec3, int> pendingStamp;

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

        /// <summary>基架事实唯一入口：只读 foundation 层，地板与临时地形不改变船体结构。</summary>
        public static TerrainDef FoundationAt(Map map, IntVec3 cell)
        {
            if (map == null || !cell.InBounds(map)) return null;
            TerrainDef foundation = map.terrainGrid.FoundationAt(cell);
            return foundation?.IsSubstructure == true ? foundation : null;
        }

        public static bool HasFoundation(Map map, IntVec3 cell) => FoundationAt(map, cell) != null;

        /// <summary>归属、质量、剪影和几何共用的基架集合；不另存结构副本。</summary>
        public static IEnumerable<IntVec3> CellsOnMap(Map map)
        {
            if (map == null || map.Disposed) yield break;
            foreach (IntVec3 cell in CellRect.WholeMap(map))
                if (HasFoundation(map, cell)) yield return cell;
        }

        /// <summary>同一基架集合上的四连通查询；归属、排除域及原版力场限制由调用者提供。</summary>
        public static void CollectConnected(Map map, IntVec3 start, ICollection<IntVec3> result,
            System.Predicate<IntVec3> accepts, int maxCells = int.MaxValue)
        {
            result.Clear();
            if (!HasFoundation(map, start)) return;
            map.floodFiller.FloodFill(start,
                cell => HasFoundation(map, cell) && (accepts == null || accepts(cell)),
                cell => { result.Add(cell); return false; }, maxCells);
        }

        /// <summary>格子的基架归属；无基架或未指派返回 Unassigned。</summary>
        public int OwnerAt(IntVec3 cell)
        {
            if (ownerGrid == null || !HasFoundation(map, cell))
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
                foreach (IntVec3 cell in CellsOnMap(map))
                {
                    if (ownerGrid[map.cellIndices.CellToIndex(cell)] != Unassigned)
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
            foreach (IntVec3 cell in CellsOnMap(map))
            {
                if (ownerGrid[map.cellIndices.CellToIndex(cell)] == ownerId)
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
            foreach (IntVec3 cell in CellsOnMap(map))
            {
                if (ownerGrid[map.cellIndices.CellToIndex(cell)] != Unassigned)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>图上是否存在任何基架格。</summary>
        public bool HasAnySubstructure() => CellsOnMap(map).Any();

        /// <summary>归属 ownerId 的基架格数。</summary>
        public int CountCellsOwnedBy(int ownerId)
        {
            if (ownerGrid == null)
            {
                return 0;
            }
            int count = 0;
            foreach (IntVec3 cell in CellsOnMap(map))
            {
                if (ownerGrid[map.cellIndices.CellToIndex(cell)] == ownerId)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// 整图基架改归 ownerId，返回格数。读档时地图尚未纳管；运行时修复须通知。
        /// </summary>
        public int ClaimAllSubstructure(int ownerId)
        {
            int count = 0;
            foreach (IntVec3 cell in CellsOnMap(map))
            {
                Assign(cell, ownerId);
                count++;
            }
            if (count > 0 && Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map) != null)
                NotifyOwnershipChanged();
            return count;
        }

        /// <summary>图上现存基架的归属域清单（显示用：按 owner 分组的格子）。</summary>
        public List<KeyValuePair<int, List<IntVec3>>> OwnerDomains()
        {
            var domains = new Dictionary<int, List<IntVec3>>();
            if (ownerGrid != null)
            {
                foreach (IntVec3 cell in CellsOnMap(map))
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
                if (!c.InBounds(map) || !SubstructureOwnership.HasFoundation(map, c))
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
        // SetOwner / ResolveBuilt / ClaimAllSubstructure 这些语义
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
        /// 内部结构管线批写：通知图上全部引擎重算连通域、失效星球仪
        /// 剪影。SubstructureGrid.MarkDirty 只扫殖民者引擎，wreck 引擎也要失效，这里显式遍历。
        /// </summary>
        public void SetOwner(IEnumerable<IntVec3> cells, int ownerId)
        {
            if (NotifyIfChanged(cells, ownerId))
            {
                NotifyOwnershipChanged();
            }
        }

        /// <summary>玩家改划的唯一门禁；保护来源整域，内部搬运/盖章不受此限制。</summary>
        public AcceptanceReport CanReassign(IntVec3 cell, int targetOwnerId)
        {
            if (targetOwnerId <= 0 || !cell.InBounds(map) || !HasFoundation(map, cell)
                || OwnerAt(cell) == targetOwnerId) return false;
            if (ShipRegistry.OwnershipHasMainConsole(map, OwnerAt(cell)))
                return "DeltaV_ReassignProtectedDomain".Translate();
            return true;
        }

        public int Reassign(IEnumerable<IntVec3> cells, int targetOwnerId)
        {
            var accepted = new List<IntVec3>();
            foreach (IntVec3 cell in cells)
                if (CanReassign(cell, targetOwnerId).Accepted) accepted.Add(cell);
            SetOwner(accepted, targetOwnerId);
            return accepted.Count;
        }

        /// <summary>
        /// 从指定格认领相连的「本 ID 或未占有」基架域。驾驶台重生、自愈与未来其他
        /// 身份锚点必须复用此入口，不得在调用点另写 flood-fill。
        /// </summary>
        public void ClaimConnectedDomain(IntVec3 start, int ownerId)
        {
            if (ownerId <= 0 || !start.InBounds(map)) return;
            var cells = new List<IntVec3>();
            CollectConnected(map, start, cells, cell =>
            {
                int current = OwnerAt(cell);
                return current == ownerId || current == Unassigned;
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
                if (!cell.InBounds(map) || !SubstructureOwnership.HasFoundation(map, cell))
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
            Current.Game?.GetComponent<UniverseManager>()?.NotifyFoundationChanged(map);
            foreach (Thing thing in map.listerThings.AllThings)
                (thing as ThingWithComps)?.TryGetComp<CompRailgunReadinessAppearance>()?.Rebind();
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole))
                thing.TryGetComp<CompDeltaVPilotConsole>()?.SyncBackupIdentity();
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.GravEngine))
            {
                if (thing is Building_GravEngine engine)
                {
                    engine.ForceSubstructureDirty();
                }
            }
            PlanetGlobeRenderer.InvalidateSilhouette(map);
            Current.Game?.GetComponent<UniverseManager>()?.Weapons.NotifyMissileOwnershipChanged(map);
            Current.Game?.GetComponent<UniverseManager>()?.Controls.NotifyWeaponOwnershipChanged(map);
        }

        /// <summary>
        /// 基架移除同步（RemoveFoundation / RemoveGravshipTerrainUnsafe postfix 的唯一
        /// 落点）：该格归属清为 Unassigned + 版本 bump（域缓存/碰撞面/月池即时失效）
        /// + 引擎/剪影通知。邻接不重解：移除不产生新归属，断连由读取端 flood 截断。
        /// 与批末 ClearOwner(ownerId) 幂等共存。
        /// </summary>
        public void NotifyFoundationRemoved(IntVec3 cell)
        {
            if (!cell.InBounds(map))
            {
                return;
            }
            Current.Game?.GetComponent<UniverseManager>()?.Lifecycle.QueueStructureCheck(map);
            // 原版已移除 foundation，此处必须清持久网格，不能经只读 OwnerAt 的无基架过滤。
            if (ownerGrid == null || ownerGrid[map.cellIndices.CellToIndex(cell)] == Unassigned)
            {
                // Even unowned substructure participates in the collision surface.
                BumpStructureVersion();
                PlanetGlobeRenderer.InvalidateSilhouette(map);
                return;
            }
            Assign(cell, Unassigned);
            NotifyOwnershipChanged();
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
            var seen = new HashSet<IntVec3>();
            var component = new List<IntVec3>();
            foreach (IntVec3 delta in GenAdj.CardinalDirectionsAround)
            {
                IntVec3 neighbor = cell + delta;
                if (seen.Contains(neighbor)) continue;
                CollectConnected(map, neighbor, component, c => OwnerAt(c) == Unassigned);
                seen.UnionWith(component);
                region.AddRange(component);
            }
            // 争议检查：区域内任一格邻接其他有效归属 → 整片保持未占有。
            foreach (IntVec3 regionCell in region)
            {
                foreach (IntVec3 delta in GenAdj.CardinalDirectionsAround)
                {
                    IntVec3 neighbor = regionCell + delta;
                    if (neighbor.InBounds(map)
                        && SubstructureOwnership.HasFoundation(map, neighbor))
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
            pendingStamp = cells.Distinct().ToDictionary(cell => cell, cell => ownerId);
        }

        internal void SetPendingStamp(Dictionary<IntVec3, int> owners) => pendingStamp = owners;

        /// <summary>该格是否命中待盖章（生成/降落管线优先于邻接规则）。</summary>
        public bool TryGetStamp(IntVec3 cell, out int ownerId)
        {
            ownerId = Unassigned;
            return pendingStamp != null && pendingStamp.TryGetValue(cell, out ownerId);
        }

        public void ClearPendingStamp()
        {
            pendingStamp = null;
        }
    }
}
