using System;
using System.Collections.Generic;
using Verse;

namespace DeltaV
{
    /// <summary>Runtime dependency stamps. Events invalidate facts immediately; readers rebuild lazily.</summary>
    internal sealed class StructureVersions
    {
        private sealed class Entry
        {
            internal int version;
            internal bool dirty;
            internal bool notified;
            internal bool watchesCollision;
            internal bool fullCollisionRefresh;
            internal HashSet<IntVec3> collisionCells;
        }

        private readonly Dictionary<Map, Entry> maps = new Dictionary<Map, Entry>();
        private readonly Action<Map> forgetProtection;
        internal StructureVersions(Action<Map> forgetProtection = null) => this.forgetProtection = forgetProtection;
        private int serial;
        internal int GlobalVersion { get; private set; }
        internal long Events { get; private set; }
        internal long Publications { get; private set; }
        internal int MapCount => maps.Count;

        internal int For(Map map)
        {
            if (map == null) return -1;
            if (!maps.TryGetValue(map, out var entry))
                maps.Add(map, entry = new Entry { dirty = true });
            if (entry.dirty)
            {
                entry.version = ++serial;
                entry.dirty = false;
                Publications++;
            }
            return entry.version;
        }

        internal void Changed(Map map, CellRect? collisionRange = null)
        {
            // An unknown local scope is not permission to invalidate the entire universe.
            if (map == null) return;
            Events++;
            GlobalVersion++;
            if (!maps.TryGetValue(map, out var entry)) maps.Add(map, entry = new Entry());
            entry.dirty = true;
            entry.notified = true;
            if (!entry.watchesCollision) return;
            if (!collisionRange.HasValue)
            {
                entry.fullCollisionRefresh = true;
                entry.collisionCells?.Clear();
                return;
            }
            foreach (IntVec3 cell in collisionRange.Value) MarkCollisionCell(entry, cell);
        }

        private static void MarkCollisionCell(Entry entry, IntVec3 cell)
        {
            if (entry.fullCollisionRefresh) return;
            entry.collisionCells ??= new HashSet<IntVec3>();
            entry.collisionCells.Add(cell);
        }

        internal HashSet<IntVec3> TakeCollisionChanges(Map map, out bool full)
        {
            if (!maps.TryGetValue(map, out var entry)) maps.Add(map, entry = new Entry { dirty = true });
            full = !entry.watchesCollision || entry.fullCollisionRefresh;
            entry.watchesCollision = true;
            entry.fullCollisionRefresh = false;
            var cells = entry.collisionCells;
            entry.collisionCells = null;
            return cells;
        }

        internal void InvalidateAll()
        {
            Events++;
            GlobalVersion++;
            foreach (var entry in maps.Values)
            {
                entry.dirty = true;
                entry.fullCollisionRefresh = true;
                entry.collisionCells?.Clear();
            }
        }

        internal void Forget(Map map)
        {
            if (map == null) return;
            forgetProtection?.Invoke(map);
            if (maps.TryGetValue(map, out var entry))
            {
                maps.Remove(map);
                // Collision queries on transient projectile maps must not churn the
                // universe-wide identity index when those maps return to the pool.
                if (entry.notified) GlobalVersion++;
            }
            // Do not recycle stamps: a pooled Map may be read again by an old cache owner.
        }

        internal void Clear()
        {
            forgetProtection?.Invoke(null);
            maps.Clear();
            GlobalVersion++;
        }
    }
}
