using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Nonfocused GravshipHull art. All masks and floor contacts come from one
    /// foundation/wall query; the section cache owns only derived meshes.</summary>
    internal static class ShellHullVisuals
    {
        internal readonly struct Adjacency
        {
            public readonly int connected;
            public readonly int floorSides;
            public readonly int cornerFill;

            public Adjacency(int connected, int floorSides, int cornerFill)
            {
                this.connected = connected;
                this.floorSides = floorSides;
                this.cornerFill = cornerFill;
            }
        }

        internal sealed class EdgeMeshes
        {
            public readonly Mesh[] floor = new Mesh[4];
            public readonly Mesh[] slopes = new Mesh[4];

            public void Dispose()
            {
                for (int i = 0; i < 4; i++)
                {
                    if (floor[i] != null) Object.Destroy(floor[i]);
                    if (slopes[i] != null) Object.Destroy(slopes[i]);
                }
            }
        }

        private sealed class QuadBuilder
        {
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<Vector2> uvs = new List<Vector2>();
            private readonly List<int> triangles = new List<int>();

            public void Add(IntVec3 cell, int side, float width, float altitude)
            {
                float x = cell.x;
                float z = cell.z;
                float x0 = x, x1 = x + 1f, z0 = z, z1 = z + 1f;
                switch (side)
                {
                    case 0: z0 = z1 - width; break; // north
                    case 1: x0 = x1 - width; break; // east
                    case 2: z1 = z0 + width; break; // south
                    default: x1 = x0 + width; break; // west
                }
                int at = vertices.Count;
                vertices.Add(new Vector3(x0, altitude, z0));
                vertices.Add(new Vector3(x0, altitude, z1));
                vertices.Add(new Vector3(x1, altitude, z1));
                vertices.Add(new Vector3(x1, altitude, z0));
                // UV.x is distance from the wall-facing edge, independent of
                // screen orientation. 0 is the seam; 1 is width into this cell.
                for (int i = 0; i < 4; i++)
                {
                    Vector3 vertex = vertices[at + i];
                    float distance = side switch
                    {
                        0 => cell.z + 1f - vertex.z,
                        1 => cell.x + 1f - vertex.x,
                        2 => vertex.z - cell.z,
                        _ => vertex.x - cell.x
                    };
                    uvs.Add(new Vector2(distance / width, i == 1 || i == 2 ? 1f : 0f));
                }
                triangles.Add(at); triangles.Add(at + 1); triangles.Add(at + 2);
                triangles.Add(at); triangles.Add(at + 2); triangles.Add(at + 3);
            }

            public Mesh Finish(string name)
            {
                if (vertices.Count == 0) return null;
                var mesh = new Mesh { name = name };
                mesh.SetVertices(vertices);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        private static Texture2D edgeGradient;
        private static Texture2D neutralFloorGradient;
        private static Material ambientFloorEdge;
        private static readonly Material[,,] EdgeMaterials = new Material[2, 2, 9];
        private static readonly Vector2[] CornerFillUVs = {
            new Vector2(0.5f, 0.6f), new Vector2(0.5f, 0.6f),
            new Vector2(0.5f, 0.6f), new Vector2(0.5f, 0.6f)
        };
        // Graphic_LinkedCornerFiller uses SW, NW, NE, SE diagonal order.
        private static readonly int[] CornerCardinalMasks = { 12, 9, 3, 6 };

        internal static Material WallMaterial(Thing wall, Adjacency adjacency)
        {
            // Preserve the original atlas, shader and stuff tint. Only the
            // four-way link mask differs from the focused-map Graphic_Linked.
            if (wall.Graphic is not Graphic_Linked linked) return null;
            return MaterialAtlasPool.SubMaterialFromAtlas(linked.SubGraphic.MatSingleFor(wall),
                (LinkDirections)adjacency.connected);
        }

        internal static Adjacency Analyze(Map map, Thing wall)
        {
            IntVec3 cell = wall.Position;
            int foundationMask = 0;
            int floorMask = 0;
            for (int side = 0; side < 4; side++)
            {
                IntVec3 neighbor = cell + GenAdj.CardinalDirections[side];
                int bit = 1 << side;
                if (!SubstructureOwnership.HasFoundation(map, neighbor)) continue;
                foundationMask |= bit;
                TerrainDef terrain = map.terrainGrid.TerrainAt(neighbor);
                if (terrain != null && !terrain.dontRender
                    && neighbor.GetEdificeSafe(map)?.def != ThingDefOf.GravshipHull)
                    floorMask |= bit;
            }
            int cornerFill = 0;
            if (wall.Graphic is Graphic_LinkedCornerFiller)
            {
                for (int corner = 0; corner < 4; corner++)
                    if ((foundationMask & CornerCardinalMasks[corner]) == CornerCardinalMasks[corner]
                        && SubstructureOwnership.HasFoundation(map,
                            cell + GenAdj.DiagonalDirectionsAround[corner]))
                        cornerFill |= 1 << corner;
            }
            // The shell links a wall to every neighboring foundation cell,
            // including an interior wall surrounded by four foundations.
            return new Adjacency(foundationMask, floorMask, cornerFill);
        }

        internal static void PrintCornerFill(SectionLayer_ThingsGeneral layer, Thing wall,
            Adjacency adjacency, Material material)
        {
            for (int corner = 0; corner < 4; corner++)
            {
                if ((adjacency.cornerFill & (1 << corner)) == 0) continue;
                IntVec3 diagonal = GenAdj.DiagonalDirectionsAround[corner];
                IntVec3 neighbor = wall.Position + diagonal;
                // Original CornerFiller's offset simplifies to 0.25 per axis.
                Vector3 center = wall.DrawPos + new Vector3(diagonal.x * 0.25f, 0f,
                    diagonal.z * 0.25f) + Altitudes.AltIncVect + new Vector3(0f, 0f, 0.09f);
                Vector2 size = new Vector2(0.5f, 0.5f);
                if (!neighbor.InBounds(wall.Map))
                {
                    if (neighbor.x == -1) { center.x -= 1f; size.x *= 5f; }
                    if (neighbor.z == -1) { center.z -= 1f; size.y *= 5f; }
                    if (neighbor.x == wall.Map.Size.x) { center.x += 1f; size.x *= 5f; }
                    if (neighbor.z == wall.Map.Size.z) { center.z += 1f; size.y *= 5f; }
                }
                Printer_Plane.PrintPlane(layer, center, size, material, 0f,
                    flipUv: false, CornerFillUVs);
            }
        }

        internal static EdgeMeshes BuildEdges(Map map, CellRect rect,
            Dictionary<IntVec3, Adjacency> adjacency)
        {
            var result = new EdgeMeshes();
            var floorBuilders = new QuadBuilder[4];
            var slopeBuilders = new QuadBuilder[4];
            for (int i = 0; i < 4; i++)
            {
                floorBuilders[i] = new QuadBuilder();
                slopeBuilders[i] = new QuadBuilder();
            }
            foreach (IntVec3 cell in rect)
            {
                if (adjacency.TryGetValue(cell, out Adjacency wall))
                    for (int side = 0; side < 4; side++)
                        if ((wall.connected & (1 << side)) == 0)
                            slopeBuilders[side].Add(cell, side, 0.16f,
                                AltitudeLayer.BuildingOnTop.AltitudeFor() + 0.003f);

                if (!SubstructureOwnership.HasFoundation(map, cell)
                    || cell.GetEdificeSafe(map)?.def == ThingDefOf.GravshipHull) continue;
                TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
                if (terrain == null || terrain.dontRender) continue;
                for (int side = 0; side < 4; side++)
                {
                    IntVec3 neighbor = cell + GenAdj.CardinalDirections[side];
                    int facing = (side + 2) & 3;
                    if (adjacency.TryGetValue(neighbor, out Adjacency wallNeighbor)
                        && (wallNeighbor.floorSides & (1 << facing)) != 0)
                        floorBuilders[side].Add(cell, side, 0.42f,
                            AltitudeLayer.Terrain.AltitudeFor() + 0.003f);
                }
            }
            for (int side = 0; side < 4; side++)
            {
                result.floor[side] = floorBuilders[side].Finish($"DeltaV_ShellFloorEdge_{side}");
                result.slopes[side] = slopeBuilders[side].Finish($"DeltaV_ShellBeamSlope_{side}");
            }
            return result;
        }

        // Local X/Z light from the world sun at the object's orbital position.
        internal static Vector2 LocalSun(SpacePose pose)
        {
            Vector3 radial = pose.position.normalized;
            Vector3 sun = GenCelestial.CurSunPositionInWorldSpace().normalized;
            float incidence = Vector3.Dot(radial, sun);
            if (incidence <= 0.04f) return Vector2.zero;
            OrbitSphere.Basis(radial, out Vector3 east, out Vector3 north);
            Vector2 tangent = new Vector2(Vector3.Dot(sun, east), Vector3.Dot(sun, north));
            if (tangent.sqrMagnitude < 0.001f) return Vector2.zero;
            float c = Mathf.Cos(pose.angle), s = Mathf.Sin(pose.angle);
            return new Vector2(c * tangent.x + s * tangent.y,
                -s * tangent.x + c * tangent.y) * Mathf.Clamp01(incidence * 2f);
        }

        internal static Material EdgeMaterial(float incidence, bool floor)
        {
            float strength = Mathf.Abs(incidence);
            if (strength < 0.04f) return null;
            int step = Mathf.Clamp(Mathf.CeilToInt(strength * 8f), 1, 8);
            int sign = incidence >= 0f ? 0 : 1;
            int kind = floor ? 0 : 1;
            if (EdgeMaterials[kind, sign, step] != null) return EdgeMaterials[kind, sign, step];
            EnsureGradient();
            float alphaScale = floor ? 0.18f + 0.38f * step / 8f
                : 0.24f * step / 8f;
            Color color = sign == 0 ? new Color(1f, 1f, 1f, alphaScale)
                : new Color(0f, 0f, 0f, alphaScale);
            return EdgeMaterials[kind, sign, step] = MaterialPool.MatFrom(
                edgeGradient, ShaderDatabase.Transparent, color);
        }

        internal static Material AmbientFloorEdge
        {
            get
            {
                if (ambientFloorEdge == null)
                {
                    EnsureNeutralFloorGradient();
                    ambientFloorEdge = MaterialPool.MatFrom(neutralFloorGradient,
                        ShaderDatabase.Transparent, Color.white);
                }
                return ambientFloorEdge;
            }
        }

        private static void EnsureNeutralFloorGradient()
        {
            if (neutralFloorGradient != null) return;
            neutralFloorGradient = new Texture2D(64, 2, TextureFormat.RGBA32, false)
            {
                name = "DeltaV_ShellNeutralFloorEdge", wrapMode = TextureWrapMode.Clamp
            };
            for (int x = 0; x < 64; x++)
            for (int y = 0; y < 2; y++)
            {
                float distance = x / 63f;
                Color pixel = distance < 0.18f
                    ? new Color(1f, 1f, 1f, 0.2f * Mathf.Pow(1f - distance / 0.18f, 2f))
                    : new Color(0f, 0f, 0f,
                        0.22f * Mathf.Pow(1f - (distance - 0.18f) / 0.82f, 2f));
                neutralFloorGradient.SetPixel(x, y, pixel);
            }
            neutralFloorGradient.Apply(false, true);
        }

        private static void EnsureGradient()
        {
            if (edgeGradient != null) return;
            edgeGradient = new Texture2D(64, 2, TextureFormat.RGBA32, false)
            {
                name = "DeltaV_ShellEdgeGradient", wrapMode = TextureWrapMode.Clamp
            };
            for (int x = 0; x < 64; x++)
            for (int y = 0; y < 2; y++)
            {
                float alpha = Mathf.Pow(1f - x / 63f, 2f);
                edgeGradient.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
            edgeGradient.Apply(false, true);
        }

        internal static void ClearRuntimeCaches()
        {
            ambientFloorEdge = null;
            for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            for (int k = 0; k < 9; k++) EdgeMaterials[i, j, k] = null;
            if (edgeGradient != null) Object.Destroy(edgeGradient);
            edgeGradient = null;
            if (neutralFloorGradient != null) Object.Destroy(neutralFloorGradient);
            neutralFloorGradient = null;
        }
    }
}
