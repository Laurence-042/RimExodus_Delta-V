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

        internal FoundationConnectivity(int width, int height, Func<int, int> readOwner)
        {
            this.width = width;
            this.height = height;
            owners = new int[width * height];
            topology = new GridTopology(width * 2 - 1, height * 2 - 1, Conn.Fg4Bg8);
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
            topology.AddBatch(initial);
        }

        internal int ComponentAt(int index) => topology.ForegroundRoot(index % width * 2, index / width * 2);

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
            if (x > 0) SetEdge(index - 1, x * 2 - 1, z * 2, owner);
            if (x + 1 < width) SetEdge(index + 1, x * 2 + 1, z * 2, owner);
            if (z > 0) SetEdge(index - width, x * 2, z * 2 - 1, owner);
            if (z + 1 < height) SetEdge(index + width, x * 2, z * 2 + 1, owner);
            if (owner == Absent)
            {
                occupied.Remove(index);
                topology.Remove(x * 2, z * 2);
            }
            return true;
        }

        private void SetEdge(int neighbor, int x, int z, int owner)
        {
            if (owner != Absent && owners[neighbor] == owner) topology.Add(x, z);
            else topology.Remove(x, z);
        }

        internal bool HasResidual(int owner, int seed) => occupied.Count != 0
            && (owner <= 0 || seed < 0 || seed >= owners.Length || owners[seed] != owner || ComponentCount != 1);
    }
}
