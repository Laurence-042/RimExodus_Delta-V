using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using UnityEngine;

namespace DeltaV
{
    /// <summary>Map index for connected foundation domains and footprint membership.</summary>
    internal sealed class FoundationTopologyService
    {
        private sealed class Axis
        {
            private readonly Dictionary<int, int> counts = new Dictionary<int, int>();
            private readonly SortedSet<int> occupied = new SortedSet<int>();
            internal int Min => occupied.Min;
            internal int Max => occupied.Max;
            internal float Center => (Min + Max + 1) * 0.5f;
            internal void Add(int coordinate)
            {
                counts.TryGetValue(coordinate, out int count);
                if (count == 0) occupied.Add(coordinate);
                counts[coordinate] = count + 1;
            }
            internal void Remove(int coordinate)
            {
                int remaining = counts[coordinate] - 1;
                if (remaining != 0)
                {
                    counts[coordinate] = remaining;
                    return;
                }
                counts.Remove(coordinate);
                occupied.Remove(coordinate);
            }
        }

        private sealed class Extents
        {
            internal readonly Axis x = new Axis(), z = new Axis();
            internal Vector2 Center => new Vector2(x.Center, z.Center);
        }

        private sealed class Domain
        {
            internal readonly HashSet<IntVec3> Cells = new HashSet<IntVec3>();
            internal readonly HashSet<Building> Buildings = new HashSet<Building>();
            private readonly Axis x = new Axis(), z = new Axis();
            private readonly Dictionary<int, SortedSet<int>> rows = new Dictionary<int, SortedSet<int>>();
            private float radius;
            internal int Revision { get; private set; }
            internal int MembershipRevision;
            internal Vector2 Center => Cells.Count == 0 ? Vector2.zero : new Vector2(x.Center, z.Center);
            internal CellRect Bounds => Cells.Count == 0 ? CellRect.Empty
                : CellRect.FromLimits(x.Min, z.Min, x.Max, z.Max);
            internal float Radius
            {
                get
                {
                    if (radius > 0f) return radius;
                    Vector2 center = Center;
                    float squared = 0f;
                    foreach (var row in rows)
                    {
                        float dx = System.Math.Max(System.Math.Abs(row.Value.Min + 0.5f - center.x), System.Math.Abs(row.Value.Max + 0.5f - center.x));
                        float dz = row.Key + 0.5f - center.y;
                        squared = System.Math.Max(squared, dx * dx + dz * dz);
                    }
                    return radius = rows.Count == 0 ? 0.5f : (float)System.Math.Sqrt(squared) + 0.71f;
                }
            }

            internal void Add(IntVec3 cell)
            {
                Cells.Add(cell);
                Revision++;
                if (!rows.TryGetValue(cell.z, out var row)) rows.Add(cell.z, row = new SortedSet<int>());
                row.Add(cell.x);
                radius = 0f;
                x.Add(cell.x);
                z.Add(cell.z);
            }
            internal void Remove(IntVec3 cell)
            {
                Cells.Remove(cell);
                Revision++;
                var row = rows[cell.z];
                row.Remove(cell.x);
                if (row.Count == 0) rows.Remove(cell.z);
                radius = 0f;
                x.Remove(cell.x);
                z.Remove(cell.z);
            }

            internal void OwnerChanged() => Revision++;
        }

