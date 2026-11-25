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

        public static implicit operator ShipBase(Ship ship) => ship.IsValid ? ship.console.Vessel : ShipBase.Invalid;

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
    /// 控制域与残留块都是同归属四连通基架域，由 FoundationTopologyService 提供。
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

        /// <summary>驾驶台身份只取组件（含子类）；建筑 Def 不参与身份判定。</summary>
        internal static IEnumerable<CompDeltaVPilotConsole> ConsolesOnMap(Map map)
        {
            if (map == null || map.Disposed) yield break;
            var index = Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.For(map);
            if (index == null) yield break;
            foreach (CompDeltaVPilotConsole console in index.All.Consoles)
                if (console.parent.Spawned && console.parent.Map == map) yield return console;
        }

        /// <summary>图上普通基架船视图（有主台的归属）；结构操作不包含单建筑船。</summary>
        public static List<Ship> FoundationShipsOnMap(Map map)
            => AllShipsOnMap(map).OfType<FoundationShip>().Select(ship => ship.Structure).ToList();

        /// <summary>船体枚举唯一入口；基架船及停靠/飞行中的单建筑船均为 ShipBase 的并列实现。</summary>
        public static IEnumerable<ShipBase> AllShipsOnMap(Map map) =>
            Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map)?.Ships ?? DiscoverShipsOnMap(map);

        internal static IEnumerable<ShipBase> DiscoverShipsOnMap(Map map)
        {
            if (map == null || map.Disposed) yield break;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.FrameOf(map) is MapSpaceObject { PersonnelShip: { } personnel }
                && personnel.IsValid)
                yield return personnel;
            var index = universe?.ShipStructure.For(map);
            if (index == null) yield break;
            foreach (CompDeltaVPilotConsole console in index.Primaries)
                if (console.Ship.IsValid) yield return console.Vessel;
            foreach (CompSpaceShuttle shuttle in index.Shuttles)
                if (shuttle.Vessel.IsValid) yield return shuttle.Vessel;
        }

        public static ShipBase ShipByIdentity(DriveSubject identity) =>
            Current.Game?.GetComponent<UniverseManager>()?.Controls.ResolveShip(identity) ?? ShipBase.Invalid;

        public static ShipBase ShipOf(Thing thing, bool requireWholeFootprint = false)
        {
            if (thing?.Map == null) return ShipBase.Invalid;
            if (Current.Game?.GetComponent<UniverseManager>()?.FrameOf(thing.Map)
                is MapSpaceObject { PersonnelShip: { } personnel } && personnel.Contains(thing))
                return personnel;
            // 单建筑船内置的武器归于自身，而非脚下宿主基架。
            CompSpaceShuttle hull = thing.TryGetComp<CompSpaceShuttle>();
            if (hull != null) return hull.Vessel;
            var record = thing is Building building ? ShipStructureIndex.Read(thing.Map)?.Record(building) : null;
            ShipBase ship = record == null ? ShipByThing(thing) : record.Structure?.Vessel ?? ShipBase.Invalid;
            if (!ship.IsValid || !requireWholeFootprint) return ship;
            if (record != null && ship.GetType() == typeof(FoundationShip))
                return record.WholeDomain ? ship : ShipBase.Invalid;
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
            var byOwner = FoundationDomains(map);
            foreach (int owner in byOwner?.Keys.OrderBy(id => id) ?? Enumerable.Empty<int>())
                yield return new DriveSubject(0, owner);
            foreach (ShipBase ship in AllShipsOnMap(map))
                if (ship.Identity.kind != DriveSubjectKind.Foundation) yield return ship.Identity;
        }

        internal static bool HasStructure(Map map, DriveSubject identity) => identity.kind != DriveSubjectKind.Foundation
            ? AllShipsOnMap(map).Any(ship => ship.Identity == identity)
            : FoundationDomains(map)?.ContainsKey(identity.key) == true;

        internal static IEnumerable<IntVec3> StructureCells(Map map, DriveSubject identity)
        {
            if (identity.kind != DriveSubjectKind.Foundation)
                return AllShipsOnMap(map).FirstOrDefault(ship => ship.Identity == identity)?.HullCells
                    ?? Enumerable.Empty<IntVec3>();
            var byOwner = FoundationDomains(map);
            return byOwner != null && byOwner.TryGetValue(identity.key, out HashSet<IntVec3> cells)
                ? cells : Enumerable.Empty<IntVec3>();
        }

        internal static int StructureCellCount(Map map, DriveSubject identity)
        {
            if (identity.kind != DriveSubjectKind.Foundation)
            {
                ShipBase ship = ShipByIdentity(identity);
                return ship.IsValid && ship.Map == map ? HullBounds(ship).Area : 0;
            }
            return FoundationDomains(map)?.TryGetValue(identity.key, out var cells) == true ? cells.Count : 0;
        }

        internal static Vector2 StructureCenter(Map map, DriveSubject identity) => identity.kind != DriveSubjectKind.Foundation
            ? AllShipsOnMap(map).FirstOrDefault(ship => ship.Identity == identity)?.HullCenter ?? Vector2.zero
            : Current.Game?.GetComponent<UniverseManager>()?.FoundationTopology.OwnerCenter(map, identity.key) ?? Vector2.zero;

        internal static CellRect HullBounds(ShipBase ship)
        {
            if (ship.GetType() == typeof(FoundationShip) && ship is FoundationShip foundation)
                return Current.Game?.GetComponent<UniverseManager>()?.FoundationTopology.BoundsOf(ship.Map, ControlDomain(foundation.Structure))
                    ?? CellRect.Empty;
            if (ship.GetType() == typeof(ShuttleShip)) return ship.Anchor.OccupiedRect();
            return CellRect.FromCellList(ship.HullCells.ToList());
        }

        private static IReadOnlyDictionary<int, HashSet<IntVec3>> FoundationDomains(Map map) =>
            Current.Game?.GetComponent<UniverseManager>()?.FoundationTopology.OwnerCells(map);

        /// <summary>按归属 ID 查询统一身份索引。找不到返回 <see cref="Ship.Invalid"/>。</summary>
        public static Ship ShipByOwner(int ownerId) =>
            (ShipByIdentity(new DriveSubject(0, ownerId)) as FoundationShip)?.Structure ?? Ship.Invalid;

        /// <summary>所属基架连通域有主台时返回该船。</summary>
        public static Ship ShipByThing(Thing thing)
        {
            Map map = thing?.MapHeld;
            var topology = Current.Game?.GetComponent<UniverseManager>()?.FoundationTopology;
            if (topology == null || map == null) return Ship.Invalid;
            var index = ShipStructureIndex.Read(map);
            if (thing is Building building && index?.Record(building) is { Shuttle: null } record)
                return record.Structure?.Vessel is FoundationShip attached && attached.IsValid
                    ? attached.Structure : Ship.Invalid;
            if (thing is not Building && index?.StructureAt(thing.PositionHeld)?.Vessel is FoundationShip occupant)
                return occupant.Structure;
            var domain = thing is Building pendingBuilding ? topology.BuildingDomain(map, pendingBuilding)
                : topology.DomainAt(map, thing.PositionHeld);
            if (domain == null) return Ship.Invalid;
            int owner = SubstructureOwnership.For(map).OwnerAt(domain.First());
            var console = MainConsoleForOwner(map, owner);
            var ship = new Ship(owner, map, console);
            return ship.IsValid && domain.Contains(console.parent.Position) ? ship : Ship.Invalid;
        }

        /// <summary>图上是否存在归属 ownerId 的主台（判定「已有主台的归属」；不要求主台仍 Spawned）。</summary>
        public static bool OwnershipHasMainConsole(Map map, int ownerId)
            => MainConsoleForOwner(map, ownerId) != null;

        public static CompDeltaVPilotConsole MainConsoleForOwner(Map map, int ownerId)
        {
            if (map == null || ownerId <= 0) return null;
            CompDeltaVPilotConsole console = Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.For(map)?.Primary(ownerId);
            return console?.Ship.IsValid == true ? console : null;
        }

        /// <summary>玩家建筑所在的无主台基架域；Alert 与侧边栏共用，不把 shuttle 当作缺主台的船。</summary>
        public static IEnumerable<Thing> MissingPrimaryConsoleTargets(Map map)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            Faction player = Faction.OfPlayerSilentFail;
            if (map == null || map.Disposed || player == null || universe?.FrameOf(map) == null
                || universe.Generation.IsGenerating(map) || universe.ShipTransitions.IsAwaitingDockingCleanup(map))
                yield break;
            foreach (Building building in universe.ShipStructure.For(map).MissingPrimaryTargets(player))
                yield return building;
        }

        // ---- 控制域 / 残留块 ----

        /// <summary>
        /// 船的控制域：从主台脚下起、四邻接且归属相同的全部基架格（§3.1，唯一实现）。
        /// 结果直接读取对应地图稳定的归属连通域，调用方只读。
        /// </summary>
        public static HashSet<IntVec3> ControlDomain(Ship ship)
        {
            if (!ship.IsValid) return new HashSet<IntVec3>();
            return ControlDomain(ship.map, ship.ownerId, ship.console.parent.Position);
        }

        public static HashSet<IntVec3> ControlDomain(Map map, int ownerId, IntVec3 seedCell)
        {
            var topology = Current.Game?.GetComponent<UniverseManager>()?.FoundationTopology;
            return ownerId > 0 && SubstructureOwnership.TryGet(map)?.OwnerAt(seedCell) == ownerId
                ? topology?.DomainAt(map, seedCell) ?? new HashSet<IntVec3>()
                : new HashSet<IntVec3>();
        }

        internal static bool HasResidualBlocks(Ship ship) => ship.IsValid
            && Current.Game?.GetComponent<UniverseManager>()?.FoundationTopology.HasResidual(ship) == true;

        public static List<HashSet<IntVec3>> ResidualBlocks(Map map, HashSet<IntVec3> exclude)
        {
            var topology = Current.Game?.GetComponent<UniverseManager>()?.FoundationTopology;
            return topology?.Domains(map).Where(domain => !ReferenceEquals(domain, exclude) && !domain.SetEquals(exclude)).ToList()
                ?? new List<HashSet<IntVec3>>();
        }

        /// <summary>
        /// 源图善后的持久内容检查；不参与出力分离方向判定。
        /// </summary>
        internal static bool HasLooseContents(Map map) => ShipStructureIndex.Read(map) is { } view
            && (view.HasLooseThings || view.HasLooseTerrain);

        public static void ClearCaches()
        {
            Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.Clear();
        }

        public static void Forget(Map map)
        {
            Current.Game?.GetComponent<UniverseManager>()?.ForgetFoundationTopology(map);
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
            return ship.console.Vessel is FoundationShip vessel
                ? vessel.RequiresSeparatePocket(frame, universe.FoundationTopology)
                : HasSeparateHost(ship, frame, universe.FoundationTopology);
        }

        internal static bool HasSeparateHost(Ship ship, SpaceObject frame, FoundationTopologyService topology)
        {
            if (SubstructureOwnership.For(ship.map).HasNaturalGround
                || topology.HasUnboundBuildings(ship.map)) return true;
            foreach (ShipBase other in frame.Ships)
            {
                if (!other.IsValid || other.Identity == new DriveSubject(0, ship.ownerId)) continue;
                if (other.Transfer.body is Building body
                    ? topology.BuildingDomain(ship.map, body) == null : other.CanDriveIn(frame)) return true;
            }
            return false;
        }

        public static bool IsFreeFlight(Ship ship) => StateOf(ship) == ShipDerivedState.FreeFlight;

        // ---- 位姿派生（§11：船无第二套积分状态）----

        /// <summary>控制域包围盒中心（连续 Map 坐标）。口袋图居中放置时即口袋几何中心。</summary>
        public static Vector2 HullCenter(Ship ship)
        {
            var topology = Current.Game?.GetComponent<UniverseManager>()?.FoundationTopology;
            return topology?.CenterOf(ship.map, ControlDomain(ship)) ?? Vector2.zero;
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
            Vector2 center = StructureCenter(map, identity);
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
            return thing is Building ? ShipByThing(thing).ownerId == ship.ownerId
                : ControlDomain(ship).Contains(thing.PositionHeld);
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
