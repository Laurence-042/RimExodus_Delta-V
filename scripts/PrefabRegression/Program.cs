using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using DeltaV;
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
    static readonly Type state = typeof(MapPrefabThingEntry).Assembly.GetType("DeltaV.MapPrefabThingState");

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
        BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
    static List<Thing> Instantiate(List<MapPrefabThingEntry> entries) =>
        (List<Thing>)Call("Instantiate", entries, "regression", null);

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
