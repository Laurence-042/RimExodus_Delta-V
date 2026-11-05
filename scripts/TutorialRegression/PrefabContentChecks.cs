using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml;
using DeltaV;
using RimExodus.Maps;
using RimWorld;
using Verse;

internal static partial class Program
{
    static void PrefabIdentities(string directory)
    {
        var xmlHarness = new HarmonyLib.Harmony("DeltaV.TutorialRegression.EnvironmentXml");
        xmlHarness.Patch(HarmonyLib.AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.LogVerbose)),
            prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(EnvironmentQuietLog)));
        bool profiling = DeepProfiler.enabled;
        try { CheckPrefabContent(directory); }
        finally { DeepProfiler.enabled = profiling; xmlHarness.UnpatchAll(xmlHarness.Id); }
    }

    static bool EnvironmentQuietLog(ref bool __result) { __result = false; return false; }

    static void CheckPrefabContent(string directory)
    {
        foreach (string path in Directory.GetFiles(directory, "*.xml").OrderBy(p => p))
        {
            var xml = new XmlDocument(); xml.Load(path);
            // Parse the authored environment with REF's production reader, isolating
            // unrelated Thing/terrain Def loading from this content-layer check.
            var environmentXml = (XmlDocument)xml.CloneNode(true);
            foreach (string xpath in new[] { "/MapPrefab/prefab/terrain", "/MapPrefab/foundations", "/MapPrefab/roofs" })
            {
                XmlNode node = environmentXml.SelectSingleNode(xpath);
                node?.ParentNode.RemoveChild(node);
            }
            environmentXml.SelectSingleNode("/MapPrefab/thingStates").RemoveAll();
            var environment = MapPrefabDocument.FromXml(environmentXml.OuterXml);
            var sampled = environment.environment.SelectMany(e => e.cells).ToHashSet();
            var covered = xml.SelectNodes("/MapPrefab/roofs/li/pos|/MapPrefab/foundations/li[def='DeltaV_MoonPool']/pos")
                .Cast<XmlNode>().Select(n => IntVec3.FromString(n.InnerText)).ToHashSet();
            Check(sampled.SetEquals(covered), Path.GetFileName(path) + " environment covers all roof/equipment/moonpool cells");
            Check(environment.environment.All(e => e.temperature == 21f && e.vacuum == 0f),
                Path.GetFileName(path) + " authored cabin temperature/vacuum");
            if (Path.GetFileNameWithoutExtension(path) == "PrototypeShip")
            {
                var moon = xml.SelectNodes("/MapPrefab/foundations/li[def='DeltaV_MoonPool']/pos")
                    .Cast<XmlNode>().Select(n => IntVec3.FromString(n.InnerText)).ToHashSet();
                var roofs = xml.SelectNodes("/MapPrefab/roofs/li/pos").Cast<XmlNode>()
                    .Select(n => IntVec3.FromString(n.InnerText)).ToHashSet();
                var basins = xml.SelectNodes("/MapPrefab/thingStates/li[def='HydroponicsBasin']").Cast<XmlNode>().ToList();
                Check(basins.Count == 6, "gift has six hydroponic basins");
                var growing = new HashSet<IntVec3>();
                foreach (var basin in basins)
                {
                    foreach (IntVec3 cell in GenAdj.OccupiedRect(IntVec3.FromString(basin["pos"].InnerText),
                        new Rot4(int.Parse(basin["rotation"].InnerText)), new IntVec2(1, 4))) growing.Add(cell);
                    Check(basin.SelectSingleNode("state/saveable/plantDefToGrow").InnerText == "Plant_Rice", "gift retains rice sowing selection");
                }
                var rice = xml.SelectNodes("/MapPrefab/thingStates/li[def='Plant_Rice']/pos").Cast<XmlNode>()
                    .Select(n => IntVec3.FromString(n.InnerText)).ToHashSet();
                Check(growing.Count == 24 && growing.SetEquals(rice) && growing.IsSubsetOf(moon)
                    && !growing.Overlaps(roofs), "all gift crops occupy sunlit moon-pool cells without opaque roofs");
                Check(xml.SelectNodes("/MapPrefab/thingStates/li[def='SunLamp']").Count == 0,
                    "gift greenhouse uses daylight without a sun lamp");
            }
            var registry = new LoadedObjectDirectory();
            int before = errors.Count;
            foreach (XmlNode node in xml.SelectNodes("/MapPrefab/thingStates/li/state/saveable/descendant-or-self::*[def and id]"))
            {
                // Both supported game versions register the numeric Thing ID as well
                // as the full load ID. A different Def prefix does not make it unique.
                var thing = Raw<ThingWithComps>();
                thing.def = Raw<ThingDef>(); thing.def.defName = node["def"].InnerText;
                thing.thingIDNumber = Thing.IDNumberFromThingID(node["id"].InnerText);
                registry.RegisterLoaded(thing);
            }
            Check(errors.Count == before, Path.GetFileName(path) + " native registration of all root and nested identities: "
                + string.Join("\n", errors.Skip(before)));
        }
    }

    static void PrefabTurretGraphs(string directory)
    {
        // Load actual authored turrets, including their deep gun and Verb graphs;
        // testing just the weapon Comp misses Scribe's nested identity registration.
        foreach (string name in new[] { "Shipyard", "WeaponRange" })
        {
            var xml = new XmlDocument(); xml.Load(Path.Combine(directory, name + ".xml"));
            var entries = new List<MapPrefabThingEntry>();
            foreach (XmlNode state in xml.SelectNodes("/MapPrefab/thingStates/li/state/saveable[gun]"))
            {
                ThingDef Def(string defName, Type type, ThingCategory category)
                {
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                    if (def == null) { def = Raw<ThingDef>(); def.defName = defName; DefDatabase<ThingDef>.Add(def); }
                    def.thingClass = type; def.category = category; def.stackLimit = 1;
                    def.comps = new List<CompProperties>();
                    return def;
                }
                var gun = Def(state["gun"]["def"].InnerText, typeof(ThingWithComps), ThingCategory.Item);
                gun.comps.Add(new CompProperties(typeof(CompEquippable)));
                Set(gun, "verbs", new List<VerbProperties> { new VerbProperties {
                    verbClass = GenTypes.GetTypeInAnyAssembly(state.SelectSingleNode("gun/verbTracker/verbs/li").Attributes["Class"].Value) } });
                var turret = Def(state["def"].InnerText, GenTypes.GetTypeInAnyAssembly(state.Attributes["Class"].Value), ThingCategory.Building);
                turret.building = new BuildingProperties { turretGunDef = gun };
                entries.Add(new MapPrefabThingEntry { def = turret, stateXml = state.OuterXml });
            }
            int before = errors.Count;
            var first = MapPrefabThingState.Instantiate(entries, name + " turret graphs", null).Cast<Building_TurretGun>().ToList();
            var second = MapPrefabThingState.Instantiate(entries, name + " turret graphs repeated", null).Cast<Building_TurretGun>().ToList();
            Check(errors.Count == before, name + " complete turret Scribe: " + string.Join("\n", errors.Skip(before)));
            var both = first.Concat(second).ToList();
            Check(both.Count == entries.Count * 2, name + " all authored turret graphs restored twice");
            Check(both.SelectMany(t => new[] { t.thingIDNumber, t.gun.thingIDNumber }).Distinct().Count() == both.Count * 2,
                name + " cloned hosts and guns have distinct numeric identities");
            foreach (var turret in both)
                Check(turret.GunCompEq.PrimaryVerb.caster == turret
                    && turret.GunCompEq.PrimaryVerb.loadID == Verb.CalculateUniqueLoadID(turret.GunCompEq, 0),
                    name + " cloned gun Verb retains caster and matching identity");
        }
    }

    static readonly Dictionary<string, XmlNode> sourceDefs = new();
    static readonly Dictionary<string, XmlNode> sourceParents = new();
    static readonly HashSet<string> checkedDefs = new();

    static void ReadSourceDefs(string data, string modDefs)
    {
        // Independent of the authoring records. Use native component types so a
        // derived power component cannot disappear from the electrical fixture.
        foreach (string file in Directory.GetFiles(data, "*.xml", SearchOption.AllDirectories)
            .Where(p => p.Replace('\\', '/').Contains("/Defs/"))
            .Concat(Directory.GetFiles(modDefs, "*.xml", SearchOption.AllDirectories)))
        {
            var xml = new XmlDocument(); xml.Load(file);
            foreach (XmlNode node in xml.SelectNodes("/Defs/ThingDef"))
            {
                if (node["defName"] != null)
                {
                    string name = node["defName"].InnerText;
                    if (file.StartsWith(modDefs, StringComparison.OrdinalIgnoreCase))
                        Check(!string.IsNullOrEmpty(name) && !char.IsNumber(name[name.Length - 1]),
                            "ThingDef.ConfigErrors naming rule: " + name);
                    sourceDefs[name] = node;
                }
                if (node.Attributes["Name"] != null) sourceParents[node.Attributes["Name"].Value] = node;
            }
        }
    }

    static IEnumerable<XmlNode> SourceChain(string name)
    {
        XmlNode node = sourceDefs[name];
        while (node != null)
        {
            yield return node;
            sourceParents.TryGetValue(node.Attributes["ParentName"]?.Value ?? "", out node);
        }
    }

    static void VerifySourceDef(JsonElement row)
    {
        string name = row.GetProperty("name").GetString();
        if (!checkedDefs.Add(name)) return;
        string Scalar(string field, string fallback) => SourceChain(name)
            .Select(n => n[field]?.InnerText).FirstOrDefault(v => v != null) ?? fallback;
        Check(row.GetProperty("hasInteraction").GetBoolean() == bool.Parse(Scalar("hasInteractionCell", "false")),
            name + " interaction flag agrees with installed Def (case insensitive)");
        Check(row.GetProperty("rotatable").GetBoolean() == bool.Parse(Scalar("rotatable", "true")),
            name + " rotation flag agrees with installed Def");
        bool transmits = false;
        foreach (var node in SourceChain(name))
        {
            var comps = node["comps"];
            if (comps == null) continue;
            foreach (XmlNode comp in comps.SelectNodes("li"))
            {
                string typeName = comp.Attributes["Class"]?.Value;
                Type type = typeName == null ? null : GenTypes.GetTypeInAnyAssembly(typeName);
                if (type != null && typeof(CompProperties_Power).IsAssignableFrom(type))
                {
                    var props = (CompProperties_Power)Activator.CreateInstance(type);
                    transmits |= comp["transmitsPower"] == null ? props.transmitsPower : bool.Parse(comp["transmitsPower"].InnerText);
                }
                else Check(comp["transmitsPower"] == null, name + " recognized native power component " + typeName);
            }
            if (comps.Attributes["Inherit"]?.Value.Equals("false", StringComparison.OrdinalIgnoreCase) == true) break;
        }
        Check(row.GetProperty("transmitsPower").GetBoolean() == transmits, name + " power flag includes derived native components");
    }

    static void CatalogPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeltaV-Catalog-" + Guid.NewGuid());
        string local = Path.Combine(root, "local"), shipped = Path.Combine(root, "shipped");
        Directory.CreateDirectory(local); Directory.CreateDirectory(Path.Combine(shipped, "Ships"));
        string a = Path.Combine(local, "Shipyard.xml"), b = Path.Combine(shipped, "Shipyard.xml"),
            c = Path.Combine(shipped, "Ships", "Cargo.xml");
        try
        {
            File.WriteAllText(a, "local"); File.WriteAllText(b, "shipped"); File.WriteAllText(c, "cargo");
            string[] roots = { local, shipped };
            Check(MapPrefab.ListPrefabFiles(roots).SequenceEqual(new[] { "Ships/Cargo.xml", "Shipyard.xml" }),
                "catalog merges shipped and local assets once with portable paths");
            Check(MapPrefab.ResolveRelativePath(roots, "Shipyard.xml") == a, "local override precedes shipped prefab");
            File.Delete(a);
            Check(MapPrefab.ResolveRelativePath(roots, "Shipyard.xml") == b, "catalog falls back to shipped prefab");
            Check(MapPrefab.ResolveRelativePath(roots, "Ships/Cargo.xml") == c, "subfolder asset resolves");
            foreach (string bad in new[] { "../escape.xml", "C:/absolute.xml", "" })
            {
                bool rejected = false;
                try { MapPrefab.ResolveRelativePath(roots, bad); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "catalog rejects " + bad);
            }
        }
        finally
        {
            File.Delete(a); File.Delete(b); File.Delete(c);
            Directory.Delete(Path.Combine(shipped, "Ships")); Directory.Delete(shipped);
            Directory.Delete(local); Directory.Delete(root);
        }
    }
}
