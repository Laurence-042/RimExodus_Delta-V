using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using DeltaV;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

internal static partial class Program
{
    sealed class SoftDockThrustProbe : SpaceObject
    {
        private readonly ShipPhysics.MassReport report = new ShipPhysics.MassReport { mass = 100f, inertia = 100f };
        internal SoftDockThrustProbe(int id) : base(id, new SpacePose(Vector3.forward, 0f))
        {
            report.thrusters.Add(new ShipPhysics.Thruster
            { dir = Vector2.right, thrust = 2000f, operational = true });
        }
        protected override ShipPhysics.MassReport MotionReport => report;
        protected override void AllocateDrive(ShipPhysics.MassReport layout, ShipPhysics.Command command,
            out Vector2 force, out float torque)
        {
            layout.thrusters[0].throttle = Mathf.Clamp01(command.fx);
            force = layout.thrusters[0].dir * (layout.thrusters[0].thrust * layout.thrusters[0].throttle);
            torque = 0f;
        }
        internal void Drive(Vector2 force) => ExecuteDrive(report, 0f, new DriveSubject(objectId, 1),
            new DriveControl { force = force }, 1f / 60f, out _);
    }

    static void CheckDockingSystem(UniverseManager universe, bool dockingOnly = false)
    {
        var dock = new CompSoftDocking();
        Set(dock, "active", true);
        for (int i = 0; i < 179; i++) dock.Advance(true);
        Check(!dock.Connected, "soft docking cannot transmit before extension completes");
        for (int i = 0; i < 3; i++) dock.Advance(true);
        Check(dock.Connected && dock.Progress == 1f, "aligned soft docking completes and clamps extension");
        for (int i = 0; i < 20; i++) dock.Advance(false);
        Check(dock.Connected, "brief alignment loss preserves an established portal above release threshold");
        for (int i = 0; i < 4; i++) dock.Advance(false);
        Check(!dock.Connected && dock.Progress > 0f, "portal disconnects before the visual passage fully retracts");
        dock.Advance(true);
        Check(!dock.Connected, "recovery above release threshold does not prematurely reopen an incomplete portal");
        for (int i = 0; i < 200; i++) dock.Advance(false);
        Check(dock.Progress == 0f, "retraction never produces negative passage length");
        var service = new SoftDockService(universe);
        universe.SoftDockEnabled = true;
        service.SetEnabled(false);
        Check(!service.Enabled, "toggle off immediately invalidates soft portals without waiting for a tick");

        var thrusting = new SoftDockThrustProbe(49698);
        thrusting.Drive(new Vector2(1000f, 0f));
        var coasting = new SoftDockThrustProbe(49699);
        coasting.Drive(Vector2.zero);
        Check(thrusting.PropulsionUsedThisTick && !coasting.PropulsionUsedThisTick
            && !CompSoftDocking.CanAssist(thrusting, coasting)
            && !CompSoftDocking.CanAssist(coasting, thrusting)
            && CompSoftDocking.CanAssist(coasting, new SpaceObject(49697, coasting.pose)),
            "soft docking disables interpolation when either ship actually uses an engine");

        var body = new SpaceObject(49700, new SpacePose(Vector3.forward, .3f)
            { velocity = new Vector2(2f, -1f), angularVelocity = .03f });
        SpacePose original = body.pose;
        CheckSmallScaleMovement();
        CheckRigidMotion();
        CheckSoftDockInterpolation();
        CheckDockingCompState(universe);
        CheckStrategicGravshipAdapter(universe);

        var cargo = NewMaplessCargo("scattered_cargo");
        var scattered = universe.CapturePayload(cargo, original, null);
        Check(!scattered.CollisionCells.Any(), "scattered cargo has no collision surface");
        Check(universe.Scatter.Candidates.Contains(scattered), "scattered registration populates the tractor candidate index");
        Check(CombatTarget.ForContainer(scattered).TryResolve(universe, out var resolved) && resolved.Container == scattered,
            "tractor locks resolve scattered object identity through existing fire control");
        CheckScatteredSweep(universe, scattered);
        universe.UnregisterObject(scattered);
        Check(!universe.Scatter.Candidates.Contains(scattered), "scattered retirement releases the tractor candidate index");
        Check(!CombatTarget.ForContainer(scattered).TryResolve(universe, out _), "retired scattered identity cannot resolve a stale tractor lock");
        CheckSoftDockReference(universe);
        CheckStrategicLandingWorksite(universe);
        CheckOrbitalWork(universe);
        if (dockingOnly) return;
        CheckScatteredRetirement(universe);
        CheckFocusedScatterAndMapSetter(universe);
        CheckTractorReceive(universe);
        CheckUndockHeading(universe);
    }