        private sealed class Entry
        {
            private readonly Map map;
            private readonly FoundationTopologyService service;
            internal readonly FoundationConnectivity connectivity;
            internal readonly HashSet<int> changed = new HashSet<int>();
            internal long Synchronizations { get; private set; }
            internal long SynchronizedCells { get; private set; }
            internal long RelabeledCells { get; private set; }
            private readonly Domain[] domainByCell;
            private readonly Dictionary<int, Domain> formerDomains = new Dictionary<int, Domain>();
            internal readonly Dictionary<HashSet<IntVec3>, Domain> domains = new Dictionary<HashSet<IntVec3>, Domain>();
            internal readonly Dictionary<int, HashSet<IntVec3>> ownerCells = new Dictionary<int, HashSet<IntVec3>>();
            internal readonly Dictionary<int, Extents> ownerExtents = new Dictionary<int, Extents>();
            private readonly int[] ownerByCell;
            private readonly Dictionary<IntVec3, HashSet<Building>> buildingsByCell = new Dictionary<IntVec3, HashSet<Building>>();
            private readonly Dictionary<Building, List<IntVec3>> footprints = new Dictionary<Building, List<IntVec3>>();
            private readonly Dictionary<Building, Domain> buildingDomains = new Dictionary<Building, Domain>();
            internal readonly HashSet<Building> unbound = new HashSet<Building>();
            private readonly HashSet<Building> affected = new HashSet<Building>();
            private readonly List<int> neighbors = new List<int>(4);
            private readonly HashSet<int> previousRoots = new HashSet<int>();
            private readonly List<Domain> joined = new List<Domain>(4);

            internal Entry(FoundationTopologyService service, Map map, SubstructureOwnership ownership, bool physical = false)
            {
                this.service = service;
                this.map = map;
                this.physical = physical;
                domainByCell = new Domain[map.Size.x * map.Size.z];
                ownerByCell = new int[domainByCell.Length];
                long start = service.startTiming?.Invoke() ?? 0;
                connectivity = new FoundationConnectivity(map.Size.x, map.Size.z, index =>
                    ownerByCell[index] = ReadOwner(ownership, map.cellIndices.IndexToCell(index)),
                    service.startTiming, service.recordReclaim);
                var roots = new Dictionary<int, Domain>();
                foreach (int index in connectivity.Occupied)
                {
                    int root = connectivity.ComponentAt(index);
                    if (!roots.TryGetValue(root, out Domain domain)) roots.Add(root, domain = NewDomain());
                    AddCell(domain, index);
                    AddOwnerCell(ownerByCell[index], map.cellIndices.IndexToCell(index));
                }
                if (start != 0) service.recordBootstrap?.Invoke(start, ownerByCell.Length, 0);
            }

            private readonly bool physical;

            private int ReadOwner(SubstructureOwnership ownership, IntVec3 cell) =>
                SubstructureOwnership.HasFoundation(map, cell) ? (physical ? 0 : ownership.OwnerAt(cell)) : FoundationConnectivity.Absent;

            private void AddOwnerCell(int owner, IntVec3 cell)
            {
                if (!ownerCells.TryGetValue(owner, out var cells))
                {
                    ownerCells.Add(owner, cells = new HashSet<IntVec3>());
                    ownerExtents.Add(owner, new Extents());
                }
                cells.Add(cell);
                var extents = ownerExtents[owner];
                extents.x.Add(cell.x);
                extents.z.Add(cell.z);
            }

            private void ChangeOwner(int index, IntVec3 cell, int owner)
            {
                int previous = ownerByCell[index];
                if (previous != FoundationConnectivity.Absent)
                {
                    var cells = ownerCells[previous];
                    cells.Remove(cell);
                    if (cells.Count == 0)
                    {
                        ownerCells.Remove(previous);
                        ownerExtents.Remove(previous);
                    }
                    else
                    {
                        var extents = ownerExtents[previous];
                        extents.x.Remove(cell.x);
                        extents.z.Remove(cell.z);
                    }
                }
                if (owner != FoundationConnectivity.Absent) AddOwnerCell(owner, cell);
                ownerByCell[index] = owner;
            }

            private Domain NewDomain()
            {
                var domain = new Domain();
                domains.Add(domain.Cells, domain);
                return domain;
            }

            private void AddCell(Domain domain, int index)
            {
                domain.Add(map.cellIndices.IndexToCell(index));
                if (!physical && changed.Count != 0 && !formerDomains.ContainsKey(index))
                    formerDomains.Add(index, domainByCell[index]);
                domainByCell[index] = domain;
            }

            private void TrackAffected(IntVec3 cell)
            {
                if (buildingsByCell.TryGetValue(cell, out var buildings)) affected.UnionWith(buildings);
            }

