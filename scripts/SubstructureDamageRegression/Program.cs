using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

// Real production patches and vanilla terrain arrays; only map/Unity notification effects
// and auto-rebuild jobs are suppressed; DLC availability is supplied by the fixture.
// This does not replace an in-game explosion test.
internal static class Program
{
    static int checks;
    static object Raw(Type type) => RuntimeHelpers.GetUninitializedObject(type);
    static void Set(object obj, string field, object value) => AccessTools.Field(obj.GetType(), field).SetValue(obj, value);
    static object Call(Type type, string name, params object[] args) => AccessTools.Method(type, name).Invoke(null, args);
    static bool Skip() => false;
    static bool SubstructureWithOdyssey(TerrainDef __instance, ref bool __result)
    { __result = __instance.HasTag("Substructure"); return false; }
    static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
    static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(args[0], name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try { Run(); Console.WriteLine($"PASS {checks} substructure checks"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static TerrainDef Terrain(string name, bool removable = false)
    {
        var def = (TerrainDef)Raw(typeof(TerrainDef));
        def.defName = name;
        def.layerable = removable;
        return def;
    }
    static void Run()
    {
        using var logs = Log.LockMessages();
        DeepProfiler.enabled = false;
        var harmony = new Harmony("DeltaV.SubstructureDamageRegression");
        harmony.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
            prefix: new HarmonyMethod(typeof(Program), nameof(SubstructureWithOdyssey)));
        harmony.Patch(AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
            prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        harmony.Patch(AccessTools.Method(typeof(ThingUtility), "CheckAutoRebuildTerrainOnDestroyed"),
            prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        foreach (string name in new[] { "Patch_TerrainGrid_RemoveFoundation_Ownership",
            "Patch_SubstructureDamage", "Patch_SubstructureExplosionDamage" })
        {
            harmony.CreateClassProcessor(typeof(SpaceImpactEffects).Assembly.GetType("DeltaV." + name)).Patch();
            Check(true, name + " binds to installed vanilla methods");
        }

        var game = (Game)Raw(typeof(Game));
        Set(game, "maps", new List<Map>());
        Current.Game = game;
        var universe = new UniverseManager(game);
        game.components = new List<GameComponent> { universe };
        var map = (Map)Raw(typeof(Map));
        map.info = new MapInfo { Size = new IntVec3(5, 1, 5) };
        map.uniqueID = 501;
        map.cellIndices = new CellIndices(map);
        map.components = new List<MapComponent>();
        map.terrainGrid = new TerrainGrid(map);
        map.thingGrid = new ThingGrid(map);
        Find.Maps.Add(map);
        universe.objects.Add(new MapSpaceObject(501, map, new SpacePose(Vector3.forward, 0)));
        var grid = map.terrainGrid;
        var space = Terrain("space");
        var soil = Terrain("soil");
        var deck = Terrain("deck", true); deck.isFoundation = true;
        deck.tags = new List<string> { "Substructure" };
        var floor = Terrain("floor", true);
        var temporary = Terrain("temporary", true);
        temporary.tempTerrain = new TempTerrainProps { terrainOnRemoved = floor };
        var under = (TerrainDef[])AccessTools.Field(typeof(TerrainGrid), "underGrid").GetValue(grid);
        var temp = (TerrainDef[])AccessTools.Field(typeof(TerrainGrid), "tempGrid").GetValue(grid);
        var cell = new IntVec3(1, 0, 2);
        int index = map.cellIndices.CellToIndex(cell);
        for (int i = 0; i < grid.topGrid.Length; i++) grid.topGrid[i] = space;
        void Layers(TerrainDef ground, bool withFloor, bool withTemp)
        {
            grid.foundationGrid[index] = deck;
            grid.topGrid[index] = withFloor ? floor : ground;
            under[index] = withFloor ? ground : null;
            temp[index] = withTemp ? temporary : null;
        }
        foreach (var ground in new[] { space, soil })
        foreach (bool withFloor in new[] { false, true })
        foreach (bool withTemp in new[] { false, true })
        {
            Layers(ground, withFloor, withTemp);
            grid.RemoveFoundation(cell, false);
            Check(grid.FoundationAt(cell) == null && grid.TerrainAt(cell) == ground
                && grid.UnderTerrainAt(cell) == null && grid.TempTerrainAt(cell) == null,
                $"remove foundation: {ground.defName}, floor={withFloor}, temp={withTemp}");
        }

        Layers(space, true, true);
        var def = (ThingDef)Raw(typeof(ThingDef));
        def.defName = "wide low-cover building"; def.category = ThingCategory.Building;
        def.size = new IntVec2(2, 2); def.building = new BuildingProperties(); def.fillPercent = 0.1f;
        var building = (Building)Raw(typeof(Building)); building.def = def;
        Set(building, "positionInt", cell); Set(building, "mapIndexOrState", (sbyte)0);
        map.thingGrid.Register(building);
        foreach (var occupied in building.OccupiedRect())
        {
            grid.foundationGrid[map.cellIndices.CellToIndex(occupied)] = deck;
            Check(!(bool)Call(typeof(SpaceImpactEffects), "CanDamageSubstructure", map, occupied),
                "live multi-cell building protects " + occupied);
            Call(typeof(SpaceImpactEffects), "StripSubstructureAt", map, occupied);
            grid.Notify_FoundationDestroyed(occupied);
            Check(grid.FoundationAt(occupied) == deck, "direct, fragment and native destruction retain protected foundation");
        }
        var worker = new DamageWorker();
        worker.def = (DamageDef)Raw(typeof(DamageDef)); DamageDefOf.Bomb = worker.def;
        var explosion = (Explosion)Raw(typeof(Explosion)); Set(explosion, "mapIndexOrState", (sbyte)0);
        AccessTools.Method(typeof(DamageWorker), "ExplosionDamageTerrain").Invoke(worker, new object[] { explosion, cell });
        Check(grid.TempTerrainAt(cell) == temporary && grid.TopTerrainAt(cell) == floor,
            "native explosion terrain phase leaves layers below surviving building intact");

        // Exercise the real collision candidate selector after this building missed its hit roll.
        Type surfaceType = typeof(SpaceCollision).GetNestedType("Surface", BindingFlags.NonPublic);
        object surface = Raw(surfaceType);
        var kinds = new byte[25]; var buildings = new Thing[25];
        kinds[index] = (byte)SpaceCellKind.Building; buildings[index] = building;
        Set(surface, "kinds", kinds); Set(surface, "buildings", buildings);
        var body = new SpaceCollision.SweptBody();
        var point = new Vector2(cell.x + 0.5f, cell.z + 0.5f);
        object[] findArgs = { body, map, surface, point, point, Vector2.up,
            new HashSet<Thing> { building }, null, IntVec3.Invalid, SpaceCellKind.Empty, null };
        Check(!(bool)Call(typeof(SpaceCollision), "FindBestCell", findArgs),
            "missed building does not expose the foundation to the same sweep");

        var overlapping = (Building)Raw(typeof(Building)); overlapping.def = def;
        Set(overlapping, "positionInt", cell); Set(overlapping, "mapIndexOrState", (sbyte)0);
        map.thingGrid.Register(overlapping);
        map.thingGrid.Deregister(building); Set(building, "mapIndexOrState", (sbyte)-2);
        Call(typeof(SpaceImpactEffects), "StripSubstructureAt", map, cell);
        Check(grid.FoundationAt(cell) == deck && grid.TempTerrainAt(cell) == temporary,
            "surviving overlapping building still protects foundation after first building loss");
        map.thingGrid.Deregister(overlapping); Set(overlapping, "mapIndexOrState", (sbyte)-2);
        Check((bool)Call(typeof(SpaceImpactEffects), "CanDamageSubstructure", map, cell),
            "building loss exposes foundation immediately");
        Call(typeof(SpaceImpactEffects), "StripSubstructureAt", map, cell);
        Check(grid.FoundationAt(cell) == null && grid.TerrainAt(cell) == space,
            "combat strip removes foundation and both covering layers after building loss");

        Layers(space, true, true);
        grid.Notify_FoundationDestroyed(cell);
        Check(grid.FoundationAt(cell) == null && grid.TerrainAt(cell) == space
            && grid.TempTerrainAt(cell) == null && grid.UnderTerrainAt(cell) == null,
            "native foundation destruction also clears floor beneath temporary terrain");

        // No universe context: vanilla ground-map removal must keep the floor.
        Current.Game = null;
        Layers(soil, true, false);
        grid.RemoveFoundation(cell, false);
        Check(grid.FoundationAt(cell) == null && grid.TerrainAt(cell) == floor,
            "unmanaged terrain removal retains vanilla behavior");
    }
}
