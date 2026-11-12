using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml;
using DeltaV;
using HarmonyLib;
using RimWorld;
using Verse;

internal static partial class Program
{
    static void ArrivalWreck(string recordsPath, string prefabPath, Game game)
    {
            using var json = JsonDocument.Parse(File.ReadAllText(recordsPath.Replace(".records.json", ".layout.json")));
            var root = json.RootElement;
            IntVec3 Cell(JsonElement c) => new IntVec3(c[0].GetInt32(), 0, c[1].GetInt32());
            var map = Raw<Map>(); map.info = new MapInfo { Size = new IntVec3(70, 1, 80) };
            Set(game, "maps", new List<Map> { map });
            map.cellIndices = new CellIndices(map); map.thingGrid = new ThingGrid(map);
            map.edificeGrid = new EdificeGrid(map); map.roofGrid = new RoofGrid(map);
            map.floodFiller = new FloodFiller(map);
            var buildings = new List<Building>();
            foreach (var row in root.GetProperty("records").EnumerateArray())
            {
                string name = row.GetProperty("name").GetString();
                string Field(string key, string fallback) => SourceChain(name)
                    .Select(n => n.SelectSingleNode(key)?.InnerText).FirstOrDefault(v => v != null) ?? fallback;
                var def = Raw<ThingDef>(); def.defName = name; def.label = name;
                def.size = IntVec2.FromString(Field("size", "(1,1)"));
                def.rotatable = bool.Parse(Field("rotatable", "true"));
                def.category = Enum.Parse<ThingCategory>(Field("category", "Undefined"));
                def.passability = Enum.Parse<Traversability>(Field("passability", "Standable"));
                def.holdsRoof = bool.Parse(Field("holdsRoof", "false"));
                def.hasInteractionCell = bool.Parse(Field("hasInteractionCell", "false"));
                def.interactionCellOffset = IntVec3.FromString(Field("interactionCellOffset", "(0,0,0)"));
                def.building = new BuildingProperties {
                    shipPart = bool.Parse(Field("building/shipPart", "false")),
                    isEdifice = bool.Parse(Field("building/isEdifice", "true")),
                    isAttachment = bool.Parse(Field("building/isAttachment", "false")),
                    supportsWallAttachments = bool.Parse(Field("building/supportsWallAttachments", "false")) };
                var pos = Cell(row.GetProperty("root")); var rot = new Rot4(row.GetProperty("rotation").GetInt32());
                var expected = row.GetProperty("cells").EnumerateArray().Select(Cell).ToHashSet();
                Check(expected.SetEquals(GenAdj.OccupiedRect(pos, rot, def.size)), name + " native footprint");
                for (int i = 0; i < 4; i++) {
                    var turn = new Rot4(i);
                    Check(expected.Select(c => PrefabUtility.GetAdjustedLocalPosition(c, turn)).ToHashSet().SetEquals(
                        GenAdj.OccupiedRect(PrefabUtility.GetAdjustedThingLocalPosition(def, turn, pos),
                            def.rotatable ? new Rot4((rot.AsInt+i)%4) : Rot4.North, def.size)), name + " prefab rotation");
                }
                if (def.category != ThingCategory.Building) continue;
                Building b = name == "Ship_CryptosleepCasket" ? Raw<Building_CryptosleepCasket>() : Raw<Building>(); b.def = def;
                Set(b, "positionInt", pos); Set(b, "rotationInt", rot); Set(b, "mapIndexOrState", (sbyte)0);
                var comps = new List<ThingComp>();
                foreach (var node in SourceChain(name)) {
                    var list = node["comps"]; if (list == null) continue;
                    foreach (XmlNode comp in list.SelectNodes("li")) {
                        string typeName = comp.Attributes["Class"]?.Value;
                        Type type = typeName == null ? null : GenTypes.GetTypeInAnyAssembly(typeName);
                        if (type == null || !typeof(CompProperties_Power).IsAssignableFrom(type)) continue;
                        var props = (CompProperties_Power)Activator.CreateInstance(type);
                        if (comp["transmitsPower"] != null) props.transmitsPower = bool.Parse(comp["transmitsPower"].InnerText);
                        comps.Add(new CompPowerTrader { parent = b, props = props });
                    }
                    if (list.Attributes["Inherit"]?.Value.Equals("false", StringComparison.OrdinalIgnoreCase) == true) break;
                }
                def.comps = comps.Select(c => c.props).ToList();
                Set(b, "comps", comps.Count == 0 ? null : comps); map.thingGrid.Register(b);
                if (def.building.isEdifice) map.edificeGrid.Register(b);
                buildings.Add(b);
            }
            foreach (var b in buildings) {
                if (b.def.building.isAttachment)
                    Check(GenConstruct.GetWallAttachedTo(b) != null, b.def.defName + " native wall attachment");
                if (b.def.HasSingleOrMultipleInteractionCells)
                    Check(GenConstruct.InteractionCellStandable(b.def, b.Position, b.Rotation, map, b).Accepted, b.def.defName + " interaction access");
                Check(GenConstruct.NotBlockingAnyInteractionCells(b.def, b.Position, b.Rotation, map, b).Accepted, b.def.defName + " does not block interaction");
            }
            var roofGrid = (RoofDef[])AccessTools.Field(typeof(RoofGrid), "roofGrid").GetValue(map.roofGrid);
            foreach (var c in root.GetProperty("roofs").EnumerateArray().Select(Cell))
                roofGrid[map.cellIndices.CellToIndex(c)] = Raw<RoofDef>();
            foreach (var c in root.GetProperty("roofs").EnumerateArray().Select(Cell))
                Check(RoofCollapseUtility.WithinRangeOfRoofHolder(c, map), "roof supported at " + c);
            var transmitters = buildings.Where(b => b.TransmitsPowerNow).ToList();
            var net = PowerNetMaker.NewPowerNetStartingFrom(transmitters[0]);
            Check(net.transmitters.Count == transmitters.Count, "one native power net includes every casket");
            foreach (var b in buildings.Where(b => b.def.defName == "Heater" || b.def.defName == "OxygenPump" || b.def.defName == "NutrientPasteDispenser"))
                Check(PowerConnectionMaker.BestTransmitterForConnector(b.Position, map)?.transNet == net, b.def.defName + " native connection reaches reactor net");
            var reactor = buildings.Single(b => b.def.defName == "Ship_Reactor");
            var attached = ShipUtility.ShipBuildingsAttachedTo(reactor).ToList();
            Check(attached.Count(b => b.def.defName == "Ship_CryptosleepCasket") == 5, "five original caskets remain attached to reactor by native ship parts");
            // Supply the authored cargo topology; exercise the production role binding.
            // Native path grids allocate Unity memory, so isolate that cache only.
            map.regionGrid = new RegionGrid(map); map.regionDirtyer = new RegionDirtyer(map);
            map.regionAndRoomUpdater = new RegionAndRoomUpdater(map);
            Set(map.regionAndRoomUpdater, "initialized", true);
            var cargo = root.GetProperty("cargo").EnumerateArray().Select(Cell).ToHashSet();
            var room = Raw<Room>(); var district = Raw<District>(); district.mapIndex = 0;
            var region = Raw<Region>(); region.mapIndex = 0; region.type = RegionType.Normal; region.valid = true;
            region.extentsClose = CellRect.FromCellList(cargo.ToList());
            Set(region, "districtInt", district); Set(region, "cachedCellCount", cargo.Count);
            Set(district, "roomInt", room); Set(district, "regions", new List<Region> { region });
            Set(room, "districts", new List<District> { district }); Set(room, "cachedCellCount", cargo.Count);
            Set(room, "cachedExposedThreshold", 1);
            foreach (var c in cargo) map.regionGrid.SetRegionAt(c, region);
            arrivalWalkable = root.GetProperty("floor").EnumerateArray().Select(Cell).ToHashSet();
            arrivalBiome = new BiomeDef { inVacuum = true };
            var harness = new Harmony("DeltaV.TutorialRegression.ArrivalBinding");
            harness.Patch(AccessTools.PropertyGetter(typeof(Map), nameof(Map.Biome)), prefix: new HarmonyMethod(typeof(Program), nameof(ArrivalBiome)));
            harness.Patch(AccessTools.Method(typeof(GenGrid), nameof(GenGrid.Walkable)), prefix: new HarmonyMethod(typeof(Program), nameof(ArrivalWalkable)));
            try {
                var bound = GenStep_DeltaVTutorialStation.BindArrivalCaskets(buildings.Cast<Thing>().Reverse().ToList());
                Check(bound.Count == 6 && bound[0].Position == Cell(root.GetProperty("captain")),
                    "production binding wakes the cargo captain instead of the leftmost crew member");
                Check(bound.Skip(1).All(c => !cargo.Contains(c.Position)), "all five original crew caskets stay outside the aid compartment");
            }
            finally { harness.UnpatchAll(harness.Id); arrivalWalkable = null; arrivalBiome = null; }
            Console.WriteLine($"Native geometry/access/roof/power: {checks} checks; five caskets on beams, captain on cargo supply run.");
    }

