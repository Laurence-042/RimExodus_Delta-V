using System;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static bool RegressionSubstructure(TerrainDef __instance, ref bool __result)
    { __result = __instance.isFoundation; return false; }
    static int separationTransfers, separationFailOn;
    static bool SeparationTransferFixture(Map sourceMap, SpaceObject sourceFrame,
        HashSet<IntVec3> cells, ref SpaceObject __result)
    {
        separationTransfers++;
        if (separationTransfers == separationFailOn) { __result = null; return false; }
        // Only replace Unity map generation/transfer; production grouping, gate and retries run.
        foreach (IntVec3 cell in cells)
        {
            sourceMap.terrainGrid.foundationGrid[sourceMap.cellIndices.CellToIndex(cell)] = null;
            Current.Game.GetComponent<UniverseManager>().NotifyFoundationChanged(sourceMap, cell);
        }
        SubstructureOwnership.BumpStructureVersion();
        __result = sourceFrame;
        return false;
    }
    static bool FailScatterFixture(ref bool __result) { __result = false; return false; }

    static int separationEntityReads, separationTerrainReads, separationFootprintReads, separationShipReads;
    static void CountSeparationEntities() => separationEntityReads++;
    static void CountSeparationTerrain() => separationTerrainReads++;
    static void CountSeparationFootprints() => separationFootprintReads++;
    static void CountSeparationShipReads() => separationShipReads++;

    static int driveAllocations, driveEntityLookups, driveCargoSamples;
    static void CountDriveAllocation() => driveAllocations++;
    static void CountDriveEntityLookup() => driveEntityLookups++;
    static void CountDriveCargoSample() => driveCargoSamples++;
    static bool ScalePocketTile(Map __instance, ref RimWorld.Planet.PlanetTile __result)
    {
        if (__instance.uniqueID != 49930) return true;
        __result = RimWorld.Planet.PlanetTile.Invalid;
        return false;
    }

    static void CheckSeparationScale(UniverseManager universe, Faction faction)
    {
        var harmony = new Harmony("DeltaV.Regression.SeparationScale");
        var space = (TerrainDef)Raw(typeof(TerrainDef)); space.defName = "scaleSpace";
        var deck = (TerrainDef)Raw(typeof(TerrainDef)); deck.defName = "scaleDeck"; deck.isFoundation = true;
        TerrainDef oldSpace = TerrainDefOf.Space;
        TerrainDefOf.Space = space;
        Map map = TestMap(49930, 250), neighbor = TestMap(49931);
        var frame = new MapSpaceObject(49930, map, new SpacePose(Vector3.forward, 0f));
        var otherFrame = new MapSpaceObject(49931, neighbor, frame.pose);
        try
        {
            void Patch(System.Reflection.MethodBase method, string prefix = null, System.Reflection.MethodInfo postfix = null) =>
                harmony.Patch(method, prefix == null ? null : new HarmonyMethod(typeof(Program), prefix),
                    postfix == null ? null : new HarmonyMethod(postfix));
            Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"), nameof(RegressionSubstructure));
            Patch(AccessTools.PropertyGetter(typeof(Map), nameof(Map.Tile)), nameof(ScalePocketTile));
            Patch(AccessTools.Method(typeof(ThingGrid), "RegisterInCell"),
                postfix: AccessTools.Method(typeof(Patch_SpaceScatterCargo_Register), "Postfix"));
            Patch(AccessTools.Method(typeof(ThingGrid), "DeregisterInCell"),
                postfix: AccessTools.Method(typeof(Patch_SpaceScatterCargo_Deregister), "Postfix"));
            universe.RegisterObject(frame);
            universe.RegisterObject(otherFrame);
            var main = AddTestConsole(map, faction, 49932);
            var neighborMain = AddTestConsole(neighbor, faction, 49933);
            int savedTick = Find.TickManager.TicksGame;
            void Prepare(Map target, int owner, int width, int height)
            {
                Array.Fill(target.terrainGrid.topGrid, space);
                var owners = new int[target.cellIndices.NumGridCells];
                Set(SubstructureOwnership.For(target), "ownerGrid", owners);
                for (int z = 0; z < height; z++)
                    for (int x = 0; x < width; x++)
                    {
                        int index = target.cellIndices.CellToIndex(new IntVec3(x, 0, z));
                        target.terrainGrid.foundationGrid[index] = deck;
                        owners[index] = owner;
                    }
                SubstructureOwnership.BumpStructureVersion(target);
            }
            Prepare(map, main.shipId, 100, 100);
            Prepare(neighbor, neighborMain.shipId, 4, 4);
            var fixture = map.listerThings.AllThings;
            var buildingDef = TestBuildingDef("scaleBuilding");
            var itemDef = TestBuildingDef("scaleItem"); itemDef.category = ThingCategory.Item;
            var pawnDef = TestBuildingDef("scalePawn"); pawnDef.category = ThingCategory.Pawn;
            Thing Add(Thing thing, ThingDef def, IntVec3 cell)
            {
                thing.def = def;
                Set(typeof(Thing), thing, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
                Set(typeof(Thing), thing, "positionInt", cell);
                fixture.Add(thing);
                map.thingGrid.Register(thing);
                if (thing is Building building)
                    SubstructureOwnership.TryGet(map)?.UpdateSpawnedBuilding(building);
                return thing;
            }
            for (int i = 0; i < 10000; i++)
            {
                Type type = i < 4000 ? typeof(Building) : i < 9000 ? typeof(Thing) : typeof(Pawn);
                Add((Thing)Raw(type), i < 4000 ? buildingDef : i < 9000 ? itemDef : pawnDef,
                    new IntVec3(i % 100, 0, i / 100));
            }
            var thrusterDef = TestBuildingDef("scaleThruster");
            thrusterDef.comps.Add(new CompProperties_GravshipThruster());
            var thruster = AddTestBuilding(map, faction, 49934, thrusterDef, new EmptyComp());
            var cabinet = AddTestBuilding(map, faction, 49935, TestBuildingDef("scaleCabinet"), new CompSpaceCargo(),
                new Building_SealedSpaceCargoContainer());
            var oldPower = ThrusterPower.Model;
            ThingDef oldEngine = ThingDefOf.GravEngine;
            var power = new DrivePowerProbe();
            Patch(AccessTools.PropertyGetter(typeof(ListerThings), "AllThings"), nameof(CountSeparationEntities));
            Patch(AccessTools.PropertyGetter(typeof(Map), "AllCells"), nameof(CountSeparationTerrain));
            Patch(AccessTools.Method(typeof(GenAdj), nameof(GenAdj.OccupiedRect), new[] { typeof(Thing) }),
                nameof(CountSeparationFootprints));
            Patch(AccessTools.Method(typeof(ShipRegistry), nameof(ShipRegistry.MainConsoleForOwner)),
                nameof(CountSeparationShipReads));
            bool ReadGate(CompDeltaVPilotConsole console) =>
                universe.ShipTransitions.RequestFoundationSeparation(universe, console.Ship, false);
            Check(ReadGate(main) && ReadGate(neighborMain), "both maps initialize their independent separation indexes");
            separationEntityReads = separationTerrainReads = separationFootprintReads = separationShipReads = 0;
            for (int i = 0; i < 1000; i++)
                if (!ReadGate(main)) throw new Exception("warm separation gate unexpectedly blocked thrust");
            Check(separationEntityReads == 0 && separationTerrainReads == 0
                && separationFootprintReads == 0 && separationShipReads == 0,
                "1,000 warm thrust gates on a 250x250 map with over 10,000 entities perform no scans or ownership lookups");

            try
            {
                ThingDefOf.GravEngine ??= TestBuildingDef("scaleEngine");
                ThrusterPower.Model = power;
                Patch(AccessTools.Method(typeof(ShipPhysics), nameof(ShipPhysics.Allocate)), nameof(CountDriveAllocation));
                Patch(AccessTools.Method(typeof(ListerThings), nameof(ListerThings.ThingsInGroup)), nameof(CountDriveEntityLookup));
                Patch(AccessTools.Method(typeof(ListerThings), nameof(ListerThings.ThingsOfDef)), nameof(CountDriveEntityLookup));
                Patch(AccessTools.PropertyGetter(typeof(SubstructureOwnership), "SpawnedBuildings"), nameof(CountDriveEntityLookup));
                Patch(AccessTools.Method(typeof(CompSpaceCargo), "MeasureStoredMass"), nameof(CountDriveCargoSample));
                var drive = DriveControl.FromInput(new ShipPhysics.Command { fz = 1f }, false, false, false);
                var subject = main.Vessel.Identity.InContainer(frame.objectId);
                ShipPhysics.MassReport report = ShipPhysics.GetReport(frame);
                Check(report.thrusters.Count == 1 && report.thrusters[0].thing == thruster,
                    "the scale fixture uses the installed engine and a real cargo cabinet in its mass report");
                separationEntityReads = separationTerrainReads = separationFootprintReads = separationShipReads = 0;
                driveAllocations = driveEntityLookups = driveCargoSamples = 0;
                for (int i = 1; i <= 1000; i++)
                {
                    Set(Find.TickManager, "ticksGameInt", savedTick + i);
                    if (frame.ApplyDrive(subject, drive, 1f / 60f, out _) != DriveResult.Executed)
                        throw new Exception("warm full drive unexpectedly blocked");
                }
                Check(frame.PropulsionUsedThisTick && frame.pose.velocity.sqrMagnitude > 0f
                    && driveAllocations == 1 && driveEntityLookups == 0 && driveCargoSamples == 0
                    && separationEntityReads == 0 && separationTerrainReads == 0 && separationFootprintReads == 0,
                    "1,000 actual drive ticks integrate thrust with one allocation and zero entity scans, footprint walks or inventory samples");
            }
            finally
            {
                Set(Find.TickManager, "ticksGameInt", savedTick);
                ThingDefOf.GravEngine = oldEngine;
                ThrusterPower.Model = oldPower;
                ShipPhysics.Forget(map);
            }

            var looseCell = new IntVec3(120, 0, 120);
            foreach (Thing cargo in new[] { fixture[5000], fixture[9500] })
            {
                IntVec3 origin = cargo.Position;
                map.thingGrid.Deregister(cargo);
                Set(typeof(Thing), cargo, "positionInt", looseCell);
                map.thingGrid.Register(cargo);
                Check(universe.Scatter.LooseCargoCount(map) == 1, "moving cargo off deck updates the loose count");
                map.thingGrid.Deregister(cargo);
                Set(typeof(Thing), cargo, "positionInt", origin);
                map.thingGrid.Register(cargo);
                Check(ReadGate(main), "moving cargo back aboard clears the gate");
            }
            Check(separationEntityReads == 0 && separationTerrainReads == 0 && separationFootprintReads == 0,
                "item and Pawn movement does not rescan the map or rebuild building ownership");

            var edge = new IntVec3(99, 0, 98);
            var foreign = new IntVec3(100, 0, 98);
            int foreignIndex = map.cellIndices.CellToIndex(foreign);
            map.terrainGrid.foundationGrid[foreignIndex] = deck;
            var owners = (int[])AccessTools.Field(typeof(SubstructureOwnership), "ownerGrid")
                .GetValue(SubstructureOwnership.For(map));
            owners[foreignIndex] = 999;
            universe.NotifyFoundationChanged(map, foreign);
            ShipStructureChanges.Notify(map, terrainCell: foreign);
            var longDef = TestBuildingDef("scaleRotatingBuilding");
            longDef.size = new IntVec2(1, 2);
            var spanning = (Building)Add((Building)Raw(typeof(Building)), longDef, edge);
            ShipStructureChanges.Notify(map, building: spanning);
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship) && ShipRegistry.HasResidualBlocks(main.Ship),
                "a building over one domain and a separate foreign domain can use ordinary fragment separation");
            map.thingGrid.Deregister(spanning);
            Set(typeof(Thing), spanning, "rotationInt", Rot4.East);
            map.thingGrid.Register(spanning);
            Check(ShipRegistry.RequiresOwnPocket(main.Ship) && universe.FoundationTopology.BuildingDomain(map, spanning) == null,
                "rotating a spawned building across two domains refreshes ownership and preserves the host");
            map.thingGrid.Deregister(spanning);
            fixture.Remove(spanning);
            Set(typeof(Thing), spanning, "mapIndexOrState", (sbyte)-1);
            ShipStructureChanges.Notify(map, building: spanning);
            map.terrainGrid.foundationGrid[foreignIndex] = null;
            universe.NotifyFoundationChanged(map, foreign);
            ShipStructureChanges.Notify(map, terrainCell: foreign);
            var rock = (Building)Add((Mineable)Raw(typeof(Mineable)), buildingDef, looseCell);
            ShipStructureChanges.Notify(map, building: rock);
            Check(ShipRegistry.RequiresOwnPocket(main.Ship)
                && universe.FoundationTopology.BuildingDomain(map, rock) == null
                && !universe.FoundationTopology.BuildingsIn(map, ShipRegistry.ControlDomain(main.Ship)).Contains(rock),
                "a domainless mineable requires extraction and is absent from the departing ship capture");
            Check(frame.contentTypeId == null && rock.Map == map && universe.FrameOf(map) == frame,
                "domainless rock stays hosted without an asteroid content type");
            map.thingGrid.Deregister(rock);
            fixture.Remove(rock);
            Set(typeof(Thing), rock, "mapIndexOrState", (sbyte)-1);
            ShipStructureChanges.Notify(map, building: rock);
            Check(ReadGate(main), "removing the last unbound building releases the gate");
            int changedFootprints = separationFootprintReads;
            Check(changedFootprints > 0 && ReadGate(neighborMain) && ReadGate(main)
                && separationFootprintReads == changedFootprints,
                "building changes rebuild ownership once on the affected map only");

            var split = new IntVec3(0, 0, 0);
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(split)] = null;
            AccessTools.Method(typeof(Patch_ShipStructure_Terrain), "Postfix").Invoke(null,
                new object[] { map.terrainGrid, split, space, deck });
            Check(ShipRegistry.RequiresOwnPocket(main.Ship) && ReadGate(neighborMain)
                && separationEntityReads == 0 && separationTerrainReads == 0,
                "losing support under a building refreshes its host decision without scanning all map entities or terrain");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            TerrainDefOf.Space = oldSpace;
            universe.Scatter.Forget(map);
            universe.ForgetFoundationTopology(map);
            universe.ForgetFoundationTopology(neighbor);
            universe.objects.Remove(frame);
            universe.objects.Remove(otherFrame);
            Set(universe, "objectById", null);
            Set(universe, "objectByMap", null);
            Actions(universe)?.Clear();
            universe.Lifecycle.Reset();
            Find.Maps.Remove(map);
            Find.Maps.Remove(neighbor);
        }
    }

    static bool RejectFixtureItem(Thing thing, ref bool __result)
    {
        if (thing is not ReceivingItem) return true;
        __result = false;
        return false;
    }

    static bool DestroyUnsupportedFixture(Map map)
    {
        Find.Maps.Remove(map);
        return false;
    }

    static void CheckUnsupportedMapScatter(UniverseManager universe, TerrainDef space)
    {
        var harmony = new Harmony("DeltaV.Regression.UnsupportedMapScatter");
        sbyte oldMapIndex = Current.Game.currentMapIndex;
        Current.Game.currentMapIndex = -1;
        Map map = TestMap(49920);
        Array.Fill(map.terrainGrid.topGrid, space);
        var frame = new MapSpaceObject(49920, map, new SpacePose(Vector3.forward, .25f)
            { velocity = new Vector2(3, -2), angularVelocity = .1f });
        universe.RegisterObject(frame);
        var pawn = (ReceivingPawn)Raw(typeof(ReceivingPawn));
        pawn.def = TestBuildingDef("unsupportedPawn");
        pawn.def.category = ThingCategory.Pawn;
        pawn.stackCount = 1;
        var item = (ReceivingItem)Raw(typeof(ReceivingItem));
        item.def = TestBuildingDef("unsupportedItem");
        item.def.category = ThingCategory.Item;
        item.stackCount = 1;
        var cargoCell = new IntVec3(3, 0, 1);
        SpacePose expected = SpaceTransform.RigidPointPose(frame.pose,
            SpaceTransform.MapToShipLocal(map, new Vector2(3.5f, 1.5f)), 0f);
        try
        {
            ProbeSpawnCargo(pawn, cargoCell, map);
            ProbeSpawnCargo(item, cargoCell, map);
            harmony.Patch(AccessTools.Method(typeof(ThingGrid), "DeregisterInCell"),
                postfix: new HarmonyMethod(typeof(Patch_SpaceScatterCargo_Deregister), "Postfix"));
            var scatter = AccessTools.Method(typeof(SpaceScatterService), "TryScatter", new[] { typeof(Thing) });
            harmony.Patch(scatter, prefix: new HarmonyMethod(typeof(Program), nameof(RejectFixtureItem)));
            Check(!universe.Lifecycle.RecycleUnsupportedMap(map) && item.Spawned && Find.Maps.Contains(map)
                && universe.ObjectById(frame.objectId) == frame,
                "an unsupported map survives when part of its cargo cannot be scattered");
            SpaceObject pawnPayload = universe.objects.Single(obj => obj.Payload?.Entity == pawn);
            Check(pawnPayload.map == null && pawnPayload.pose.Equals(expected),
                "stranded pawn becomes a mapless object at its old rigid point state");
            harmony.Unpatch(scatter, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Patch(AccessTools.Method(typeof(PocketMapUtility), nameof(PocketMapUtility.DestroyPocketMap)),
                prefix: new HarmonyMethod(typeof(Program), nameof(DestroyUnsupportedFixture)));
            Check(universe.Lifecycle.RecycleUnsupportedMap(map) && universe.ObjectById(frame.objectId) == null
                && !Find.Maps.Contains(map), "successful retry recycles the unsupported map");
            SpaceObject itemPayload = universe.objects.Single(obj => obj.Payload?.Entity == item);
            Check(itemPayload.map == null && itemPayload.pose.Equals(expected)
                && universe.objects.Single(obj => obj.Payload?.Entity == pawn) == pawnPayload,
                "retry captures the remaining item once and preserves the existing pawn payload");
        }
        finally
        {
            Current.Game.currentMapIndex = oldMapIndex;
            harmony.UnpatchAll(harmony.Id);
            foreach (SpaceObject obj in universe.objects.Where(obj =>
                obj == frame || obj.Payload?.Entity == pawn || obj.Payload?.Entity == item).ToList())
                universe.objects.Remove(obj);
            Set(universe, "objectById", null);
            Set(universe, "objectByMap", null);
            Find.Maps.Remove(map);
        }
    }

    static void CheckSeparationRules(UniverseManager universe, Faction faction)
    {
        ThingDef oldEngine = ThingDefOf.GravEngine;
        ThingDefOf.GravEngine ??= TestBuildingDef("separationEngine");
        var harmony = new Harmony("DeltaV.Regression.Separation");
        sbyte oldMapIndex = Current.Game.currentMapIndex;
        Current.Game.currentMapIndex = -1;
        Map map = TestMap(49900);
        var frame = new MapSpaceObject(49900, map, new SpacePose(Vector3.forward, .25f)
            { velocity = new Vector2(3, -2), angularVelocity = .1f });
        var metadata = new DeltaVPocketMapMetadata(map);
        map.components.Add(metadata);
        universe.objects.Add(frame);
        Set(universe, "objectById", null); // This fixture bypasses RegisterObject.
        var main = AddTestConsole(map, faction, 49901);
        var space = (TerrainDef)Raw(typeof(TerrainDef));
        space.defName = "separationSpace"; space.natural = true; space.exposesToVacuum = true;
        TerrainDef oldSpace = TerrainDefOf.Space;
        TerrainDefOf.Space = space;
        ThingDef oldPort = DeltaVDefOf.DeltaV_DockingPort;
        DeltaVDefOf.DeltaV_DockingPort = TestBuildingDef("separationPort");
        ThingDef oldLauncher = DeltaVDefOf.DeltaV_Weapon_MissileLauncher;
        DeltaVDefOf.DeltaV_Weapon_MissileLauncher = TestBuildingDef("separationLauncher");
        var deck = (TerrainDef)Raw(typeof(TerrainDef)); deck.isFoundation = true;
        var soil = (TerrainDef)Raw(typeof(TerrainDef)); soil.natural = true;
        var floor = (TerrainDef)Raw(typeof(TerrainDef)); floor.natural = false;
        for (int i = 0; i < map.terrainGrid.topGrid.Length; i++) map.terrainGrid.topGrid[i] = space;
        int[] owners = new int[map.cellIndices.NumGridCells];
        Set(SubstructureOwnership.For(map), "ownerGrid", owners);
        void Foundation(IntVec3 c, int owner)
        {
            int i = map.cellIndices.CellToIndex(c);
            map.terrainGrid.foundationGrid[i] = deck; owners[i] = owner;
            universe.NotifyFoundationChanged(map, c);
            SubstructureOwnership.BumpStructureVersion();
        }
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
                prefix: new HarmonyMethod(typeof(Program), nameof(RegressionSubstructure)));
            Check(harmony.Patch(AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
                    postfix: new HarmonyMethod(typeof(Patch_ShipStructure_Terrain), "Postfix")) != null
                && harmony.Patch(AccessTools.Method(typeof(TerrainGrid), nameof(TerrainGrid.SetUnderTerrain)),
                    postfix: new HarmonyMethod(typeof(Patch_ShipStructure_UnderTerrain), "Postfix")) != null
                && harmony.Patch(AccessTools.Method(typeof(TerrainGrid), "RemoveGravshipTerrainUnsafe"),
                    postfix: new HarmonyMethod(typeof(Patch_TerrainGrid_RemoveGravshipTerrainUnsafe_Ownership), "Postfix")) != null,
                "foundation and support event postfixes bind to installed RimWorld signatures");
            Foundation(main.parent.Position, main.shipId);
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "single clean ship stays in its current container");
            Check(!ShipRegistry.HasResidualBlocks(main.Ship), "initial topology matches the clean main-console domain");
            IntVec3 adjacent = main.parent.Position + new IntVec3(1, 0, 0);
            Foundation(adjacent, main.shipId);
            Check(!ShipRegistry.HasResidualBlocks(main.Ship), "connected same-owner addition remains clean");
            SubstructureOwnership.For(map).Assign(adjacent, 999);
            Check(ShipRegistry.HasResidualBlocks(main.Ship)
                && !ShipRegistry.ControlDomain(main.Ship).Contains(adjacent),
                "authoritative Assign invalidates topology; touching foreign cells stay outside the control domain");
            SubstructureOwnership.For(map).ClearOwner(999);
            Check(ShipRegistry.HasResidualBlocks(main.Ship), "ClearOwner preserves unassigned foundation as residual");
            SubstructureOwnership.For(map).Assign(adjacent, main.shipId);
            Check(!ShipRegistry.HasResidualBlocks(main.Ship), "reassignment restores a clean domain");
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(adjacent)] = null;
            SubstructureOwnership.For(map).NotifyFoundationRemoved(adjacent);
            Check(!ShipRegistry.HasResidualBlocks(main.Ship)
                && SubstructureOwnership.For(map).OwnerAt(adjacent) == 0,
                "foundation removal synchronizes topology and persistent owner without splitting");
            // Exercise actual postfix bodies after the fixture writes the same final state as TerrainGrid.
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(adjacent)] = deck;
            AccessTools.Method(typeof(Patch_ShipStructure_Terrain), "Postfix").Invoke(null,
                new object[] { map.terrainGrid, adjacent, deck, null });
            Check(ShipRegistry.HasResidualBlocks(main.Ship),
                "terrain callback catches new unassigned foundation without an owner write");
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(adjacent)] = null;
            Patch_TerrainGrid_RemoveGravshipTerrainUnsafe_Ownership.Postfix(map.terrainGrid, adjacent);
            Check(!ShipRegistry.HasResidualBlocks(main.Ship),
                "unsafe transfer removal callback clears unassigned foundation from topology");
            Foundation(IntVec3.Zero, 0);
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "unassigned detached foundation does not force ship extraction");
            owners[0] = 999;
            universe.NotifyFoundationChanged(map, IntVec3.Zero);
            SubstructureOwnership.BumpStructureVersion();
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "foreign wreck ownership does not force extraction either");
            metadata.worksite = true;
            Check(ShipRegistry.RequiresOwnPocket(main.Ship), "worksite always extracts the ship to a compact map");
            metadata.worksite = false;
            int far = map.cellIndices.CellToIndex(new IntVec3(4, 0, 4));
            map.terrainGrid.topGrid[far] = soil;
            ShipStructureChanges.Notify(map, terrainCell: map.cellIndices.IndexToCell(far));
            Check(ShipRegistry.RequiresOwnPocket(main.Ship), "natural ground beside the ship preserves the host");
            map.terrainGrid.topGrid[far] = space;
            Check(ShipRegistry.RequiresOwnPocket(main.Ship), "natural-ground reads reuse the indexed cell count");
            ShipStructureChanges.Notify(map, terrainCell: map.cellIndices.IndexToCell(far));
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "terrain structure invalidation refreshes the host decision");
            map.terrainGrid.topGrid[far] = floor;
            ShipStructureChanges.Notify(map, terrainCell: map.cellIndices.IndexToCell(far));
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "artificial flooring is not asteroid ground");
            map.terrainGrid.topGrid[far] = space;
            var other = AddTestConsole(map, faction, 49902);
            ShipStructureChanges.Notify(map, building: (Building)other.parent);
            Check(ShipRegistry.RequiresOwnPocket(main.Ship), "other valid ship requires extraction even without thrusters");
            other.isMain = false;
            SubstructureOwnership.BumpStructureVersion();
            var shuttle = AddTestHull(map, faction, 49903);
            ShipStructureChanges.Notify(map, building: (Building)shuttle.parent);
            Check(!shuttle.Vessel.CanDriveIn(frame) && !ShipRegistry.RequiresOwnPocket(main.Ship),
                "carried shuttle and backup console do not become independent host ships");

            Foundation(new IntVec3(4, 0, 4), main.shipId);
            harmony.Patch(AccessTools.Method(typeof(ShipFlightTransitionService), "TransferDomainToNewPocket"),
                prefix: new HarmonyMethod(typeof(Program), nameof(SeparationTransferFixture)));
            separationTransfers = 0; separationFailOn = 2;
            SpacePose before = frame.pose;
            Check(!universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "residuals stop this tick's thrust and queue one separation");
            Check(!universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false)
                && universe.ShipTransitions.pendingSeparationOwnerIds.Count == 1,
                "repeated requests cannot duplicate a pending separation");
            Call(universe, "RunFrameActions");
            Check(separationTransfers == 2 && ShipRegistry.ResidualBlocks(map, ShipRegistry.ControlDomain(main.Ship)).Count == 1,
                "failed second transfer leaves only the unresolved block in the source");
            separationFailOn = 0;
            Check(!universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "unresolved block continues to block thrust");
            Call(universe, "RunFrameActions");
            Check(separationTransfers == 3 && universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "retry moves only the remaining block then releases the thrust gate");
            Check(main.parent.Map == map && universe.FrameOf(map) == frame
                && frame.pose.position.Equals(before.position) && frame.pose.velocity == before.velocity
                && frame.pose.angle == before.angle && frame.pose.angularVelocity == before.angularVelocity,
                "residual separation preserves the main ship Map, container and absolute motion");

            var cargo = (Thing)Raw(typeof(Thing));
            cargo.def = TestBuildingDef("looseCargo"); cargo.def.category = ThingCategory.Item;
            Set(typeof(Thing), cargo, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
            Set(typeof(Thing), cargo, "positionInt", IntVec3.Zero);
            map.listerThings.AllThings.Add(cargo);
            harmony.Patch(AccessTools.Method(typeof(ThingGrid), "RegisterInCell"),
                postfix: new HarmonyMethod(typeof(Patch_SpaceScatterCargo_Register), "Postfix"));
            harmony.Patch(AccessTools.Method(typeof(ThingGrid), "DeregisterInCell"),
                postfix: new HarmonyMethod(typeof(Patch_SpaceScatterCargo_Deregister), "Postfix"));
            map.thingGrid.Register(cargo);
            harmony.Patch(AccessTools.Method(typeof(SpaceScatterService), "TryScatter", new[] { typeof(Thing) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(FailScatterFixture)));
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship)
                && !universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "loose cargo without fragments uses scatter, not extraction or immediate thrust");
            Call(universe, "RunFrameActions");
            Check(!universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "failed scatter keeps loose cargo from moving with the ship");
            universe.ShipTransitions.pendingSeparationOwnerIds.Remove(main.shipId);
            Actions(universe).Clear();
            Check(universe.Scatter.LooseCargo(map).Contains(cargo),
                "cargo registration adds an unsupported item to the per-map index");
            map.terrainGrid.topGrid[0] = soil;
            universe.NotifySupportChanged(map, IntVec3.Zero);
            Check(!universe.Scatter.LooseCargo(map).Any(),
                "support change removes the indexed loose cargo without a map scan");
            map.terrainGrid.topGrid[0] = space;
            universe.NotifySupportChanged(map, IntVec3.Zero);
            Check(universe.Scatter.LooseCargo(map).Contains(cargo),
                "losing support restores the loose cargo entry");
            map.thingGrid.Deregister(cargo);
            map.listerThings.AllThings.Remove(cargo);
            Check(!universe.Scatter.LooseCargo(map).Any(),
                "cargo deregistration clears the per-map index");

            Foundation(new IntVec3(4, 0, 4), 999);
            Check(!universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "foundation change invalidates the previously clean split conclusion");
            Foundation(new IntVec3(4, 0, 4), 1000);
            Call(universe, "RunFrameActions");
            Check(separationTransfers == 3, "stale thrust separation does not transfer a reassigned domain");
            Check(!universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "the next thrust request resolves the current domain");
            Call(universe, "RunFrameActions");
            Check(separationTransfers == 4 && universe.ShipTransitions.RequestFoundationSeparation(universe, main.Ship, false),
                "new residual is transferred once and the clean result is reused");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            ThingDefOf.GravEngine = oldEngine;
            Current.Game.currentMapIndex = oldMapIndex;
            TerrainDefOf.Space = oldSpace; DeltaVDefOf.DeltaV_DockingPort = oldPort;
            DeltaVDefOf.DeltaV_Weapon_MissileLauncher = oldLauncher;
            Actions(universe)?.Clear(); universe.ShipTransitions.pendingSeparationOwnerIds.Remove(main.shipId);
            universe.objects.Remove(frame); Set(universe, "objectById", null);
            Find.Maps.Remove(map); ShipRegistry.ClearCaches();
            universe.ForgetFoundationTopology(map);
        }
        try
        {
            TerrainDefOf.Space = space;
            CheckUnsupportedMapScatter(universe, space);
            CheckSeparationScale(universe, faction);
        }
        finally { TerrainDefOf.Space = oldSpace; }
    }
}
