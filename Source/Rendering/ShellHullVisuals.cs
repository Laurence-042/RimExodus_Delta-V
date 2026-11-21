using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Object = UnityEngine.Object;

namespace DeltaV
{
    /// <summary>GravshipHull art and wall boundary geometry for nonfocused shells.</summary>
    internal static class ShellHullVisuals
    {
        internal readonly struct Adjacency
        {
            public readonly int connected;
            public readonly int floorSides;
            public readonly int atlasLinks;
            public readonly int cornerFill;
            public readonly int outerCorner;
            public readonly int angledSides;
            public readonly int insetDoorSides;

            public Adjacency(int connected, int floorSides, int atlasLinks, int cornerFill,
                int outerCorner, int angledSides, int insetDoorSides)
            {
                this.connected = connected;
                this.floorSides = floorSides;
                this.atlasLinks = atlasLinks;
                this.cornerFill = cornerFill;
                this.outerCorner = outerCorner;
                this.angledSides = angledSides;
                this.insetDoorSides = insetDoorSides;
            }
        }

        internal sealed class EdgeMeshes
        {
            public readonly Mesh[] floor = new Mesh[4];
            public readonly Mesh[] corners = new Mesh[4];
            public readonly Mesh[] outerCorners = new Mesh[4];
            public readonly Mesh[] slopes = new Mesh[4];
            public readonly Mesh[] angledSlopes = new Mesh[4];

            public void Dispose()
            {
                for (int i = 0; i < 4; i++)
                {
                    if (floor[i] != null) Object.Destroy(floor[i]);
                    if (corners[i] != null) Object.Destroy(corners[i]);
                    if (outerCorners[i] != null) Object.Destroy(outerCorners[i]);
                    if (slopes[i] != null) Object.Destroy(slopes[i]);
                    if (angledSlopes[i] != null) Object.Destroy(angledSlopes[i]);
                }
            }
        }

        private sealed class QuadBuilder
        {
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<Vector2> uvs = new List<Vector2>();
            private readonly List<int> triangles = new List<int>();

            public void Add(IntVec3 cell, int side, float width, float altitude,
                float startMiter = 0f, float endMiter = 0f, float inset = 0f, float trim = 0f)
            {
                Vector2 outward = SideNormal(side);
                Vector2 along = new Vector2(outward.y, -outward.x);
                Vector2 middle = new Vector2(cell.x + 0.5f, cell.z + 0.5f) + outward * (0.5f - inset);
                Vector2 start = middle - along * (0.5f - trim);
                Vector2 end = middle + along * (0.5f - trim);
                AddStrip(start, end, start - outward * width + along * startMiter,
                    end - outward * width - along * endMiter, altitude);
            }

            public void AddStrip(Vector2 start, Vector2 end, Vector2 innerStart,
                Vector2 innerEnd, float altitude)
            {
                int at = vertices.Count;
                vertices.Add(new Vector3(start.x, altitude, start.y));
                vertices.Add(new Vector3(end.x, altitude, end.y));
                vertices.Add(new Vector3(innerEnd.x, altitude, innerEnd.y));
                vertices.Add(new Vector3(innerStart.x, altitude, innerStart.y));
                // UV.x runs from the visible edge into the surface.
                uvs.Add(Vector2.zero);
                uvs.Add(Vector2.up);
                uvs.Add(Vector2.one);
                uvs.Add(Vector2.right);
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
        // The standard door and autodoor movers leave ten transparent pixels
        // along both long edges of the 64-pixel sprite.
        private const float StandardDoorInset = 10f / 64f;
        private const float DiagonalComponent = 0.70710678f;
        private const float SlopeMiter = (2f * DiagonalComponent - 1f) * WallSlopeWidth;
        private const float FloorSeamWidth = 0.65f;
        private const int ArcSegments = 24;
        private static readonly Vector2[] CornerArc = MakeCornerArc();
        // Graphic_LinkedCornerFiller uses SW, NW, NE, SE diagonal order.
        private static readonly int[] CornerCardinalMasks = { 12, 9, 3, 6 };

        internal static int CornerSideX(int corner) => corner < 2 ? 1 : 3;
        internal static int CornerSideZ(int corner) => corner == 0 || corner == 3 ? 0 : 2;

        internal static Vector2 SideNormal(int side)
        {
            IntVec3 direction = GenAdj.CardinalDirections[side];
            return new Vector2(direction.x, direction.z);
        }

        internal static Vector2 AngledNormal(int corner) =>
            -(SideNormal(CornerSideX(corner)) + SideNormal(CornerSideZ(corner))) * DiagonalComponent;

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
            // Preserve the original atlas, shader and stuff tint.
            Material atlas = WallAtlas(wall);
            return atlas == null ? null : MaterialAtlasPool.SubMaterialFromAtlas(atlas,
                (LinkDirections)adjacency.atlasLinks);
        }