    static BiomeDef arrivalBiome;
    static int arrivalSelectionCalls;
    static bool ArrivalSelectionBase()
    {
        Check(Find.GameInitData.startingPawnKind == PawnKindDefOf.Colonist, "civilian kind is selected before native candidate generation");
        arrivalSelectionCalls++;
        return false;
    }

    static void ArrivalClothing(Game game, string data)
    {
        var xml = new XmlDocument(); xml.Load(Path.Combine(data, "Core/Defs/PawnKindDefs_Humanlikes/PawnKinds_Player.xml"));
        Check(xml.SelectSingleNode("/Defs/PawnKindDef[defName='Colonist']/apparelTags/li").InnerText == "IndustrialBasic",
            "native colonist kind generates ordinary personal clothing");
        var binding = AccessTools.Field(typeof(DefOfHelper), "bindingNow"); var oldBinding = binding.GetValue(null);
        binding.SetValue(null, true);
        var oldData = game.InitData; var oldKind = PawnKindDefOf.Colonist;
        var harness = new Harmony("DeltaV.TutorialRegression.ArrivalClothing");
        harness.Patch(AccessTools.Method(typeof(ScenPart_ConfigPage_ConfigureStartingPawns), "PostIdeoChosen"),
            prefix: new HarmonyMethod(typeof(Program), nameof(ArrivalSelectionBase)));
        try
        {
            binding.SetValue(null, true); PawnKindDefOf.Colonist = new PawnKindDef { defName = "Colonist" };
            game.InitData = new GameInitData();
            arrivalSelectionCalls = 0;
            new ScenPart_ConfigPage_TutorialColonists().PostIdeoChosen();
            Check(arrivalSelectionCalls == 1, "tutorial uses the original candidate-selection flow");
            Check(game.InitData.startingPawnKind == PawnKindDefOf.Colonist,
                "civilian kind remains the native default for later candidate rerolls");
        }
        finally
        {
            harness.UnpatchAll(harness.Id); game.InitData = oldData;
            PawnKindDefOf.Colonist = oldKind; binding.SetValue(null, oldBinding);
        }
    }

