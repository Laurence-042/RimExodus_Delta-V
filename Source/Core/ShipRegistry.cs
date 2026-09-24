using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 一艘飞船的查询句柄（解耦架构 §2）：主驾驶台认领的归属 + 它当前所在地图。
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
    }

    public enum ShipDerivedState : byte
    {
        /// <summary>所在图上有其他有效船 / 停在天然地面 / 地表图：与承载环境对接，禁止主动出力。</summary>
        Docked,

        /// <summary>图上唯一有效船且不在天然地面上：可驾驶、可自动驾驶。</summary>
        FreeFlight
    }

    /// <summary>
    /// 飞船查询唯一入口（解耦架构 §2/§3/§8.1）。飞船 = 认领归属的主驾驶台 + 从其脚下
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
    /// ShipPose/HeadingAngle 只用于显示与交互。
    /// </summary>
    public static class ShipRegistry
    {
        // ---- 枚举 ----

        /// <summary>图上全部有效飞船（有主台的归属）。主台即船，一归属至多一船。</summary>
        public static List<Ship> ShipsOnMap(Map map)
        {
            var ships = new List<Ship>();
            if (map == null || map.Disposed) return ships;
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole))
            {
                CompDeltaVPilotConsole console = thing.TryGetComp<CompDeltaVPilotConsole>();
                if (console?.isMain != true || console.shipId <= 0) continue;
                ships.Add(new Ship(console.shipId, map, console));
            }
            return ships;
        }

        /// <summary>按归属 ID 找船（扫全部已加载图）。找不到返回 <see cref="Ship.Invalid"/>。</summary>
        public static Ship ShipByOwner(int ownerId)
        {
            if (ownerId <= 0) return Ship.Invalid;
            foreach (Map map in Find.Maps)
            {
                foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole))
                {
                    CompDeltaVPilotConsole console = thing.TryGetComp<CompDeltaVPilotConsole>();
                    if (console?.isMain == true && console.shipId == ownerId)
                    {
                        return new Ship(ownerId, map, console);
                    }
                }
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
                || cached.version != version
                || now - cached.cachedAtTick > DomainRescanIntervalTicks)
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
        /// 域缓存重扫间隔（tick）：已知写入（归属/盖章/放置/剥离）经全局结构版本即时
        /// 失效；拆除基架等少数未经挂钩的地形变更允许至多 1 秒过期（与质量报告的
        /// RescanIntervalTicks 同风格的折衷）。
        /// </summary>
        private const int DomainRescanIntervalTicks = 60;

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
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                if (visited.Contains(cell) || exclude.Contains(cell)) continue;
                TerrainDef foundation = map.terrainGrid.FoundationAt(cell);
                if (foundation == null || !foundation.IsSubstructure) continue;
                var block = new HashSet<IntVec3>();
                map.floodFiller.FloodFill(cell, c => c.InBounds(map)
                        && map.terrainGrid.FoundationAt(c) != null
                        && map.terrainGrid.FoundationAt(c).IsSubstructure
                        && !exclude.Contains(c),
                    c => { block.Add(c); return false; });
                visited.UnionWith(block);
                blocks.Add(block);
            }
            return blocks;
        }

        private static HashSet<IntVec3> FloodOwnedDomain(Map map, SubstructureOwnership ownership,
            int ownerId, IntVec3 seedCell)
        {
            var cells = new HashSet<IntVec3>();
            if (!seedCell.InBounds(map)) return cells;
            map.floodFiller.FloodFill(seedCell, c => c.InBounds(map)
                    && map.terrainGrid.FoundationAt(c) != null
                    && map.terrainGrid.FoundationAt(c).IsSubstructure
                    && ownership.OwnerAt(c) == ownerId,
                c => { cells.Add(c); return false; });
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
            if (universe == null || universe.FrameOf(ship.map) == null) return ShipDerivedState.Docked;
            if (ShipsOnMap(ship.map).Count > 1) return ShipDerivedState.Docked;
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
                TerrainDef foundation = terrain.FoundationAt(cell);
                if (foundation == null || !foundation.IsSubstructure) continue;
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
        public static SpacePose ShipPose(Ship ship)
        {
            if (!ship.IsValid) return new SpacePose(Vector3.right, 0f);
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject frame = universe?.FrameOf(ship.map);
            if (frame == null) return new SpacePose(Vector3.right, 0f);
            return SpaceTransform.RigidPointPose(frame.pose,
                SpaceTransform.MapToShipLocal(ship.map, HullCenter(ship)), LocalFrameAngle(ship));
        }

        /// <summary>船艏方向（宇宙角）：容器角 ⊕ σ。旧 HeadingAngle 的派生等价物。</summary>
        public static float HeadingAngle(Ship ship)
        {
            if (!ship.IsValid) return 0f;
            SpaceObject frame = Current.Game?.GetComponent<UniverseManager>()?.FrameOf(ship.map);
            return frame == null ? 0f : SpaceTransform.WrapAngle(frame.pose.angle + LocalFrameAngle(ship));
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
