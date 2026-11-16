using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Xml;
using DeltaV;
using HarmonyLib;
using RimExodus.Maps;
using RimWorld;
using RimWorld.Planet;
using Verse;
using UnityEngine;

internal static partial class Program
{
    static int checks;
    static readonly List<string> errors = new List<string>();
    static T Raw<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); checks++; }
    static bool Error(string text) { errors.Add(text); return false; }
    static void Set(object value, string name, object fieldValue) => AccessTools.Field(value.GetType(), name).SetValue(value, fieldValue);

    static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(args[0], name.Name + ".dll");
            if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try
        {
            if (args.Length == 5 && args[1] == "ship-motion")
            {
                Console.WriteLine(SearchMotion(args[2], double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture)));
                return 0;
            }
            var harmony = new Harmony("DeltaV.TutorialRegression");
            foreach (string method in new[] { "Error", "Warning" })
                harmony.Patch(AccessTools.Method(typeof(Log), method, new[] { typeof(string) }),
                    prefix: new HarmonyMethod(typeof(Program), nameof(Error)));
            using var logLock = Log.LockMessages();
            DeepProfiler.enabled = false;
            Game game = Raw<Game>();
            game.components = new List<GameComponent>();
            game.uniqueIDsManager = new UniqueIDsManager();
            game.tickManager = new TickManager();
            game.signalManager = new SignalManager();
            var world = Raw<World>();
            world.factionManager = new FactionManager();
            Set(game, "worldInt", world);
            Current.Game = game;
            // Supply the native type resolver with the Mod assembly, without invoking Mod/Unity startup.
            var pack = Raw<ModContentPack>();
            pack.assemblies = new ModAssemblyHandler(pack);
            pack.assemblies.loadedAssemblies.Add(typeof(TutorialRunState).Assembly);
            LoadedModManager.RunningModsListForReading.Add(pack);
            string prefabRoot = args[3];
            string gameData = Directory.Exists(Path.Combine(args[0], "Data"))
                ? Path.Combine(args[0],"Data") : Path.GetFullPath(Path.Combine(args[0], "../../Data"));
            ReadSourceDefs(gameData,
                Path.GetFullPath(Path.Combine(prefabRoot, "../../1.6/Defs")));
            string tutorialPrefabs = Path.Combine(prefabRoot, "Tutorial");
            var records = Path.GetDirectoryName(args[1]);
            string[] TutorialFiles(params string[] names) =>
                names.Select(name => Path.Combine(tutorialPrefabs, name + ".xml")).ToArray();
            if (args.Length > 4 && args[4] == "shipyard")
            {
                CheckPrefabContent(TutorialFiles("Shipyard"), authoredEnvironment: true, tutorial: true);
                string directory = Path.GetDirectoryName(args[1]);
                Geometry(Path.Combine(directory, "Shipyard.records.json"), game);
                PrefabLifeSupport(directory, game, "Shipyard");
                PrefabTurretGraphs(tutorialPrefabs, new[] { "Shipyard" });
                ShipyardStates(tutorialPrefabs, harmony);
                ShipyardTradeState(tutorialPrefabs, harmony);
                Check(errors.Count == 0, "Unexpected shipyard logs: " + string.Join("\n", errors));
                Console.WriteLine($"PASS {checks} shipyard geometry, power, life support and Scribe checks. No game launched.");
                return 0;
            }
            if (args.Length > 5 && args[4] == "ship-search")
            {
                string directory = args[5];
                using var batch = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "batch.json")));
                AccessTools.Field(typeof(DefOfHelper), "bindingNow").SetValue(null, true);
                try
                {
                    TerrainDefOf.Space = Raw<TerrainDef>();
                    DeltaVDefOf.DeltaV_DockingChannel = Raw<TerrainDef>();
                }
                finally { AccessTools.Field(typeof(DefOfHelper), "bindingNow").SetValue(null, false); }
                var selections = batch.RootElement.GetProperty("selected");
                Check(selections.GetArrayLength() > 0, "ship search supplied at least one accepted preview");
                CheckPrefabContent(selections.EnumerateArray().Select(selected =>
                    Path.Combine(directory, selected.GetProperty("name").GetString() + ".xml")));
                foreach (var selected in selections.EnumerateArray())
                {
                    string name = selected.GetProperty("name").GetString();
                    Check(File.Exists(Path.Combine(directory, name + ".xml")), "selected prefab exists: " + name);
                    SearchExport(directory,name);
                    Geometry(Path.Combine(directory, name + ".records.json"), game);
                    if (batch.RootElement.TryGetProperty("playerShell", out var shell))
                        VerifyPlayerShell(directory, name, shell, batch.RootElement.GetProperty("corridorWidths"));
                    if (selected.TryGetProperty("task", out var task))
                        VerifyTaskDeck(directory, name, batch.RootElement.GetProperty("deckLimits"), task);
                }
                foreach (var selected in selections.EnumerateArray())
                    VerifySearchMotion(Path.Combine(directory,selected.GetProperty("name").GetString()+".records.json"));
                Check(errors.Count == 0, "Unexpected generated assembly logs: " + string.Join("\n", errors));
                Console.WriteLine($"PASS {checks} generated ship checks. No game launched.");
                return 0;
            }
            if (args.Length > 4 && args[4] == "modules")
            {
                string modules = Path.Combine(prefabRoot, "Templates", "Modules");
                string assemblies = Path.Combine(prefabRoot, "Templates", "Assemblies");
                var files = Directory.GetFiles(modules, "*.xml").Concat(Directory.GetFiles(assemblies, "*.xml")).ToArray();
                CheckPrefabContent(files);
                foreach (string path in files)
                    Geometry(Path.Combine(Path.GetDirectoryName(args[1]), Path.GetFileNameWithoutExtension(path)
                        + ".records.json"), game);
                PrefabTurretGraphs(modules, new[] { "ModuleHeavyBattery", "ModuleRapidBattery",
                    "ModuleLaserBattery", "ModulePointDefense" });
                Check(errors.Count == 0, "Unexpected module logs: " + string.Join("\n", errors));
                Console.WriteLine($"PASS {checks} module geometry, power, rail-chain and prefab checks. No game launched.");
                return 0;
            }
            if (args.Length > 4 && args[4] == "range")
            {
                var layouts = new[] { "WeaponRange", "ShieldTarget" };
                CheckPrefabContent(TutorialFiles(layouts), authoredEnvironment: true, tutorial: true);
                PrefabTurretGraphs(tutorialPrefabs, new[] { "WeaponRange" });
                foreach (string layout in layouts)
                    Geometry(Path.Combine(records, layout + ".records.json"), game);
                State(game);
                Theory(game, harmony);
                RangeChecks(game, harmony, prefabRoot);
                Check(errors.Count == 0, "Unexpected range native logs: " + string.Join("\n", errors));
                Console.WriteLine($"PASS {checks} optional range checks: geometry, power, native Scribe, presets, firing sectors, native use-job signatures and optional progress. No game launched.");
                return 0;
            }
            if (args.Length > 4 && args[4] == "sector")
            {
                CheckPrefabContent(Directory.GetFiles(Path.Combine(prefabRoot, "Sector"), "*.xml", SearchOption.AllDirectories),
                    authoredEnvironment: true);
                SectorContentDefaults(prefabRoot);
                foreach (string category in new[] { "Craft", "Stations" })
                foreach (string prefab in Directory.GetFiles(Path.Combine(prefabRoot, "Sector", category), "*.xml"))
                {
                    string layout = Path.GetFileNameWithoutExtension(prefab);
                    Geometry(Path.Combine(records, layout + ".records.json"), game);
                }
                foreach (string layout in new[] { "SectorFighter", "SectorGunboat", "SectorFrigate",
                    "SectorDestroyer", "SectorCruiser", "SectorTradeStation", "SectorShipyard", "SectorFactoryStation" })
                    PrefabLifeSupport(records, game, layout);
                Berths(prefabRoot, records, sector: true);
                Check(errors.Count == 0, "Unexpected sector logs: " + string.Join("\n", errors));
                Console.WriteLine($"PASS {checks} sector prefab, generation-pool, geometry and berth checks. No game launched.");
                return 0;
            }
            ScenarioTranslation(prefabRoot);
            CheckPrefabContent(TutorialFiles("ArrivalStation", "TrainingShip", "Salvage", "Shipyard", "PrototypeShip",
                "WeaponRange", "ShieldTarget"), authoredEnvironment: true, tutorial: true);
            PrefabTurretGraphs(tutorialPrefabs);
            foreach (string layout in new[] { "TrainingShip", "Salvage", "Shipyard", "PrototypeShip" })
                Geometry(Path.Combine(records, layout + ".records.json"), game);
            ArrivalWreck(args[1], args[2], game);
            ArrivalClothing(game, gameData);
            State(game);
            Theory(game, harmony);
            FlightEvidence();
            string layoutDirectory = Path.GetDirectoryName(args[1]);
            PrefabLifeSupport(layoutDirectory, game, "ArrivalStation");
            PrefabLifeSupport(layoutDirectory, game, "Shipyard");
            FocusThread(game, harmony);
            Caskets(args[2]);
            Shelves(Path.Combine(tutorialPrefabs, "Salvage.xml"));
            PublicFacilities(game, harmony);
            Comms(game, harmony);
            Berths(prefabRoot, Path.GetDirectoryName(args[1]));
            CourseClearance(game);
            Paths();
            CatalogPaths();
            ShipyardStates(tutorialPrefabs, harmony);
            MissionChecks(game, harmony, prefabRoot);
            ShipyardTradeState(tutorialPrefabs, harmony);
            Check(errors.Count == 0, "Unexpected native logs: " + string.Join("\n", errors));
            Console.WriteLine($"PASS {checks} tutorial checks: native geometry/interaction/power, Scribe, phase guards, public access/storage/goodwill, focus thread, paths. No game launched.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine(string.Join("\n", errors)); return 1; }
        finally { Scribe.ForceStop(); }
    }

    static void Geometry(string path, Game game)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var map = Raw<Map>();
        int maxCell = json.RootElement.EnumerateArray().SelectMany(r => r.GetProperty("cells").EnumerateArray())
            .Max(c => Math.Max(c[0].GetInt32(), c[1].GetInt32()));
        map.info = new MapInfo { Size = new IntVec3(maxCell + 42, 1, maxCell + 42) };
        map.cellIndices = new CellIndices(map);
        map.thingGrid = new ThingGrid(map);
        map.terrainGrid = new TerrainGrid(map);
        map.edificeGrid = new EdificeGrid(map);
        map.roofGrid = new RoofGrid(map);
        map.floodFiller = new FloodFiller(map);
        Set(game, "maps", new List<Map> { map });
        var buildings = new List<Building>();
        var yardConnectors = new List<Building>();
        bool shipyard = Path.GetFileName(path) == "Shipyard.records.json";
        var affordances = new Dictionary<string, TerrainAffordanceDef>();
        TerrainAffordanceDef Affordance(string name)
        {
            if (!affordances.TryGetValue(name, out var value))
                affordances[name] = value = new TerrainAffordanceDef { defName = name };
            return value;
        }
        foreach (var row in json.RootElement.EnumerateArray())
        {
            string name = row.GetProperty("name").GetString();
            VerifySourceDef(row);
            var def = Raw<ThingDef>();
            def.defName = name;
            def.size = new IntVec2(row.GetProperty("size")[0].GetInt32(), row.GetProperty("size")[1].GetInt32());
            def.rotatable = row.GetProperty("rotatable").GetBoolean();
            def.category = Enum.Parse<ThingCategory>(row.GetProperty("category").GetString());
            def.building = new BuildingProperties();
            if (shipyard)
            {
                bool BuildingFlag(string flag, bool fallback) => SourceChain(name)
                    .Select(n => n.SelectSingleNode("building/" + flag)?.InnerText)
                    .Where(value => value != null).Select(bool.Parse).DefaultIfEmpty(fallback).First();
                def.building.isAttachment = BuildingFlag("isAttachment", false);
                def.building.supportsWallAttachments = BuildingFlag("supportsWallAttachments", false);
                def.building.allowWireConnection = BuildingFlag("allowWireConnection", true);
            }
            def.holdsRoof = row.GetProperty("holdsRoof").GetBoolean();
            string needed = row.GetProperty("terrainAffordance").GetString();
            def.terrainAffordanceNeeded = string.IsNullOrEmpty(needed) ? null : Affordance(needed);
            def.passability = Enum.Parse<Traversability>(row.GetProperty("passability").GetString());
            def.hasInteractionCell = row.GetProperty("hasInteraction").GetBoolean();
            def.interactionCellOffset = new IntVec3(row.GetProperty("interaction")[0].GetInt32(), 0,
                row.GetProperty("interaction")[2].GetInt32());
            var pos = new IntVec3(row.GetProperty("root")[0].GetInt32(), 0, row.GetProperty("root")[1].GetInt32());
            var rot = new Rot4(row.GetProperty("rotation").GetInt32());
            var expected = row.GetProperty("cells").EnumerateArray().Select(c => new IntVec3(c[0].GetInt32(), 0, c[1].GetInt32())).ToHashSet();
            var support = Raw<TerrainDef>();
            support.affordances = row.GetProperty("supportAffordances").EnumerateArray()
                .Select(v => Affordance(v.GetString())).ToList();
            foreach (var c in expected) map.terrainGrid.topGrid[map.cellIndices.CellToIndex(c)] = support;
            if (def.category == ThingCategory.Building)
                Check(GenConstruct.CanBuildOnTerrain(def, pos, map, rot), name + " native prefab terrain support");
            if (name == "Door" && Path.GetFileName(path) == "ArrivalStation.records.json")
            {
                var portDef = Raw<ThingDef>(); portDef.size = IntVec2.One;
                portDef.building = new BuildingProperties(); portDef.terrainAffordanceNeeded = Affordance("Substructure");
                Check(!GenConstruct.CanBuildOnTerrain(portDef, pos, map, rot), "old substructure-only port rejected on arrival platform");
            }
            Check(expected.SetEquals(GenAdj.OccupiedRect(pos, rot, def.size)), name + " native occupied rectangle");
            if (row.TryGetProperty("exclusionSize", out var exclusion))
            {
                var offset = row.GetProperty("exclusionOffset");
                var props = new CompProperties_GravshipThruster {
                    exclusionAreaSize = new IntVec3(exclusion[0].GetInt32(), 0, exclusion[2].GetInt32()),
                    exclusionAreaOffset = new IntVec3(offset[0].GetInt32(), 0, offset[2].GetInt32()) };
                List<IntVec3> cells = null;
                props.GetExclusionZone(pos, rot, ref cells);
                var authored = row.GetProperty("exclusionCells").EnumerateArray()
                    .Select(c => new IntVec3(c[0].GetInt32(), 0, c[1].GetInt32())).ToHashSet();
                Check(authored.SetEquals(cells), name + " native exclusion zone matches authored clearance");
            }
            for (int n = 0; n < 4; n++)
            {
                var turn = new Rot4(n);
                var transformed = PrefabUtility.GetAdjustedThingLocalPosition(def, turn, pos);
                var facing = def.rotatable ? new Rot4((rot.AsInt+n)%4) : Rot4.North;
                Check(expected.Select(c => PrefabUtility.GetAdjustedLocalPosition(c, turn)).ToHashSet()
                    .SetEquals(GenAdj.OccupiedRect(transformed, facing, def.size)), name + " native prefab rotation " + n);
            }
            Building b = Raw<Building>();
            b.def = def;
            Set(b, "mapIndexOrState", (sbyte)0);
            Set(b, "positionInt", pos + new IntVec3(20, 0, 20));
            Set(b, "rotationInt", rot);
            bool transmits = row.GetProperty("transmitsPower").GetBoolean();
            var transmitter = transmits ? new CompProperties_Power { transmitsPower = true } : null;
            def.comps = transmitter == null ? new List<CompProperties>() : new List<CompProperties> { transmitter };
            Set(b, "comps", transmits ? new List<ThingComp> { new CompPowerTrader
                { parent = b, props = transmitter } } : null);
            map.thingGrid.Register(b);
            if (def.holdsRoof) map.edificeGrid.Register(b);
            buildings.Add(b);
            if (shipyard && !transmits && row.GetProperty("powerWatts").GetSingle() != 0)
                yardConnectors.Add(b);
        }
        foreach (Building b in buildings)
        {
            if (b.def.HasSingleOrMultipleInteractionCells)
                Check(GenConstruct.InteractionCellStandable(b.def, b.Position, b.Rotation, map, b).Accepted,
                    b.def.defName + " native interaction cell");
            Check(GenConstruct.NotBlockingAnyInteractionCells(b.def, b.Position, b.Rotation, map, b).Accepted,
                b.def.defName + " does not block another interaction");
        }
        var transmitters = buildings.Where(b => b.TransmitsPowerNow).ToList();
        string roofPath = path.Replace(".records.json", ".layout.json");
        if (File.Exists(roofPath))
        {
            using var layout = JsonDocument.Parse(File.ReadAllText(roofPath));
            var roof = Raw<RoofDef>();
            var grid = (RoofDef[])AccessTools.Field(typeof(RoofGrid), "roofGrid").GetValue(map.roofGrid);
            var cells = layout.RootElement.GetProperty("roofs").EnumerateArray()
                .Select(c => new IntVec3(c[0].GetInt32()+20, 0, c[1].GetInt32()+20)).ToList();
            foreach (var cell in cells) grid[map.cellIndices.CellToIndex(cell)] = roof;
            foreach (var cell in cells)
                Check(RoofCollapseUtility.WithinRangeOfRoofHolder(cell, map), Path.GetFileName(path) + " supported roof " + cell);
            if (layout.RootElement.TryGetProperty("mirrorAxis", out var axis))
                CraftSymmetry(json.RootElement, layout.RootElement, buildings, axis.GetInt32());
        }
        var powerCells = new HashSet<IntVec3>();
        Check(transmitters.All(b => b.OccupiedRect().All(c => powerCells.Add(c))), "no overlapping power transmitters");
        if (transmitters.Count != 0)
        {
            if (Path.GetFileName(path) == "ShieldTarget.records.json") RangePowerNet(transmitters, path);
            else
            {
                PowerNet net = PowerNetMaker.NewPowerNetStartingFrom(transmitters[0]);
                Check(net.transmitters.Count == transmitters.Count, "native single connected power net");
                foreach (var b in yardConnectors)
                {
                    var mount = b.def.building.isAttachment ? GenConstruct.GetWallAttachedTo(b) : b;
                    Check(mount != null, b.def.defName + " native wall mount");
                    Check(PowerConnectionMaker.BestTransmitterForConnector(mount.Position, map)?.transNet == net,
                        b.def.defName + " connects to sparse shipyard power grid at " + b.Position);
                }
            }
        }
        var thrusters = buildings.Where(b => b.def.defName == "SmallThruster" || b.def.defName == "LargeThruster").ToList();
        if (Path.GetFileName(path) == "WeaponRange.records.json") RangeLayout(buildings, path);
        if (Path.GetFileName(path) == "SectorDestroyer.records.json") RangeLayout(buildings, path, 7);
        if (Path.GetFileName(path) == "ModuleRailgun.records.json") RangeLayout(buildings, path, 5);
        if (Path.GetFileName(path) == "AssemblyCombat.records.json") RangeLayout(buildings, path, 5);
        if (Path.GetFileName(path) == "ModuleBedroom.records.json")
        {
            var beds = buildings.Where(b => b.def.defName == "Bed").ToList();
            var tables = buildings.Where(b => b.def.defName == "EndTable").ToList();
            Check(beds.Count == 2 && tables.Count == 2, "two private bedrooms each have a bed and end table");
            foreach (var bed in beds)
            {
                var head = BedUtility.GetSleepingSlotPos(0, bed.Position, bed.Rotation, bed.def.size);
                Check(tables.Count(t => GenAdj.CardinalDirections.Any(d => t.Position == head + d)) == 1,
                    "end table touches its native bed head");
            }
        }
        if (Path.GetFileName(path) == "ModuleMissile.records.json")
        {
            var launchers = buildings.Where(b => b.def.defName == "DeltaV_Weapon_MissileLauncher").ToList();
            var magazines = buildings.Where(b => b.def.defName == "DeltaV_Building_MissileMagazine").ToList();
            Check(launchers.Count == 3 && magazines.Count == 2, "three launchers share two magazines");
            var sockets = sourceDefs[launchers[0].def.defName].SelectNodes(
                "comps/li[@Class='DeltaV.CompProperties_MissileAutoloader']/magazineRootOffsets/li");
            var properties = new CompProperties_MissileAutoloader {
                magazineDefs = magazines.Select(b => b.def).ToList(),
                magazineRootOffsets = sockets.Cast<XmlNode>().Select(n => IntVec3.FromString(n.InnerText)).ToList()
            };
            foreach (var launcher in launchers)
                launcher.def.comps = new List<CompProperties> { properties };
            for (int n = 0; n < 4; n++)
            {
                var turn = new Rot4(n);
                IntVec3 Root(Building b) => PrefabUtility.GetAdjustedThingLocalPosition(b.def, turn, b.Position);
                bool Fits(Building launcher, Building magazine) => SpaceMissileMagazineGeometry.Fits(
                    launcher.def, Root(launcher), turn, magazine.def, Root(magazine), turn);
                Check(magazines.All(magazine => launchers.Count(launcher => Fits(launcher, magazine)) == 2),
                    "each magazine links to two launchers in native rotation " + n);
                foreach (var launcher in launchers)
                {
                    int expected = launcher.Position.x == launchers[1].Position.x ? 2 : 1;
                    Check(magazines.Count(magazine => Fits(launcher, magazine)) == expected,
                        "launcher links to the adjacent magazines in native rotation " + n);
                }
            }
        }
        if (thrusters.Count != 0)
        {
            var report = new ShipPhysics.MassReport { mass = 1000f, inertia = 30000f,
                comLocal = new Vector2((float)buildings.Average(b => b.DrawPos.x), (float)buildings.Average(b => b.DrawPos.z)) };
            bool sector = Path.GetFileName(path).StartsWith("Sector", StringComparison.Ordinal);
            bool assembly = Path.GetFileName(path).StartsWith("Assembly", StringComparison.Ordinal);
            bool generated = Path.GetFileName(path).StartsWith("AssemblyGenerated", StringComparison.Ordinal);
            bool fighter = Path.GetFileName(path) == "SectorFighter.records.json";
            if (fighter)
                Check(thrusters.Count(b => b.Rotation == Rot4.North) == 4 && thrusters.Count(b => b.Rotation == Rot4.South) == 2
                    && thrusters.Count(b => b.Rotation == Rot4.East) == 2 && thrusters.Count(b => b.Rotation == Rot4.West) == 2,
                    "fighter has four main engines, two braking engines and two lateral engines per side");
            var authoredThrusters = json.RootElement.EnumerateArray()
                .Where(row => row.TryGetProperty("exclusionSize", out _)).ToList();
            if (sector || assembly) ApplySectorMass(json.RootElement, roofPath, report);
            for (int i = 0; i < thrusters.Count; i++)
            {
                Building b = thrusters[i];
                var rect = b.OccupiedRect();
                float thrust = ShipPhysics.ThrusterThrust;
                if (sector || assembly)
                {
                    float limit = authoredThrusters[i].GetProperty("stateOverrides").GetProperty("DeltaV_powerPercent").GetSingle();
                    Check(limit > 0 && limit <= 100, b.def.defName + " valid authored power limit");
                    thrust *= limit * .01f;
                }
                report.thrusters.Add(new ShipPhysics.Thruster { localPos = new Vector2(rect.minX + rect.Width*.5f, rect.minZ + rect.Height*.5f),
                    dir = ShipPhysics.ThrustDir(b.Rotation), thrust = thrust, operational = true });
            }
            var accelerationByAxis = new List<float>();
            var commands = new[] { new ShipPhysics.Command { fx=1 }, new ShipPhysics.Command { fx=-1 },
                new ShipPhysics.Command { fz=1 }, new ShipPhysics.Command { fz=-1 },
                new ShipPhysics.Command { torque=1 }, new ShipPhysics.Command { torque=-1 } };
            foreach (var command in commands)
            {
                ShipPhysics.PreviewAllocation(report, command, out var force, out float torque);
                Check(command.torque != 0 ? torque * command.torque > 1f
                    : Vector2.Dot(force, new Vector2(command.fx,command.fz)) > 1f,
                    Path.GetFileName(path) + " has real allocator authority in all six directions");
                if (assembly && command.torque == 0)
                    accelerationByAxis.Add(Vector2.Dot(force, new Vector2(command.fx, command.fz)) / report.mass);
                if (fighter && command.torque == 0)
                {
                    float minimum = command.fz > 0 ? 4.5f : 2.2f;
                    float acceleration = Vector2.Dot(force, new Vector2(command.fx, command.fz)) / report.mass;
                    Check(acceleration >= minimum, $"fighter acceleration toward ({command.fx}, {command.fz}) is {acceleration:F2}");
                }
                if (sector)
                    Console.WriteLine($"{Path.GetFileNameWithoutExtension(roofPath)}: ({command.fx}, {command.fz}, {command.torque}) " +
                        $"mass={report.mass:F0}, force=({force.x:F0}, {force.y:F0}), acceleration={force.magnitude/report.mass:F2}, " +
                        $"angular acceleration={torque/report.inertia:F3} rad/s²; empty, healthy ship.");
            }
            if (assembly)
                Console.WriteLine($"{Path.GetFileNameWithoutExtension(path)} empty mass {report.mass:F0}, "
                    + $"forward {accelerationByAxis[2]:F2}, reverse {accelerationByAxis[3]:F2}, "
                    + $"east {accelerationByAxis[0]:F2}, west {accelerationByAxis[1]:F2} cells/s²");
            if (generated)
            {
                var pilot = buildings.Single(b => b.def.defName == "PilotConsole");
                Check(pilot.Rotation == Rot4.North, "generated motion report axes match the pilot frame");
                var port = buildings.Single(b => b.def.defName == "DeltaV_DockingPort");
                var facing = port.Rotation.FacingCell;
                var target = port.Position + facing * 4;
                using var layout = JsonDocument.Parse(File.ReadAllText(roofPath));
                var foundation = layout.RootElement.GetProperty("foundation").EnumerateArray()
                    .Select(c => new IntVec3(c[0].GetInt32() + 20, 0, c[1].GetInt32() + 20)).ToHashSet();
                for (int n = 1; n <= 3; n++)
                {
                    var c = port.Position + facing * n;
                    Check(!foundation.Contains(c) && !buildings.Any(b => b.OccupiedRect().Contains(c)), "clear docking approach");
                    map.terrainGrid.topGrid[map.cellIndices.CellToIndex(c)] = TerrainDefOf.Space;
                }
                map.terrainGrid.topGrid[map.cellIndices.CellToIndex(target)] = Raw<TerrainDef>();
                var channel = new List<IntVec3>();
                Check(Building_DockingPort.TryFindChannel(map, port.Position, port.Rotation, 8, channel, out var face)
                      && face == target && channel.Count == 3, "native probe reaches a docking face across three vacuum cells");
            }
            if (Path.GetFileName(path) == "AssemblyIndustrial.records.json")
            {
                ApplySectorMass(json.RootElement, roofPath, report, steelStacksPerHold: 12);
                ShipPhysics.PreviewAllocation(report, new ShipPhysics.Command { fz = 1 }, out var forward, out _);
                ShipPhysics.PreviewAllocation(report, new ShipPhysics.Command { fz = -1 }, out var reverse, out _);
                Check(forward.y > 0 && reverse.y < 0, "loaded industrial ship can accelerate and brake");
                Console.WriteLine($"AssemblyIndustrial with 48 stacks of steel, mass {report.mass:F0}, "
                    + $"forward {forward.y / report.mass:F2}, reverse {-reverse.y / report.mass:F2} cells/s²");
            }
            if (Path.GetFileName(path) == "SectorCruiser.records.json")
                CruiserAcceleration(report, thrusters);
            if (Path.GetFileName(path) == "TrainingShip.records.json") TrainerStrafe(report, thrusters);
            if (Path.GetFileName(path) == "PrototypeShip.records.json") FreighterLoads(path, report);
        }
    }

    static void CraftSymmetry(JsonElement records, JsonElement layout, List<Building> buildings, int axis)
    {
        IntVec3 Reflect(IntVec3 c) => new IntVec3(2*(axis+20)-c.x, 0, c.z);
        bool symmetricShell = !layout.TryGetProperty("symmetricShell", out var symmetry) || symmetry.GetBoolean();
        foreach (string layer in symmetricShell ? new[] { "foundation", "roofs" } : Array.Empty<string>())
        {
            var cells = layout.GetProperty(layer).EnumerateArray()
                .Select(c => new IntVec3(c[0].GetInt32()+20, 0, c[1].GetInt32()+20)).ToHashSet();
            Check(cells.SetEquals(cells.Select(Reflect)), layer + " mirrored about the ship centerline");
        }
        var pairs = records.EnumerateArray().Zip(buildings, (row, b) => (row, b))
            .Where(entry => entry.row.TryGetProperty("mirrorPair", out _))
            .GroupBy(entry => entry.row.GetProperty("mirrorPair").GetInt32()).ToList();
        Check(pairs.Count > 0, "craft has mirrored equipment");
        foreach (var pair in pairs)
        {
            Check(pair.Count() == 2, "two instances per mirrored equipment pair");
            var a = pair.First().b;
            var b = pair.Last().b;
            Check(a.def.defName == b.def.defName && (a.Rotation.AsInt + b.Rotation.AsInt) % 4 == 0,
                a.def.defName + " mirrored fixture facing");
            Check(a.OccupiedRect().Select(Reflect).ToHashSet().SetEquals(b.OccupiedRect()),
                a.def.defName + " mirrored native footprint");
        }
        var consoles = buildings.Where(b => b.def.defName == "PilotConsole").ToList();
        bool cruiser = layout.GetProperty("name").GetString() == "SectorCruiser";
        Check(consoles.Count == (cruiser ? 2 : 1), "craft has the expected number of control stations");
        foreach (var b in consoles.Take(1))
            Check(b.OccupiedRect().Select(Reflect).ToHashSet().SetEquals(b.OccupiedRect()),
                "primary console has a centered native footprint");
        if (cruiser)
        {
            Building main = consoles[0], backup = consoles[1];
            Check(main.Rotation == backup.Rotation && main.Rotation == Rot4.North
                && main.OccupiedRect().minX > backup.OccupiedRect().maxX
                && main.OccupiedRect().minZ > backup.OccupiedRect().maxZ,
                "cruiser backup faces the same way and is clear of the primary's axial firing lanes");
            var walls = buildings.Where(b => b.def.defName == "GravshipHull")
                .SelectMany(b => b.OccupiedRect()).ToHashSet();
            foreach (int x in Enumerable.Range(main.OccupiedRect().minX, main.OccupiedRect().Width))
                Check(walls.Contains(new IntVec3(x, 0, main.OccupiedRect().maxZ+1))
                    && walls.Contains(new IntVec3(x, 0, main.OccupiedRect().maxZ+2)),
                    "two bow layers cover the primary console footprint");
            var reactor = buildings.Single(b => b.def.defName == "DeltaV_ShipReactor").OccupiedRect();
            for (int z = reactor.minZ; z <= reactor.maxZ; z++)
            {
                var row = walls.Where(c => c.z == z).Select(c => c.x).OrderBy(x => x).ToArray();
                Check(row.Length >= 5 && row[0]+1 == row[1] && row[^3]+1 == row[^2] && row[^2]+1 == row[^1]
                    && row[1] < reactor.minX && row[^3] > reactor.maxX,
                    "two port layers and three starboard layers cover the reactor");
            }
            var shields = records.EnumerateArray().Zip(buildings, (row, b) => (row, b))
                .Where(entry => entry.b.def.defName == "DeltaV_ShieldGenerator").ToList();
            Check(shields.Count == 2 && shields[0].b.Position.z != shields[1].b.Position.z,
                "cruiser has two separated guard emitters");
            Check(layout.GetProperty("generationW").GetSingle() - layout.GetProperty("nameplateLoadW").GetSingle()
                >= shields.Count * new CompProperties_SpaceShield().recoveryPowerWatts,
                "cruiser has headroom to recharge both guard shields together");
            foreach (var (row, b) in shields)
            {
                var state = row.GetProperty("stateOverrides");
                float radius = state.GetProperty("DeltaV_shieldGroundRadius").GetSingle();
                var comp = new CompSpaceShield { parent = b, props = new CompProperties_SpaceShield() };
                var center = (Vector2)AccessTools.Property(typeof(CompSpaceShield), "GuardCenter").GetValue(comp);
                Check(state.GetProperty("DeltaV_shieldMode").GetString() == "Guard"
                    && state.GetProperty("DeltaV_regularCapacity").GetSingle() == comp.ShieldProps.regularCapacity
                    && state.GetProperty("fuel").GetSingle() == comp.ShieldProps.emergencyCapacity
                    && state.GetProperty("currentHitPoints").GetInt32() == -1
                    && radius <= comp.ShieldProps.maxGroundRadius,
                    "cruiser emitter starts in guard mode with both capacities full");
                foreach (var cell in layout.GetProperty("foundation").EnumerateArray())
                    for (int x = 0; x <= 1; x++)
                    for (int z = 0; z <= 1; z++)
                        Check((new Vector2(cell[0].GetInt32()+20+x, cell[1].GetInt32()+20+z) - center).sqrMagnitude
                            < radius*radius, "each guard circle covers the actual hull, including the cell corners");
            }
            var generators = buildings.Where(b => b.def.defName == "ChemfuelPoweredGenerator").ToList();
            var reserve = buildings.Single(b => b.def.defName == "DeltaV_SafetyBattery").OccupiedRect();
            Check(generators.Count == 2 && generators.All(b => b.OccupiedRect().minZ > reactor.maxZ)
                && reserve.minZ > reactor.maxZ,
                "cruiser emergency generation and storage are in a different bay from the reactor");
        }
    }

    static void CruiserAcceleration(ShipPhysics.MassReport report, List<Building> thrusters)
    {
        Check(thrusters.Count(b => b.Rotation == Rot4.North) == 8 && thrusters.Count(b => b.Rotation == Rot4.South) == 6
            && thrusters.Count(b => b.Rotation == Rot4.East) == 4 && thrusters.Count(b => b.Rotation == Rot4.West) == 4,
            "cruiser has eight main engines, six braking engines and four lateral engines per side");
        // Model evenly distributed extra mass with unchanged COM and the same mass distribution.
        report.mass *= 1.2f;
        report.inertia *= 1.2f;
        foreach (var (direction, minimum) in new[] { (new Vector2(0, 1), 1.50f), (new Vector2(0, -1), 1.10f),
                     (new Vector2(1, 0), .68f), (new Vector2(-1, 0), .68f) })
        {
            ShipPhysics.PreviewAllocation(report, new ShipPhysics.Command { fx=direction.x, fz=direction.y },
                out var force, out _);
            float acceleration = Vector2.Dot(force, direction)/report.mass;
            Check(acceleration >= minimum, $"cruiser loaded acceleration toward {direction} is {acceleration:F2}");
            Console.WriteLine($"SectorCruiser: 20% distributed load, ({direction.x}, {direction.y}) "
                + $"acceleration={acceleration:F2} cells/s².");
        }
    }

    static void TrainerStrafe(ShipPhysics.MassReport report, List<Building> buildings)
    {
        Check(buildings.Count == 6 && buildings.Count(b => b.def.defName == "LargeThruster") == 4,
            "trainer has exactly four main engines and two lateral RCS");
        Check(buildings.Count(b => b.Rotation == Rot4.North) == 2 && buildings.Count(b => b.Rotation == Rot4.South) == 2
            && buildings.Count(b => b.Rotation == Rot4.East) == 1 && buildings.Count(b => b.Rotation == Rot4.West) == 1,
            "trainer has two forward, two reverse and one RCS for each lateral direction");
        // Healthy engines at their production thrust. Sweep a declared COM envelope rather than
        // presenting this offline fixture as a full in-game mass/SpawnSetup calculation.
        foreach (float x in new[] { 32.5f, 33f, 33.5f })
        foreach (float z in new[] { 27f, 27.5f, 28f })
        foreach (int direction in new[] { -1, 1 })
        {
            report.comLocal = new Vector2(x, z);
            var lateral = report.thrusters.Where(t => t.dir.x != 0).ToList();
            Check(lateral.All(t => t.localPos.y < z), "both lateral RCS are aft of COM");
            Check(report.thrusters.Where(t => t.dir.y != 0).All(t => Mathf.Abs(t.localPos.x-x)
                < lateral.Min(r => z-r.localPos.y)), "main engines have shorter torque arms than stern RCS");
            ShipPhysics.PreviewAllocation(report, new ShipPhysics.Command { fx=direction }, out var force, out float torque);
            float mainTorque = 0, rcsTorque = 0, forward = 0, reverse = 0;
            for (int i=0; i<report.thrusters.Count; i++)
            {
                var t = report.thrusters[i];
                Vector2 f = t.dir * (t.thrust * report.previewScratch[i]);
                Vector2 arm = t.localPos - report.comLocal;
                float moment = arm.x*f.y-arm.y*f.x;
                if (t.dir.x != 0) rcsTorque += moment;
                else { mainTorque += moment; if (f.y > 0) forward += f.y; else reverse -= f.y; }
            }
            Check(force.x*direction > 100f && Mathf.Abs(force.y) < Mathf.Abs(force.x)*.1f,
                "lateral command produces sideways motion with little fore/aft drift");
            Check(forward > 100f && reverse > 100f && mainTorque*rcsTorque < 0f,
                "both forward and reverse main engines counter stern RCS torque");
            Check(Mathf.Abs(torque) < Mathf.Abs(rcsTorque)*.15f,
                "main engines cancel at least 85 percent of stern RCS turning moment");
        }
        Console.WriteLine("Trainer: both strafe directions recruit forward/reverse engines across COM x=12.5..13.5, z=7..8; healthy engines, mass=1000, inertia=30000.");
    }

    static bool AssetsPresent(ref bool __result) { __result = true; return false; }

    static void Theory(Game game, Harmony harmony)
    {
        var universe = game.GetComponent<UniverseManager>();
        var quest = new Quest { id = 82, acceptanceTick = 0 };
        var run = new TutorialRunState { stage = TutorialStage.Design, quest = quest };
        universe.tutorialRun = run;
        var assetCheck = AccessTools.Method(typeof(DeltaVTutorialService), "CheckFlightAssets");
        // Isolate facility existence; exercise the actual quest signal/phase/queue logic.
        harmony.Patch(assetCheck, prefix: new HarmonyMethod(typeof(Program), nameof(AssetsPresent)));
        try
        {
            universe.QueueFrameAction(() => { });
            var actions = (List<Action>)AccessTools.Field(typeof(UniverseManager), "frameActions").GetValue(universe);
            actions.Clear();
            universe.Tutorial.Receive(quest, DeltaVTutorialService.Signal(quest, "CheckFlight"));
            Check(run.stage == TutorialStage.Design && actions.Count == 0, "flight answer cannot skip theory");
            universe.Tutorial.Receive(quest, DeltaVTutorialService.Signal(quest, "StartPractice"));
            Check(run.stage == TutorialStage.Flight && actions.Count == 1, "theory acknowledgement opens flight without construction");
            universe.Tutorial.Receive(quest, DeltaVTutorialService.Signal(quest, "StartPractice"));
            universe.Tutorial.Receive(quest, DeltaVTutorialService.Signal(quest, "DeferDesign"));
            Check(run.stage == TutorialStage.Flight && actions.Count == 1, "stale theory replies cannot replay or quit flight");
            Check(run.forwardTicks == 0 && run.lateralTicks == 0 && run.turnTicks == 0, "theory grants no practical flight credit");
            actions.Clear();
            universe.Tutorial.Ended(quest);
        }
        finally { harmony.Unpatch(assetCheck, HarmonyPatchType.Prefix, harmony.Id); }
    }

    static void Berths(string directory, string records, bool sector = false)
    {
        AccessTools.Field(typeof(DefOfHelper), "bindingNow").SetValue(null, true);
        try { TerrainDefOf.Space = Raw<TerrainDef>(); }
        finally { AccessTools.Field(typeof(DefOfHelper), "bindingNow").SetValue(null, false); }
        var berths = sector
            ? new[] { "SectorFighter", "SectorGunboat", "SectorFrigate", "SectorDestroyer", "SectorCruiser" }
                .SelectMany(ship => new[] { ("SectorShipyard", ship, Rot4.East), ("SectorShipyard", ship, Rot4.West) })
            : new[] { ("ArrivalStation", "TrainingShip", Rot4.West), ("Shipyard", "PrototypeShip", Rot4.West),
                ("Shipyard", "TrainingShip", Rot4.East) };
        foreach (var pair in berths)
        {
            var host = new XmlDocument(); host.Load(Path.Combine(directory, sector ? "Sector/Stations" : "Tutorial", pair.Item1 + ".xml"));
            var ship = new XmlDocument(); ship.Load(Path.Combine(directory, sector ? "Sector/Craft" : "Tutorial", pair.Item2 + ".xml"));
            HashSet<IntVec3> Hull(XmlDocument doc) => doc.SelectNodes("/MapPrefab/foundations/li/pos")
                .Cast<XmlNode>().Select(n => IntVec3.FromString(n.InnerText)).ToHashSet();
            var hostHull = Hull(host); var shipHull = Hull(ship);
            if (pair.Item1 == "ArrivalStation")
            {
                Check(hostHull.Count == 0, "colony wreck has orbital platform rather than gravship substructure");
                foreach (XmlNode row in host.SelectNodes("/MapPrefab/prefab/terrain/OrbitalPlatform/rects/li"))
                {
                    var numbers = row.InnerText.Trim('(', ')').Split(',').Select(int.Parse).ToArray();
                    foreach (var c in new CellRect(numbers[0], numbers[1], numbers[2]-numbers[0]+1, numbers[3]-numbers[1]+1)) hostHull.Add(c);
                }
                Check(hostHull.Count != 0, "colony wreck has an actual platform footprint");
                var names = host.SelectNodes("/MapPrefab/thingStates/li/def").Cast<XmlNode>().Select(n => n.InnerText).ToHashSet();
                Check(names.Contains("Ship_Beam") && names.Contains("Ship_Reactor") && names.Contains("Wall") && !names.Contains("DeltaV_DockingPort")
                    && !names.Contains("PilotConsole") && !names.Contains("SmallThruster") && !names.Contains("Ship_Engine")
                    && !names.Contains("Ship_ComputerCore") && !names.Contains("Ship_SensorCluster"), "looted interstellar ship equipment contract");
            }
            // Use the generated instance records; runtime binds these exact port roles by facing.
            (IntVec3, Rot4) Port(string name, Rot4 facing)
            {
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(records, name + ".records.json")));
                var rows = json.RootElement.EnumerateArray();
                var port = name == "ArrivalStation"
                    ? rows.Where(r => r.GetProperty("name").GetString() == "Door").OrderByDescending(r => r.GetProperty("root")[0].GetInt32()).First()
                    : rows.Single(r => r.GetProperty("name").GetString() == "DeltaV_DockingPort" && r.GetProperty("rotation").GetInt32() == facing.AsInt);
                return (new IntVec3(port.GetProperty("root")[0].GetInt32(), 0, port.GetProperty("root")[1].GetInt32()), facing);
            }
            bool westTutorialBerth = pair.Item1 == "Shipyard" && pair.Item3 == Rot4.East;
            var hostPort = Port(pair.Item1, sector ? Rot4.North : pair.Item3.Opposite);
            var shipPort = Port(pair.Item2, pair.Item3);
            if (sector)
            {
                shipHull = shipHull.Select(c => PrefabUtility.GetAdjustedLocalPosition(c, pair.Item3)).ToHashSet();
                shipPort = (PrefabUtility.GetAdjustedLocalPosition(shipPort.Item1, pair.Item3), Rot4.South);
            }
            var offset = new IntVec3(westTutorialBerth ? 40 : 12, 0, 12);
            var sourceRoot = new IntVec3(shipHull.Min(c => c.x), 0, shipHull.Min(c => c.z));
            var landing = DeltaVTutorialService.BerthPortCell(hostPort.Item1 + offset, hostPort.Item2)
                - (shipPort.Item1 - sourceRoot);
            var placed = shipHull.Select(c => c - sourceRoot + landing).ToHashSet();
            var stationary = hostHull.Select(c => c + offset).ToHashSet();
            Check(!placed.Overlaps(stationary), pair + " berth hulls do not overlap");
            Check(!placed.Any(c => GenAdj.CardinalDirections.Any(d => stationary.Contains(c + d))), pair + " no adjacent foundations between ships");
            var map = Raw<Map>(); map.info = new MapInfo { Size = new IntVec3(placed.Concat(stationary).Max(c => c.x) + 15, 1,
                placed.Concat(stationary).Max(c => c.z) + 15) };
            map.cellIndices = new CellIndices(map); map.thingGrid = new ThingGrid(map); map.terrainGrid = new TerrainGrid(map);
            Array.Fill(map.terrainGrid.topGrid, TerrainDefOf.Space);
            var foundation = Raw<TerrainDef>(); foundation.isFoundation = true;
            foreach (var cell in placed) map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(cell)] = foundation;
            foreach (var cell in stationary)
                if (pair.Item1 == "ArrivalStation") map.terrainGrid.topGrid[map.cellIndices.CellToIndex(cell)] = Raw<TerrainDef>();
                else map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(cell)] = foundation;
            var channel = new List<IntVec3>();
            var placedPort = shipPort.Item1 - sourceRoot + landing;
            if (pair.Item1 != "ArrivalStation")
                Check(Building_DockingPort.TryFindChannel(map, hostPort.Item1 + offset, hostPort.Item2, 8, channel, out var yardFace)
                    && yardFace == placedPort && channel.Count == 3, pair + " yard port reaches opposing ship port");
            Check(Building_DockingPort.TryFindChannel(map, placedPort, shipPort.Item2, 8, channel, out var face)
                && face == hostPort.Item1 + offset && channel.Count == 3, pair + " ship port reaches host door across three vacuum cells");
            Check(channel.All(c => map.terrainGrid.FoundationAt(c) == null), pair + " passage contains no foundation");
            if (pair.Item1 == "ArrivalStation")
            {
                Current.Game.Maps.Add(map);
                var port = Raw<Building_DockingPort>(); var door = Raw<Building_Door>();
                Set(port, "mapIndexOrState", (sbyte)(Current.Game.Maps.Count - 1));
                Set(door, "mapIndexOrState", (sbyte)(Current.Game.Maps.Count - 1));
                Set(port, "positionInt", placedPort); Set(port, "rotationInt", shipPort.Item2);
                Set(door, "positionInt", hostPort.Item1 + offset);
                Set(port, "channelFace", face);
                for (int n = 0; n < 4; n++)
                {
                    Set(door, "rotationInt", new Rot4(n));
                    Check(DeltaVTutorialService.BerthConnected(port, door), "ordinary door visual rotation cannot break boarding berth");
                }
                Set(port, "channelFace", IntVec3.Invalid);
                Check(!DeltaVTutorialService.BerthConnected(port, door), "retracted trainer passage is not a usable arrival berth");
                Current.Game.Maps.Remove(map);
            }
            var hostSize = IntVec2.FromString(host.SelectSingleNode("/MapPrefab/prefab/size").InnerText);
            var shipSize = IntVec2.FromString(ship.SelectSingleNode("/MapPrefab/prefab/size").InnerText);
            var clearance = ShipPocketMapFactory.CompactSize(new IntVec2(hostSize.x + DeltaVTutorialService.BerthGap + shipSize.x,
                Math.Max(hostSize.z, shipSize.z)));
            if (pair.Item1 == "Shipyard" && !westTutorialBerth)
                Check(placed.All(c => c.x < clearance.x && c.z < clearance.z), "gift berth fits the real shipyard map");
        }
    }

    static int shownBriefings;
    static bool CountBriefing() { shownBriefings++; return false; }

    static void Comms(Game game, Harmony harmony)
    {
        harmony.CreateClassProcessor(typeof(Patch_TutorialComms)).Patch();
        harmony.CreateClassProcessor(typeof(Patch_TutorialCandidateCrew)).Patch();
        Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(GameInitData), nameof(GameInitData.PrepForMapGen))).Prefixes
            .Any(p => p.PatchMethod.DeclaringType == typeof(Patch_TutorialCandidateCrew)), "candidate capture binds before native optional-pawn removal");
        var universe = game.GetComponent<UniverseManager>();
        var previous = universe.tutorialRun;
        var map = Raw<Map>(); map.info = new MapInfo { Size = new IntVec3(20, 1, 20) };
        map.gameConditionManager = new GameConditionManager(map); game.Maps.Add(map);
        var console = Raw<Building_CommsConsole>();
        console.def = Raw<ThingDef>(); console.def.size = new IntVec2(3, 2);
        console.def.hasInteractionCell = true; console.def.interactionCellOffset = IntVec3.South;
        Set(console, "mapIndexOrState", (sbyte)(game.Maps.Count - 1)); Set(console, "positionInt", new IntVec3(5, 0, 5));
        Set(console, "rotationInt", Rot4.North);
        var power = new CompPowerTrader { parent = console }; Set(console, "powerComp", power); Set(power, "powerOnInt", true);
        var captain = Raw<Pawn>(); Set(captain, "positionInt", console.InteractionCell);
        captain.def = Raw<ThingDef>(); captain.def.defName = "Human"; captain.thingIDNumber = 9701;
        AccessTools.Field(typeof(DefOfHelper), "bindingNow").SetValue(null, true);
        try { JobDefOf.UseCommsConsole = new JobDef(); }
        finally { AccessTools.Field(typeof(DefOfHelper), "bindingNow").SetValue(null, false); }
        captain.jobs = new Verse.AI.Pawn_JobTracker(captain);
        captain.jobs.curJob = new Verse.AI.Job(JobDefOf.UseCommsConsole, console);
        var faction = new Faction();
        var run = new TutorialRunState { quest = new Quest(), stage = TutorialStage.Welcome, mentors = faction, comms = console };
        run.colonists.Add(captain); universe.tutorialRun = run;
        var display = AccessTools.Method(typeof(DeltaVTutorialService), "ShowBriefing");
        harmony.Patch(display, prefix: new HarmonyMethod(typeof(Program), nameof(CountBriefing)));
        try
        {
            shownBriefings = 0;
            Check(!universe.Tutorial.ReceiveComms(new Faction(), captain) && run.stage == TutorialStage.Welcome, "unrelated faction call cannot advance the course");
            Set(power, "powerOnInt", false);
            Check(!universe.Tutorial.ReceiveComms(faction, captain), "unpowered actual console rejects first contact");
            Set(power, "powerOnInt", true);
            Set(captain, "positionInt", new IntVec3(1, 0, 1));
            Check(!universe.Tutorial.ReceiveComms(faction, captain), "captain must reach the actual console interaction cell");
            Set(captain, "positionInt", console.InteractionCell);
            faction.TryOpenComms(captain);
            Check(run.stage == TutorialStage.Inquiry && shownBriefings == 1, "native faction call routes to the tutorial inquiry");
            universe.Tutorial.Receive(run.quest, DeltaVTutorialService.Signal(run.quest, "AskPurpose"));
            Check(run.stage == TutorialStage.Briefing, "asking purpose opens the promised explanation");
            Check(!universe.Tutorial.ReceiveComms(faction, Raw<Pawn>()), "another pawn cannot impersonate the captain");
            run.stage = TutorialStage.Flight;
            Check(!universe.Tutorial.ReceiveComms(faction, captain), "later faction calls return to ordinary diplomacy");
        }
        finally
        {
            harmony.Unpatch(display, HarmonyPatchType.Prefix, harmony.Id);
            universe.tutorialRun = previous; game.Maps.Remove(map);
        }
    }

    sealed class SavedPose : IExposable
    {
        internal SpacePose pose;
        public void ExposeData() => pose.ExposeData();
    }

    static void InitializeOrbitFixture(Game game)
    {
        var layer = Raw<PlanetLayer>();
        var tiles = AccessTools.Field(typeof(PlanetLayer), "tileIDToNeighbors_offsets");
        object array = Activator.CreateInstance(tiles.FieldType);
        // The fixture only needs TilesCount. The 1.7 Unity stubs rename this backing field.
        var length = AccessTools.Field(tiles.FieldType, "m_Length") ?? AccessTools.Field(tiles.FieldType, "_length");
        length.SetValue(array, 1);
        tiles.SetValue(layer, array);
        AccessTools.Field(typeof(OrbitSphere), "cachedWorld").SetValue(null, game.World);
        AccessTools.Field(typeof(OrbitSphere), "cachedLayer").SetValue(null, layer);
        ((Dictionary<PlanetLayer, float>)AccessTools.Field(typeof(OrbitSphere), "RadiusByLayer").GetValue(null))[layer] = 100000;
        ((Dictionary<PlanetLayer, float>)AccessTools.Field(typeof(OrbitSphere), "WorldPerCellByLayer").GetValue(null))[layer] = 1;
    }

    static void CourseClearance(Game game)
    {
        InitializeOrbitFixture(game);
        foreach (int edge in new[] { 150, 250, 400, 600 })
        foreach (float angle in new[] { 0f, 1.17f, -2.4f })
        foreach (int clearance in new[] { 100, 286, 500 })
        foreach (float archiveRadius in new[] { 300f, 1200f, 2400f })
        {
            var homeMap = Raw<Map>(); homeMap.info = new MapInfo { Size = new IntVec3(edge, 1, edge + 50) };
            var home = new MapSpaceObject(1001, homeMap, new SpacePose(Vector3.forward, angle))
                { baseTag = SpaceObjectTag.Container | SpaceObjectTag.GravityRegion };
            var map = Raw<Map>(); map.info = new MapInfo { Size = new IntVec3(69, 1, 45) };
            var shipPort = new IntVec3(12, 0, 30);
            var berth = DeltaVTutorialService.BerthPortCell(new IntVec3(edge - 14, 0, 68), Rot4.East);
            var staged = new MapSpaceObject(1003, map, DeltaVTutorialService.CourseBerthPose(home, map, shipPort, berth));
            Vector3 stagedPort = SpaceTransform.MapPointToUniverse(staged, map, new Vector2(shipPort.x + .5f, shipPort.z + .5f));
            Vector3 destination = SpaceTransform.MapPointToUniverse(home, homeMap, new Vector2(berth.x + .5f, berth.z + .5f));
            Check(OrbitSphere.ArcCells(stagedPort, destination) < .01f
                && Mathf.Abs(SpaceTransform.WrapAngle(staged.pose.angle - home.pose.angle)) < .001f,
                "tutorial ship spawns aligned with the hard-dock berth before the transfer");
            var frame = new MapSpaceObject(1002, map, home.pose);
            var area = CellRect.WholeMap(map).ExpandedBy(clearance);
            Check(OrbitalWorkService.IntersectsGravity(frame, CellRect.WholeMap(map), home), "coincident course intersects its tagged home map");
            frame.pose = DeltaVTutorialService.CoursePose(home.pose, homeMap.Size, map.Size, clearance, archiveRadius, 180f, 0);
            Check(!OrbitalWorkService.IntersectsGravity(frame, area, home), "course and approach margin outside actual colony rectangle");
            float parkingRadius = new Vector2(homeMap.Size.x, homeMap.Size.z).magnitude * .5f + clearance;
            for (int bearing = 0; bearing < 8; bearing++)
            {
                var parked = SpaceTransform.RigidPointPose(home.pose,
                    SpaceTransform.Rotate(new Vector2(parkingRadius, 0f), bearing * Mathf.PI / 4f), 0f);
                Check(OrbitSphere.ArcCells(parked.position, frame.pose.position) > archiveRadius,
                    "large ship parked anywhere around home leaves tutorial beyond archive radius");
            }
            var saved = frame.pose;
            Vector3 point = SpaceTransform.MapPointToUniverse(frame, map, new Vector2(20, 25));
            foreach (SpaceObject focus in new SpaceObject[] { home, frame, home, frame })
            {
                Vector2 display = SpaceTransform.UniverseToFocused(focus, point);
                Check(OrbitSphere.ArcCells(point, SpaceTransform.FocusedToUniverse(focus, display)) < .04f,
                    "focus switch only changes display coordinates");
                Check(SpaceTransform.UniverseToFocused(focus, focus.pose.position).magnitude < .01f
                    && Mathf.Abs(SpaceTransform.UniverseAngleToFocused(focus, focus.pose.angle)) < .001f,
                    "focused origin and angle remain identity");
            }
            Check(frame.pose.direction.Equals(saved.direction) && frame.pose.angle == saved.angle
                && frame.pose.velocity == Vector2.zero, "display queries preserve absolute stationary course pose");
            var wrapper = new SavedPose { pose = saved };
            var xml = new XmlDocument(); xml.LoadXml(Scribe.saver.DebugOutputFor(wrapper));
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "<root><pose>" + xml.DocumentElement.InnerXml + "</pose></root>");
                Scribe.loader.InitLoading(path); Scribe.EnterNode("pose");
                var loaded = new SavedPose(); loaded.ExposeData(); Scribe.ExitNode(); Scribe.loader.FinalizeLoading();
                frame.pose = loaded.pose;
                Check(frame.pose.direction.Equals(saved.direction) && !OrbitalWorkService.IntersectsGravity(frame, area, home),
                    "native Scribe retains location and colony clearance");
            }
            finally { Scribe.ForceStop(); File.Delete(path); }
        }
    }

    static void FlightEvidence()
    {
        var run = new TutorialRunState();
        var all = new ShipPhysics.Command { fx=1, fz=1, torque=1 };
        DeltaVTutorialService.RecordFlightInput(run, all, Vector2.zero, 0f);
        Check(run.forwardTicks == 0 && run.lateralTicks == 0 && run.turnTicks == 0, "input without real propulsion earns no flight evidence");
        DeltaVTutorialService.RecordFlightInput(run, default, Vector2.one, 100f);
        Check(run.forwardTicks == 0 && run.lateralTicks == 0 && run.turnTicks == 0, "passive effects without player commands earn no evidence");
        for (int i=0;i<45;i++) DeltaVTutorialService.RecordFlightInput(run, all, Vector2.one, 100f);
        Check(run.forwardTicks == 30 && run.lateralTicks == 30 && run.turnTicks == 30, "real observations saturate at required game ticks");
    }

    static void Shelves(string path)
    {
        var steel = Raw<ThingDef>(); steel.defName = "Steel"; steel.category = ThingCategory.Item; steel.thingClass = typeof(Thing);
        steel.stackLimit = 75; steel.comps = new List<CompProperties>();
        DefDatabase<ThingDef>.Add(steel);
        var shelf = Raw<ThingDef>(); shelf.defName = "ShelfSmall"; shelf.category = ThingCategory.Building;
        shelf.thingClass = typeof(Building_Storage); shelf.comps = new List<CompProperties>();
        shelf.building = new BuildingProperties { fixedStorageSettings = new StorageSettings() };
        DefDatabase<ThingDef>.Add(shelf);
        var xml = new XmlDocument(); xml.Load(path);
        var entries = xml.SelectNodes("/MapPrefab/thingStates/li").Cast<XmlNode>()
            .Select(n => new MapPrefabThingEntry { def = n["def"].InnerText == "Steel" ? steel : shelf,
                stateXml = n["state"]["saveable"].OuterXml }).ToList();
        var things = MapPrefabThingState.Instantiate(entries, "salvage shelf and cargo", null);
        Building_Storage storage = things.OfType<Building_Storage>().Single();
        Check(storage.settings?.filter != null && storage.settings.filter.Allows(steel), "native shelf Scribe retains valid steel filter");
        Check(things.Single(t => t.def == steel).stackCount == 75, "real salvage stock survives Scribe with the requested quantity");
    }

    static void State(Game game)
    {
        var universe = new UniverseManager(game);
        game.components.Add(universe);
        Quest quest = new Quest { id = 81, acceptanceTick = 0 };
        var run = new TutorialRunState { stage = TutorialStage.Welcome };
        universe.tutorialRun = run;
        universe.Tutorial.RegisterQuest(quest);
        Check(run.quest == quest, "bind one quest");
        bool rejected = false;
        try { universe.Tutorial.RegisterQuest(new Quest()); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "reject duplicate registration");
        universe.Tutorial.Receive(new Quest { id = 81 }, DeltaVTutorialService.Signal(quest, "AcceptWelcome"));
        Check(run.stage == TutorialStage.Welcome, "reject same-id wrong quest instance");
        universe.Tutorial.Receive(quest, DeltaVTutorialService.Signal(quest, "StartFlight"));
        Check(run.stage == TutorialStage.Welcome, "reject future-phase answer");
        universe.Tutorial.Receive(quest, DeltaVTutorialService.Signal(quest, "AcceptWelcome"));
        Check(run.stage == TutorialStage.Welcome, "welcome acknowledgement waits for actual communication");
        int actions = ((List<Action>)AccessTools.Field(typeof(UniverseManager), "frameActions").GetValue(universe))?.Count ?? 0;
        universe.Tutorial.Receive(quest, DeltaVTutorialService.Signal(quest, "AcceptWelcome"));
        universe.Tutorial.Receive(quest, DeltaVTutorialService.Signal(quest, "DeferWelcome"));
        Check(run.stage == TutorialStage.Welcome, "repeated acknowledgement and defer keep arrival open");
        universe.Tutorial.Receive(quest, DeltaVTutorialService.Signal(quest, "DeferWelcome"));
        Check(run.stage == TutorialStage.Welcome && universe.Tutorial.Active, "postponing contact keeps tutorial active");
        Check((((List<Action>)AccessTools.Field(typeof(UniverseManager), "frameActions").GetValue(universe))?.Count ?? 0) == actions,
            "duplicate answer does not enqueue another dialog");
        var comms = Raw<Building_CommsConsole>(); comms.def = Raw<ThingDef>(); comms.def.defName = "CommsFixture"; comms.thingIDNumber = 9401;
        var berth = Raw<Building_Door>(); berth.def = Raw<ThingDef>(); berth.def.defName = "PortFixture"; berth.thingIDNumber = 9402;
        var rangePort = Raw<Building_DockingPort>(); rangePort.def = Raw<ThingDef>(); rangePort.def.defName = "RangePortFixture"; rangePort.thingIDNumber = 9403;
        foreach (TutorialStage stage in Enum.GetValues<TutorialStage>())
        {
            var saved = new TutorialRunState { stage = stage, quest = quest, trainingShipId = 400,
                forwardTicks = 17, lateralTicks = 30, turnTicks = 4, recoveryDocked = true, comms = comms, berthDoor = berth,
                rangeConsole = comms, rangeTarget = berth, rangePort = rangePort };
            string xml = Scribe.saver.DebugOutputFor(saved);
            string tmp = Path.GetTempFileName();
            try
            {
                var document = new XmlDocument();
                document.LoadXml(xml);
                File.WriteAllText(tmp, "<root><run>" + document.DocumentElement.InnerXml + "</run></root>");
                Scribe.loader.InitLoading(tmp);
                TutorialRunState loaded = null;
                Scribe_Deep.Look(ref loaded, "run");
                ((LoadedObjectDirectory)AccessTools.Field(typeof(CrossRefHandler), "loadedObjectDirectory")
                    .GetValue(Scribe.loader.crossRefs)).RegisterLoaded(quest);
                ((LoadedObjectDirectory)AccessTools.Field(typeof(CrossRefHandler), "loadedObjectDirectory")
                    .GetValue(Scribe.loader.crossRefs)).RegisterLoaded(comms);
                ((LoadedObjectDirectory)AccessTools.Field(typeof(CrossRefHandler), "loadedObjectDirectory")
                    .GetValue(Scribe.loader.crossRefs)).RegisterLoaded(berth);
                ((LoadedObjectDirectory)AccessTools.Field(typeof(CrossRefHandler), "loadedObjectDirectory")
                    .GetValue(Scribe.loader.crossRefs)).RegisterLoaded(rangePort);
                Scribe.loader.FinalizeLoading();
                Check(loaded.stage == stage && loaded.quest == quest, "native Scribe stage + quest ref " + stage);
                Check(loaded.colonists != null && loaded.caskets != null, "empty collections survive Scribe " + stage);
                Check(loaded.comms == comms && loaded.berthDoor == berth, "native Scribe resolves exact comms and berth roles " + stage);
                Check(loaded.rangeConsole == comms && loaded.rangeTarget == berth && loaded.rangePort == rangePort, "native Scribe resolves range roles " + stage);
                Check(loaded.trainingShipId == 400 && loaded.forwardTicks == 17 && loaded.lateralTicks == 30
                    && loaded.turnTicks == 4 && loaded.recoveryDocked, "flight and docking evidence survives Scribe " + stage);
            }
            finally { Scribe.ForceStop(); File.Delete(tmp); }
        }
        universe.Tutorial.Ended(new Quest { id = 81 });
        Check(run.stage == TutorialStage.Welcome, "unrelated quest cleanup cannot end tutorial");
        Current.Game = Raw<Game>();
        Current.Game.components = new List<GameComponent>();
        Check(!universe.Tutorial.Active, "previous game's tutorial is inactive after changing games");
        Current.Game = game;
        universe.Tutorial.Ended(quest);
        Check(run.stage == TutorialStage.Ended && !universe.Tutorial.Active, "cleanup deactivates and releases targets");
        Check(!universe.Tutorial.Targets.Any(), "ended tutorial does not pin maps");
    }

    static int drawCalls;
    static bool CountDraw() { drawCalls++; return false; }

    static void FocusThread(Game game, Harmony harmony)
    {
        // Intercept only the graphics boundary; run the actual focus prefix and native thread test.
        var draw = AccessTools.Method(typeof(MapDrawer), nameof(MapDrawer.RegenerateEverythingNow));
        harmony.Patch(draw, prefix: new HarmonyMethod(typeof(Program), nameof(CountDraw)));
        var threadField = AccessTools.Field(typeof(UnityData), "mainThreadId");
        UnityDataInitializer.initializing = true;
        int previousThread = (int)threadField.GetValue(null);
        UnityDataInitializer.initializing = false;
        threadField.SetValue(null, System.Threading.Thread.CurrentThread.ManagedThreadId);
        var runtime = new MapRuntime(game);
        game.components.Add(runtime);
        var map = Raw<Map>();
        map.info = new MapInfo { Size = new IntVec3(30, 1, 30) };
        map.mapDrawer = Raw<MapDrawer>();
        game.Maps.Add(map);
        UniverseManager universe = game.GetComponent<UniverseManager>();
        var obj = new MapSpaceObject(991, map, default);
        universe.objects.Add(obj);
        var prefix = AccessTools.Method(typeof(Patch_Game_CurrentMapFocus), "Prefix");
        try
        {
            Exception workerError = null;
            bool allowed = false;
            var worker = new System.Threading.Thread(() =>
            {
                try { allowed = (bool)prefix.Invoke(null, new object[] { game, map }); }
                catch (Exception ex) { workerError = ex; }
            });
            worker.Start();
            worker.Join();
            if (workerError != null) throw workerError;
            Check(allowed && drawCalls == 0, "background new-game focus never touches the graphics boundary");
            Check((bool)prefix.Invoke(null, new object[] { game, map }) && drawCalls == 1,
                "main-thread focus still refreshes the live orbital map");
            prefix.Invoke(null, new object[] { game, map });
            Check(drawCalls == 2, "repeated runtime focus retains immediate refresh");
        }
        finally
        {
            universe.objects.Remove(obj);
            game.Maps.Remove(map);
            game.components.Remove(runtime);
            threadField.SetValue(null, previousThread);
            harmony.Unpatch(draw, HarmonyPatchType.Prefix, harmony.Id);
        }
    }

    static void Caskets(string prefabPath)
    {
        var def = Raw<ThingDef>();
        def.defName = "Ship_CryptosleepCasket";
        def.category = ThingCategory.Building;
        def.thingClass = typeof(Building_CryptosleepCasket);
        def.stackLimit = 1;
        def.comps = new List<CompProperties>();
        DefDatabase<ThingDef>.Add(def);
        var xml = new XmlDocument();
        xml.Load(prefabPath);
        var entries = xml.SelectNodes("/MapPrefab/thingStates/li[def='Ship_CryptosleepCasket']").Cast<XmlNode>()
            .Select(n => new MapPrefabThingEntry { def = def, stateXml = n["state"]["saveable"].OuterXml }).ToList();
        var first = MapPrefabThingState.Instantiate(entries, "tutorial caskets", null);
        var second = MapPrefabThingState.Instantiate(entries, "tutorial caskets repeated", null);
        Check(first.Count == 6 && second.Count == 6, "all six original crew caskets survive native batch Scribe");
        Check(first.Concat(second).OfType<Building_CryptosleepCasket>().All(c => c.GetDirectlyHeldThings() != null && !c.HasAnyContents),
            "all native casket ThingOwners are initialized and empty");
        Check(first.Concat(second).Select(c => c.ThingID).Distinct().Count() == 12, "repeated prefab caskets have distinct IDs");
    }

    static void Paths()
    {
        string root = Path.Combine(Path.GetTempPath(), "TutorialRegression");
        Check(MapPrefab.ResolveWithin(root, "ArrivalStation.xml") == Path.Combine(root, "ArrivalStation.xml"), "valid content path");
        foreach (string bad in new[] { "", "../escape.xml", "C:/absolute.xml" })
        {
            bool rejected = false;
            try { MapPrefab.ResolveWithin(root, bad); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "reject invalid content path " + bad);
        }
    }

    static int goodwillEvents, goodwillDelta;
    static bool CaptureGoodwill(int goodwillChange)
    { goodwillEvents++; goodwillDelta += goodwillChange; return false; }
    static bool FixtureFoundation(TerrainDef __instance, ref bool __result)
    { __result = __instance.isFoundation; return false; }
    static bool storageCellUsable = true;
    static bool FixtureStoreCell(ref bool __result) { __result = storageCellUsable; return false; }
    sealed class SteelFilter : ThingFilter
    {
        // The fixture contains only plain steel, with no quality, HP or DLC-specific book filters.
        public override bool Allows(Thing t) => Allows(t.def);
    }

    sealed class ConsoleSave : IExposable
    {
        internal CompDeltaVPilotConsole console = new CompDeltaVPilotConsole();
        public void ExposeData() => console.PostExposeData();
    }

    static void PublicFacilities(Game game, Harmony harmony)
    {
        AccessTools.Field(typeof(DefOfHelper), "bindingNow").SetValue(null, true);
        var language = Raw<LoadedLanguage>(); Set(language, "dataIsLoaded", true);
        language.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement>();
        foreach (string key in new[] { "DeltaV_PublicFacility_NoClaim", "DeltaV_PublicFacility_NoConstruction" })
            language.keyedReplacements[key] = new LoadedLanguage.KeyedReplacement { key = key, value = key };
        LanguageDatabase.activeLanguage = language;
        var dlcGate = AccessTools.PropertyGetter(typeof(TerrainDef), nameof(TerrainDef.IsSubstructure));
        harmony.Patch(dlcGate, prefix: new HarmonyMethod(typeof(Program), nameof(FixtureFoundation)));
        foreach (Type type in typeof(PublicFacility).Assembly.GetTypes()
            .Where(t => t.Name.StartsWith("Patch_PublicFacility", StringComparison.Ordinal)))
        { harmony.CreateClassProcessor(type).Patch(); checks++; }
        var player = new Faction { def = new FactionDef { isPlayer = true } };
        var ally = new Faction { def = new FactionDef() };
        var relation = new FactionRelation { other = player, kind = FactionRelationKind.Ally, baseGoodwill = 100 };
        Set(ally, "relations", new List<FactionRelation> { relation });
        Set(player, "relations", new List<FactionRelation> { new FactionRelation { other = ally,
            kind = FactionRelationKind.Ally, baseGoodwill = 100 } });
        Set(game.World.factionManager, "ofPlayer", player);
        var map = Raw<Map>(); map.info = new MapInfo { Size = new IntVec3(10, 1, 10) };
        game.Maps.Add(map);
        map.mapPawns = new MapPawns(map);
        map.passingShipManager = new PassingShipManager(map);
        map.cellIndices = new CellIndices(map);
        map.terrainGrid = new TerrainGrid(map);
        map.listerThings = new ListerThings(ListerThingsUse.Global);
        map.components = new List<MapComponent>();
        var ownership = new SubstructureOwnership(map); map.components.Add(ownership);
        var owners = new int[100]; owners[22] = 401; owners[23] = 401;
        Set(ownership, "ownerGrid", owners);
        var foundation = Raw<TerrainDef>(); foundation.isFoundation = true;
        map.terrainGrid.foundationGrid[22] = map.terrainGrid.foundationGrid[23] = foundation;
        var def = Raw<ThingDef>(); def.defName = "PublicConsoleFixture"; def.category = ThingCategory.Building;
        def.building = new BuildingProperties(); def.size = IntVec2.One; def.comps = new List<CompProperties>();
        ThingDefOf.PilotConsole = def;
        var building = Raw<Building>(); building.def = def;
        Set(building, "mapIndexOrState", (sbyte)(game.Maps.Count - 1));
        Set(building, "positionInt", new IntVec3(2, 0, 2)); Set(building, "factionInt", ally);
        var console = new CompDeltaVPilotConsole { parent = building, shipId = 401, isMain = true, isPublicFacility = true };
        Set(building, "comps", new List<ThingComp> { console });
        Set(map.listerThings, "listsByDef", new Dictionary<ThingDef, List<Thing>> { [def] = new List<Thing> { building } });
        var universe = game.GetComponent<UniverseManager>();
        var frame = new MapSpaceObject(950, map, default); universe.objects.Add(frame);
        SubstructureOwnership.BumpStructureVersion();
        var ship = console.Vessel;
        Check(PlayerAccess.CanOperate(PlayerAccessLevel.Player) && PlayerAccess.CanOperate(PlayerAccessLevel.Public)
            && !PlayerAccess.CanOperate(PlayerAccessLevel.Neutral) && !PlayerAccess.CanOperate(PlayerAccessLevel.Hostile),
            "shared access policy limits operation to player and public objects");
        Check(PlayerAccess.CanInteract(PlayerAccessLevel.Player) && PlayerAccess.CanInteract(PlayerAccessLevel.Public)
            && PlayerAccess.CanInteract(PlayerAccessLevel.Neutral) && !PlayerAccess.CanInteract(PlayerAccessLevel.Hostile),
            "shared access policy permits interaction with every non-hostile object");
        Check(PlayerAccess.CanHack(PlayerAccessLevel.Neutral, PlayerAccessLevel.Hostile)
            && !PlayerAccess.CanHack(PlayerAccessLevel.Neutral, PlayerAccessLevel.Neutral)
            && !PlayerAccess.CanHack(PlayerAccessLevel.Hostile, PlayerAccessLevel.Hostile),
            "shared access policy reserves hacking for a usable source and hostile target");
        Check(ship.PlayerAccessLevel == PlayerAccessLevel.Public && ship.PlayerMayOperate
            && PublicFacility.PlayerMayUse(building), "allied public ship grants use without changing ownership");
        Check(!universe.PlayerReferenceObjects().Contains(frame), "empty public facility cannot keep itself or nearby maps loaded");
        var visitor = Raw<Pawn>(); Set(visitor, "factionInt", player);
        var spawned = (List<Pawn>)AccessTools.Field(typeof(MapPawns), "pawnsSpawned").GetValue(map.mapPawns);
        spawned.Add(visitor);
        Check(universe.PlayerReferenceObjects().Contains(frame), "occupied public training ship remains an exploration reference");
        spawned.Clear();
        Check(!universe.PlayerReferenceObjects().Contains(frame), "leaving public facility releases its distance reference");
        sbyte previousMapIndex = game.currentMapIndex;
        try
        {
            game.currentMapIndex = (sbyte)game.Maps.IndexOf(map);
            Check(universe.PlayerReferenceObjects().Contains(frame), "focused public ship supports remote piloting and nearby loading");
        }
        finally { game.currentMapIndex = previousMapIndex; }
        Check(!universe.PlayerReferenceObjects().Contains(frame), "switching away releases empty public facility");
        Check(building.Faction == ally && PublicFacility.ConsoleAt(map, new IntVec3(3, 0, 2)) == console,
            "public rules protect the whole stamped domain");
        Check(!building.ClaimableBy(player), "native claim path rejects public facilities");
        Check(building.DeconstructibleBy(player), "native deconstruction remains available with goodwill cost");
        console.isPublicFacility = false;
        Check(ship.PlayerAccessLevel == PlayerAccessLevel.Neutral && !ship.PlayerMayOperate
            && PublicFacility.ConsoleAt(map, building.Position) == null, "ordinary ally remains neutral without a public grant");
        console.isPublicFacility = true;
        relation.kind = FactionRelationKind.Neutral;
        Check(ship.PlayerAccessLevel == PlayerAccessLevel.Public && ship.PlayerMayOperate
            && PublicFacility.PlayerMayUse(building), "explicit public access remains valid for a non-hostile neutral faction");
        relation.kind = FactionRelationKind.Hostile;
        Check(ship.PlayerAccessLevel == PlayerAccessLevel.Hostile && !ship.PlayerMayOperate
            && !PublicFacility.PlayerMayUse(building), "hostility overrides the public flag and revokes control");
        relation.kind = FactionRelationKind.Ally;
        var placement = AccessTools.Method(typeof(Patch_PublicFacilityConstruction), "Postfix");
        def.size = new IntVec2(2, 1);
        object[] placementArgs = { def, new IntVec3(1, 0, 2), Rot4.North, map, false, (AcceptanceReport)true };
        placement.Invoke(null, placementArgs);
        Check(!((AcceptanceReport)placementArgs[5]).Accepted, "multi-cell blueprint cannot overlap public domain from outside");
        placementArgs[4] = true; placementArgs[5] = (AcceptanceReport)true; placement.Invoke(null, placementArgs);
        Check(((AcceptanceReport)placementArgs[5]).Accepted, "explicit god mode keeps construction override");
        placementArgs[1] = new IntVec3(6, 0, 6); placementArgs[4] = false; placementArgs[5] = (AcceptanceReport)true;
        placement.Invoke(null, placementArgs);
        Check(((AcceptanceReport)placementArgs[5]).Accepted, "unclaimed construction remains available");
        def.size = IntVec2.One;

        var shelf = Raw<Building_Storage>(); shelf.def = DefDatabase<ThingDef>.GetNamed("ShelfSmall");
        shelf.def.size = IntVec2.One;
        Set(shelf, "mapIndexOrState", (sbyte)(game.Maps.Count - 1));
        Set(shelf, "positionInt", new IntVec3(3, 0, 2)); Set(shelf, "factionInt", ally);
        shelf.settings = new StorageSettings(); shelf.settings.Priority = StoragePriority.Critical;
        shelf.settings.filter = new SteelFilter();
        var steel = Raw<Thing>(); steel.def = DefDatabase<ThingDef>.GetNamed("Steel"); steel.stackCount = 75;
        steel.def.virtualDefs = new List<ThingDef>();
        Set(steel, "mapIndexOrState", (sbyte)(game.Maps.Count - 1)); Set(steel, "positionInt", shelf.Position);
        shelf.settings.filter.SetAllow(steel.def, true); shelf.slotGroup = new SlotGroup(shelf);
        map.haulDestinationManager = new HaulDestinationManager(map);
        map.haulDestinationManager.AllGroupsListForReading.Add(shelf.slotGroup);
        map.haulDestinationManager.AllHaulDestinationsListForReading.Add(shelf);
        ((SlotGroup[,,])AccessTools.Field(typeof(HaulDestinationManager), "groupGrid").GetValue(map.haulDestinationManager))[3,0,2] = shelf.slotGroup;
        var storeBoundary = AccessTools.Method(typeof(StoreUtility), nameof(StoreUtility.IsGoodStoreCell));
        harmony.Patch(storeBoundary, prefix: new HarmonyMethod(typeof(Program), nameof(FixtureStoreCell)));
        try
        {
            Check(StoreUtility.TryFindBestBetterStoreCellFor(steel, null, map, StoragePriority.Low, player, out var found)
                && found == shelf.Position, "native storage search can select allied public shelf");
            Check(StoreUtility.CurrentStoragePriorityOf(steel, forced: true) == StoragePriority.Critical
                && StoreUtility.IsInValidBestStorage(steel), "public storage stays valid for forced hauling and job completion");
            storageCellUsable = false;
            Check(!StoreUtility.TryFindBestBetterStoreCellFor(steel, null, map, StoragePriority.Low, player, out _),
                "public storage retains native cell viability rejection");
            storageCellUsable = true; console.isPublicFacility = false;
            Check(!StoreUtility.TryFindBestBetterStoreCellFor(steel, null, map, StoragePriority.Low, player, out _),
                "ordinary allied shelf remains outside player storage search");
            console.isPublicFacility = true;
        }
        finally { harmony.Unpatch(storeBoundary, HarmonyPatchType.Prefix, harmony.Id); }
        var actor = Raw<Pawn>(); Set(actor, "factionInt", player);
        var goodwillMethod = AccessTools.Method(typeof(Faction), nameof(Faction.TryAffectGoodwillWith));
        harmony.Patch(goodwillMethod, prefix: new HarmonyMethod(typeof(Program), nameof(CaptureGoodwill)));
        try
        {
            goodwillEvents = goodwillDelta = 0;
            ally.Notify_BuildingRemoved(building, actor);
            Check(goodwillEvents == 1 && goodwillDelta == -10, "native removal notification charges public demolition once");
            ally.Notify_BuildingRemoved(building, null);
            PublicFacility.Penalize(ally, null, building);
            Check(goodwillEvents == 1, "unattributed removal cannot blame the player");
            var cut = new DamageDef(); Set(cut, "externalViolence", true); DamageDefOf.Cut = cut;
            PublicFacility.NotifyCut(building, actor, 4);
            Check(goodwillEvents == 2 && goodwillDelta == -14, "engineering tools reuse native damage goodwill");
            ally.Notify_BuildingTookDamage(building, new DamageInfo(cut, 3, instigator: actor));
            Check(goodwillEvents == 3 && goodwillDelta == -17, "ordinary damage keeps exactly one native penalty");
        }
        finally { harmony.Unpatch(goodwillMethod, HarmonyPatchType.Prefix, harmony.Id); }
        var saved = new ConsoleSave(); saved.console.isPublicFacility = true; saved.console.shipId = 401;
        string xml = Scribe.saver.DebugOutputFor(saved), path = Path.GetTempFileName();
        try
        {
            var document = new XmlDocument(); document.LoadXml(xml);
            File.WriteAllText(path, "<root><run>" + document.DocumentElement.InnerXml + "</run></root>");
            Scribe.loader.InitLoading(path);
            var loaded = new ConsoleSave(); Scribe.EnterNode("run"); loaded.ExposeData(); Scribe.ExitNode();
            Scribe.loader.FinalizeLoading();
            Check(loaded.console.isPublicFacility && loaded.console.shipId == 401, "native console Scribe retains public access and ship identity");
        }
        finally { Scribe.ForceStop(); File.Delete(path); }
        universe.objects.Remove(frame); game.Maps.Remove(map);
        harmony.Unpatch(dlcGate, HarmonyPatchType.Prefix, harmony.Id);
        AccessTools.Field(typeof(DefOfHelper), "bindingNow").SetValue(null, false);
    }

}
