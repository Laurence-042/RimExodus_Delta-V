using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using DeltaV;
using HarmonyLib;
using RimExodus.Maps;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    private static object CallInstance(object instance, string name, params object[] args)
    {
        MethodInfo method = instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length
                && candidate.GetParameters().Zip(args).All(pair => pair.Second == null
                    || pair.First.ParameterType.IsInstanceOfType(pair.Second)));
        return method.Invoke(instance, args);
    }

    private static void SetRoofFixtureFoundation(Map map, IntVec3 cell, TerrainDef foundation)
    {
        map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(cell)] = foundation;
        var universe = Current.Game.GetComponent<UniverseManager>();
        CallInstance(universe, "NotifyFoundationChanged", map, cell);
    }

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
        map.components = new List<MapComponent> { new SubstructureOwnership(map) };
        map.terrainGrid = new TerrainGrid(map);
        map.thingGrid = new ThingGrid(map);
        map.edificeGrid = new EdificeGrid(map);
        map.events = new MapEvents(map);
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
                SetRoofFixtureFoundation(map, origin, deck);
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
        SetRoofFixtureFoundation(map, origin, deck);
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
        SetRoofFixtureFoundation(map, origin, deck);
        map.roofGrid.SetRoof(origin, roof);
        Check(!(bool)Call(edges, "IsOpenEdge", map, edgeCell, 1), "roof repair removes broken-edge geometry");
        map.roofGrid.SetRoof(origin, field);
        Check((bool)Call(hull, "HasConstructedRoof", map, origin),
            "moon-pool force field participates in constructed-roof continuity");
        Check(!(bool)Call(edges, "IsOpenEdge", map, edgeCell, 1),
            "constructed roof joins a moon-pool force field without broken-edge geometry");
        Check(!(bool)Call(hull, "HasShellRoof", map, origin), "transparent moon-pool roof does not hide buildings");
        var modRoof = (RoofDef)Raw(typeof(RoofDef));
        map.roofGrid.SetRoof(origin, modRoof);
        Check((bool)Call(hull, "HasConstructedRoof", map, origin)
            && !(bool)Call(edges, "IsOpenEdge", map, edgeCell, 1),
            "third-party constructed roofs join without a hard-coded RoofDef list");
        map.roofGrid.SetRoof(origin, natural);
        Check(!(bool)Call(hull, "HasConstructedRoof", map, origin)
            && (bool)Call(edges, "IsOpenEdge", map, edgeCell, 1),
            "natural roofs do not close a constructed ship-roof edge");
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
        object snapshots = AccessTools.Field(cache.GetType(), "snapshots")?.GetValue(cache) ?? cache;
        var dirty = (HashSet<int>)AccessTools.Field(snapshots.GetType(), "dirty").GetValue(snapshots);
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
        Check(dirty.SetEquals(new[] { 0, 1, 1 << 16, 1 | (1 << 16) }),
            "foundation and roof notifications deduplicate the affected hull and building sections");
        var seam = new IntVec3(16, 0, 16);
        dirty.Clear();
        MapOverlayRenderer.MarkDirty(map, seam, (ulong)MapMeshFlagDefOf.Terrain);
        Check(dirty.SetEquals(new[] { 0, 1, 1 << 16, 1 | (1 << 16) }),
            "terrain damage at a section corner refreshes all four sections in the neighbor ring");
        Check((int)ShellSurfaceLayer.Deck < (int)ShellSurfaceLayer.Hull
            && (int)ShellSurfaceLayer.Hull < (int)ShellSurfaceLayer.Exposed,
            "roof surface covers interior draws while exterior turret tops stay above it");
        Check(new ShellRenderConfig().layer == ShellSurfaceLayer.Hull,
            "existing rules without an explicit layer retain the hull default");
        CheckTerrainMesh();
        CheckTerrainDrawOrder();
        CheckTurretDamageAlpha();
        CheckHullCorners(map, hull, deck, roof);
        CheckAngledHullSlopes(map, hull, deck, dirty);
        Building_Door door = CheckWallSegmentLighting(map, hull, deck, roof, dirty);
        CheckBuildingDamage(map, deck, roof, dirty);
        CheckOwnedDomainBoundaries(universe, deck, space, roof, hull);
        ExportRoofEdgePreview(edges);
        MapOverlayRenderer.Release(map);
        dirty.Clear();
        map.events.Notify_DoorOpened(door);
        Check(dirty.Count == 0, "released shell cache no longer receives door events");
        Check(!ReferenceEquals(cache, Call(typeof(MapOverlayRenderer), "CacheFor", map)),
            "reopened map starts with a fresh render cache");
        MapOverlayRenderer.Release(map);
    }

    private static void CheckOwnedDomainBoundaries(UniverseManager universe, TerrainDef deck,
        TerrainDef space, RoofDef roof, Type hull)
    {
        var map = (Map)Raw(typeof(Map));
        map.info = new MapInfo { Size = new IntVec3(68, 1, 34) };
        map.uniqueID = 820;
        map.cellIndices = new CellIndices(map);
        map.components = new List<MapComponent> { new SubstructureOwnership(map) };
        map.terrainGrid = new TerrainGrid(map);
        map.thingGrid = new ThingGrid(map);
        map.edificeGrid = new EdificeGrid(map);
        map.linkGrid = new LinkGrid(map);
        map.roofGrid = new RoofGrid(map);
        map.events = new MapEvents(map);
        map.listerThings = new ListerThings(ListerThingsUse.Global);
        map.mapDrawer = new MapDrawer(map);
        Find.Maps.Add(map);
        var frame = new MapSpaceObject(820, map, new SpacePose(Vector3.forward, 0));
        universe.objects.Add(frame);
        Array.Fill(map.terrainGrid.topGrid, space);
        MapMeshFlagDefOf.Buildings ??= new MapMeshFlagDef();
        MapMeshFlagDefOf.Things ??= new MapMeshFlagDef();
        var a = new IntVec3(16, 0, 8);
        var b = a + IntVec3.East;
        SetRoofFixtureFoundation(map, a, deck);
        SetRoofFixtureFoundation(map, b, deck);
        var ordinaryWall = (ThingDef)Raw(typeof(ThingDef));
        ordinaryWall.category = ThingCategory.Building;
        ordinaryWall.size = IntVec2.One;
        ordinaryWall.building = new BuildingProperties { isWall = true };
        ordinaryWall.graphicData = new GraphicData { linkFlags = LinkFlags.Wall };
        Building PlaceOrdinaryWall(IntVec3 cell)
        {
            var wall = new Building { def = ordinaryWall };
            RegisterWall(wall, cell);
            return wall;
        }
        void RegisterWall(Building wall, IntVec3 cell)
        {
            Set(wall, "positionInt", cell);
            Set(wall, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
            map.thingGrid.Register(wall);
            map.edificeGrid.Register(wall);
            map.linkGrid.Notify_LinkerCreatedOrDestroyed(wall);
        }
        Building first = PlaceOrdinaryWall(a);
        Building second = PlaceOrdinaryWall(b);
        var owner = SubstructureOwnership.For(map);
        Type patch = typeof(MapOverlayRenderer).Assembly.GetType("DeltaV.Patch_ShipHullRendering");
        var harmony = new Harmony("DeltaV.RoofRegression.OwnedBoundaries");
        // The native queries need the DLC flag, but the fixture creates no Unity materials or capture layers.
        var skipInitializer = new HarmonyMethod(typeof(Program), nameof(Skip));
        harmony.Patch(typeof(ModsConfig).TypeInitializer, prefix: skipInitializer);
        harmony.Patch(typeof(SectionLayer_SubstructureProps).TypeInitializer, prefix: skipInitializer);
        harmony.Patch(typeof(GravshipCapturer).TypeInitializer, prefix: skipInitializer);
        FieldInfo odyssey = AccessTools.Field(typeof(ModsConfig), "odysseyActive");
        odyssey.SetValue(null, true);
        foreach (Type nested in patch.GetNestedTypes(BindingFlags.NonPublic).Where(t => t.IsDefined(typeof(HarmonyPatch), false)))
        {
            // Capture uses Unity native rendering calls and cannot be JIT-patched by the .NET fixture.
            if (nested.Name == "CaptureWalls")
            {
                Check(AccessTools.Method(typeof(GravshipCapturer), nameof(GravshipCapturer.BeginGravshipRender),
                    new[] { typeof(Building_GravEngine), typeof(Action<Capture>) }) != null,
                    "native capture entry accepts the original engine and completion callback");
                continue;
            }
            Check(harmony.CreateClassProcessor(nested).Patch().Count == 1, $"owned shell Harmony target resolves ({nested.Name})");
        }
        foreach (MethodInfo drawer in new[]
        {
            AccessTools.DeclaredMethod(typeof(Graphic_LinkedCornerFiller), nameof(Graphic_LinkedCornerFiller.Print)),
            AccessTools.DeclaredMethod(typeof(SectionLayer_GravshipHull), nameof(SectionLayer_GravshipHull.Regenerate)),
            AccessTools.DeclaredMethod(typeof(SectionLayer_SubstructureProps), nameof(SectionLayer_SubstructureProps.Regenerate))
        })
            Check(drawer != null && Harmony.GetPatchInfo(drawer)?.Owners.Contains(harmony.Id) != true,
                $"focused meshes use the original drawer ({drawer?.DeclaringType.Name})");

        var props = (SectionLayer_SubstructureProps)Raw(typeof(SectionLayer_SubstructureProps));
        Set(props, "map", map);
        var shouldDrawProps = AccessTools.Method(typeof(SectionLayer_SubstructureProps), "ShouldDrawPropsOn");
        var linker = new Graphic_LinkedCornerFiller(new HullTestGraphic());
        (int sides, int corners) NativeEdges(IntVec3 cell)
        {
            object[] arguments = { cell, map.terrainGrid, null, null };
            shouldDrawProps.Invoke(props, arguments);
            return (Convert.ToInt32(arguments[2]), Convert.ToInt32(arguments[3]));
        }
        var index = AccessTools.Property(typeof(UniverseManager), "ShipStructure").GetValue(universe);
        CallInstance(index, "For", map);
        object cache = Call(typeof(MapOverlayRenderer), "CacheFor", map);
        object snapshots = AccessTools.Field(cache.GetType(), "snapshots")?.GetValue(cache) ?? cache;
        var dirty = (HashSet<int>)AccessTools.Field(snapshots.GetType(), "dirty").GetValue(snapshots);
        bool Joined(IntVec3 first, IntVec3 second) => (bool)Call(hull, "SharesDomain", map, first, second);
        int Edge(IntVec3 cell) => (int)Call(hull, "FoundationEdges", map, cell);
        int Connected(Building wall) => AdjacencyField(
            Call(hull, "Analyze", map, wall.Position, wall, 0), "connected");
        try
        {
            Check(Joined(a, b) && (Edge(a) & 2) == 0 && (Edge(b) & 8) == 0,
                "unassigned continuous foundation shares one domain and no internal edge");
            Check((Connected(first) & 2) != 0 && (Connected(second) & 8) != 0,
                "same-domain wall segments omit the shared shell edge");
            Check(linker.ShouldLinkWith(b, first) && linker.ShouldLinkWith(a, second)
                && (NativeEdges(a).sides & 2) == 0 && (NativeEdges(b).sides & 8) == 0,
                "native focused wall and foundation edges join across a same-domain section boundary");
            IntVec3 roofCell = a + IntVec3.North;
            SetRoofFixtureFoundation(map, roofCell, deck);
            map.roofGrid.SetRoof(roofCell, roof);
            Check(!linker.ShouldLinkWith(roofCell, first), "a same-domain roof cannot link a native focused wall");
            map.roofGrid.SetRoof(roofCell, null);
            SetRoofFixtureFoundation(map, roofCell, null);
            Check(!Joined(a, a + IntVec3.North), "empty space is distinct from unassigned foundation");
            dirty.Clear();
            CallInstance(owner, "Assign", b, 811);
            Check(!Joined(a, b) && (Edge(a) & 2) != 0 && (Edge(b) & 8) != 0
                && dirty.SetEquals(new[] { 0, 1 }),
                "pure reassignment introduces a shared-domain seam and only dirties the two affected sections");
            Check((Connected(first) & 2) == 0 && (Connected(second) & 8) == 0,
                "adjacent walls across ownership domains each keep their shell edge");
            Check(!linker.ShouldLinkWith(b, first) && !linker.ShouldLinkWith(a, second)
                && (NativeEdges(a).sides & 2) != 0 && (NativeEdges(b).sides & 8) != 0,
                "native focused wall and foundation edges preserve the ownership seam");
            Type strategicCapture = patch.GetNestedType("StrategicCapture", BindingFlags.NonPublic);
            Call(strategicCapture, "Prefix");
            try
            {
                Check(linker.ShouldLinkWith(b, first) && (NativeEdges(a).sides & 2) == 0,
                    "strategic capture reads the original wall links and foundation borders");
            }
            finally { Call(strategicCapture, "Finalizer"); }
            dirty.Clear();
            CallInstance(owner, "Assign", b, 811);
            Check(dirty.Count == 0, "stable ownership produces no render invalidation");

            SetRoofFixtureFoundation(map, b, null);
            Check((Connected(first) & 2) == 0 && (Connected(second) & 8) == 0,
                "losing foundation under one wall leaves a shell edge at the hull boundary");
            SetRoofFixtureFoundation(map, a, null);
            Check(!Joined(a, b) && Connected(first) == 2 && Connected(second) == 8
                && linker.ShouldLinkWith(b, first) && linker.ShouldLinkWith(a, second),
                "ordinary walls without ship foundations share the internal edge and keep exposed edges");

            var inner = new IntVec3(35, 0, 20);
            foreach (IntVec3 cell in CellRect.CenteredOn(inner, 1))
                SetRoofFixtureFoundation(map, cell, deck);
            Check(NativeEdges(inner) == (0, 0), "native borders omit cardinal and diagonal trims inside a domain");
            CallInstance(owner, "Assign", inner + IntVec3.NorthEast, 811);
            Check(NativeEdges(inner) == (0, 4), "native inner corner sees the diagonal ownership seam");

            var angle = new IntVec3(53, 0, 8);
            HullTestWall Wall(IntVec3 cell)
            {
                SetRoofFixtureFoundation(map, cell, deck);
                var wall = new HullTestWall { def = ThingDefOf.GravshipHull, graphic = new HullTestGraphic() };
                RegisterWall(wall, cell);
                return wall;
            }
            SectionLayer_GravshipHull.CornerType NativeAngle()
            {
                bool found = SectionLayer_GravshipHull.ShouldDrawCornerPiece(
                    angle, map, map.terrainGrid, out var type, out _);
                if (found != (type != SectionLayer_GravshipHull.CornerType.None))
                    throw new Exception("Native angle result and variant disagree");
                return type;
            }
            IntVec3 north = angle + IntVec3.North, east = angle + IntVec3.East;
            Wall(north); Wall(east);
            IntVec3 bridge = north + IntVec3.East;
            SetRoofFixtureFoundation(map, bridge, deck);
            object[] inputs = { map, angle, -1, false, null };
            Check((bool)Call(hull, "TryAngle", inputs) && (int)inputs[2] == 0 && !(bool)inputs[3]
                && NativeAngle() == SectionLayer_GravshipHull.CornerType.Diagonal_NE,
                "both draw paths admit a partial diagonal across same-domain walls");
            SetRoofFixtureFoundation(map, bridge, null);
            dirty.Clear();
            CallInstance(index, "NotifyCell", map, bridge, Enum.Parse(
                typeof(MapOverlayRenderer).Assembly.GetType("DeltaV.ShipStructureAspect"), "Foundation"),
                deck, null, 0, 0);
            Check(!(bool)Call(hull, "TryAngle", inputs)
                && NativeAngle() == SectionLayer_GravshipHull.CornerType.None && dirty.SetEquals(new[] { 3 }),
                "foundation break removes the diagonal between same-owner fragments and refreshes only that section");

            HullTestWall diagonal = Wall(bridge);
            Check(NativeAngle() == SectionLayer_GravshipHull.CornerType.Corner_NE
                && linker.ShouldLinkWith(north, diagonal) && !linker.ShouldLinkWith(east + IntVec3.East, diagonal),
                "native geometry selects the full variant with a third joined hull wall");
            foreach (IntVec3 cell in new[]
            {
                north + IntVec3.North, bridge + IntVec3.North, bridge + IntVec3.NorthEast,
                bridge + IntVec3.East, east + IntVec3.East
            })
                SetRoofFixtureFoundation(map, cell, deck);
            CallInstance(owner, "Assign", bridge, 811);
            Check(!linker.ShouldLinkWith(east, diagonal)
                && NativeAngle() == SectionLayer_GravshipHull.CornerType.Diagonal_NE,
                "native geometry uses the partial variant when only the diagonal wall is in another domain");
        }
        finally
        {
            odyssey.SetValue(null, false);
            harmony.UnpatchAll(harmony.Id);
            MapOverlayRenderer.Release(map);
            CallInstance(universe, "ForgetFoundationTopology", map);
            universe.objects.Remove(frame);
            Find.Maps.Remove(map);
        }
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
        MethodInfo baselineAppend = AccessTools.Method(type, "AppendCell");
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
            var center = new Vector3(cell.x + 0.5f, altitude, cell.z + 0.5f);
            if (baselineAppend != null)
                baselineAppend.Invoke(null, new object[] { mesh, cell, altitude, vertices, normals, triangles });
            else
                Call(typeof(SectionMeshBatches), "AppendPlane", mesh, center, Call(type, "TileUv", cell),
                    0.0625f, vertices, normals, triangles);
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
                    || !mesh.uvs[v].Equals(uv) || !mesh.normals[v].Equals(Vector3.up)
                    || !((Vector3)Call(type, "CellUv", cell, vertices[i])).Equals(uv))
                    throw new Exception($"Batched terrain vertex differs from one-cell submission at {cell}");
            }
            for (int i = 0; i < triangles.Length; i++)
                if (mesh.tris[q * triangles.Length + i] != q * vertices.Length + triangles[i])
                    throw new Exception("Batched terrain triangle winding or offset changed");
        }
        Check(mesh.verts.Count == 12 && mesh.tris.Count == 18,
            "terrain batching preserves geometry, normals and UVs across the texture wrap and section seam");
    }

    private static void CheckTerrainDrawOrder()
    {
        Type overlay = typeof(MapOverlayRenderer);
        Type commandType = overlay.GetNestedType("OverlayCommand", BindingFlags.NonPublic);
        object Command(ShellSurfaceLayer layer, int queue, int sequence, bool damage = false)
        {
            object command = Activator.CreateInstance(commandType);
            Set(command, "surface", layer);
            Set(command, "queue", queue);
            Set(command, "sequence", sequence);
            Set(command, "damageOverlay", damage);
            return command;
        }

        object firstDeck = Command(ShellSurfaceLayer.Deck, 3000, 2);
        object equipment = Command(ShellSurfaceLayer.Deck, 3000, 3);
        object roof = Command(ShellSurfaceLayer.Hull, 2990, 1);
        object exposed = Command(ShellSurfaceLayer.Exposed, 2980, 0);
        object moonPoolDamage = Command(ShellSurfaceLayer.Deck, 2980, 0, damage: true);
        object exposedDamage = Command(ShellSurfaceLayer.Exposed, 2960, 0, damage: true);
        Check((int)Call(overlay, "CompareCommands", firstDeck, equipment) < 0
            && (int)Call(overlay, "CompareCommands", equipment, moonPoolDamage) < 0
            && (int)Call(overlay, "CompareCommands", moonPoolDamage, roof) < 0
            && (int)Call(overlay, "CompareCommands", roof, exposed) < 0,
            "moon pool equipment and damage draw above the deck and below opaque roof cover");
        Check((int)Call(overlay, "CompareCommands", exposed, exposedDamage) < 0,
            "an exposed device draws before its damage even when the damage material has a lower queue");
    }

    private sealed class HullTestGraphic : Graphic
    {
        public Material atlas;
        public override Material MatSingleFor(Thing thing) => atlas;
    }

    private static void CheckTurretDamageAlpha()
    {
        const int size = 8;
        Type overlay = typeof(MapOverlayRenderer);
        Type textureType = overlay.GetNestedType("TexturePixels", BindingFlags.NonPublic);
        object Texture(Color32[] pixels, int width, int height) =>
            Activator.CreateInstance(textureType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new object[] { pixels, width, height }, null);

        var rect = new Rect(10f, 20f, 4f, 2f);
        var scratch = new LayerSubMesh(null, null);
        scratch.verts.AddRange(new[]
        {
            new Vector3(10f, 1f, 20.5f), new Vector3(10f, 1f, 21.5f),
            new Vector3(14f, 1f, 21.5f), new Vector3(14f, 1f, 20.5f)
        });
        scratch.uvs.AddRange(new[]
        {
            new Vector3(0f, 0f, 0f), new Vector3(0f, 1f, 0f),
            new Vector3(1f, 1f, 0f), new Vector3(1f, 0f, 0f)
        });
        var marks = new Color32[size * size];
        var white = new Color32(255, 255, 255, 255);
        object scratchTexture = Texture(new[] { new Color32(90, 60, 30, 192) }, 1, 1);
        for (int i = 0; i < 2; i++)
            Call(overlay, "RasterizeTopDamageQuad", marks, size, rect, scratch, 0, scratchTexture, white);
        int row = size * 3;
        Check(marks[row].a == 239 && marks[row + size - 1].a == 239 && marks[0].a == 0,
            "native-style damage quads span the four-cell projection and preserve scratch blending");

        var body = new Color32[size * size];
        for (int i = 0; i < body.Length; i++)
            body[i].a = (byte)(i % size == 3 ? 0 : i % size == 4 ? 128 : 255);
        Check((bool)Call(overlay, "MaskTopDamage", marks, size, Texture(body, size, size))
            && marks[row].a == 239 && marks[row + size - 1].a == 239 && marks[row + 3].a == 0
            && marks[row + 4].a == 120 && marks[row + 4].r == 90,
            "body alpha is applied once after scratch blending, with exact transparent and partial coverage");
        Check(!(bool)Call(overlay, "MaskTopDamage", marks, size,
            Texture(new[] { new Color32(255, 255, 255, 0) }, 1, 1)) && marks.All(pixel => pixel.a == 0),
            "fully transparent art cannot emit a damage decal");
    }

    private sealed class HullTestWall : Building
    {
        public HullTestGraphic graphic;
        public override Graphic Graphic => new Graphic_LinkedCornerFiller(graphic);
    }

    private sealed class DamageTestBuilding : Building
    {
        public override Graphic Graphic => null;
        public override void Print(SectionLayer layer) { }
    }

    private static readonly Dictionary<Building, SectionLayer_BuildingsDamage> PrintedDamage =
        new Dictionary<Building, SectionLayer_BuildingsDamage>();

    private static bool CaptureBuildingDamage(SectionLayer_BuildingsDamage __instance, Building b)
    {
        PrintedDamage.Add(b, __instance);
        return false;
    }

    private static bool DamageFixtureMaxHitPoints(Thing __instance, ref int __result)
    {
        if (__instance is not DamageTestBuilding) return true;
        __result = 100;
        return false;
    }

    private static bool SkipDamageFixtureTerrain(Map map, IntVec3 cell, out Material deck, out Material cover)
    {
        deck = cover = null;
        return false;
    }

    private static IEnumerable<CodeInstruction> SkipDamageFixtureMeshPasses(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo odyssey = AccessTools.PropertyGetter(typeof(ModsConfig), nameof(ModsConfig.OdysseyActive));
        MethodInfo terrain = AccessTools.Method(typeof(MapOverlayRenderer), "TerrainMaterialsAt");
        MethodInfo skipTerrain = AccessTools.Method(typeof(Program), nameof(SkipDamageFixtureTerrain));
        int replacedDlc = 0, replacedTerrain = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(odyssey))
            {
                instruction.opcode = OpCodes.Ldc_I4_0;
                instruction.operand = null;
                replacedDlc++;
            }
            else if (instruction.Calls(terrain))
            {
                instruction.operand = skipTerrain;
                replacedTerrain++;
            }
            yield return instruction;
        }
        Check(replacedDlc == 1 && replacedTerrain == 1,
            "offline damage fixture bypasses the installed mod loader and terrain mesh pass");
    }

    private static void CheckBuildingDamage(Map map, TerrainDef deck, RoofDef roof, HashSet<int> dirty)
    {
        DamageTestBuilding Place(ThingDef def, int z, int x = 34)
        {
            def.category = ThingCategory.Building;
            def.size = IntVec2.One;
            def.useHitPoints = def.drawDamagedOverlay = true;
            def.drawerType = DrawerType.MapMeshOnly;
            var building = new DamageTestBuilding { def = def };
            Set(building, "positionInt", new IntVec3(x, 0, z));
            Set(building, "mapIndexOrState", (sbyte)1);
            Set(building, "hitPointsInt", 42);
            map.thingGrid.Register(building);
            return building;
        }

        ThingDefOf.GravshipHull.building ??= new BuildingProperties { isWall = true };
        Building bulkhead = Place(ThingDefOf.GravshipHull, 35);
        map.edificeGrid.Register(bulkhead);
        var wallDef = (ThingDef)Raw(typeof(ThingDef));
        wallDef.defName = "ordinary wall";
        wallDef.building = new BuildingProperties { isWall = true };
        Building wall = Place(wallDef, 36);
        var equipmentDef = (ThingDef)Raw(typeof(ThingDef));
        equipmentDef.defName = "roof test equipment";
        equipmentDef.building = new BuildingProperties();
        Building exposed = Place(equipmentDef, 37);
        Building covered = Place(equipmentDef, 38);
        var thrusterDef = (ThingDef)Raw(typeof(ThingDef));
        thrusterDef.defName = "LargeThruster";
        thrusterDef.building = new BuildingProperties { isAirtight = true };
        Building thruster = Place(thrusterDef, 35, x: 36);
        var turretDef = (ThingDef)Raw(typeof(ThingDef));
        turretDef.defName = "roof test turret";
        turretDef.building = new BuildingProperties { turretTopDrawSize = 2.4f };
        Building turret = Place(turretDef, 36, x: 36);
        var shieldDef = (ThingDef)Raw(typeof(ThingDef));
        shieldDef.defName = "roof test shield";
        shieldDef.building = new BuildingProperties { turretTopDrawSize = 1.2f };
        Building shield = Place(shieldDef, 37, x: 36);

        foreach (Building building in new[] { covered, thruster, turret, shield })
        {
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(building.Position)] = deck;
            map.roofGrid.SetRoof(building.Position, roof);
        }
        map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(exposed.Position)] = MoonPoolUtility.MoonPoolTerrain;
        map.roofGrid.SetRoof(exposed.Position, MoonPoolUtility.FieldRoof);

        MapMeshFlagDefOf.BuildingsDamage = (MapMeshFlagDef)Raw(typeof(MapMeshFlagDef));
        Set(MapMeshFlagDefOf.BuildingsDamage, "mask", 16UL);
        foreach (Building building in new[] { bulkhead, wall, exposed })
        {
            dirty.Clear();
            MapOverlayRenderer.MarkDirty(map, building.Position, (ulong)MapMeshFlagDefOf.BuildingsDamage);
            Check(dirty.SetEquals(new[] { 1 | (2 << 16), 2 | (2 << 16) }),
                "vanilla damage notifications refresh the root and cross-section neighbor");
        }
        dirty.Clear();
        MapOverlayRenderer.MarkDirty(map, bulkhead.Position + IntVec3.West, (ulong)MapMeshFlagDefOf.BuildingsDamage);
        Check(dirty.Count == 0, "empty cells do not invalidate the shell on a damage-only notification");

        var overlay = typeof(MapOverlayRenderer);
        var rules = (Dictionary<ThingDef, ShellRenderRuleDef>)AccessTools.Field(overlay, "Rules").GetValue(null);
        var topConfig = new ShellRenderConfig { source = ShellGraphicSource.TurretTop, layer = ShellSurfaceLayer.Exposed };
        rules.Add(thrusterDef, new ShellRenderRuleDef { render = new ShellRenderConfig { zIndex = 1f } });
        rules.Add(turretDef, new ShellRenderRuleDef { render = topConfig });
        rules.Add(shieldDef, new ShellRenderRuleDef { render = topConfig });
        var harmony = new Harmony("DeltaV.ShellDamageRegression");
        harmony.Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.MaxHitPoints)),
            prefix: new HarmonyMethod(typeof(Program), nameof(DamageFixtureMaxHitPoints)));
        harmony.Patch(AccessTools.Method(typeof(SectionLayer_BuildingsDamage), "PrintDamageVisualsFrom"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CaptureBuildingDamage)));
        harmony.Patch(AccessTools.Method(overlay, "PopulateSection"),
            transpiler: new HarmonyMethod(typeof(Program), nameof(SkipDamageFixtureMeshPasses)));
        // Exercise section collection without allocating Unity edge meshes.
        foreach (var target in new[]
        {
            AccessTools.Method(overlay.Assembly.GetType("DeltaV.ShellHullVisuals"), "BuildEdges"),
            AccessTools.Method(overlay.Assembly.GetType("DeltaV.ShellRoofEdges"), "Build")
        })
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        object cache = Call(overlay, "CacheFor", map);
        var printContext = (Section)Raw(typeof(Section));
        Set(printContext, "map", map);
        Set(cache, "printContext", printContext);
        try
        {
            using var damagedSnapshot = (IDisposable)Call(overlay, "BuildSection", cache, map, 2, 2, null);
            var batches = (System.Collections.IEnumerable)AccessTools.Field(damagedSnapshot.GetType(), "printed")
                .GetValue(damagedSnapshot);
            ShellSurfaceLayer SurfaceOf(Building building)
            {
                var layer = PrintedDamage[building];
                object batch = batches.Cast<object>().Single(candidate =>
                    AccessTools.Field(candidate.GetType(), "layer").GetValue(candidate) == layer);
                return (ShellSurfaceLayer)AccessTools.Field(batch.GetType(), "surface").GetValue(batch);
            }
            Check(PrintedDamage.Count == 6 && !PrintedDamage.ContainsKey(covered),
                "only visible damaged buildings reach the original per-building printer");
            Check(SurfaceOf(bulkhead) == ShellSurfaceLayer.Hull && SurfaceOf(wall) == ShellSurfaceLayer.Hull
                && SurfaceOf(exposed) == ShellSurfaceLayer.Deck && SurfaceOf(thruster) == ShellSurfaceLayer.Hull,
                "walls, roof-mounted thrusters and moon pool equipment damage follow the building surface");

            var topLayers = (System.Collections.IDictionary)
                AccessTools.Field(damagedSnapshot.GetType(), "turretTopDamage").GetValue(damagedSnapshot);
            object NativeTopLayer(Thing thing) =>
                AccessTools.Field(topLayers[thing].GetType(), "nativeLayer").GetValue(topLayers[thing]);
            Check(topLayers.Count == 2 && NativeTopLayer(turret) == PrintedDamage[turret]
                && NativeTopLayer(shield) == PrintedDamage[shield],
                "covered turrets and projectors keep the original damage quads for compositing");

            PrintedDamage.Clear();
            Set(wall, "hitPointsInt", 100);
            Set(turret, "hitPointsInt", 100);
            map.roofGrid.SetRoof(exposed.Position, roof);
            MapOverlayRenderer.MarkDirty(map, wall.Position, (ulong)MapMeshFlagDefOf.BuildingsDamage);
            using var repairedSnapshot = (IDisposable)Call(overlay, "BuildSection", cache, map, 2, 2, null);
            var repairedTop = (System.Collections.IDictionary)
                AccessTools.Field(repairedSnapshot.GetType(), "turretTopDamage").GetValue(repairedSnapshot);
            Check(PrintedDamage.Count == 3 && !PrintedDamage.ContainsKey(wall) && !PrintedDamage.ContainsKey(turret)
                && !PrintedDamage.ContainsKey(exposed) && repairedTop.Count == 1 && repairedTop.Contains(shield),
                "repair removes both kinds of overlay and a solid moon pool roof hides the equipment beneath");
        }
        finally
        {
            PrintedDamage.Clear();
            harmony.UnpatchAll(harmony.Id);
            rules.Remove(thrusterDef);
            rules.Remove(turretDef);
            rules.Remove(shieldDef);
        }
    }

    private static int AdjacencyField(object adjacency, string name) =>
        (int)AccessTools.Field(adjacency.GetType(), name).GetValue(adjacency);

    private static void CheckHullCorners(Map map, Type hull, TerrainDef deck, RoofDef roof)
    {
        ThingDefOf.GravshipHull = (ThingDef)Raw(typeof(ThingDef));
        ThingDefOf.GravshipHull.defName = "GravshipHull";
        ThingDefOf.GravshipHull.size = IntVec2.One;
        ThingDefOf.GravshipHull.graphicData = new GraphicData { linkFlags = LinkFlags.Wall };
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
            SetRoofFixtureFoundation(map, floor, deck);
            SetRoofFixtureFoundation(map, floor + x, deck);
            SetRoofFixtureFoundation(map, floor + z, deck);
            SetRoofFixtureFoundation(map, floor + x + z, deck);
            map.roofGrid.SetRoof(floor, roof);
            HullTestWall xWall = Place(floor + x, steel);
            HullTestWall zWall = Place(floor + z, uranium);
            HullTestWall elbow = Place(floor + x + z, uranium);

            object Analyze() => Call(hull, "Analyze", map, elbow.Position, elbow, 0);
            object adjacency = Analyze();
            int Field(string name) => AdjacencyField(adjacency, name);
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
                Check((float)Call(overlay, "HullIncidence", localSun, sideX, !outer, false) == sign * 0.6f
                    && (float)Call(overlay, "HullIncidence", localSun, sideZ, !outer, false) == sign * 0.8f
                    && (float)Call(overlay, "HullIncidence", -localSun, sideX, !outer, false) == -sign * 0.6f
                    && (float)Call(overlay, "HullIncidence", -localSun, sideZ, !outer, false) == -sign * 0.8f,
                    $"inner and outer arc edges reverse highlights under opposite light ({corner}, outer={outer})");
            }

            var roofNeighbor = elbow.Position + x;
            SetRoofFixtureFoundation(map, roofNeighbor, deck);
            map.roofGrid.SetRoof(roofNeighbor, roof);
            adjacency = Analyze();
            int fillBit = 1 << corner; // Opposite diagonal in SW/NW/NE/SE order.
            int roofSide = x.x > 0 ? 2 : 8;
            Check((Field("atlasLinks") & roofSide) != 0 && (Field("cornerFill") & fillBit) != 0,
                $"roof-only joins fill the shell atlas ({corner})");
            map.roofGrid.SetRoof(roofNeighbor, null);
            map.roofGrid.SetRoof(floor, null);
            adjacency = Analyze();
            Check(Field("outerCorner") == -1 && Field("cornerFill") == 0,
                $"unroofed open deck does not close the hull corner ({corner})");
            Place(floor, steel);
            adjacency = Analyze();
            Check(Field("outerCorner") == -1 && (Field("cornerFill") & fillBit) != 0,
                $"four actual walls fill the shell cross without a roof at the diagonal ({corner})");

            CallInstance(SubstructureOwnership.For(map), "Assign", xWall.Position, 1200);
            int splitSide = z.z > 0 ? 4 : 1;
            adjacency = Analyze();
            Check((Field("atlasLinks") & splitSide) == 0 && Field("cornerFill") == 0,
                $"different ownership preserves the shell wall seam ({corner})");
        }
    }

    private static void CheckAngledHullSlopes(Map map, Type hull, TerrainDef deck, HashSet<int> dirty)
    {
        Type builderType = hull.GetNestedType("QuadBuilder", BindingFlags.NonPublic);
        Dictionary<IntVec3, int> Angles(CellRect rect) =>
            (Dictionary<IntVec3, int>)Call(hull, "FindAngledCorners", map, rect);
        Building Place(IntVec3 cell)
        {
            var wall = new Building { def = ThingDefOf.GravshipHull };
            Set(wall, "positionInt", cell);
            map.edificeGrid.Register(wall);
            return wall;
        }
        List<Vector3> Strip(string method, params object[] args)
        {
            object builder = Activator.CreateInstance(builderType, true);
            var inputs = new List<object> { builder };
            inputs.AddRange(args);
            Call(hull, method, inputs.ToArray());
            return (List<Vector3>)AccessTools.Field(builderType, "vertices").GetValue(builder);
        }
        bool SamePoint(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 0.00000001f;

        for (int corner = 0; corner < 4; corner++)
        {
            var cell = new IntVec3(7 + 7 * corner, 0, 6);
            IntVec3 x = corner < 2 ? IntVec3.East : IntVec3.West;
            IntVec3 z = corner == 0 || corner == 3 ? IntVec3.North : IntVec3.South;
            SetRoofFixtureFoundation(map, cell + x, deck);
            SetRoofFixtureFoundation(map, cell + z, deck);
            SetRoofFixtureFoundation(map, cell + x + z, deck);
            Building alongX = Place(cell + x);
            Building alongZ = Place(cell + z);
            CellRect bounds = CellRect.CenteredOn(cell, 1);
            var partial = Angles(bounds);
            Building elbow = Place(cell + x + z);
            var full = Angles(bounds);
            Check(partial.TryGetValue(cell, out int a) && a == corner
                && full.TryGetValue(cell, out int b) && b == corner,
                $"owned-domain partial and full corners select the same outward edge ({corner})");

            List<Vector3> diagonal = Strip("AddAngledSlope", cell, corner, full, 0f);
            List<Vector3> partialSlope = Strip("AddAngledSlope", cell, corner, partial, 0f);
            var midpoint = new Vector3(cell.x + 0.5f, 0f, cell.z + 0.5f);
            var outward = new Vector2(-x.x * 0.70710678f, -z.z * 0.70710678f);
            var direction = new Vector3(outward.x, 0f, outward.y);
            Check(diagonal.Count == 4 && diagonal.TrueForAll(v => partialSlope.Exists(p => SamePoint(v, p)))
                && SamePoint((diagonal[0] + diagonal[1]) * 0.5f, midpoint)
                && MathF.Abs(Vector3.Dot(diagonal[2] - midpoint, direction) + 0.16f) < 0.0001f
                && MathF.Abs(Vector3.Dot(diagonal[3] - midpoint, direction) + 0.16f) < 0.0001f
                && Vector3.Cross(diagonal[1] - diagonal[0], diagonal[2] - diagonal[0]).y > 0f,
                $"light strip follows the exterior texture diagonal at constant width ({corner})");

            float Incidence(Vector2 sun) =>
                (float)Call(typeof(MapOverlayRenderer), "HullIncidence", sun, corner, false, true);
            Check(MathF.Abs(Incidence(outward) - 1f) < 0.0001f
                && MathF.Abs(Incidence(-outward) + 1f) < 0.0001f
                && MathF.Abs(Incidence(new Vector2(-outward.y, outward.x))) < 0.0001f,
                $"diagonal incidence changes sign and vanishes under grazing light ({corner})");

            int sideX = x.x > 0 ? 1 : 3, sideZ = z.z > 0 ? 0 : 2;
            CheckMiter(alongX.Position, (sideX + 2) & 3, (sideZ + 2) & 3);
            CheckMiter(alongZ.Position, (sideZ + 2) & 3, (sideX + 2) & 3);
            void CheckMiter(IntVec3 wall, int coveredSide, int exposedSide)
            {
                int hidden = (int)Call(hull, "AngledSidesAt", wall, full);
                Check(hidden == (1 << coveredSide)
                    && Strip("AddStraightSlope", wall, coveredSide, hidden, full, 0f, 0).Count == 0,
                    $"covered straight face does not leave a rectangular light band ({corner}, {coveredSide})");
                List<Vector3> straight = Strip("AddStraightSlope", wall, exposedSide, hidden, full, 0f, 0);
                Check(straight.Count == 4 && straight.FindAll(v => diagonal.Exists(d => SamePoint(v, d))).Count == 2,
                    $"angle and straight wall share both vertices of the join ({corner}, {exposedSide})");
            }
            map.edificeGrid.DeRegister(alongX);
            Check(!Angles(bounds).ContainsKey(cell), $"removing a wall removes the original corner and its slope ({corner})");
            map.edificeGrid.Register(alongX);
            SetRoofFixtureFoundation(map, cell, deck);
            Check(!Angles(bounds).ContainsKey(cell), $"foundation in the anchor suppresses the corner ({corner})");
        }

        var left = new IntVec3(16, 0, 29);
        var right = left + IntVec3.East + IntVec3.South;
        SetRoofFixtureFoundation(map, left + IntVec3.North, deck);
        SetRoofFixtureFoundation(map, left + IntVec3.East, deck);
        SetRoofFixtureFoundation(map, left + IntVec3.North + IntVec3.East, deck);
        SetRoofFixtureFoundation(map, right + IntVec3.East, deck);
        SetRoofFixtureFoundation(map, right + IntVec3.East + IntVec3.North, deck);
        Place(left + IntVec3.North);
        Place(left + IntVec3.East);
        Building farWall = Place(right + IntVec3.East);
        var leftAngles = Angles(CellRect.SingleCell(left).ExpandedBy(1));
        var rightAngles = Angles(CellRect.SingleCell(right).ExpandedBy(1));
        List<Vector3> leftSlope = Strip("AddAngledSlope", left, 0, leftAngles, 0f);
        List<Vector3> rightSlope = Strip("AddAngledSlope", right, 0, rightAngles, 0f);
        Check(leftSlope.FindAll(v => rightSlope.Exists(p => SamePoint(v, p))).Count == 2,
            "continuing diagonal shares both endpoints with the next section");
        map.edificeGrid.DeRegister(farWall);
        dirty.Clear();
        MapOverlayRenderer.MarkDirty(map, farWall.Position, (ulong)MapMeshFlagDefOf.Terrain);
        Check(dirty.SetEquals(new[] { 1 << 16, 1 | (1 << 16) }),
            "two-cell dependency dirties the neighboring section when the far wall changes");
        var brokenAngles = Angles(CellRect.SingleCell(left).ExpandedBy(1));
        List<Vector3> brokenSlope = Strip("AddAngledSlope", left, 0, brokenAngles, 0f);
        Check(!brokenAngles.ContainsKey(right)
            && brokenSlope.FindAll(v => leftSlope.Exists(p => SamePoint(v, p))).Count == 3,
            "breaking a diagonal across sections restores a miter at the surviving endpoint");
    }

    private static Building_Door CheckWallSegmentLighting(Map map, Type hull, TerrainDef deck, RoofDef roof,
        HashSet<int> dirty)
    {
        var angles = new Dictionary<IntVec3, int>();
        Building Place(Building building, IntVec3 cell, BuildingProperties props, bool full = true,
            IntVec2? size = null)
        {
            if (building.def == null)
            {
                var def = (ThingDef)Raw(typeof(ThingDef));
                def.category = ThingCategory.Building;
                def.thingClass = building.GetType();
                def.building = props;
                def.fillPercent = full ? 1f : 0.5f;
                def.size = size ?? IntVec2.One;
                building.def = def;
            }
            Set(building, "positionInt", cell);
            map.edificeGrid.Register(building);
            return building;
        }
        System.Collections.IDictionary Analyze(CellRect rect) =>
            (System.Collections.IDictionary)Call(hull, "AnalyzeWalls", map, rect, angles);
        int Mask(System.Collections.IDictionary walls, IntVec3 cell, string field)
        {
            object entry = walls[cell];
            return (int)AccessTools.Field(entry.GetType(), field).GetValue(entry);
        }

        var start = new IntVec3(4, 0, 36);
        Place(new Building(), start, new BuildingProperties { isWall = true });
        Place(new HullTestWall { def = ThingDefOf.GravshipHull, graphic = new HullTestGraphic() },
            start + IntVec3.East, null);
        var doorCell = start + IntVec3.East * 2;
        var door = (Building_Door)Place(new Building_Door(), doorCell, new BuildingProperties());
        var ventCell = start + IntVec3.East * 3;
        var vent = Place(new Building_Vent(), ventCell, new BuildingProperties { canPlaceOverWall = true });
        var coolerCell = start + IntVec3.East * 4;
        Place(new Building_Cooler(), coolerCell, new BuildingProperties { canPlaceOverWall = true });
        var reactor = coolerCell + IntVec3.East;
        Place(new Building(), reactor, new BuildingProperties { isAirtight = true });
        var partialInsert = reactor + IntVec3.East;
        Place(new Building(), partialInsert, new BuildingProperties { canPlaceOverWall = true }, full: false);
        var row = CellRect.FromLimits(start.x, start.z, partialInsert.x, partialInsert.z);
        foreach (IntVec3 cell in row)
        {
            IntVec3 inside = cell + IntVec3.North;
            SetRoofFixtureFoundation(map, cell, deck);
            SetRoofFixtureFoundation(map, inside, deck);
            map.roofGrid.SetRoof(inside, roof);
        }

        foreach (Rot4 rotation in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
        {
            vent.Rotation = rotation;
            var walls = Analyze(row);
            Check(walls.Count == 5 && !walls.Contains(reactor) && !walls.Contains(partialInsert),
                $"wall, gravship hull, door and full wall inserts join the lighting boundary ({rotation})");
            Check(Mask(walls, ventCell, "connected") == 0b1011 && Mask(walls, ventCell, "floorSides") == 0b0001,
                $"embedded vent has one exterior side and one roof seam ({rotation})");
            Check(Mask(walls, start + IntVec3.East, "connected") == 0b1011
                && Mask(walls, start + IntVec3.East, "atlasLinks") == 0b0001,
                "wall illumination crosses material boundaries while the gravship atlas keeps its native links");
        }

        Set(door, "openInt", true);
        var openRow = Analyze(row);
        Check(!openRow.Contains(doorCell) && Mask(openRow, ventCell, "connected") == 0b0011
            && Mask(openRow, start + IntVec3.East, "connected") == 0b1001,
            "open door leaves a gap and exposes the adjacent wall edges");
        Type builderType = hull.GetNestedType("QuadBuilder", BindingFlags.NonPublic);
        foreach (int side in new[] { 0, 1, 2, 3 })
        {
            object builder = Activator.CreateInstance(builderType, true);
            int inset = side % 2 == 0 ? 0b0101 : 0b1010;
            Call(hull, "AddStraightSlope", builder, doorCell, side, 0, angles, 0f, inset);
            var vertices = (List<Vector3>)AccessTools.Field(builderType, "vertices").GetValue(builder);
            var center = doorCell.ToVector3Shifted();
            var normal = (Vector2)Call(hull, "SideNormal", side);
            Check(vertices.Count == 4 && vertices.Take(2).All(vertex =>
                Vector2.Dot(new Vector2(vertex.x - center.x, vertex.z - center.z), normal) == 22f / 64f),
                $"standard door slope begins on the visible mover edge ({side})");
        }

        var multi = (Building_Door)Place(new Building_Door(), new IntVec3(17, 0, 36),
            new BuildingProperties(), size: new IntVec2(5, 1));
        CellRect footprint = multi.OccupiedRect();
        foreach (IntVec3 cell in footprint) SetRoofFixtureFoundation(map, cell, deck);
        var closed = Analyze(footprint);
        Check(closed.Count == 5 && Mask(closed, multi.Position, "connected") == 0b1010,
            "multi-cell door contributes all occupied cells without internal boundary strips");
        dirty.Clear();
        Set(multi, "openInt", true);
        map.events.Notify_DoorOpened(multi);
        Check(dirty.SetEquals(new[] { 2 << 16, 1 | (2 << 16) }) && Analyze(footprint).Count == 0,
            "opening a multi-cell door clears its light across both affected sections");
        dirty.Clear();
        Set(multi, "openInt", false);
        map.events.Notify_DoorClosed(multi);
        Check(dirty.SetEquals(new[] { 2 << 16, 1 | (2 << 16) }) && Analyze(footprint).Count == 5,
            "closing a multi-cell door refreshes both sections and restores the boundary");
        return multi;
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
