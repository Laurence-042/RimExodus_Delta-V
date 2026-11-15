using System.Collections.Generic;
using Verse;
using UnityEngine;

namespace DeltaV
{
    /// <summary>Runtime foundation components and their building membership.</summary>
    internal sealed class FoundationTopologyService
    {
        private sealed class Entry
        {
            internal readonly FoundationConnectivity connectivity;
            internal readonly HashSet<int> changed = new HashSet<int>();
            private Dictionary<int, HashSet<IntVec3>> domains;
            internal readonly Dictionary<HashSet<IntVec3>, Vector2> centers =
                new Dictionary<HashSet<IntVec3>, Vector2>();
            internal int memberVersion = -1;
            internal int unboundVersion = -1;
            internal bool hasUnboundBuildings;
            internal readonly Dictionary<HashSet<IntVec3>, HashSet<Building>> members =
                new Dictionary<HashSet<IntVec3>, HashSet<Building>>();

            internal Entry(Map map, SubstructureOwnership ownership)
            {
                connectivity = new FoundationConnectivity(map.Size.x, map.Size.z, index =>
                {
                    IntVec3 cell = map.cellIndices.IndexToCell(index);
                    return SubstructureOwnership.HasFoundation(map, cell)
                        ? ownership.OwnerAt(cell) : FoundationConnectivity.Absent;
                });
            }

            internal void Synchronize(Map map, SubstructureOwnership ownership)
            {
                foreach (int index in changed)
                {
                    IntVec3 cell = map.cellIndices.IndexToCell(index);
                    int owner = SubstructureOwnership.HasFoundation(map, cell)
                        ? ownership.OwnerAt(cell) : FoundationConnectivity.Absent;
                    if (connectivity.Update(index, owner))
                    {
                        domains = null;
                        centers.Clear();
                        members.Clear();
                    }
                }
                changed.Clear();
            }

            internal Dictionary<int, HashSet<IntVec3>> Domains(Map map)
            {
                if (domains != null) return domains;
                domains = new Dictionary<int, HashSet<IntVec3>>();
                foreach (int index in connectivity.Occupied)
                {
                    int root = connectivity.ComponentAt(index);
                    if (!domains.TryGetValue(root, out var cells))
                        domains.Add(root, cells = new HashSet<IntVec3>());
                    cells.Add(map.cellIndices.IndexToCell(index));
                }
                return domains;
            }
        }

        private readonly Dictionary<Map, Entry> maps = new Dictionary<Map, Entry>();

        private Entry For(Map map)
        {
            if (map == null || map.Disposed) return null;
            var ownership = SubstructureOwnership.TryGet(map);
            if (ownership == null) return null;
            if (!maps.TryGetValue(map, out var entry))
                maps.Add(map, entry = new Entry(map, ownership));
            entry.Synchronize(map, ownership);
            return entry;
        }

        internal void NotifyChanged(Map map, IntVec3 cell)
        {
            if (map != null && !map.Disposed && cell.InBounds(map) && maps.TryGetValue(map, out var entry))
                entry.changed.Add(map.cellIndices.CellToIndex(cell));
        }

        internal bool HasResidual(Ship ship) => For(ship.map)?.connectivity.HasResidual(ship.ownerId,
            ship.console.parent.Position.InBounds(ship.map)
                ? ship.map.cellIndices.CellToIndex(ship.console.parent.Position) : -1) == true;

        internal int DomainCount(Map map) => For(map)?.connectivity.ComponentCount ?? 0;

        internal bool HasUnboundBuildings(Map map)
        {
            var entry = For(map);
            if (entry == null) return false;
            int version = SubstructureOwnership.StructureVersion(map);
            if (entry.unboundVersion == version) return entry.hasUnboundBuildings;
            entry.hasUnboundBuildings = false;
            foreach (Building building in SubstructureOwnership.For(map).SpawnedBuildings)
            {
                if (ComponentUnder(map, entry, building.OccupiedRect()) != 0) continue;
                entry.hasUnboundBuildings = true;
                break;
            }
            entry.unboundVersion = version;
            return entry.hasUnboundBuildings;
        }

        internal IEnumerable<HashSet<IntVec3>> Domains(Map map)
        {
            var entry = For(map);
            return entry == null ? System.Array.Empty<HashSet<IntVec3>>() : entry.Domains(map).Values;
        }

        internal HashSet<IntVec3> DomainAt(Map map, IntVec3 cell)
        {
            var entry = For(map);
            if (entry == null || !cell.InBounds(map)) return null;
            int root = entry.connectivity.ComponentAt(map.cellIndices.CellToIndex(cell));
            return root == 0 ? null : entry.Domains(map)[root];
        }

        internal HashSet<IntVec3> DomainUnder(Map map, IEnumerable<IntVec3> footprint)
        {
            var entry = For(map);
            if (entry == null) return null;
            return DomainUnder(map, entry, footprint);
        }

        private static HashSet<IntVec3> DomainUnder(Map map, Entry entry, IEnumerable<IntVec3> footprint)
        {
            int root = ComponentUnder(map, entry, footprint);
            return root == 0 ? null : entry.Domains(map)[root];
        }

        private static int ComponentUnder(Map map, Entry entry, IEnumerable<IntVec3> footprint)
        {
            int root = 0;
            foreach (IntVec3 cell in footprint)
            {
                if (!cell.InBounds(map)) continue;
                int found = entry.connectivity.ComponentAt(map.cellIndices.CellToIndex(cell));
                if (found == 0) continue;
                if (root != 0 && root != found) return 0;
                root = found;
            }
            return root;
        }

        internal HashSet<IntVec3> BuildingDomain(Map map, Building building) =>
            building?.Spawned == true && building.Map == map
                ? DomainUnder(map, building.OccupiedRect()) : null;

        internal HashSet<Building> BuildingsIn(Map map, HashSet<IntVec3> cells)
        {
            var entry = For(map);
            if (entry == null) return new HashSet<Building>();
            int version = SubstructureOwnership.StructureVersion(map);
            if (entry.memberVersion != version)
            {
                entry.memberVersion = version;
                entry.members.Clear();
            }
            if (entry.members.TryGetValue(cells, out var buildings)) return buildings;
            buildings = new HashSet<Building>();
            var seen = new HashSet<Building>();
            foreach (IntVec3 cell in cells)
                foreach (Thing thing in map.thingGrid.ThingsListAt(cell))
                    if (thing is Building building && building.Spawned && building.Map == map && seen.Add(building)
                        && ReferenceEquals(DomainUnder(map, entry, building.OccupiedRect()), cells))
                        buildings.Add(building);
            entry.members.Add(cells, buildings);
            return buildings;
        }

        internal Vector2 CenterOf(Map map, HashSet<IntVec3> cells)
        {
            var entry = For(map);
            if (entry == null || cells.Count == 0) return Vector2.zero;
            if (!entry.centers.TryGetValue(cells, out var center))
                entry.centers.Add(cells, center = SpaceTransform.BoundsCenterOfCells(cells));
            return center;
        }

        internal void Forget(Map map) { if (map != null) maps.Remove(map); }
    }
}
