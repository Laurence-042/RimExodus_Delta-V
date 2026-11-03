using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Xml;
using AdaptiveStorage;
using AdaptiveStorage.PrintDatas;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    sealed class RecordingCargoGraphic : Graphic
    {
        public int prints;
        public RecordingCargoGraphic() { color = colorTwo = Color.white; drawSize = new Vector2(2, 3); }
        public override Material MatSingle => null;
        public override void Print(SectionLayer layer, Thing thing, float extraRotation) => prints++;
    }

    sealed class RecordingCargoPrintData : PrintData
    {
        public RecordingCargoPrintData(Thing thing, Graphic graphic) { Thing = thing; Graphic = graphic; }
        public override void PrintAt(SectionLayer layer, in ITransformable.TransformData transformData)
            => Graphic.Print(layer, Thing, 0f);
        public override void DrawAt(in ITransformable.TransformData transformData) { }
    }

    static bool CreateCargoPrintData(Thing thing, Graphic graphic, ref PrintData __result)
    {
        if (graphic is not RecordingCargoGraphic) return true;
        __result = new RecordingCargoPrintData(thing, graphic); return false;
    }

    static void CheckEquipmentIntegration()
    {
        CheckCargoLifecycle();
        CheckStationTrade();
        var patch = new Harmony("DeltaV.Regression.EquipmentIntegration");
        patch.CreateClassProcessor(typeof(Patch_SwitchableBatteryPowerConnection)).Patch();
        try
        {
            var paths = new[] { "1.6/Defs/ThingDefs_Buildings/DeltaV_SafetyBattery.xml",
                "1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml" };
            var batteryDefs = paths.SelectMany(path => XDocument.Load(path).Root.Elements("ThingDef"))
                .Where(node => node.Element("comps")?.Elements("li")
                    .Any(comp => (string)comp.Attribute("Class") == "DeltaV.CompProperties_SwitchableBattery") == true);
            foreach (var node in batteryDefs)
            {
                var def = MakeDef((string)node.Element("defName"));
                def.comps.Add(new CompProperties_SwitchableBattery());
                Check(def.ConnectToPower && !def.EverTransmitsPower,
                    def.defName + " is accepted by the real vanilla power-connector getter");
            }
            var ordinary = MakeDef("ordinary");
            Check(!ordinary.ConnectToPower, "connection adapter leaves buildings without power comps unchanged");
            ordinary.comps.Add(new CompProperties_Power { compClass = typeof(CompPowerTrader) });
            Check(ordinary.ConnectToPower, "connection adapter preserves vanilla consumers");
            var transmitting = MakeDef("transmitting");
            transmitting.comps.Add(new CompProperties_SwitchableBattery { transmitsPower = true });
            Check(!transmitting.ConnectToPower && transmitting.EverTransmitsPower,
                "connection adapter preserves vanilla transmitter versus connector distinction");
            var safe = XDocument.Load(paths[0]).Root.Element("ThingDef");
            Check(safe.Element("comps").Elements("li").Count() == 1,
                "safe battery uses one shared storage comp without a second power or weapon reservoir");
        }
        finally { patch.UnpatchAll(patch.Id); }

        // Load real XML and run ASF's destructive ConfigErrors check before rendering.
        // Only Unity print-data creation/drawing is replaced by counters.
        var xml = XDocument.Load("1.6/Defs/ThingDefs_Buildings/DeltaV_StationTrade.xml");
        var cargoNode = xml.Root.Elements("ThingDef").Single(n => (string)n.Element("defName") == "DeltaV_CargoContainer");
        var graphicsNode = xml.Root.Element("AdaptiveStorage.GraphicsDef");
        var cargoDef = MakeDef("DeltaV_CargoContainer");
        cargoDef.thingClass = typeof(Building_SpaceCargoContainer);
        cargoDef.destroyable = true;
        cargoDef.building.isEdifice = (bool)cargoNode.Element("building").Element("isEdifice");
        cargoDef.passability = Traversability.PassThroughOnly;
        cargoDef.surfaceType = SurfaceType.Item;
        var moduleNode = xml.Root.Elements("ThingDef").Single(n => (string)n.Element("defName") == "DeltaV_CargoTemperatureModule");
        var moduleDef = MakeDef("DeltaV_CargoTemperatureModule");
        moduleDef.destroyable = true;
        moduleDef.building.isEdifice = (bool)moduleNode.Element("building").Element("isEdifice");
        var wallDef = MakeDef("ordinaryWall"); wallDef.destroyable = true;
        var itemDef = MakeDef("cargoItem"); itemDef.category = ThingCategory.Item; itemDef.destroyable = true;
        Check(GenSpawn.SpawningWipes(cargoDef, cargoDef),
            "real vanilla spawn rule replaces the previous cargo rather than overlapping slot groups");
        Check(GenSpawn.SpawningWipes(cargoDef, wallDef) && GenSpawn.SpawningWipes(wallDef, cargoDef),
            "cargo and ordinary edifices use the same mutually exclusive building rule");
        Check(!GenSpawn.SpawningWipes(moduleDef, cargoDef) && !GenSpawn.SpawningWipes(cargoDef, moduleDef),
            "only the non-edifice overlay module can coexist with cargo");
        Check(!GenSpawn.SpawningWipes(cargoDef, itemDef),
            "cargo replacement does not wipe physical stored items");
        var graphics = new GraphicsDef {
            defName = (string)graphicsNode.Element("defName"),
            targetDefs = new List<ThingDef> { cargoDef },
            showBaseGraphic = (bool)graphicsNode.Element("showBaseGraphic"),
            showContainedItems = (bool)graphicsNode.Element("showContainedItems") };
        var fixtureMod = (ModContentPack)Raw(typeof(ModContentPack));
        fixtureMod.assemblies = new ModAssemblyHandler(fixtureMod);
        fixtureMod.assemblies.loadedAssemblies.Add(typeof(StorageGraphicData_CargoState).Assembly);
        fixtureMod.assemblies.loadedAssemblies.Add(typeof(StorageGraphic).Assembly);
        LoadedModManager.RunningModsListForReading.Add(fixtureMod);
        try
        {
            foreach (var node in graphicsNode.Element("graphics").Elements("li"))
            {
                var document = new XmlDocument(); document.LoadXml(node.ToString());
                var graphic = DirectXmlToObject.ObjectFromXml<StorageGraphic>(document.DocumentElement, true);
                graphic.Initialize(graphics); graphics.graphics.Add(graphic);
            }
        }
        finally { LoadedModManager.RunningModsListForReading.Remove(fixtureMod); }
        var invalid = new GraphicsDef { defName = "BrokenCargoFixture", targetDefs = graphics.targetDefs,
            graphics = new List<StorageGraphic> { new StorageGraphic() } };
        Check(invalid.ConfigErrors().ToList().Any(e => e.Contains("missing graphicData")) && invalid.graphics.Count == 0,
            "reproduce ASF rejecting and deleting the previous empty graphics entry");
        var errors = graphics.ConfigErrors().ToList();
        Check(errors.Count == 0 && graphics.graphics.Count == 1,
            "real cargo XML passes installed ASF ConfigErrors and retains its graphics entry: " + string.Join("; ", errors));
        Check(graphics.graphics.Count > 0 && graphics.graphics[0].Worker != null
            && (string)graphicsNode.Element("targetDef") == "DeltaV_CargoContainer",
            "cargo has a registered ASF graphics variation and initialized storage worker");
        var cargo = (Building_SpaceCargoContainer)Raw(typeof(Building_SpaceCargoContainer));
        cargo.def = cargoDef; cargo.def.graphicData = new GraphicData();
        Set(cargo, "mapIndexOrState", (sbyte)-1);
        var hackNode = cargoNode.Element("comps").Elements("li")
            .Single(n => (string)n.Attribute("Class") == "CompProperties_Hackable");
        var locked = new RecordingCargoGraphic(); var unlocked = new RecordingCargoGraphic();
        var stateGraphic = (StorageGraphicData_CargoState)graphics.graphics[0].graphicDatas.Single();
        Check(stateGraphic.texPath.EndsWith("CargoContainer_Locked")
            && stateGraphic.unlockedGraphicData.texPath.EndsWith("CargoContainer_Unlocked")
            && stateGraphic.drawSize == new Vector2(2, 3)
            && stateGraphic.unlockedGraphicData.drawSize == new Vector2(2, 3),
            "XML loads both actual cargo textures and sizes through the shared ASF state graphic");
        Set(stateGraphic, "cachedGraphic", locked); Set(stateGraphic.unlockedGraphicData, "cachedGraphic", unlocked);
        Check(hackNode.Element("unhackedGraphicData") == null && hackNode.Element("hackedGraphicData") == null,
            "vanilla hackable has no duplicate graphics to print over ASF");
        var hack = new CompHackable { parent = cargo, props = new CompProperties_Hackable { defence = 1800 } };
        Set(hack, "defence", 1800f);
        Set(cargo, "comps", new List<ThingComp> { hack });
        var renderer = (StorageRenderer)Raw(typeof(StorageRenderer));
        Set(renderer, "<Parent>k__BackingField", cargo);
        var printed = new List<PrintData>(); Set(renderer, "_buildingGraphics", printed);
        renderer.CurrentGraphicVariation = graphics;
        renderer.CurrentGraphic = graphics.graphics[0];
        var update = AccessTools.Method(typeof(StorageRenderer), "UpdateBuildingGraphics");
        var drawing = new Harmony("DeltaV.Regression.CargoGraphicDrawing");
        drawing.Patch(AccessTools.Method(typeof(PrintData), "Create"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CreateCargoPrintData)));
        try
        {
            update.Invoke(renderer, null);
            Check(!renderer.BuildingGraphicsDirty && !renderer.VisibleBaseGraphic
                && printed.Count == 1 && printed[0].Graphic == locked,
                "ASF rebuild selects exactly one locked graphic after config validation");
            renderer.CurrentGraphic.Worker.PrintAt(null, printed[0], new ITransformable.TransformData());
            Set(hack, "progress", 1800f); Set(hack, "hacked", true);
            update.Invoke(renderer, null);
            Check(printed.Count == 1 && printed[0].Graphic == unlocked,
                "ASF rebuild replaces locked graphic with unlocked graphic from original hack state");
            renderer.CurrentGraphic.Worker.PrintAt(null, printed[0], new ITransformable.TransformData());
            Check(locked.prints == 1 && unlocked.prints == 1, "state changes draw once without overlapping cargo graphics");
            update.Invoke(renderer, null);
            Check(printed.Count == 1 && printed[0].Graphic == unlocked,
                "repeated cargo graphic rebuild remains valid after state change");
        }
        finally { drawing.UnpatchAll(drawing.Id); }
    }
}
