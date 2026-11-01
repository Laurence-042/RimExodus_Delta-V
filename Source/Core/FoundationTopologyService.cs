using System.Collections.Generic;
using Verse;

namespace DeltaV
{
    /// <summary>UniverseManager 持有的按图拓扑缓存。事件只标格，查询前读取最终事实。</summary>
    internal sealed class FoundationTopologyService
    {
        private sealed class Entry
        {
            private readonly Map map;
            private readonly SubstructureOwnership ownership;
            internal readonly FoundationConnectivity connectivity;
            internal readonly HashSet<int> changed = new HashSet<int>();
            internal int blocksVersion = -1;
            internal List<HashSet<IntVec3>> blocks;

            internal Entry(Map map, SubstructureOwnership ownership)
            {
                this.map = map;
                this.ownership = ownership;
                connectivity = new FoundationConnectivity(map.Size.x, map.Size.z, ReadOwner);
            }

            private int ReadOwner(int index)
            {
                IntVec3 cell = map.cellIndices.IndexToCell(index);
                return SubstructureOwnership.HasFoundation(map, cell)
                    ? ownership.OwnerAt(cell) : FoundationConnectivity.Absent;
            }

            internal void Synchronize()
            {
                // Coalesce repeated terrain/ownership events, including transfer stamps.
                // No per-tick scan and no replay of transient states between queries.
                foreach (int index in changed) connectivity.Update(index, ReadOwner(index));
                changed.Clear();
            }
        }
        private readonly Dictionary<Map, Entry> maps = new Dictionary<Map, Entry>();

        internal void NotifyChanged(Map map, IntVec3 cell)
        {
            if (map != null && !map.Disposed && cell.InBounds(map) && maps.TryGetValue(map, out Entry entry))
                entry.changed.Add(map.cellIndices.CellToIndex(cell));
        }

        internal bool HasResidual(Ship ship)
        {
            Map map = ship.map;
            if (!maps.TryGetValue(map, out Entry entry))
            {
                SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
                if (ownership == null) return false; // Same empty-result contract as ResidualBlocks.
                entry = new Entry(map, ownership);
                maps.Add(map, entry);
            }
            else
            {
                entry.Synchronize();
            }
            IntVec3 seed = ship.console.parent.Position;
            return entry.connectivity.HasResidual(ship.ownerId,
                seed.InBounds(map) ? map.cellIndices.CellToIndex(seed) : -1);
        }

        internal IReadOnlyList<HashSet<IntVec3>> PhysicalBlocks(Map map)
        {
            if (!maps.TryGetValue(map, out Entry entry))
            {
                entry = new Entry(map, SubstructureOwnership.For(map));
                maps.Add(map, entry);
            }
            if (entry.blocks == null || entry.blocksVersion != SubstructureOwnership.GlobalStructureVersion)
            {
                entry.blocks = ShipRegistry.ResidualBlocks(map, new HashSet<IntVec3>());
                entry.blocksVersion = SubstructureOwnership.GlobalStructureVersion;
            }
            return entry.blocks;
        }

        internal void Forget(Map map) { if (map != null) maps.Remove(map); }
    }
}
