using System;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static partial class Program
{
    sealed class DerivedPilotConsole : CompDeltaVPilotConsole { }
    sealed class PilotFixtureBuilding : Building
    {
        public override string Label => def.label;
    }

    static ThingDef PilotFixtureDef(string name, Type comp = null)
    {
        var def = Raw<ThingDef>();
        def.defName = name; def.label = name; def.category = ThingCategory.Building;
        def.thingClass = typeof(Building); def.size = IntVec2.One;
        def.building = new BuildingProperties(); def.bringAlongOnGravship = true;
        def.comps = new List<CompProperties>();
        if (comp != null) def.comps.Add(new CompProperties { compClass = comp });
        return def;
    }

    static void PilotConsoleChecks(Game game, Harmony harmony)
    {
        var universe = game.GetComponent<UniverseManager>();
        var binding = AccessTools.Field(typeof(DefOfHelper), "bindingNow");
        bool oldBinding = (bool)binding.GetValue(null);
        binding.SetValue(null, true);
        var oldConsole = ThingDefOf.PilotConsole;
        var oldEngine = ThingDefOf.GravEngine;
        var oldLauncher = DeltaVDefOf.DeltaV_Weapon_MissileLauncher;
        var oldGravship = WorldObjectDefOf.Gravship;
        var mapsBefore = game.Maps.ToArray();
        var objectsBefore = universe.objects.ToArray();
        var oldLanguage = LanguageDatabase.activeLanguage;
        var language = Raw<LoadedLanguage>(); Set(language, "dataIsLoaded", true);
        Set(language, "workerInt", new LanguageWorker_English());
        language.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement>();
        language.keyedReplacements["DeltaV_ConsolePrimarySet"] = new LoadedLanguage.KeyedReplacement
            { key = "DeltaV_ConsolePrimarySet", value = "Primary {0}" };
        LanguageDatabase.activeLanguage = language;
        if (AccessTools.Field(typeof(FactionManager), "ofPlayer").GetValue(game.World.factionManager) == null)
            Set(game.World.factionManager, "ofPlayer", new Faction { def = new FactionDef { isPlayer = true } });
        var patch = typeof(UniverseManager).Assembly.GetType("DeltaV.Patch_GravshipPilotConsole");
        var addThing = AccessTools.Method(typeof(Gravship), "AddThing");
        var foundationGetter = AccessTools.PropertyGetter(typeof(TerrainDef), nameof(TerrainDef.IsSubstructure));
        harmony.Patch(foundationGetter, prefix: new HarmonyMethod(typeof(Program), nameof(FixtureFoundation)));
        // Only omit Unity materials; native AddThing and its engine membership checks still execute.
        harmony.Patch(typeof(Building_GravEngine).TypeInitializer,
            prefix: new HarmonyMethod(typeof(Program), nameof(SkipMissionMessage)));
        var refreshFloor = AccessTools.Method(typeof(Building_GravEngine), "UpdateSubstructureIfNeeded");
        harmony.Patch(refreshFloor, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMissionMessage)));
        harmony.CreateClassProcessor(patch).Patch();
        try
        {
            var map = Raw<Map>(); map.uniqueID = 28110;
            map.info = new MapInfo { Size = new IntVec3(8, 1, 8) };
            game.Maps.Clear(); game.Maps.Add(map);
            universe.objects.Clear();
            map.cellIndices = new CellIndices(map);
            map.terrainGrid = new TerrainGrid(map);
            map.thingGrid = new ThingGrid(map);
            map.floodFiller = new FloodFiller(map);
            map.listerThings = new ListerThings(ListerThingsUse.Global);
            map.components = new List<MapComponent> { new SubstructureOwnership(map) };
            map.mapPawns = new MapPawns(map);
            map.storyState = new StoryState(map);
            map.autoSlaughterManager = new AutoSlaughterManager(map);
            map.zoneManager = new ZoneManager(map);
            map.areaManager = new AreaManager(map);
            map.storageGroups = new StorageGroupManager(map);
            var frame = new MapSpaceObject(28110, map, default);
            universe.objects.Add(frame);
            ThingDefOf.PilotConsole = PilotFixtureDef("pilotFixtureVanilla");
            ThingDefOf.GravEngine = PilotFixtureDef("pilotFixtureEngine");
            DeltaVDefOf.DeltaV_Weapon_MissileLauncher = PilotFixtureDef("pilotFixtureLauncher");
            WorldObjectDefOf.Gravship = new WorldObjectDef();
            var custom = PilotFixtureDef("pilotFixtureCustom", typeof(CompDeltaVPilotConsole));
            var derived = PilotFixtureDef("pilotFixtureDerived", typeof(DerivedPilotConsole));
            var deck = Raw<TerrainDef>(); deck.isFoundation = true;
            var owner = SubstructureOwnership.For(map);
            var cells = new HashSet<IntVec3> { new IntVec3(2, 0, 2), new IntVec3(3, 0, 2) };
            foreach (var cell in cells)
            {
                map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(cell)] = deck;
                owner.Assign(cell, 28111);
            }
            Building Place(ThingDef def, CompDeltaVPilotConsole console, IntVec3 cell, int id)
            {
                var building = Raw<PilotFixtureBuilding>(); building.def = def; building.thingIDNumber = id;
                Set(building, "mapIndexOrState", (sbyte)0); Set(building, "positionInt", cell);
                Set(building, "factionInt", Faction.OfPlayer);
                Set(building, "comps", console == null ? null : new List<ThingComp> { console });
                if (console != null) console.parent = building;
                map.listerThings.AllThings.Add(building);
                map.thingGrid.Register(building);
                ShipStructureChanges.Notify(map, building);
                return building;
            }
            var primary = new CompDeltaVPilotConsole { shipId = 28111, isMain = true };
            var backup = new DerivedPilotConsole { shipId = 28111 };
            var first = Place(custom, primary, new IntVec3(2, 0, 2), 28112);
            var second = Place(derived, backup, new IntVec3(3, 0, 2), 28113);
            var impostor = Place(ThingDefOf.PilotConsole, null, new IntVec3(4, 0, 2), 28114);
            Check(ShipRegistry.ConsolesOnMap(map).Count() == 2
                && ShipRegistry.MainConsoleForOwner(map, 28111) == primary
                && frame.Ships.Single().Anchor == first,
                "custom Def and derived Comp are recognized; vanilla Def alone has no console identity");

            var messages = AccessTools.Method(typeof(Messages), nameof(Messages.Message),
                new[] { typeof(string), typeof(MessageTypeDef), typeof(bool) });
            harmony.Patch(messages, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMissionMessage)));
            try
            {
                backup.MakeMainConsole();
                Check(backup.isMain && !primary.isMain && ShipRegistry.MainConsoleForOwner(map, 28111) == backup,
                    "derived console replaces and demotes a different Def's primary");
                primary.MakeMainConsole();
                Check(primary.isMain && !backup.isMain && ShipRegistry.ConsolesOnMap(map).Count(c => c.isMain) == 1,
                    "base console replaces a derived primary without duplicate claims");
            }
            finally { harmony.Unpatch(messages, HarmonyPatchType.Prefix, harmony.Id); }
            backup.shipId = -1;
            AccessTools.Method(typeof(SubstructureOwnership), "NotifyOwnershipChanged").Invoke(owner, null);
            Check(backup.shipId == 28111, "ownership changes synchronize derived backup identity");
            backup.shipId = 98113;
            AccessTools.Method(typeof(UniverseManager), "SyncOwnerIdFloor").Invoke(universe, null);
            Check(universe.AllocateOwnerId() > backup.shipId,
                "loaded custom backup identities reserve the global owner ID range");
            backup.shipId = 28111;

            var members = new HashSet<Building> { second, first };
            var preview = GravshipInterop.BuildPreview(cells, first, map, buildings: members);
            var captured = GravshipInterop.CaptureDomain(map, cells, first, null,
                includeTerrain: false, buildings: members);
            Check(preview.PilotConsole == first && captured.PilotConsole == first
                && preview.ThingPlacements[first].local == captured.ThingPlacements[first].local,
                "preview and actual capture preserve the same explicit custom primary and anchor");
            members = new HashSet<Building> { first, second };
            captured = GravshipInterop.CaptureDomain(map, cells, first, null,
                includeTerrain: false, buildings: members);
            Check(captured.PilotConsole == first, "capture order cannot replace a primary with a backup");
            var wreck = GravshipInterop.CaptureDomain(map, cells, null, null,
                includeTerrain: false, buildings: members);
            Check(wreck.PilotConsole == null, "a capture without an explicit primary does not invent one");

            var engine = Raw<Building_GravEngine>(); engine.def = PilotFixtureDef("pilotFixtureEngine");
            Set(engine, "mapIndexOrState", (sbyte)0); Set(engine, "substructureDirty", false);
            Set(engine, "validSubstructure", new HashSet<IntVec3>(cells));
            Gravship NativeShip()
            {
                var ship = (Gravship)AccessTools.Constructor(typeof(Gravship), Type.EmptyTypes).Invoke(null);
                Set(ship, "engine", engine);
                return ship;
            }
            void Add(Gravship ship, Thing thing) => addThing.Invoke(ship, new object[] { thing, thing.Position });
            foreach (bool reverse in new[] { false, true })
            {
                var ship = NativeShip();
                Add(ship, reverse ? second : first); Add(ship, reverse ? first : second);
                Check(ship.PilotConsole == first, "native AddThing chooses custom primary in either order");
                Set(ship, "pilotConsole", second);
                Check(CompDeltaVPilotConsole.IdentityConsoleOf(ship) == primary,
                    "identity recovery prefers the carried primary over a linked backup");
            }
            var excluded = NativeShip(); Add(excluded, impostor);
            Check(!excluded.ContainsThing(impostor) && excluded.PilotConsole == null,
                "native rejected buildings cannot become the pilot console");
            Set(impostor, "positionInt", first.Position);
            var acceptedImpostor = NativeShip(); Add(acceptedImpostor, impostor);
            Check(acceptedImpostor.ContainsThing(impostor) && acceptedImpostor.PilotConsole == null,
                "native vanilla Def without a console component gains no Delta-V pilot identity");
            Set(impostor, "positionInt", new IntVec3(4, 0, 2));
            var nativeBackupDef = second.def;
            second.def = ThingDefOf.PilotConsole;
            var mixed = NativeShip(); Add(mixed, first); Add(mixed, second);
            Check(mixed.PilotConsole == first, "native vanilla Def backup cannot overwrite custom primary");
            second.def = nativeBackupDef;
            universe.objects.Clear();
            Set(universe, "objectByMap", null);
            var ground = NativeShip(); Add(ground, first);
            Check(ground.PilotConsole == null, "non-Delta-V ground capture keeps native behavior");
            universe.objects.Add(frame);
            var empty = Place(custom, new DerivedPilotConsole { shipId = -1 }, new IntVec3(5, 0, 2), 28115);
            var late = empty.TryGetComp<DerivedPilotConsole>();
            Check(ShipRegistry.ConsolesOnMap(map).Contains(late), "new console appears after a warm building index");
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(empty.Position)] = deck;
            owner.Assign(empty.Position, 28111);
            late.PostSpawnSetup(false);
            Check(!late.isMain && late.shipId == 28111,
                "derived console spawn joins an existing claim as backup without replacing the primary");
            Set(empty, "mapIndexOrState", (sbyte)-1);
            late.PostDeSpawn(map);
            Check(!ShipRegistry.ConsolesOnMap(map).Contains(late), "despawn removes a console from the warm index");
            var newMain = Place(derived, new DerivedPilotConsole(), new IntVec3(6, 0, 4), 28116);
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(newMain.Position)] = deck;
            owner.Assign(newMain.Position, 28117);
            var newClaim = newMain.TryGetComp<DerivedPilotConsole>();
            newClaim.PostSpawnSetup(false);
            Check(newClaim.isMain && newClaim.shipId == 28117
                && ShipRegistry.MainConsoleForOwner(map, 28117) == newClaim,
                "derived console spawn claims an owner with no primary through the production lifecycle");
        }
        finally
        {
            harmony.Unpatch(addThing, HarmonyPatchType.All, harmony.Id);
            harmony.Unpatch(foundationGetter, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(typeof(Building_GravEngine).TypeInitializer, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(refreshFloor, HarmonyPatchType.Prefix, harmony.Id);
            ThingDefOf.PilotConsole = oldConsole; WorldObjectDefOf.Gravship = oldGravship;
            ThingDefOf.GravEngine = oldEngine;
            DeltaVDefOf.DeltaV_Weapon_MissileLauncher = oldLauncher;
            LanguageDatabase.activeLanguage = oldLanguage;
            binding.SetValue(null, oldBinding);
            universe.objects.Clear(); universe.objects.AddRange(objectsBefore);
            Set(universe, "objectByMap", null);
            game.Maps.Clear(); game.Maps.AddRange(mapsBefore);
        }
    }
}