            internal void Synchronize(SubstructureOwnership ownership)
            {
                if (changed.Count == 0) return;
                long start = service.startTiming?.Invoke() ?? 0;
                int work = changed.Count;
                int[] changedCells = physical ? null : changed.ToArray();
                if (work == 1 || (!TryAddBatch(ownership) && !TryRelabelDomains(ownership)))
                    foreach (int index in changed) SynchronizeCell(index, ownership);
                Synchronizations++;
                SynchronizedCells += work;
                changed.Clear();
                foreach (Building building in affected) RefreshDomain(building);
                affected.Clear();
                if (changedCells != null)
                {
                    var cells = new HashSet<IntVec3>();
                    foreach (int index in changedCells) cells.Add(map.cellIndices.IndexToCell(index));
                    foreach (int index in formerDomains.Keys) cells.Add(map.cellIndices.IndexToCell(index));
                    service.TopologyChanged?.Invoke(map, cells);
                }
                PublishBoundaryChanges();
                if (start != 0) service.recordUpdate?.Invoke(start, work);
            }

            private bool TryAddBatch(SubstructureOwnership ownership)
            {
                List<(int Index, int Owner)> additions = null;
                foreach (int index in changed)
                {
                    int owner = ReadOwner(ownership, map.cellIndices.IndexToCell(index));
                    int previous = ownerByCell[index];
                    if (owner == previous) continue;
                    if (previous != FoundationConnectivity.Absent) return false;
                    (additions ??= new List<(int Index, int Owner)>(changed.Count)).Add((index, owner));
                }
                if (additions == null) return true;
                if (additions.Count == 1) return false;
                connectivity.AddBatch(additions);
                foreach (var (index, owner) in additions)
                {
                    IntVec3 cell = map.cellIndices.IndexToCell(index);
                    ChangeOwner(index, cell, owner);
                    connectivity.GetNeighbors(index, neighbors);
                    TrackAffected(cell);
                    JoinDomain(index);
                }
                return true;
            }

            private bool TryRelabelDomains(SubstructureOwnership ownership)
            {
                if (physical) return false;
                var nextOwners = new Dictionary<int, int>(changed.Count);
                foreach (int index in changed)
                {
                    if (ownerByCell[index] == FoundationConnectivity.Absent) return false;
                    int owner = ReadOwner(ownership, map.cellIndices.IndexToCell(index));
                    if (owner == FoundationConnectivity.Absent) return false;
                    if (owner != ownerByCell[index]) nextOwners.Add(index, owner);
                }
                foreach (var change in nextOwners)
                {
                    connectivity.GetNeighbors(change.Key, neighbors);
                    foreach (int neighbor in neighbors)
                    {
                        int before = ownerByCell[neighbor];
                        int after = nextOwners.TryGetValue(neighbor, out int next) ? next : before;
                        if ((before == ownerByCell[change.Key]) != (after == change.Value)) return false;
                    }
                }
                connectivity.RelabelDomains(nextOwners);
                var relabeled = new HashSet<Domain>();
                foreach (var change in nextOwners)
                {
                    ChangeOwner(change.Key, map.cellIndices.IndexToCell(change.Key), change.Value);
                    if (relabeled.Add(domainByCell[change.Key])) domainByCell[change.Key].OwnerChanged();
                }
                RelabeledCells += nextOwners.Count;
                return true;
            }

            private void PublishBoundaryChanges()
            {
                if (formerDomains.Count == 0) return;
                var boundary = new HashSet<IntVec3>();
                foreach (var entry in formerDomains)
                {
                    Domain after = domainByCell[entry.Key];
                    if (after == entry.Value) continue;
                    IntVec3 cell = map.cellIndices.IndexToCell(entry.Key);
                    // A local foundation addition or removal already publishes its cell.
                    // Compare the relationships that can change even among surviving cells.
                    if (after == null || entry.Value == null) continue;
                    for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        var neighbor = new IntVec3(cell.x + dx, cell.y, cell.z + dz);
                        if (!neighbor.InBounds(map)) continue;
                        int index = map.cellIndices.CellToIndex(neighbor);
                        Domain neighborAfter = domainByCell[index];
                        Domain neighborBefore = formerDomains.TryGetValue(index, out var before) ? before : neighborAfter;
                        if ((entry.Value == neighborBefore) == (after == neighborAfter)) continue;
                        boundary.Add(cell);
                        boundary.Add(neighbor);
                    }
                }
                formerDomains.Clear();
                if (boundary.Count != 0) service.HullBoundaryChanged?.Invoke(map, boundary);
            }

