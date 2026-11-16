using System.Collections.Generic;
using RimExodus.Maps;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Supplies the shell's two terrain surfaces and tile UV convention to REF.</summary>
    internal sealed class ShellTerrainBatches
    {
        internal const int TexturePeriod = 16;
        private const float UvScale = 1f / TexturePeriod;
        private const int DeckSurface = 0;
        private const int HullSurface = 1;

        internal static float Altitude => AltitudeLayer.Terrain.AltitudeFor();

        private readonly SectionMeshBatches batches;

        internal ShellTerrainBatches(Section section) => batches = new SectionMeshBatches(section, 2);

        internal IReadOnlyList<LayerSubMesh> Deck => batches.Batches(DeckSurface);
        internal IReadOnlyList<LayerSubMesh> Hull => batches.Batches(HullSurface);

        internal void Add(IntVec3 cell, Material floor, Material cover)
        {
            var center = new Vector3(cell.x + 0.5f, Altitude, cell.z + 0.5f);
            Vector2 tile = TileUv(cell);
            if (floor != null) batches.AddPlane(DeckSurface, floor, center, tile, UvScale);
            if (cover != null) batches.AddPlane(HullSurface, cover, center, tile, UvScale);
        }

        internal void Complete() => batches.Complete();
        internal void Dispose() => batches.Dispose();

        internal static int TextureCoordinate(int coordinate)
            => ((coordinate % TexturePeriod) + TexturePeriod) % TexturePeriod;

        private static Vector2 TileUv(IntVec3 cell)
            => new Vector2(TextureCoordinate(cell.x), TextureCoordinate(cell.z));

        internal static Vector3 CellUv(IntVec3 cell, Vector3 vertex)
            => SectionMeshBatches.PlaneUv(TileUv(cell), UvScale, vertex);
    }
}
