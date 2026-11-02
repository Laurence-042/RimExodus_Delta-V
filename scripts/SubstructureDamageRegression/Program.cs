using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Xml;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

// Real production patches and vanilla terrain arrays; only map/Unity notification effects
// and auto-rebuild jobs are suppressed; building death and roof notifications are isolated.
// DLC availability is supplied by the fixture.
// This does not replace an in-game explosion test.
internal static partial class Program
{
    static int checks;
    static int breachEvents;
    static bool CaptureBreach(Map map, IntVec3 cell)
    {
        Check(map.terrainGrid.FoundationAt(cell) == null, "fragment event follows actual foundation removal");
        breachEvents++;
        return false;
    }
    static object Raw(Type type) => RuntimeHelpers.GetUninitializedObject(type);
    static void Set(object obj, string field, object value) => AccessTools.Field(obj.GetType(), field).SetValue(obj, value);
    static object Call(Type type, string name, params object[] args) => AccessTools.Method(type, name).Invoke(null, args);
    static bool Skip() => false;
    static bool SetRoofWithoutNotifications(IntVec3 c, RoofDef def, Map ___map, RoofDef[] ___roofGrid)
    { ___roofGrid[___map.cellIndices.CellToIndex(c)] = def; return false; }
    sealed class TestBuilding : Building
    {
        public int killCalls;
        public TestBuilding linked;
        public override void Kill(DamageInfo? dinfo = null, Hediff exactCulprit = null)
        {
            killCalls++;
            Check(Map.terrainGrid.FoundationAt(Position) == null,
                "foundation is gone before building death callback");
            Map.thingGrid.Deregister(this);
            Set(this, "mapIndexOrState", (sbyte)-2);
            if (linked != null && !linked.Destroyed) linked.Kill();
        }
    }
    sealed class TestFilth : Filth
    {
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            map.thingGrid.Deregister(this);
            map.listerThings.AllThings.Remove(this);
            Set(this, "mapIndexOrState", (sbyte)-2);
        }
    }
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
        try { CheckSpaceFilthInheritance(args[0]); Run(); Console.WriteLine($"PASS {checks} substructure checks"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void CheckSpaceFilthInheritance(string managed)
    {
        using var logs = Log.LockMessages();
        DeepProfiler.enabled = false;
        string data = Path.GetFullPath(Path.Combine(managed, "../..", "Data"));
        var core = new XmlDocument();
        core.Load(Path.Combine(data, "Core/Defs/TerrainDefs/Terrain_Natural.xml"));
        var odyssey = new XmlDocument();
        odyssey.Load(Path.Combine(data, "Odyssey/Defs/TerrainDefs/Terrain_Natural.xml"));
        XmlNode parent = core.SelectSingleNode("Defs/TerrainDef[@Name='NaturalTerrainBase']");
        XmlNode child = odyssey.SelectSingleNode("Defs/TerrainDef[defName='Space']");
        XmlInheritance.Clear();
        XmlInheritance.TryRegister(parent, null);
        XmlInheritance.TryRegister(child, null);
        XmlInheritance.Resolve();
        XmlNode resolved = XmlInheritance.GetResolvedNodeFor(child);
        FilthSourceFlags mask = FilthSourceFlags.None;
        foreach (XmlNode flag in resolved.SelectNodes("filthAcceptanceMask/li"))
            mask |= (FilthSourceFlags)Enum.Parse(typeof(FilthSourceFlags), flag.InnerText);
        var space = Terrain("Space");
        space.exposesToVacuum = true;
        space.filthAcceptanceMask = mask;
        var rubble = (ThingDef)Raw(typeof(ThingDef));
        rubble.filth = new FilthProperties();
        Console.WriteLine($"EVIDENCE resolved Space filth mask={mask}; rubble placement={rubble.filth.placementMask}");
        Check(FilthMaker.TerrainAcceptsFilth(space, rubble),
            "vanilla inherited Space mask accepts rubble despite vacuum blocking new filth");
        XmlInheritance.Clear();
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
        harmony.Patch(AccessTools.Method(typeof(SpaceProjectileService), "NotifyFoundationDestroyed"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CaptureBreach)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
            prefix: new HarmonyMethod(typeof(Program), nameof(SubstructureWithOdyssey)));
        harmony.Patch(AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
            prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        harmony.Patch(AccessTools.Method(typeof(RoofGrid), nameof(RoofGrid.SetRoof)),
            prefix: new HarmonyMethod(typeof(Program), nameof(SetRoofWithoutNotifications)));
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
        ThingDefOf.PilotConsole = (ThingDef)Raw(typeof(ThingDef));
        ThingDefOf.PilotConsole.defName = "PilotConsole";
        ThingDefOf.PilotConsole.shortHash = 1;
        var universe = new UniverseManager(game);
        game.components = new List<GameComponent> { universe };
        var map = (Map)Raw(typeof(Map));
        map.info = new MapInfo { Size = new IntVec3(5, 1, 5) };
        map.uniqueID = 501;
        map.cellIndices = new CellIndices(map);
        map.components = new List<MapComponent>();
        map.terrainGrid = new TerrainGrid(map);
        map.thingGrid = new ThingGrid(map);
        map.listerThings = new ListerThings(ListerThingsUse.Global);
        map.roofGrid = new RoofGrid(map);
        Find.Maps.Add(map);
        universe.objects.Add(new MapSpaceObject(501, map, new SpacePose(Vector3.forward, 0)));
        var grid = map.terrainGrid;
        var space = Terrain("space");
        space.exposesToVacuum = true;
        space.filthAcceptanceMask = FilthSourceFlags.Unnatural;
        TerrainDefOf.Space = space;
        var soil = Terrain("soil");
        soil.filthAcceptanceMask = FilthSourceFlags.Unnatural;
        var deck = Terrain("deck", true); deck.isFoundation = true;
        deck.tags = new List<string> { "Substructure" };
        var floor = Terrain("floor", true);
        var temporary = Terrain("temporary", true);
        temporary.tempTerrain = new TempTerrainProps { terrainOnRemoved = floor };
        var under = (TerrainDef[])AccessTools.Field(typeof(TerrainGrid), "underGrid").GetValue(grid);
        var temp = (TerrainDef[])AccessTools.Field(typeof(TerrainGrid), "tempGrid").GetValue(grid);
        var cell = new IntVec3(1, 0, 2);
        int index = map.cellIndices.CellToIndex(cell);
        var roof = (RoofDef)Raw(typeof(RoofDef));
        for (int i = 0; i < grid.topGrid.Length; i++) grid.topGrid[i] = space;
        var rubbleDef = (ThingDef)Raw(typeof(ThingDef));
        rubbleDef.defName = "Filth_RubbleBuilding";
        rubbleDef.category = ThingCategory.Filth;
        rubbleDef.size = new IntVec2(1, 1);
        rubbleDef.filth = new FilthProperties();
        TestFilth Rubble()
        {
            var filth = (TestFilth)Raw(typeof(TestFilth)); filth.def = rubbleDef;
            Set(filth, "positionInt", cell); Set(filth, "mapIndexOrState", (sbyte)0);
            map.thingGrid.Register(filth); map.listerThings.AllThings.Add(filth);
            return filth;
        }
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
            TestFilth rubble = Rubble();
            map.roofGrid.SetRoof(cell, roof);
            int eventsBefore = breachEvents;
            Check(SubstructureDamageUtility.TryDestroy(map, cell), "explicit destruction succeeds");
            Check(grid.FoundationAt(cell) == null && grid.TerrainAt(cell) == ground
                && grid.UnderTerrainAt(cell) == null && grid.TempTerrainAt(cell) == null
                && map.roofGrid.RoofAt(cell) == null,
                $"destroy foundation: {ground.defName}, floor={withFloor}, temp={withTemp}");
            Check(!SubstructureDamageUtility.TryDestroy(map, cell),
                "repeated destruction does not remove underlying terrain");
            Check(breachEvents == eventsBefore + 1, "one breach event per destroyed foundation, none on repeated hit");
            Check(rubble.Destroyed == (ground == space),
                $"existing rubble after destruction: ground={ground.defName}, floor={withFloor}, temp={withTemp}");
            if (!rubble.Destroyed) rubble.Destroy();
        }

        // Reuse the real queued destruction event for effects created after synchronous cleanup.
        TestFilth lateRubble = Rubble();
        AccessTools.Method(typeof(SpaceScatterService), "NotifyDestroyedCell").Invoke(universe.Scatter,
            new object[] { map, cell });
        AccessTools.Method(typeof(UniverseManager), "RunFrameActions").Invoke(universe, null);
        Check(lateRubble.Spawned, "frame cleanup preserves rubble on retained natural ground");
        lateRubble.Destroy();
        grid.topGrid[index] = space;
        lateRubble = Rubble();
        AccessTools.Method(typeof(SpaceScatterService), "NotifyDestroyedCell").Invoke(universe.Scatter,
            new object[] { map, cell });
        AccessTools.Method(typeof(UniverseManager), "RunFrameActions").Invoke(universe, null);
        Check(lateRubble.Destroyed, "frame cleanup removes late rubble on exposed space");

        Layers(space, true, true);
        var conduitDef = (ThingDef)Raw(typeof(ThingDef));
        conduitDef.defName = "hidden conduit"; conduitDef.category = ThingCategory.Building;
        conduitDef.size = new IntVec2(1, 1);
        conduitDef.building = new BuildingProperties { isPowerConduit = true, canBeDamagedByAttacks = false };
        var conduit = (TestBuilding)Raw(typeof(TestBuilding)); conduit.def = conduitDef;
        Set(conduit, "positionInt", cell); Set(conduit, "mapIndexOrState", (sbyte)0);
        map.thingGrid.Register(conduit);
        Check(SpaceStructureRules.ClassifyCell(map, cell, out var solid) == SpaceCellKind.Substructure
            && solid == null, "hidden conduit is not a collision surface protecting its foundation");
        var impact = new SpaceCollision.ImpactEvent
        {
            defender = universe.objects[0], hitCell = cell, defenderIsSubstructure = true
        };
        var damage = new ImpactOutcomePart
        {
            kind = ImpactEffectKind.DamageMap, damage = 100f, substructureBreakThreshold = 60f,
            damageDef = (DamageDef)Raw(typeof(DamageDef))
        };
        SpaceImpactEffects.ApplyToDefender(impact, damage);
        Check(grid.FoundationAt(cell) == null && grid.TerrainAt(cell) == space,
            "hidden conduit cannot block foundation destruction or layer cleanup");
        Check(conduit.Destroyed && conduit.killCalls == 1,
            "foundation destruction kills hidden conduit");
        Check(SpaceStructureRules.ClassifyCell(map, cell, out _) == SpaceCellKind.Empty,
            "remaining conduit cannot create a phantom collision after foundation loss");
        Layers(space, true, true);
        map.roofGrid.SetRoof(cell, roof);
        impact.hitCell = cell + IntVec3.West;
        impact.defenderIsSubstructure = false;
        damage.stripSubstructureRadius = 1.5f;
        damage.stripSubstructureChance = 10f;
        SpaceImpactEffects.ApplyToDefender(impact, damage);
        Check(grid.FoundationAt(cell) == null && grid.TerrainAt(cell) == space
            && map.roofGrid.RoofAt(cell) == null, "fragment effect invokes shared destruction");
        conduitDef.building.canBeDamagedByAttacks = true;
        Set(conduit, "mapIndexOrState", (sbyte)0);
        map.thingGrid.Register(conduit);
        Layers(space, true, true);
        Check(SpaceStructureRules.ClassifyCell(map, cell, out _) == SpaceCellKind.Substructure,
            "ordinary power conduits follow the same non-structural collision rule");
        var def = (ThingDef)Raw(typeof(ThingDef));
        def.defName = "wide low-cover building"; def.category = ThingCategory.Building;
        def.size = new IntVec2(2, 2); def.building = new BuildingProperties(); def.fillPercent = 0.1f;
        var building = (Building)Raw(typeof(Building)); building.def = def;
        Set(building, "positionInt", cell); Set(building, "mapIndexOrState", (sbyte)0);
        map.thingGrid.Register(building);
        foreach (var occupied in building.OccupiedRect())
        {
            grid.foundationGrid[map.cellIndices.CellToIndex(occupied)] = deck;
            Check(!SubstructureDamageUtility.CanDamage(map, occupied),
                "live multi-cell building protects " + occupied);
            SubstructureDamageUtility.TryDestroy(map, occupied);
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
        SubstructureDamageUtility.TryDestroy(map, cell);
        Check(grid.FoundationAt(cell) == deck && grid.TempTerrainAt(cell) == temporary,
            "surviving overlapping building still protects foundation after first building loss");
        map.thingGrid.Deregister(overlapping); Set(overlapping, "mapIndexOrState", (sbyte)-2);
        Check(SubstructureDamageUtility.CanDamage(map, cell),
            "building loss exposes foundation immediately");
        SubstructureDamageUtility.TryDestroy(map, cell);
        Check(grid.FoundationAt(cell) == null && grid.TerrainAt(cell) == space,
            "combat strip removes foundation and both covering layers after building loss");

        Layers(space, true, true);
        map.roofGrid.SetRoof(cell, roof);
        Set(conduit, "mapIndexOrState", (sbyte)0); map.thingGrid.Register(conduit);
        grid.Notify_FoundationDestroyed(cell);
        Check(grid.FoundationAt(cell) == null && grid.TerrainAt(cell) == space
            && grid.TempTerrainAt(cell) == null && grid.UnderTerrainAt(cell) == null,
            "native foundation destruction also clears floor beneath temporary terrain");
        Check(map.roofGrid.RoofAt(cell) == null && conduit.Destroyed,
            "native destruction adapter clears roof and conduit through shared utility");

        Layers(space, true, false);
        map.roofGrid.SetRoof(cell, roof);
        var host = (TestBuilding)Raw(typeof(TestBuilding)); host.def = def;
        var addon = (TestBuilding)Raw(typeof(TestBuilding)); addon.def = conduitDef;
        foreach (var thing in new[] { host, addon })
        {
            Set(thing, "positionInt", cell); Set(thing, "mapIndexOrState", (sbyte)0);
            map.thingGrid.Register(thing);
        }
        host.linked = addon;
        grid.RemoveFoundation(cell, false);
        Check(host.Spawned && addon.Spawned && map.roofGrid.RoofAt(cell) == roof
            && grid.TerrainAt(cell) == floor,
            "managed generic removal preserves buildings, roof and floor");
        Layers(space, true, false);
        Check(!SubstructureDamageUtility.TryDestroy(map, cell) && host.Spawned && addon.Spawned,
            "explicit destruction still respects live structural building protection");
        var wideConduitDef = (ThingDef)Raw(typeof(ThingDef));
        wideConduitDef.defName = "wide conduit";
        wideConduitDef.category = ThingCategory.Building;
        wideConduitDef.size = def.size;
        wideConduitDef.building = new BuildingProperties { isPowerConduit = true };
        host.def = wideConduitDef;
        Check(SubstructureDamageUtility.TryDestroy(map, cell), "destroy unprotected overlapping conduits");
        Check(host.Destroyed && addon.Destroyed && host.killCalls == 1 && addon.killCalls == 1,
            "destruction kills multi-cell conduit and linked overlay exactly once");
        Check(map.roofGrid.RoofAt(cell) == null,
            "destruction clears roof with overlapping buildings");

        // Transfer strips terrain only; captured buildings and roofs belong to the transfer chain.
        Layers(space, true, false);
        map.roofGrid.SetRoof(cell, roof);
        Set(host, "mapIndexOrState", (sbyte)0); map.thingGrid.Register(host);
        int beforeTransfer = breachEvents;
        grid.RemoveGravshipTerrainUnsafe(cell, index);
        Check(host.Spawned && map.roofGrid.RoofAt(cell) == roof,
            "unsafe transfer terrain removal does not invoke destructive cleanup");
        Check(breachEvents == beforeTransfer, "transfer terrain removal emits no breach fragments");
        TestFilth transferRubble = Rubble();
        AccessTools.Method(typeof(SubstructureDamageUtility), "CleanInvalidFilth")
            .Invoke(null, new object[] { map, cell });
        Check(transferRubble.Destroyed && host.Spawned,
            "transfer cleanup clears only invalid filth without destructive foundation effects");

        // No universe context: vanilla ground-map removal must keep the floor.
        game.components.Clear();
        Layers(soil, true, false);
        grid.RemoveFoundation(cell, false);
        Check(grid.FoundationAt(cell) == null && grid.TerrainAt(cell) == floor,
            "unmanaged terrain removal retains vanilla behavior");
        Check(host.Spawned && map.roofGrid.RoofAt(cell) == roof,
            "unmanaged removal preserves buildings and roof");
        CheckRoofs(game, universe, deck, space, roof);
    }
}