    static HashSet<IntVec3> arrivalWalkable;
    static bool ArrivalWalkable(IntVec3 c, ref bool __result) { __result = arrivalWalkable.Contains(c); return false; }
    static bool ArrivalBiome(ref BiomeDef __result) { __result = arrivalBiome; return false; }

    static void PrefabLifeSupport(string records, Game game, string layoutName)
    {
        using var layout = JsonDocument.Parse(File.ReadAllText(Path.Combine(records, layoutName + ".layout.json")));
        using var authored = JsonDocument.Parse(File.ReadAllText(Path.Combine(records, layoutName + ".records.json")));
        var root = layout.RootElement;
        bool wreck = layoutName == "ArrivalStation";
        arrivalBiome = new BiomeDef { inVacuum = true, constantOutdoorTemperature =
            wreck ? root.GetProperty("outdoorTemperature").GetSingle() : -75f };
        var harness = new Harmony("DeltaV.TutorialRegression.ArrivalThermal");
        harness.Patch(AccessTools.PropertyGetter(typeof(Map), nameof(Map.Biome)),
            prefix: new HarmonyMethod(typeof(Program), nameof(ArrivalBiome)));
        try
        {
            var size = wreck ? new IntVec3(40, 1, 55)
                : new IntVec3(root.GetProperty("size")[0].GetInt32(), 1, root.GetProperty("size")[1].GetInt32());
            var map = Raw<Map>(); map.info = new MapInfo { Size = size };
            Set(game, "maps", new List<Map> { map });
            map.cellIndices = new CellIndices(map); map.regionGrid = new RegionGrid(map);
            map.thingGrid = new ThingGrid(map); map.terrainGrid = new TerrainGrid(map);
            map.edificeGrid = new EdificeGrid(map);
            Array.Fill(map.terrainGrid.topGrid, Raw<TerrainDef>());
            map.regionDirtyer = new RegionDirtyer(map);
            map.regionAndRoomUpdater = new RegionAndRoomUpdater(map);
            Set(map.regionAndRoomUpdater, "initialized", true);
            map.mapTemperature = new MapTemperature(map);
            float Property(string def, string xpath) => float.Parse(SourceChain(def)
                .Select(n => n.SelectSingleNode(xpath)?.InnerText).First(v => v != null),
                System.Globalization.CultureInfo.InvariantCulture);
            foreach (var cabin in root.GetProperty(wreck ? "rooms" : "cabins").EnumerateArray())
            {
                var cells = cabin.GetProperty("cells").EnumerateArray()
                    .Select(c => new IntVec3(c[0].GetInt32(), 0, c[1].GetInt32())).ToHashSet();
                var room = Raw<Room>(); var district = Raw<District>(); district.mapIndex = 0;
                var region = Raw<Region>(); region.mapIndex = 0; region.type = RegionType.Normal; region.valid = true;
                region.extentsClose = CellRect.FromCellList(cells.ToList());
                Set(region, "districtInt", district); Set(region, "cachedCellCount", cells.Count);
                Set(district, "roomInt", room); Set(district, "regions", new List<Region> { region });
                Set(district, "cachedCellCount", cells.Count);
                Set(room, "districts", new List<District> { district });
                Set(room, "cachedCellCount", cells.Count); Set(room, "cachedExposedThreshold", 1);
                Set(room, "uniqueContainedThings", new List<Thing>());
                foreach (var cell in cells) map.regionGrid.SetRegionAt(cell, region);
                var tracker = new RoomTempTracker(room, map); Set(room, "tempTracker", tracker);
                // Conservative native wall leakage: every cell faces the exterior
                // on all four sides. Roof is ordinary constructed roof, no insulation cheat.
                Set(tracker, "equalizeCells", Enumerable.Repeat(IntVec3.Zero, cells.Count * 4).ToList());
                float target = cabin.GetProperty("temperature").GetSingle(); room.Temperature = target;
                var heaters = new List<Building_Heater>();
                var coolers = new List<Building_Cooler>();
                var heatPushers = new List<CompHeatPusherPowered>();
                foreach (var row in authored.RootElement.EnumerateArray().Where(r => r.GetProperty("name").GetString() == "Heater"))
                {
                    var c = row.GetProperty("root"); var pos = new IntVec3(c[0].GetInt32(), 0, c[1].GetInt32());
                    if (!cells.Contains(pos)) continue;
                    var heater = Raw<Building_Heater>(); Set(heater, "mapIndexOrState", (sbyte)0); Set(heater, "positionInt", pos);
                    heater.compPowerTrader = new CompPowerTrader { parent = heater, props = new CompProperties_Power() };
                    Set(heater.compPowerTrader, "powerOnInt", true);
                    heater.compTempControl = new CompTempControl { parent = heater, targetTemperature = target,
                        props = new CompProperties_TempControl { energyPerSecond = Property("Heater", "comps/li[@Class='CompProperties_TempControl']/energyPerSecond") } };
                    heaters.Add(heater);
                }
                foreach (var row in authored.RootElement.EnumerateArray())
                {
                    var c = row.GetProperty("root"); var pos = new IntVec3(c[0].GetInt32(), 0, c[1].GetInt32());
                    string name = row.GetProperty("name").GetString();
                    if (name == "ChemfuelPoweredGenerator" && cells.Contains(pos))
                    {
                        var generator = Raw<Building>(); generator.def = Raw<ThingDef>();
                        Set(generator, "positionInt", pos); Set(generator, "mapIndexOrState", (sbyte)0);
                        var heat = new CompHeatPusherPowered { parent = generator,
                            props = new CompProperties_HeatPusher { heatPerSecond = Property(name, "comps/li[@Class='CompProperties_HeatPusher']/heatPerSecond") } };
                        var supply = new CompPowerTrader { parent = generator, props = new CompProperties_Power() };
                        Set(supply, "powerOnInt", true); Set(heat, "powerComp", supply);
                        Set(generator, "comps", new List<ThingComp> { supply, heat });
                        heatPushers.Add(heat);
                    }
                    if (name != "Cooler") continue;
                    var rot = new Rot4(row.GetProperty("rotation").GetInt32());
                    if (!cells.Contains(pos + IntVec3.South.RotatedBy(rot))) continue;
                    var cooler = Raw<Building_Cooler>(); Set(cooler, "mapIndexOrState", (sbyte)0);
                    Set(cooler, "positionInt", pos); Set(cooler, "rotationInt", rot);
                    cooler.compPowerTrader = new CompPowerTrader { parent = cooler, props = new CompProperties_Power() };
                    Set(cooler.compPowerTrader, "powerOnInt", true);
                    cooler.compTempControl = new CompTempControl { parent = cooler, targetTemperature = target,
                        props = new CompProperties_TempControl { energyPerSecond = Property(name, "comps/li[@Class='CompProperties_TempControl']/energyPerSecond") } };
                    coolers.Add(cooler);
                }
                if (wreck)
                    Check(heaters.Count == cabin.GetProperty("heaters").GetInt32(), "native thermal fixture uses actual authored heaters");
                int pumpCount = authored.RootElement.EnumerateArray().Count(row =>
                    row.GetProperty("name").GetString() == "OxygenPump"
                    && cells.Contains(new IntVec3(row.GetProperty("root")[0].GetInt32(), 0, row.GetProperty("root")[1].GetInt32())));
                Check(heaters.Count > 0 && pumpCount > 0, layoutName + " sealed cabin has powered climate and oxygen hardware");
                var oxygenParent = Raw<Building>(); Set(oxygenParent, "positionInt", cells.First());
                Set(oxygenParent, "mapIndexOrState", (sbyte)0);
                var pump = new CompOxygenPusher { parent = oxygenParent,
                    props = new CompProperties_OxygenPusher { requiresPower = true,
                        airPerSecondPerHundredCells = Property("OxygenPump", "comps/li[@Class='CompProperties_OxygenPusher']/airPerSecondPerHundredCells") } };
                var power = new CompPowerTrader { parent = oxygenParent, props = new CompProperties_Power() };
                Set(power, "powerOnInt", true); Set(oxygenParent, "comps", new List<ThingComp> { power, pump });
                room.Vacuum = 1;
                float minimum = target, maximum = target;
                int repressurizedAt = 0;
                for (int tick = 1; tick <= 60000; tick++)
                {
                    if (tick % 120 == 7) tracker.EqualizeTemperature();
                    if (tick % 250 == 0)
                    {
                        foreach (var heater in heaters) heater.TickRare();
                        foreach (var heat in heatPushers) heat.CompTickRare();
                        foreach (var cooler in coolers) cooler.TickRare();
                        for (int i = 0; i < pumpCount; i++) pump.CompTickRare();
                    }
                    if (repressurizedAt == 0 && room.Vacuum == 0) repressurizedAt = tick;
                    minimum = Math.Min(minimum, room.Temperature); maximum = Math.Max(maximum, room.Temperature);
                }
                string label = layoutName + "/" + cabin.GetProperty("name").GetString();
                Check(minimum >= target - 3 && maximum <= target + 3, label + " maintains temperature over native 60000-tick thermal fixture");
                Check(room.Vacuum == 0, label + " native powered oxygen pump repressurizes sealed room");
                if (!wreck) Check(repressurizedAt > 0 && repressurizedAt <= 12500,
                    label + " repressurizes within five game hours");
                Console.WriteLine($"{label}: {cells.Count} cells, {heaters.Count} heaters / {coolers.Count} coolers / {heatPushers.Count} generators / {pumpCount} pumps, {minimum:F2}..{maximum:F2} C, vacuum={room.Vacuum:F2}; closed-room/full-power fixture.");
            }
            Check(root.GetProperty("generationW").GetSingle() >= root.GetProperty("nameplateLoadW").GetSingle(),
                layoutName + " can power its simultaneous nameplate loads");
        }
        finally { harness.UnpatchAll(harness.Id); arrivalBiome = null; }
    }
}
