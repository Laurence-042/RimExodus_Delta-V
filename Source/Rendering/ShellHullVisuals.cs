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
            public readonly Mesh[] corners = new Mesh[4];
            public readonly Mesh[] slopes = new Mesh[4];

            public void Dispose()
            {
                for (int i = 0; i < 4; i++)
                {
                    if (floor[i] != null) Object.Destroy(floor[i]);
                    if (corners[i] != null) Object.Destroy(corners[i]);
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

            public void AddCorner(IntVec3 cell, int corner, float altitude)
            {
                int at = vertices.Count;
                float x = cell.x, z = cell.z;
                vertices.Add(new Vector3(x, altitude, z));
                vertices.Add(new Vector3(x, altitude, z + 1f));
                vertices.Add(new Vector3(x + 1f, altitude, z + 1f));
                vertices.Add(new Vector3(x + 1f, altitude, z));
                bool east = corner == 0 || corner == 1;
                bool north = corner == 0 || corner == 3;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 vertex = vertices[at + i];
                    // (u,v) grows from the inner floor corner toward the
                    // east/west and north/south walls of this radius-one arc.
                    float u = east ? vertex.x - x : x + 1f - vertex.x;
                    float v = north ? vertex.z - z : z + 1f - vertex.z;
                    uvs.Add(new Vector2(u, v));
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
        private static Material edgeMaterial;
        private static readonly Texture2D[] CornerGradients = new Texture2D[2];
        private static readonly Material[] CornerMaterials = new Material[2];
        private static readonly int ShaderColor = Shader.PropertyToID("_Color");
        private static readonly List<MaterialPropertyBlock> EdgeBlocks = new List<MaterialPropertyBlock>();
        private static int edgeBlockFrame = -1;
        private static int edgeBlockCursor;
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
            var cornerBuilders = new QuadBuilder[4];
            var slopeBuilders = new QuadBuilder[4];
            for (int i = 0; i < 4; i++)
            {
                floorBuilders[i] = new QuadBuilder();
                cornerBuilders[i] = new QuadBuilder();
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
                int contacts = 0;
                for (int side = 0; side < 4; side++)
                {
                    IntVec3 neighbor = cell + GenAdj.CardinalDirections[side];
                    int facing = (side + 2) & 3;
                    if (adjacency.TryGetValue(neighbor, out Adjacency wallNeighbor)
                        && (wallNeighbor.floorSides & (1 << facing)) != 0)
                        contacts |= 1 << side;
                }
                // A radius-one fillet needs a whole floor cell. Three or four
                // contacts cannot fit several such fillets, so keep their
                // original straight seams.
                int corner = contacts switch { 3 => 0, 6 => 1, 12 => 2, 9 => 3, _ => -1 };
                float altitude = AltitudeLayer.Terrain.AltitudeFor() + 0.003f;
                if (corner >= 0)
                    cornerBuilders[corner].AddCorner(cell, corner, altitude);
                else
                    for (int side = 0; side < 4; side++)
                        if ((contacts & (1 << side)) != 0)
                            floorBuilders[side].Add(cell, side, 0.65f, altitude);
            }
            for (int side = 0; side < 4; side++)
            {
                result.floor[side] = floorBuilders[side].Finish($"DeltaV_ShellFloorEdge_{side}");
                result.corners[side] = cornerBuilders[side].Finish($"DeltaV_ShellFloorCorner_{side}");
                result.slopes[side] = slopeBuilders[side].Finish($"DeltaV_ShellBeamSlope_{side}");
            }
            return result;
        }

        // Local X/Z light from the world sun at the object's orbital position.
        internal static Vector2 LocalSun(SpacePose pose)
        {
            Vector3 radial = pose.position.normalized;
            Vector3 sun = GenCelestial.CurSunPositionInWorldSpace().normalized;
            Vector3 tangentSun = sun - radial * Vector3.Dot(radial, sun);
            OrbitSphere.Basis(radial, out Vector3 east, out Vector3 north);
            Vector2 tangent = new Vector2(Vector3.Dot(tangentSun, east),
                Vector3.Dot(tangentSun, north));
            Vector2 local = SpaceTransform.RotateInverse(tangent, pose.angle);
            const float fallbackStrength = 0.25f;
            float magnitude = local.magnitude;
            if (magnitude >= fallbackStrength) return local;

            // Only near the subsolar/antisolar points is the tangent direction
            // ill-defined. Keep a low-contrast ship-local direction there, and
            // blend its angle into the real sun direction without a hard flip.
            const float fallbackAngle = 45f;
            float blend = Mathf.SmoothStep(0f, 1f, magnitude / fallbackStrength);
            float realAngle = magnitude > 1e-6f
                ? Mathf.Atan2(local.y, local.x) * Mathf.Rad2Deg : fallbackAngle;
            float angle = Mathf.LerpAngle(fallbackAngle, realAngle, blend) * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * fallbackStrength;
        }

        internal static Material EdgeMaterial
        {
            get
            {
                EnsureGradient();
                return edgeMaterial ??= MaterialPool.MatFrom(edgeGradient,
                    ShaderDatabase.Transparent, Color.white);
            }
        }

        internal static Material CornerMaterial(int axis)
        {
            if (CornerMaterials[axis] != null) return CornerMaterials[axis];
            EnsureCornerGradients();
            return CornerMaterials[axis] = MaterialPool.MatFrom(CornerGradients[axis],
                ShaderDatabase.Transparent, Color.white);
        }

        internal static MaterialPropertyBlock EdgeTint(float incidence, bool floor)
        {
            // CommandBuffer may read the block after later shells have queued
            // their edges, so each command keeps its own block until next frame.
            if (edgeBlockFrame != Time.frameCount)
            {
                edgeBlockFrame = Time.frameCount;
                edgeBlockCursor = 0;
            }
            while (EdgeBlocks.Count <= edgeBlockCursor)
                EdgeBlocks.Add(new MaterialPropertyBlock());
            MaterialPropertyBlock block = EdgeBlocks[edgeBlockCursor++];
            // The tint changes sign at zero; opacity also reaches zero there,
            // so the composited seam brightness varies continuously.
            float brightness = incidence >= 0f ? 1f : 0f;
            float opacity = (floor ? 0.85f : 0.35f) * Mathf.Clamp01(Mathf.Abs(incidence));
            block.SetColor(ShaderColor, new Color(brightness, brightness,
                brightness, opacity));
            return block;
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
                float distance = x / 63f * 0.65f;
                edgeGradient.SetPixel(x, y, new Color(1f, 1f, 1f, EdgeFade(distance)));
            }
            edgeGradient.Apply(false, true);
        }

        private static void EnsureCornerGradients()
        {
            if (CornerGradients[0] != null) return;
            for (int axis = 0; axis < 2; axis++)
            {
                var texture = new Texture2D(128, 128, TextureFormat.RGBA32, false)
                {
                    name = $"DeltaV_ShellCornerGradient_{axis}",
                    wrapMode = TextureWrapMode.Clamp
                };
                for (int x = 0; x < 128; x++)
                for (int y = 0; y < 128; y++)
                {
                    float u = x / 127f, v = y / 127f;
                    float distance = 1f - Mathf.Sqrt(u * u + v * v);
                    float weight = (u + v) > 0f
                        ? (axis == 0 ? u : v) / (u + v) : 0.5f;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f,
                        EdgeFade(distance) * weight));
                }
                texture.Apply(false, true);
                CornerGradients[axis] = texture;
            }
        }

        private static float EdgeFade(float distance)
        {
            if (distance < 0f || distance >= 0.65f) return 0f;
            // The first half cell remains visible, then the final 0.15 fades.
            return distance <= 0.5f
                ? 1f - 0.55f * distance / 0.5f
                : 0.45f * (0.65f - distance) / 0.15f;
        }

        internal static void ClearRuntimeCaches()
        {
            edgeMaterial = null;
            EdgeBlocks.Clear();
            edgeBlockFrame = -1;
            edgeBlockCursor = 0;
            if (edgeGradient != null) Object.Destroy(edgeGradient);
            edgeGradient = null;
            for (int axis = 0; axis < 2; axis++)
            {
                CornerMaterials[axis] = null;
                if (CornerGradients[axis] != null) Object.Destroy(CornerGradients[axis]);
                CornerGradients[axis] = null;
            }
        }
    }
}