            private void SynchronizeCell(int index, SubstructureOwnership ownership)
            {
                IntVec3 cell = map.cellIndices.IndexToCell(index);
                int nextOwner = ReadOwner(ownership, cell);
                Domain previous = domainByCell[index];
                if (!connectivity.Update(index, nextOwner)) return;
                ChangeOwner(index, cell, nextOwner);
                connectivity.GetNeighbors(index, neighbors);
                TrackAffected(cell);
                if (previous != null) RemoveFromDomain(previous, index, cell);
                if (nextOwner != FoundationConnectivity.Absent) JoinDomain(index);
            }

            private void RemoveFromDomain(Domain previous, int index, IntVec3 cell)
            {
                previous.Remove(cell);
                if (!physical && !formerDomains.ContainsKey(index)) formerDomains.Add(index, previous);
                domainByCell[index] = null;
                if (previous.Cells.Count == 0)
                {
                    domains.Remove(previous.Cells);
                    return;
                }
                previousRoots.Clear();
                foreach (int neighbor in neighbors)
                    if (domainByCell[neighbor] == previous)
                        previousRoots.Add(connectivity.ComponentAt(neighbor));
                if (previousRoots.Count <= 1) return;

                long start = service.startTiming?.Invoke() ?? 0;
                int visited = previous.Cells.Count;
                // Only a genuine split enumerates the former domain. Keep its largest piece in place.
                var pieces = new Dictionary<int, List<IntVec3>>();
                List<IntVec3> largest = null;
                foreach (IntVec3 member in previous.Cells)
                {
                    int root = connectivity.ComponentAt(map.cellIndices.CellToIndex(member));
                    if (!pieces.TryGetValue(root, out var piece)) pieces.Add(root, piece = new List<IntVec3>());
                    piece.Add(member);
                    if (largest == null || piece.Count > largest.Count) largest = piece;
                }
                foreach (var piece in pieces.Values)
                {
                    if (piece == largest) continue;
                    Domain domain = NewDomain();
                    foreach (IntVec3 member in piece)
                    {
                        previous.Remove(member);
                        AddCell(domain, map.cellIndices.CellToIndex(member));
                        TrackAffected(member);
                    }
                }
                if (start != 0) service.recordSplit?.Invoke(start, visited);
            }

            private void JoinDomain(int index)
            {
                int root = connectivity.ComponentAt(index);
                joined.Clear();
                Domain largest = null;
                foreach (int neighbor in neighbors)
                {
                    Domain domain = domainByCell[neighbor];
                    if (domain == null || joined.Contains(domain) || connectivity.ComponentAt(neighbor) != root) continue;
                    joined.Add(domain);
                    if (largest == null || domain.Cells.Count > largest.Cells.Count) largest = domain;
                }
                largest ??= NewDomain();
                AddCell(largest, index);
                foreach (Domain domain in joined)
                {
                    if (domain == largest) continue;
                    foreach (IntVec3 cell in domain.Cells)
                    {
                        AddCell(largest, map.cellIndices.CellToIndex(cell));
                        TrackAffected(cell);
                    }
                    domains.Remove(domain.Cells);
                }
            }

            internal Domain At(IntVec3 cell) => cell.InBounds(map) ? domainByCell[map.cellIndices.CellToIndex(cell)] : null;

            internal Domain Under(IEnumerable<IntVec3> footprint)
            {
                Domain domain = null;
                foreach (IntVec3 cell in footprint)
                {
                    Domain found = At(cell);
                    if (found == null) continue;
                    if (domain != null && domain != found) return null;
                    domain = found;
                }
                return domain;
            }

