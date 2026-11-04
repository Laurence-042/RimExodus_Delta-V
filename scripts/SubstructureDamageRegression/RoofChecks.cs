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
        TerrainDefOf.Substructure = deck;
        int roofIndex = map.cellIndices.CellToIndex(origin);
        map.terrainGrid.foundationGrid[roofIndex] = pool;
        map.terrainGrid.topGrid[roofIndex] = space;
        space.dontRender = true;
        Check(map.terrainGrid.TerrainAt(origin) == pool, "moon pool is the actual foundation and visible terrain");
        Check(Call(hull, "RoofSurfaceTerrain", map, origin) == deck,
            "ordinary roof over dontRender moon pool uses standard substructure material source");
        Check(Call(typeof(MapOverlayRenderer), "MaterialFor", pool) == null,
            "dontRender foundation cannot leak its missing-texture material into the shell");
        map.roofGrid.SetRoof(origin, field);
        Check(Call(hull, "RoofSurfaceTerrain", map, origin) == null,
            "moon pool force field receives no opaque roof surface");
        var floor = Terrain("visible floor", true);
        var under = (TerrainDef[])AccessTools.Field(typeof(TerrainGrid), "underGrid").GetValue(map.terrainGrid);
        map.roofGrid.SetRoof(origin, roof);
        under[roofIndex] = space;
        map.terrainGrid.topGrid[roofIndex] = floor;
        Check(Call(hull, "RoofSurfaceTerrain", map, origin) == floor,
            "roof surface preserves actual visible floor when one is present");
        map.terrainGrid.topGrid[roofIndex] = space;
        under[roofIndex] = null;
        map.terrainGrid.foundationGrid[roofIndex] = deck;

        Building wide = Place(new IntVec3(15, 0, 15), false, new IntVec2(4, 4));
        foreach (IntVec3 c in wide.OccupiedRect())
        {
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(c)] = deck;
            map.roofGrid.SetRoof(c, roof);
        }
        Check(!(bool)Call(typeof(MapOverlayRenderer), "HasExposedDeck", wide), "fully roofed interior building remains hidden");
        var opening = new IntVec3(17, 0, 17);
        map.roofGrid.SetRoof(opening, null);
        Check((bool)Call(typeof(MapOverlayRenderer), "HasExposedDeck", wide), "partial roof loss reveals a multi-cell building");
        MapMeshFlagDefOf.Roofs = (MapMeshFlagDef)Raw(typeof(MapMeshFlagDef));
        Set(MapMeshFlagDefOf.Roofs, "mask", 8UL);
        MapOverlayRenderer.MarkDirty(map, opening, (ulong)MapMeshFlagDefOf.Roofs);
        object cache = Call(typeof(MapOverlayRenderer), "CacheFor", map);
        var dirty = (HashSet<int>)AccessTools.Field(cache.GetType(), "dirty").GetValue(cache);
        Check(dirty.Contains(0) && dirty.Contains(1 | (1 << 16)), "roof change invalidates building-root and opening sections");
        Check((int)ShellSurfaceLayer.Deck < (int)ShellSurfaceLayer.Hull
            && (int)ShellSurfaceLayer.Hull < (int)ShellSurfaceLayer.Exposed,
            "roof surface covers interior draws while exterior turret tops stay above it");
        Check(new ShellRenderConfig().layer == ShellSurfaceLayer.Hull,
            "existing rules without an explicit layer retain the hull default");
        ExportRoofEdgePreview(edges);
        MapOverlayRenderer.Release(map);
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
