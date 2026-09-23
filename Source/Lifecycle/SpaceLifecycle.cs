using System;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Delta-V 距离策略；地图生成、休眠和父对象语义分别委托通用服务。</summary>
    public sealed class SpaceLifecycleService
    {
        public const int PoiMapSize = 90;

        private readonly UniverseManager universe;
        private readonly MapDormancyService dormancy = new MapDormancyService();

        public SpaceLifecycleService(UniverseManager universe)
        {
            this.universe = universe;
        }

        public bool IsDormant(Map map) => dormancy.IsDormant(map);
        public bool IsFrozen(Map map) => IsDormant(map) || IsUniverseHost(map)
            || universe.Generation?.IsGenerating(map) == true;
        public static bool IsUniverseHost(Map map) => map?.Parent is UniverseHostParent_DeltaV;
        public void Wake(Map map, string reason) => dormancy.Wake(map, reason);

        public void Tick()
        {
            OrbitalPoiRegistry.Synchronize(universe);
            float activeRadius = DeltaVMod.CurrentSettings.poiActiveRadius;
            float deleteRadius = Mathf.Max(DeltaVMod.CurrentSettings.poiDeleteRadius, activeRadius + 1f);
            foreach (PoiState poi in universe.objects.OfType<PoiState>().ToList())
            {
                SynchronizeNative(poi);
                float distance = MinPlayerDistance(poi);
                if (float.IsPositiveInfinity(distance)) continue;
                string key = GenerationKey(poi);
                universe.Generation.UpdatePriority(key, distance);
                if (distance > activeRadius) universe.Generation.CancelQueued(key);
                if (poi.map != null && SpaceMapGovernance.MustStayActive(poi.map, universe))
                {
                    dormancy.Wake(poi.map, "protected map");
                    continue;
                }
                if (IsPendingGeneration(poi)) continue;
                if (distance <= activeRadius) EnsureActive(poi, distance);
                else if (distance <= deleteRadius) EnsureDormant(poi);
                else TryPack(poi);
            }
        }

        public float MinPlayerDistance(PoiState poi)
        {
            float best = float.PositiveInfinity;
            foreach (ShipState ship in universe.Ships)
                if (ship.faction == Faction.OfPlayer)
                    best = Mathf.Min(best, OrbitSphere.ArcCells(ship.pose.position, poi.pose.position));
            return best;
        }

        private static void SynchronizeNative(PoiState poi)
        {
            if (poi.kind != PoiKind.NativeParent || poi.nativeParent == null) return;
            poi.map = poi.nativeParent.Map;
            if (poi.nativeParent.Tile.Valid)
                poi.pose.position = OrbitSphere.TileCenterDirection(poi.nativeParent.Tile);
        }

        private void EnsureActive(PoiState poi, float priority = 0f)
        {
            if (poi.map != null)
            {
                dormancy.Wake(poi.map, "inside POI load radius");
                return;
            }
            if (poi.kind == PoiKind.NativeParent) QueueNativeGeneration(poi, priority);
            else QueueSnapshotGeneration(poi, priority);
        }

        private void QueueNativeGeneration(PoiState poi, float priority)
        {
            MapParent parent = poi.nativeParent;
            if (parent == null || parent.Destroyed || parent.HasMap) return;
            universe.Generation.Enqueue(new IncrementalMapGenerationRequest
            {
                key = GenerationKey(poi), priority = priority, parent = parent,
                size = SpaceMapGovernance.GenerationSize(parent), generator = parent.MapGeneratorDef,
                extraGenSteps = parent.ExtraGenStepDefs, isPocketMap = false,
                onComplete = map =>
                {
                    poi.map = map;
                    OrbitalPoiRegistry.EnsureForMap(universe, map);
                    dormancy.Wake(map, "generation complete");
                    Messages.Message("DeltaV_PoiActivated".Translate(poi.label), MessageTypeDefOf.NeutralEvent, false);
                },
                onFailure = ex => ReportGenerationFailure(poi.label, ex)
            });
        }

        private void QueueSnapshotGeneration(PoiState poi, float priority)
        {
            var parent = (ShipPocketParent_DeltaV)WorldObjectMaker.MakeWorldObject(DeltaVDefOf.DeltaV_ShipMapParent);
            parent.sourceMap = universe.GetOrCreateHostMap();
            parent.mapGenerator = DeltaVDefOf.DeltaV_ShipPocket;
            universe.Generation.Enqueue(new IncrementalMapGenerationRequest
            {
                key = GenerationKey(poi), priority = priority, parent = parent,
                size = new IntVec3(PoiMapSize, 1, PoiMapSize), generator = DeltaVDefOf.DeltaV_ShipPocket,
                isPocketMap = true,
                onComplete = map =>
                {
                    Find.World.pocketMaps.Add(parent);
                    if (poi.snapshot != null)
                    {
                        poi.snapshot.ApplyTo(map, poi.objectId);
                        poi.snapshot = null;
                        Messages.Message("DeltaV_PoiRestored".Translate(poi.label), MessageTypeDefOf.NeutralEvent, false);
                    }
                    else
                    {
                        PoiTemplate.Stamp(map, poi.seed, poi.objectId);
                        Messages.Message("DeltaV_PoiActivated".Translate(poi.label), MessageTypeDefOf.NeutralEvent, false);
                    }
                    poi.map = map;
                },
                onFailure = ex => ReportGenerationFailure(poi.label, ex)
            });
        }

        private void EnsureDormant(PoiState poi)
        {
            if (poi.map != null) dormancy.Sleep(poi.map, "outside POI load radius");
        }

        private void TryPack(PoiState poi)
        {
            Map map = poi.map;
            if (map == null || SpaceMapGovernance.MustStayActive(map, universe)) return;
            if (poi.kind == PoiKind.NativeParent)
            {
                MapParent parent = poi.nativeParent;
                if (parent == null || parent.Destroyed) return;
                if (!parent.ShouldRemoveMapNow(out bool alsoRemoveWorldObject))
                {
                    dormancy.Sleep(map, "native removal blocked");
                    return;
                }
                poi.map = null;
                DeinitMap(map, true);
                if (!parent.Destroyed && (alsoRemoveWorldObject || parent.forceRemoveWorldObjectWhenMapRemoved))
                    parent.Destroy();
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{poi.label}: removed at POI delete radius (worldObject={parent.Destroyed})");
                return;
            }

            if (map.mapPawns.AllPawnsSpawned.Any())
            {
                dormancy.Sleep(map, "snapshot removal blocked by pawn");
                return;
            }
            poi.snapshot = PoiSnapshot.Capture(map);
            poi.map = null;
            DestroyPocketMap(map);
            Messages.Message("DeltaV_PoiPacked".Translate(poi.label), MessageTypeDefOf.NeutralEvent, false);
        }

        public void Forget(Map map)
        {
            dormancy.Forget(map);
            MapOverlayRenderer.Release(map);
        }

        public void DestroyPocketMap(Map map)
        {
            if (map == null || map.Disposed) return;
            Forget(map);
            PocketMapUtility.DestroyPocketMap(map);
        }

        public void DeinitMap(Map map, bool notifyPlayer)
        {
            if (map == null || map.Disposed) return;
            Forget(map);
            Current.Game.DeinitAndRemoveMap(map, notifyPlayer);
        }

        public void ForceActivate(PoiState poi) => EnsureActive(poi);
        public void ForceDormant(PoiState poi)
        {
            if (poi.map != null && poi.map != Find.CurrentMap) dormancy.Sleep(poi.map, "dev action");
        }
        public void ForcePack(PoiState poi)
        {
            TryPack(poi);
        }

        /// <summary>
        /// 逆重起飞离场善后（星区标准流程第 5 步，2026-09-20）：起飞船所在星区内其余
        /// SpaceObject 地图一律按「距离 = 超删除半径」处理——复用距离生命周期同一条
        /// 收纳/删除路径（TryPack：快照 POI 收快照销毁口袋图、对象保留为 packed 可
        /// 重访；MustStayActive 与滞留 Pawn 守卫照常生效，删不掉的只休眠）。
        /// worldtile 图由锚规则（HandleStrategicTravelDeparture 第 ② 段）另行处理，
        /// 经 excludeMap 排除。旅行期间 MinPlayerDistance 为 +Infinity 且评估整体
        /// 跳过，不能依赖自然距离触发——离场时刻必须显式执行。
        /// </summary>
        public void PackDepartedSectorPois(PlanetTile sector, Map excludeMap)
        {
            if (universe == null || !sector.Valid) return;
            foreach (SpaceObject obj in SectorContentService.ObjectsInSector(universe, sector).ToList())
            {
                if (!(obj is PoiState poi)) continue;
                Map map = poi.map;
                if (map == null || map.Disposed || !Find.Maps.Contains(map)) continue;
                if (map == excludeMap || IsUniverseHost(map)) continue;
                TryPack(poi);
            }
        }
        private bool IsPendingGeneration(PoiState poi) => poi != null
            && universe.Generation?.IsQueued(GenerationKey(poi)) == true;
        private static string GenerationKey(PoiState poi) => poi.kind == PoiKind.NativeParent
            ? $"orbital-parent:{poi.nativeParent?.ID ?? poi.objectId}" : $"snapshot-poi:{poi.objectId}";
        private static void ReportGenerationFailure(string label, Exception error)
        {
            if (error is OperationCanceledException)
                DeltaVLog.Message(DeltaVLogModule.Lifecycle, $"map generation canceled: {label}");
            else
                Log.Error($"[DeltaV] Failed to generate POI {label}: {error}");
        }
        public void Reset() => dormancy.Reset();
    }
}
