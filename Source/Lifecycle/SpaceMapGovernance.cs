using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    /// <summary>轨道 MapParent 的唯一纳管判定。类型开放，避免遗漏第三方子类。</summary>
    public static class SpaceMapGovernance
    {
        private static WorldObjectDef BlankSpaceDef => DefDatabase<WorldObjectDef>.GetNamedSilentFail("Space");

        public static bool IsGoverned(MapParent parent, UniverseManager universe = null)
        {
            if (parent == null || parent.Destroyed || parent is ShipPocketParent_DeltaV
                || parent is UniverseHostParent_DeltaV || parent is SpaceHostParent_DeltaV)
                return false;
            if (!parent.Tile.Valid || !parent.Tile.LayerDef.isSpace) return false;
            universe = universe ?? Current.Game?.GetComponent<UniverseManager>();
            // 只排除"被某艘船占用的图"：parent 无图（Map == null）时没有可占用的目标，
            // 必须放行——否则与飞行船的 dockedMap == null 形成 null == null 恒真，
            // 所有从未生成过地图的 POI（小行星、定居点等）被永久拒绝纳管并遭
            // stale 清理移除（2026-09-16：orbit 层只剩门中心一例的根因）。
            if (universe != null && parent.Map != null
                && universe.Ships.Any(s => s.map == parent.Map || s.dockedMap == parent.Map))
                return false;
            // 未生成的 Space def 只是原版通用降落目的地；已生成且留下真实内容的图不再算空画布。
            if (parent.def == BlankSpaceDef && IsBlankLandingCanvas(parent.Map)) return false;
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
            return map == Find.CurrentMap || map.IsPlayerHome || HasPlayerPawn(map)
                || IncrementalMapGenerationService.IsGenerating(map)
                || (universe != null && universe.Ships.Any(s => s.isPlayerShip && s.dockedMap == map));
        }

        public static IntVec3 GenerationSize(MapParent parent)
        {
            if (parent is Site site) return site.PreferredMapSize;
            return parent.def.overrideMapSize ?? Find.World.info.initialMapSize;
        }

        private static bool IsBlankLandingCanvas(Map map)
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
        public static void NotifyDestroyed(MapParent parent)
        {
            IncrementalMapGenerationService.CancelQueuedForParent(parent);
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null || parent == null) return;
            foreach (PoiState poi in universe.objects.OfType<PoiState>()
                .Where(p => p.kind == PoiKind.NativeParent && p.nativeParent == parent).ToList())
            {
                MapDormancyService.Forget(poi.map);
                MapOverlayRenderer.Release(poi.map);
                universe.objects.Remove(poi);
            }
        }

        public static void Synchronize(UniverseManager universe)
        {
            if (universe == null || Find.World?.worldObjects == null) return;
            List<MapParent> parents = Find.World.worldObjects.AllWorldObjects
                .OfType<MapParent>().Where(p => SpaceMapGovernance.IsGoverned(p, universe)).ToList();
            foreach (MapParent parent in parents)
            {
                PoiState poi = universe.objects.OfType<PoiState>()
                    .FirstOrDefault(p => p.kind == PoiKind.NativeParent && p.nativeParent == parent);
                if (poi == null)
                {
                    poi = new PoiState(universe.NextObjectId(), parent);
                    universe.objects.Add(poi);
                    DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                        $"registered orbital POI {parent.LabelCap} ({parent.GetType().FullName}, id={parent.ID})");
                }
                poi.label = parent.LabelCap;
                poi.map = parent.Map;
                poi.pose.position = OrbitSphere.TileCenterDirection(parent.Tile);
                poi.pose.angle = 0f;
            }

            HashSet<MapParent> live = new HashSet<MapParent>(parents);
            foreach (PoiState stale in universe.objects.OfType<PoiState>()
                .Where(p => p.kind == PoiKind.NativeParent
                    && (p.nativeParent == null || p.nativeParent.Destroyed || !live.Contains(p.nativeParent))).ToList())
            {
                MapDormancyService.Forget(stale.map);
                MapOverlayRenderer.Release(stale.map);
                universe.objects.Remove(stale);
            }
        }
    }
}