            internal Domain DomainOf(Building building) => buildingDomains.TryGetValue(building, out var domain) ? domain : null;

            internal void UpdateBuilding(Building building)
            {
                if (footprints.Remove(building, out var previous))
                    foreach (IntVec3 cell in previous)
                    {
                        var buildings = buildingsByCell[cell];
                        buildings.Remove(building);
                        if (buildings.Count == 0) buildingsByCell.Remove(cell);
                    }
                if (!building.Spawned || building.Map != map)
                {
                    RefreshDomain(building);
                    return;
                }
                var footprint = new List<IntVec3>();
                foreach (IntVec3 cell in building.OccupiedRect())
                {
                    if (!cell.InBounds(map)) continue;
                    footprint.Add(cell);
                    if (!buildingsByCell.TryGetValue(cell, out var buildings))
                        buildingsByCell.Add(cell, buildings = new HashSet<Building>());
                    buildings.Add(building);
                }
                footprints.Add(building, footprint);
                RefreshDomain(building, footprintChanged: true);
            }

            private void RemoveBuildingDomain(Building building)
            {
                if (buildingDomains.Remove(building, out var domain)) domain.Buildings.Remove(building);
                unbound.Remove(building);
            }

            private void RefreshDomain(Building building, bool footprintChanged = false)
            {
                Domain previous = DomainOf(building);
                RemoveBuildingDomain(building);
                bool registered = footprints.TryGetValue(building, out var footprint);
                Domain domain = registered ? Under(footprint) : null;
                if (domain != null)
                {
                    buildingDomains.Add(building, domain);
                    domain.Buildings.Add(building);
                }
                else if (registered) unbound.Add(building);
                if (previous != null && (previous != domain || footprintChanged)) previous.MembershipRevision++;
                if (domain != null && previous != domain) domain.MembershipRevision++;
                service.BuildingDomainChanged?.Invoke(map, building, previous?.Cells, domain?.Cells);
            }
        }

        private readonly Func<long> startTiming;
        private readonly Action<long, int, int> recordBootstrap, recordReclaim;
        private readonly Action<long, int> recordSplit, recordUpdate;

        internal FoundationTopologyService(Func<long> startTiming = null, Action<long, int, int> recordBootstrap = null,
            Action<long, int> recordSplit = null, Action<long, int, int> recordReclaim = null, Action<long, int> recordUpdate = null)
        {
            this.startTiming = startTiming;
            this.recordBootstrap = recordBootstrap;
            this.recordSplit = recordSplit;
            this.recordReclaim = recordReclaim;
            this.recordUpdate = recordUpdate;
        }

        private readonly Dictionary<Map, Entry> maps = new Dictionary<Map, Entry>();
        private readonly Dictionary<Map, Entry> physicalDomains = new Dictionary<Map, Entry>();
        internal event Action<Map, Building, HashSet<IntVec3>, HashSet<IntVec3>> BuildingDomainChanged;
        internal event Action<Map, HashSet<IntVec3>> HullBoundaryChanged;
        internal event Action<Map, HashSet<IntVec3>> TopologyChanged;

        internal (long Syncs, long Cells, long AdditionBatches, long BatchAddedCells, long RelabeledCells, long EdgeChanges)
            WorkOf(Map map) => map != null && maps.TryGetValue(map, out var entry)
                ? (entry.Synchronizations, entry.SynchronizedCells, entry.connectivity.AdditionBatches,
                    entry.connectivity.BatchAddedCells, entry.RelabeledCells, entry.connectivity.EdgeChanges)
                : default;

        private Entry For(Map map)
        {
            if (map == null || map.Disposed) return null;
            var ownership = SubstructureOwnership.TryGet(map);
            if (ownership == null) return null;
            if (!maps.TryGetValue(map, out var entry))
            {
                maps.Add(map, entry = new Entry(this, map, ownership));
                foreach (Building building in ownership.SpawnedBuildings) entry.UpdateBuilding(building);
            }
            entry.Synchronize(ownership);
            return entry;
        }

