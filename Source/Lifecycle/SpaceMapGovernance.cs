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
            if (map?.mapPawns?.AllPawnsSpawned == null) return false;
            Faction player = Faction.OfPlayerSilentFail;
            return player != null && map.mapPawns.AllPawnsSpawned.Any(p => p.Faction == player);
        }

        public static bool MustStayActive(Map map, UniverseManager universe)
        {
            if (map == null || map.Disposed) return false;
            if (map == Find.CurrentMap || map.IsPlayerHome || HasPlayerPawn(map)
                || universe?.Generation?.IsGenerating(map) == true)
            {
                return true;
            }
            // 图上有玩家船（主台派系 = 玩家）即保护（停靠船保持宿主图活跃）。
            if (universe != null)
            {
                foreach (Ship ship in ShipRegistry.ShipsOnMap(map))
                {
                    if (ShipRegistry.FactionOf(ship) == Faction.OfPlayer) return true;
                }
            }
            return false;
        }

        public static IntVec3 GenerationSize(MapParent parent)
        {
            if (parent is Site site) return site.PreferredMapSize;
            return parent.def.overrideMapSize ?? Find.World.info.initialMapSize;
        }

        /// <summary>
        /// 空画布锚对象：原版通用降落目的地（Space def）或 Delta-V 历史遗留的
        /// DeltaV_SpaceHostParent 站。二者都是「以 worldtile 地图为内部的 SpaceObject」，
        /// 离港后若图彻底为空则按 §5 空降落图清理语义移除。
        /// </summary>
        public static bool IsBlankCanvasAnchor(MapParent parent)
        {
            if (parent == null) return false;
            return parent is SpaceHostParent_DeltaV || parent.def.defName == "Space";
        }

        public static bool IsBlankLandingCanvas(Map map)
        {
            if (map == null) return true;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing == null || thing.Destroyed || !thing.Spawned) continue;
                if (thing.def.category == ThingCategory.Filth || thing.def.category == ThingCategory.Gas
                    || thing.def.category == ThingCategory.Mote) continue;
                return false;
            }
            return true;
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
                universe.Lifecycle.Forget(poi.map);
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
                universe.Lifecycle.Forget(stale.map);
                universe.UnregisterObject(stale);
            }
        }
    }
}
