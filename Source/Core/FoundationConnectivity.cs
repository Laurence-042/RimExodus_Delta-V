using System;
using System.Collections.Generic;
using DeltaV.Topology;

namespace DeltaV
{
    /// <summary>从真实基架/归属重建的查询索引；不持久化、不负责归属或拆分决策。</summary>
    internal sealed class FoundationConnectivity
    {
        // -1 means no foundation; zero remains a real, unassigned foundation owner.
        internal const int Absent = -1;
        private readonly int width;
        private readonly int[] owners;
        private readonly Dictionary<int, int> counts = new Dictionary<int, int>();
        private readonly GridTopology topology;
        private int total;

        internal FoundationConnectivity(int width, int height, Func<int, int> readOwner)
        {
            this.width = width;
            owners = new int[width * height];
            topology = new GridTopology(width, height, Conn.Fg4Bg8);
            var initial = new List<int>();
            for (int i = 0; i < owners.Length; i++)
            {
                int owner = owners[i] = readOwner(i);
                if (owner == Absent) continue;
                ChangeCount(owner, 1);
                initial.Add((i / width + 1) * topology.Size + i % width + 1);
            }
            topology.AddBatch(initial);
        }

        internal void Update(int index, int owner)
        {
            int previous = owners[index];
            if (previous == owner) return;
            if (previous != Absent) ChangeCount(previous, -1);
            if (owner != Absent) ChangeCount(owner, 1);
            if (previous == Absent) topology.Add(index % width, index / width);
            else if (owner == Absent) topology.Remove(index % width, index / width);
            owners[index] = owner;
        }

        private void ChangeCount(int owner, int delta)
        {
            counts.TryGetValue(owner, out int count);
            count += delta;
            if (count == 0) counts.Remove(owner); else counts[owner] = count;
            total += delta;
        }

        /// <summary>
        /// 控制域外有基架的充要判据：非空图只有在全部基架同归属、物理上仅一块、
        /// 且主台脚下在该块内时才无残留。无需建立每个 owner 的第二套拓扑。
        /// 混合归属即使物理相连也有残留；枚举清单仍交回 ShipRegistry 的原四连通入口。
        /// </summary>
        internal bool HasResidual(int owner, int seed)
        {
            if (total == 0) return false;
            return owner <= 0 || seed < 0 || seed >= owners.Length || owners[seed] != owner
                || !counts.TryGetValue(owner, out int owned) || owned != total
                || topology.ComponentCount != 1;
        }
    }
}
