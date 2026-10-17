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
        /// <summary>所在图上有其他有效船 / 停在天然地面 / 地表图：与承载环境对接，禁止主动出力。</summary>
        Docked,

        /// <summary>图上唯一有效船且不在天然地面上：可驾驶、可自动驾驶。</summary>
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
            return ships.FirstOrDefault(ship => ship.Faction == Faction.OfPlayer) ??
                (ships.Count == 1 ? ships[0] : ShipBase.Invalid);
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
        {
            if (map == null || ownerId <= 0) return false;
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole))
            {
                CompDeltaVPilotConsole console = thing.TryGetComp<CompDeltaVPilotConsole>();
                if (console != null && console.shipId == ownerId && console.isMain) return true;
            }
            return false;
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
            int now = Find.TickManager.TicksGame;
            CachedDomain cached;
            if (!byOwner.TryGetValue(ownerId, out cached)
                || SubstructureOwnership.CacheNeedsRefresh(cached.version, cached.cachedAtTick))
            {
                cached = new CachedDomain
                {
                    version = version,
                    cachedAtTick = now,
                    cells = FloodOwnedDomain(map, ownership, ownerId, seedCell),
                };
                byOwner[ownerId] = cached;
            }
            return cached.cells;
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
        /// 基架块搬运不能覆盖的持久内容。exclude 是本船将带走的控制域；其余无基架格上
        /// 的实物、屋顶或非真空地形必须留给宿主，不能让该 Map 继续作为本船的运动容器。
        /// </summary>
        internal static bool HasLooseContents(Map map, HashSet<IntVec3> exclude = null)
        {
            if (map == null || map.Disposed) return false;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing == null || thing.Destroyed || !thing.Spawned
                    || exclude?.Contains(thing.Position) == true
                    || SubstructureOwnership.HasFoundation(map, thing.Position)) continue;
                ThingCategory category = thing.def.category;
                if (category == ThingCategory.Mote || category == ThingCategory.Projectile
                    || category == ThingCategory.Gas
                    || category == ThingCategory.Ethereal && !(thing is Blueprint) && !(thing is Frame)) continue;
                return true;
            }
            foreach (IntVec3 cell in map.AllCells)
            {
                if (exclude?.Contains(cell) == true
                    || SubstructureOwnership.HasFoundation(map, cell)) continue;
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
            public int cachedAtTick;
            public HashSet<IntVec3> cells;
        }

        private static readonly Dictionary<Map, Dictionary<int, CachedDomain>> domainCache =
            new Dictionary<Map, Dictionary<int, CachedDomain>>();

        /// <summary>读档 / 场景重置时清空（可重建缓存纪律，AGENTS §15）。</summary>
        public static void ClearCaches()
        {
            domainCache.Clear();
            SubstructureOwnership.BumpStructureVersion();
        }

        public static void Forget(Map map)
        {
            if (map != null) domainCache.Remove(map);
        }

        // ---- 状态派生（§5.2）----

        /// <summary>
        /// 对接状态由同图组成与停放关系判定（§5.2，派生不存档）：图上有其他有效船、
        /// 停在天然地面（基架下是非 Space 的真实地面 = 小行星）或地表图（无承载容器）
        /// 均为对接；无主台残骸不构成船，与残骸合图的唯一船仍自由飞行。
        /// </summary>
        public static ShipDerivedState StateOf(Ship ship)
        {
            if (!ship.IsValid) return ShipDerivedState.Docked;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject frame = universe?.FrameOf(ship.map);
            if (frame == null) return ShipDerivedState.Docked;
            // 停放在本船上的成员不能直接驾驶宿主；只计算该容器的独立驾驶主体。
            if (frame.Ships.Any(other => other.Identity != new DriveSubject(0, ship.ownerId)
                && other.CanDriveIn(frame))) return ShipDerivedState.Docked;
            if (RestsOnNaturalGround(ship)) return ShipDerivedState.Docked;
            return ShipDerivedState.FreeFlight;
        }

        public static bool IsFreeFlight(Ship ship) => StateOf(ship) == ShipDerivedState.FreeFlight;

        /// <summary>
        /// 船是否停在天然地面上（小行星停放，§5.2）：控制域任一格的基架之下存在
        /// 非 Space 的真实地面（TerrainGrid 的 top/under 层，忽略 foundation 层）。
        /// 口袋图结构外全部是 Space，天然不命中；不能凭 worldtile/POI 类型推定。
        /// </summary>
        public static bool RestsOnNaturalGround(Ship ship)
        {
            if (!ship.IsValid) return false;
            TerrainGrid terrain = ship.map.terrainGrid;
            foreach (IntVec3 cell in ControlDomain(ship))
            {
                TerrainDef baseTerrain = terrain.BaseTerrainAt(cell);
                if (baseTerrain != null && baseTerrain != TerrainDefOf.Space)
                {
                    return true;
                }
            }
            return false;
        }

        // ---- 位姿派生（§11：船无第二套积分状态）----

        /// <summary>控制域包围盒中心（连续 Map 坐标）。口袋图居中放置时即口袋几何中心。</summary>
        public static Vector2 HullCenter(Ship ship)
        {
            return SpaceTransform.BoundsCenterOfCells(ControlDomain(ship));
        }

        /// <summary>船帧相对所在地图帧的朝向 σ：主台 East ⇔ 0（主台朝向定义船头，§3.2）。</summary>
        public static float LocalFrameAngle(Ship ship)
        {
            return ship.console != null && ship.console.parent != null
                ? ShipOrientation.AngleFromRot4(ship.console.parent.Rotation)
                : 0f;
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
