using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static bool CaptureRoof(Map map, IntVec3 cell)
    {
        if (map.roofGrid.RoofAt(cell) != null || map.terrainGrid.FoundationAt(cell) == null)
            throw new Exception("Roof debris must follow roof removal and preserve foundation");
        return false;
    }

    static void CheckRoofs(Game game, UniverseManager universe, TerrainDef deck, TerrainDef space, RoofDef roof)
    {
        var harmony = new Harmony("DeltaV.RoofRegression");
        harmony.Patch(AccessTools.Method(typeof(SpaceProjectileService), "NotifyRoofDestroyed"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CaptureRoof)));
        game.components.Add(universe);
        var map = (Map)Raw(typeof(Map));
        map.info = new MapInfo { Size = new IntVec3(40, 1, 40) };
        map.uniqueID = 502;
        map.cellIndices = new CellIndices(map);
        map.components = new List<MapComponent>();
        map.terrainGrid = new TerrainGrid(map);
        map.thingGrid = new ThingGrid(map);
        map.edificeGrid = new EdificeGrid(map);
        map.roofGrid = new RoofGrid(map);
        map.listerThings = new ListerThings(ListerThingsUse.Global);
        Find.Maps.Add(map);
        universe.objects.Add(new MapSpaceObject(502, map, new SpacePose(Vector3.forward, 0)));
        var origin = new IntVec3(17, 0, 17);
        var natural = (RoofDef)Raw(typeof(RoofDef)); natural.isNatural = true;
        var field = (RoofDef)Raw(typeof(RoofDef));
        AccessTools.Field(typeof(MoonPoolUtility), "fieldRoofDef").SetValue(null, field);
        foreach (IntVec3 c in CellRect.WholeMap(map)) map.terrainGrid.topGrid[map.cellIndices.CellToIndex(c)] = space;
        foreach (IntVec3 c in CellRect.CenteredOn(origin, 2))
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(c)] = deck;
        Building Place(IntVec3 c, bool wall, IntVec2 size)
        {
            var def = (ThingDef)Raw(typeof(ThingDef));
            def.category = ThingCategory.Building; def.size = size;
            def.building = new BuildingProperties { isWall = wall };
            var building = (Building)Raw(typeof(Building)); building.def = def;
            Set(building, "positionInt", c); Set(building, "mapIndexOrState", (sbyte)1);
            map.thingGrid.Register(building);
            if (wall) map.edificeGrid.Register(building);
            return building;
        }
        Place(origin + IntVec3.North, true, IntVec2.One);
        Place(origin + IntVec3.East, true, IntVec2.One);
        Building furniture = Place(origin + IntVec3.South + IntVec3.East, false, IntVec2.One);
        int removed = 0, retained = 0;
        Rand.PushState(82371);
        try
        {
            for (int trial = 0; trial < 32; trial++)
            {
                foreach (IntVec3 c in CellRect.CenteredOn(origin, 2)) map.roofGrid.SetRoof(c, roof);
                map.roofGrid.SetRoof(origin + IntVec3.West, natural);
                map.roofGrid.SetRoof(origin + IntVec3.South, field);
                map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(origin)] = deck;
                SubstructureDamageUtility.TryDestroy(map, origin);
                if (map.roofGrid.RoofAt(furniture.Position) == null) removed++; else retained++;
                if (map.roofGrid.RoofAt(origin + IntVec3.North) != roof
                    || map.roofGrid.RoofAt(origin + IntVec3.East) != roof
                    || map.roofGrid.RoofAt(origin + IntVec3.North + IntVec3.East) != roof
                    || map.roofGrid.RoofAt(origin + IntVec3.West) != natural
                    || map.roofGrid.RoofAt(origin + IntVec3.South) != field
                    || map.roofGrid.RoofAt(origin + IntVec3.South * 2) != roof)
                    throw new Exception("Roof shock escaped its wall/type/radius boundaries");
            }
        }
        finally { Rand.PopState(); }
        Check(removed > 0 && retained > 0, "seeded roof shock both removes and retains nearby roofs");
        Check(furniture.Spawned && map.terrainGrid.FoundationAt(furniture.Position) == deck,
            "roof shock exposes surviving equipment without damaging it or its foundation");
        Check(true, "roof shock respects walls, closed corners, rock, force fields and one-cell radius");

        foreach (IntVec3 c in CellRect.CenteredOn(origin, 2)) map.roofGrid.SetRoof(c, roof);
        map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(origin)] = deck;
        Call(typeof(SpaceImpactEffects), "ClearCuttingRoofs", map, origin);
        Check(map.roofGrid.RoofAt(origin) == null
            && map.roofGrid.RoofAt(origin + IntVec3.West) == null
            && map.roofGrid.RoofAt(origin + IntVec3.South) == null,
            "engineering cut clears the exposed center and cardinal ceilings without a probability roll");
        Check(map.roofGrid.RoofAt(origin + IntVec3.North) == roof
            && map.roofGrid.RoofAt(origin + IntVec3.East) == roof
            && map.roofGrid.RoofAt(furniture.Position) == roof,
            "engineering cut preserves building-covered ceilings and diagonal roofs");
        SubstructureDamageUtility.TryDestroy(map, origin, scatterNearbyRoofs: false);
        Check(map.roofGrid.RoofAt(furniture.Position) == roof,
            "engineering foundation destruction does not add explosive diagonal roof damage");

        Type hull = typeof(MapOverlayRenderer).Assembly.GetType("DeltaV.ShellHullVisuals");
        Type edges = typeof(MapOverlayRenderer).Assembly.GetType("DeltaV.ShellRoofEdges");
        var edgeCell = origin + IntVec3.West;
        map.roofGrid.SetRoof(edgeCell, roof);
        Check((bool)Call(edges, "IsOpenEdge", map, edgeCell, 1), "unsupported roof edge opens toward destroyed foundation");
        map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(origin)] = deck;
        map.roofGrid.SetRoof(origin, roof);
        Check(!(bool)Call(edges, "IsOpenEdge", map, edgeCell, 1), "roof repair removes broken-edge geometry");
        map.roofGrid.SetRoof(origin, field);
        Check(!(bool)Call(hull, "HasShellRoof", map, origin), "transparent moon-pool roof does not hide buildings");
        map.roofGrid.SetRoof(origin, roof);
        Check(!(bool)Call(edges, "IsOpenEdge", map, origin, 0), "wall-supported roof edge has no exposed framing");

        var pool = Terrain("moon pool"); pool.isFoundation = true; pool.dontRender = true;
        pool.tags = new List<string> { "Substructure" };
        AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").SetValue(null, pool);
        TerrainDefOf.Substructure = deck;
        int roofIndex = map.cellIndices.CellToIndex(origin);
        map.terrainGrid.foundationGrid[roofIndex] = pool;
        map.terrainGrid.topGrid[roofIndex] = space;
        space.dontRender = true;
        Check(map.terrainGrid.TerrainAt(origin) == pool, "moon pool is the actual foundation and visible terrain");
        Check(Call(hull, "RoofSurfaceTerrain", map, origin) == deck,
            "ordinary roof over dontRender moon pool uses standard substructure material source");
        CheckTerrainSurfaces(map, origin, null, deck, true);
        Check(Call(typeof(MapOverlayRenderer), "MaterialFor", pool) == null,
            "dontRender foundation cannot leak its missing-texture material into the shell");
        map.roofGrid.SetRoof(origin, field);
        Check(Call(hull, "RoofSurfaceTerrain", map, origin) == null,
            "moon pool force field receives no opaque roof surface");
        CheckTerrainSurfaces(map, origin, null, null, true);
        var floor = Terrain("visible floor", true);
        var under = (TerrainDef[])AccessTools.Field(typeof(TerrainGrid), "underGrid").GetValue(map.terrainGrid);
        map.roofGrid.SetRoof(origin, roof);
        under[roofIndex] = space;
        map.terrainGrid.topGrid[roofIndex] = floor;
        Check(Call(hull, "RoofSurfaceTerrain", map, origin) == floor,
            "roof surface preserves actual visible floor when one is present");
        CheckTerrainSurfaces(map, origin, floor, floor, false);
        map.terrainGrid.topGrid[roofIndex] = space;
        under[roofIndex] = null;
        map.terrainGrid.foundationGrid[roofIndex] = deck;

        Building wide = Place(new IntVec3(15, 0, 15), false, new IntVec2(6, 6));
        foreach (IntVec3 c in wide.OccupiedRect())
        {
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(c)] = deck;
            map.roofGrid.SetRoof(c, roof);
        }
        Check(!(bool)Call(typeof(MapOverlayRenderer), "HasExposedDeck", wide), "fully roofed interior building remains hidden");
        var opening = new IntVec3(18, 0, 18);
        map.roofGrid.SetRoof(opening, null);
        Check((bool)Call(typeof(MapOverlayRenderer), "HasExposedDeck", wide), "partial roof loss reveals a multi-cell building");
        MapMeshFlagDefOf.Roofs = (MapMeshFlagDef)Raw(typeof(MapMeshFlagDef));
        Set(MapMeshFlagDefOf.Roofs, "mask", 8UL);
        object cache = Call(typeof(MapOverlayRenderer), "CacheFor", map);
        MapOverlayRenderer.MarkDirty(map, opening, (ulong)MapMeshFlagDefOf.Roofs);
        var dirty = (HashSet<int>)AccessTools.Field(cache.GetType(), "dirty").GetValue(cache);
        Check(dirty.SetEquals(new[] { 0, 1 | (1 << 16) }),
            "roof change invalidates just the remote building root and opening sections");
        CheckTerrainSurfaces(map, opening, deck, null, false);
        map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(opening)] = null;
        CheckTerrainSurfaces(map, opening, null, null, false);
        Check(!(bool)Call(typeof(MapOverlayRenderer), "HasExposedDeck", wide),
            "losing the only exposed foundation updates the remote building's deck classification");
        map.roofGrid.SetRoof(opening, roof);
        CheckTerrainSurfaces(map, opening, null, null, false);
        MapMeshFlagDefOf.Terrain = (MapMeshFlagDef)Raw(typeof(MapMeshFlagDef));
        Set(MapMeshFlagDefOf.Terrain, "mask", 2UL);
        dirty.Clear();
        MapOverlayRenderer.MarkDirty(map, opening, (ulong)MapMeshFlagDefOf.Terrain);
        MapOverlayRenderer.MarkDirty(map, opening, (ulong)MapMeshFlagDefOf.Roofs);
        Check(dirty.SetEquals(new[] { 0, 1 | (1 << 16) }),
            "foundation and roof notifications deduplicate the two affected sections");
        var seam = new IntVec3(16, 0, 16);
        MapOverlayRenderer.MarkDirty(map, seam, (ulong)MapMeshFlagDefOf.Terrain);
        Check(dirty.SetEquals(new[] { 0, 1, 1 << 16, 1 | (1 << 16) }),
            "terrain damage at a section corner refreshes all four sections in the neighbor ring");
        Check((int)ShellSurfaceLayer.Deck < (int)ShellSurfaceLayer.Hull
            && (int)ShellSurfaceLayer.Hull < (int)ShellSurfaceLayer.Exposed,
            "roof surface covers interior draws while exterior turret tops stay above it");
        Check(new ShellRenderConfig().layer == ShellSurfaceLayer.Hull,
            "existing rules without an explicit layer retain the hull default");
        CheckTerrainMesh();
        CheckHullCorners(map, hull, deck, roof);
        ExportRoofEdgePreview(edges);
        MapOverlayRenderer.Release(map);
    }

    private static void CheckTerrainSurfaces(Map map, IntVec3 cell, TerrainDef deck, TerrainDef cover, bool pool)
    {
        object[] args = { map, cell, null, null };
        bool result = (bool)Call(typeof(MapOverlayRenderer), "TerrainSurfacesAt", args);
        Check(result == pool && ReferenceEquals(args[2], deck) && ReferenceEquals(args[3], cover),
            $"terrain surfaces: deck={deck?.defName}, cover={cover?.defName}, pool={pool}");
    }

    private static void CheckTerrainMesh()
    {
        Type type = typeof(MapOverlayRenderer).Assembly.GetType("DeltaV.ShellTerrainBatches");
        var mesh = new LayerSubMesh(null, null);
        // MeshMakerPlanes.NewPlaneMesh(1f) in both game versions.
        Vector3[] vertices = { new(-0.5f, 0f, -0.5f), new(-0.5f, 0f, 0.5f),
            new(0.5f, 0f, 0.5f), new(0.5f, 0f, -0.5f) };
        Vector3[] normals = { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
        int[] triangles = { 0, 1, 2, 0, 2, 3 };
        IntVec3[] cells = { new(15, 0, 15), new(16, 0, 16), new(17, 0, 17) };
        const float altitude = 0.125f;
        const float yaw = 37f * MathF.PI / 180f;
        float sin = MathF.Sin(yaw), cos = MathF.Cos(yaw);
        // The offline runner has no native Unity TRS binding.
        var transform = new Matrix4x4(new Vector4(cos, 0f, -sin, 0f),
            new Vector4(0f, 1f, 0f, 0f), new Vector4(sin, 0f, cos, 0f), new Vector4(-28f, 2f, 84f, 1f));
        for (int q = 0; q < cells.Length; q++)
        {
            IntVec3 cell = cells[q];
            Call(type, "AppendCell", mesh, cell, altitude, vertices, normals, triangles);
            var center = new Vector3(cell.x + 0.5f, altitude, cell.z + 0.5f);
            Matrix4x4 legacy = transform;
            Vector3 translation = transform.MultiplyPoint3x4(center);
            legacy.m03 = translation.x;
            legacy.m13 = translation.y;
            legacy.m23 = translation.z;
            for (int i = 0; i < vertices.Length; i++)
            {
                int v = q * vertices.Length + i;
                Vector3 expected = legacy.MultiplyPoint3x4(vertices[i]);
                var uv = new Vector3((cell.x % 16 + 0.5f + vertices[i].x) * 0.0625f,
                    (cell.z % 16 + 0.5f + vertices[i].z) * 0.0625f, 0f);
                if ((transform.MultiplyPoint3x4(mesh.verts[v]) - expected).sqrMagnitude > 1e-9f
                    || !mesh.uvs[v].Equals(uv) || !mesh.normals[v].Equals(Vector3.up))
                    throw new Exception($"Batched terrain vertex differs from one-cell submission at {cell}");
            }
            for (int i = 0; i < triangles.Length; i++)
                if (mesh.tris[q * triangles.Length + i] != q * vertices.Length + triangles[i])
                    throw new Exception("Batched terrain triangle winding or offset changed");
        }
        Check(mesh.verts.Count == 12 && mesh.tris.Count == 18,
            "terrain batching preserves geometry, normals and UVs across the texture wrap and section seam");
    }

    private sealed class HullTestGraphic : Graphic
    {
        public Material atlas;
        public override Material MatSingleFor(Thing thing) => atlas;
    }

    private sealed class HullTestWall : Building
    {
        public HullTestGraphic graphic;
        public override Graphic Graphic => new Graphic_LinkedCornerFiller(graphic);
    }

    private static void CheckHullCorners(Map map, Type hull, TerrainDef deck, RoofDef roof)
    {
        ThingDefOf.GravshipHull = (ThingDef)Raw(typeof(ThingDef));
        ThingDefOf.GravshipHull.defName = "GravshipHull";
        ThingDefOf.GravshipHull.size = IntVec2.One;
        var steel = (Material)Raw(typeof(Material));
        var uranium = (Material)Raw(typeof(Material));
        var plasteel = (Material)Raw(typeof(Material));
        HullTestWall Place(IntVec3 cell, Material atlas)
        {
            var wall = new HullTestWall { def = ThingDefOf.GravshipHull,
                graphic = new HullTestGraphic { atlas = atlas } };
            Set(wall, "positionInt", cell);
            map.edificeGrid.Register(wall);
            return wall;
        }

        for (int corner = 0; corner < 4; corner++)
        {
            var floor = new IntVec3(32, 0, 5 + 7 * corner);
            var x = corner < 2 ? IntVec3.East : IntVec3.West;
            var z = corner == 0 || corner == 3 ? IntVec3.North : IntVec3.South;
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(floor)] = deck;
            map.roofGrid.SetRoof(floor, roof);
            HullTestWall xWall = Place(floor + x, steel);
            HullTestWall zWall = Place(floor + z, uranium);
            HullTestWall elbow = Place(floor + x + z, uranium);

            object adjacency = Call(hull, "Analyze", map, elbow);
            int Field(string name) => (int)AccessTools.Field(adjacency.GetType(), name).GetValue(adjacency);
            Check(Field("outerCorner") == corner, $"outer arc bows in the same direction as the inner arc ({corner})");
            Check(Call(hull, "SelectCornerWall", xWall, zWall, elbow) == zWall,
                $"two matching wall materials win the corner ({corner})");
            elbow.graphic.atlas = plasteel;
            Thing first = x.x > 0 ? zWall : z.z > 0 ? xWall : elbow;
            Check(Call(hull, "SelectCornerWall", xWall, zWall, elbow) == first,
                $"three distinct wall materials use local x/y/z order ({corner})");

            foreach (bool outer in new[] { false, true })
            {
                IntVec3 cell = outer ? elbow.Position : floor;
                var mesh = new LayerSubMesh(null, null);
                Call(hull, "PrintArcSurface", mesh, cell, corner, 0f, outer);
                float area = 0f;
                for (int i = 0; i < mesh.tris.Count; i += 3)
                {
                    Vector3 a = mesh.verts[mesh.tris[i]];
                    Vector3 b = mesh.verts[mesh.tris[i + 1]];
                    Vector3 c = mesh.verts[mesh.tris[i + 2]];
                    float doubleArea = Vector3.Cross(b - a, c - a).y;
                    if (doubleArea <= 0f) throw new Exception("Hull arc triangle faces away from the camera");
                    area += doubleArea * 0.5f;
                }
                float expected = outer ? 0.95f * 0.95f * MathF.PI / 4f : 1f - MathF.PI / 4f;
                Check(MathF.Abs(area - expected) < 0.001f,
                    $"arc clips the expected wall region ({corner}, outer={outer})");
                int sideX = x.x > 0 ? 1 : 3;
                int sideZ = z.z > 0 ? 0 : 2;
                var localSun = new Vector2(x.x * 0.6f, z.z * 0.8f);
                Type overlay = typeof(MapOverlayRenderer);
                float sign = outer ? 1f : -1f;
                Check((float)Call(overlay, "HullIncidence", localSun, sideX, !outer) == sign * 0.6f
                    && (float)Call(overlay, "HullIncidence", localSun, sideZ, !outer) == sign * 0.8f
                    && (float)Call(overlay, "HullIncidence", -localSun, sideX, !outer) == -sign * 0.6f
                    && (float)Call(overlay, "HullIncidence", -localSun, sideZ, !outer) == -sign * 0.8f,
                    $"inner and outer arc edges reverse highlights under opposite light ({corner}, outer={outer})");
            }

            map.roofGrid.SetRoof(floor, null);
            adjacency = Call(hull, "Analyze", map, elbow);
            Check(Field("outerCorner") == -1 && Field("cornerFill") == 0,
                $"unroofed open deck does not close the hull corner ({corner})");
            Place(floor, steel);
            adjacency = Call(hull, "Analyze", map, elbow);
            int fillBit = 1 << corner; // Opposite diagonal in SW/NW/NE/SE order.
            Check(Field("outerCorner") == -1 && (Field("cornerFill") & fillBit) != 0,
                $"four actual walls fill the cross even with no roof at the diagonal ({corner})");
        }
    }

    static void ExportRoofEdgePreview(Type edges)
    {
        Type builderType = edges.GetNestedType("Builder", BindingFlags.NonPublic);
        object builder = Activator.CreateInstance(builderType, true);
        var cell = new IntVec3(0, 0, 0);
        for (int side = 0; side < 4; side++) Call(edges, "AddEdge", builder, cell, side);
        var vertices = (List<Vector3>)AccessTools.Field(builderType, "vertices").GetValue(builder);
        var colors = (List<Color>)AccessTools.Field(builderType, "colors").GetValue(builder);
        var svg = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' viewBox='-0.5 -0.5 2 2' width='640' height='640'><rect x='-0.5' y='-0.5' width='2' height='2' fill='#171d25'/><rect width='1' height='1' fill='#9a9d9d'/>");
        for (int i = 0; i < vertices.Count; i += 4)
        {
            Color color = colors[i];
            svg.Append($"<polygon fill='rgb({(int)(color.r * 255)},{(int)(color.g * 255)},{(int)(color.b * 255)})' points='");
            for (int j = 0; j < 4; j++)
            {
                Vector3 v = vertices[i + j];
                if (!float.IsFinite(v.x) || !float.IsFinite(v.z) || v.x < -0.4f || v.x > 1.4f || v.z < -0.4f || v.z > 1.4f)
                    throw new Exception("Roof edge geometry escaped its local bounds");
                svg.Append(v.x.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                    .Append((1f - v.z).ToString("F4", CultureInfo.InvariantCulture)).Append(' ');
            }
            svg.Append("'/>");
        }
        svg.Append("</svg>");
        string path = Path.Combine(AppContext.BaseDirectory, "roof-edge-preview.svg");
        File.WriteAllText(path, svg.ToString());
        Check(vertices.Count > 0, "four-sided production roof geometry is finite and bounded");
        Console.WriteLine("PREVIEW " + path);
    }
}
