using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

    // Overlay attachments share the conduit-style no-clear building area. When the vanilla
    // GenConstruct.BlocksConstruction reports the host as a blocker, the construction work
    // giver hands the pawn a deconstruct job for that host through HandleBlockingThingJob.
    // ThingDefGenerator_Buildings copies clearBuildingArea onto the generated blueprint and
    // frame Defs, so both stages are evaluated with that same copy.
    static void CheckOverlayAttachmentConstruction()
    {
        const string placeWorker = "DeltaV.PlaceWorker_SpaceWeaponAttachment";
        const string baseName = "DeltaV_SpaceWeaponAttachmentBase";
        var files = new[] { "Defs/SpaceCombat/DeltaV_SpaceWeapons.xml", "Defs/SpaceCombat/DeltaV_EnergyRecovery.xml",
            "Defs/SpaceCombat/DeltaV_Engineering.xml", "Defs/SpaceCombat/DeltaV_RailgunRapidReset.xml",
            "Defs/ThingDefs_Buildings/DeltaV_StationTrade.xml" };
        var named = new Dictionary<string, XmlNode>();
        var defs = new List<XmlNode>();
        foreach (var file in files)
        {
            Check(File.ReadAllBytes(Path.Combine("1.6", file)).SequenceEqual(File.ReadAllBytes(Path.Combine("1.7", file))),
                file + " is shared verbatim by 1.6 and 1.7");
            var document = new XmlDocument(); document.Load(Path.Combine("1.6", file));
            foreach (XmlNode node in document.SelectNodes("/Defs/ThingDef"))
            {
                if (node.Attributes["Name"] is XmlAttribute name) named[name.Value] = node;
                if (node["defName"] != null) defs.Add(node);
            }
        }
        IEnumerable<XmlNode> Chain(XmlNode node)
        {
            for (; node != null; node = node.Attributes["ParentName"] is XmlAttribute parent
                    ? named.GetValueOrDefault(parent.Value) : null) yield return node;
        }
        bool Declares(XmlNode node) => node.SelectNodes("placeWorkers/li").Cast<XmlNode>().Any(li => li.InnerText == placeWorker);
        var overlays = defs.Where(node => Chain(node).Any(Declares)
            || node.Attributes["ParentName"]?.Value == baseName).ToList();

        XmlInheritance.Clear();
        // Core's BuildingBase does not declare the field; a same-named empty root only closes
        // the vanilla inheritance chain inside this offline fixture.
        var core = new XmlDocument(); core.LoadXml("<ThingDef Name=\"BuildingBase\" Abstract=\"True\" />");
        XmlInheritance.TryRegister(core.DocumentElement, null);
        foreach (XmlNode node in overlays.SelectMany(Chain).Distinct()) XmlInheritance.TryRegister(node, null);
        try
        {
            XmlInheritance.Resolve();
            XmlNode[] resolved = overlays.Select(XmlInheritance.GetResolvedNodeFor).ToArray();
            var uncleared = resolved.Where(node => node["clearBuildingArea"]?.InnerText != "false" || !Declares(node))
                .Select(node => node["defName"].InnerText).ToList();
            Check(resolved.Length > 0 && uncleared.Count == 0,
                "every overlay attachment resolves " + baseName + " and keeps its place worker: " + string.Join(", ", uncleared));

            XmlNode turret = named["DeltaV_SpaceTurret1"];
            var hostDef = MakeDef("DeltaV_Weapon_RapidMainSmall"); hostDef.destroyable = true;
            hostDef.passability = (Traversability)Enum.Parse(typeof(Traversability), turret["passability"].InnerText);
            hostDef.fillPercent = float.Parse(turret["fillPercent"].InnerText, CultureInfo.InvariantCulture);
            var host = new Building { def = hostDef };
            Blueprint_Build Blueprint(XmlNode node, bool clearBuildingArea)
            {
                var building = MakeDef(node["defName"].InnerText);
                building.destroyable = true;
                building.passability = (Traversability)Enum.Parse(typeof(Traversability), node["passability"].InnerText);
                building.building.isEdifice = node["building"]["isEdifice"].InnerText == "true";
                building.clearBuildingArea = clearBuildingArea;
                var blueprintDef = MakeDef(building.defName + "_Blueprint");
                blueprintDef.clearBuildingArea = clearBuildingArea;
                blueprintDef.entityDefToBuild = building; building.blueprintDef = blueprintDef;
                var blueprint = (Blueprint_Build)Raw(typeof(Blueprint_Build)); blueprint.def = blueprintDef;
                return blueprint;
            }
            Frame FrameOf(Blueprint_Build blueprint)
            {
                var frame = (Frame)Raw(typeof(Frame));
                frame.def = MakeDef(blueprint.def.entityDefToBuild.defName + "_Frame");
                frame.def.entityDefToBuild = blueprint.def.entityDefToBuild;
                return frame;
            }
            var blocking = new List<string>();
            foreach (XmlNode node in resolved)
            {
                var blueprint = Blueprint(node, node["clearBuildingArea"]?.InnerText != "false");
                if (GenConstruct.BlocksConstruction(blueprint, host) || GenConstruct.BlocksConstruction(FrameOf(blueprint), host))
                    blocking.Add(blueprint.def.entityDefToBuild.defName);
            }
            Check(resolved.Length > 0 && blocking.Count == 0,
                "overlay blueprints and frames do not treat the host turret as a construction blocker: " + string.Join(", ", blocking));
            var clearing = Blueprint(resolved[0], clearBuildingArea: true);
            Check(GenConstruct.BlocksConstruction(clearing, host) && GenConstruct.BlocksConstruction(FrameOf(clearing), host),
                "default clearBuildingArea reproduces the vanilla blocker that sent pawns to deconstruct the host");
        }
        finally { XmlInheritance.Clear(); }
    }

    static void CheckEquipmentIntegration()
    {
        CheckOverlayAttachmentConstruction();
        CheckCargoLifecycle();
        CheckCargoModes();
        CheckStationTrade();
        CheckCargoVisualGeometry();
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
        var slotDocument = new XmlDocument();
        slotDocument.LoadXml(cargoNode.Element("modExtensions").Elements("li")
            .Single(n => (string)n.Attribute("Class") == "DeltaV.DefModExtension_CargoAttachments")
            .Element("slots").Element("li").ToString());
        var cargoSlot = DirectXmlToObject.ObjectFromXml<SpaceWeaponAttachmentSlot>(slotDocument.DocumentElement, true);
        cargoDef.size = ParseHelper.FromString<IntVec2>((string)cargoNode.Element("size"));
        var slotHost = new Building { def = cargoDef };
        Set(slotHost, "positionInt", new IntVec3(20, 0, 20));
        var moduleGraphic = new Graphic_SpaceWeaponAttachment();
        var visualCenter = AccessTools.Method(typeof(Graphic_SpaceWeaponAttachment), "VisualCenter");
        for (int rotation = 0; rotation < 4; rotation++)
        {
            var facing = new Rot4(rotation); Set(slotHost, "rotationInt", facing);
            IntVec3 cell = SpaceWeaponGeometry.RotatedCell(slotHost, cargoSlot.cell);
            Vector3 shown = (Vector3)visualCenter.Invoke(moduleGraphic,
                new object[] { cell.ToVector3Shifted(), facing, facing, cargoSlot });
            Vector3 center = slotHost.TrueCenter();
            Vector2 corner = SpaceTransform.RotatePlacementOffset(new Vector2(.5f, 1f), facing);
            Check(slotHost.OccupiedRect().Contains(cell) && cargoSlot.drawOffset == Vector2.zero
                && Math.Abs(shown.x - cell.x - .5f) < .0001f && Math.Abs(shown.z - cell.z - .5f) < .0001f
                && Math.Abs(shown.x - center.x - corner.x) < .0001f && Math.Abs(shown.z - center.z - corner.y) < .0001f,
                "cargo module build cell and rendered center coincide at the service-row socket: " + facing);
        }
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
        fixtureMod.assemblies.loadedAssemblies.Add(typeof(CompSpaceCargo).Assembly);
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
            && (string)graphicsNode.Element("targetDef") == "DeltaV_CargoStorage",
            "cargo has a registered ASF graphics variation and initialized storage worker");
        var cargo = (Building_SpaceCargoContainer)Raw(typeof(Building_SpaceCargoContainer));
        cargo.def = cargoDef; cargo.def.graphicData = new GraphicData();
        Set(cargo, "mapIndexOrState", (sbyte)-1);
        var hackNode = cargoNode.Element("comps").Elements("li")
            .Single(n => (string)n.Attribute("Class") == "CompProperties_Hackable");
        var locked = new RecordingCargoGraphic(); var unlocked = new RecordingCargoGraphic();
        var stateGraphic = graphics.graphics[0].graphicDatas.Single();
        Check(stateGraphic.texPath.EndsWith("CargoPort") && stateGraphic.graphicClass == typeof(Graphic_SpaceCargo)
            && stateGraphic.drawSize == ParseHelper.FromString<Vector2>((string)cargoNode.Element("graphicData").Element("drawSize")),
            "ASF storage uses the shared port graphic with independent state and paint channels");
        Set(stateGraphic, "cachedGraphic", unlocked);
        Check(hackNode.Element("hackedGraphicData") == null
            && (string)cargoNode.Element("thingClass") == "DeltaV.Building_SealedSpaceCargoContainer",
            "native crate keeps hack/open behaviour without an overlapping replacement graphic");
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
                && printed.Count == 1 && printed[0].Graphic == unlocked,
                "ASF storage rebuild selects exactly one unlocked graphic after config validation");
            renderer.CurrentGraphic.Worker.PrintAt(null, printed[0], new ITransformable.TransformData());
            Check(unlocked.prints == 1, "ASF storage draws once without overlapping native crate graphics");
            update.Invoke(renderer, null);
            Check(printed.Count == 1 && printed[0].Graphic == unlocked,
                "repeated cargo graphic rebuild remains valid");
        }
        finally { drawing.UnpatchAll(drawing.Id); }
    }
}