        private static Thing HullWallAt(Map map, IntVec3 cell)
        {
            Thing wall = cell.GetEdificeSafe(map);
            return wall != null && wall.def == ThingDefOf.GravshipHull ? wall : null;
        }

        internal static bool IsWallSegment(Building building)
        {
            if (building is Building_Door door) return !door.Open;
            return building != null && (building.def == ThingDefOf.GravshipHull || building.def.IsWall
                || (building.def.building?.canPlaceOverWall == true && building.def.Fillage == FillCategory.Full));
        }

        internal static bool SharesDomain(Map map, IntVec3 cell, IntVec3 neighbor)
        {
            var topology = Current.Game?.GetComponent<UniverseManager>()?.FoundationTopology;
            var domain = topology?.DomainAt(map, cell);
            return domain != null && domain == topology.DomainAt(map, neighbor);
        }

        internal static Dictionary<IntVec3, Adjacency> AnalyzeWalls(Map map, CellRect neighborhood,
            Dictionary<IntVec3, int> angles)
        {
            var adjacency = new Dictionary<IntVec3, Adjacency>();
            foreach (IntVec3 cell in neighborhood)
            {
                Building wall = cell.GetEdificeSafe(map);
                if (IsWallSegment(wall))
                    adjacency.Add(cell, Analyze(map, cell, wall, AngledSidesAt(cell, angles)));
            }
            return adjacency;
        }

