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
        }

        private readonly Dictionary<Map, Entry> maps = new Dictionary<Map, Entry>();
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

        internal void Changed(Map map)
        {
            // An unknown local scope is not permission to invalidate the entire universe.
            if (map == null) return;
            Events++;
            GlobalVersion++;
            if (!maps.TryGetValue(map, out var entry)) maps.Add(map, entry = new Entry());
            entry.dirty = true;
            entry.notified = true;
        }

        internal void InvalidateAll()
        {
            Events++;
            GlobalVersion++;
            foreach (var entry in maps.Values) entry.dirty = true;
        }

        internal void Forget(Map map)
        {
            if (map == null) return;
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
            maps.Clear();
            GlobalVersion++;
        }
    }
}
