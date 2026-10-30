using System;
using System.Collections.Generic;
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

    static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(args[0], name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try { Run(); Console.WriteLine($"PASS {checks} prefab checks (production DLL + vanilla Scribe)"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Scribe.ForceStop(); }
    }

    static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
    static bool CaptureError(string text) { errors.Add(text); Console.WriteLine(text); return false; }
    static void NoErrors(string phase) => Check(errors.Count == 0, phase + ": " + string.Join("\n", errors));
    static object Call(string method, params object[] args) => state.GetMethod(method,
        BindingFlags.Static | BindingFlags.Public).Invoke(null, args);
    static List<Thing> Instantiate(List<MapPrefabThingEntry> entries) =>
        (List<Thing>)Call("Instantiate", entries, "regression", null, true);

    static void Run()
    {
        // Redirect native log presentation; Scribe/identity logic remains unpatched.
        var harmony = new Harmony("DeltaV.PrefabRegression");
        foreach (string method in new[] { "Error", "Warning" })
            harmony.Patch(AccessTools.Method(typeof(Log), method, new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(CaptureError)));
        using var logLock = Log.LockMessages();
        DeepProfiler.enabled = false;
        var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        game.uniqueIDsManager = new UniqueIDsManager();
        var world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
        world.factionManager = new FactionManager();
        AccessTools.Field(typeof(Game), "worldInt").SetValue(game, world);
        Current.Game = game;

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
        string[] expectedScribeTypes = { "CombatStrategy", "CombatStrategyRule",
            "CombatManeuverCurves", "CombatScoreCurve", "CombatScorePoint" };
        Check(errors.All(e => expectedScribeTypes.Any(type => e.StartsWith(
            "Could not find class DeltaV." + type + " "))),
            "standalone Scribe type lookup uses only the expected DeltaV class fallback");
        errors.Clear();

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
