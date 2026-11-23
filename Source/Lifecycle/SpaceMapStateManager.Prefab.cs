using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimExodus.Maps;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed partial class SpaceMapStateManager
    {
        private bool prefabReplacementPending;

        /// <summary>
        /// One transaction for every external SpaceObject marker in a placed prefab. Marker comps
        /// only describe plans; this manager owns registration, rollback and the host's one-shot fact.
        /// Pawn and other local markers still expand on every cold rebuild.
        /// </summary>
        internal void ExpandPrefabMarkers(PrefabSpawnContext context, List<Thing> things)
        {
            var markers = things.Select(thing => thing.TryGetComp<CompPrefabMarker>())
                .Where(marker => marker != null).ToList();
            var external = markers.Where(marker => marker.IsExternalSpaceObjectMarker).ToList();
            SpaceObject host = context?.Universe?.SpaceObjectOnMap(context.Map);
            var registered = new List<PoiState>();
            try
            {
                if (external.Count > 0 && host == null)
                    throw new InvalidDataException($"{UniverseManager.MapTag(context?.Map)} has external "
                        + "SpaceObject markers but no registered host.");

                if (host?.externalPrefabGenerationCompleted != true)
                {
                    var plans = new List<PrefabObjectSpawnPlan>();
                    foreach (CompPrefabMarker marker in external)
                    {
                        if (marker is CompPrefabObjectSpawner exact)
                            plans.Add(exact.Plan(context));
                        else if (marker is CompPrefabClusterSpawner cluster && host?.clusterChild != true)
                        {
                            List<PrefabObjectSpawnPlan> clusterPlans = cluster.Plans();
                            if (clusterPlans.Count == 0)
                                throw new InvalidDataException($"Prefab cluster marker at "
                                    + $"{cluster.parent.Position} generated no objects.");
                            plans.AddRange(clusterPlans);
                        }
                    }

                    var documents = new Dictionary<string, MapPrefabDocument>(StringComparer.OrdinalIgnoreCase);
                    foreach (PrefabObjectSpawnPlan plan in plans)
                    {
                        if (documents.ContainsKey(plan.prefabPath)) continue;
                        if (!PrefabObjectMarkerUtility.TryLoadDocument(context, plan.prefabPath,
                            out SpaceObject checkedHost, out MapPrefabDocument document, out string error))
                            throw new InvalidDataException($"External prefab '{plan.prefabPath}' failed validation: {error}");
                        host = checkedHost;
                        documents.Add(plan.prefabPath, document);
                    }
                    foreach (PrefabObjectSpawnPlan plan in plans)
                    {
                        if (!PrefabObjectMarkerUtility.TryRegister(context, host, documents[plan.prefabPath],
                            plan, out PoiState child, out string error))
                            throw new InvalidDataException($"External prefab '{plan.prefabPath}' failed registration: {error}");
                        registered.Add(child);
                    }
                }

                foreach (CompPrefabMarker marker in markers.Where(marker => !marker.IsExternalSpaceObjectMarker))
                    marker.Expand(context);

                foreach (CompPrefabMarker marker in external)
                    PrefabObjectMarkerUtility.DestroyMarker(marker.parent);
                if (host != null) host.externalPrefabGenerationCompleted = true;
            }
            catch
            {
                foreach (PoiState child in registered)
                    if (context.Universe.ObjectById(child.objectId) == child)
                        context.Universe.UnregisterObject(child);
                throw;
            }
        }

        internal bool CanReplaceFocusedMap(SpaceObject source, out string reason)
        {
            reason = null;
            if (source?.map == null || source.map.Disposed || source.map != Find.CurrentMap
                || universe.ObjectById(source.objectId) != source || source.IsTransient
                || IsUniverseHost(source.map))
                reason = "DeltaV_PrefabReplaceRequiresMap".Translate();
            else if (prefabReplacementPending || universe.Generation.IsAnyGenerating
                || !universe.CanApplyScaleChange())
                reason = "DeltaV_PrefabReplaceBusy".Translate();
            return reason == null;
        }

        internal void ReplaceFocusedMapFromPrefab(SpaceObject source, string path, Faction faction)
        {
            if (!CanReplaceFocusedMap(source, out string reason))
            {
                Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                return;
            }
            MapPrefabDocument prefab;
            try
            {
                prefab = MapPrefab.LoadRelative(path);
            }
            catch (Exception ex)
            {
                ReportPrefabReplacementFailure(path, ex);
                return;
            }
            prefabReplacementPending = true;
            Find.TickManager.Pause();
            LongEventHandler.QueueLongEvent(() => GeneratePrefabReplacement(source, path, prefab, faction),
                "GeneratingMap", false, null);
        }

        private void GeneratePrefabReplacement(SpaceObject source, string path,
            MapPrefabDocument prefab, Faction faction)
        {
            Map map = null;
            SpaceObject replacement = null;
            bool ready = false;
            try
            {
                if (source.map == null || source.map.Disposed || source.map != Find.CurrentMap
                    || universe.ObjectById(source.objectId) != source)
                    throw new InvalidOperationException("The source map changed before prefab replacement.");
                IntVec3 size = ShipPocketMapFactory.CompactSize(MapPrefab.ContentBounds(prefab, Rot4.North).Size);
                map = ShipPocketMapFactory.Generate(size, universe);
                if (map == null) throw new InvalidOperationException("Replacement map generation returned null.");
                replacement = new MapSpaceObject(source.objectId, map, source.pose);
                source.CopyFactsTo(replacement);
                replacement.faction = faction;
                replacement.ambientLabel = prefab.name;
                replacement.baseTag = SpaceObjectTag.Container
                    | (SectorContentService.PrefabHasNaturalBody(prefab) ? SpaceObjectTag.GravityRegion : 0);
                replacement.sourcePrefabPath = path.Replace('\\', '/');
                replacement.sourcePrefabCategoryPath = SectorGenerationTable.CategoryPathForPrefab(
                    DeltaVMod.CurrentSettings?.sectorGenerationRoots ?? SectorGenerationTable.Defaults(), path);
                replacement.sourcePrefabSeed = Gen.HashCombineInt(source.objectId,
                    path.Aggregate(23, (hash, value) => unchecked(hash * 31 + value)));
                replacement.externalPrefabGenerationCompleted = false;
                // ClaimOnSpawn must resolve the new container before any consoles spawn.
                universe.ReplaceRepresentation(source, replacement);
                int ownerId = universe.AllocateOwnerId();
                universe.NoteStructureName(ownerId, prefab.name);
                MapPrefab.SpawnSpaceObject(prefab, map, MapPrefab.CenteredPosition(prefab, map, Rot4.North),
                    faction, ownerId, Rot4.North, universe, blankSpace: true);
                ready = true;
            }
            catch (Exception ex)
            {
                if (replacement != null && universe.ObjectById(source.objectId) == replacement)
                    universe.ReplaceRepresentation(replacement, source);
                ReportPrefabReplacementFailure(path, ex);
            }
            finally
            {
                // FinalizeInit queues mesh/power initialization after the long event.
                // Both successful handoff and failed-map disposal must wait for it.
                universe.QueueFrameAction(() => FinishPrefabReplacement(source, replacement, map, path, ready));
            }
        }

        private void FinishPrefabReplacement(SpaceObject source, SpaceObject replacement,
            Map map, string path, bool ready)
        {
            bool handedOff = false;
            try
            {
                if (!ready) return;
                Map oldMap = source.map;
                if (oldMap == null || oldMap.Disposed || oldMap != Find.CurrentMap
                    || universe.ObjectById(source.objectId) != replacement)
                    throw new InvalidOperationException("The source map changed before prefab handoff.");

                Vector3 camera = Find.CameraDriver.CurrentViewRect.CenterVector3;
                Vector3 cameraUniverse = SpaceTransform.MapPointToUniverse(source, oldMap,
                    new Vector2(camera.x, camera.z));
                replacement.pose = source.pose;
                Vector2 cameraPoint = SpaceTransform.UniverseToMapPoint(replacement, map, cameraUniverse);
                universe.SetFocusedWithCamera(replacement, cameraPoint, Find.CameraDriver.RootSize);
                handedOff = true;

                MapParent oldParent = oldMap.Parent;
                if (oldParent is PocketMapParent) DestroyPocketMap(oldMap);
                else
                {
                    DeinitMap(oldMap, true);
                    // A native orbital parent must not recreate the map we just replaced.
                    if (oldParent != null && !oldParent.Destroyed) oldParent.Destroy();
                }
                Messages.Message("DeltaV_PrefabReplaceComplete".Translate(path), MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception ex)
            {
                ReportPrefabReplacementFailure(path, ex);
            }
            finally
            {
                prefabReplacementPending = false;
                if (!handedOff)
                {
                    if (replacement != null && universe.ObjectById(source.objectId) == replacement)
                        universe.ReplaceRepresentation(replacement, source);
                    DestroyPocketMap(map);
                }
            }
        }

        private static void ReportPrefabReplacementFailure(string path, Exception error)
        {
            Log.Error($"[DeltaV] Prefab map replacement '{path}' failed: {error}");
            Messages.Message("DeltaV_PrefabReplaceFailed".Translate(), MessageTypeDefOf.RejectInput, false);
        }
    }
}
