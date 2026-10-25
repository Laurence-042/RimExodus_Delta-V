using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using DeltaV;
using HarmonyLib;
using Verse;

internal static class Program
{
    private static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(args[0], name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try
        {
            var harmony = new Harmony("DeltaV.MoonPoolRoofRegression");
            harmony.Patch(AccessTools.Method(typeof(RoofGrid), nameof(RoofGrid.SetRoof)),
                prefix: new HarmonyMethod(typeof(Program), nameof(SetRoofWithoutMapNotifications)));
            var patches = typeof(MoonPoolUtility).Assembly.GetTypes()
                .Where(t => t.Name.StartsWith("Patch_MoonPool_", StringComparison.Ordinal))
                .OrderBy(t => t.Name).ToArray();
            if (patches.Length < 15) throw new Exception("Moon pool roof patch classes missing");
            foreach (Type patch in patches)
            {
                var methods = harmony.CreateClassProcessor(patch).Patch();
                if (methods == null || !methods.Any()) throw new Exception("Unbound Harmony patch: " + patch.Name);
                Console.WriteLine("BOUND " + patch.Name);
            }
            CheckRoofState();
            Console.WriteLine("PASS " + patches.Length + " moon pool Harmony patch classes");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static bool SetRoofWithoutMapNotifications(RoofGrid __instance, IntVec3 c, RoofDef def)
    {
        var map = (Map)AccessTools.Field(typeof(RoofGrid), "map").GetValue(__instance);
        var roofs = (RoofDef[])AccessTools.Field(typeof(RoofGrid), "roofGrid").GetValue(__instance);
        roofs[map.cellIndices.CellToIndex(c)] = def;
        return false;
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL " + description);
        Console.WriteLine("PASS " + description);
    }

    private static void Scope(string name)
        => AccessTools.Method(typeof(MoonPoolUtility), name).Invoke(null, null);

    private static void CheckRoofState()
    {
        var moon = (TerrainDef)RuntimeHelpers.GetUninitializedObject(typeof(TerrainDef));
        var other = (TerrainDef)RuntimeHelpers.GetUninitializedObject(typeof(TerrainDef));
        var field = (RoofDef)RuntimeHelpers.GetUninitializedObject(typeof(RoofDef));
        var constructed = (RoofDef)RuntimeHelpers.GetUninitializedObject(typeof(RoofDef));
        AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").SetValue(null, moon);
        AccessTools.Field(typeof(MoonPoolUtility), "fieldRoofDef").SetValue(null, field);
        var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
        map.info = new MapInfo { Size = new IntVec3(2, 1, 2) };
        map.cellIndices = new CellIndices(2, 2);
        map.roofGrid = new RoofGrid(map);
        var terrain = (TerrainGrid)RuntimeHelpers.GetUninitializedObject(typeof(TerrainGrid));
        AccessTools.Field(typeof(TerrainGrid), "map").SetValue(terrain, map);
        terrain.topGrid = new TerrainDef[4];
        terrain.foundationGrid = new TerrainDef[4];
        AccessTools.Field(typeof(TerrainGrid), "tempGrid").SetValue(terrain, new TerrainDef[4]);
        AccessTools.Field(typeof(TerrainGrid), "underGrid").SetValue(terrain, new TerrainDef[4]);
        map.terrainGrid = terrain;
        var cell = new IntVec3(0, 0, 0);
        terrain.foundationGrid[0] = moon;
        MoonPoolUtility.SyncRoofAt(map, cell);
        Require(map.roofGrid.RoofAt(cell) == field && map.roofGrid.Roofed(cell), "field roof installs as real roof");
        Require(map.roofGrid.Roofed(0) && map.roofGrid.Roofed(0, 0), "all roof query overloads see field roof");
        Scope("BeginLighting");
        try
        {
            Require(!map.roofGrid.Roofed(cell) && !map.roofGrid.Roofed(0)
                && !map.roofGrid.Roofed(0, 0) && map.roofGrid.RoofAt(0) == null,
                "lighting sees through field roof");
        }
        finally { Scope("EndLighting"); }
        Scope("BeginShuttlePassage");
        try { Require(!map.roofGrid.Roofed(cell), "shuttle passes through moon pool"); }
        finally { Scope("EndShuttlePassage"); }
        map.roofGrid.SetRoof(cell, constructed);
        Require(map.roofGrid.RoofAt(cell) == constructed, "existing ordinary roof retained");
        Scope("BeginLighting");
        try { Require(map.roofGrid.Roofed(cell) && map.roofGrid.RoofAt(0) == constructed,
            "ordinary roof blocks daylight"); }
        finally { Scope("EndLighting"); }
        Scope("BeginShuttlePassage");
        try { Require(!map.roofGrid.Roofed(cell), "shuttle passes existing roof on moon pool"); }
        finally { Scope("EndShuttlePassage"); }
        var ordinaryCell = new IntVec3(1, 0, 0);
        terrain.topGrid[1] = other;
        map.roofGrid.SetRoof(ordinaryCell, constructed);
        Scope("BeginShuttlePassage");
        try { Require(map.roofGrid.Roofed(ordinaryCell), "ordinary roof elsewhere blocks shuttle"); }
        finally { Scope("EndShuttlePassage"); }
        map.roofGrid.SetRoof(cell, null);
        Require(map.roofGrid.RoofAt(cell) == field, "field roof returns after manual removal");
        map.roofCollapseBuffer = new RoofCollapseBuffer();
        map.roofCollapseBuffer.MarkToCollapse(cell);
        new RoofCollapseBufferResolver(map).CollapseRoofsMarkedToCollapse();
        RoofCollapserImmediate.DropRoofInCells(cell, map);
        Require(map.roofGrid.RoofAt(cell) == field && !map.roofCollapseBuffer.CellsMarkedToCollapse.Any(),
            "field roof cannot collapse");
        terrain.foundationGrid[0] = null;
        terrain.topGrid[0] = other;
        MoonPoolUtility.SyncRoofAt(map, cell);
        Require(map.roofGrid.RoofAt(cell) == null, "field roof removed with terrain");
    }
}
