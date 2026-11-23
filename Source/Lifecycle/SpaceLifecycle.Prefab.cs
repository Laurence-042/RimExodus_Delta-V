using System;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed partial class SpaceLifecycleService
    {
        private bool prefabReplacementPending;

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
                replacement = new MapSpaceObject(universe.AllocateOwnerId(), map, source.pose)
                {
                    faction = faction,
                    ambientLabel = prefab.name,
                    baseTag = SpaceObjectTag.Container
                        | (SectorContentService.PrefabHasNaturalBody(prefab) ? SpaceObjectTag.GravityRegion : 0),
                    sourcePrefabPath = path,
                };
                // ClaimOnSpawn must resolve the new container before any consoles spawn.
                universe.RegisterObject(replacement);
                int ownerId = universe.AllocateOwnerId();
                universe.NoteStructureName(ownerId, prefab.name);
                MapPrefab.SpawnSpaceObject(prefab, map, MapPrefab.CenteredPosition(prefab, map, Rot4.North),
                    faction, ownerId, Rot4.North, universe, blankSpace: true);
                ready = true;
            }
            catch (Exception ex)
            {
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
                    || universe.ObjectById(source.objectId) != source)
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
                universe.UnregisterObject(source);
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
                    DestroyPocketMap(map);
                    universe.UnregisterObject(replacement);
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
