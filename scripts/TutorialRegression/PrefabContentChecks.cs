using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
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
        string scenarioPath = Path.Combine(version, "Defs/Tutorial/DeltaV_Tutorial.xml");
        xml.Load(scenarioPath);
        var grants = xml.SelectNodes("/Defs/ScenarioDef[defName='DeltaV_OrbitalColonists']/scenario/parts/li[@Class='ScenPart_StartingThing_Defined']");
        Check(grants.Count == 1 && grants[0]["def"].InnerText == "StartingThing_Defined"
            && grants[0]["thingDef"].InnerText == "Gun_ChargeRifle" && grants[0]["count"] == null && grants[0]["quality"] == null,
            "shipped tutorial grants one default-quality charge rifle as a starting item");
        // 1.7 content is a build-time mirror of 1.6 and is absent before the first 1.7 build.
        string mirror = Path.GetFullPath(Path.Combine(prefabDirectory, "../../1.7/Defs/Tutorial/DeltaV_Tutorial.xml"));
        Check(!File.Exists(mirror) || File.ReadAllText(mirror) == File.ReadAllText(scenarioPath),
            "1.7 tutorial scenario mirrors the 1.6 source including the starting weapon");
    }

    static void SectorContentDefaults(string directory)
    {
        var settings = new DeltaVSettings();
        settings.EnsureSectorGenerationTable();
        var leaves = SectorGenerationTable.Leaves(settings.sectorGenerationRoots).ToList();
        Check(leaves.Count == 12, "default sector generation table has twelve leaves");
        Check(leaves.All(leaf => leaf.prefabPaths.Count == 1 && !leaf.prefabPaths[0].NullOrEmpty()),
            "every sector generation leaf has a shipped default prefab");
        Check(leaves.Select(leaf => leaf.prefabPaths[0]).Distinct().Count() == leaves.Count,
            "sector generation leaves use distinct authored defaults");
        List<string> shippedPaths = MapPrefab.ListPrefabFiles(new[] { Path.Combine(directory, "Sector") });
        Check(shippedPaths.Count == 17
            && shippedPaths.All(path => path.StartsWith("Craft/") || path.StartsWith("Stations/") || path.StartsWith("Asteroids/")),
            "all seventeen sector prefabs use the sector purpose directories");
        Check(leaves.All(leaf => leaf.prefabPaths[0].StartsWith("Sector/")),
            "every sector default points into the sector catalog tree");
        foreach (SectorGenerationNode leaf in leaves)
        {
            string path = Path.Combine(directory, leaf.prefabPaths[0]);
            Check(File.Exists(path), leaf.id + " default prefab is shipped");
            var document = new XmlDocument(); document.Load(path);
            Check(document.DocumentElement?.GetAttribute("formatVersion") == "2"
                && document.SelectNodes("/MapPrefab/thingStates/li").Count > 0,
                leaf.id + " default is a non-empty format-2 prefab");
            Check(document.SelectSingleNode("/MapPrefab/thingStates/li[def='PilotConsole' "
                + "and not(state/saveable/combat/fireControlMode='Automatic')]") == null,
                leaf.id + " authored pilot consoles use automatic fire control");
            int clusters = document.SelectNodes("/MapPrefab/thingStates/li[def='DeltaV_PrefabClusterMarker']").Count;
            Check(clusters == (leaf.id == "asteroid" ? 1 : 0),
                leaf.id + " default has the expected cluster-core role");
            Check(document.SelectSingleNode("/MapPrefab/thingStates/li[def='GravEngine' or def='GravFieldExtender']") == null,
                leaf.id + " default has no surface-launch hardware");
            var cells = document.SelectNodes("/MapPrefab/thingStates/li[def='GravcorePowerCell']");
            Check(cells.Count == 0, leaf.id + " has no gravcore power cell");
            if (leaf.id == "fighter")
            {
                Check(document.SelectNodes("/MapPrefab/thingStates/li[def='ChemfuelPoweredGenerator' and state/saveable/fuel > 0]").Count == 1
                    && document.SelectNodes("/MapPrefab/thingStates/li[def='Battery']").Count == 2
                    && document.SelectSingleNode("/MapPrefab/thingStates/li[def='Chemfuel' and stackCount > 0]") != null,
                    "fighter ships with one fueled chemfuel generator, two standard batteries and reserve fuel");
            }
            if (leaf.id == "cruiser")
                Check(document.SelectNodes("/MapPrefab/thingStates/li[def='DeltaV_ShieldGenerator' "
                    + "and state/saveable[DeltaV_shieldMode='Guard' and DeltaV_shieldGroundRadius > 0 "
                    + "and DeltaV_regularCapacity=750 and fuel=750 and currentHitPoints=-1]]").Count == 2,
                    "both exported cruiser shield states are charged guard domes");
            if (leaf.prefabPaths[0].StartsWith("Sector/Stations/"))
                CheckStationSolar(document, leaf.id, "Battery",
                    leaf.id == "trade-station" || leaf.id == "shipyard" || leaf.id == "factory-station" ? 1 : 0);
        }
        Check(settings.sectorGenerationPreset == SectorGenerationPreset.Standard,
            "fresh sector settings use the standard density preset");

        var expectedScales = new Dictionary<SectorGenerationPreset, float>
        {
            [SectorGenerationPreset.Disabled] = 0f,
            [SectorGenerationPreset.Sparse] = .4f,
            [SectorGenerationPreset.Standard] = 1f,
            [SectorGenerationPreset.Dense] = 2f
        };
        double standardTotal = leaves.Sum(leaf => leaf.standardDensityPer100MCells2)
            * SectorGenerationPresets.DefaultReferenceSectorAreaCellsSquared
            / SectorGenerationPresets.DensityAreaUnitCellsSquared;
        var expectedTotals = new Dictionary<SectorGenerationPreset, double>
        {
            [SectorGenerationPreset.Disabled] = 0d,
            [SectorGenerationPreset.Sparse] = standardTotal * .4d,
            [SectorGenerationPreset.Standard] = standardTotal,
            [SectorGenerationPreset.Dense] = standardTotal * 2d
        };
        Check(SectorGenerationPresets.All.Count == expectedScales.Count,
            "sector density preset registry has four choices");
        foreach (SectorGenerationPreset preset in SectorGenerationPresets.All)
        {
            Check(Math.Abs(SectorGenerationPresets.Scale(preset) - expectedScales[preset]) < .0001f,
                preset + " sector preset uses the reviewed uniform multiplier");
            settings.sectorGenerationPreset = preset;
            Check(Math.Abs(SectorGenerationPresets.ExpectedObjectCount(settings,
                    SectorGenerationPresets.DefaultReferenceSectorAreaCellsSquared) - expectedTotals[preset]) < 0.0001d,
                preset + " sector preset has the reviewed default-scale reference count");
            Check(Math.Abs(SectorGenerationPresets.ExpectedObjectCount(settings,
                    SectorGenerationPresets.DefaultReferenceSectorAreaCellsSquared * 4d)
                - expectedTotals[preset] * 4d) < 0.0001d,
                preset + " sector preset expectation scales linearly with area");
        }

        var stochasticRound = typeof(SectorContentService).GetMethod("StochasticRound",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        int Round(double value, int seed) => (int)stochasticRound.Invoke(null, new object[] { value, seed });
        int fractional = Round(7.25d, 12345);
        Check(stochasticRound != null && Round(0d, 1) == 0 && Round(7d, 2) == 7
            && (fractional == 7 || fractional == 8) && fractional == Round(7.25d, 12345),
            "sector expected counts use deterministic stochastic rounding");

        string preservedPool = leaves[0].prefabPaths[0];
        settings.sectorGenerationPreset = SectorGenerationPreset.Dense;
        Check(Math.Abs(SectorGenerationPresets.ExpectedObjectCount(settings,
                SectorGenerationPresets.DefaultReferenceSectorAreaCellsSquared)
                - expectedTotals[SectorGenerationPreset.Dense]) < 0.0001d
            && leaves[0].prefabPaths.SequenceEqual(new[] { preservedPool }),
            "applying a sector preset changes density while preserving prefab pools");
        settings.sectorGenerationPreset = SectorGenerationPreset.Disabled;
        Check(SectorGenerationPresets.ExpectedObjectCount(settings,
                SectorGenerationPresets.DefaultReferenceSectorAreaCellsSquared) == 0d
            && leaves.All(leaf => leaf.prefabPaths.Count > 0 && leaf.standardDensityPer100MCells2 > 0f),
            "disabled sector preset suppresses generation without mutating recipes");

        var custom = settings.sectorGenerationRoots.Select(root => root.Copy()).ToList();
        SectorGenerationNode customLeaf = SectorGenerationTable.Leaves(custom).First();
        customLeaf.label = "paper-thin research skiff";
        customLeaf.prefabPaths = new List<string> { "Custom/ResearchSkiff.xml" };
        customLeaf.standardDensityPer100MCells2 = .123f;
        Check(SectorGenerationTable.Validate(custom, out _)
            && SectorGenerationTable.Leaves(custom).First().prefabPaths.Single() == "Custom/ResearchSkiff.xml"
            && leaves[0].prefabPaths.Single() == preservedPool,
            "editable generation trees validate and deep-copy without changing defaults");
        var generationDocument = new SectorGenerationDocument
        {
            densityPreset = SectorGenerationPreset.Sparse,
            roots = custom.Select(root => root.Copy()).ToList()
        };
        var serializer = new DataContractJsonSerializer(typeof(SectorGenerationDocument));
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, generationDocument); stream.Position = 0;
            var restored = (SectorGenerationDocument)serializer.ReadObject(stream);
            Check(restored.version == 1 && restored.densityPreset == SectorGenerationPreset.Sparse
                && SectorGenerationTable.Validate(restored.roots, out _)
                && SectorGenerationTable.Leaves(restored.roots).First().label == "paper-thin research skiff",
                "sector generation JSON preserves editable hierarchy, names, densities and prefab pools");
        }
        custom.Add(custom[0].Copy());
        Check(!SectorGenerationTable.Validate(custom, out _),
            "generation table validation rejects duplicate stable IDs");

        string trade = Path.Combine(directory, leaves.Single(leaf => leaf.id == "trade-station").prefabPaths[0]);
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
        Check(cluster["contentTypeId"] == null
            && cluster["innerRadius"].InnerText == "70"
            && cluster["outerRadius"].InnerText == "260"
            && cluster["densityPerTenThousand"].InnerText == "0.6"
            && cluster["minimumSpacing"].InnerText == "28",
            "asteroid belt core stores geometry without a generation classification");
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

    static void MatureHydroponics(string directory)
    {
        int totalBasins = 0;
        int totalRice = 0;
        IntVec2 basinSize = ParseHelper.FromString<IntVec2>(sourceDefs["HydroponicsBasin"]["size"].InnerText);
        ThingDef riceDef = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_Rice");
        if (riceDef == null)
        {
            riceDef = Raw<ThingDef>();
            riceDef.defName = "Plant_Rice";
            riceDef.category = ThingCategory.Plant;
            riceDef.thingClass = typeof(Plant);
            riceDef.stackLimit = 1;
            riceDef.size = IntVec2.One;
            riceDef.useHitPoints = true;
            riceDef.comps = new List<CompProperties>();
            riceDef.virtualDefs = new List<ThingDef>();
            riceDef.plant = new PlantProperties();
            DefDatabase<ThingDef>.Add(riceDef);
        }
        foreach (string path in Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories))
        {
            var xml = new XmlDocument();
            xml.Load(path);
            var basins = xml.SelectNodes("/MapPrefab/thingStates/li[def='HydroponicsBasin']")
                .Cast<XmlNode>().ToList();
            if (basins.Count == 0)
                continue;

            string label = Path.GetRelativePath(directory, path);
            var rice = xml.SelectNodes("/MapPrefab/thingStates/li[def='Plant_Rice']")
                .Cast<XmlNode>().ToList();
            var riceByCell = rice.GroupBy(node => IntVec3.FromString(node["pos"].InnerText))
                .ToDictionary(group => group.Key, group => group.ToList());
            var basinCells = new HashSet<IntVec3>();
            foreach (XmlNode basin in basins)
            {
                Check(basin.SelectSingleNode("state/saveable/plantDefToGrow")?.InnerText == "Plant_Rice",
                    label + " hydroponics basins retain rice as the selected crop");
                var root = IntVec3.FromString(basin["pos"].InnerText);
                var rotation = new Rot4(ParseHelper.FromString<int>(basin["rotation"].InnerText));
                foreach (IntVec3 cell in GenAdj.OccupiedRect(root, rotation, basinSize))
                    Check(basinCells.Add(cell), label + " hydroponics footprints do not overlap");
            }

            Check(basinCells.Count == basins.Count * 4 && rice.Count == basinCells.Count
                && basinCells.All(cell => riceByCell.TryGetValue(cell, out var plants) && plants.Count == 1),
                label + " has exactly one rice plant in every hydroponics cell");
            Check(rice.All(node => node.SelectSingleNode("state/saveable/growth")?.InnerText == "1"
                    && node.SelectSingleNode("state/saveable/sown")?.InnerText == "True"),
                label + " hydroponics rice is fully grown and sown");
            Check(rice.All(node => node["hitPoints"]?.InnerText == "85"
                    && node.SelectSingleNode("state/saveable/health")?.InnerText == "85"),
                label + " hydroponics rice stores full health in placement and Scribe state");
            var riceIds = rice.Select(node => node.SelectSingleNode("state/saveable/id")?.InnerText).ToList();
            Check(riceIds.All(id => !id.NullOrEmpty()) && riceIds.Distinct().Count() == riceIds.Count,
                label + " hydroponics rice has unique persistent IDs");
            int before = errors.Count;
            var restored = (Plant)MapPrefabThingState.Instantiate(new[] { new MapPrefabThingEntry {
                def = riceDef, stateXml = rice[0].SelectSingleNode("state/saveable").OuterXml,
            } }, label + " mature rice", null).Single();
            Check(errors.Count == before && restored.HitPoints == 85 && restored.Growth == 1f
                    && restored.Age == 30000 && restored.sown,
                label + " restores mature rice at full health through native Scribe");
            Check(xml.SelectNodes("/MapPrefab/thingStates/li[def='PlantPot']/state/saveable[plantDefToGrow='Plant_Rice']").Count == 0,
                label + " decorative plant pots are not converted to rice");
            totalBasins += basins.Count;
            totalRice += rice.Count;
        }
        Check(totalBasins == 256 && totalRice == 1024,
            "shipped prefab catalog has 1024 mature rice plants across all 256 hydroponics basins");
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
        const string rocks = "/MapPrefab/thingStates/li[starts-with(def,'Mineable') or def='Vacstone']";
        var rockEntries = xml.SelectNodes(rocks).Cast<XmlElement>().ToList();
        Check(xml.SelectNodes("/MapPrefab/prefab/terrain/*").Count == 1
            && xml.SelectSingleNode("/MapPrefab/prefab/terrain/Space") != null,
            label + " authors only Space terrain");
        Check(xml.SelectNodes("/MapPrefab/foundations/li|/MapPrefab/environment/li|/MapPrefab/roofs/li").Count == 0,
            label + " has no foundation, roof or authored atmosphere");
        Check(rockEntries.Any(e => e["def"].InnerText == "Vacstone")
            && rockEntries.Any(e => e["def"].InnerText.StartsWith("Mineable"))
            && rockEntries.All(e => e.SelectSingleNode("state/saveable/canChangeTerrainOnDestroyed")?.InnerText == "false"),
            label + " preserves the ore and rock body and leaves Space after removal");
        var rockDefs = new Dictionary<string, ThingDef>();
        foreach (string name in rockEntries.Select(e => e["def"].InnerText).Distinct())
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if (def == null)
            {
                def = Raw<ThingDef>();
                def.defName = name;
                def.thingClass = typeof(Mineable);
                def.category = ThingCategory.Building;
                def.stackLimit = 1;
                def.comps = new List<CompProperties>();
                def.building = new BuildingProperties { isNaturalRock = true };
                DefDatabase<ThingDef>.Add(def);
            }
            rockDefs.Add(name, def);
        }
        var entries = rockEntries.Select(e => new MapPrefabThingEntry
        {
            def = rockDefs[e["def"].InnerText],
            stateXml = e.SelectSingleNode("state/saveable").OuterXml
        }).ToList();
        Check(MapPrefabThingState.Instantiate(entries, label, null).Cast<Mineable>()
            .All(rock => !rock.canChangeTerrainOnDestroyed),
            label + " restores the no-terrain state through Scribe");
        var positions = xml.SelectNodes("/MapPrefab/thingStates/li/pos").Cast<XmlNode>()
            .Select(n => IntVec3.FromString(n.InnerText)).ToList();
        IntVec2 size = IntVec2.FromString(xml.SelectSingleNode("/MapPrefab/prefab/size").InnerText);
        for (int i = 0; i < 4; i++)
        {
            var cells = positions.Select(c => PrefabUtility.GetAdjustedLocalPosition(c, new Rot4(i))).ToHashSet();
            Check(cells.Count == positions.Count
                && cells.Max(c => c.x) - cells.Min(c => c.x) < (i % 2 == 0 ? size.x : size.z)
                && cells.Max(c => c.z) - cells.Min(c => c.z) < (i % 2 == 0 ? size.z : size.x),
                label + " has distinct occupied cells inside its rotated span");
        }
    }

    static void CheckPrefabContent(IEnumerable<string> files, bool authoredEnvironment = false, bool tutorial = false)
    {
        foreach (string path in files.OrderBy(p => p))
        {
            var xml = new XmlDocument(); xml.Load(path);
            string name = Path.GetFileNameWithoutExtension(path);
            Check(xml.SelectNodes("//terminalUuid|//markerUuid").Count == 0,
                name + " contains no terminal or prefab-marker UUID state");
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
                Check(xml.SelectNodes("/MapPrefab/thingStates/li[def='Building_OutfitStand']/state/saveable[BodyType='Male' and innerContainer/innerList/li/def='Apparel_Vacsuit' and innerContainer/innerList/li/def='Apparel_VacsuitHelmet']").Count == 1
                    && xml.SelectNodes("/MapPrefab/thingStates/li[def='Apparel_Vacsuit' or def='Apparel_VacsuitHelmet']").Count == 0,
                    "the stocked outfit stand has a deterministic adult body and keeps both vacuum garments contained");
                Check(xml.SelectSingleNode("/MapPrefab/thingStates/li[def='MealSurvivalPack']/stackCount").InnerText == "10"
                    && xml.SelectSingleNode("/MapPrefab/thingStates/li[def='MedicineIndustrial']/stackCount").InnerText == "5",
                    "limited aid is authored in the prefab");
            }
            if (tutorial && name == "PrototypeShip")
            {
                var cargo = xml.SelectNodes("/MapPrefab/thingStates/li[def='DeltaV_CargoStorage']").Cast<XmlNode>().ToList();
                var expectedCargoDefs = new HashSet<string> { "Steel", "ComponentIndustrial", "ComponentSpacer", "Chemfuel",
                    "MealSurvivalPack", "MealSimple", "MedicineIndustrial", "Silver", "RawRice" };
                Check(cargo.Count == 4, "gift has four cargo storages");
                foreach (var storage in cargo)
                {
                    var filter = storage.SelectSingleNode("state/saveable/settings/filter");
                    var allowed = filter?.SelectNodes("allowedDefs/li").Cast<XmlNode>().Select(n => n.InnerText).ToHashSet();
                    Check(filter != null && filter.SelectSingleNode("disallowedSpecialFilters") != null
                        && filter.SelectSingleNode("allowedHitPointsPercents")?.InnerText == "0~1"
                        && filter.SelectSingleNode("allowedQualityLevels")?.InnerText == "Awful~Legendary"
                        && allowed != null && allowed.SetEquals(expectedCargoDefs),
                        "gift cargo storage keeps full hit-point and quality ranges with the authored supply filter");
                }
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
    static readonly Dictionary<string, XmlNode> sourceApparelLayers = new();
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
            foreach (XmlNode node in xml.SelectNodes("/Defs/ApparelLayerDef[defName]"))
                sourceApparelLayers[node["defName"].InnerText] = node;
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
