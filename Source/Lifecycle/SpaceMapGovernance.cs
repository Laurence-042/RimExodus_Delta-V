using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 轨道 MapParent 的唯一纳管判定。类型开放，避免遗漏第三方子类。
    /// 星区架构 §4/§9：「每个可进入地图都属于一个 SpaceObject」——空画布锚对象
    /// （DeltaV_SpaceHostParent 与原版 Space def 画布站）与被停靠船占用的宿主图
    /// 一并纳管；未物化的空画布对象与其图同时按需物化，无未物化投影问题。
    /// </summary>
    public static class SpaceMapGovernance
    {
        public static bool IsGoverned(MapParent parent, UniverseManager universe = null)
        {
            if (parent == null || parent.Destroyed || parent is ShipPocketParent_DeltaV
                || parent is UniverseHostParent_DeltaV)
                return false;
            if (!parent.Tile.Valid || !parent.Tile.LayerDef.isSpace) return false;
            universe = universe ?? Current.Game?.GetComponent<UniverseManager>();
            // 已有容器承载的图（口袋容器图）不作为独立 POI 纳管；多船宿主图由宿主
            // 容器承载——多船同泊、他船再降落都依赖宿主保持 SpaceObject 身份。
            // 例外：该图由本 parent 自己的 NativeParent 镜像承载 = 正常物化态，
            // **自引用不算占用**（2026-09-25 修复：旧判定命中镜像自身 → IsGoverned
            // 恒 false → Synchronize 每 120 tick 注销/重注册振荡，objectId churn、
            // FrameOf 空窗、原版移除拦截失效）。
            if (universe != null && parent.Map != null)
            {
                SpaceObject occupant = universe.SpaceObjectOnMap(parent.Map);
                if (occupant != null
                    && !(occupant is PoiState mirror && mirror.kind == PoiKind.NativeParent
                         && mirror.nativeParent == parent))
                {
                    return false;
                }
            }
            return true;
        }

        public static bool IsGoverned(Map map, UniverseManager universe = null)
        {
            return map != null && !map.Disposed && IsGoverned(map.Parent, universe);
        }

        public static bool HasPlayerPawn(Map map)
        {
            if (map == null || map.Disposed) return false;
            Faction player = Faction.OfPlayerSilentFail;
            if (player == null) return false;
            if (map.mapPawns.AllPawnsSpawned.Any(p => p.Faction == player || p.HostFaction == player))
                return true;
            var nested = new List<Thing>();
            ThingOwnerUtility.GetAllThingsRecursively(map, nested);
            return nested.OfType<Pawn>().Any(p => p.Faction == player || p.HostFaction == player);
        }

        public static bool MustStayActive(Map map, UniverseManager universe)
        {
            if (map == null || map.Disposed) return false;
            if (map == Find.CurrentMap || map.IsPlayerHome || HasPlayerPawn(map)
                || universe?.ShipTransitions.IsAwaitingDockingCleanup(map) == true
                || universe?.Generation?.IsGenerating(map) == true)
            {
                return true;
            }
            SpaceObject container = universe?.FrameOf(map);
            return container?.HasTag(SpaceObjectTag.Ship) == true
                && container.HasFaction(Faction.OfPlayerSilentFail);
        }

        public static bool HasPlayerBuildings(Map map)
        {
            if (map == null || map.Disposed) return false;
            return map.listerBuildings.allBuildingsColonist.Any(building => !building.Destroyed);
        }

        /// <summary>冷退役提交前的纯查询；普通散落物品不构成阻塞。</summary>
        public static string ColdRetirementBlockReason(Map map, UniverseManager universe)
        {
            if (map == null || map.Disposed) return "map unavailable";
            if (map == Find.CurrentMap) return "current map";
            if (HasPlayerPawn(map)) return "player pawn or prisoner";
            if (map.IsPlayerHome) return "player home";
            if (HasPlayerBuildings(map)) return "player building";
            SpaceObject container = universe?.FrameOf(map);
            if (container?.HasTag(SpaceObjectTag.Ship) == true
                && container.HasFaction(Faction.OfPlayerSilentFail)) return "player ship";
            if (container != null && universe?.Missions.Protects(container) == true) return "active mission";
            if (universe?.Generation?.IsGenerating(map) == true) return "map generation";
            if (universe?.ShipTransitions.IsAwaitingStrategicTakeoff(map) == true
                || universe?.ShipTransitions.IsAwaitingDockingCleanup(map) == true
                || universe?.Approach.ApproachShipMap == map
                || universe?.Approach.ActivePlan?.targetMap == map
                || universe?.SoftDock.UsesMap(map) == true) return "map transfer or docking";
            if (!ArchivedMapStore.CanUnloadLosslessly(map)) return "external map reference";
            return null;
        }

        public static IntVec3 GenerationSize(MapParent parent)
        {
            if (parent is Site site) return site.PreferredMapSize;
            return parent.def.overrideMapSize ?? Find.World.info.initialMapSize;
        }

        /// <summary>
        /// 旧空画布锚对象：原版通用降落目的地（Space def）或 Delta-V 历史遗留的
        /// DeltaV_SpaceHostParent 站。1.7 空轨道层实际走 AddNewHome，另由
        /// <see cref="IsLandingCreatedSettlement"/> 识别本次降落新建的 SpaceSettlement。
        /// </summary>
        public static bool IsBlankCanvasAnchor(MapParent parent)
        {
            if (parent == null) return false;
            return parent is SpaceHostParent_DeltaV || parent.def?.defName == "Space";
        }

        /// <summary>
        /// 1.7 GravshipUtility.ArriveNewMap 在空轨道层调用 SettleUtility.AddNewHome，生成
        /// 玩家 SpaceSettlement，而非 SpaceMapParent。只有 WorldObject 与 Map 在同一游戏
        /// tick 新建，才能证明它是本次降落产生的临时 Home；较早存在的真实 Settlement
        /// 即使后来由逆重飞船首次生成地图，也不能按临时宿主回收。
        /// </summary>
        public static bool IsLandingCreatedSettlement(Map map, MapParent parent)
        {
            return map != null && parent is Settlement && map.wasSpawnedViaGravShipLanding
                && parent.creationGameTicks == map.generationTick;
        }

        public static bool IsBlankLandingCanvas(Map map) => ShipStructureIndex.Read(map)?.IsBlankCanvas ?? true;

        /// <summary>
        /// 原版向空轨道 tile 战略降落时创建的 Space/SpaceSettlement worldtile 只是临时作业宿主。
        /// 最后一艘船脱离后，仅在结构和散落内容均已交接完毕时允许回收；真实站点、
        /// 小行星、任务图以及仍承载其他船的 worldtile 均不进入该路径。
        /// </summary>
        public static bool IsEmptyLandingWorksite(Map map, UniverseManager universe)
        {
            if (map == null || map.Disposed || !map.Tile.Valid || universe == null
                || universe.FrameOf(map) is not PoiState poi
                || poi.kind != PoiKind.NativeParent || poi.map != map
                || poi.nativeParent is not MapParent parent || parent.Map != map
                || !poi.IsWorksite || !map.wasSpawnedViaGravShipLanding
                || !(IsBlankCanvasAnchor(parent) || IsLandingCreatedSettlement(map, parent)))
            {
                return false;
            }
            return !poi.Ships.Any(ship => ship.IsValid)
                && IsBlankLandingCanvas(map) && !ShipRegistry.HasLooseContents(map);
        }
    }

    /// <summary>把所有轨道 POI WorldObject 投影进持久化 Universe State。</summary>
    public static class OrbitalPoiRegistry
    {
        /// <summary>地图首次可用时立即建立其真实 SpaceObject；周期同步只负责维护。</summary>
        public static PoiState EnsureForMap(UniverseManager universe, Map map)
        {
            if (universe == null || !SpaceMapGovernance.IsGoverned(map, universe)) return null;
            return EnsureForParent(universe, map.Parent);
        }

        private static PoiState EnsureForParent(UniverseManager universe, MapParent parent)
        {
            if (universe == null || !SpaceMapGovernance.IsGoverned(parent, universe)) return null;
            PoiState poi = universe.objects.OfType<PoiState>()
                .FirstOrDefault(p => p.kind == PoiKind.NativeParent && p.nativeParent == parent);
            if (poi == null)
            {
                poi = new PoiState(universe.AllocateOwnerId(), parent);
                universe.RegisterObject(poi);
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"registered orbital POI {parent.LabelCap} ({parent.GetType().FullName}, id={parent.ID})");
            }
            UpdateFromParent(poi, parent);
            return poi;
        }

        private static void UpdateFromParent(PoiState poi, MapParent parent)
        {
            poi.envLabel = parent.LabelCap;
            poi.map = parent.Map;
            poi.faction = parent.Faction;
            poi.pose.position = OrbitSphere.TileCenterDirection(parent.Tile);
            poi.pose.angle = 0f;
        }

        public static void NotifyDestroyed(MapParent parent)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            universe?.Generation?.CancelQueuedForParent(parent);
            if (universe == null || parent == null) return;
            foreach (PoiState poi in universe.objects.OfType<PoiState>()
                .Where(p => p.kind == PoiKind.NativeParent && p.nativeParent == parent).ToList())
            {
                universe.MapStates.Forget(poi.map);
                universe.UnregisterObject(poi);
            }
        }

        public static void Synchronize(UniverseManager universe)
        {
            if (universe == null || Find.World?.worldObjects == null) return;
            List<MapParent> parents = Find.World.worldObjects.AllWorldObjects
                .OfType<MapParent>().Where(p => SpaceMapGovernance.IsGoverned(p, universe)).ToList();
            foreach (MapParent parent in parents)
            {
                EnsureForParent(universe, parent);
            }

            HashSet<MapParent> live = new HashSet<MapParent>(parents);
            foreach (PoiState stale in universe.objects.OfType<PoiState>()
                .Where(p => p.kind == PoiKind.NativeParent
                    && (p.nativeParent == null || p.nativeParent.Destroyed || !live.Contains(p.nativeParent))).ToList())
            {
                universe.MapStates.Forget(stale.map);
                universe.UnregisterObject(stale);
            }
        }
    }
}
