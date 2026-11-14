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
    static void ScenarioTranslation(string prefabDirectory)
    {
        string version = Path.GetFullPath(Path.Combine(prefabDirectory, "../../1.6"));
        var xml = new XmlDocument(); xml.Load(Path.Combine(version, "Defs/Tutorial/DeltaV_Tutorial.xml"));
        var node = xml.SelectSingleNode("/Defs/ScenarioDef[defName='DeltaV_OrbitalColonists']");
        Check(node.SelectSingleNode("scenario/parts/li[@Class='DeltaV.ScenPart_ConfigPage_TutorialColonists']/pawnChoiceCount").InnerText == "6",
            "shipped tutorial uses civilian clothing configuration for all six candidates");
        var def = new ScenarioDef { defName = node["defName"].InnerText, label = node["label"].InnerText,
            description = node["description"].InnerText, scenario = new Scenario() };
        def.PostLoad();
        Check(def.scenario.name == def.label, "native scenario PostLoad copies the untranslated name before language injection");
        DefDatabase<ScenarioDef>.Add(def);
        xml.Load(Path.Combine(version, "Languages/ChineseSimplified/DefInjected/ScenarioDef/DeltaV_Tutorial.xml"));
        var package = new DefInjectionPackage(typeof(ScenarioDef));
        foreach (XmlNode entry in xml.DocumentElement.ChildNodes)
            if (entry is XmlElement)
                package.injections.Add(entry.Name, new DefInjectionPackage.DefInjection {
                    path = entry.Name, injection = entry.InnerText, fileSource = "DeltaV_Tutorial.xml" });
        package.InjectIntoDefs(true);
        Check(package.loadErrors.Count == 0 && package.injections.Values.All(i => i.injected),
            "all tutorial scenario translations use valid native field paths");
        Check(def.scenario.name == "轨道殖民者", "scenario selection reads the translated nested name");
        Check(def.scenario.description == xml.DocumentElement["DeltaV_OrbitalColonists.scenario.description"].InnerText,
            "scenario details read the translated nested description");
    }

    static void SectorContentDefaults(string directory)
    {
        var leaves = SectorContentTypes.AllLeaves().ToList();
        Check(leaves.Count == 12, "sector content registry has twelve generation leaves");
        Check(leaves.All(leaf => !leaf.defaultPrefabPath.NullOrEmpty()),
            "every sector generation leaf has a shipped default prefab");
        Check(leaves.Select(leaf => leaf.defaultPrefabPath).Distinct().Count() == leaves.Count,
            "sector generation leaves use distinct authored defaults");
        List<string> shippedPaths = MapPrefab.ListPrefabFiles(new[] { Path.Combine(directory, "Sector") });
        Check(shippedPaths.Count == 17
            && shippedPaths.All(path => path.StartsWith("Craft/") || path.StartsWith("Stations/") || path.StartsWith("Asteroids/")),
            "all seventeen sector prefabs use the sector purpose directories");
        Check(leaves.All(leaf => leaf.defaultPrefabPath.StartsWith("Sector/")),
            "every sector default points into the sector catalog tree");
        foreach (SectorContentType leaf in leaves)
        {
            string path = Path.Combine(directory, leaf.defaultPrefabPath);
            Check(File.Exists(path), leaf.id + " default prefab is shipped");
            var document = new XmlDocument(); document.Load(path);
            Check(document.DocumentElement?.GetAttribute("formatVersion") == "2"
                && document.SelectNodes("/MapPrefab/thingStates/li").Count > 0,
                leaf.id + " default is a non-empty format-2 prefab");
            int clusters = document.SelectNodes("/MapPrefab/thingStates/li[def='DeltaV_PrefabClusterMarker']").Count;
            Check(clusters == (leaf.id == "Asteroid" ? 1 : 0),
                leaf.id + " default has the expected cluster-core role");
            Check(document.SelectSingleNode("/MapPrefab/thingStates/li[def='GravEngine' or def='GravFieldExtender']") == null,
                leaf.id + " default has no surface-launch hardware");
            var cells = document.SelectNodes("/MapPrefab/thingStates/li[def='GravcorePowerCell']");
            Check(cells.Count == 0, leaf.id + " has no gravcore power cell");
            if (leaf.id == "Fighter")
            {
                Check(document.SelectNodes("/MapPrefab/thingStates/li[def='ChemfuelPoweredGenerator' and state/saveable/fuel > 0]").Count == 1
                    && document.SelectNodes("/MapPrefab/thingStates/li[def='Battery']").Count == 2
                    && document.SelectSingleNode("/MapPrefab/thingStates/li[def='Chemfuel' and stackCount > 0]") != null,
                    "fighter ships with one fueled chemfuel generator, two standard batteries and reserve fuel");
            }
            if (leaf.id == "Cruiser")
                Check(document.SelectNodes("/MapPrefab/thingStates/li[def='DeltaV_ShieldGenerator' "
                    + "and state/saveable[DeltaV_shieldMode='Guard' and DeltaV_shieldGroundRadius > 0 "
                    + "and DeltaV_regularCapacity=750 and fuel=750 and currentHitPoints=-1]]").Count == 2,
                    "both exported cruiser shield states are charged guard domes");
            if (leaf.defaultPrefabPath.StartsWith("Sector/Stations/"))
                CheckStationSolar(document, leaf.id, "Battery",
                    leaf.id == "TradeStation" || leaf.id == "Shipyard" || leaf.id == "FactoryStation" ? 1 : 0);
        }
        var settings = new DeltaVSettings();
        settings.EnsureSectorRulesSeeded();
        Check(settings.sectorRules.Count == leaves.Count
            && leaves.All(leaf => settings.EffectiveSectorRule(leaf).prefabPaths
                .SequenceEqual(new[] { leaf.defaultPrefabPath })),
            "fresh sector settings seed every shipped default pool");

        var custom = new DeltaVSettings();
        custom.sectorRules.Add(new SectorTypeRule {
            typeId = leaves[0].id, prefabPaths = new List<string> { "Custom.xml" } });
        custom.EnsureSectorRulesSeeded();
        Check(custom.EffectiveSectorRule(leaves[0]).prefabPaths.SequenceEqual(new[] { "Custom.xml" }),
            "default seeding preserves an existing custom pool");
        custom.sectorRules[0].prefabPaths.Clear();
        custom.EnsureSectorRulesSeeded();
        Check(custom.EffectiveSectorRule(leaves[0]).prefabPaths.SequenceEqual(new[] { leaves[0].defaultPrefabPath }),
            "empty sector pool regains its shipped usable default");

        string trade = Path.Combine(directory, leaves.Single(leaf => leaf.id == "TradeStation").defaultPrefabPath);
        var xml = new XmlDocument(); xml.Load(trade);
        XmlNode terminal = xml.SelectSingleNode("/MapPrefab/thingStates/li[def='DeltaV_TradeTerminal']/state/saveable");
        var ids = xml.SelectNodes("/MapPrefab/thingStates/li/state/saveable")
            .Cast<XmlNode>().ToDictionary(node => node["id"].InnerText, node => node["def"].InnerText);
        // Inspect the intended targets of the existing sector resource. The tutorial fixture tests native resolution.
        string TargetThingId(XmlNode node) => node.InnerText.StartsWith("Thing_") ? node.InnerText[6..] : node.InnerText;
        Check(terminal.SelectNodes("supplyCargo/li").Cast<XmlNode>()
                .All(node => ids.TryGetValue(TargetThingId(node), out string def) && def == "DeltaV_CargoContainer")
            && terminal.SelectNodes("supplyCargo/li").Count == 3,
            "trade station terminal references three sealed supply bays");
        Check(ids.TryGetValue(TargetThingId(terminal["deliveryCargo"]), out string delivery)
            && delivery == "DeltaV_CargoStorage",
            "trade station terminal references one unlocked delivery bay");

        string belt = Path.Combine(directory, "Sector/Asteroids/SectorAsteroid.xml");
        xml.Load(belt);
        XmlNode cluster = xml.SelectSingleNode(
            "/MapPrefab/thingStates/li[def='DeltaV_PrefabClusterMarker']/state/saveable");
        var children = cluster.SelectNodes("prefabs/li").Cast<XmlNode>().ToList();
        Check(children.Count == 5 && children.All(node => ParseHelper.FromString<float>(node["weight"].InnerText) > 0f),
            "asteroid belt core has five positive weighted child variants");
        Check(cluster["contentTypeId"].InnerText == "Asteroid"
            && cluster["innerRadius"].InnerText == "70"
            && cluster["outerRadius"].InnerText == "260"
            && cluster["densityPerTenThousand"].InnerText == "0.6"
            && cluster["minimumSpacing"].InnerText == "28",
            "asteroid belt core stores its authored annulus and density");
        CheckAsteroidPrefab(xml, "asteroid belt core");
        foreach (XmlNode child in children)
        {
            string childPath = Path.Combine(directory, child["prefabPath"].InnerText);
            Check(File.Exists(childPath), "asteroid child prefab is shipped: " + child["prefabPath"].InnerText);
            var childXml = new XmlDocument(); childXml.Load(childPath);
            CheckAsteroidPrefab(childXml, child["prefabPath"].InnerText);
            Check(childXml.SelectNodes("/MapPrefab/thingStates/li[def='DeltaV_PrefabClusterMarker']").Count == 0,
                child["prefabPath"].InnerText + " cannot recursively generate a belt");
        }

        var markerDef = Raw<ThingDef>();
        markerDef.defName = "DeltaV_PrefabClusterMarker";
        markerDef.thingClass = typeof(Building);
        markerDef.category = ThingCategory.Building;
        markerDef.stackLimit = 1;
        markerDef.comps = new List<CompProperties> { new CompProperties_PrefabClusterSpawner() };
        DefDatabase<ThingDef>.Add(markerDef);
        var markerEntry = new MapPrefabThingEntry { def = markerDef, stateXml = cluster.OuterXml };
        int before = errors.Count;
        ThingWithComps loadedMarker = (ThingWithComps)MapPrefabThingState.Instantiate(
            new[] { markerEntry }, "asteroid belt cluster marker", null).Single();
        var loadedComp = loadedMarker.TryGetComp<CompPrefabClusterSpawner>();
        var loadedEntries = (List<PrefabClusterEntry>)HarmonyLib.AccessTools.Field(
            typeof(CompPrefabClusterSpawner), "entries").GetValue(loadedComp);
        Check(errors.Count == before && loadedEntries.Count == children.Count
            && loadedEntries.Select(entry => entry.prefabPath).SequenceEqual(
                children.Select(node => node["prefabPath"].InnerText)),
            "asteroid belt cluster marker restores its complete production Scribe state");
    }

    static void CheckStationSolar(XmlDocument document, string label, string batteryDef, int reserveCount)
    {
        var openDeck = document.SelectNodes("/MapPrefab/noRoof/li").Cast<XmlNode>()
            .Select(row => IntVec3.FromString(row.InnerText)).ToHashSet();
        var roofed = document.SelectNodes("/MapPrefab/roofs/li/pos").Cast<XmlNode>()
            .Select(row => IntVec3.FromString(row.InnerText)).ToHashSet();
        var panels = document.SelectNodes("/MapPrefab/thingStates/li[def='SolarGenerator']").Cast<XmlNode>().ToList();
        Check(panels.Count > 0 && openDeck.Count > 0 && !roofed.Overlaps(openDeck),
            label + " has a protected unroofed solar deck");
        bool IsOn(XmlNode row, string def, HashSet<IntVec3> cells)
        {
            var position = IntVec3.FromString(row["pos"].InnerText);
            var rotation = new Rot4(ParseHelper.FromString<int>(row["rotation"].InnerText));
            var size = ParseHelper.FromString<IntVec2>(sourceDefs[def]["size"].InnerText);
            return GenAdj.OccupiedRect(position, rotation, size).All(cells.Contains);
        }
        foreach (XmlNode panel in panels)
            Check(IsOn(panel, "SolarGenerator", openDeck), label + " fully exposes each native panel footprint");
        var batteries = document.SelectNodes("/MapPrefab/thingStates/li[def='" + batteryDef + "']")
            .Cast<XmlNode>().ToList();
        Check(batteries.Count >= 2 && batteries.All(row => IsOn(row, batteryDef, roofed)
            && ParseHelper.FromString<float>(row.SelectSingleNode("state/saveable/storedPower").InnerText) > 0),
            label + " starts with sheltered, charged grid batteries");
        var reserve = document.SelectNodes("/MapPrefab/thingStates/li[def='DeltaV_ShipReactor']/state/saveable")
            .Cast<XmlNode>().ToList();
        Check(reserve.Count == reserveCount && reserve.All(row => row["switchOn"]?.InnerText == "False"
            && row["wantSwitchOn"]?.InnerText == "False" && ParseHelper.FromString<float>(row["fuel"].InnerText) > 0),
            label + " standby reactors are fueled and switched off");
    }

    static void CheckAsteroidPrefab(XmlDocument xml, string label)
    {
        int minerals = xml.SelectNodes("/MapPrefab/thingStates/li[starts-with(def,'Mineable')]").Count;
        int vacstone = xml.SelectNodes("/MapPrefab/thingStates/li[def='Vacstone']").Count;
        int roofs = xml.SelectNodes("/MapPrefab/roofs/li").Count;
        Check(xml.SelectNodes("/MapPrefab/prefab/terrain/*").Count == 1
            && xml.SelectSingleNode("/MapPrefab/prefab/terrain/Vacstone_Rough") != null,
            label + " uses Odyssey's Vacstone_Rough asteroid terrain");
        Check(xml.SelectNodes("/MapPrefab/foundations/li|/MapPrefab/environment/li").Count == 0,
            label + " has no gravship foundation or authored atmosphere");
        Check(roofs > 0 && xml.SelectNodes("/MapPrefab/roofs/li[def='RoofRockThin']").Count == roofs,
            label + " uses native thin rock roof over its solid body");
        Check(minerals > 0 && vacstone > 0 && minerals + vacstone == roofs,
            label + " replaces only part of its Vacstone body with minerals");
    }

    static void CheckPrefabContent(IEnumerable<string> files, bool authoredEnvironment = false, bool tutorial = false)
    {
        foreach (string path in files.OrderBy(p => p))
        {
            var xml = new XmlDocument(); xml.Load(path);
            string name = Path.GetFileNameWithoutExtension(path);
            bool arrival = tutorial && name == "ArrivalStation";
            bool asteroid = name == "SectorAsteroid" || name.StartsWith("Asteroid", StringComparison.Ordinal);
            var requirements = authoredEnvironment && !asteroid
                ? new PrefabEnvironmentRequirements(requireRoofCoverage: true,
                    expectedTemperature: arrival ? null : 21f, expectedVacuum: arrival ? null : 0f)
                : default;
            var environment = PrefabContentValidation.Validate(xml, name, requirements, errors, Check);
            if (authoredEnvironment && asteroid)
                Check(environment.environment.Count == 0, name + " leaves atmosphere to the native vacuum room calculation");
            if (tutorial && name == "Shipyard")
            {
                Check(xml.SelectNodes("/MapPrefab/thingStates/li[def='GravcorePowerCell']").Count == 0,
                    "tutorial ring yard has no routine gravcore generation");
                CheckStationSolar(xml, "tutorial ring yard", "DeltaV_SafetyBattery", 2);
            }
            if (arrival)
            {
                Check(environment.environment.All(e => e.temperature == 12f && e.vacuum == 0f
                    || e.temperature == -75f && e.vacuum == 1f), "arrival wreck has the authored breathable hold and vacuum zones");
                Check(environment.environment.Single(e => e.temperature == 12f).cells.Count == 114,
                    "only the original cargo compartment receives warm breathable air");
                Check(xml.SelectNodes("/MapPrefab/thingStates/li[def='Ship_Reactor']/state/saveable[hibernateState='Running']").Count == 1,
                    "one running reactor remains");
                Check(xml.SelectNodes("/MapPrefab/thingStates/li[def='Heater']").Count == 2
                    && xml.SelectNodes("/MapPrefab/thingStates/li[def='OxygenPump']").Count == 1,
                    "only minimum cargo life support remains");
                Check(xml.SelectNodes("/MapPrefab/thingStates/li[def='Battery' or def='UnstablePowerCell' or def='WallLamp' or def='VitalsMonitor']").Count == 0,
                    "lights, extra power and medical monitors are looted");
                Check(xml.SelectNodes("/MapPrefab/thingStates/li[def='HospitalBed']").Count == 6
                    && xml.SelectNodes("/MapPrefab/thingStates/li[def='Bed' or def='DoubleBed']").Count == 6
                    && xml.SelectNodes("/MapPrefab/thingStates/li[def='Building_OutfitStand']").Count == 10
                    && xml.SelectNodes("/MapPrefab/thingStates/li[def='DiningChair']").Count == 14,
                    "all original beds, outfit stands and chairs remain");
                Check(xml.SelectNodes("/MapPrefab/thingStates/li[def='ElectricStove' or def='NutrientPasteDispenser' or def='Hopper']").Count == 5,
                    "original kitchen facilities remain");
                Check(xml.SelectNodes("/MapPrefab//innerList/li[def='Apparel_Vacsuit']").Count == 1
                    && xml.SelectNodes("/MapPrefab//innerList/li[def='Apparel_VacsuitHelmet']").Count == 1,
                    "exactly one vacuum suit and helmet remain in the captain's cargo outfit stand");
                Check(xml.SelectSingleNode("/MapPrefab/thingStates/li[def='MealSurvivalPack']/stackCount").InnerText == "10"
                    && xml.SelectSingleNode("/MapPrefab/thingStates/li[def='MedicineIndustrial']/stackCount").InnerText == "5",
                    "limited aid is authored in the prefab");
            }
            if (tutorial && name == "PrototypeShip")
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
        }
    }

    static void PrefabTurretGraphs(string directory, string[] names = null)
    {
        // Load actual authored turrets, including their deep gun and Verb graphs;
        // testing just the weapon Comp misses Scribe's nested identity registration.
        foreach (string name in names ?? new[] { "Shipyard", "WeaponRange" })
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
            Type treeType = typeof(MapPrefab).Assembly.GetType("DeltaV.PrefabTreeNode");
            var tree = (TreeNode)HarmonyLib.AccessTools.Method(treeType, "Build",
                new[] { typeof(IEnumerable<string>), typeof(int) }).Invoke(null,
                new object[] { MapPrefab.ListPrefabFiles(roots), TreeOpenMasks.Default });
            TreeNode ships = tree.children.Single(node =>
                (string)HarmonyLib.AccessTools.Field(treeType, "label").GetValue(node) == "Ships");
            Check(ships.IsOpen(TreeOpenMasks.Default) && ships.children.Count == 1
                && !ships.children[0].Openable,
                "prefab selector builds an initially expanded native TreeNode directory with file leaves");
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
