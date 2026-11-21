using System;
using System.Collections.Generic;
using DeltaV.Topology;

namespace DeltaV
{
    /// <summary>One incremental graph of foundations. Intermediate grid slots encode same-owner edges.</summary>
    internal sealed class FoundationConnectivity
    {
        internal const int Absent = -1;
        private readonly int width;
        private readonly int height;
        private readonly int[] owners;
        private readonly GridTopology topology;
        private readonly HashSet<int> occupied = new HashSet<int>();
        internal int ComponentCount => topology.ComponentCount;
        internal IEnumerable<int> Occupied => occupied;
        internal long AdditionBatches { get; private set; }
        internal long BatchAddedCells { get; private set; }
        internal long EdgeChanges { get; private set; }

        internal FoundationConnectivity(int width, int height, Func<int, int> readOwner,
            Func<long> startReclaim = null, Action<long, int, int> recordReclaim = null)
        {
            this.width = width;
            this.height = height;
            owners = new int[width * height];
            topology = new GridTopology(width * 2 - 1, height * 2 - 1, Conn.Fg4Bg8)
            { StartReclaim = startReclaim, RecordReclaim = recordReclaim };
            var initial = new List<int>();
            for (int index = 0; index < owners.Length; index++)
            {
                int owner = owners[index] = readOwner(index);
                if (owner == Absent) continue;
                occupied.Add(index);
                int x = index % width, z = index / width;
                int vertex = (z * 2 + 1) * topology.Size + x * 2 + 1;
                initial.Add(vertex);
                if (x > 0 && owners[index - 1] == owner) initial.Add(vertex - 1);
                if (z > 0 && owners[index - width] == owner) initial.Add(vertex - topology.Size);
            }
            if (initial.Count != 0)
            {
                topology.AddBatch(initial);
                AdditionBatches++;
                BatchAddedCells += occupied.Count;
                EdgeChanges += initial.Count - occupied.Count;
            }
        }

        internal int ComponentAt(int index) => topology.ForegroundRoot(index % width * 2, index / width * 2);

        // Every index is absent from the old graph. Install the final owners before building edges.
        internal void AddBatch(IReadOnlyList<(int Index, int Owner)> additions)
        {
            var slots = new List<int>(additions.Count * 3);
            foreach (var (index, owner) in additions)
            {
                owners[index] = owner;
                occupied.Add(index);
            }
            foreach (var (index, owner) in additions)
            {
                int x = index % width, z = index / width;
                int vertex = (z * 2 + 1) * topology.Size + x * 2 + 1;
                slots.Add(vertex);
                if (x > 0 && owners[index - 1] == owner) slots.Add(vertex - 1);
                if (z > 0 && owners[index - width] == owner) slots.Add(vertex - topology.Size);
                if (x + 1 < width && owners[index + 1] == owner && topology.Present((x + 1) * 2, z * 2))
                    slots.Add(vertex + 1);
                if (z + 1 < height && owners[index + width] == owner && topology.Present(x * 2, (z + 1) * 2))
                    slots.Add(vertex + topology.Size);
            }
            topology.AddBatch(slots);
            AdditionBatches++;
            BatchAddedCells += additions.Count;
            EdgeChanges += slots.Count - additions.Count;
        }

        // The service has verified that each incident edge retains its current state.
        internal void RelabelDomains(IReadOnlyDictionary<int, int> nextOwners)
        {
            foreach (var owner in nextOwners) owners[owner.Key] = owner.Value;
        }

        internal bool Update(int index, int owner)
        {
            int previous = owners[index];
            if (previous == owner) return false;
            owners[index] = owner;
            int x = index % width, z = index / width;
            if (previous == Absent)
            {
                occupied.Add(index);
                topology.Add(x * 2, z * 2);
            }
            if (x > 0) SetEdge(index - 1, x * 2 - 1, z * 2, previous, owner);
            if (x + 1 < width) SetEdge(index + 1, x * 2 + 1, z * 2, previous, owner);
            if (z > 0) SetEdge(index - width, x * 2, z * 2 - 1, previous, owner);
            if (z + 1 < height) SetEdge(index + width, x * 2, z * 2 + 1, previous, owner);
            if (owner == Absent)
            {
                occupied.Remove(index);
                topology.Remove(x * 2, z * 2);
            }
            return true;
        }

        internal void GetNeighbors(int index, List<int> neighbors)
        {
            neighbors.Clear();
            int x = index % width, z = index / width;
            if (x > 0) neighbors.Add(index - 1);
            if (x + 1 < width) neighbors.Add(index + 1);
            if (z > 0) neighbors.Add(index - width);
            if (z + 1 < height) neighbors.Add(index + width);
        }

        private void SetEdge(int neighbor, int x, int z, int previous, int owner)
        {
            int adjacent = owners[neighbor];
            bool before = previous != Absent && adjacent == previous;
            bool after = owner != Absent && adjacent == owner;
            if (before == after) return;
            EdgeChanges++;
            if (after) topology.Add(x, z);
            else topology.Remove(x, z);
        }

        internal bool HasResidual(int owner, int seed) => occupied.Count != 0
            && (owner <= 0 || seed < 0 || seed >= owners.Length || owners[seed] != owner || ComponentCount != 1);
    }
}