    static void CheckStrategicGravshipAdapter(UniverseManager universe)
    {
        var worldtile = TestMap(49806);
        var pocket = TestMap(49807);
        var parent = (Settlement)Raw(typeof(Settlement));
        AccessTools.PropertySetter(typeof(WorldObject), nameof(WorldObject.Tile))
            .Invoke(parent, new object[] { new PlanetTile(0, 0) });
        worldtile.info.parent = parent;
        pocket.info.parent = null;
        try
        {
            Check(ShipFlightTransitionService.StrategicDepartureWorldtileSource(worldtile) == worldtile
                && ShipFlightTransitionService.StrategicDepartureWorldtileSource(pocket) == null
                && universe.ShipTransitions.StrategicSourceAddress(worldtile) == worldtile.Tile,
                "strategic departure selects only the actual worldtile source, never a same-sector POI for a pocket source");

            MethodInfo picker = AccessTools.Method(typeof(CompPilotConsole),
                nameof(CompPilotConsole.StartChoosingDestination_NewTemp));
            MethodInfo mapTile = AccessTools.PropertyGetter(typeof(Map), nameof(Map.Tile));
            MethodInfo strategicTile = AccessTools.Method(typeof(ShipFlightTransitionService),
                nameof(ShipFlightTransitionService.StrategicSelectionTile));
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(picker).ToList();
            bool Calls(CodeInstruction instruction, MethodInfo method) =>
                (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && Equals(instruction.operand, method);
            int originalTileCalls = original.Count(instruction => Calls(instruction, mapTile));
            List<CodeInstruction> patched =
                Patch_CompPilotConsole_StartChoosingDestination_WorldJump.Transpiler(original, picker).ToList();
            Check(originalTileCalls == 2
                && patched.Count(instruction => Calls(instruction, mapTile)) == 0
                && patched.Count(instruction => Calls(instruction, strategicTile)) == 2,
                "strategic destination picker rewrites exactly its two captured Map.Tile reads through the central source adapter");

            universe.ShipTransitions.CompleteStrategicLanding(null);
            universe.ShipTransitions.CompleteStrategicLanding(null);
            Check(!universe.ShipTransitions.TryGetLandingCaptureScope(null, out _),
                "landing completion is idempotent when no capture or destination map exists");
        }
        finally
        {
            Find.Maps.Remove(worldtile);
            Find.Maps.Remove(pocket);
        }
    }

    sealed class OrbitalWorkTestShip : FoundationShip
    {
        private readonly Map map;
        internal OrbitalWorkTestShip(Map map) : base(Ship.Invalid) { this.map = map; }
        public override bool IsValid => true;
        public override Map Map => map;
        public override DriveSubject Identity => new DriveSubject(0, map.uniqueID);
        public override ShipDerivedState State => ShipDerivedState.FreeFlight;
        public override ShipTransferSpec Transfer => new ShipTransferSpec(null, null, map.uniqueID, false);
    }

    static string worksiteRejection;
    static bool worksiteLandingValid;
    static Map worksiteDockMap;
    static IntVec3 worksiteRequestedSize;
    static TerrainDef worksiteFoundation;

    static bool RecordWorksiteRejection(string key) { worksiteRejection = key; return false; }
    static bool PlaceWorksiteProbe(PilotTarget target, SpaceObject host,
        ref LandingApproachController.DockingPlan __result)
    {
        __result = new LandingApproachController.DockingPlan(target, host, IntVec3.Zero, IntVec3.Zero, Rot4.North);
        return false;
    }
    static bool ValidateWorksiteProbe(ref bool __result) { __result = worksiteLandingValid; return false; }
    static bool RecordWorksiteDock(LandingApproachController.DockingPlan placement)
    { worksiteDockMap = placement.targetMap; return false; }
    static bool RecordWorksiteGeneration(IntVec3 size, ref Map __result)
    { worksiteRequestedSize = size; __result = null; return false; }
    static bool ReadWorksiteSubstructure(TerrainDef __instance, ref bool __result)
    {
        if (__instance != worksiteFoundation) return true;
        __result = true;
        return false;
    }
    static bool SetWorksiteRoof(RoofGrid __instance, IntVec3 c, RoofDef def)
    {
        Map map = (Map)AccessTools.Field(typeof(RoofGrid), "map").GetValue(__instance);
        var roofs = (RoofDef[])AccessTools.Field(typeof(RoofGrid), "roofGrid").GetValue(__instance);
        roofs[map.cellIndices.CellToIndex(c)] = def;
        return false;
    }

    static void CheckStrategicLandingWorksite(UniverseManager universe)
    {
        var unsafeRoofPatch = new Harmony("DeltaV.Regression.UnsafeRoofStructure");
        unsafeRoofPatch.CreateClassProcessor(typeof(Patch_RoofGrid_RemoveRoofUnsafe_Structure)).Patch();
        unsafeRoofPatch.CreateClassProcessor(typeof(Patch_MoonPool_GravshipTerrainRemoved)).Patch();
        unsafeRoofPatch.CreateClassProcessor(typeof(Patch_MoonPool_GravshipRoofRemoved)).Patch();
        unsafeRoofPatch.CreateClassProcessor(typeof(Patch_MoonPool_SetRoof)).Patch();
        unsafeRoofPatch.Patch(AccessTools.Method(typeof(RoofGrid), nameof(RoofGrid.SetRoof)),
            prefix: new HarmonyMethod(typeof(Program), nameof(SetWorksiteRoof)) { priority = Priority.First });
        unsafeRoofPatch.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), nameof(TerrainDef.IsSubstructure)),
            prefix: new HarmonyMethod(typeof(Program), nameof(ReadWorksiteSubstructure)));
        WorldGrid oldGrid = Find.World.grid;
        TerrainDef oldSpace = TerrainDefOf.Space;
        TerrainDef oldMoonPool = (TerrainDef)AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").GetValue(null);
        RoofDef oldFieldRoof = (RoofDef)AccessTools.Field(typeof(MoonPoolUtility), "fieldRoofDef").GetValue(null);
        var fixtureSpace = (TerrainDef)Raw(typeof(TerrainDef));
        fixtureSpace.defName = "regressionSpace";
        var fixtureMoonPool = (TerrainDef)Raw(typeof(TerrainDef));
        fixtureMoonPool.defName = "regressionMoonPool";
        var fixtureFieldRoof = (RoofDef)Raw(typeof(RoofDef));
        fixtureFieldRoof.defName = "regressionFieldRoof";
        TerrainDefOf.Space = fixtureSpace;
        AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").SetValue(null, fixtureMoonPool);
        AccessTools.Field(typeof(MoonPoolUtility), "fieldRoofDef").SetValue(null, fixtureFieldRoof);
        var fixtureLayer = (PlanetLayer)Raw(typeof(PlanetLayer));
        var fixtureLayerDef = new PlanetLayerDef { isSpace = true };
        Set(fixtureLayer, "def", fixtureLayerDef);
        Find.World.grid = (WorldGrid)Raw(typeof(WorldGrid));
        Set(Find.World.grid, "planetLayers", new Dictionary<int, PlanetLayer> { [0] = fixtureLayer });
        var landingMap = TestMap(49808);
        var ordinaryMap = TestMap(49809);
        var surfaceMap = TestMap(49815);
        landingMap.roofGrid = new RoofGrid(landingMap);
        ordinaryMap.info.parent = null;
        var surfaceParent = new RegressionLandingMapParent("RegressionSurface", 0, 0);
        surfaceMap.info.parent = surfaceParent;
        landingMap.generationTick = 13579;
        var landingParent = new RegressionLandingMapParent("SpaceSettlement", 0, landingMap.generationTick);
        landingMap.info.parent = landingParent;
        Array.Fill(landingMap.terrainGrid.topGrid, TerrainDefOf.Space);
        landingMap.wasSpawnedViaGravShipLanding = true;
        var native = new PoiState(49808, (MapParent)null)
        {
            map = landingMap,
            nativeParent = landingParent,
            pose = new SpacePose(Vector3.forward, 0f)
        };
        var pocketSource = new MapSpaceObject(49809, ordinaryMap, native.pose);
        universe.RegisterObject(native);
        universe.RegisterObject(pocketSource);
        try
        {
            fixtureLayerDef.isSpace = false;
            try
            {
                universe.ShipTransitions.BeginStrategicLanding(null, surfaceMap, IntVec3.Zero);
                universe.ShipTransitions.CompleteStrategicLanding(null);
                universe.ShipTransitions.CompleteStrategicLanding(null);
            }
            finally
            {
                fixtureLayerDef.isSpace = true;
            }
            Check(!universe.ShipTransitions.TryGetLandingCaptureScope(null, out _),
                "surface landing completion is idempotent when no Delta-V landing capture was established");
            Check(landingMap.GetComponent<DeltaVPocketMapMetadata>() == null,
                "vanilla orbital landing maps start without Delta-V worksite metadata");
            Check(universe.ShipTransitions.MarkStrategicLandingWorksite(landingMap)
                && landingMap.GetComponent<DeltaVPocketMapMetadata>()?.worksite == true
                && native.IsWorksite,
                "strategic landing settlement marks its native worldtile container as a worksite");
            Check(landingMap.Tile.Valid && landingParent.Map == landingMap
                && universe.FrameOf(landingMap) == native,
                "strategic landing retirement fixture has a real worldtile NativeParent frame");
            Check(!native.Ships.Any(ship => ship.IsValid),
                "strategic landing retirement fixture has no remaining ship");
            Check(SpaceMapGovernance.IsBlankLandingCanvas(landingMap),
                "strategic landing retirement fixture has no remaining canvas entity");
            Check(!ShipRegistry.HasLooseContents(landingMap),
                "strategic landing retirement fixture has no loose content");
            IntVec3 strippedCell = new IntVec3(2, 0, 2);
            int strippedIndex = landingMap.cellIndices.CellToIndex(strippedCell);
            var fixtureFoundation = (TerrainDef)Raw(typeof(TerrainDef));
            fixtureFoundation.defName = "regressionSubstructure";
            worksiteFoundation = fixtureFoundation;
            var fixtureRoof = (RoofDef)Raw(typeof(RoofDef));
            fixtureRoof.defName = "regressionRoof";
            landingMap.terrainGrid.foundationGrid[strippedIndex] = fixtureFoundation;
            ((RoofDef[])AccessTools.Field(typeof(RoofGrid), "roofGrid").GetValue(landingMap.roofGrid))[strippedIndex]
                = fixtureRoof;
            ShipStructureIndex.Read(landingMap).UpdateTerrain(strippedCell);
            Check(!ShipRegistry.HasLooseContents(landingMap),
                "roofed substructure is supported before Gravship stripping");
            landingMap.terrainGrid.RemoveGravshipTerrainUnsafe(strippedCell, strippedIndex);
            ShipStructureIndex.Read(landingMap).UpdateTerrain(strippedCell);
            Check(ShipRegistry.HasLooseContents(landingMap),
                "foundation removal observes the still-present roof as loose terrain");
            landingMap.roofGrid.RemoveRoofUnsafe(strippedIndex);
            Check(!ShipRegistry.HasLooseContents(landingMap),
                "unsafe roof removal refreshes the ship structure cache after Gravship stripping");
            landingMap.terrainGrid.topGrid[strippedIndex] = fixtureMoonPool;
            landingMap.terrainGrid.foundationGrid[strippedIndex] = fixtureFoundation;
            ((RoofDef[])AccessTools.Field(typeof(RoofGrid), "roofGrid").GetValue(landingMap.roofGrid))[strippedIndex]
                = fixtureRoof;
            landingMap.terrainGrid.RemoveGravshipTerrainUnsafe(strippedCell, strippedIndex);
            Check(landingMap.roofGrid.RoofAt(strippedIndex) == fixtureRoof,
                "Gravship terrain removal preserves a normal roof over restored moon-pool terrain");
            landingMap.roofGrid.RemoveRoofUnsafe(strippedIndex);
            Check(landingMap.roofGrid.RoofAt(strippedIndex) == fixtureFieldRoof,
                "final unsafe roof removal restores the moon-pool field roof");
            landingMap.terrainGrid.topGrid[strippedIndex] = fixtureSpace;
            ((RoofDef[])AccessTools.Field(typeof(RoofGrid), "roofGrid").GetValue(landingMap.roofGrid))[strippedIndex]
                = null;
            ShipStructureIndex.Read(landingMap).UpdateTerrain(strippedCell);
            Check(landingMap.wasSpawnedViaGravShipLanding,
                "strategic landing retirement fixture records vanilla Gravship generation");
            Check(landingParent is Settlement && landingParent.creationGameTicks == landingMap.generationTick,
                "strategic landing retirement fixture records a same-tick landing Settlement");
            Check(SpaceMapGovernance.IsLandingCreatedSettlement(landingMap, landingParent),
                "same-tick Gravship Settlement is classified as landing-created");
            Check(SpaceMapGovernance.IsEmptyLandingWorksite(landingMap, universe),
                "an emptied landing-created SpaceSettlement is a native worksite retirement candidate");
            Check(ShipFlightTransitionService.StrategicDepartureWorldtileSource(landingMap) == landingMap
                && ShipFlightTransitionService.StrategicDepartureWorldtileSource(ordinaryMap) == null
                && universe.FrameOf(landingMap) == native && universe.FrameOf(ordinaryMap) == pocketSource,
                "strategic departure selects only the actual worldtile source, never a same-sector POI for a pocket source");
            landingParent.creationGameTicks--;
            Check(!SpaceMapGovernance.IsEmptyLandingWorksite(landingMap, universe),
                "a pre-existing orbital Settlement is not retired as a temporary landing Home");
            landingParent.creationGameTicks++;
            Check(!universe.ShipTransitions.MarkStrategicLandingWorksite(ordinaryMap)
                && ordinaryMap.GetComponent<DeltaVPocketMapMetadata>() == null,
                "landing settlement does not mark an unrelated map as a worksite");
        }
        finally
        {
            worksiteFoundation = null;
            TerrainDefOf.Space = oldSpace;
            AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").SetValue(null, oldMoonPool);
            AccessTools.Field(typeof(MoonPoolUtility), "fieldRoofDef").SetValue(null, oldFieldRoof);
            unsafeRoofPatch.UnpatchAll(unsafeRoofPatch.Id);
            Find.World.grid = oldGrid;
            native.map = null;
            pocketSource.map = null;
            universe.UnregisterObject(pocketSource);
            universe.UnregisterObject(native);
            Find.Maps.Remove(landingMap);
            Find.Maps.Remove(ordinaryMap);
            Find.Maps.Remove(surfaceMap);
        }
    }

    static void CheckOrbitalWork(UniverseManager universe)
    {
        var ownMap = TestMap(49810);
        var hostMap = TestMap(49811);
        var thirdMap = TestMap(49812);
        ownMap.roofGrid = new RoofGrid(ownMap);
        hostMap.roofGrid = new RoofGrid(hostMap);
        hostMap.terrainGrid.topGrid[1] = hostMap.terrainGrid.topGrid[0];
        hostMap.terrainGrid.topGrid[0] = TerrainDefOf.Space;
        var pose = new SpacePose(Vector3.forward, 0f);
        var marker = new PoiState(49811, "asteroid", pose.position, pose.angle, 0);
        marker.baseTag |= SpaceObjectTag.GravityRegion;
        var station = new PoiState(49813, "station", pose.position, pose.angle, 0);
        var native = new PoiState(49814, (MapParent)null);
        var host = SpaceMapStateManager.CreateMaterializedContainer(marker, hostMap);
        var own = new MapSpaceObject(49810, ownMap, pose);
        var third = SpaceMapStateManager.CreateMaterializedContainer(
            new PoiState(49812, "other asteroid", pose.position, pose.angle, 0), thirdMap);
        third.baseTag |= SpaceObjectTag.GravityRegion;
        Check(marker.HasGravityRegion && native.HasGravityRegion && !station.HasGravityRegion
            && !own.HasGravityRegion, "gravity tags are assigned by creation rules");
        Check(host.HasGravityRegion && host.HasTag(SpaceObjectTag.Container)
            && !host.HasTag(SpaceObjectTag.Marker) && !host.CanRelocateMap,
            "materialization preserves gravity while replacing the marker role");
        Check(!OrbitalWorkService.IntersectsGravity(own, CellRect.WholeMap(ownMap), marker)
            && OrbitalWorkService.IntersectsGravity(own, CellRect.WholeMap(ownMap), third),
            "generation clearance projects loaded tagged maps only");
        var archived = new ArchivedSpaceObject();
        host.CopyFactsTo(archived);
        string path = System.IO.Path.GetFullPath("Source/obj/NpcRetreatRegression/gravity-region.xml");
        var restored = new ArchivedSpaceObject();
        try
        {
            Scribe.saver.InitSaving(path, "gravity-region"); archived.ExposeData(); Scribe.saver.FinalizeSaving();
            Scribe.loader.InitLoading(path); restored.ExposeData(); Scribe.loader.FinalizeLoading();
        }
        finally { Scribe.ForceStop(); System.IO.File.Delete(path); }
        Check(restored.HasGravityRegion && restored.RestoreRepresentation(hostMap).HasGravityRegion,
            "archived gravity tag survives Scribe and map restoration");

        PilotTarget target = PilotTarget.Of(new OrbitalWorkTestShip(ownMap));
        var patch = new Harmony("DeltaV.Regression.OrbitalWork");
        void Stub(Type type, string method, string prefix) =>
            patch.Patch(AccessTools.Method(type, method),
                prefix: new HarmonyMethod(typeof(Program), prefix));
        List<WorldComponent> worldComponents = Find.World.components;
        Find.World.components ??= new List<WorldComponent>();
        universe.RegisterObject(own); universe.RegisterObject(host); universe.RegisterObject(third);
        try
        {
            Stub(typeof(OrbitalWorkService), "Reject", nameof(RecordWorksiteRejection));
            Stub(typeof(LandingApproachController), "NearestPlacement", nameof(PlaceWorksiteProbe));
            Stub(typeof(LandingApproachController), "LandingSiteStillValid", nameof(ValidateWorksiteProbe));
            Stub(typeof(OrbitalWorkService), "Dock", nameof(RecordWorksiteDock));
            Stub(typeof(ShipPocketMapFactory), "Generate", nameof(RecordWorksiteGeneration));
            var service = universe.Worksites;
            void Start()
            {
                worksiteRejection = null; worksiteDockMap = null;
                worksiteRequestedSize = IntVec3.Zero;
                service.Start(target);
            }
            Start();
            int margin = DeltaVMod.CurrentSettings.worksiteMargin;
            Check(worksiteRejection == null && worksiteDockMap == null
                && worksiteRequestedSize == new IntVec3(ownMap.Size.x + 2 * margin, 1, ownMap.Size.z + 2 * margin),
                "starting work without a sync plan expands only the source map even when nearby motion matches");

            var plan = new LandingApproachController.DriftSyncPlan(target, host, 0f);
            Set(universe.Approach, "plan", plan);
            Start();
            Check(worksiteRejection == "DeltaV_EVANotSynchronized" && worksiteRequestedSize == IntVec3.Zero
                && worksiteDockMap == null, "a new plan waits for stability without expanding or docking");
            plan.holdTicks = DeltaVMod.CurrentSettings.dockingHoldTicks;
            own.pose.velocity = Vector2.right;
            Start();
            Check(worksiteRejection == "DeltaV_EVANotSynchronized" && plan.holdTicks == 0
                && universe.Approach.ActivePlan == plan, "motion mismatch resets stability while keeping the explicit target");
            own.pose.velocity = Vector2.zero;
            for (int tick = 0; tick < DeltaVMod.CurrentSettings.dockingHoldTicks; tick++)
                universe.Approach.Tick(universe);
            worksiteLandingValid = true;
            Start();
            Check(worksiteDockMap == hostMap && worksiteRejection == null, "legal landing goes to the specific tagged host");
            worksiteLandingValid = false;
            Start();
            Check(worksiteRejection == "DeltaV_EVAGravity" && worksiteRequestedSize == IntVec3.Zero && worksiteDockMap == null,
                "illegal landing on a tagged sync target reports gravity");
            host.baseTag = SpaceObjectTag.Container;
            Start();
            int sharedMargin = margin + ShipPocketMapFactory.StructureMargin;
            Check(worksiteRequestedSize == new IntVec3(2 + 2 * sharedMargin, 1, 1 + 2 * sharedMargin)
                && worksiteRejection == null,
                "overlapping third-party gravity does not block joint worksite preflight");
            host.RepresentationVersion++;
            Start();
            Check(worksiteRejection == "DeltaV_EVASyncRequired" && universe.Approach.ActivePlan == null
                && worksiteRequestedSize == IntVec3.Zero && worksiteDockMap == null, "representation change cancels the explicit plan");
            plan = new LandingApproachController.DriftSyncPlan(target, host, 0f);
            Set(universe.Approach, "plan", plan);
            host.pose.position = OrbitSphere.Move(pose.position,
                new Vector2(DeltaVMod.CurrentSettings.dockingCancelDistance + 10f, 0f));
            Start();
            Check(worksiteRejection == "DeltaV_EVASyncRequired" && universe.Approach.ActivePlan == null,
                "leaving sync range requires a fresh explicit plan");
            host.pose = pose;
            plan = new LandingApproachController.DriftSyncPlan(target, host, 0f);
            Set(universe.Approach, "plan", plan);
            host.map = null;
            Start();
            Check(worksiteRejection == "DeltaV_EVASyncRequired" && universe.Approach.ActivePlan == null,
                "unloading the target map requires a fresh explicit plan");
        }
        finally
        {
            Set(universe.Approach, "plan", null);
            Find.World.components = worldComponents;
            host.map = hostMap;
            patch.UnpatchAll(patch.Id);
            universe.UnregisterObject(third); universe.UnregisterObject(host); universe.UnregisterObject(own);
            Find.Maps.Remove(thirdMap); Find.Maps.Remove(hostMap); Find.Maps.Remove(ownMap);
        }
    }

    static void CheckSoftDockInterpolation()
    {
        var start = new SpacePose(new Vector3(1, 2, 3).normalized, Mathf.PI - .08f)
            { velocity = new Vector2(2f, -1f), angularVelocity = .04f };
        var target = SpaceTransform.RigidPointPose(start, new Vector2(.8f, -.4f), .16f);
        SpacePose actual = start;
        float distance = OrbitSphere.ArcCells(actual.direction, target.direction);
        float angle = Mathf.Abs(SpaceTransform.WrapAngle(target.angle - actual.angle));
        bool monotonic = true;
        for (int tick = 0; tick < 240; tick++)
        {
            actual = SpaceTransform.LerpPose(actual, target, 1f - Mathf.Exp(-1f / 30f));
            float nextDistance = OrbitSphere.ArcCells(actual.direction, target.direction);
            float nextAngle = Mathf.Abs(SpaceTransform.WrapAngle(target.angle - actual.angle));
            monotonic &= nextDistance <= distance + 1e-5f && nextAngle <= angle + 1e-6f;
            distance = nextDistance; angle = nextAngle;
        }
        Check(monotonic && distance < .001f && angle < .001f,
            "soft pose interpolation converges without overshoot across the angle wrap");
        Check(Math.Abs(actual.velocity.magnitude - start.velocity.magnitude) < .001f
            && actual.angularVelocity == start.angularVelocity,
            "soft pose interpolation adds no acceleration or spin damping");
        SpacePose untouched = SpaceTransform.LerpPose(start, target, 0f);
        Check(untouched.direction.x == start.direction.x && untouched.direction.y == start.direction.y
            && untouched.direction.z == start.direction.z && untouched.angle == start.angle,
            "zero interpolation leaves the authoritative pose untouched");
    }

    static void CheckSmallScaleMovement()
    {
        var radii = (Dictionary<RimWorld.Planet.PlanetLayer, float>)typeof(OrbitSphere).GetField("RadiusByLayer", Private).GetValue(null);
        var scales = (Dictionary<RimWorld.Planet.PlanetLayer, float>)typeof(OrbitSphere).GetField("WorldPerCellByLayer", Private).GetValue(null);
        var layer = OrbitSphere.Layer;
        float radius = radii[layer], scale = scales[layer];
        bool drag = DeltaVMod.CurrentSettings.passiveDrag;
        try
        {
            // Same angular scale at different world-unit scales must produce the same motion.
            radii[layer] = 1f; scales[layer] = .0001f;
            DeltaVMod.CurrentSettings.passiveDrag = false;
            foreach (Vector3 direction in new[] { Vector3.forward, new Vector3(1, 2, 3).normalized })
            {
                var body = new SpaceObject(49740, new SpacePose(direction, 0f) { velocity = new Vector2(.2f, 0f) });
                for (int tick = 0; tick < 60; tick++)
                    ShipPhysics.Integrate(body, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
                Check(OrbitSphere.ArcCells(direction, body.pose.position) > .15f,
                    "sub-one-cell-per-second motion survives small world units in the real integrator");
            }
            radii[layer] = 100f;
            var precise = new SpaceObject(49741, new SpacePose(new Vector3(1, 2, 3).normalized, 0f)
                { velocity = new Vector2(.2f, 0f) });
            Vector3 before = precise.pose.position;
            for (int tick = 0; tick < 60; tick++)
                ShipPhysics.Integrate(precise, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
            Check(OrbitSphere.ArcCells(before, precise.pose.position) > .15f,
                "low-speed integration retains displacement at a million cells per radian");
            SpacePose saved = precise.pose, restored = default;
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "precise-pose.xml");
            try
            {
                Scribe.saver.InitSaving(path, "pose-test"); saved.ExposeData(); Scribe.saver.FinalizeSaving();
                Scribe.loader.InitLoading(path); restored.ExposeData(); Scribe.loader.FinalizeLoading();
            }
            finally { Scribe.ForceStop(); }
            Check(OrbitSphere.ArcCells(saved.direction, restored.direction) < 1e-7f
                && saved.velocity == restored.velocity && saved.angle == restored.angle,
                "real Scribe round trip preserves the sole double-precision position and motion");
            var replay = new SpaceObject(49742, restored);
            for (int tick = 0; tick < 60; tick++)
            {
                ShipPhysics.Integrate(precise, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
                ShipPhysics.Integrate(replay, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
            }
            Check(OrbitSphere.ArcCells(precise.pose.direction, replay.pose.direction) < 1e-6f,
                "continued low-speed integration agrees before and after save/load without a residual cache");
            Map ma = TestMap(49743), mb = TestMap(49744);
            var a = new MapSpaceObject(49743, ma, precise.pose);
            var b = new MapSpaceObject(49744, mb, SpaceTransform.RigidPointPose(precise.pose, new Vector2(30f, -21f), .7f));
            SpacePose ap = a.pose, bp = b.pose;
            for (int repeat = 0; repeat < 4; repeat++)
            foreach (SpaceObject focus in new SpaceObject[] { a, b })
            {
                Vector2 center = SpaceTransform.UniverseToMapPoint(focus, focus.map, focus.pose.position);
                Check((center - SpaceTransform.ShipLocalOrigin(focus.map)).magnitude < .001f,
                    "switching the draw reference keeps the focused map identity");
                foreach (SpaceObject obj in new SpaceObject[] { a, b })
                {
                    Vector2 drawn = SpaceTransform.UniverseToMapPoint(focus, focus.map, obj.pose.position);
                    Vector3 back = SpaceTransform.MapPointToUniverse(focus, focus.map, drawn);
                    Check(OrbitSphere.ArcCells(back, obj.pose.position) < .1f,
                        "relative position survives both draw references within float rendering precision");
                }
            }
            Check(a.pose.direction.x == ap.direction.x && a.pose.direction.y == ap.direction.y && a.pose.direction.z == ap.direction.z
                && b.pose.direction.x == bp.direction.x && b.pose.direction.y == bp.direction.y && b.pose.direction.z == bp.direction.z
                && a.pose.angle == ap.angle && b.pose.angle == bp.angle,
                "repeated focus transforms never rewrite absolute positions or relative angles");
            Find.Maps.Remove(mb); Find.Maps.Remove(ma);
        }
        finally { radii[layer] = radius; scales[layer] = scale; DeltaVMod.CurrentSettings.passiveDrag = drag; }
    }

    static void CheckScatteredSweep(UniverseManager universe, SpaceObject scattered)
    {
        var sweep = new SpaceCollision.SweptBody { attacker = new SpaceObject(49702, scattered.pose),
            requiredDefender = scattered, sweepRadius = 0f,
            from = SpaceTransform.ShipLocalToUniverse(scattered, new Vector2(-2.5f, 0f)),
            to = SpaceTransform.ShipLocalToUniverse(scattered, new Vector2(2.5f, 0f)) };
        SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { sweep });
        Check(!sweep.resolvedHit && !sweep.dead, "production projectile sweep skips scattered cargo entirely");
    }

    static void CheckSoftDockReference(UniverseManager universe)
    {
        Map a = TestMap(49710), b = TestMap(49711);
        a.info.Size = new IntVec3(31, 1, 28); b.info.Size = new IntVec3(44, 1, 39);
        var own = new MapSpaceObject(49710, a, new SpacePose(Vector3.forward, 0f));
        var host = new MapSpaceObject(49711, b, new SpacePose(new Vector3(1, 2, 3).normalized, .4f)
            { velocity = new Vector2(3f, -1f), angularVelocity = .05f });
        universe.objects.Add(own); universe.objects.Add(host);
        Building_DockingPort Port(Map map, IntVec3 position)
        {
            var port = (Building_DockingPort)Raw(typeof(Building_DockingPort));
            Set(typeof(Thing), port, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
            Set(typeof(Thing), port, "positionInt", position);
            return port;
        }
        Building_DockingPort ownPort = Port(a, new IntVec3(6, 0, 17));
        Building_DockingPort peerPort = Port(b, new IntVec3(27, 0, 9));
        var dock = new CompSoftDocking { parent = ownPort };
        Set(dock, "peer", peerPort);
        Set(dock, "active", true);
        foreach (Rot4 ar in Rot4.AllRotations)
        foreach (Rot4 br in Rot4.AllRotations)
        {
            Set(typeof(Thing), ownPort, "rotationInt", ar); Set(typeof(Thing), peerPort, "rotationInt", br);
            own.pose = dock.AlignmentReference(universe, peerPort);
            Check(dock.Error(universe, peerPort) < .01f
                && Math.Abs(dock.AngleError(universe, peerPort)) < 1e-5f,
                "one reference aligns both crossed guides for every port direction");
            Vector2 ap = SpaceTransform.MapToShipLocal(a, CompSoftDocking.MapPoint(ownPort, 4f));
            Vector2 bp = SpaceTransform.MapToShipLocal(b, CompSoftDocking.MapPoint(peerPort, 4f));
            SpacePose at = SpaceTransform.RigidPointPose(own.pose, ap, 0f), bt = SpaceTransform.RigidPointPose(host.pose, bp, 0f);
            Check(SpaceTransform.RelativeVelocity(at, bt).magnitude < .002f
                && own.pose.angularVelocity == host.pose.angularVelocity,
                "soft docking guide reference includes moving-host point velocity and spin");
        }
        Set(typeof(Thing), ownPort, "rotationInt", Rot4.East);
        Set(typeof(Thing), peerPort, "rotationInt", Rot4.West);
        own.pose = SpaceTransform.RigidPointPose(dock.AlignmentReference(universe, peerPort),
            new Vector2(.5f, -.25f), .08f);
        Check(dock.Error(universe, peerPort) < 1.5f,
            "perturbed soft docking starts inside the capture range");
        for (int tick = 0; tick < 180; tick++)
        {
            float guideError = dock.Error(universe, peerPort);
            own.pose = SpaceTransform.LerpPose(own.pose, dock.AlignmentReference(universe, peerPort),
                CompSoftDocking.AssistFraction(guideError));
            ShipPhysics.Integrate(own, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
            ShipPhysics.Integrate(host, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
        }
        Check(dock.Error(universe, peerPort) < .3f
            && Mathf.Abs(dock.AngleError(universe, peerPort)) < .03f,
            "captured soft docking converges while both ships drift and rotate");
        float far = CompSoftDocking.AssistFraction(1.2f);
        float near = CompSoftDocking.AssistFraction(.6f);
        Check(CompSoftDocking.AssistFraction(1.5f) == 0f
            && near > far && Mathf.Abs(near / far - 4f) < .001f,
            "soft docking assist cuts off at capture range and follows inverse-square guide distance");
        Check(CompSoftDocking.AssistFraction(.1f) == CompSoftDocking.AssistFraction(0f)
            && CompSoftDocking.AssistFraction(.1f) < 1f,
            "coincident guides keep a finite interpolation strength");
        a.info.Size = new IntVec3(401, 1, 401);
        b.info.Size = new IntVec3(401, 1, 401);
        Set(typeof(Thing), ownPort, "positionInt", new IntVec3(390, 0, 200));
        Set(typeof(Thing), peerPort, "positionInt", new IntVec3(10, 0, 200));
        Vector2 ownGuide = SpaceTransform.MapToShipLocal(a, CompSoftDocking.MapPoint(ownPort, 4f));
        Vector2 hostGuide = SpaceTransform.MapToShipLocal(b, CompSoftDocking.MapPoint(peerPort, 4f));
        float relative = SpaceTransform.WrapAngle(ShipOrientation.AngleFromRot4(peerPort.Rotation)
            + Mathf.PI - ShipOrientation.AngleFromRot4(ownPort.Rotation));
        own.pose = SpaceTransform.RigidPointPose(SpaceTransform.AlignLocalPoint(host.pose,
            hostGuide, ownGuide, relative + .18f), new Vector2(1f, 0f), 0f);
        SpacePose exact = dock.AlignmentReference(universe, peerPort), before = own.pose;
        float entranceError = dock.Error(universe, peerPort);
        float weighted = CompSoftDocking.AssistFraction(entranceError);
        SpacePose assisted = SpaceTransform.LerpPose(before, exact, weighted);
        SpacePose oldStep = SpaceTransform.LerpPose(before, exact, 1f - Mathf.Exp(-1f / 30f));
        Check(entranceError > .8f && entranceError < 1.5f
            && OrbitSphere.ArcCells(before.position, exact.position) > 10f
            && OrbitSphere.ArcCells(before.position, assisted.position)
                < OrbitSphere.ArcCells(before.position, oldStep.position) * .2f,
            "edge-port capture eases whole-map movement at the outer guide range");
        universe.objects.Remove(own); universe.objects.Remove(host);
        Find.Maps.Remove(a); Find.Maps.Remove(b);
    }

    sealed class DockingStateExpose : IExposable
    {
        internal CompSoftDocking dock;
        public void ExposeData() => dock.PostExposeData();
    }

    static void CheckDockingCompState(UniverseManager universe)
    {
        Map a = TestMap(49714), b = TestMap(49715);
        Building_DockingPort Port(Map map, int id)
        {
            var port = (Building_DockingPort)Raw(typeof(Building_DockingPort));
            port.def = TestBuildingDef("soft_dock_state_" + id);
            port.thingIDNumber = id;
            Set(typeof(Thing), port, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
            Set(typeof(Thing), port, "positionInt", new IntVec3(1, 0, 2));
            var state = new CompSoftDocking { parent = port };
            Set(typeof(ThingWithComps), port, "comps", new List<ThingComp> { state });
            map.listerThings.AllThings.Add(port);
            return port;
        }
        Building_DockingPort source = Port(a, 49716), original = Port(b, 49717), replacement = Port(b, 49718);
        CompSoftDocking state = source.Docking;
        Call(state, "Capture", original);
        for (int i = 0; i < 90; i++) state.Advance(true);
        Check(state.Peer == original && original.Docking.Peer == source && Mathf.Abs(state.Progress - .5f) < .001f,
            "initiating comp alone owns progress while the opposite port reads its runtime backlink");

        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "soft-dock-state.xml");
        CompSoftDocking RoundTrip(CompSoftDocking current)
        {
            var restored = new CompSoftDocking { parent = source };
            var wrapper = new DockingStateExpose { dock = restored };
            try
            {
                Scribe.saver.InitSaving(path, "soft-dock"); current.PostExposeData(); Scribe.saver.FinalizeSaving();
                Scribe.loader.InitLoading(path);
                Scribe.loader.curParent = wrapper;
                restored.PostExposeData();
                Scribe.loader.crossRefs.RegisterForCrossRefResolve(wrapper);
                Scribe.loader.crossRefs.RegisterForCrossRefResolve(original);
                Scribe.loader.initer.RegisterForPostLoadInit(wrapper);
                Scribe.loader.FinalizeLoading();
            }
            finally { Scribe.ForceStop(); }
            Set(typeof(ThingWithComps), source, "comps", new List<ThingComp> { restored });
            return restored;
        }
        CompSoftDocking loaded = RoundTrip(state);
        Check(original.Docking.Owner == null && loaded.Peer == null,
            "load keeps the stored pair inert until soft docking is restored");
        Call(loaded, "Capture", original);
        Check(loaded.Peer == original && Mathf.Abs(loaded.Progress - .5f) < .001f && !loaded.Connected,
            "real Scribe round trip resumes the same peer's extension without a service mirror");
        for (int i = 0; i < 90; i++) loaded.Advance(true);
        loaded = RoundTrip(loaded);
        Call(loaded, "Capture", original);
        Check(loaded.Connected && loaded.Progress == 1f && original.Docking.Peer == source,
            "real Scribe round trip preserves an already connected port");
        Call(loaded, "Capture", replacement);
        Check(loaded.Progress == 0f && loaded.Peer == replacement && original.Docking.Owner == null,
            "changing the peer starts a new extension and releases the old backlink");
        replacement.Docking.Clear();
        Check(loaded.Peer == null && loaded.Progress == 0f && !loaded.Connected,
            "removing the opposite port clears the initiating comp");
        Call(loaded, "Capture", original);
        var service = new SoftDockService(universe);
        universe.SoftDockEnabled = true;
        ((HashSet<Building_DockingPort>)typeof(SoftDockService).GetField("ports", Private).GetValue(service)).Add(source);
        service.SetEnabled(false);
        Check(loaded.Peer == null && loaded.Progress == 0f,
            "explicitly disabling soft docking clears the port comp immediately");
        var savedManager = new UniverseManager(Current.Game)
        {
            SoftDockEnabled = true,
            ShowThrusterDebugOverlay = true,
            ShowWeaponArcOverlay = true,
            ShowCollisionDebugOverlay = true
        };
        var loadedManager = new UniverseManager(Current.Game);
        string switchPath = System.IO.Path.Combine(AppContext.BaseDirectory, "soft-dock-switch.xml");
        try
        {
            Scribe.saver.InitSaving(switchPath, "soft-dock-switch");
            savedManager.ExposeData();
            Scribe.saver.FinalizeSaving();
            Scribe.loader.InitLoading(switchPath);
            loadedManager.ExposeData();
            Scribe.loader.FinalizeLoading();
        }
        finally { Scribe.ForceStop(); }
        Check(loadedManager.SoftDockEnabled && loadedManager.SoftDock.Enabled
            && loadedManager.ShowThrusterDebugOverlay && loadedManager.ShowWeaponArcOverlay
            && loadedManager.ShowCollisionDebugOverlay,
            "game component Scribe round trip restores all four play-settings row switches");
        var freshManager = new UniverseManager(Current.Game);
        Check(!freshManager.SoftDock.Enabled && !freshManager.ShowThrusterDebugOverlay
            && !freshManager.ShowWeaponArcOverlay && !freshManager.ShowCollisionDebugOverlay,
            "new games default all four play-settings row switches to off");
        Find.Maps.Remove(a); Find.Maps.Remove(b);
    }

    // Observe the vanilla destruction endpoint without Unity; retirement, revalidation,
    // unregistering and pool handoff all run through the production services.
    sealed class LostCargoProbe : Thing
    {
        public int destroyCalls;
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Check(mode == DestroyMode.Vanish && Thing.allowDestroyNonDestroyable,
                "lost cargo vanishes through vanilla removal even when its Def is indestructible");
            destroyCalls++;
            Set(typeof(Thing), this, "mapIndexOrState", (sbyte)-2);
        }
    }

    static Thing NewMaplessCargo(string name)
    {
        var cargo = (LostCargoProbe)Raw(typeof(LostCargoProbe));
        cargo.def = TestBuildingDef(name);
        cargo.def.category = ThingCategory.Item;
        cargo.def.stackLimit = 75;
        cargo.stackCount = 1;
        Set(typeof(Thing), cargo, "mapIndexOrState", (sbyte)-1);
        return cargo;
    }

    static void CheckScatteredRetirement(UniverseManager universe)
    {
        var home = new SpaceObject(49721, new SpacePose(Vector3.forward, 0f))
            { baseTag = SpaceObjectTag.Ship, faction = Faction.OfPlayer };
        universe.RegisterObject(home);
        var cargo = (LostCargoProbe)NewMaplessCargo("lost_cargo");
        var lost = universe.CapturePayload(cargo, new SpacePose(OrbitSphere.Move(Vector3.forward,
            new Vector2(DeltaVMod.CurrentSettings.poiDeleteRadius + 1000f, 0f)), 0f), null);
        SpacePose far = lost.pose;
        universe.Scatter.RetireLost(lost);
        Check(cargo.destroyCalls == 0 && Actions(universe).Count > 0, "distance loss queues retirement");
        lost.pose = home.pose;
        Call(universe, "RunFrameActions");
        Check(cargo.destroyCalls == 0 && universe.objects.Contains(lost), "returning within range cancels queued loss");
        lost.pose = far;
        universe.Scatter.RetireLost(lost, departing: true);
        Call(universe, "RunFrameActions");
        Check(cargo.destroyCalls == 1 && universe.ObjectById(lost.objectId) == null
            && !universe.Scatter.Candidates.Contains(lost) && !Thing.allowDestroyNonDestroyable,
            "sector departure destroys and releases the actual payload exactly once");
        universe.UnregisterObject(home);
    }

    static void CheckFocusedScatterAndMapSetter(UniverseManager universe)
    {
        Game game = Current.Game;
        Type focusPatch = typeof(UniverseManager).Assembly.GetType("DeltaV.Patch_Game_CurrentMapFocus");
        var postfix = focusPatch.GetMethod("Postfix", Private);
        Map target = TestMap(49723);
        var prefix = focusPatch.GetMethod("Prefix", Private);
        game.currentMapIndex = (sbyte)Find.Maps.Count;
        object[] args = { game, target };
        Check((bool)prefix.Invoke(null, args),
            "map setter prefix accepts a valid target while old index is out of range");
        Map invalid = (Map)Raw(typeof(Map));
        args[1] = invalid;
        Check((bool)prefix.Invoke(null, args),
            "invalid target is left to vanilla without reading stale current map");
        target.info.parent = (UniverseHostParent_DeltaV)Raw(typeof(UniverseHostParent_DeltaV));
        args[1] = target;
        Check(!(bool)prefix.Invoke(null, args) && game.currentMapIndex == Find.Maps.Count,
            "host map is rejected without reading or changing the stale old index");
        postfix.Invoke(null, new object[] { game });
        Check(game.currentMapIndex == Find.Maps.Count,
            "map setter postfix tolerates the out-of-range index without reading CurrentMap");
        game.currentMapIndex = -1;
        Find.Maps.Remove(target);
    }
}
