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

internal static class Program
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
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try
        {
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
            Geometry(args[1], game);
            State(game);
            FocusThread(game, harmony);
            Caskets(args[2]);
            Paths();
            Check(errors.Count == 0, "Unexpected native logs: " + string.Join("\n", errors));
            Console.WriteLine($"PASS {checks} tutorial checks: native geometry/interaction/power, Scribe, phase guards, focus thread, paths. No game launched.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine(string.Join("\n", errors)); return 1; }
        finally { Scribe.ForceStop(); }
    }

    static void Geometry(string path, Game game)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var map = Raw<Map>();
        map.info = new MapInfo { Size = new IntVec3(70, 1, 70) };
        map.cellIndices = new CellIndices(map);
        map.thingGrid = new ThingGrid(map);
        Set(game, "maps", new List<Map> { map });
        var buildings = new List<Building>();
        foreach (var row in json.RootElement.EnumerateArray())
        {
            string name = row.GetProperty("name").GetString();
            var def = Raw<ThingDef>();
            def.defName = name;
            def.size = new IntVec2(row.GetProperty("size")[0].GetInt32(), row.GetProperty("size")[1].GetInt32());
            def.rotatable = row.GetProperty("rotatable").GetBoolean();
            def.passability = Enum.Parse<Traversability>(row.GetProperty("passability").GetString());
            def.hasInteractionCell = row.GetProperty("hasInteraction").GetBoolean();
            def.interactionCellOffset = new IntVec3(row.GetProperty("interaction")[0].GetInt32(), 0,
                row.GetProperty("interaction")[2].GetInt32());
            var pos = new IntVec3(row.GetProperty("root")[0].GetInt32(), 0, row.GetProperty("root")[1].GetInt32());
            var rot = new Rot4(row.GetProperty("rotation").GetInt32());
            var expected = row.GetProperty("cells").EnumerateArray().Select(c => new IntVec3(c[0].GetInt32(), 0, c[1].GetInt32())).ToHashSet();
            Check(expected.SetEquals(GenAdj.OccupiedRect(pos, rot, def.size)), name + " native occupied rectangle");
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
            bool transmits = name == "HiddenConduit" || name == "Battery" || name == "GravcorePowerCell";
            Set(b, "comps", transmits ? new List<ThingComp> { new CompPowerTrader
                { parent = b, props = new CompProperties_Power { transmitsPower = true } } } : null);
            map.thingGrid.Register(b);
            buildings.Add(b);
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
        var powerCells = new HashSet<IntVec3>();
        Check(transmitters.All(b => b.OccupiedRect().All(c => powerCells.Add(c))), "no overlapping power transmitters");
        PowerNet net = PowerNetMaker.NewPowerNetStartingFrom(transmitters[0]);
        Check(net.transmitters.Count == transmitters.Count, "native single connected power net");
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
        universe.Tutorial.Receive(quest, DeltaVTutorialService.Signal(quest, "ReportForTraining"));
        Check(run.stage == TutorialStage.Welcome, "reject future-phase answer");
        universe.Tutorial.Receive(quest, DeltaVTutorialService.Signal(quest, "AcceptWelcome"));
        Check(run.stage == TutorialStage.Briefing, "advance accepted welcome");
        int actions = ((List<Action>)AccessTools.Field(typeof(UniverseManager), "frameActions").GetValue(universe)).Count;
        universe.Tutorial.Receive(quest, DeltaVTutorialService.Signal(quest, "AcceptWelcome"));
        universe.Tutorial.Receive(quest, DeltaVTutorialService.Signal(quest, "LeaveWelcome"));
        Check(run.stage == TutorialStage.Briefing, "reject stale answer and stale quit option");
        Check(((List<Action>)AccessTools.Field(typeof(UniverseManager), "frameActions").GetValue(universe)).Count == actions,
            "duplicate answer does not enqueue another dialog");
        foreach (TutorialStage stage in Enum.GetValues<TutorialStage>())
        {
            var saved = new TutorialRunState { stage = stage, quest = quest };
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
                Scribe.loader.FinalizeLoading();
                Check(loaded.stage == stage && loaded.quest == quest, "native Scribe stage + quest ref " + stage);
                Check(loaded.colonists != null && loaded.caskets != null, "empty collections survive Scribe " + stage);
            }
            finally { Scribe.ForceStop(); File.Delete(tmp); }
        }
        universe.Tutorial.Ended(new Quest { id = 81 });
        Check(run.stage == TutorialStage.Briefing, "unrelated quest cleanup cannot end tutorial");
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
        def.defName = "CryptosleepCasket";
        def.category = ThingCategory.Building;
        def.thingClass = typeof(Building_CryptosleepCasket);
        def.stackLimit = 1;
        def.comps = new List<CompProperties>();
        DefDatabase<ThingDef>.Add(def);
        var xml = new XmlDocument();
        xml.Load(prefabPath);
        var entries = xml.SelectNodes("/MapPrefab/thingStates/li[def='CryptosleepCasket']").Cast<XmlNode>()
            .Select(n => new MapPrefabThingEntry { def = def, stateXml = n["state"]["saveable"].OuterXml }).ToList();
        var first = MapPrefabThingState.Instantiate(entries, "tutorial caskets", null);
        var second = MapPrefabThingState.Instantiate(entries, "tutorial caskets repeated", null);
        Check(first.Count == 1 && second.Count == 1, "single-colonist casket survives native batch Scribe");
        Check(first.Concat(second).OfType<Building_CryptosleepCasket>().All(c => c.GetDirectlyHeldThings() != null && !c.HasAnyContents),
            "all native casket ThingOwners are initialized and empty");
        Check(first.Concat(second).Select(c => c.ThingID).Distinct().Count() == 2, "repeated prefab caskets have distinct IDs");
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
}
