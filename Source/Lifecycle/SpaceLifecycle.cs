using System;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Delta-V 距离策略；地图生成、休眠和父对象语义分别委托通用服务。</summary>
    public static class SpaceLifecycle
    {
        public const int PoiMapSize = 90;

        public static bool IsDormant(Map map) => MapDormancyService.IsDormant(map);
        public static bool IsFrozen(Map map) => IsDormant(map) || IsUniverseHost(map)
            || IncrementalMapGenerationService.IsGenerating(map);
        public static bool IsUniverseHost(Map map) => map?.Parent is UniverseHostParent_DeltaV;
        public static void Wake(Map map, string reason) => MapDormancyService.Wake(map, reason);

        public static void Tick(UniverseManager universe)
        {
            OrbitalPoiRegistry.Synchronize(universe);
            float activeRadius = DeltaVMod.Settings.poiActiveRadius;
            float deleteRadius = Mathf.Max(DeltaVMod.Settings.poiDeleteRadius, activeRadius + 1f);
            foreach (PoiState poi in universe.objects.OfType<PoiState>().ToList())
            {
                SynchronizeNative(poi);
                float distance = MinPlayerDistance(universe, poi);
                if (float.IsPositiveInfinity(distance)) continue;
                string key = GenerationKey(poi);
                IncrementalMapGenerationService.UpdatePriority(key, distance);
                if (distance > activeRadius) IncrementalMapGenerationService.CancelQueued(key);
                if (poi.map != null && SpaceMapGovernance.MustStayActive(poi.map, universe))
                {
                    MapDormancyService.Wake(poi.map, "protected map");
                    continue;
                }
                if (IsPendingGeneration(poi)) continue;
                if (distance <= activeRadius) EnsureActive(universe, poi, distance);
                else if (distance <= deleteRadius) EnsureDormant(poi);
                else TryPack(universe, poi);
            }
        }

        public static float MinPlayerDistance(UniverseManager universe, PoiState poi)
        {
            float best = float.PositiveInfinity;
            foreach (ShipState ship in universe.Ships)
                if (ship.isPlayerShip)
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

        private static void EnsureActive(UniverseManager universe, PoiState poi, float priority = 0f)
        {
            if (poi.map != null)
            {
                MapDormancyService.Wake(poi.map, "inside POI load radius");
                return;
            }
            if (poi.kind == PoiKind.NativeParent) QueueNativeGeneration(poi, priority);
            else QueueSnapshotGeneration(universe, poi, priority);
        }

        private static void QueueNativeGeneration(PoiState poi, float priority)
        {
            MapParent parent = poi.nativeParent;
            if (parent == null || parent.Destroyed || parent.HasMap) return;
            IncrementalMapGenerationService.Enqueue(new IncrementalMapGenerationRequest
            {
                key = GenerationKey(poi), priority = priority, parent = parent,
                size = SpaceMapGovernance.GenerationSize(parent), generator = parent.MapGeneratorDef,
                extraGenSteps = parent.ExtraGenStepDefs, isPocketMap = false,
                onComplete = map =>
                {
                    poi.map = map;
                    MapDormancyService.Wake(map, "generation complete");
                    Messages.Message("DeltaV_PoiActivated".Translate(poi.label), MessageTypeDefOf.NeutralEvent, false);
                },
                onFailure = ex => ReportGenerationFailure(poi.label, ex)
            });
        }

        private static void QueueSnapshotGeneration(UniverseManager universe, PoiState poi, float priority)
        {
            var parent = (ShipPocketParent_DeltaV)WorldObjectMaker.MakeWorldObject(DeltaVDefOf.DeltaV_ShipMapParent);
            parent.sourceMap = universe.GetOrCreateHostMap();
            parent.mapGenerator = DeltaVDefOf.DeltaV_ShipPocket;
            IncrementalMapGenerationService.Enqueue(new IncrementalMapGenerationRequest
            {
                key = GenerationKey(poi), priority = priority, parent = parent,
                size = new IntVec3(PoiMapSize, 1, PoiMapSize), generator = DeltaVDefOf.DeltaV_ShipPocket,
                isPocketMap = true,
                onComplete = map =>
                {
                    Find.World.pocketMaps.Add(parent);
                    if (poi.snapshot != null)
                    {
                        poi.snapshot.ApplyTo(map);
                        poi.snapshot = null;
                        Messages.Message("DeltaV_PoiRestored".Translate(poi.label), MessageTypeDefOf.NeutralEvent, false);
                    }
                    else
                    {
                        PoiTemplate.Stamp(map, poi.seed);
                        Messages.Message("DeltaV_PoiActivated".Translate(poi.label), MessageTypeDefOf.NeutralEvent, false);
                    }
                    poi.map = map;
                },
                onFailure = ex => ReportGenerationFailure(poi.label, ex)
            });
        }

        private static void EnsureDormant(PoiState poi)
        {
            if (poi.map != null) MapDormancyService.Sleep(poi.map, "outside POI load radius");
        }

        private static void TryPack(UniverseManager universe, PoiState poi)
        {
            Map map = poi.map;
            if (map == null || SpaceMapGovernance.MustStayActive(map, universe)) return;
            if (poi.kind == PoiKind.NativeParent)
            {
                MapParent parent = poi.nativeParent;
                if (parent == null || parent.Destroyed) return;
                if (!parent.ShouldRemoveMapNow(out bool alsoRemoveWorldObject))
                {
                    MapDormancyService.Sleep(map, "native removal blocked");
                    return;
                }
                Forget(map);
                poi.map = null;
                Current.Game.DeinitAndRemoveMap(map, true);
                if (!parent.Destroyed && (alsoRemoveWorldObject || parent.forceRemoveWorldObjectWhenMapRemoved))
                    parent.Destroy();
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{poi.label}: removed at POI delete radius (worldObject={parent.Destroyed})");
                return;
            }

            if (map.mapPawns.AllPawnsSpawned.Any())
            {
                MapDormancyService.Sleep(map, "snapshot removal blocked by pawn");
                return;
            }
            poi.snapshot = PoiSnapshot.Capture(map);
            poi.map = null;
            Forget(map);
            PocketMapUtility.DestroyPocketMap(map);
            Messages.Message("DeltaV_PoiPacked".Translate(poi.label), MessageTypeDefOf.NeutralEvent, false);
        }

        public static void Forget(Map map)
        {
            MapDormancyService.Forget(map);
            MapOverlayRenderer.Release(map);
        }

        public static void ForceActivate(UniverseManager universe, PoiState poi) => EnsureActive(universe, poi);
        public static void ForceDormant(PoiState poi)
        {
            if (poi.map != null && poi.map != Find.CurrentMap) MapDormancyService.Sleep(poi.map, "dev action");
        }
        public static void ForcePack(PoiState poi)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe != null) TryPack(universe, poi);
        }
        private static bool IsPendingGeneration(PoiState poi) => poi != null
            && IncrementalMapGenerationService.IsQueued(GenerationKey(poi));
        private static string GenerationKey(PoiState poi) => poi.kind == PoiKind.NativeParent
            ? $"orbital-parent:{poi.nativeParent?.ID ?? poi.objectId}" : $"snapshot-poi:{poi.objectId}";
        private static void ReportGenerationFailure(string label, Exception error)
        {
            if (error is OperationCanceledException)
                DeltaVLog.Message(DeltaVLogModule.Lifecycle, $"map generation canceled: {label}");
            else
                Log.Error($"[DeltaV] Failed to generate POI {label}: {error}");
        }
        public static void Reset() => MapDormancyService.Reset();
    }
}
