using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 基架控制域的查询快照（由 FoundationShip 包装为飞船）（解耦架构 §2）：主驾驶台认领的归属 + 它当前所在地图。
    /// 身份 = 归属 ID（全局唯一、经 AllocateOwnerId 发放、永不回收）——归属格只存在于
    /// 一张图上，故 (ownerId) 即全局定位。本结构是可重建派生查询结果，不是存档事实：
    /// 事实在 CompDeltaVPilotConsole（认领）与 SubstructureOwnership（归属格）。
    /// 使用前必须经 <see cref="IsValid"/> 校验（主台被拆/船已转移会使句柄过期）。
    /// </summary>
    public readonly struct Ship
    {
        public readonly int ownerId;
        public readonly Map map;
        public readonly CompDeltaVPilotConsole console;

        public Ship(int ownerId, Map map, CompDeltaVPilotConsole console)
        {
            this.ownerId = ownerId;
            this.map = map;
            this.console = console;
        }

        public bool IsValid =>
            ownerId > 0 && console != null && console.parent != null
            && console.parent.Spawned && !console.parent.Destroyed
            && console.isMain && console.shipId == ownerId
            && map != null && !map.Disposed && console.parent.Map == map;

        public static readonly Ship Invalid = new Ship(0, null, null);

        public static implicit operator ShipBase(Ship ship) => ship.IsValid ? ship.console.Vessel : new FoundationShip(ship);

    }

    public enum ShipDerivedState : byte
    {
        /// <summary>工作图、其他独立船或整图天然地面宿主：主动出力前先提取本船。</summary>
        Docked,

        /// <summary>普通飞行图唯一独立船：主动出力前先搬出断片和散落内容。</summary>
        FreeFlight
    }

    /// <summary>
    /// 飞船查询唯一入口（解耦架构 §2/§3/§8.1）。基架实现的控制域 = 认领归属的主驾驶台 + 从其脚下
    /// 基架起四邻接且归属相同的连续基架（控制域）。全部方法为只读派生查询，不保存事实。
    ///
    /// 两种连通语义严格区分（§8.1，不得混用、不得在调用方另写近似）：
    /// - 控制域（ControlDomain）：同归属四连通——驾驶、捕获、预览的范围；
    /// - 残留块（ResidualBlocks）：剩余基架物理四连通（不分归属）——拆分时的块划分。
    ///
    /// 位姿派生（§11）：船不持有可与容器分叉的第二套宇宙积分状态——
    /// 船帧角 σ = AngleFromRot4(主台.Rotation)（控制台 East ⇔ 船帧对齐地图帧，
    /// 与原自由飞行口袋图 East 规范一致）；锚 = 控制域包围盒中心（口袋图居中放置时
    /// 即口袋几何中心，与旧 dockedOrigin 注册语义连续）。
    /// 自由飞行船的控制/物理直接作用于其容器（FrameOf(map).pose）；本类的
    /// ShipPose 只用于显示与交互。
    /// </summary>
    public static class ShipRegistry
    {
        // ---- 枚举 ----

        /// <summary>图上普通基架船视图（有主台的归属）；结构操作不包含单建筑船。</summary>
        public static List<Ship> FoundationShipsOnMap(Map map)
            => AllShipsOnMap(map).OfType<FoundationShip>().Select(ship => ship.Structure).ToList();

        /// <summary>船体枚举唯一入口；基架船及停靠/飞行中的单建筑船均为 ShipBase 的并列实现。</summary>
        public static IEnumerable<ShipBase> AllShipsOnMap(Map map) =>
            Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map)?.Ships ?? DiscoverShipsOnMap(map);

        internal static IEnumerable<ShipBase> DiscoverShipsOnMap(Map map)
        {
            if (map == null || map.Disposed) yield break;
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole))
            {
                CompDeltaVPilotConsole console = thing.TryGetComp<CompDeltaVPilotConsole>();
                if (console?.isMain != true || console.shipId <= 0) continue;
                var ship = new Ship(console.shipId, map, console);
                if (ship.IsValid) yield return ship;
            }
            foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Transporter))
            {
                ShipBase ship = thing.TryGetComp<CompSpaceShuttle>()?.Vessel;
                if (ship?.IsValid == true) yield return ship;
            }
        }

        public static ShipBase ShipByIdentity(DriveSubject identity) =>
            Current.Game?.GetComponent<UniverseManager>()?.Controls.ResolveShip(identity) ?? ShipBase.Invalid;

        public static ShipBase ShipOf(Thing thing, bool requireWholeFootprint = false)
        {
            if (thing?.Map == null) return ShipBase.Invalid;
            // 单建筑船内置的武器归于自身，而非脚下宿主基架。
            CompSpaceShuttle hull = thing.TryGetComp<CompSpaceShuttle>();
            ShipBase ship = hull != null ? hull.Vessel : ShipByThing(thing);
            if (!ship.IsValid || !requireWholeFootprint) return ship;
            IEnumerable<IntVec3> cells = ship.HullCells;
            return thing.OccupiedRect().Cells.All(cells.Contains) ? ship : ShipBase.Invalid;
        }

        public static ShipBase DefaultVesselOnMap(Map map)
        {
            var ships = AllShipsOnMap(map).ToList();
            return ships.FirstOrDefault(ship => ship.PlayerMayOperate) ??
                (ships.Count == 1 ? ships[0] : ShipBase.Invalid);
        }

        /// <summary>侧边栏结构归属（含无主台及未占有基架），与单建筑船共用已有身份类型。</summary>
        internal static IEnumerable<DriveSubject> StructuresOnMap(Map map)
        {
            foreach (int owner in FoundationDomains(map).Keys.OrderBy(id => id))
                yield return new DriveSubject(0, owner);
            foreach (ShipBase ship in AllShipsOnMap(map))
                if (ship.Identity.shuttle) yield return ship.Identity;
        }

        internal static bool HasStructure(Map map, DriveSubject identity) => identity.shuttle
            ? AllShipsOnMap(map).Any(ship => ship.Identity == identity)
            : FoundationDomains(map).ContainsKey(identity.key);

        internal static IEnumerable<IntVec3> StructureCells(Map map, DriveSubject identity)
        {
            if (identity.shuttle)
                return AllShipsOnMap(map).FirstOrDefault(ship => ship.Identity == identity)?.HullCells
                    ?? Enumerable.Empty<IntVec3>();
            return FoundationDomains(map).TryGetValue(identity.key, out HashSet<IntVec3> cells)
                ? cells : Enumerable.Empty<IntVec3>();
        }

        private sealed class CachedStructures
        {
            internal int version;
            internal readonly Dictionary<int, HashSet<IntVec3>> domains = new Dictionary<int, HashSet<IntVec3>>();
        }

        private static readonly Dictionary<Map, CachedStructures> structureCache = new Dictionary<Map, CachedStructures>();
        private static readonly Dictionary<int, HashSet<IntVec3>> noStructures = new Dictionary<int, HashSet<IntVec3>>();

        private static Dictionary<int, HashSet<IntVec3>> FoundationDomains(Map map)
        {
            if (map == null || map.Disposed) return noStructures;
            if (!structureCache.TryGetValue(map, out CachedStructures cached)
                || SubstructureOwnership.CacheNeedsRefresh(cached.version))
            {
                cached = new CachedStructures { version = SubstructureOwnership.GlobalStructureVersion };
                SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
                foreach (IntVec3 cell in SubstructureOwnership.CellsOnMap(map))
                {
                    int owner = ownership?.OwnerAt(cell) ?? SubstructureOwnership.Unassigned;
                    if (!cached.domains.TryGetValue(owner, out HashSet<IntVec3> cells))
                        cached.domains[owner] = cells = new HashSet<IntVec3>();
                    cells.Add(cell);
                }
                structureCache[map] = cached;
            }
            return cached.domains;
        }

        /// <summary>按归属 ID 查询统一身份索引。找不到返回 <see cref="Ship.Invalid"/>。</summary>
        public static Ship ShipByOwner(int ownerId) =>
            (ShipByIdentity(new DriveSubject(0, ownerId)) as FoundationShip)?.Structure ?? Ship.Invalid;

        /// <summary>
        /// 点击物所在归属域的船：扫描 OccupiedRect 首个能解析为有效船（同图主台）的归属格。
        /// 未占有格、无主台残骸归属与无归属组件的图自然落空（不在地表生效）。
        /// </summary>
        public static Ship ShipByThing(Thing thing)
        {
            Map map = thing?.MapHeld;
            if (map == null) return Ship.Invalid;
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
            if (ownership == null) return Ship.Invalid;
            foreach (IntVec3 cell in thing.OccupiedRect())
            {
                Ship ship = (AllShipsOnMap(map).FirstOrDefault(s => s.Identity ==
                    new DriveSubject(0, ownership.OwnerAt(cell))) as FoundationShip)?.Structure ?? Ship.Invalid;
                if (ship.IsValid && ship.map == map) return ship;
            }
            return Ship.Invalid;
        }

        /// <summary>图上是否存在归属 ownerId 的主台（判定「已有主台的归属」；不要求主台仍 Spawned）。</summary>
        public static bool OwnershipHasMainConsole(Map map, int ownerId)
            => MainConsoleForOwner(map, ownerId) != null;

        public static CompDeltaVPilotConsole MainConsoleForOwner(Map map, int ownerId)
        {
            if (map == null || ownerId <= 0) return null;
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole))
            {
                CompDeltaVPilotConsole console = thing.TryGetComp<CompDeltaVPilotConsole>();
                if (console != null && console.shipId == ownerId && console.isMain) return console;
            }
            return null;
        }

        /// <summary>玩家建筑所在的无主台基架域；Alert 与侧边栏共用，不把 shuttle 当作缺主台的船。</summary>
        public static IEnumerable<Thing> MissingPrimaryConsoleTargets(Map map)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            Faction player = Faction.OfPlayerSilentFail;
            if (map == null || map.Disposed || player == null || universe?.FrameOf(map) == null
                || universe.Generation.IsGenerating(map) || universe.ShipTransitions.IsAwaitingDockingCleanup(map))
                yield break;
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
            if (ownership == null) yield break;
            var seen = new HashSet<int>();
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing is not Building || thing.Faction != player || !thing.Spawned || thing.Destroyed
                    || thing.TryGetComp<CompSpaceShuttle>() != null
                    || !SubstructureOwnership.HasFoundation(map, thing.Position)) continue;
                int owner = ownership.OwnerAt(thing.Position);
                if (seen.Add(owner) && !OwnershipHasMainConsole(map, owner)) yield return thing;
            }
        }

        // ---- 控制域 / 残留块 ----

        /// <summary>
        /// 船的控制域：从主台脚下起、四邻接且归属相同的全部基架格（§3.1，唯一实现）。
        /// 结果按 (图, 归属) 缓存，经 <see cref="SubstructureOwnership.GlobalStructureVersion"/>
        /// 失效；只读，调用方不得修改。
        /// </summary>
        public static HashSet<IntVec3> ControlDomain(Ship ship)
        {
            if (!ship.IsValid) return new HashSet<IntVec3>();
            return ControlDomain(ship.map, ship.ownerId, ship.console.parent.Position);
        }

        public static HashSet<IntVec3> ControlDomain(Map map, int ownerId, IntVec3 seedCell)
        {
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
            if (ownership == null || ownerId <= 0) return new HashSet<IntVec3>();
            Dictionary<int, CachedDomain> byOwner;
            if (!domainCache.TryGetValue(map, out byOwner))
            {
                byOwner = new Dictionary<int, CachedDomain>();
                domainCache[map] = byOwner;
            }
            int version = SubstructureOwnership.GlobalStructureVersion;
            CachedDomain cached;
            if (!byOwner.TryGetValue(ownerId, out cached)
                || SubstructureOwnership.CacheNeedsRefresh(cached.version))
            {
                cached = new CachedDomain
                {
                    version = version,
                    cells = FloodOwnedDomain(map, ownership, ownerId, seedCell),
                };
                byOwner[ownerId] = cached;
            }
            return cached.cells;
        }

        /// <summary>
        /// 出力前只判断是否需要残片清单；实际清单仍使用 ControlDomain/ResidualBlocks。
        /// </summary>
        internal static bool HasResidualBlocks(Ship ship)
        {
            if (!ship.IsValid) return false;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            return universe != null ? universe.FoundationTopology.HasResidual(ship)
                : ResidualBlocks(ship.map, ControlDomain(ship)).Count != 0;
        }

        /// <summary>
        /// 残留块划分：图上不在 exclude（控制域等保留集）内的全部基架格按**物理四连通**
        /// （不分归属）分块（§8.1 残留块语义，唯一实现）。用于出力前拆分与脱离清单。
        /// </summary>
        public static List<HashSet<IntVec3>> ResidualBlocks(Map map, HashSet<IntVec3> exclude)
        {
            var blocks = new List<HashSet<IntVec3>>();
            if (map == null) return blocks;
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
            if (ownership == null) return blocks;
            var visited = new HashSet<IntVec3>();
            foreach (IntVec3 cell in SubstructureOwnership.CellsOnMap(map))
            {
                if (visited.Contains(cell) || exclude.Contains(cell)) continue;
                var block = new HashSet<IntVec3>();
                SubstructureOwnership.CollectConnected(map, cell, block, c => !exclude.Contains(c));
                visited.UnionWith(block);
                blocks.Add(block);
            }
            return blocks;
        }

        /// <summary>
        /// 源图善后的持久内容检查；不参与出力分离方向判定。
        /// </summary>
        internal static bool HasLooseContents(Map map)
        {
            if (map == null || map.Disposed) return false;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing == null || thing.Destroyed || !thing.Spawned
                    || SubstructureOwnership.HasFoundation(map, thing.Position)) continue;
                ThingCategory category = thing.def.category;
                if (category == ThingCategory.Filth || category == ThingCategory.Mote || category == ThingCategory.Projectile
                    || category == ThingCategory.Gas
                    || category == ThingCategory.Ethereal && !(thing is Blueprint) && !(thing is Frame)) continue;
                return true;
            }
            foreach (IntVec3 cell in map.AllCells)
            {
                if (SubstructureOwnership.HasFoundation(map, cell)) continue;
                if (map.roofGrid.RoofAt(cell) != null) return true;
                TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
                if (terrain != null && terrain != TerrainDefOf.Space && terrain != DeltaVDefOf.DeltaV_Void)
                    return true;
            }
            return false;
        }

        private static HashSet<IntVec3> FloodOwnedDomain(Map map, SubstructureOwnership ownership,
            int ownerId, IntVec3 seedCell)
        {
            var cells = new HashSet<IntVec3>();
            SubstructureOwnership.CollectConnected(map, seedCell, cells,
                c => ownership.OwnerAt(c) == ownerId);
            return cells;
        }

        private sealed class CachedDomain
        {
            public int version;
            public HashSet<IntVec3> cells;
            public Vector2? center;
        }

        private static readonly Dictionary<Map, Dictionary<int, CachedDomain>> domainCache =
            new Dictionary<Map, Dictionary<int, CachedDomain>>();

        /// <summary>读档 / 场景重置时清空（可重建缓存纪律，AGENTS §15）。</summary>
        public static void ClearCaches()
        {
            domainCache.Clear();
            structureCache.Clear();
            SubstructureOwnership.BumpStructureVersion();
        }

        public static void Forget(Map map)
        {
            if (map != null) domainCache.Remove(map);
            if (map != null) structureCache.Remove(map);
        }

        // ---- 状态派生（§5.2）----

        /// <summary>
        /// 显示状态与出力分离方向共用宿主判定；残骸和散落内容不构成其他船。
        /// </summary>
        public static ShipDerivedState StateOf(Ship ship) =>
            RequiresOwnPocket(ship) ? ShipDerivedState.Docked : ShipDerivedState.FreeFlight;

        internal static bool RequiresOwnPocket(Ship ship)
        {
            if (!ship.IsValid) return true;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject frame = universe?.FrameOf(ship.map);
            if (frame == null || frame.IsWorksite) return true;
            // 停放在本船上的成员不能直接驾驶宿主；只计算该容器的独立驾驶主体。
            if (frame.Ships.Any(other => other.Identity != new DriveSubject(0, ship.ownerId)
                && other.IsValid && other.CanDriveIn(frame))) return true;
            return SubstructureOwnership.For(ship.map).HasNaturalGround;
        }

        public static bool IsFreeFlight(Ship ship) => StateOf(ship) == ShipDerivedState.FreeFlight;

        // ---- 位姿派生（§11：船无第二套积分状态）----

        /// <summary>控制域包围盒中心（连续 Map 坐标）。口袋图居中放置时即口袋几何中心。</summary>
        public static Vector2 HullCenter(Ship ship)
        {
            var cells = ControlDomain(ship);
            // Same domain entry, version and Map lifetime; never cache a moving universe pose.
            if (ship.IsValid && domainCache.TryGetValue(ship.map, out var owners)
                && owners.TryGetValue(ship.ownerId, out var cached) && ReferenceEquals(cells, cached.cells))
                return cached.center ?? (cached.center = SpaceTransform.BoundsCenterOfCells(cells)).Value;
            return SpaceTransform.BoundsCenterOfCells(cells);
        }

        /// <summary>船帧相对所在地图帧的朝向 σ：主台 East ⇔ 0（主台朝向定义船头，§3.2）。</summary>
        public static float LocalFrameAngle(Ship ship)
        {
            return ship.console != null && ship.console.parent != null
                ? ShipOrientation.AngleFromRot4(ship.console.parent.Rotation)
                : ShipOrientation.AngleFromRot4(Rot4.North);
        }

        /// <summary>
        /// 船的显示/交互位姿（唯一派生入口）：容器位姿 ⊕ 船体中心锚 ⊕ σ。
        /// 自由飞行船 ≈ 其容器位姿（锚 = 口袋中心）；停靠船 = 宿主刚体上的局部点位姿。
        /// 控制、物理与对接判据始终直接消费容器位姿（FrameOf(map).pose），不经本入口。
        /// </summary>
        public static SpacePose ShipPose(ShipBase ship)
        {
            if (ship?.IsValid != true) return new SpacePose(Vector3.right, 0f);
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject frame = universe?.FrameOf(ship.Map);
            if (frame == null) return new SpacePose(Vector3.right, 0f);
            return SpaceTransform.RigidPointPose(frame.pose,
                SpaceTransform.MapToShipLocal(ship.Map, ship.HullCenter), ship.FrameAngle);
        }

        /// <summary>无驾驶台结构仍是所在容器上的刚体点；其默认船艏为地图北侧。</summary>
        internal static SpacePose StructurePose(Map map, DriveSubject identity)
        {
            ShipBase ship = ShipByIdentity(identity);
            if (ship.IsValid && ship.Map == map) return ShipPose(ship);
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject frame = universe?.FrameOf(map);
            if (frame == null || !HasStructure(map, identity)) return new SpacePose(Vector3.right, 0f);
            Vector2 center = SpaceTransform.BoundsCenterOfCells(StructureCells(map, identity));
            return SpaceTransform.RigidPointPose(frame.pose,
                SpaceTransform.MapToShipLocal(map, center),
                ShipOrientation.AngleFromRot4(Rot4.North));
        }

        // ---- 成员 / 派系 / 引擎 ----

        public static Faction FactionOf(Ship ship)
        {
            return ship.console?.parent?.Faction;
        }

        /// <summary>Thing 是否在船的控制域内（成员判定唯一入口）。</summary>
        public static bool Contains(Ship ship, Thing thing)
        {
            if (!ship.IsValid || thing == null || thing.MapHeld != ship.map) return false;
            return ControlDomain(ship).Contains(thing.PositionHeld);
        }

        /// <summary>
        /// 图上与驾驶台关联的逆重引擎（战略发射仍需引擎）：设施链接优先，否则扫描引擎
        /// 所在归属域包含该驾驶台者。仅供战略发射语义；战术流程一律用控制域。
        /// </summary>
        public static Building_GravEngine EngineOn(Map map, Building console)
        {
            if (map == null || map.Disposed) return null;
            CompPilotConsole consoleComp = console?.TryGetComp<CompPilotConsole>();
            if (consoleComp?.engine != null && consoleComp.engine.Spawned && consoleComp.engine.Map == map)
            {
                return consoleComp.engine;
            }
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.GravEngine))
            {
                if (!(thing is Building_GravEngine engine)) continue;
                if (console != null && console.Spawned && console.Map == map
                    && ownership != null && console.TryGetComp<CompDeltaVPilotConsole>() != null
                    && ControlDomain(map, ownership.OwnerAt(console.Position), console.Position)
                        .Contains(engine.Position))
                {
                    return engine;
                }
            }
            return null;
        }
    }
}
