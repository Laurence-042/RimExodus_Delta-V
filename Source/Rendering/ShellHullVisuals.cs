using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Object = UnityEngine.Object;

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
            public readonly int outerCorner;

            public Adjacency(int connected, int floorSides, int cornerFill, int outerCorner)
            {
                this.connected = connected;
                this.floorSides = floorSides;
                this.cornerFill = cornerFill;
                this.outerCorner = outerCorner;
            }
        }

        internal sealed class EdgeMeshes
        {
            public readonly Mesh[] floor = new Mesh[4];
            public readonly Mesh[] corners = new Mesh[4];
            public readonly Mesh[] outerCorners = new Mesh[4];
            public readonly Mesh[] slopes = new Mesh[4];

            public void Dispose()
            {
                for (int i = 0; i < 4; i++)
                {
                    if (floor[i] != null) Object.Destroy(floor[i]);
                    if (corners[i] != null) Object.Destroy(corners[i]);
                    if (outerCorners[i] != null) Object.Destroy(outerCorners[i]);
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

            public void AddCorner(IntVec3 cell, int corner, float altitude, float radius)
            {
                int at = vertices.Count;
                for (int i = 0; i < 4; i++)
                {
                    float u = CornerSideX(corner) == 1 ? PlaneUVs[i].x : 1f - PlaneUVs[i].x;
                    float v = CornerSideZ(corner) == 0 ? PlaneUVs[i].y : 1f - PlaneUVs[i].y;
                    Vector2 point = CornerPoint(corner, u * radius, v * radius);
                    vertices.Add(new Vector3(cell.x + point.x, altitude, cell.z + point.y));
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
        private static readonly Texture2D[] CornerGradients = new Texture2D[4];
        private static readonly Material[] CornerMaterials = new Material[4];
        private static readonly List<MaterialPropertyBlock> EdgeBlocks = new List<MaterialPropertyBlock>();
        private static int edgeBlockFrame = -1;
        private static int edgeBlockCursor;
        private static readonly Vector2[] CornerFillUVs = {
            new Vector2(0.5f, 0.6f), new Vector2(0.5f, 0.6f),
            new Vector2(0.5f, 0.6f), new Vector2(0.5f, 0.6f)
        };
        private static readonly Vector2[] PlaneUVs = {
            Vector2.zero, Vector2.up, Vector2.one, Vector2.right
        };
        // The visible outside edge of the Odyssey wall atlas is inset 0.05 cell.
        private const float OuterRadius = 0.95f;
        private const float WallSlopeWidth = 0.16f;
        private const float FloorSeamWidth = 0.65f;
        private const int ArcSegments = 24;
        private static readonly Vector2[] CornerArc = MakeCornerArc();
        // Graphic_LinkedCornerFiller uses SW, NW, NE, SE diagonal order.
        private static readonly int[] CornerCardinalMasks = { 12, 9, 3, 6 };

        internal static int CornerSideX(int corner) => corner < 2 ? 1 : 3;
        internal static int CornerSideZ(int corner) => corner == 0 || corner == 3 ? 0 : 2;

        private static int CornerOf(int sides) => sides switch
        {
            3 => 0, 6 => 1, 12 => 2, 9 => 3, _ => -1
        };

        private static Vector2 CornerPoint(int corner, float u, float v) =>
            new Vector2(CornerSideX(corner) == 1 ? u : 1f - u,
                CornerSideZ(corner) == 0 ? v : 1f - v);

        private static Vector2[] MakeCornerArc()
        {
            var arc = new Vector2[ArcSegments + 1];
            arc[0] = Vector2.right;
            arc[ArcSegments] = Vector2.up;
            for (int i = 1; i < ArcSegments; i++)
            {
                float angle = i * (Mathf.PI / 2f / ArcSegments);
                arc[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            }
            return arc;
        }

        private static Material WallAtlas(Thing wall) =>
            (wall?.Graphic as Graphic_Linked)?.SubGraphic.MatSingleFor(wall);

        internal static Material WallMaterial(Thing wall, Adjacency adjacency)
        {
            // Preserve the original atlas, shader and stuff tint. Only the
            // four-way link mask differs from the focused-map Graphic_Linked.
            Material atlas = WallAtlas(wall);
            return atlas == null ? null : MaterialAtlasPool.SubMaterialFromAtlas(atlas,
                (LinkDirections)adjacency.connected);
        }

        private static Thing HullWallAt(Map map, IntVec3 cell)
        {
            Thing wall = cell.GetEdificeSafe(map);
            return wall != null && wall.def == ThingDefOf.GravshipHull ? wall : null;
        }

        internal static Adjacency Analyze(Map map, Thing wall)
        {
            IntVec3 cell = wall.Position;
            int foundationMask = 0;
            int floorMask = 0;
            int wallMask = 0;
            for (int side = 0; side < 4; side++)
            {
                IntVec3 neighbor = cell + GenAdj.CardinalDirections[side];
                int bit = 1 << side;
                if (HullWallAt(map, neighbor) != null
                    || HullWallInsert.LinksFrom(neighbor.GetEdificeSafe(map), cell)) wallMask |= bit;
                else if (!HasShellRoof(map, neighbor)) continue;
                foundationMask |= bit;
                if ((wallMask & bit) == 0) floorMask |= bit;
            }
            int cornerFill = 0;
            if (wall.Graphic is Graphic_LinkedCornerFiller)
            {
                for (int corner = 0; corner < 4; corner++)
                {
                    IntVec3 diagonal = cell + GenAdj.DiagonalDirectionsAround[corner];
                    if ((foundationMask & CornerCardinalMasks[corner]) == CornerCardinalMasks[corner]
                        && (HullWallAt(map, diagonal) != null || HasShellRoof(map, diagonal)))
                        cornerFill |= 1 << corner;
                }
            }
            int outerCorner = -1;
            int innerCorner = CornerOf(wallMask);
            if (innerCorner >= 0 && wallMask == foundationMask)
            {
                IntVec3 inside = cell + GenAdj.CardinalDirections[CornerSideX(innerCorner)]
                    + GenAdj.CardinalDirections[CornerSideZ(innerCorner)];
                if (HullWallAt(map, inside) == null && HasShellRoof(map, inside)
                    && wall.Graphic is Graphic_Linked)
                    outerCorner = (innerCorner + 2) & 3;
            }
            // Join actual hull walls and roofed foundations. Open decks expose
            // the wall face instead of completing an imaginary roof surface.
            return new Adjacency(foundationMask, floorMask, cornerFill, outerCorner);
        }

        internal static bool HasShellRoof(Map map, IntVec3 cell) =>
            SubstructureOwnership.HasFoundation(map, cell)
            && map.roofGrid.RoofAt(cell) != null && !MoonPoolUtility.IsFieldRoofAt(map, cell);

        internal static TerrainDef RoofSurfaceTerrain(Map map, IntVec3 cell)
        {
            if (!HasShellRoof(map, cell)) return null;
            TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
            if (terrain != null && !terrain.dontRender) return terrain;
            TerrainDef foundation = SubstructureOwnership.FoundationAt(map, cell);
            // The moon pool is itself a dontRender foundation, so its ordinary
            // roof needs the standard deck material rather than its BadGraphic.
            return foundation?.dontRender == false ? foundation : TerrainDefOf.Substructure;
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

        internal static void PrintWall(SectionLayer_ThingsGeneral layer, Thing wall,
            Adjacency adjacency, Material material)
        {
            if (adjacency.outerCorner < 0)
                Printer_Plane.PrintPlane(layer, wall.TrueCenter(), Vector2.one, material);
            else
                PrintArcSurface(layer.GetSubMesh(material), wall.Position, adjacency.outerCorner,
                    wall.TrueCenter().y, outer: true);
            PrintCornerFill(layer, wall, adjacency, material);
        }

        // Clip the native material with geometry, keeping its atlas and stuff tint.
        private static void PrintArcSurface(LayerSubMesh mesh, IntVec3 cell,
            int corner, float altitude, bool outer)
        {
            int start = mesh.verts.Count;
            float radius = outer ? OuterRadius : 1f;
            AddVertex(outer ? Vector2.zero : Vector2.one);
            foreach (Vector2 point in CornerArc)
                AddVertex(point * radius);

            // Reflections flip the winding, as does using the opposite fan vertex.
            bool reverse = ((corner & 1) != 0) != outer;
            for (int i = 0; i < ArcSegments; i++)
            {
                mesh.tris.Add(start);
                mesh.tris.Add(start + i + (reverse ? 2 : 1));
                mesh.tris.Add(start + i + (reverse ? 1 : 2));
            }

            void AddVertex(Vector2 uv)
            {
                Vector2 point = CornerPoint(corner, uv.x, uv.y);
                mesh.verts.Add(new Vector3(cell.x + point.x, altitude + 0.01f * point.y,
                    cell.z + point.y));
                Vector2 sample = outer ? point : CornerFillUVs[0];
                mesh.uvs.Add(new Vector3(sample.x, sample.y, 0f));
                mesh.colors.Add(Color.white);
            }
        }

        private static Thing SelectCornerWall(Thing xWall, Thing zWall, Thing elbow)
        {
            Material x = WallAtlas(xWall), z = WallAtlas(zWall), diagonal = WallAtlas(elbow);
            if (x is not null && (ReferenceEquals(x, z) || ReferenceEquals(x, diagonal))) return xWall;
            if (z is not null && ReferenceEquals(z, diagonal)) return zWall;
            Thing first = x is not null ? xWall : null;
            if (z is not null && (first == null || Earlier(zWall.Position, first.Position))) first = zWall;
            if (diagonal is not null && (first == null || Earlier(elbow.Position, first.Position))) first = elbow;
            return first;
        }

        private static bool Earlier(IntVec3 a, IntVec3 b)
        {
            if (a.x != b.x) return a.x < b.x;
            if (a.y != b.y) return a.y < b.y;
            return a.z < b.z;
        }

        internal static EdgeMeshes BuildEdges(Map map, CellRect rect,
            Dictionary<IntVec3, Adjacency> adjacency, Func<SectionLayer_ThingsGeneral> hullLayer)
        {
            var result = new EdgeMeshes();
            var floorBuilders = new QuadBuilder[4];
            var cornerBuilders = new QuadBuilder[4];
            var outerCornerBuilders = new QuadBuilder[4];
            var slopeBuilders = new QuadBuilder[4];
            for (int i = 0; i < 4; i++)
            {
                floorBuilders[i] = new QuadBuilder();
                cornerBuilders[i] = new QuadBuilder();
                outerCornerBuilders[i] = new QuadBuilder();
                slopeBuilders[i] = new QuadBuilder();
            }
            foreach (IntVec3 cell in rect)
            {
                if (adjacency.TryGetValue(cell, out Adjacency wall))
                {
                    float slopeAltitude = AltitudeLayer.BuildingOnTop.AltitudeFor() + 0.003f;
                    if (wall.outerCorner >= 0)
                        outerCornerBuilders[wall.outerCorner].AddCorner(cell, wall.outerCorner,
                            slopeAltitude, OuterRadius);
                    else
                        for (int side = 0; side < 4; side++)
                            if ((wall.connected & (1 << side)) == 0)
                                slopeBuilders[side].Add(cell, side, WallSlopeWidth, slopeAltitude);
                    continue;
                }

                if (RoofSurfaceTerrain(map, cell) == null) continue;
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
                int corner = CornerOf(contacts);
                float altitude = AltitudeLayer.Terrain.AltitudeFor() + 0.003f;
                if (corner >= 0)
                {
                    IntVec3 x = GenAdj.CardinalDirections[CornerSideX(corner)];
                    IntVec3 z = GenAdj.CardinalDirections[CornerSideZ(corner)];
                    Thing source = SelectCornerWall(HullWallAt(map, cell + x),
                        HullWallAt(map, cell + z), HullWallAt(map, cell + x + z));
                    if (source != null)
                    {
                        Material material = MaterialAtlasPool.SubMaterialFromAtlas(WallAtlas(source),
                            LinkDirections.None);
                        PrintArcSurface(hullLayer().GetSubMesh(material), cell, corner,
                            source.TrueCenter().y, outer: false);
                        cornerBuilders[corner].AddCorner(cell, corner, altitude, 1f);
                    }
                    else corner = -1;
                }
                if (corner < 0)
                    for (int side = 0; side < 4; side++)
                        if ((contacts & (1 << side)) != 0)
                            floorBuilders[side].Add(cell, side, FloorSeamWidth, altitude);
                for (int side = 0; side < 4; side++)
                    if (ShellRoofEdges.IsOpenEdge(map, cell, side))
                        floorBuilders[side].Add(cell, side, 0.24f, altitude);
            }
            for (int side = 0; side < 4; side++)
            {
                result.floor[side] = floorBuilders[side].Finish($"DeltaV_ShellFloorEdge_{side}");
                result.corners[side] = cornerBuilders[side].Finish($"DeltaV_ShellFloorCorner_{side}");
                result.outerCorners[side] = outerCornerBuilders[side].Finish($"DeltaV_ShellOuterCorner_{side}");
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

        internal static Material CornerMaterial(int axis, bool outer)
        {
            int index = axis + (outer ? 2 : 0);
            if (CornerMaterials[index] != null) return CornerMaterials[index];
            return CornerMaterials[index] = MaterialPool.MatFrom(CornerGradient(index),
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
            block.SetColor(ShaderPropertyIDs.Color, new Color(brightness, brightness,
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
                float distance = x / 63f * FloorSeamWidth;
                edgeGradient.SetPixel(x, y, new Color(1f, 1f, 1f, EdgeFade(distance)));
            }
            edgeGradient.Apply(false, true);
        }

        private static Texture2D CornerGradient(int index)
        {
            if (CornerGradients[index] != null) return CornerGradients[index];
            var texture = new Texture2D(128, 128, TextureFormat.RGBA32, false)
            {
                name = $"DeltaV_ShellCornerGradient_{index}",
                wrapMode = TextureWrapMode.Clamp
            };
            float distanceScale = index < 2 ? 1f : OuterRadius * FloorSeamWidth / WallSlopeWidth;
            for (int x = 0; x < 128; x++)
            for (int y = 0; y < 128; y++)
            {
                float u = x / 127f, v = y / 127f;
                float distance = (1f - Mathf.Sqrt(u * u + v * v)) * distanceScale;
                float weight = (u + v) > 0f
                    ? ((index & 1) == 0 ? u : v) / (u + v) : 0.5f;
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, EdgeFade(distance) * weight));
            }
            texture.Apply(false, true);
            return CornerGradients[index] = texture;
        }

        private static float EdgeFade(float distance)
        {
            if (distance < 0f || distance >= FloorSeamWidth) return 0f;
            // The first half cell remains visible, then the final 0.15 fades.
            return distance <= 0.5f
                ? 1f - 0.55f * distance / 0.5f
                : 0.45f * (FloorSeamWidth - distance) / 0.15f;
        }

        internal static void ClearRuntimeCaches()
        {
            edgeMaterial = null;
            EdgeBlocks.Clear();
            edgeBlockFrame = -1;
            edgeBlockCursor = 0;
            if (edgeGradient != null) Object.Destroy(edgeGradient);
            edgeGradient = null;
            for (int index = 0; index < CornerGradients.Length; index++)
            {
                CornerMaterials[index] = null;
                if (CornerGradients[index] != null) Object.Destroy(CornerGradients[index]);
                CornerGradients[index] = null;
            }
        }
    }
}
