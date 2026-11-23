using System;
using System.Collections.Generic;
using System.Linq;
using RimExodus.Maps;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    internal enum MapTransitionKind : byte
    {
        StructureReview,
        Materialize,
        Wake,
        Sleep,
        Archive,
        Restore,
        ColdRetire,
        PermanentRetire
    }

    public sealed partial class SpaceMapStateManager
    {
        private sealed class PendingMapTransition
        {
            public int objectId;
            public int representationVersion;
            public MapTransitionKind kind;
            public int priority;
            public Map map;
        }

        [Unsaved] private Dictionary<int, PendingMapTransition> pendingTransitions;

        private Dictionary<int, PendingMapTransition> PendingTransitions
            => pendingTransitions ??= new Dictionary<int, PendingMapTransition>();

        private static int TransitionPriority(MapTransitionKind kind)
        {
            return kind switch
            {
                MapTransitionKind.Restore => 500,
                MapTransitionKind.Wake => 450,
                MapTransitionKind.PermanentRetire => 400,
                MapTransitionKind.StructureReview => 350,
                MapTransitionKind.Archive => 300,
                MapTransitionKind.Materialize => 250,
                MapTransitionKind.Sleep => 200,
                MapTransitionKind.ColdRetire => 100,
                _ => 0
            };
        }

        private bool TryBeginTransition(SpaceObject obj, MapTransitionKind kind, Map map = null)
        {
            if (obj == null || universe.ObjectById(obj.objectId) != obj) return false;
            int priority = TransitionPriority(kind);
            if (PendingTransitions.TryGetValue(obj.objectId, out PendingMapTransition pending))
            {
                if (pending.kind == kind && pending.representationVersion == obj.RepresentationVersion)
                    return false;
                if (pending.priority >= priority) return false;
            }
            PendingTransitions[obj.objectId] = new PendingMapTransition
            {
                objectId = obj.objectId,
                representationVersion = obj.RepresentationVersion,
                kind = kind,
                priority = priority,
                map = map
            };
            return true;
        }

        private bool TransitionStillCurrent(int objectId, MapTransitionKind kind,
            int representationVersion, Map map = null)
        {
            return PendingTransitions.TryGetValue(objectId, out PendingMapTransition pending)
                && pending.kind == kind && pending.representationVersion == representationVersion
                && (map == null || pending.map == map);
        }

        private bool IsTransitionPending(int objectId, MapTransitionKind kind)
            => PendingTransitions.TryGetValue(objectId, out PendingMapTransition pending)
                && pending.kind == kind;

        private void CompleteTransition(int objectId, MapTransitionKind kind, int representationVersion)
        {
            if (TransitionStillCurrent(objectId, kind, representationVersion))
                PendingTransitions.Remove(objectId);
        }

        private void CancelTransition(int objectId, MapTransitionKind kind)
        {
            if (IsTransitionPending(objectId, kind)) PendingTransitions.Remove(objectId);
        }

        internal string PendingTransitionName(int objectId)
            => PendingTransitions.TryGetValue(objectId, out PendingMapTransition pending)
                ? pending.kind.ToString() : null;

        internal bool RequestColdRetirement(SpaceObject obj, bool playerRequested = false)
        {
            if (obj == null || universe.ObjectById(obj.objectId) != obj) return false;
            bool exactArchive = obj is ArchivedSpaceObject archive && !archive.mapData.NullOrEmpty();
            if (!exactArchive && (obj.map == null || obj.map.Disposed)) return false;
            MapRetentionRecord record = RecordFor(obj, create: false);
            if (record == null || record.manualKeepExact || obj.sourcePrefabPath.NullOrEmpty()) return false;
            if (!playerRequested && !ExactRetentionExpired(obj)) return false;
            if (universe.Missions.Protects(obj)) return false;
            if (!playerRequested && MinPlayerDistance(obj) <= DeleteRadius) return false;
            int version = obj.RepresentationVersion;
            if (!TryBeginTransition(obj, MapTransitionKind.ColdRetire, obj.map)) return false;
            universe.QueueFrameAction(() => ExecuteColdRetirement(obj.objectId, version, playerRequested));
            return true;
        }

        private void ExecuteColdRetirement(int objectId, int version, bool playerRequested)
        {
            Map restored = null;
            try
            {
                if (!TransitionStillCurrent(objectId, MapTransitionKind.ColdRetire, version)
                    || universe.ObjectById(objectId) is not SpaceObject current
                    || current.RepresentationVersion != version) return;
                ArchivedSpaceObject archive = current as ArchivedSpaceObject;
                if (archive != null && archive.mapData.NullOrEmpty()) return;
                if (archive == null && (current.map == null || current.map.Disposed)) return;
                MapRetentionRecord record = RecordFor(current, create: false);
                if (record == null || record.manualKeepExact || current.sourcePrefabPath.NullOrEmpty()
                    || !playerRequested && !ExactRetentionExpired(current)
                    || universe.Missions.Protects(current)
                    || !playerRequested && MinPlayerDistance(current) <= DeleteRadius) return;

                if (!TryResolvePrefabSource(current, out string resolvedPrefabPath,
                    out MapPrefabDocument _, out string prefabError))
                {
                    Log.Error($"[DeltaV] Cold retirement kept exact archive #{objectId}: "
                        + $"prefab validation failed: {prefabError}");
                    return;
                }

                Map retiringMap = current.map;
                if (archive != null && !universe.MapPool.TryMakeRoom(score:
                    CapacityScore(archive.mapArea, MinPlayerDistance(archive)))) return;
                if (archive != null && !ArchivedMapStore.TryRestore(archive.mapData, archive.mapId,
                    archive.parent, out restored))
                {
                    Log.Warning($"[DeltaV] Cold retirement kept exact archive #{objectId}: restore failed.");
                    return;
                }
                if (restored != null) retiringMap = restored;
                string block = SpaceMapGovernance.ColdRetirementBlockReason(retiringMap, universe);
                if (block != null)
                {
                    Log.Message($"[DeltaV] Cold retirement kept object #{objectId}: blocked by {block}.");
                    return;
                }
                if (!ReleaseUnspawnedPawnsForRemoval(retiringMap)) return;
                PocketMapParent parent = retiringMap.Parent as PocketMapParent;
                DeinitMap(retiringMap, notifyPlayer: false);
                restored = null;
                if (parent != null)
                {
                    Find.World.pocketMaps.Remove(parent);
                    if (!parent.Destroyed) parent.Destroy();
                }

                var marker = new PoiState(current.objectId, current.label, current.pose.position,
                    current.pose.angle, current.sourcePrefabSeed);
                current.CopyFactsTo(marker);
                marker.sourcePrefabPath = resolvedPrefabPath;
                marker.externalPrefabGenerationCompleted = true;
                marker.kind = PoiKind.SnapshotPocket;
                marker.baseTag = (marker.baseTag & ~(SpaceObjectTag.Container | SpaceObjectTag.Ship))
                    | SpaceObjectTag.Marker;
                marker.envLabel = current.ambientLabel.NullOrEmpty() ? current.label : current.ambientLabel;
                marker.map = null;
                marker.snapshot = null;
                universe.ReplaceRepresentation(current, marker);
                record.compressedBytes = 0;
                if (archive != null)
                {
                    archive.mapData = null;
                    archive.dynamics = null;
                }
                Log.Message($"[DeltaV] Exact archive #{objectId} retired to cold prefab marker '{resolvedPrefabPath}'.");
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] Cold retirement failed for object #{objectId}; exact archive retained: {ex}");
            }
            finally
            {
                if (restored != null && !restored.Disposed)
                    PreserveRestoredArchiveOnRollback(objectId, restored, "cold retirement");
                CompleteTransition(objectId, MapTransitionKind.ColdRetire, version);
            }
        }

        /// <summary>
        /// A retirement probe restores the exact snapshot into the live game. If that restored map
        /// acquired a reference which makes unloading lossy, the safe rollback is to keep the real
        /// map and consume the snapshot, not to destroy state which the blocker explicitly protects.
        /// </summary>
        private void PreserveRestoredArchiveOnRollback(int objectId, Map restored, string operation)
        {
            try
            {
                if (ArchivedMapStore.CanUnloadLosslessly(restored))
                {
                    DeinitMap(restored, notifyPlayer: false);
                    return;
                }
                if (universe.ObjectById(objectId) is not ArchivedSpaceObject archive)
                {
                    Log.Error($"[DeltaV] {operation} rollback kept {UniverseManager.MapTag(restored)}, "
                        + $"but archived object #{objectId} is no longer current.");
                    return;
                }

                MapSpaceObject current = archive.RestoreRepresentation(restored);
                universe.ReplaceRepresentation(archive, current);
                archive.mapData = null;
                archive.dynamics = null;
                ActivateRestoredContainer(current);
                Log.Warning($"[DeltaV] {operation} rollback promoted object #{objectId} to a live map "
                    + "because unloading the restored map would lose external state.");
            }
            catch (Exception cleanup)
            {
                Log.Error($"[DeltaV] {operation} rollback failed for object #{objectId}; "
                    + $"the restored map was left alive: {cleanup}");
            }
        }

        private static bool ReleaseUnspawnedPawnsForRemoval(Map map)
        {
            foreach (Pawn pawn in map.mapPawns.AllPawnsUnspawned.ToList())
            {
                if (pawn.Spawned) continue;
                if (pawn.holdingOwner == null || !pawn.holdingOwner.TryDrop(pawn,
                    pawn.PositionHeld, map, ThingPlaceMode.Near, out Thing released,
                    playDropSound: false) || released != pawn || !pawn.Spawned)
                {
                    Log.Error($"[DeltaV] Could not release crew {pawn.GetUniqueLoadID()} before map retirement.");
                    return false;
                }
            }
            return true;
        }

        internal bool RequestPermanentRetirement(SpaceObject obj, string reason,
            bool playerRequested = false)
        {
            if (obj == null || universe.ObjectById(obj.objectId) != obj || obj.IsTransient) return false;
            if (universe.Missions.Protects(obj)) return false;
            int version = obj.RepresentationVersion;
            if (!TryBeginTransition(obj, MapTransitionKind.PermanentRetire, obj.map)) return false;
            universe.QueueFrameAction(() => ExecutePermanentRetirement(
                obj.objectId, version, reason ?? "retired", playerRequested));
            return true;
        }

        private void ExecutePermanentRetirement(int objectId, int version, string reason,
            bool playerRequested)
        {
            Map restored = null;
            try
            {
                if (!TransitionStillCurrent(objectId, MapTransitionKind.PermanentRetire, version)
                    || universe.ObjectById(objectId) is not SpaceObject obj
                    || obj.RepresentationVersion != version || universe.Missions.Protects(obj)) return;

                Map map = obj.map;
                PocketMapParent parent = null;
                if (obj is ArchivedSpaceObject archive)
                {
                    if (archive.mapData.NullOrEmpty()
                        || !universe.MapPool.TryMakeRoom(score:
                            CapacityScore(archive.mapArea, MinPlayerDistance(archive)))
                        || !ArchivedMapStore.TryRestore(archive.mapData, archive.mapId,
                            archive.parent, out restored)) return;
                    map = restored;
                    parent = archive.parent;
                }
                else if (map?.Parent is PocketMapParent pocket) parent = pocket;

                if (map != null)
                {
                    string block = SpaceMapGovernance.ColdRetirementBlockReason(map, universe);
                    if (block != null)
                    {
                        if (playerRequested)
                            Messages.Message($"Cannot permanently retire: {block}",
                                MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                    if (!ReleaseUnspawnedPawnsForRemoval(map)) return;
                    DeinitMap(map, notifyPlayer: false);
                    restored = null;
                    if (parent != null)
                    {
                        Find.World.pocketMaps.Remove(parent);
                        if (!parent.Destroyed) parent.Destroy();
                    }
                    obj.map = null;
                }
                CommitRetirement(obj, reason);
                Log.Message($"[DeltaV] Object #{objectId} permanently retired ({reason}).");
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] Permanent retirement failed for object #{objectId}; object retained: {ex}");
            }
            finally
            {
                if (restored != null && !restored.Disposed)
                    PreserveRestoredArchiveOnRollback(objectId, restored, "permanent retirement");
                CompleteTransition(objectId, MapTransitionKind.PermanentRetire, version);
            }
        }
    }
}
