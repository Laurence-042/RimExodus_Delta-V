using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using DeltaV;
using RimExodus.Maps;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

// Runs real prefab loading, vanilla turret/Verb serialization and cross-reference resolution.
// No Map/Unity rendering is created; this does not replace in-game prefab placement testing.
internal static class Program
{
    static readonly List<string> errors = new List<string>();
    static int checks;
    static readonly Type state = typeof(MapPrefabThingEntry).Assembly.GetType("RimExodus.Maps.MapPrefabThingState");
    static readonly Type exportBackend = typeof(MapPrefab).Assembly.GetType("DeltaV.SharedMapBackend");

    static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(args[0], name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try
        {
            if (args.Length == 1) Run();
            else RunContent(args.Skip(1).ToArray());
            Console.WriteLine($"PASS {checks} prefab checks (production DLL + vanilla Scribe)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Scribe.ForceStop(); }
    }

    static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
    static bool DisableVerboseLogging(ref bool __result) { __result = false; return false; }
    static bool DisablePlantRenderingInitializer() { return false; }
    static bool CaptureError(string text) { errors.Add(text); Console.WriteLine(text); return false; }
    static void NoErrors(string phase) => Check(errors.Count == 0, phase + ": " + string.Join("\n", errors));
    static object Call(string method, params object[] args) => state.GetMethod(method,
        BindingFlags.Static | BindingFlags.Public).Invoke(null, args);
    static List<Thing> Instantiate(List<MapPrefabThingEntry> entries) =>
        MapPrefabThingState.Instantiate(entries, "regression", null, true);

    static void RunContent(string[] args)
    {
        bool requireEnvironment = false, requireRoofCoverage = false;
        float? temperature = null, vacuum = null;
        var paths = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--require-environment": requireEnvironment = true; break;
                case "--require-roof-coverage": requireRoofCoverage = true; break;
                case "--expected-temperature": temperature = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--expected-vacuum": vacuum = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default:
                    if (Directory.Exists(args[i]))
                        paths.AddRange(Directory.GetFiles(args[i], "*.xml", SearchOption.AllDirectories));
                    else if (File.Exists(args[i])) paths.Add(args[i]);
                    else throw new ArgumentException("Prefab path does not exist: " + args[i]);
                    break;
            }
        }
        if (paths.Count == 0) throw new ArgumentException("Specify at least one prefab XML file or a directory containing prefab XML.");
        var requirements = new PrefabEnvironmentRequirements(requireEnvironment, requireRoofCoverage, temperature, vacuum);
        using var logLock = Log.LockMessages();
        PrepareNativeFixture();
        foreach (string path in paths.Select(Path.GetFullPath).Distinct().OrderBy(p => p))
        {
            var xml = new System.Xml.XmlDocument();
            xml.Load(path);
            PrefabContentValidation.Validate(xml, path, requirements, errors, Check);
        }
        NoErrors("prefab content");
    }

