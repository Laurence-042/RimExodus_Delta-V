using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Owns the static terrain meshes of one shell section.</summary>
    internal sealed class ShellTerrainBatches
    {
        internal const int TexturePeriod = 16;
        internal static float Altitude => AltitudeLayer.Terrain.AltitudeFor();

        private readonly SectionLayer_Terrain deck;
        private readonly SectionLayer_Terrain hull;
        private static Vector3[] vertices;
        private static Vector3[] normals;
        private static int[] triangles;

        internal ShellTerrainBatches(Section section)
        {
            deck = new SectionLayer_Terrain(section);
            hull = new SectionLayer_Terrain(section);
            if (vertices == null)
            {
                Mesh plane = MeshPool.plane10;
                vertices = plane.vertices;
                normals = plane.normals;
                triangles = plane.triangles;
            }
        }

        internal List<LayerSubMesh> Deck => deck.subMeshes;
        internal List<LayerSubMesh> Hull => hull.subMeshes;

        internal void Add(IntVec3 cell, Material floor, Material cover)
        {
            if (floor != null) AppendCell(deck.GetSubMesh(floor), cell, Altitude, vertices, normals, triangles);
            if (cover != null) AppendCell(hull.GetSubMesh(cover), cell, Altitude, vertices, normals, triangles);
        }

        internal void Complete()
        {
            const MeshParts parts = MeshParts.Verts | MeshParts.Tris | MeshParts.UVs1 | MeshParts.Normals;
            foreach (LayerSubMesh batch in Deck) batch.FinalizeMesh(parts);
            foreach (LayerSubMesh batch in Hull) batch.FinalizeMesh(parts);
        }

        internal void Dispose()
        {
            deck.Dispose();
            hull.Dispose();
        }

        internal static void AppendCell(LayerSubMesh batch, IntVec3 cell, float altitude,
            Vector3[] planeVertices, Vector3[] planeNormals, int[] planeTriangles)
        {
            int offset = batch.verts.Count;
            var center = new Vector3(cell.x + 0.5f, altitude, cell.z + 0.5f);
            foreach (Vector3 vertex in planeVertices)
            {
                batch.verts.Add(center + vertex);
                batch.uvs.Add(CellUv(cell, vertex));
            }
            if (planeNormals.Length == planeVertices.Length) batch.normals.AddRange(planeNormals);
            foreach (int index in planeTriangles) batch.tris.Add(offset + index);
        }

        internal static int TextureCoordinate(int coordinate)
            => ((coordinate % TexturePeriod) + TexturePeriod) % TexturePeriod;

        internal static Vector3 CellUv(IntVec3 cell, Vector3 vertex)
            => new Vector3((TextureCoordinate(cell.x) + 0.5f + vertex.x) / TexturePeriod,
                (TextureCoordinate(cell.z) + 0.5f + vertex.z) / TexturePeriod, 0f);
    }
}
