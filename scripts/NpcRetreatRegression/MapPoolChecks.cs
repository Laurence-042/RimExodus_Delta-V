using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using DeltaV;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void CheckMapPool(UniverseManager universe)
    {
        int previousLimit = DeltaVMod.CurrentSettings.loadedMapLimit;
        var launched = new List<SpaceObject>();
        try
        {
            Map capacityMap = TestMap(48100);
            DeltaVMod.CurrentSettings.loadedMapLimit = Find.Maps.Count;
            Check(!universe.MapPool.TryMakeRoom(Find.Maps.Count + 1),
                "capacity guard rejects requests larger than the configured limit");
            int maps = Find.Maps.Count;
            var def = NewMaplessCargo("bulk_cargo").def;
            for (int i = 0; i < 256; i++)
            {
                Thing item = NewMaplessCargo("bulk_cargo");
                item.def = def;
                SpaceObject obj = universe.CapturePayload(item, new SpacePose(Vector3.forward, 0f), null);
                Check(obj?.Payload?.Entity == item && obj.map == null,
                    "each original stack can exist independently without a map slot");
                launched.Add(obj);
            }
            Check(Find.Maps.Count == maps, "256 simultaneous payloads preserve the map count");
            Check(launched.All(obj => obj.IsScattered && universe.Scatter.Candidates.Contains(obj)),
                "every simultaneous stack has a registered tractor identity");
            Find.Maps.Remove(capacityMap);
        }
        finally
        {
            foreach (SpaceObject obj in launched) universe.UnregisterObject(obj);
            DeltaVMod.CurrentSettings.loadedMapLimit = previousLimit;
        }
    }

    static void CheckPayloadSave(UniverseManager universe)
    {
        var def = TestBuildingDef("savedDriftingItem");
        def.category = ThingCategory.Item;
        def.thingClass = typeof(Thing);
        def.stackLimit = 75;
        def.useHitPoints = true;
        DefDatabase<ThingDef>.Add(def);
        var cargo = new Thing { def = def };
        cargo.stackCount = 17;
        Set(typeof(Thing), cargo, "thingIDNumber", 48199);
        Set(typeof(Thing), cargo, "hitPointsInt", 42);
        var saved = universe.CapturePayload(cargo, new SpacePose(Vector3.forward, .25f), null);
        var mod = (ModContentPack)Raw(typeof(ModContentPack));
        mod.assemblies = new ModAssemblyHandler(mod);
        mod.assemblies.loadedAssemblies.Add(typeof(AbstractThing).Assembly);
        LoadedModManager.RunningModsListForReading.Add(mod);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        try
        {
            Scribe.saver.InitSaving(path, "payload");
            Scribe_Deep.Look(ref saved, "object");
            Scribe.saver.FinalizeSaving();
            SpaceObject loaded = null;
            Scribe.loader.InitLoading(path);
            Scribe_Deep.Look(ref loaded, "object");
            Scribe.loader.FinalizeLoading();
            Check(loaded.objectId == saved.objectId && loaded.map == null && loaded.IsScattered,
                "real Scribe preserves the mapless object identity and role");
            Check(Math.Abs(loaded.pose.angle - saved.pose.angle) < 1e-6f,
                "real Scribe preserves the drifting pose");
            Check(loaded.Payload.Entity.def == def && loaded.Payload.Entity.stackCount == 17
                && loaded.Payload.Entity.HitPoints == 42, "real Scribe restores the actual item stack state");
            Check(loaded.Payload.Entity.holdingOwner == loaded.Payload.GetDirectlyHeldThings(),
                "real Scribe rebinds the item's owner");
        }
        finally
        {
            LoadedModManager.RunningModsListForReading.Remove(mod);
            saved.Payload.GetDirectlyHeldThings().Remove(cargo);
            universe.UnregisterObject(saved);
            File.Delete(path);
        }
    }
    static void CheckPlacementGeometry()
    {
        Map source = TestMap(48101), target = TestMap(48102);
        source.info.Size = new IntVec3(32, 1, 29);
        IntVec3 sourceRoot = new IntVec3(9, 0, 8);
        var pose = new SpacePose(Vector3.forward, 0.23f) { velocity = new Vector2(3, -2), angularVelocity = 0.04f };
        foreach (Rot4 rotation in Rot4.AllRotations)
        {
            var gravship = (Gravship)Activator.CreateInstance(typeof(Gravship), true);
            Set(gravship, "bounds", new CellRect(0, 0, 8, 5));
            var body = (Thing)Raw(typeof(Thing));
            body.def = TestBuildingDef("overhanging_body");
            body.def.size = new IntVec2(3, 5);
            body.def.rotatable = true;
            ((Dictionary<Thing, PositionData>)typeof(Gravship).GetField("things", Private).GetValue(gravship))
                .Add(body, new PositionData(new IntVec3(7, 0, 3), Rot4.North));
            gravship.Rotation = rotation;
            CellRect bounds = ShipPocketMapFactory.PlacedBounds(gravship);
            target.info.Size = ShipPocketMapFactory.CompactSize(bounds.Size);
            IntVec3 root = new IntVec3(12 - bounds.minX, 0, 12 - bounds.minZ);
            var placed = gravship.ThingPlacements[body];
            Check(GenAdj.OccupiedRect(root + placed.local, placed.rotation, body.def.size)
                .All(c => c.x >= 12 && c.z >= 12 && c.x < target.Size.x - 12 && c.z < target.Size.z - 12),
                "compact rotation includes the complete overhanging building and leaves twelve cells on every side");
            SpacePose moved = SpaceTransform.PoseAfterPlacement(pose, source, sourceRoot, target, root, rotation);
            Vector2 origin = SpaceTransform.ShipOriginFromPlacementRoot(source, sourceRoot, root, rotation);
            foreach (IntVec3 cell in new[] { sourceRoot, sourceRoot + new IntVec3(7, 0, 4) })
            {
                IntVec3 destination = root + PrefabUtility.GetAdjustedLocalPosition(cell - sourceRoot, rotation);
                Vector2 oldLocal = SpaceTransform.MapToShipLocal(source, new Vector2(cell.x + .5f, cell.z + .5f));
                Vector2 newLocal = SpaceTransform.MapToShipLocal(target, new Vector2(destination.x + .5f, destination.z + .5f));
                Vector3 before = SpaceTransform.ShipLocalToUniverse(pose, oldLocal);
                Vector3 after = SpaceTransform.ShipLocalToUniverse(moved, newLocal);
                Check(OrbitSphere.ArcCells(before, after) < .03f, "four-way map relocation preserves actual cell-center positions");
                Vector2 vBefore = SpaceTransform.VelocityAtLocalPoint(pose, oldLocal, before);
                Vector2 vAfter = SpaceTransform.VelocityAtLocalPoint(moved, newLocal, before);
                Check((vBefore - vAfter).magnitude < .01f && moved.angularVelocity == pose.angularVelocity,
                    "relocation preserves rigid-point velocity and spin");
                Vector2 expected = origin + SpaceTransform.RotatePlacementOffset(oldLocal, rotation);
                Check((expected - new Vector2(destination.x + .5f, destination.z + .5f)).sqrMagnitude < .00001f,
                    "registered landing origin agrees with vanilla integer placement at cell centers");
            }
        }
        Find.Maps.Clear();
    }

    static void CheckNativeSmallMapContracts()
    {
        var harmony = new Harmony("DeltaV.Regression.SingleCellMap");
        Type patch = typeof(UniverseManager).Assembly.GetType("DeltaV.Patch_SingleCellMapGeneration", true);
        Map previous = MapGenerator.mapBeingGenerated;
        var iterations = AccessTools.Field(typeof(Rand), "iterations");
        try
        {
            Check(harmony.CreateClassProcessor(patch).Patch().Count == 1,
                "single-cell compatibility postfix binds to the actual vanilla GasGrid constructor");
            foreach (IntVec2 size in new[] { new IntVec2(1, 1), new IntVec2(2, 1), new IntVec2(3, 2) })
            {
                var map = (Map)Raw(typeof(Map));
                map.info = new MapInfo { Size = new IntVec3(size.x, 1, size.z),
                    parent = (ShipPocketParent_DeltaV)Raw(typeof(ShipPocketParent_DeltaV)) };
                map.cellIndices = new CellIndices(map);
                foreach (bool generating in new[] { false, true })
                {
                    MapGenerator.mapBeingGenerated = generating ? map : null;
                    Rand.PushState(1268);
                    try
                    {
                        var gas = new GasGrid(map);
                        uint expected = map.Area == 1 && !generating ? 0u : 1u;
                        Check((uint)iterations.GetValue(null) == expected,
                            $"native {size.x}x{size.z} gas construction consumes {expected} RNG draws, generating={generating}");
                        gas.ClearCellUnsafe(IntVec3.Zero);
                        gas.RecalculateEverHadGas();
                        Check(!gas.CalculateGasEffects, "native gas grid can be reset on the exact map footprint");
                    }
                    finally { Rand.PopState(); }
                }
                CellRect rect = GenAdj.OccupiedRect(IntVec3.Zero, Rot4.North, size);
                IntVec3 anchor = new IntVec3(-rect.minX, 0, -rect.minZ);
                CellRect placed = GenAdj.OccupiedRect(anchor, Rot4.North, size);
                Check(placed.minX == 0 && placed.minZ == 0
                    && placed.maxX == size.x - 1 && placed.maxZ == size.z - 1,
                    $"native building anchor fills exact {size.x}x{size.z} footprint without clipping");
            }
        }
        finally
        {
            MapGenerator.mapBeingGenerated = previous;
            harmony.UnpatchAll(harmony.Id);
        }
    }
}
