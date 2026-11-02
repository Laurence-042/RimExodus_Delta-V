using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Discardable roof-edge geometry; RoofGrid and real walls are the only facts.</summary>
    internal static class ShellRoofEdges
    {
        private static Material material;
        internal static Material Material => material ??= new Material(ShaderDatabase.Transparent)
        {
            name = "DeltaV_RoofFraming", mainTexture = Texture2D.whiteTexture,
            color = Color.white, renderQueue = 3100
        };

        internal static void ClearRuntimeCaches()
        {
            if (material != null) Object.Destroy(material);
            material = null;
        }

        internal static bool IsOpenEdge(Map map, IntVec3 cell, int side)
        {
            if (!ShellHullVisuals.HasShellRoof(map, cell) || HasWall(map, cell)) return false;
            IntVec3 neighbor = cell + GenAdj.CardinalDirections[side];
            return !ShellHullVisuals.HasShellRoof(map, neighbor) && !HasWall(map, neighbor);
        }

        private static bool HasWall(Map map, IntVec3 cell)
        {
            Building wall = cell.GetEdificeSafe(map);
            return wall != null && (wall.def.IsWall || wall.def.IsDoor || wall.IsAirtight);
        }

        internal static Mesh Build(Map map, CellRect rect)
        {
            var builder = new Builder();
            foreach (IntVec3 cell in rect)
                for (int side = 0; side < 4; side++)
                    if (IsOpenEdge(map, cell, side)) AddEdge(builder, cell, side);
            return builder.Finish();
        }

        private static void AddEdge(Builder builder, IntVec3 cell, int side)
        {
            IntVec3 direction = GenAdj.CardinalDirections[side];
            Vector2 outward = new Vector2(direction.x, direction.z);
            Vector2 along = new Vector2(outward.y, -outward.x);
            Vector2 edge = new Vector2(cell.x + 0.5f, cell.z + 0.5f) + outward * 0.5f;
            var dark = new Color(0.13f, 0.15f, 0.17f);
            var steel = new Color(0.43f, 0.47f, 0.49f);
            var light = new Color(0.68f, 0.70f, 0.69f);
            // The dark cross section stays inside the roof; two exposed ribs and
            // a broken cable extend into the opening, at most 0.38 cells.
            builder.Strip(edge - along * 0.5f - outward * 0.035f,
                edge + along * 0.5f - outward * 0.035f, 0.075f, dark, 0f);
            for (int rib = 0; rib < 2; rib++)
            {
                float variation = Variation(cell, side, rib);
                Vector2 root = edge + along * ((rib == 0 ? -0.29f : 0.27f) + variation * 0.07f);
                Vector2 tip = root + outward * (0.16f + variation * 0.18f)
                    + along * ((variation - 0.5f) * 0.08f);
                builder.Strip(root - outward * 0.09f, tip, 0.105f, dark, 0.001f);
                builder.Strip(root - outward * 0.085f, tip - outward * 0.014f, 0.061f, steel, 0.002f);
                builder.Strip(root - along * 0.027f, tip - along * 0.027f, 0.017f, light, 0.003f);
                builder.Strip(tip - along * 0.041f, tip + along * 0.031f + outward * 0.018f,
                    0.025f, light, 0.004f);
            }
            float bend = Variation(cell, side, 2) - 0.5f;
            Vector2 a = edge + along * (bend * 0.22f) - outward * 0.055f;
            Vector2 b = a + outward * 0.19f + along * (bend * 0.18f);
            Vector2 c = b + outward * 0.08f + along * (0.1f + bend * 0.12f);
            Vector2 d = c - outward * 0.04f + along * 0.07f;
            builder.Strip(a, b, 0.045f, dark, 0.005f);
            builder.Strip(b, c, 0.045f, dark, 0.005f);
            builder.Strip(c, d, 0.045f, dark, 0.005f);
            var cable = new Color(0.32f, 0.35f, 0.36f);
            builder.Strip(a, b, 0.018f, cable, 0.006f);
            builder.Strip(b, c, 0.018f, cable, 0.006f);
            builder.Strip(c, d, 0.018f, cable, 0.006f);
            builder.Strip(d, d + along * 0.035f, 0.024f, new Color(0.63f, 0.43f, 0.25f), 0.007f);
        }

        private static float Variation(IntVec3 cell, int side, int part)
        {
            // No gameplay RNG or per-frame randomization; saves and cache rebuilds agree.
            uint seed = unchecked((uint)(cell.x * 73856093 ^ cell.z * 19349663 ^ side * 83492791 ^ part * 1640531527));
            seed ^= seed >> 16;
            seed *= 0x7feb352du;
            seed ^= seed >> 15;
            return (seed & 65535) / 65535f;
        }

        private sealed class Builder
        {
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<Vector2> uvs = new List<Vector2>();
            private readonly List<Color> colors = new List<Color>();
            private readonly List<int> triangles = new List<int>();

            internal void Strip(Vector2 a, Vector2 b, float width, Color color, float lift)
            {
                Vector2 delta = b - a;
                Vector2 normal = new Vector2(-delta.y, delta.x).normalized * (width * 0.5f);
                float y = AltitudeLayer.BuildingOnTop.AltitudeFor() + 0.02f + lift;
                int start = vertices.Count;
                Add(a - normal); Add(a + normal); Add(b + normal); Add(b - normal);
                triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
                triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
                void Add(Vector2 point)
                {
                    vertices.Add(new Vector3(point.x, y, point.y));
                    uvs.Add(Vector2.zero);
                    colors.Add(color);
                }
            }

            internal Mesh Finish()
            {
                if (vertices.Count == 0) return null;
                var mesh = new Mesh { name = "DeltaV_RoofFraming" };
                mesh.SetVertices(vertices); mesh.SetUVs(0, uvs);
                mesh.SetColors(colors); mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
