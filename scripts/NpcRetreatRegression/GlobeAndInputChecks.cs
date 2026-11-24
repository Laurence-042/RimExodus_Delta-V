using RimExodus.Maps;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeltaV;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static bool GlobeSubstructure(ref bool __result) { __result = true; return false; }

    sealed class GlobePawnHolder : MapComponent, IThingHolder
    {
        internal readonly ThingOwner<Pawn> contents;
        internal GlobePawnHolder(Map map) : base(map) { contents = new ThingOwner<Pawn>(this); }
        public IThingHolder ParentHolder => map;
        public ThingOwner GetDirectlyHeldThings() => contents;
        public void GetChildHolders(List<IThingHolder> outChildren) { }
    }

    static void CheckGlobeAndInput(UniverseManager universe)
    {
        var buttons = new AimButtonLatch();
        buttons.Validate(true, true, true);
        Check(!buttons.Main && !buttons.Secondary, "holding UI-origin buttons cannot authorize fire on entering the map");
        buttons.Press(0); buttons.Press(1); buttons.Validate(true, true, true);
        Check(buttons.Main && buttons.Secondary, "independent authorized main and secondary salvos can remain held");
        buttons.Validate(false, true, true); buttons.Validate(true, true, true);
        Check(!buttons.Main && !buttons.Secondary, "UI/focus/targeter invalidation cannot re-arm until a new map press");
        buttons.Press(1); buttons.Validate(true, true, false);
        Check(!buttons.Secondary, "release clears authorization even when its GUI event was consumed");
        buttons.Press(0); buttons.Clear(); buttons.Validate(true, true, false);
        Check(!buttons.Main, "new sessions and map changes cannot inherit a held volley");

        var markerA = new WorldShipMarkerRenderer.ScreenMarker(
            new DriveSubject(0, 4101), new Vector2(0f, 0f), 10f, current: true);
        var markerB = new WorldShipMarkerRenderer.ScreenMarker(
            new DriveSubject(0, 4102, true), new Vector2(18f, 0f), 10f);
        var markerC = new WorldShipMarkerRenderer.ScreenMarker(
            new DriveSubject(0, 4103), new Vector2(36f, 0f), 10f);
        var markerFar = new WorldShipMarkerRenderer.ScreenMarker(
            new DriveSubject(0, 4104), new Vector2(100f, 0f), 10f);
        var markerGroups = WorldShipMarkerRenderer.ClusterScreenMarkers(
            new[] { markerC, markerFar, markerA, markerB });
        Check(markerGroups.Count == 2 && markerGroups.Any(group => group.Members.Count == 3)
            && markerGroups.Any(group => group.Members.Count == 1),
            "world ship markers cluster only connected overlapping screen hit areas");
        var connected = markerGroups.Single(group => group.Members.Count == 3);
        Check(connected.Representative.Identity == markerA.Identity
            && connected.Members.Select(member => member.Identity)
                .SequenceEqual(new[] { markerA.Identity, markerB.Identity, markerC.Identity }),
            "world marker clustering is transitive, stable, and prefers the current ship");
        var reversed = WorldShipMarkerRenderer.ClusterScreenMarkers(
            new[] { markerFar, markerB, markerA, markerC });
        Check(reversed.SelectMany(group => group.Members).Select(member => member.Identity)
                .SequenceEqual(markerGroups.SelectMany(group => group.Members).Select(member => member.Identity))
            && reversed[0].Representative.Identity == markerGroups[0].Representative.Identity,
            "world marker grouping and representatives do not depend on ship enumeration order");
        connected = WorldShipMarkerRenderer.ClusterScreenMarkers(
            new[] { markerA, markerB, markerC }, markerB.Identity).Single();
        Check(connected.Representative.Identity == markerB.Identity,
            "an explicitly selected ship takes representative priority over the current-map fallback");

        TerrainDef Terrain(string name) { var def = (TerrainDef)Raw(typeof(TerrainDef)); def.defName = name; return def; }
        var space = Terrain("Space");
        var empty = Terrain("DeltaV_Void");
        var deck = Terrain("GlobeDeck");
        var rock = Terrain("GlobeRock");
        var doc = new MapPrefabDocument { size = new IntVec2(1000, 1000) };
        doc.terrain.Add(new PrefabTerrainData { def = space, rects = new List<CellRect> { new CellRect(0, 0, 20, 20) } });
        doc.terrain.Add(new PrefabTerrainData { def = deck, rects = new List<CellRect> { new CellRect(2, 2, 2, 2) } });
        doc.terrain.Add(new PrefabTerrainData { def = rock, rects = new List<CellRect> { new CellRect(12, 12, 1, 1) } });
        doc.terrain.Add(new PrefabTerrainData { def = empty, rects = new List<CellRect> { new CellRect(0, 0, 2, 2) } });
        doc.foundations.Add(new PrefabFoundationEntry { pos = new IntVec3(2, 0, 2), def = deck });
        Check(GlobeDisplayRules.Measure(doc) == 5, "prefab area counts natural terrain and deduplicates covered foundations, excluding space and gaps");
        doc.size = new IntVec2(30, 30);
        Check(GlobeDisplayRules.Measure(doc) == 5, "map padding and prefab canvas dimensions cannot change visibility area");
        var asteroidRock = TestBuildingDef("GlobeAsteroidRock");
        asteroidRock.building.isNaturalRock = true;
        var asteroid = new MapPrefabDocument { size = new IntVec2(5, 5) };
        asteroid.terrain.Add(doc.terrain[0]);
        asteroid.thingEntries.Add(new MapPrefabThingEntry { def = asteroidRock, pos = new IntVec3(1, 0, 2) });
        Check(GlobeDisplayRules.Measure(asteroid) == 1, "a pure Space asteroid prefab has its rock footprint as area");
        asteroid.foundations.Add(new PrefabFoundationEntry { pos = asteroid.thingEntries[0].pos, def = deck });
        Check(GlobeDisplayRules.Measure(asteroid) == 1, "prefab rock and the foundation under it count as one cell");
        Check(!GlobeDisplayRules.Visible(4, 0.1f, false) && GlobeDisplayRules.Visible(100, 0.1f, false),
            "station-sized debris appears before small fragments at the same scale");
        Check(GlobeDisplayRules.Visible(4, 0.25f, false), "small fragments appear when zoomed to the area threshold");
        Check(GlobeDisplayRules.Visible(0, 0.001f, true) && GlobeDisplayRules.Visible(-1, 0.001f, false),
            "native/player markers and unknown asset measurements retain their icons");
        Check(!GlobeDisplayRules.UseProjection(16, 1f, false)
            && GlobeDisplayRules.UseProjection(16, 0.5f, true)
            && !GlobeDisplayRules.UseProjection(16, 0.49f, true),
            "projection uses the common view scale and falls back to icons until geometry is available");
        foreach (float area in new[] { 1f, 2f, 15f, 256f, 1683f })
            Check(GlobeDisplayRules.Visible(area, 0.558f, false)
                && GlobeDisplayRules.UseProjection(16, 0.558f, true),
                $"near view draws loaded {area}-cell terrain at true scale instead of a fixed-size symbol");
        Check(!GlobeDisplayRules.UseProjection(16, 0.2447f, true)
            && GlobeDisplayRules.UseProjection(5, 0.558f, true),
            "far view uses icons and a shuttle-only reference can still reach projection scale");
        var projectedPoi = new PoiState();
        var distantPoi = new PoiState();
        var projectedMember = new PlanetGlobeRenderer.Legend {
            entry = new GlobeDisplayEntry { owner = projectedPoi, craft = true }, projection = true };
        var smallMember = new PlanetGlobeRenderer.Legend {
            entry = new GlobeDisplayEntry { owner = projectedPoi, craft = true } };
        var nativeFallback = new PlanetGlobeRenderer.Legend {
            entry = new GlobeDisplayEntry { owner = projectedPoi, native = true } };
        var otherNative = new PlanetGlobeRenderer.Legend {
            entry = new GlobeDisplayEntry { owner = distantPoi, native = true } };
        var legends = new List<PlanetGlobeRenderer.Legend> {
            nativeFallback, projectedMember, smallMember, otherNative };
        PlanetGlobeRenderer.RemoveRedundantNativeIcons(legends);
        Check(legends.Count == 3 && !legends.Contains(nativeFallback)
            && legends.Contains(projectedMember) && legends.Contains(smallMember) && legends.Contains(otherNative),
            "native POI projection removes its container icon while preserving independent ships and other POIs");
        legends = new List<PlanetGlobeRenderer.Legend> { nativeFallback, smallMember };
        PlanetGlobeRenderer.RemoveRedundantNativeIcons(legends);
        Check(legends.Contains(nativeFallback), "native POI icon remains until a visible member projection replaces it");
        Check(!GlobeDisplayRules.ShowLabel(1000000, 10f, true)
            && GlobeDisplayRules.ShowLabel(100, 0.2f, false), "wreck names stay hidden even at maximum zoom");
        Check(GlobeDisplayRules.IsWreck(false, false)
            && !GlobeDisplayRules.IsWreck(true, false)
            && !GlobeDisplayRules.IsWreck(false, true), "people or remaining installed capabilities prevent wreck classification");
        var consoleDef = TestBuildingDef("GlobeBackupConsole");
        consoleDef.comps.Add(new CompProperties_DeltaVPilotConsole());
        var engineDef = TestBuildingDef("GlobeBrokenEngine");
        engineDef.comps.Add(new CompProperties_GravshipThruster());
        var weaponDef = TestBuildingDef("GlobeUnpoweredGun");
        weaponDef.comps.Add(new CompProperties_SpaceWeapon());
        Check(new[] { consoleDef, engineDef, weaponDef }.All(d => GlobeDisplayRules.HasEquipment(new[] { d })),
            "classification uses installed definitions, not power/ammo/primary-console availability");

        Map map = TestMap(2370);
        FieldInfo mainThread = typeof(UnityData).GetField("mainThreadId", Private);
        int previousThread = (int)mainThread.GetValue(null);
        mainThread.SetValue(null, System.Threading.Thread.CurrentThread.ManagedThreadId);
        map.mapPawns = new MapPawns(map);
        map.passingShipManager = new PassingShipManager(map);
        for (int i = 0; i < map.terrainGrid.topGrid.Length; i++) map.terrainGrid.topGrid[i] = space;
        map.terrainGrid.topGrid[0] = deck;
        map.terrainGrid.foundationGrid[0] = deck;
        map.terrainGrid.topGrid[1] = rock;
        var mapped = new MapSpaceObject(2370, map, new SpacePose(Vector3.forward, 0));
        var display = new GlobeDisplayInfo();
        var first = display.Entries(mapped).Single();
        Check(first.area == 2 && first.wreck && first.cells.Count == 2,
            "loaded maps share terrain de-duplication and actual wreck classification");
        Check(ReferenceEquals(first, display.Entries(mapped).Single()), "unchanged map display reads reuse the measured entry");
        void ChangeTerrain(IntVec3 cell, TerrainDef terrain)
        {
            int index = map.cellIndices.CellToIndex(cell);
            TerrainDef former = map.terrainGrid.topGrid[index];
            map.terrainGrid.topGrid[index] = terrain;
            universe.ShipStructure.NotifyTerrain(map, cell);
            universe.ShipStructure.NotifyCell(map, cell, ShipStructureAspect.Terrain, former, terrain);
        }
        ChangeTerrain(map.cellIndices.IndexToCell(1), space);
        var damaged = display.Entries(mapped).Single();
        Check(damaged.area == 1 && !ReferenceEquals(first, damaged), "structural damage invalidates actual footprint and projection source together");
        var collisionHarness = new HarmonyLib.Harmony("DeltaV.Regression.GlobeArchiveCollision");
        collisionHarness.Patch(HarmonyLib.AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
            prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(GlobeSubstructure)));
        GlobeDisplaySummary captured;
        try
        {
            universe.NotifyFoundationChanged(map, IntVec3.Zero);
            universe.ShipStructure.NotifyCell(map, IntVec3.Zero, ShipStructureAspect.Foundation, null, deck);
            captured = display.Capture(mapped);
        }
        finally { collisionHarness.UnpatchAll(collisionHarness.Id); }
        Check(captured.area == 1 && captured.wreck && captured.navigationRadius > 0f,
            "archive capture retains damaged display area and the live collision clearance");
        var mineable = AddTestBuilding(map, null, 2372, asteroidRock, new CompDeltaVPilotConsole(),
            (Mineable)Raw(typeof(Mineable)));
        Current.Game.GetComponent<UniverseManager>().ShipStructure.Changed(map);
        Check(display.Entries(mapped).Single().area == 2 && display.Entries(mapped).Single().natural,
            "a live rock above Space adds a natural environment cell");
        ChangeTerrain(mineable.Position, deck);
        Check(display.Entries(mapped).Single().area == 2, "live rock and terrain beneath it count as one cell");
        ChangeTerrain(mineable.Position, space);
        RemoveFixtureBuilding(mineable, map);
        var holder = new GlobePawnHolder(map);
        map.components.Add(holder);
        var pawn = (Pawn)Raw(typeof(Pawn));
        pawn.def = TestBuildingDef("GlobeSurvivor"); pawn.def.category = ThingCategory.Pawn;
        pawn.health = (Pawn_HealthTracker)Raw(typeof(Pawn_HealthTracker));
        Set(pawn.health, "healthState", PawnHealthState.Mobile);
        Set(typeof(Thing), pawn, "mapIndexOrState", (sbyte)-1);
        holder.contents.InnerListForReading.Add(pawn);
        display.NotifyPopulationChanged(map);
        Check(!display.Entries(mapped).Single().wreck, "a living pawn inside a map holder prevents wreck classification");
        holder.contents.InnerListForReading.Clear();
        display.NotifyPopulationChanged(map);
        Check(display.Entries(mapped).Single().wreck, "removing the last contained pawn refreshes wreck status without a terrain rescan");
        var backup = new CompDeltaVPilotConsole { isMain = false };
        var console = AddTestBuilding(map, null, 2371, consoleDef, backup);
        Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll();
        Check(!display.Entries(mapped).Single().wreck, "a backup console alone prevents a loaded map from being labelled a wreck");
        RemoveFixtureBuilding(console, map);
        var shuttleA = new CompSpaceShuttle { props = new CompProperties_SpaceShuttle() };
        var twoCellHull = TestBuildingDef("GlobeTwoCellShuttle");
        twoCellHull.size = new IntVec2(2, 1);
        AddTestBuilding(map, null, 2373, twoCellHull, shuttleA);
        var shuttleB = AddTestHull(map, null, 2374);
        var secondCell = new IntVec3(3, 0, 3);
        RemoveFixtureBuilding((Building)shuttleB.parent, map);
        Set(typeof(Thing), shuttleB.parent, "positionInt", secondCell);
        PlaceFixtureBuilding((Building)shuttleB.parent, map);
        var beforeLocal = display.Entries(mapped).ToArray();
        ChangeTerrain(shuttleA.parent.OccupiedRect().First(), deck);
        var afterLocal = display.Entries(mapped).ToArray();
        GlobeDisplayEntry Member(GlobeDisplayEntry[] entries, int id) => entries.Single(entry => entry.ship?.key == id);
        Check(afterLocal.Length == 3 && Member(beforeLocal, 2373).area == 2 && Member(afterLocal, 2373).area == 1
            && !ReferenceEquals(Member(beforeLocal, 2373), Member(afterLocal, 2373))
            && ReferenceEquals(Member(beforeLocal, 2374), Member(afterLocal, 2374))
            && ReferenceEquals(beforeLocal.Single(entry => !entry.craft), afterLocal.Single(entry => !entry.craft)),
            "one shuttle changes its displayed area while the other shuttle and environment keep their projection");
        var piece = new IntVec3(3, 0, 2);
        ChangeTerrain(piece, rock);
        var afterEnvironment = display.Entries(mapped).ToArray();
        Check(afterEnvironment.Where(entry => entry.craft).SequenceEqual(afterLocal.Where(entry => entry.craft))
            && afterEnvironment.Single(entry => !entry.craft).area == 2
            && !ReferenceEquals(afterEnvironment.Single(entry => !entry.craft), afterLocal.Single(entry => !entry.craft)),
            "a local environment change updates its own area and preserves both ship projections");
        var cacheType = typeof(PlanetGlobeRenderer);
        var silhouetteCache = (System.Collections.IDictionary)cacheType
            .GetField("SilhouetteCache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var byShip = (System.Collections.IDictionary)Activator.CreateInstance(silhouetteCache.GetType().GetGenericArguments()[1]);
        var cachedSilhouette = cacheType.GetNestedType("CachedSilhouette", BindingFlags.NonPublic);
        var firstShip = new DriveSubject(0, 2373, true);
        var secondShip = new DriveSubject(0, 2374, true);
        foreach (DriveSubject key in new[] { firstShip, secondShip, default(DriveSubject) })
            byShip.Add(key, Activator.CreateInstance(cachedSilhouette, nonPublic: true));
        silhouetteCache.Add(map, byShip);

        RemoveFixtureBuilding((Building)shuttleB.parent, map);
        var afterRemoval = display.Entries(mapped).ToArray();
        Check(!byShip.Contains(secondShip) && byShip.Contains(firstShip) && byShip.Contains(default(DriveSubject))
            && ReferenceEquals(Member(afterEnvironment, 2373), Member(afterRemoval, 2373)),
            "removing a ship releases its texture cache entry and retains the other ship and environment");
        foreach (IntVec3 cell in afterRemoval.Single(entry => !entry.craft).cells)
        {
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(cell)] = null;
            ChangeTerrain(cell, space);
        }
        Check(display.Entries(mapped).Single().craft && byShip.Contains(firstShip) && !byShip.Contains(default(DriveSubject)),
            "losing the last environment cell releases only the environment texture cache entry");
        RemoveFixtureBuilding((Building)shuttleA.parent, map);
        _ = display.Entries(mapped).ToArray();
        Check(!silhouetteCache.Contains(map), "releasing the last texture entry removes the empty map cache");
        display.Forget(map);
        Check(!ReferenceEquals(damaged, display.Entries(mapped).Single()), "map deletion/representation cleanup discards display caches");
        Find.Maps.Remove(map);
        mainThread.SetValue(null, previousThread);

        var archive = new ArchivedSpaceObject { objectId = 2380, pose = new SpacePose(Vector3.forward, 0.2f),
            globe = new GlobeDisplaySummary { area = 45, environmentArea = 20, wreck = false,
                ships = new List<GlobeShipSummary> { new GlobeShipSummary { key = 2381, shuttle = true,
                    area = 25, label = "archived shuttle", localCenter = new Vector2(12, 3), localAngle = 0.7f } } } };
        var info = new GlobeDisplayInfo();
        var native = new PoiState { kind = PoiKind.NativeParent, nativeParent = (MapParent)Raw(typeof(MapParent)),
            envLabel = "Unloaded native POI", pose = new SpacePose(Vector3.forward, 0) };
        var nativeEntry = info.Entries(native).Single();
        Check(nativeEntry.native && nativeEntry.AlwaysVisible && native.map == null
            && !GlobeDisplayRules.UseProjection(16, 100f, false),
            "unloaded native POIs remain visible icons without generating a map");
        var entries = info.Entries(archive).ToList();
        Check(entries.Count == 2 && entries[0].craft && entries[0].ship.shuttle && entries[0].area == 25,
            "archived member keeps its arrow and actual area separately from remaining terrain");
        Check(OrbitSphere.ArcCells(entries[0].Pose.position,
            SpaceTransform.ShipLocalToUniverse(archive.pose, new Vector2(12, 3))) < 0.01f,
            "archived marker position uses the same rigid-point transform as loaded ships");
        string path = Path.Combine(AppContext.BaseDirectory, "globe-summary.xml");
        GlobeDisplaySummary saved = archive.globe, restored = null;
        try
        {
            Scribe.saver.InitSaving(path, "globe-test");
            Scribe_Deep.Look(ref saved, "summary");
            Scribe.saver.FinalizeSaving();
            Scribe.loader.InitLoading(path);
            Scribe_Deep.Look(ref restored, "summary");
            Scribe.loader.FinalizeLoading();
        }
        finally { Scribe.ForceStop(); }
        Check(restored.area == 45 && restored.environmentArea == 20 && restored.ships.Single().area == 25
            && restored.ships[0].label == "archived shuttle" && restored.ships[0].localAngle == 0.7f,
            "archive display facts round-trip through real Scribe without textures or map-size substitution");

        foreach (string name in new[] { "Patch_GlobePopulation", "Patch_GlobeContainedPopulation" })
        {
            Type patch = typeof(UniverseManager).Assembly.GetType("DeltaV." + name);
            var targets = (IEnumerable<MethodBase>)patch.GetMethod("TargetMethods", Private).Invoke(null, null);
            Check(targets.All(m => m != null), name + " Harmony target signatures resolve against the installed game");
        }
        Check(typeof(MapPawns).GetField("map", Private) != null
            && typeof(GenUI).GetMethod("AbsorbClicksInRect").GetParameters()[0].Name == "r",
            "Harmony map-field and UI rectangle parameter bindings match vanilla");
    }
}
