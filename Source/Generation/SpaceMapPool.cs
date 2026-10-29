using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>A single rental, not a Map identity. Old callbacks cannot return a later rental.</summary>
    public sealed class SpaceMapLease
    {
        public Map Map { get; }
        public SpaceObjectTag Tag { get; }
        internal bool idle;
        internal bool busy;

        internal SpaceMapLease(Map map, SpaceObjectTag tag)
        {
            Map = map;
            Tag = tag;
        }
    }

    /// <summary>
    /// Instance-owned map resources. Only explicit rent/return/load/removal events change this
    /// pool; there is no tick or scan schedule. Content and SpaceObject lifetime belong to callers.
    /// </summary>
    public sealed class SpaceMapPool
    {
        private const int MaxIdleMaps = 64;
        private readonly UniverseManager universe;
        private readonly Dictionary<Map, SpaceMapLease> leases = new Dictionary<Map, SpaceMapLease>();
        private readonly Dictionary<(SpaceObjectTag tag, int x, int z), List<Map>> idle =
            new Dictionary<(SpaceObjectTag, int, int), List<Map>>();
        private int idleCount;

        public SpaceMapPool(UniverseManager universe) { this.universe = universe; }

        public int IdleCount => idleCount;
        public int TrackedCount => leases.Count;

        public bool IsCurrent(SpaceMapLease lease) => lease != null && !lease.Map.Disposed
            && leases.TryGetValue(lease.Map, out SpaceMapLease current) && ReferenceEquals(current, lease);

        /// <summary>
        /// On population failure, lease remains available to the caller for recovery. Never
        /// delete a partly populated map containing real cargo or pawns. No object is registered here.
        /// </summary>
        public bool TryRent(SpaceObjectTag tag, IntVec2 size, Action<Map> populate, out SpaceMapLease lease)
        {
            lease = null;
            if (size.x < 1 || size.z < 1 || tag == 0 || populate == null)
                throw new ArgumentException("A map rental requires a tag, positive dimensions and content callback.");
            var key = (tag, size.x, size.z);
            Map map = null;
            if (idle.TryGetValue(key, out List<Map> maps))
            {
                while (maps.Count > 0 && map == null)
                {
                    Map candidate = maps[maps.Count - 1];
                    maps.RemoveAt(maps.Count - 1);
                    idleCount--;
                    leases.Remove(candidate);
                    if (!candidate.Disposed) map = candidate;
                }
                if (maps.Count == 0) idle.Remove(key);
            }
            if (map == null)
                map = ShipPocketMapFactory.GenerateImmediate(new IntVec3(size.x, 1, size.z), universe);
            if (map == null) return false;
            DeltaVPocketMapMetadata metadata = map.GetComponent<DeltaVPocketMapMetadata>();
            metadata.pooledMap = true;
            metadata.poolTag = tag;
            lease = new SpaceMapLease(map, tag) { busy = true };
            leases[map] = lease;
            universe.Lifecycle.Wake(map, "map pool rental");
            SpaceCollision.Forget(map);
            MapOverlayRenderer.Release(map);
            try
            {
                populate(map);
                return IsCurrent(lease);
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] Map pool population failed; retaining {UniverseManager.MapTag(map)} for recovery: {ex}");
                return false;
            }
            finally
            {
                lease.busy = false;
                if (tag != SpaceObjectTag.Projectile)
                    HostColonistBarVisibility.NotifyDockingStateChanged();
            }
        }

        /// <summary>Caller must unregister the owning object first; cleanup runs only for a current rental.</summary>
        public bool TryReturn(SpaceMapLease lease, Action<Map> releaseContents = null)
        {
            if (!IsCurrent(lease) || lease.idle || lease.busy) return false;
            Map map = lease.Map;
            if (universe.SpaceObjectOnMap(map) != null || map == Find.CurrentMap) return false;
            lease.busy = true;
            try
            {
                releaseContents?.Invoke(map);
                if (!IsCurrent(lease) || universe.SpaceObjectOnMap(map) != null
                    || map == Find.CurrentMap || !IsEmpty(map)) return false;
                // Empty maps may retain reservations or gas left by their previous occupants.
                map.reservationManager = new Verse.AI.ReservationManager(map);
                map.pawnDestinationReservationManager = new PawnDestinationReservationManager();
                map.physicalInteractionReservationManager = new PhysicalInteractionReservationManager();
                map.enrouteManager = new Verse.AI.EnrouteManager(map);
                if (map.gasGrid != null)
                {
                    foreach (IntVec3 cell in map.AllCells) map.gasGrid.ClearCellUnsafe(cell);
                    map.gasGrid.RecalculateEverHadGas();
                }
                if (idleCount >= MaxIdleMaps)
                {
                    Forget(map);
                    universe.Lifecycle.DestroyPocketMap(map);
                    return true;
                }
                lease.idle = true;
                var key = (lease.Tag, map.Size.x, map.Size.z);
                if (!idle.TryGetValue(key, out List<Map> maps)) idle[key] = maps = new List<Map>();
                maps.Add(map);
                idleCount++;
                if (lease.Tag != SpaceObjectTag.Projectile)
                    HostColonistBarVisibility.NotifyDockingStateChanged();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] Map pool return failed; retaining {UniverseManager.MapTag(map)}: {ex}");
                return false;
            }
            finally { lease.busy = false; }
        }

        private static bool IsEmpty(Map map)
        {
            if (map.listerThings.AllThings.Count != 0
                || (map.mapPawns != null && map.mapPawns.AllPawns.Count != 0)
                || (map.lordManager != null && map.lordManager.lords.Count != 0)) return false;
            // Return and reset use the same test. Empty Things does not imply empty terrain.
            foreach (IntVec3 cell in map.AllCells)
                if (map.terrainGrid.TerrainAt(cell) != TerrainDefOf.Space
                    || map.terrainGrid.FoundationAt(cell) != null
                    || map.terrainGrid.UnderTerrainAt(cell) != null
                    || map.terrainGrid.TempTerrainAt(cell) != null
                    || map.roofGrid?.RoofAt(cell) != null) return false;
            return true;
        }

        public bool IsIdle(Map map) => map != null && leases.TryGetValue(map, out SpaceMapLease lease) && lease.idle;

        internal SpaceMapLease LeaseFor(Map map) => map != null && leases.TryGetValue(map, out SpaceMapLease lease)
            ? lease : null;

        /// <summary>Called once at load for explicitly marked maps. Business owners retain nonempty rentals.</summary>
        internal SpaceMapLease Restore(Map map)
        {
            if (map == null || map.Disposed) return null;
            DeltaVPocketMapMetadata metadata = map.GetComponent<DeltaVPocketMapMetadata>();
            if (metadata?.pooledMap != true) return null;
            if (!leases.TryGetValue(map, out SpaceMapLease lease))
                leases[map] = lease = new SpaceMapLease(map, metadata.poolTag);
            return lease;
        }

        public void Forget(Map map)
        {
            if (map == null || !leases.TryGetValue(map, out SpaceMapLease lease)) return;
            leases.Remove(map);
            if (!lease.idle) return;
            var key = (lease.Tag, map.Size.x, map.Size.z);
            if (idle.TryGetValue(key, out List<Map> maps))
            {
                if (maps.Remove(map)) idleCount--;
                if (maps.Count == 0) idle.Remove(key);
            }
        }

        /// <summary>Snapshot rental identities now, release after caller's queued content teardown.</summary>
        internal void QueueReleaseUnused(SpaceObjectTag tag)
        {
            SpaceMapLease[] snapshot = leases.Values.Where(l => l.Tag == tag).ToArray();
            universe.QueueFrameAction(() =>
            {
                foreach (SpaceMapLease lease in snapshot)
                {
                    Map map = lease.Map;
                    if (map.Disposed) { Forget(map); continue; }
                    if (!IsCurrent(lease) || lease.busy || map == Find.CurrentMap || universe.SpaceObjectOnMap(map) != null
                        || !IsEmpty(map)) continue;
                    Forget(map);
                    universe.Lifecycle.DestroyPocketMap(map);
                }
            });
        }
    }
}