        internal static Adjacency Analyze(Map map, IntVec3 cell, Building wall, int angledSides)
        {
            bool gravshipHull = wall.def == ThingDefOf.GravshipHull;
            bool onFoundation = SubstructureOwnership.HasFoundation(map, cell);
            int connected = 0;
            int floorMask = 0;
            int atlasLinks = 0;
            int hullWallMask = 0;
            for (int side = 0; side < 4; side++)
            {
                IntVec3 neighbor = cell + GenAdj.CardinalDirections[side];
                bool sameDomain = SharesDomain(map, cell, neighbor);
                if (!CanJoinWalls(neighbor, sameDomain)) continue;
                Building other = neighbor.GetEdificeSafe(map);
                bool insert = HullWallInsert.LinksFrom(other, cell);
                bool roof = sameDomain && HasShellRoof(map, neighbor);
                bool wallSegment = insert || IsWallSegment(other);
                int bit = 1 << side;
                if (wallSegment || roof) connected |= bit;
                if (roof && !wallSegment) floorMask |= bit;
                if (!gravshipHull) continue;
                if (insert || (other != null && other.def == ThingDefOf.GravshipHull)) hullWallMask |= bit;
                if (roof || (hullWallMask & bit) != 0) atlasLinks |= bit;
            }
            int cornerFill = 0;
            if (gravshipHull && wall.Graphic is Graphic_LinkedCornerFiller)
            {
                for (int corner = 0; corner < 4; corner++)
                {
                    int sides = CornerCardinalMasks[corner];
                    if ((atlasLinks & sides) != sides) continue;
                    IntVec3 diagonal = cell + GenAdj.DiagonalDirectionsAround[corner];
                    bool sameDomain = SharesDomain(map, cell, diagonal);
                    if (!CanJoinWalls(diagonal, sameDomain)) continue;
                    if (HullWallAt(map, diagonal) != null || (sameDomain && HasShellRoof(map, diagonal)))
                        cornerFill |= 1 << corner;
                }
            }
            int outerCorner = -1;
            int innerCorner = CornerOf(hullWallMask);
            if (innerCorner >= 0 && hullWallMask == connected && angledSides == 0)
            {
                IntVec3 inside = cell + GenAdj.CardinalDirections[CornerSideX(innerCorner)]
                    + GenAdj.CardinalDirections[CornerSideZ(innerCorner)];
                if (SharesDomain(map, cell, inside)
                    && !IsWallSegment(inside.GetEdificeSafe(map)) && HasShellRoof(map, inside)
                    && wall.Graphic is Graphic_Linked)
                    outerCorner = (innerCorner + 2) & 3;
            }
            int insetDoorSides = 0;
            if (wall is Building_Door && wall.def.size == IntVec2.One
                && wall.def.graphicData?.texPath is ("Things/Building/Door/DoorSimple_Mover"
                    or "Things/Building/Door/Autodoor_Mover"))
            {
                Rot4 rotation = DoorUtility.DoorRotationAt(cell, map, wall.def.building.preferConnectingToFences);
                insetDoorSides = rotation.IsHorizontal ? 0b1010 : 0b0101;
            }
            return new Adjacency(connected, floorMask, atlasLinks, cornerFill, outerCorner,
                angledSides, insetDoorSides);

            // Walls on ordinary terrain can share an edge without a ship domain.
            bool CanJoinWalls(IntVec3 neighbor, bool sameDomain) =>
                sameDomain || (!onFoundation && !SubstructureOwnership.HasFoundation(map, neighbor));
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

        internal static void PrintCornerFill(SectionLayer layer, Thing wall,
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

        internal static void PrintWall(SectionLayer layer, Thing wall,
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

        internal static bool TryAngle(Map map, IntVec3 cell, out int corner, out bool full, out Color color)
        {
            corner = -1;
            full = false;
            color = Color.white;
            if (!cell.InBounds(map) || cell.GetEdificeSafe(map) != null
                || SubstructureOwnership.HasFoundation(map, cell)) return false;

            int sides = 0;
            for (int side = 0; side < 4; side++)
                if (HullWallAt(map, cell + GenAdj.CardinalDirections[side]) != null) sides |= 1 << side;
            corner = CornerOf(sides);
            if (corner < 0) return false;
            IntVec3 x = GenAdj.CardinalDirections[CornerSideX(corner)];
            IntVec3 z = GenAdj.CardinalDirections[CornerSideZ(corner)];
            if (!SharesDomain(map, cell + x, cell + z)) return false;
            full = HullWallAt(map, cell + x + z) != null && SharesDomain(map, cell + x, cell + x + z);
            color = HullWallAt(map, cell + z).DrawColor;
            return true;
        }

        internal static Dictionary<IntVec3, int> FindAngledCorners(Map map, CellRect rect)
        {
            var corners = new Dictionary<IntVec3, int>();
            foreach (IntVec3 cell in rect)
                if (TryAngle(map, cell, out int corner, out _, out _)) corners.Add(cell, corner);
            return corners;
        }

        private static class Materials
        {
            private const string HullArt = "Things/Building/Linked/GravshipHull/";
            private const string FoundationArt = "Terrain/Surfaces/Substructure/";
            private static readonly string[] AngleDirections = { "northeast", "southeast", "southwest", "northwest" };
            internal static readonly CachedMaterial[] FullAngles = AngleMaterials("AngledGravshipHull_");
            internal static readonly CachedMaterial[] PartialAngles = AngleMaterials("AngledGravshipHull_Partial_");
            internal static readonly CachedMaterial EastCorner = new CachedMaterial(HullArt + "SubstructureCorner_Full_east", ShaderDatabase.Transparent);
            internal static readonly CachedMaterial WestCorner = new CachedMaterial(HullArt + "SubstructureCorner_Full_west", ShaderDatabase.Transparent);
            internal static readonly CachedMaterial EastTip = new CachedMaterial(HullArt + "SubstructureCorner_Tip_east", ShaderDatabase.Transparent);
            internal static readonly CachedMaterial WestTip = new CachedMaterial(HullArt + "SubstructureCorner_Tip_west", ShaderDatabase.Transparent);
            internal static readonly CachedMaterial FlatEdge = new CachedMaterial(FoundationArt + "SubstructureBorder_Flat", ShaderDatabase.Transparent);
            internal static readonly CachedMaterial OuterEdge = new CachedMaterial(FoundationArt + "SubstructureBorder_CornerOuter", ShaderDatabase.Transparent);
            internal static readonly CachedMaterial InnerEdge = new CachedMaterial(FoundationArt + "SubstructureBorder_CornerInner", ShaderDatabase.Transparent);
            internal static readonly CachedMaterial ThreeEdges = new CachedMaterial(FoundationArt + "SubstructureBorder_UShape", ShaderDatabase.Transparent);
            internal static readonly CachedMaterial FourEdges = new CachedMaterial(FoundationArt + "SubstructureBorder_OShape", ShaderDatabase.Transparent);
            internal static readonly CachedMaterial Bottom = new CachedMaterial(FoundationArt + "SubstructureProps_Bottom", ShaderDatabase.Transparent);

            private static CachedMaterial[] AngleMaterials(string prefix)
            {
                var materials = new CachedMaterial[4];
                for (int i = 0; i < materials.Length; i++)
                    materials[i] = new CachedMaterial(HullArt + prefix + AngleDirections[i], ShaderDatabase.CutoutOverlay);
                return materials;
            }
        }

        internal static void PrintAngles(Map map, CellRect rect, SectionLayer layer)
        {
            foreach (IntVec3 cell in rect)
            {
                if (!TryAngle(map, cell, out int corner, out bool full, out Color color)) continue;
                IntVec3 offset = new IntVec3(corner >= 2 ? -1 : 0, 0, corner is 1 or 2 ? -1 : 0);
                Material material = (full ? Materials.FullAngles : Materials.PartialAngles)[corner].Material;
                float height = AltitudeLayer.BuildingOnTop.AltitudeFor();
                PrintTile(layer, material, cell + offset, height, size: 2, color: color);
                bool indoor = (cell + GenAdj.CardinalDirections[CornerSideX(corner)]).Roofed(map)
                    || (cell + GenAdj.CardinalDirections[CornerSideZ(corner)]).Roofed(map);
                if (indoor)
                    PrintTile(layer, IndoorMaterial(material), cell + offset, height, size: 2, color: color);
                if (corner is not (0 or 3)) continue;
                float borderHeight = AltitudeLayer.TerrainEdges.AltitudeFor();
                bool east = corner == 0;
                Material border = (east ? Materials.EastCorner : Materials.WestCorner).Material;
                PrintTile(layer, border, cell, borderHeight);
                if (indoor) PrintTile(layer, IndoorMaterial(border), cell, borderHeight);
                IntVec3 south = cell + IntVec3.South;
                if (SubstructureOwnership.HasFoundation(map, south)) continue;
                Material tip = (east ? Materials.EastTip : Materials.WestTip).Material;
                PrintTile(layer, tip, south, borderHeight);
                if (indoor) PrintTile(layer, IndoorMaterial(tip), south, borderHeight);
            }
        }

        private static Material IndoorMaterial(Material material) =>
            MaterialPool.MatFrom(material.mainTexture as Texture2D, ShaderDatabase.IndoorMaskMasked, material.color);

        internal static int FoundationEdges(Map map, IntVec3 cell)
        {
            if (!SubstructureOwnership.HasFoundation(map, cell)) return 0;
            int edges = 0;
            for (int side = 0; side < 4; side++)
                if (!SharesDomain(map, cell, cell + GenAdj.CardinalDirections[side]))
                    edges |= 1 << side;
            return edges;
        }

        internal static CellRect BoundarySections(Map map, CellRect cells, int radius)
        {
            const int sectionSize = 17;
            CellRect expanded = cells.ExpandedBy(radius).ClipInsideMap(map);
            return CellRect.FromLimits(expanded.minX / sectionSize, expanded.minZ / sectionSize,
                expanded.maxX / sectionSize, expanded.maxZ / sectionSize);
        }

        internal static void PrintFoundationEdges(Map map, CellRect rect, SectionLayer layer)
        {
            float height = AltitudeLayer.TerrainScatter.AltitudeFor();
            foreach (IntVec3 cell in rect)
            {
                if (!SubstructureOwnership.HasFoundation(map, cell)) continue;
                int edges = FoundationEdges(map, cell);
                switch (edges)
                {
                    case 15: PrintTile(layer, Materials.FourEdges.Material, cell, height); break;
                    case 7: PrintTile(layer, Materials.ThreeEdges.Material, cell, height, 3); break;
                    case 14: PrintTile(layer, Materials.ThreeEdges.Material, cell, height, 0); break;
                    case 13: PrintTile(layer, Materials.ThreeEdges.Material, cell, height, 1); break;
                    case 11: PrintTile(layer, Materials.ThreeEdges.Material, cell, height, 2); break;
                    case 3: PrintTile(layer, Materials.OuterEdge.Material, cell, height, 3); break;
                    case 6: PrintTile(layer, Materials.OuterEdge.Material, cell, height, 0); break;
                    case 12: PrintTile(layer, Materials.OuterEdge.Material, cell, height, 1); break;
                    case 9: PrintTile(layer, Materials.OuterEdge.Material, cell, height, 2); break;
                    default:
                        for (int side = 0; side < 4; side++)
                            if ((edges & (1 << side)) != 0)
                                PrintTile(layer, Materials.FlatEdge.Material, cell, height, (2 + side) & 3);
                        break;
                }
                for (int corner = 0; corner < 4; corner++)
                {
                    IntVec3 x = GenAdj.CardinalDirections[CornerSideX(corner)];
                    IntVec3 z = GenAdj.CardinalDirections[CornerSideZ(corner)];
                    if ((edges & CornerCardinalMasks[(corner + 2) & 3]) == 0 && !SharesDomain(map, cell, cell + x + z))
                        PrintTile(layer, Materials.InnerEdge.Material, cell, height, (3 + corner) & 3);
                }
                IntVec3 south = cell + IntVec3.South;
                if ((edges & 4) != 0 && !SubstructureOwnership.HasFoundation(map, south)
                    && (!TryAngle(map, south, out int angle, out _, out _) || angle is not (0 or 3)))
                    PrintTile(layer, Materials.Bottom.Material, south, height);
            }
        }

        private static void PrintTile(SectionLayer layer, Material material, IntVec3 cell,
            float height, int rotation = 0, float size = 1, Color? color = null)
        {
            LayerSubMesh mesh = layer.GetSubMesh(material);
            int start = mesh.verts.Count;
            for (int i = 0; i < 4; i++)
            {
                Vector2 uv = PlaneUVs[i];
                mesh.verts.Add(new Vector3(cell.x + uv.x * size, height, cell.z + uv.y * size));
                mesh.uvs.Add(PlaneUVs[(i + 4 - rotation) & 3]);
                mesh.colors.Add(color ?? Color.white);
            }
            mesh.tris.Add(start);
            mesh.tris.Add(start + 1);
            mesh.tris.Add(start + 2);
            mesh.tris.Add(start);
            mesh.tris.Add(start + 2);
            mesh.tris.Add(start + 3);
        }

        internal static int AngledSidesAt(IntVec3 wall, Dictionary<IntVec3, int> corners)
        {
            int sides = 0;
            for (int side = 0; side < 4; side++)
            {
                IntVec3 neighbor = wall + GenAdj.CardinalDirections[side];
                if (corners.TryGetValue(neighbor, out int corner)
                    && FacesIntoAngle(corner, side)) sides |= 1 << side;
            }
            return sides;
        }

        private static bool FacesIntoAngle(int corner, int fromWallSide) =>
            ((fromWallSide + 2) & 3) == ((fromWallSide & 1) == 0
                ? CornerSideZ(corner) : CornerSideX(corner));

        private static void AddStraightSlope(QuadBuilder builder, IntVec3 cell, int side,
            int hiddenSides, Dictionary<IntVec3, int> angles, float altitude, int insetDoorSides)
        {
            if ((hiddenSides & (1 << side)) != 0) return;
            int start = (side + 3) & 3, end = (side + 1) & 3;
            float inset = (insetDoorSides & (1 << side)) != 0 ? StandardDoorInset : 0f;
            float trim = (insetDoorSides & (1 << start)) != 0 ? StandardDoorInset : 0f;
            builder.Add(cell, side, WallSlopeWidth, altitude, MiterAt(start), MiterAt(end), inset, trim);

            float MiterAt(int toward)
            {
                IntVec3 neighbor = cell + GenAdj.CardinalDirections[toward];
                return (hiddenSides & (1 << toward)) != 0
                    && angles.TryGetValue(neighbor, out int corner) && FacesIntoAngle(corner, side)
                    ? SlopeMiter : 0f;
            }
        }

        private static void AddAngledSlope(QuadBuilder builder, IntVec3 cell, int corner,
            Dictionary<IntVec3, int> angles, float altitude)
        {
            Vector2 outward = AngledNormal(corner);
            Vector2 along = new Vector2(outward.y, -outward.x);
            var step = new IntVec3(Math.Sign(along.x), 0, Math.Sign(along.y));
            Vector2 half = new Vector2(step.x * 0.5f, step.z * 0.5f);
            Vector2 center = new Vector2(cell.x + 0.5f, cell.z + 0.5f);
            Vector2 start = center - half;
            Vector2 end = center + half;
            // Continuing diagonals share a perpendicular cut. At a straight wall,
            // the inner endpoints instead meet at the intersection of the two insets.
            float startMiter = ContinuesAt(cell - step) ? 0f : SlopeMiter;
            float endMiter = ContinuesAt(cell + step) ? 0f : SlopeMiter;
            builder.AddStrip(start, end, start - outward * WallSlopeWidth + along * startMiter,
                end - outward * WallSlopeWidth - along * endMiter, altitude);

            bool ContinuesAt(IntVec3 other) =>
                angles.TryGetValue(other, out int adjacent) && adjacent == corner;
        }

        internal static EdgeMeshes BuildEdges(Map map, CellRect rect,
            Dictionary<IntVec3, Adjacency> adjacency, Dictionary<IntVec3, int> angles,
            Func<SectionLayer_ThingsGeneral> hullLayer)
        {
            var result = new EdgeMeshes();
            var floorBuilders = new QuadBuilder[4];
            var cornerBuilders = new QuadBuilder[4];
            var outerCornerBuilders = new QuadBuilder[4];
            var slopeBuilders = new QuadBuilder[4];
            var angledBuilders = new QuadBuilder[4];
            for (int i = 0; i < 4; i++)
            {
                floorBuilders[i] = new QuadBuilder();
                cornerBuilders[i] = new QuadBuilder();
                outerCornerBuilders[i] = new QuadBuilder();
                slopeBuilders[i] = new QuadBuilder();
                angledBuilders[i] = new QuadBuilder();
            }
            float slopeAltitude = AltitudeLayer.BuildingOnTop.AltitudeFor() + 0.003f;
            foreach (IntVec3 cell in rect)
            {
                if (angles.TryGetValue(cell, out int angle))
                {
                    AddAngledSlope(angledBuilders[angle], cell, angle, angles, slopeAltitude);
                    continue;
                }
                if (adjacency.TryGetValue(cell, out Adjacency wall))
                {
                    if (wall.outerCorner >= 0)
                        outerCornerBuilders[wall.outerCorner].AddCorner(cell, wall.outerCorner,
                            slopeAltitude, OuterRadius);
                    else
                    {
                        for (int side = 0; side < 4; side++)
                            if ((wall.connected & (1 << side)) == 0)
                                AddStraightSlope(slopeBuilders[side], cell, side, wall.angledSides,
                                    angles, slopeAltitude, wall.insetDoorSides);
                    }
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
                    Thing xWall = HullWallAt(map, cell + x);
                    Thing zWall = HullWallAt(map, cell + z);
                    Thing source = xWall != null && zWall != null
                        ? SelectCornerWall(xWall, zWall, HullWallAt(map, cell + x + z)) : null;
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
                result.angledSlopes[side] = angledBuilders[side].Finish($"DeltaV_ShellAngledSlope_{side}");
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
