using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
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
        using (FixtureUniverse.ShipStructure.BeginChanges())
            foreach (IntVec3 cell in cells) SetFixtureFoundation(sourceMap, cell, null, 0);
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
    static void InitializeScalePocketTile(Map map)
    {
        // TestMap allocates the parent without running its constructor. Initialize the one
        // instance field directly, without activating WorldObject's Unity static initializer.
        var field = AccessTools.Field(typeof(RimWorld.Planet.WorldObject), "tile");
        var write = new DynamicMethod("InitializeScalePocketTile", typeof(void),
            new[] { field.DeclaringType, field.FieldType }, typeof(Program), true);
        var il = write.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        write.CreateDelegate<Action<RimWorld.Planet.WorldObject, RimWorld.Planet.PlanetTile>>()(
            map.info.parent, RimWorld.Planet.PlanetTile.Invalid);
    }

    static void CheckSeparationScale(UniverseManager universe, Faction faction)
    {
        var harmony = new Harmony("DeltaV.Regression.SeparationScale");
        var space = (TerrainDef)Raw(typeof(TerrainDef)); space.defName = "scaleSpace";
        var deck = (TerrainDef)Raw(typeof(TerrainDef)); deck.defName = "scaleDeck"; deck.isFoundation = true;
        TerrainDef oldSpace = TerrainDefOf.Space;
        ThingDef oldLauncher = DeltaVDefOf.DeltaV_Weapon_MissileLauncher;
        DeltaVDefOf.DeltaV_Weapon_MissileLauncher ??= TestBuildingDef("scaleLauncher");
        TerrainDefOf.Space = space;
        Map map = TestMap(49930, 250), neighbor = TestMap(49931);
        InitializeScalePocketTile(map);
        var frame = new MapSpaceObject(49930, map, new SpacePose(Vector3.forward, 0f));
        var otherFrame = new MapSpaceObject(49931, neighbor, frame.pose);
        try
        {
            void Patch(System.Reflection.MethodBase method, string prefix = null, System.Reflection.MethodInfo postfix = null) =>
                harmony.Patch(method, prefix == null ? null : new HarmonyMethod(typeof(Program), prefix),
                    postfix == null ? null : new HarmonyMethod(postfix));
            Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"), nameof(RegressionSubstructure));
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
                using var changes = universe.ShipStructure.BeginChanges();
                for (int z = 0; z < height; z++)
                    for (int x = 0; x < width; x++)
                    {
                        var cell = new IntVec3(x, 0, z);
                        int index = target.cellIndices.CellToIndex(cell);
                        target.terrainGrid.foundationGrid[index] = deck;
                        owners[index] = owner;
                        universe.NotifyFoundationChanged(target, cell);
                    }
                Current.Game.GetComponent<UniverseManager>().ShipStructure.Changed(target);
            }
            Prepare(map, main.shipId, 100, 100);
            Prepare(neighbor, neighborMain.shipId, 4, 4);
            var fixture = map.listerThings.AllThings;
            var buildingDef = TestBuildingDef("scaleBuilding");
            var itemDef = TestBuildingDef("scaleItem"); itemDef.category = ThingCategory.Item;
            var pawnDef = TestBuildingDef("scalePawn"); pawnDef.category = ThingCategory.Pawn;
            int nextThingId = 60000;
            Thing Add(Thing thing, ThingDef def, IntVec3 cell)
            {
                thing.def = def;
                thing.thingIDNumber = nextThingId++;
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
            var thruster = AddTestBuilding(map, faction, 49934, thrusterDef, new CompThrusterPowerLimit());
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
                Patch(AccessTools.Method(typeof(CompStructureMass), "Initialize"), nameof(CountDriveCargoSample));
                Patch(AccessTools.Method(typeof(ShipPhysics), nameof(ShipPhysics.BuildInstalledThruster)), nameof(CountInstalledThrusterBuild));
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
                CheckInstalledLayoutChange(frame, report, thruster);
                separationEntityReads = separationTerrainReads = separationFootprintReads = 0;
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
            (Rot4 rotation, CellRect? footprint) previous = (spanning.Rotation, spanning.OccupiedRect());
            map.thingGrid.Deregister(spanning);
            Set(typeof(Thing), spanning, "rotationInt", Rot4.East);
            map.thingGrid.Register(spanning);
            Patch_ShipStructure_Rotation.Postfix(spanning, previous);
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
            Check(rock.Map == map && universe.FrameOf(map) == frame,
                "domainless rock stays hosted without a generation classification");
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
            Patch_TerrainGrid_RemoveGravshipTerrainUnsafe_Ownership.Postfix(map.terrainGrid, split, (deck, main.shipId));
            Check(ShipRegistry.RequiresOwnPocket(main.Ship) && ReadGate(neighborMain)
                && separationEntityReads == 0 && separationTerrainReads == 0,
                "losing support under a building refreshes its host decision without scanning all map entities or terrain");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            TerrainDefOf.Space = oldSpace;
            DeltaVDefOf.DeltaV_Weapon_MissileLauncher = oldLauncher;
            universe.Scatter.Forget(map);
            universe.ForgetFoundationTopology(map);
            universe.ForgetFoundationTopology(neighbor);
            universe.objects.Remove(frame);
            universe.objects.Remove(otherFrame);
            Set(universe, "objectById", null);
            Set(universe, "objectByMap", null);
            Actions(universe)?.Clear();
            universe.MapStates.Reset();
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
        pawn.thingIDNumber = 49921;
        var item = (ReceivingItem)Raw(typeof(ReceivingItem));
        item.def = TestBuildingDef("unsupportedItem");
        item.def.category = ThingCategory.Item;
        item.stackCount = 1;
        item.thingIDNumber = 49922;
        var cargoCell = new IntVec3(3, 0, 1);
        SpacePose expected = SpaceTransform.RigidPointPose(frame.pose,
            SpaceTransform.MapToShipLocal(map, new Vector2(3.5f, 1.5f)), 0f);
        try
        {
            harmony.Patch(AccessTools.Method(typeof(ThingGrid), "RegisterInCell"),
                postfix: new HarmonyMethod(typeof(Patch_SpaceScatterCargo_Register), "Postfix"));
            ProbeSpawnCargo(pawn, cargoCell, map);
            ProbeSpawnCargo(item, cargoCell, map);
            harmony.Patch(AccessTools.Method(typeof(ThingGrid), "DeregisterInCell"),
                postfix: new HarmonyMethod(typeof(Patch_SpaceScatterCargo_Deregister), "Postfix"));
            var scatter = AccessTools.Method(typeof(SpaceScatterService), "TryScatter", new[] { typeof(Thing) });
            harmony.Patch(scatter, prefix: new HarmonyMethod(typeof(Program), nameof(RejectFixtureItem)));
            Check(!universe.MapStates.RecycleUnsupportedMap(map) && item.Spawned && Find.Maps.Contains(map)
                && universe.ObjectById(frame.objectId) == frame,
                "an unsupported map survives when part of its cargo cannot be scattered");
            SpaceObject pawnPayload = universe.objects.Single(obj => obj.Payload?.Entity == pawn);
            Check(pawnPayload.map == null && pawnPayload.pose.Equals(expected),
                "stranded pawn becomes a mapless object at its old rigid point state");
            harmony.Unpatch(scatter, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Patch(AccessTools.Method(typeof(PocketMapUtility), nameof(PocketMapUtility.DestroyPocketMap)),
                prefix: new HarmonyMethod(typeof(Program), nameof(DestroyUnsupportedFixture)));
            Check(universe.MapStates.RecycleUnsupportedMap(map) && universe.ObjectById(frame.objectId) == null
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
        void Foundation(IntVec3 cell, int owner) => SetFixtureFoundation(map, cell, deck, owner);
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
                prefix: new HarmonyMethod(typeof(Program), nameof(RegressionSubstructure)));
            Check(harmony.Patch(AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
                    postfix: new HarmonyMethod(typeof(Patch_ShipStructure_Terrain), "Postfix")) != null
                && harmony.Patch(AccessTools.Method(typeof(TerrainGrid), nameof(TerrainGrid.SetUnderTerrain)),
                    prefix: new HarmonyMethod(typeof(Patch_ShipStructure_UnderTerrain), "Prefix"),
                    postfix: new HarmonyMethod(typeof(Patch_ShipStructure_UnderTerrain), "Postfix")) != null
                && harmony.Patch(AccessTools.Method(typeof(TerrainGrid), "RemoveGravshipTerrainUnsafe"),
                    prefix: new HarmonyMethod(typeof(Patch_TerrainGrid_RemoveGravshipTerrainUnsafe_Ownership), "Prefix"),
                    postfix: new HarmonyMethod(typeof(Patch_TerrainGrid_RemoveGravshipTerrainUnsafe_Ownership), "Postfix")) != null,
                "foundation and support event postfixes bind to installed RimWorld signatures");
            Foundation(main.parent.Position, main.shipId);
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "single clean ship stays in its current container");
            Check(!ShipRegistry.HasResidualBlocks(main.Ship), "initial topology matches the clean main-console domain");
            var scoped = new ShipFlightTransitionService.TransitionRevision(universe, main.Vessel);
            var residual = new ShipFlightTransitionService.TransitionRevision(universe, main.Vessel, movesResidual: true);
            var foreignCell = new IntVec3(4, 0, 4);
            SetFixtureFoundation(map, foreignCell, deck, 0);
            Check(scoped.Matches(universe, main.Vessel) && !residual.Matches(universe, main.Vessel),
                "another domain does not revoke ship extraction but revokes a plan that moves residual domains");
            SetFixtureFoundation(map, foreignCell, null, 0);
            var unrelated = new ShipFlightTransitionService.TransitionRevision(universe, main.Vessel);
            universe.ShipStructure.NotifyCell(map, main.parent.Position, ShipStructureAspect.Terrain, space, floor);
            Check(unrelated.Matches(universe, main.Vessel), "display-only terrain does not revoke a validated transfer");

            IntVec3 adjacent = main.parent.Position + new IntVec3(1, 0, 0);
            Foundation(adjacent, main.shipId);
            Check(!unrelated.Matches(universe, main.Vessel), "the same domain object gets a new revision when a cell joins");
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
            AccessTools.Method(typeof(Patch_TerrainGrid_SetFoundation_Ownership), "Postfix").Invoke(null,
                new object[] { map.terrainGrid, adjacent, deck, null });
            Check(ShipRegistry.ControlDomain(main.Ship).Contains(adjacent),
                "foundation callback registers the new foundation and resolves its neighboring owner");
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(adjacent)] = null;
            Patch_TerrainGrid_RemoveGravshipTerrainUnsafe_Ownership.Postfix(map.terrainGrid, adjacent, (deck, main.shipId));
            Check(!ShipRegistry.HasResidualBlocks(main.Ship),
                "unsafe transfer removal callback clears unassigned foundation from topology");
            Foundation(IntVec3.Zero, 0);
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "unassigned detached foundation does not force ship extraction");
            SubstructureOwnership.For(map).Assign(IntVec3.Zero, 999);
            Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll();
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "foreign wreck ownership does not force extraction either");
            metadata.worksite = true;
            Check(ShipRegistry.RequiresOwnPocket(main.Ship), "worksite always extracts the ship to a compact map");
            metadata.worksite = false;
            int far = map.cellIndices.CellToIndex(new IntVec3(4, 0, 4));
            map.terrainGrid.topGrid[far] = soil;
            AccessTools.Method(typeof(Patch_ShipStructure_Terrain), "Postfix").Invoke(null,
                new object[] { map.terrainGrid, map.cellIndices.IndexToCell(far), soil, space, null });
            Check(ShipRegistry.RequiresOwnPocket(main.Ship), "natural ground beside the ship preserves the host");
            map.terrainGrid.topGrid[far] = space;
            Check(ShipRegistry.RequiresOwnPocket(main.Ship), "natural-ground reads reuse the indexed cell count");
            AccessTools.Method(typeof(Patch_ShipStructure_Terrain), "Postfix").Invoke(null,
                new object[] { map.terrainGrid, map.cellIndices.IndexToCell(far), space, soil, null });
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "terrain structure invalidation refreshes the host decision");
            map.terrainGrid.topGrid[far] = floor;
            AccessTools.Method(typeof(Patch_ShipStructure_Terrain), "Postfix").Invoke(null,
                new object[] { map.terrainGrid, map.cellIndices.IndexToCell(far), floor, space, null });
            Check(!ShipRegistry.RequiresOwnPocket(main.Ship), "artificial flooring is not asteroid ground");
            map.terrainGrid.topGrid[far] = space;
            var other = AddTestConsole(map, faction, 49902);
            ShipStructureChanges.Notify(map, building: (Building)other.parent);
            Check(ShipRegistry.RequiresOwnPocket(main.Ship), "other valid ship requires extraction even without thrusters");
            other.isMain = false;
            universe.ShipStructure.NotifyConsole(other);
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
