using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public static partial class SpaceCollision
    {
        internal sealed partial class Surface
        {
            private const int BlockSize = 8;
            private const int BlockShift = 3;
            private const int BlockMask = BlockSize - 1;
            private ulong[] occupancy;
            private int[] columns, rows;
            private int blocksWide;
            private int occupiedCount;
            private readonly HashSet<int> dirtyBlocks = new HashSet<int>();

            internal void ClearOccupancy(Map map)
            {
                if (columns == null || columns.Length != map.Size.x || rows.Length != map.Size.z)
                {
                    columns = new int[map.Size.x];
                    rows = new int[map.Size.z];
                    blocksWide = (columns.Length + BlockMask) >> BlockShift;
                    occupancy = new ulong[blocksWide * ((rows.Length + BlockMask) >> BlockShift)];
                }
                else
                {
                    Array.Clear(columns, 0, columns.Length);
                    Array.Clear(rows, 0, rows.Length);
                    Array.Clear(occupancy, 0, occupancy.Length);
                }
                occupiedCount = 0;
            }

            private int BlockOf(IntVec3 cell) => (cell.z >> BlockShift) * blocksWide + (cell.x >> BlockShift);

            internal void SetCell(Map map, IntVec3 cell, SpaceCellKind kind, Thing building)
            {
                int index = map.cellIndices.CellToIndex(cell);
                bool wasOccupied = kinds[index] != (byte)SpaceCellKind.Empty;
                bool isOccupied = kind != SpaceCellKind.Empty;
                kinds[index] = (byte)kind;
                buildings[index] = building;
                foundations[index] = isOccupied && (kind == SpaceCellKind.Substructure
                    || SubstructureOwnership.HasFoundation(map, cell));
                if (wasOccupied == isOccupied) return;
                occupancy[BlockOf(cell)] ^= 1UL << (((cell.z & BlockMask) << BlockShift) | (cell.x & BlockMask));
                int delta = isOccupied ? 1 : -1;
                columns[cell.x] += delta;
                rows[cell.z] += delta;
                occupiedCount += delta;
            }

            internal void RefreshBlocks(Map map, HashSet<IntVec3> changed)
            {
                if (changed == null || changed.Count == 0) return;
                foreach (IntVec3 cell in changed)
                    if (cell.InBounds(map)) dirtyBlocks.Add(BlockOf(cell));
                foreach (int block in dirtyBlocks)
                {
                    int x0 = (block % blocksWide) << BlockShift;
                    int z0 = (block / blocksWide) << BlockShift;
                    int x1 = Math.Min(x0 + BlockSize, columns.Length);
                    int z1 = Math.Min(z0 + BlockSize, rows.Length);
                    for (int z = z0; z < z1; z++)
                    for (int x = x0; x < x1; x++)
                    {
                        var cell = new IntVec3(x, 0, z);
                        var kind = SpaceStructureRules.ClassifyCell(map, cell, out Thing building);
                        SetCell(map, cell, kind, building);
                    }
                }
                dirtyBlocks.Clear();
            }

            internal IEnumerable<IntVec3> OccupiedCells(CellRect? window = null)
            {
                int minX = Math.Max(0, window?.minX ?? 0);
                int minZ = Math.Max(0, window?.minZ ?? 0);
                int maxX = Math.Min(columns.Length - 1, window?.maxX ?? columns.Length - 1);
                int maxZ = Math.Min(rows.Length - 1, window?.maxZ ?? rows.Length - 1);
                if (minX > maxX || minZ > maxZ) yield break;
                for (int bz = minZ >> BlockShift; bz <= maxZ >> BlockShift; bz++)
                for (int bx = minX >> BlockShift; bx <= maxX >> BlockShift; bx++)
                {
                    ulong bits = occupancy[bz * blocksWide + bx];
                    if (bits == 0) continue;
                    int x0 = bx << BlockShift;
                    for (int z = bz << BlockShift; bits != 0 && z <= maxZ; z++, bits >>= BlockSize)
                    {
                        if (z < minZ) continue;
                        uint line = (uint)(bits & 0xff);
                        for (int x = x0; line != 0 && x <= maxX; x++, line >>= 1)
                            if (x >= minX && (line & 1) != 0) yield return new IntVec3(x, 0, z);
                    }
                }
            }

            internal void UpdateHullBounds(Map map)
            {
                anySolid = occupiedCount != 0;
                if (!anySolid)
                {
                    boundsCenter = SpaceTransform.ShipLocalOrigin(map);
                    hullRadius = 0f;
                    return;
                }
                int minX = 0, maxX = columns.Length - 1, minZ = 0, maxZ = rows.Length - 1;
                while (columns[minX] == 0) minX++;
                while (columns[maxX] == 0) maxX--;
                while (rows[minZ] == 0) minZ++;
                while (rows[maxZ] == 0) maxZ--;
                boundsCenter = new Vector2((minX + maxX + 1) * 0.5f, (minZ + maxZ + 1) * 0.5f);
                float width = maxX - minX + 1;
                float height = maxZ - minZ + 1;
                hullRadius = 0.5f * Mathf.Sqrt(width * width + height * height);
            }
        }
    }
}