        internal HashSet<IntVec3> PhysicalDomainAt(Map map, IntVec3 cell)
        {
            if (map == null || map.Disposed || SubstructureOwnership.TryGet(map) is not { } ownership) return null;
            if (!physicalDomains.TryGetValue(map, out var entry))
                physicalDomains.Add(map, entry = new Entry(this, map, ownership, physical: true));
            entry.Synchronize(ownership);
            return entry.At(cell)?.Cells;
        }

        internal void NotifyChanged(Map map, IntVec3 cell)
        {
            if (map == null || map.Disposed || !cell.InBounds(map)) return;
            int index = map.cellIndices.CellToIndex(cell);
            if (maps.TryGetValue(map, out var entry)) entry.changed.Add(index);
            if (physicalDomains.TryGetValue(map, out var physical)) physical.changed.Add(index);
        }

        internal void NotifyBuilding(Map map, Building building)
        {
            if (map != null && building != null && maps.TryGetValue(map, out var entry)) entry.UpdateBuilding(building);
        }

        internal bool HasResidual(Ship ship) => For(ship.map)?.connectivity.HasResidual(ship.ownerId,
            ship.console.parent.Position.InBounds(ship.map)
                ? ship.map.cellIndices.CellToIndex(ship.console.parent.Position) : -1) == true;

        internal IReadOnlyDictionary<int, HashSet<IntVec3>> OwnerCells(Map map) => For(map)?.ownerCells;
        internal Vector2 OwnerCenter(Map map, int owner) => For(map) is { } entry
            && entry.ownerExtents.TryGetValue(owner, out var extents) ? extents.Center : Vector2.zero;
        internal int DomainCount(Map map) => For(map)?.domains.Count ?? 0;
        internal bool HasUnboundBuildings(Map map) => For(map)?.unbound.Count > 0;

        internal IEnumerable<HashSet<IntVec3>> Domains(Map map) => For(map) is { } entry
            ? entry.domains.Keys : System.Array.Empty<HashSet<IntVec3>>();

        internal int DomainRevision(Map map, HashSet<IntVec3> cells) => cells != null && For(map) is { } entry
            && entry.domains.TryGetValue(cells, out var domain) ? domain.Revision : -1;

        internal int MembershipRevision(Map map, HashSet<IntVec3> cells) => cells != null && For(map) is { } entry
            && entry.domains.TryGetValue(cells, out var domain) ? domain.MembershipRevision : -1;

        internal HashSet<IntVec3> DomainAt(Map map, IntVec3 cell) => For(map)?.At(cell)?.Cells;
        internal HashSet<IntVec3> DomainUnder(Map map, IEnumerable<IntVec3> footprint) => For(map)?.Under(footprint)?.Cells;
        internal HashSet<IntVec3> BuildingDomain(Map map, Building building) => building?.Spawned == true && building.Map == map
            ? For(map)?.DomainOf(building)?.Cells : null;

        internal HashSet<Building> BuildingsIn(Map map, HashSet<IntVec3> cells) =>
            For(map) is { } entry && entry.domains.TryGetValue(cells, out var domain) ? domain.Buildings : new HashSet<Building>();

        internal Vector2 CenterOf(Map map, HashSet<IntVec3> cells) => cells.Count == 0 ? Vector2.zero
            : For(map) is { } entry && entry.domains.TryGetValue(cells, out var domain) ? domain.Center
            : SpaceTransform.BoundsCenterOfCells(cells);

        internal CellRect BoundsOf(Map map, HashSet<IntVec3> cells) => cells.Count == 0 ? CellRect.Empty
            : For(map) is { } entry && entry.domains.TryGetValue(cells, out var domain) ? domain.Bounds
            : CellRect.FromCellList(new List<IntVec3>(cells));

        internal float? RadiusOf(Map map, HashSet<IntVec3> cells) => For(map) is { } entry
            && entry.domains.TryGetValue(cells, out var domain) ? domain.Radius : null;

        internal void Forget(Map map)
        {
            if (map == null) return;
            maps.Remove(map);
            physicalDomains.Remove(map);
        }

        internal void Clear()
        {
            maps.Clear();
            physicalDomains.Clear();
        }
    }
}