    static void PrepareNativeFixture()
    {
        // Redirect native log presentation; Scribe/identity logic remains unpatched.
        var harmony = new Harmony("DeltaV.PrefabRegression");
        foreach (string method in new[] { "Error", "Warning" })
            harmony.Patch(AccessTools.Method(typeof(Log), method, new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(CaptureError)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.LogVerbose)),
            prefix: new HarmonyMethod(typeof(Program), nameof(DisableVerboseLogging)));
        harmony.Patch(typeof(Plant).TypeInitializer,
            prefix: new HarmonyMethod(typeof(Program), nameof(DisablePlantRenderingInitializer)));
        DeepProfiler.enabled = false;
        var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        game.uniqueIDsManager = new UniqueIDsManager();
        var world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
        world.factionManager = new FactionManager();
        AccessTools.Field(typeof(Game), "worldInt").SetValue(game, world);
        Current.Game = game;
        var pack = (ModContentPack)RuntimeHelpers.GetUninitializedObject(typeof(ModContentPack));
        pack.assemblies = new ModAssemblyHandler(pack);
        pack.assemblies.loadedAssemblies.Add(typeof(CompShipCombatAI).Assembly);
        LoadedModManager.RunningModsListForReading.Add(pack);
    }

    static void ImportMissingDefs()
    {
        ThingDef NewDef(string name)
        {
            var def = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));
            def.defName = name;
            def.category = ThingCategory.Item;
            def.thingClass = typeof(ThingWithComps);
            def.stackLimit = 1;
            def.virtualDefs = new List<ThingDef>();
            DefDatabase<ThingDef>.Add(def);
            return def;
        }

        var category = new StuffCategoryDef { defName = "PrefabFixtureMaterialCategory" };
        ThingDef validStuff = NewDef("PrefabFixtureMaterial");
        validStuff.stuffProps = new StuffProperties { categories = new List<StuffCategoryDef> { category } };
        ThingDef otherStuff = NewDef("PrefabFixtureOtherMaterial");
        otherStuff.stuffProps = new StuffProperties { categories = new List<StuffCategoryDef>() };
        ThingDef validItem = NewDef("PrefabFixtureAllowed");
        var special = new SpecialThingFilterDef { defName = "PrefabFixtureSpecial" };
        DefDatabase<SpecialThingFilterDef>.Add(special);

        ThingDef holder = NewDef("PrefabFixtureFilterHolder");
        holder.stuffCategories = new List<StuffCategoryDef> { category };
        holder.defaultStuff = validStuff;
        holder.comps = new List<CompProperties> { new CompProperties(typeof(CompPrefabRegressionFilter)) };

        MapPrefabThingEntry Entry(int offset)
        {
            var thing = new ThingWithComps { def = holder, thingIDNumber = 2100240 + offset };
            thing.SetStuffDirect(validStuff);
            thing.InitializeComps();
            var filter = thing.GetComp<CompPrefabRegressionFilter>().filter;
            filter.SetAllow(validItem, true);
            filter.SetAllow(special, false);
            return new MapPrefabThingEntry { def = holder, stuff = validStuff, pos = new IntVec3(offset, 0, 0),
                stateXml = (string)Call("Capture", thing) };
        }
        const string missingThing = "PrefabFixtureMissingThing", missingStuff = "PrefabFixtureMissingStuff";
        const string missingAllowed = "PrefabFixtureMissingAllowed", missingSpecial = "PrefabFixtureMissingSpecial";
        var source = new MapPrefabDocument { name = "missing-def regression", size = new IntVec2(4, 4) };
        source.thingEntries.Add(Entry(1));
        source.thingEntries.Add(Entry(2));
        string intact = source.ToXml();
        string WithMissingFilters(string filterState, string name, string missing, bool keepOriginal)
        {
            string element = $"<li>{name}</li>";
            return filterState.Replace(element, (keepOriginal ? element : "") + $"<li>{missing}</li>");
        }
        var first = source.thingEntries[0];
        first.stateXml = WithMissingFilters(first.stateXml, validItem.defName, missingAllowed, true);
        first.stateXml = WithMissingFilters(first.stateXml, special.defName, missingSpecial, true);
        var second = source.thingEntries[1];
        second.stateXml = WithMissingFilters(second.stateXml, validItem.defName, missingAllowed, false);
        second.stateXml = WithMissingFilters(second.stateXml, special.defName, missingSpecial, false);
        var deleted = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));
        deleted.defName = missingThing;
        source.thingEntries.Insert(1, new MapPrefabThingEntry { def = deleted, pos = IntVec3.Zero });
        string missing = source.ToXml().Replace(">" + validStuff.defName + "<", ">" + missingStuff + "<");
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "missing-def-prefab.xml");

        MapPrefabDocument Read(string xml, PrefabImportOptions options)
        {
            File.WriteAllText(fixturePath, xml);
            return MapPrefabDocument.Load(fixturePath, options);
        }

        var decoder = state.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "Instantiate");
        var importChanges = typeof(MapPrefabDocument).GetField("ImportChanges", BindingFlags.Instance | BindingFlags.NonPublic);
        List<ThingWithComps> Decode(MapPrefabDocument doc)
            => ((List<Thing>)decoder.Invoke(null, new[] { (object)doc.thingEntries, doc.name, null, true, false,
                true, importChanges.GetValue(doc) })).Cast<ThingWithComps>().ToList();

        bool strictRejected = false;
        try { Read(missing, null); }
        catch (InvalidDataException) { strictRejected = true; }
        Check(strictRejected, "strict load rejects missing stuff");
        strictRejected = false;
        try { Read(missing.Replace(missingStuff, validStuff.defName), null); }
        catch (InvalidDataException ex) when (ex.Message.Contains(missingThing)) { strictRejected = true; }
        Check(strictRejected, "strict load rejects missing ThingDef");

        var options = new PrefabImportOptions
        {
            SkipMissingThings = true, SkipMissingFilterDefs = true, MissingStuff = MissingPrefabDef.Replace,
            DefaultStuff = validStuff,
        };
        var resolved = Read(missing, options);
        Check(resolved.thingEntries.Select(entry => entry.pos.x).SequenceEqual(new[] { 1, 2 }),
            "surviving Thing states retain their order and coordinates");
        Check(resolved.thingEntries.All(entry => entry.stuff == validStuff && entry.stateXml.Contains($"<stuff>{validStuff.defName}</stuff>")),
            "replacement stuff updates placement and Scribe state");
        var decoded = Decode(resolved);
        Check(decoded.All(thing => thing.Stuff == validStuff), "native Scribe reads replacement stuff");
        var keptFilter = decoded[0].GetComp<CompPrefabRegressionFilter>().filter;
        var emptyFilter = decoded[1].GetComp<CompPrefabRegressionFilter>().filter;
        Check(keptFilter.Allows(validItem) && !keptFilter.Allows(special),
            "native Scribe keeps the working Def and special filter settings");
        Check(emptyFilter.AllowedDefCount == 0 && !emptyFilter.IsAlwaysDisallowedDueToSpecialFilters(validItem),
            "fully pruned lists are usable empty native lists");
        Check(resolved.thingEntries[0].stateXml.Contains(missingSpecial),
            "Scribe filter sanitation operates on a per-import copy");

        options.DefaultStuff = otherStuff;
        Check(Read(missing, options).ThingCellCount == 0, "incompatible configured stuff skips the affected entities");
        options.UseVanillaDefaultStuff = true;
        var vanillaDefaulted = Decode(Read(missing, options));
        Check(vanillaDefaulted.Count == 2 && vanillaDefaulted.All(thing => thing.Stuff == validStuff),
            "vanilla default stuff is tried if explicitly enabled");
        options.MissingStuff = MissingPrefabDef.Skip;
        Check(Read(missing, options).ThingCellCount == 0, "skip policy is independent of the configured default stuff");
        var allAvailable = Decode(Read(intact, options));
        Check(allAvailable.Count == 2 && allAvailable.All(thing => thing.Stuff == validStuff
            && thing.GetComp<CompPrefabRegressionFilter>().filter.Allows(validItem)
            && !thing.GetComp<CompPrefabRegressionFilter>().filter.Allows(special)),
            "available Defs and materials survive with the tolerant policy enabled");
        holder.defaultStuff = null;
        holder.stuffCategories = new List<StuffCategoryDef> { new StuffCategoryDef { defName = "PrefabFixtureUnavailableCategory" } };
        options.MissingStuff = MissingPrefabDef.Replace;
        Check(Read(missing, options).ThingCellCount == 0, "no compatible installed material safely skips the affected entities");
        NoErrors("missing Def import");
    }

    static void PlantGrowerContents()
    {
        ThingDef NewDef(string name, ThingCategory category, Type thingClass, bool useHitPoints)
        {
            var def = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));
            def.defName = name;
            def.category = category;
            def.thingClass = thingClass;
            def.stackLimit = 1;
            def.size = IntVec2.One;
            def.useHitPoints = useHitPoints;
            def.comps = new List<CompProperties>();
            def.virtualDefs = new List<ThingDef>();
            DefDatabase<ThingDef>.Add(def);
            return def;
        }

        ThingDef plantDef = NewDef("PrefabRegressionPlant", ThingCategory.Plant, typeof(Plant), true);
        plantDef.plant = new PlantProperties { neverBlightable = false };
        ThingDef growerDef = NewDef("PrefabRegressionGrower", ThingCategory.Building,
            typeof(Building_PlantGrower), true);
        growerDef.building = new BuildingProperties { defaultPlantToGrow = plantDef };
        ThingDef blightDef = NewDef("PrefabRegressionBlight", ThingCategory.Ethereal, typeof(Blight), false);
        ThingDef itemDef = NewDef("PrefabRegressionItem", ThingCategory.Item, typeof(ThingWithComps), false);

        var grower = new Building_PlantGrower
        {
            def = growerDef, thingIDNumber = 2100250, Position = new IntVec3(4, 0, 4), HitPoints = 86,
        };
        grower.SetPlantDefToGrow(plantDef);
        var plant = new Plant
        {
            def = plantDef, thingIDNumber = 2100251, Position = new IntVec3(4, 0, 4),
            Growth = 0.63f, Age = 23456, sown = true, HitPoints = 37,
        };
        AccessTools.Field(typeof(Plant), "unlitTicks").SetValue(plant, 3456);
        AccessTools.Field(typeof(Plant), "madeLeaflessTick").SetValue(plant, 4567);
        var blight = new Blight
        {
            def = blightDef, thingIDNumber = 2100252, Position = new IntVec3(4, 0, 4), Severity = 0.58f,
        };
        AccessTools.Field(typeof(Blight), "lastPlantHarmTick").SetValue(blight, 5678);
        var wildPlant = new Plant
        {
            def = plantDef, thingIDNumber = 2100253, Position = new IntVec3(7, 0, 7), Growth = 0.31f,
        };
        var strayBlight = new Blight
        {
            def = blightDef, thingIDNumber = 2100254, Position = new IntVec3(7, 0, 7), Severity = 0.42f,
        };
        var item = new ThingWithComps
        {
            def = itemDef, thingIDNumber = 2100255, Position = new IntVec3(5, 0, 4),
        };

        MethodInfo include = exportBackend.GetMethod("ShouldExportThing", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo order = exportBackend.GetMethod("OrderExportThings", BindingFlags.Static | BindingFlags.NonPublic);
        var plants = new HashSet<Thing> { plant };
        var blights = new HashSet<Thing> { blight };
        bool Included(Thing thing) => (bool)include.Invoke(null, new object[] { thing, plants, blights });
        Check(Included(grower) && Included(item) && Included(plant) && Included(blight),
            "export policy includes buildings, items and selected grower dependants");
        Check(!Included(wildPlant) && !Included(strayBlight),
            "export policy excludes unrelated plants and blight");
        var ordered = ((IEnumerable<Thing>)order.Invoke(null,
            new object[] { new Thing[] { blight, plant, grower } })).ToList();
        Check(ordered.SequenceEqual(new Thing[] { grower, plant, blight }),
            "export order places grower before plant before blight");

        MapPrefabThingEntry Entry(Thing thing) => new MapPrefabThingEntry
        {
            def = thing.def,
            pos = thing.Position,
            stateXml = (string)Call("Capture", thing),
        };
        var entries = new List<MapPrefabThingEntry> { Entry(grower), Entry(plant), Entry(blight) };
        string plantState = entries[1].stateXml;
        Check(plantState.Contains("<growth>"), "plant Scribe state contains growth");
        Check(plantState.Contains("<age>23456</age>"), "plant Scribe state contains age");
        Check(plantState.Contains("<unlitTicks>3456</unlitTicks>"), "plant Scribe state contains light timer");
        Check(plantState.Contains("<madeLeaflessTick>4567</madeLeaflessTick>"),
            "plant Scribe state contains leafless timer");
        Check(plantState.Contains("<sown>True</sown>"), "plant Scribe state contains sown flag");
        Check(plantState.Contains("<health>37</health>"), "plant Scribe state contains health");

        var first = Instantiate(entries);
        var second = Instantiate(entries);
        NoErrors("plant grower capture and instantiate twice");
        void CheckCopy(List<Thing> copy, string label)
        {
            var copiedGrower = (Building_PlantGrower)copy[0];
            var copiedPlant = (Plant)copy[1];
            var copiedBlight = (Blight)copy[2];
            Check(copiedGrower.GetPlantDefToGrow() == plantDef && copiedGrower.HitPoints == 86,
                label + " restores grower selection and health");
            Check(Math.Abs(copiedPlant.Growth - 0.63f) < 0.0001f && copiedPlant.Age == 23456
                && copiedPlant.sown && copiedPlant.HitPoints == 37
                && (int)AccessTools.Field(typeof(Plant), "unlitTicks").GetValue(copiedPlant) == 3456
                && (int)AccessTools.Field(typeof(Plant), "madeLeaflessTick").GetValue(copiedPlant) == 4567,
                label + " restores all plant growth state");
            Check(Math.Abs(copiedBlight.Severity - 0.58f) < 0.0001f
                && (int)AccessTools.Field(typeof(Blight), "lastPlantHarmTick").GetValue(copiedBlight) == 5678,
                label + " restores blight state");
        }
        CheckCopy(first, "first clone");
        CheckCopy(second, "second clone");
        Check(new Thing[] { grower, plant, blight }.Concat(first).Concat(second)
                .Select(thing => thing.GetUniqueLoadID()).Distinct().Count() == 9,
            "source and plant grower clones have independent Thing IDs");
    }

    static void StablePrefabUuids()
    {
        Check(AccessTools.Field(typeof(Building_SpaceTradeTerminal), "terminalUuid") == null
            && AccessTools.Field(typeof(CompPrefabMarker), "markerUuid") == null,
            "prefab terminals and markers require no per-instance UUID identity");
        Check(AccessTools.Method(typeof(Building_SpaceTradeTerminal), "ValidatePrefabEndpoints") == null
            && AccessTools.Method(typeof(CompPrefabMarker), "ValidatePrefabMarkerUuids") == null,
            "prefab loading contains no UUID validation gate");
    }

    static void Run()
    {
        using var logLock = Log.LockMessages();
        PrepareNativeFixture();
        StablePrefabUuids();
        ImportMissingDefs();
        PlantGrowerContents();
        // Def constructors preload shaders; only those rendering defaults are bypassed here.
        var gunDef = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));
        gunDef.defName = "PrefabRegressionGun";
        gunDef.category = ThingCategory.Item;
        gunDef.thingClass = typeof(ThingWithComps);
        gunDef.stackLimit = 1;
        gunDef.comps = new List<CompProperties> { new CompProperties(typeof(CompEquippable)) };
        AccessTools.Field(typeof(ThingDef), "verbs").SetValue(gunDef,
            new List<VerbProperties> { new VerbProperties { verbClass = typeof(Verb_Shoot) } });
        var turretDef = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));
        turretDef.defName = "PrefabRegressionTurret";
        turretDef.category = ThingCategory.Building;
        turretDef.thingClass = typeof(Building_TurretGun);
        turretDef.stackLimit = 1;
        turretDef.building = new BuildingProperties { turretGunDef = gunDef };
        turretDef.comps = new List<CompProperties> { new CompProperties(typeof(CompPrefabRegressionReference)) };
        DefDatabase<ThingDef>.Add(gunDef);
        DefDatabase<ThingDef>.Add(turretDef);
        var gun = new ThingWithComps { def = gunDef, thingIDNumber = 2100221 };
        gun.InitializeComps();
        var turret = new Building_TurretGun { def = turretDef, thingIDNumber = 2100220, gun = gun };
        turret.InitializeComps();
        var links = turret.GetComp<CompPrefabRegressionReference>();
        links.target = gun;
        links.verb = gun.GetComp<CompEquippable>().PrimaryVerb;
        AccessTools.Field(typeof(Verb), "lastShotTick").SetValue(links.verb, 123);
        gun.questTags = new List<string> { "source-only" };
        var entries = new List<MapPrefabThingEntry> {
            new MapPrefabThingEntry { def = turretDef, stateXml = (string)Call("Capture", turret) }
        };
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "prefab-source.xml"), entries[0].stateXml);
        var first = (Building_TurretGun)Instantiate(entries).Single();
        var second = (Building_TurretGun)Instantiate(entries).Single();
        NoErrors("capture and instantiate twice");
        Check(first != null && second != null && first.gun != null && second.gun != null, "both turret graphs restored");
        var identities = new[] { turret, gun, first, first.gun, second, second.gun }
            .Select(t => t.GetUniqueLoadID()).ToList();
        Check(identities.Distinct().Count() == identities.Count, "source and both clones have distinct root and nested Thing IDs");
        foreach (var copy in new[] { first, second })
        {
            var refs = copy.GetComp<CompPrefabRegressionReference>();
            var verb = copy.GunCompEq.PrimaryVerb;
            Check(ReferenceEquals(refs.target, copy.gun) && ReferenceEquals(refs.verb, verb), "internal Thing/Verb references point to the same cloned objects");
            Check(verb.caster == copy && verb.LastShotTick == 123, "turret caster binding and saved Verb state preserved");
            Check(verb.loadID == Verb.CalculateUniqueLoadID(copy.GunCompEq, 0), "Verb identity follows the new gun identity");
            Check(copy.gun.questTags == null, "nested Thing source quest tags cleared");
        }
        Check(first.GunCompEq.PrimaryVerb.GetUniqueLoadID() != second.GunCompEq.PrimaryVerb.GetUniqueLoadID(), "cloned Verb IDs differ");

        var strategyDef = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));
        strategyDef.defName = "PrefabRegressionStrategyHolder";
        strategyDef.category = ThingCategory.Item;
        strategyDef.thingClass = typeof(ThingWithComps);
        strategyDef.stackLimit = 1;
        strategyDef.comps = new List<CompProperties> { new CompProperties_ShipCombatAI() };
        DefDatabase<ThingDef>.Add(strategyDef);
        var a = new ThingWithComps { def = strategyDef, thingIDNumber = 2100230 };
        var b = new ThingWithComps { def = strategyDef, thingIDNumber = 2100231 };
        a.InitializeComps(); b.InitializeComps();
        a.GetComp<CompShipCombatAI>().strategy.name = "heavy cruiser";
        a.GetComp<CompShipCombatAI>().strategy.maneuverRules.Add(
            new CombatStrategyRule { name = "guard", maneuverCurves = new CombatManeuverCurves
            {
                distance = new CombatScoreCurve(true,
                    new CombatScorePoint(50f, -20f), new CombatScorePoint(120f, 40f))
            } });
        b.GetComp<CompShipCombatAI>().strategy.name = "interceptor";
        b.GetComp<CompShipCombatAI>().strategy.maneuverRules.Add(
            new CombatStrategyRule { name = "close", maneuverCurves = new CombatManeuverCurves
            {
                distance = new CombatScoreCurve(false,
                    new CombatScorePoint(10f, 30f), new CombatScorePoint(80f, -10f))
            } });
        b.GetComp<CompShipCombatAI>().strategy.fireRules.Add(
            new CombatStrategyRule { name = "finish", finishingWeight = 4f });
        var twoShipEntries = new List<MapPrefabThingEntry>
        {
            new MapPrefabThingEntry { def = strategyDef, stateXml = (string)Call("Capture", a) },
            new MapPrefabThingEntry { def = strategyDef, stateXml = (string)Call("Capture", b) }
        };
        var pairOne = Instantiate(twoShipEntries).Cast<ThingWithComps>().ToList();
        var pairTwo = Instantiate(twoShipEntries).Cast<ThingWithComps>().ToList();
        Check(pairOne.Count == 2 && pairTwo.Count == 2
            && pairOne[0].GetComp<CompShipCombatAI>().strategy.name == "heavy cruiser"
            && pairOne[1].GetComp<CompShipCombatAI>().strategy.name == "interceptor"
            && pairTwo[0].GetComp<CompShipCombatAI>().strategy.maneuverRules[0].name == "guard"
            && pairTwo[0].GetComp<CompShipCombatAI>().strategy.maneuverRules[0]
                .maneuverCurves.distance.points[1].x == 120f
            && pairTwo[1].GetComp<CompShipCombatAI>().strategy.maneuverRules[0]
                .maneuverCurves.distance.points[0].x == 10f
            && pairTwo[1].GetComp<CompShipCombatAI>().strategy.fireRules[0].name == "finish",
            "two independent comp strategies survive repeated prefab instantiation");
        pairOne[0].GetComp<CompShipCombatAI>().strategy.name = "edited instance";
        pairOne[0].GetComp<CompShipCombatAI>().strategy.maneuverRules[0]
            .maneuverCurves.distance.points[1].y = 99f;
        Check(pairTwo[0].GetComp<CompShipCombatAI>().strategy.name == "heavy cruiser"
            && pairTwo[0].GetComp<CompShipCombatAI>().strategy.maneuverRules[0]
                .maneuverCurves.distance.points[1].y == 40f
            && a.GetComp<CompShipCombatAI>().strategy.name == "heavy cruiser",
            "repeated prefab instances do not share strategy objects");
        string strategiesPath = Path.Combine(AppContext.BaseDirectory, "prefab-strategies.xml");
        List<Thing> strategySaved = pairOne.Cast<Thing>().ToList();
        Scribe.saver.InitSaving(strategiesPath, "fixture");
        Scribe_Collections.Look(ref strategySaved, "things", LookMode.Deep);
        Scribe.saver.FinalizeSaving();
        List<Thing> strategyRestored = null;
        Scribe.loader.InitLoading(strategiesPath);
        Scribe_Collections.Look(ref strategyRestored, "things", LookMode.Deep);
        Scribe.loader.FinalizeLoading();
        Check(strategyRestored.Count == 2
            && ((ThingWithComps)strategyRestored[0]).GetComp<CompShipCombatAI>().strategy.name == "edited instance"
            && ((ThingWithComps)strategyRestored[0]).GetComp<CompShipCombatAI>()
                .strategy.maneuverRules[0].maneuverCurves.distance.points[1].y == 99f
            && ((ThingWithComps)strategyRestored[1]).GetComp<CompShipCombatAI>().strategy.name == "interceptor",
            "ordinary Scribe reload preserves distinct strategies");
        Type files = typeof(CompShipCombatAI).Assembly.GetType("DeltaV.CombatStrategyFile");
        MethodInfo import = files.GetMethod("ImportFrom", BindingFlags.Static | BindingFlags.NonPublic);
        string validFile = Path.Combine(AppContext.BaseDirectory, "combat-strategy-valid.xml");
        string invalidFile = Path.Combine(AppContext.BaseDirectory, "combat-strategy-invalid.xml");
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(CombatStrategy));
        using (var output = File.Create(validFile)) serializer.Serialize(output,
            pairTwo[1].GetComp<CompShipCombatAI>().strategy);
        var imported = (CombatStrategy)import.Invoke(null, new object[] { AppContext.BaseDirectory, validFile });
        Check(imported.name == "interceptor" && !ReferenceEquals(imported,
            pairTwo[1].GetComp<CompShipCombatAI>().strategy)
            && imported.maneuverRules[0].maneuverCurves.distance.points[0].x == 10f,
            "standalone XML import yields an independent strategy snapshot");
        File.WriteAllText(invalidFile, "<CombatStrategy><format>999</format></CombatStrategy>");
        CombatStrategy selectedBefore = pairOne[0].GetComp<CompShipCombatAI>().strategy;
        bool rejected = false;
        try { import.Invoke(null, new object[] { AppContext.BaseDirectory, invalidFile }); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
        { rejected = true; }
        Check(rejected && ReferenceEquals(pairOne[0].GetComp<CompShipCombatAI>().strategy,
            selectedBefore), "invalid import leaves the selected comp strategy untouched");
        NoErrors("strategy Scribe resolves the loaded Mod assembly");

        // Save the source and two prefab clones together; then use ordinary Scribe, no prefab remap.
        string path = Path.Combine(AppContext.BaseDirectory, "prefab-roundtrip.xml");
        var saved = new List<Thing> { turret, first, second };
        Scribe.saver.InitSaving(path, "fixture");
        Scribe_Collections.Look(ref saved, "things", LookMode.Deep);
        Scribe.saver.FinalizeSaving();
        List<Thing> restored = null;
        Scribe.loader.InitLoading(path);
        Scribe_Collections.Look(ref restored, "things", LookMode.Deep);
        var loadedObjects = Scribe.loader.crossRefs.crossReferencingExposables.ToArray();
        var loadIDs = loadedObjects.OfType<ILoadReferenceable>().Select(t => t.GetUniqueLoadID()).ToList();
        Check(loadIDs.Count == loadIDs.Distinct().Count(), "ordinary reload registry contains no duplicate load IDs");
        Scribe.loader.FinalizeLoading();
        NoErrors("ordinary save/reload");
        Check(restored.Count == 3, "ordinary reload preserves all three turrets");
        foreach (Building_TurretGun copy in restored)
            Check(ReferenceEquals(copy.GetComp<CompPrefabRegressionReference>().target, copy.gun)
                && ReferenceEquals(copy.GetComp<CompPrefabRegressionReference>().verb, copy.GunCompEq.PrimaryVerb)
                && copy.GunCompEq.PrimaryVerb.LastShotTick == 123, "ordinary reload preserves each turret's own graph and Verb state");
    }
}

public sealed class CompPrefabRegressionFilter : ThingComp
{
    public ThingFilter filter = new ThingFilter();
    public override void PostExposeData() => Scribe_Deep.Look(ref filter, "fixtureFilter");
}

public sealed class CompPrefabRegressionReference : ThingComp
{
    public Thing target;
    public Verb verb;
    public override void PostExposeData()
    {
        Scribe_References.Look(ref target, "fixtureTarget");
        Scribe_References.Look(ref verb, "fixtureVerb");
    }
}
