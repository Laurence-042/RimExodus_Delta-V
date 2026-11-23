using System;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static int salvageMessages, salvagePlacements, salvageWarnings;
    static bool salvagePlacementSucceeds;
    static Map salvageOutputMap;
    static Thing lastSalvageOutput;
    static bool useNativeDropLocation;

    // No Unity scene: substitute only final placement and UI, retaining production cutting,
    // vanilla material generation/ThingOwner/GenDrop and the actual Harmony result hook.
    static bool PlaceSalvage(Thing thing, Map map, ref Thing lastResultingThing, ref bool __result)
    {
        if (useNativeDropLocation) return true;
        salvagePlacements++;
        salvageOutputMap = map;
        lastSalvageOutput = thing;
        lastResultingThing = thing;
        __result = salvagePlacementSucceeds;
        return false;
    }

    static bool MakeSalvage(ThingDef def, ref Thing __result)
    {
        var thing = (Thing)Raw(typeof(ReceivingItem));
        thing.def = def;
        thing.stackCount = 1;
        Set(typeof(Thing), thing, "mapIndexOrState", (sbyte)-1);
        __result = thing;
        return false;
    }

    static bool ShowSalvageMessage() { salvageMessages++; return false; }
    static bool SalvageLabel(ref string __result) { __result = "Mining beam"; return false; }
    static bool SalvageFoundation(TerrainDef __instance, ref bool __result)
    { __result = __instance.HasTag("Substructure"); return false; }
    static bool ObserveSalvageWarning(string text)
    {
        if (text.StartsWith("Failed to place all leavings")) salvageWarnings++;
        return false;
    }

    sealed class SalvageTestBuilding : Building
    {
        int hp;
        public int destroyCalls;
        public bool refuseRemoval;
        public override int HitPoints { get => hp; set => hp = value; }
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Check(mode == DestroyMode.Deconstruct, "cut retains the original deconstruction cleanup");
            GenLeaving.DoLeavingsFor(this, Map, mode);
            if (refuseRemoval) return;
            destroyCalls++;
            Set(typeof(Thing), this, "mapIndexOrState", (sbyte)-2);
        }
    }

    sealed class MiningTestRock : Mineable
    {
        int hp;
        public override int HitPoints { get => hp; set => hp = value; }
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Check(mode == DestroyMode.Vanish, "mining suppresses duplicate vanilla rock drops");
            Set(typeof(Thing), this, "mapIndexOrState", (sbyte)-2);
        }
    }

    sealed class SalvageTestConduit : Building
    {
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Check(mode == DestroyMode.Deconstruct, "foundation cutting removes its hidden attachment as a deconstruction");
            GenLeaving.DoLeavingsFor(this, Map, mode);
            Map.thingGrid.Deregister(this);
            Set(typeof(Thing), this, "mapIndexOrState", (sbyte)-2);
        }
    }

    static void CheckSalvageOutput(UniverseManager universe)
    {
        var harness = new Harmony("DeltaV.SalvageRegression");
        ThingDefOf.PilotConsole = TestBuildingDef("salvagePilotConsole");
        var player = (Faction)Raw(typeof(Faction));
        player.def = new FactionDef { isPlayer = true };
        var enemy = (Faction)Raw(typeof(Faction));
        enemy.def = new FactionDef();
        Map sourceMap = TestMap(47001), targetMap = TestMap(47002);
        targetMap.roofGrid = new RoofGrid(targetMap);
        Thing launcher = AddTestHull(sourceMap, player, 47003).parent;
        var target = new MapSpaceObject(47004, targetMap, new SpacePose(Vector3.forward, 0));
        universe.objects.Add(target);
        var steel = (ThingDef)Raw(typeof(ThingDef));
        steel.defName = "salvageSteel"; steel.label = "steel";
        steel.category = ThingCategory.Item; steel.stackLimit = 75; steel.useHitPoints = false;
        var def = TestBuildingDef("salvageWall");
        def.destroyable = def.useHitPoints = true;
        def.resourcesFractionWhenDeconstructed = 0.5f;
        def.costList = new List<ThingDefCountClass> { new ThingDefCountClass(steel, 20) };
        var building = (SalvageTestBuilding)Raw(typeof(SalvageTestBuilding));
        building.def = def;
        building.HitPoints = 120;
        Set(typeof(ThingWithComps), building, "comps", new List<ThingComp>());
        Set(typeof(Thing), building, "positionInt", new IntVec3(2, 0, 2));
        Set(typeof(Thing), building, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(targetMap));
        var evt = new SpaceCollision.ImpactEvent { launcher = launcher, defender = target,
            weapon = new CompSpaceWeapon { parent = (ThingWithComps)launcher, props = new CompProperties_MiningBeam
                { cutDamage = 60, miningYieldFactor = .5f, dismantleYieldFactor = .5f, deliverToEmitter = true } },
            defenderBuilding = building, hitCell = building.Position, damageScale = 1 };
        var part = new ImpactOutcomePart { kind = ImpactEffectKind.CutStructure };
        var oldState = Current.ProgramState;
        var mainThread = AccessTools.Field(typeof(UnityData), "mainThreadId");
        object oldMainThread = mainThread.GetValue(null);
        mainThread.SetValue(null, System.Threading.Thread.CurrentThread.ManagedThreadId);
        Current.ProgramState = ProgramState.Playing;
        Current.Game.storyteller = (Storyteller)Raw(typeof(Storyteller));
        Current.Game.storyteller.difficulty = new Difficulty();
        try
        {
            harness.Patch(Patch_SalvageOutput_Placement.TargetMethod(),
                prefix: new HarmonyMethod(typeof(Program), nameof(PlaceSalvage)));
            harness.CreateClassProcessor(typeof(Patch_SalvageOutput_Placement)).Patch();
            harness.CreateClassProcessor(typeof(Patch_SalvageOutput_Warning)).Patch();
            harness.CreateClassProcessor(typeof(Patch_CombatLeavings)).Patch();
            harness.CreateClassProcessor(typeof(Patch_SalvageBuildingYield)).Patch();
            harness.CreateClassProcessor(typeof(Patch_SalvageTerrainYield)).Patch();
            harness.CreateClassProcessor(typeof(Patch_SalvageForcedBuildingYield)).Patch();
            harness.Patch(AccessTools.PropertyGetter(typeof(MapInfo), nameof(MapInfo.Tile)),
                prefix: new HarmonyMethod(typeof(Program), nameof(SalvageFixtureTile)));
            harness.Patch(AccessTools.Method(typeof(ThingMaker), nameof(ThingMaker.MakeThing)),
                prefix: new HarmonyMethod(typeof(Program), nameof(MakeSalvage)));
            harness.Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.LabelShort)),
                prefix: new HarmonyMethod(typeof(Program), nameof(SalvageLabel)));
            harness.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.ActiveLanguageWorker)),
                prefix: new HarmonyMethod(typeof(Program), nameof(WeaponMenuLanguage)));
            harness.Patch(AccessTools.Method(typeof(Messages), nameof(Messages.Message),
                new[] { typeof(string), typeof(LookTargets), typeof(MessageTypeDef), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(ShowSalvageMessage)));
            harness.Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning)),
                prefix: new HarmonyMethod(typeof(Program), nameof(ObserveSalvageWarning)) { priority = Priority.Last });
            salvagePlacementSucceeds = false;
            SpaceImpactEffects.ApplyToDefender(evt, part);
            Check(building.HitPoints == 60 && salvagePlacements == 0 && salvageMessages == 0,
                "cut damages target before output exists, even without any receiver foundation or empty-deck scan");
            building.refuseRemoval = true;
            SpaceImpactEffects.ApplyToDefender(evt, part);
            Check(building.Spawned && salvagePlacements == 0 && salvageMessages == 0,
                "a refused removal publishes no staged material");
            building.refuseRemoval = false;
            SpaceImpactEffects.ApplyToDefender(evt, part);
            Check(building.Destroyed && building.destroyCalls == 1 && salvagePlacements == 1
                && salvageOutputMap == sourceMap && lastSalvageOutput.def == steel
                && lastSalvageOutput.stackCount == 5,
                "final cut destroys target and recovers half the normal dismantling yield on receiver");
            Check(salvageMessages == 1 && salvageWarnings == 0,
                $"real drop failure produces one loss notice, no warning spam (messages={salvageMessages}, warnings={salvageWarnings})");
            SpaceImpactEffects.ApplyToDefender(evt, part);
            Check(salvagePlacements == 1, "destroyed target cannot pay materials twice");
            CheckFractionalCutReturn(launcher, sourceMap, building);

            var terrain = (TerrainDef)Raw(typeof(TerrainDef));
            terrain.defName = "salvageFoundation"; terrain.resourcesFractionWhenDeconstructed = 0.5f;
            terrain.costList = new List<ThingDefCountClass> { new ThingDefCountClass(steel, 8) };
            using (new SalvageOutputScope(launcher))
                GenLeaving.DoLeavingsFor(terrain, launcher.Position, sourceMap);
            Check(lastSalvageOutput.stackCount == 4 && salvageMessages == 2 && salvageWarnings == 0,
                "terrain's real GenLeaving failure immediately emits its own notification");
            using (new SalvageOutputScope(launcher))
                GenPlace.TryPlaceThing(lastSalvageOutput, launcher.Position, sourceMap, ThingPlaceMode.Near);
            Check(salvageMessages == 3, "another mineral failure in the same tick also notifies");
            salvagePlacementSucceeds = true;
            using (new SalvageOutputScope(launcher))
                GenPlace.TryPlaceThing(lastSalvageOutput, launcher.Position, sourceMap, ThingPlaceMode.Near);
            Check(salvageMessages == 3, "successful placement is silent");
            salvagePlacementSucceeds = false;
            GenLeaving.DoLeavingsFor(terrain, launcher.Position, sourceMap);
            Check(salvageMessages == 3 && salvageWarnings == 1,
                "ordinary deconstruction retains vanilla warning and never emits mining notice");
            try
            {
                using (new SalvageOutputScope(launcher)) throw new InvalidOperationException("fixture");
            }
            catch (InvalidOperationException) { }
            GenPlace.TryPlaceThing(lastSalvageOutput, launcher.Position, sourceMap, ThingPlaceMode.Near);
            Check(salvageMessages == 3, "exception restores scope; unrelated placement is silent");
            using (new SalvageOutputScope(launcher))
            {
                using (new SalvageOutputScope(launcher)) { }
                // Simulate the native boundary after a partial placement: failure with remainder.
                lastSalvageOutput.stackCount = 1;
                GenPlace.TryPlaceThing(lastSalvageOutput, launcher.Position, sourceMap, ThingPlaceMode.Near);
            }
            Check(salvageMessages == 4, "nested scope restores outer observer and partial failure notifies");
            Thing anotherLauncher = AddTestHull(sourceMap, player, 47005).parent;
            using (new SalvageOutputScope(anotherLauncher))
                GenPlace.TryPlaceThing(lastSalvageOutput, anotherLauncher.Position, sourceMap, ThingPlaceMode.Near);
            Check(salvageMessages == 5, "another emitter's failure also notifies immediately");
            using (new SalvageOutputScope(launcher))
                GenPlace.TryPlaceThing(lastSalvageOutput, launcher.Position, targetMap, ThingPlaceMode.Near);
            Check(salvageMessages == 5, "unrelated map placement inside a callback does not report mining loss");
            Thing enemyLauncher = AddTestHull(sourceMap, enemy, 47006).parent;
            using (new SalvageOutputScope(enemyLauncher))
                GenPlace.TryPlaceThing(lastSalvageOutput, enemyLauncher.Position, sourceMap, ThingPlaceMode.Near);
            Check(salvageMessages == 5, "enemy mining losses do not notify player");
            using (new SalvageOutputScope(launcher)) { }
            Check(salvageMessages == 5, "no products or no placement attempt does not imply loss");

            harness.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), nameof(TerrainDef.IsSubstructure)),
                prefix: new HarmonyMethod(typeof(Program), nameof(SalvageFoundation)));
            harness.Patch(AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
                prefix: new HarmonyMethod(typeof(Program), nameof(SkipWeaponMenuLayout)));
            var conduitDef = TestBuildingDef("salvageHiddenConduit");
            conduitDef.building.isPowerConduit = true;
            conduitDef.building.canBeDamagedByAttacks = false;
            Thing conduit = AddTestBuilding(targetMap, enemy, 47007, conduitDef, new CompPowerTransmitter(),
                (SalvageTestConduit)Raw(typeof(SalvageTestConduit)));
            terrain.isFoundation = true;
            terrain.tags = new List<string> { "Substructure" };
            int cellIndex = targetMap.cellIndices.CellToIndex(conduit.Position);
            targetMap.terrainGrid.foundationGrid[cellIndex] = terrain;
            targetMap.terrainGrid.topGrid[cellIndex] = (TerrainDef)Raw(typeof(TerrainDef));
            var kind = SpaceStructureRules.ClassifyCell(targetMap, conduit.Position, out var hit);
            Check(kind == SpaceCellKind.Substructure && hit == null, "mining hit selects foundation underneath hidden conduit");
            evt.defenderBuilding = hit;
            evt.defenderIsSubstructure = kind == SpaceCellKind.Substructure;
            evt.hitCell = conduit.Position;
            int dropsBefore = salvagePlacements;
            salvagePlacementSucceeds = true;
            SpaceImpactEffects.ApplyToDefender(evt, part);
            Check(!SubstructureOwnership.HasFoundation(targetMap, conduit.Position)
                && salvagePlacements == dropsBefore + 1 && lastSalvageOutput.stackCount == 2
                && salvageOutputMap == sourceMap,
                "actual salvage removes conduit-covered foundation and hands its materials to native receiver placement");
            salvagePlacementSucceeds = false;
            GenPlace.TryPlaceThing(lastSalvageOutput, launcher.Position, null, ThingPlaceMode.Near);
            Check(salvageMessages == 5, "null-map failure outside mining scope remains untouched");

            CheckMiningRoutes(universe, harness, evt, part, steel, targetMap, sourceMap);
            CheckCombatLeavings(universe, harness, steel);
            CheckCutCasket(harness, evt, part, steel, targetMap);
            CheckSmallMapDisposal(universe);
        }
        finally
        {
            harness.UnpatchAll(harness.Id);
            mainThread.SetValue(null, oldMainThread);
            Current.ProgramState = oldState;
            universe.objects.Clear();
            Find.Maps.Clear();
            ShipRegistry.ClearCaches();
            universe.Weapons.ResetFlow();
        }
    }

    static Building_Casket cutCasket;
    static int cutExplosionCalls;

    // Run Building_Casket.Destroy itself. Its base removes the scene building and
    // calls GenLeaving and PostDestroy; replace only that scene-facing part.
    static bool CutCasketBaseDestroy(Building __instance, DestroyMode mode)
    {
        if (__instance != cutCasket) return true;
        Map map = __instance.Map;
        GenLeaving.DoLeavingsFor(__instance, map, mode);
        Set(typeof(Thing), __instance, "mapIndexOrState", (sbyte)-2);
        __instance.GetComp<CompExplosive>().PostDestroy(mode, map);
        return false;
    }

    static bool ObserveCutExplosion()
    {
        cutExplosionCalls++;
        return false;
    }

    static bool PlaceCasketOccupant(Thing thing, IntVec3 dropCell, Map map, ref Thing resultingThing, ref bool __result)
    {
        if (thing is not ReceivingPawn) return true;
        ProbeSpawnCargo(thing, dropCell, map);
        resultingThing = thing;
        __result = true;
        return false;
    }

    static void CheckCutCasket(Harmony harness, SpaceCollision.ImpactEvent evt, ImpactOutcomePart part, ThingDef steel, Map map)
    {
        var casket = new Building_Casket { def = TestBuildingDef("salvageCasket") };
        cutCasket = casket;
        casket.def.destroyable = true;
        casket.def.resourcesFractionWhenDeconstructed = .5f;
        casket.def.costList = new List<ThingDefCountClass> { new ThingDefCountClass(steel, 20) };
        var explosive = new CompExplosive { parent = casket, props = new CompProperties_Explosive { explodeOnDestroyed = true } };
        Set(typeof(ThingWithComps), casket, "comps", new List<ThingComp> { explosive });
        var occupant = (ReceivingPawn)Raw(typeof(ReceivingPawn));
        occupant.def = TestBuildingDef("salvageCasketOccupant");
        occupant.def.category = ThingCategory.Pawn;
        occupant.stackCount = 1;
        Set(typeof(Thing), occupant, "mapIndexOrState", (sbyte)-1);
        Check(casket.GetDirectlyHeldThings().TryAdd(occupant, canMergeWithExistingStacks: false), "casket fixture contains a pawn");
        evt.hitCell = new IntVec3(1, 0, 1);
        ProbeSpawnCargo(casket, evt.hitCell, map);
        evt.defenderBuilding = casket;
        evt.weapon = new CompSpaceWeapon { parent = (ThingWithComps)evt.launcher,
            props = new CompProperties_MiningBeam { cutDamage = 180, dismantleYieldFactor = .5f, deliverToEmitter = true } };
        harness.CreateClassProcessor(typeof(Patch_SalvageCutExplosion)).Patch();
        harness.Patch(AccessTools.DeclaredMethod(typeof(Building), nameof(Building.Destroy)),
            prefix: new HarmonyMethod(typeof(Program), nameof(CutCasketBaseDestroy)));
        harness.Patch(AccessTools.Method(typeof(GenDrop), nameof(GenDrop.TryDropSpawn)),
            prefix: new HarmonyMethod(typeof(Program), nameof(PlaceCasketOccupant)));
        harness.Patch(AccessTools.Method(typeof(CompExplosive), nameof(CompExplosive.PostDestroy)),
            prefix: new HarmonyMethod(typeof(Program), nameof(ObserveCutExplosion)) { priority = Priority.Last });
        try
        {
            cutExplosionCalls = 0;
            explosive.PostDestroy(DestroyMode.Deconstruct, map);
            Check(cutExplosionCalls == 1, "unrelated explosive destruction reaches its original callback");
            salvagePlacementSucceeds = true;
            int placementsBefore = salvagePlacements;
            SpaceImpactEffects.ApplyToDefender(evt, part);
            Check(casket.Destroyed && occupant.Spawned && !occupant.Destroyed && occupant.Map == map
                && casket.GetDirectlyHeldThings().Count == 0 && cutExplosionCalls == 1,
                "cutting releases the casket occupant and suppresses the destruction explosion");
            Check(salvagePlacements == placementsBefore + 1 && salvageOutputMap == evt.launcher.Map
                && lastSalvageOutput.stackCount == 5, "casket materials are delivered once beside the emitter");
        }
        finally { cutCasket = null; }
    }

    static void CheckMiningRoutes(UniverseManager universe, Harmony harness, SpaceCollision.ImpactEvent hit,
        ImpactOutcomePart shipBeam, ThingDef ore, Map asteroid, Map receiver)
    {
        var rock = (MiningTestRock)Raw(typeof(MiningTestRock));
        rock.def = TestBuildingDef("miningVein");
        rock.def.mineable = rock.def.destroyable = rock.def.useHitPoints = true;
        var mining = rock.def.building;
        mining.canBeDamagedByAttacks = false;
        mining.mineableThing = ore;
        mining.mineableYield = 40;
        mining.mineableDropChance = 1f;
        Current.Game.storyteller.difficulty.mineYieldFactor = 1f;
        hit.defenderBuilding = rock;
        hit.defenderIsSubstructure = false;
        hit.hitCell = new IntVec3(2, 0, 2);
        hit.hitDefenderLocal = new Vector2(2f, 2.5f);
        hit.incidentDir = Vector2.right;
        Set(typeof(Thing), rock, "positionInt", hit.hitCell);
        void SpawnRock(int hp)
        {
            rock.HitPoints = hp;
            Set(typeof(Thing), rock, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(asteroid));
        }

        salvagePlacementSucceeds = true;
        int placements = salvagePlacements;
        SpawnRock(61);
        SpaceImpactEffects.ApplyToDefender(hit, shipBeam);
        Check(rock.HitPoints == 1 && salvagePlacements == placements, "beam cuts a mineable vein before paying yield");
        SpaceImpactEffects.ApplyToDefender(hit, shipBeam);
        Check(rock.Destroyed && salvagePlacements == ++placements && salvageOutputMap == receiver
            && lastSalvageOutput.def == ore && lastSalvageOutput.stackCount == 20,
            "ship mining delivers half the vein yield to its own map");

        mining.mineableYieldWasteable = false;
        mining.mineableYield = 1;
        SpawnRock(1);
        SpaceImpactEffects.ApplyToDefender(hit, shipBeam);
        Check(salvagePlacements == ++placements && lastSalvageOutput.stackCount == 1,
            "a successful chunk drop yields one whole chunk");

        int originalMapCount = Find.Maps.Count;
        harness.Patch(AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[] {
            typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) }),
            prefix: new HarmonyMethod(typeof(Program), nameof(ReceiveSpawn)));
        harness.Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Label)),
            prefix: new HarmonyMethod(typeof(Program), nameof(SalvageLabel)));
        mining.mineableYieldWasteable = true;
        mining.mineableYield = 40;
        SpawnRock(1);
        hit.weapon = new CompSpaceWeapon { parent = (ThingWithComps)hit.launcher,
            props = new CompProperties_MiningBeam { cutDamage = 180 } };
        SpaceImpactEffects.ApplyToDefender(hit, shipBeam);
        Check(rock.Destroyed && salvagePlacements == placements, "EVA mining does not place ore on either ship or asteroid");
        Call(universe, "RunFrameActions");
        SpaceObject cargo = universe.objects.Single(obj => obj.IsScattered && obj.Payload?.Entity?.def == ore);
        Thing extractedOre = cargo.Payload.Entity;
        Check(Find.Maps.Count == originalMapCount && cargo.map == null, "EVA mining consumes no map slots");
        Check(cargo.IsScattered && extractedOre.def == ore && extractedOre.stackCount == 40
            && universe.Scatter.Candidates.Contains(cargo), "EVA ejects the full vein yield as a tractor candidate");
        Vector2 drift = SpaceTransform.UniverseTangentToMapDirection(hit.defender, asteroid, cargo.pose.position,
            cargo.pose.velocity);
        Check(drift.x < 0f && cargo.pose.velocity.magnitude >= 1.99f && cargo.pose.velocity.magnitude <= 4.01f,
            "EVA ore drifts out of the asteroid toward the incident side");
        cargo.Payload.GetDirectlyHeldThings().Remove(extractedOre);
        universe.UnregisterObject(cargo);
    }

    static void CheckFractionalCutReturn(Thing launcher, Map map, Building building)
    {
        var outputCell = CellRect.SingleCell(launcher.Position);
        var costs = building.def.costList;
        bool placementSucceeded = salvagePlacementSucceeds;
        var forced = building.def.building.forcedCostLeavings;
        building.def.costList = new List<ThingDefCountClass> { new ThingDefCountClass(costs[0].thingDef, 3) };
        CostListCalculator.Reset();
        salvagePlacementSucceeds = true;
        int zero = 0;
        int one = 0;
        try
        {
            for (int seed = 1; seed <= 64; seed++)
            {
                Rand.PushState(seed);
                int expected = GenMath.RoundRandom(.75f);
                Rand.PopState();
                Rand.PushState(seed);
                int before = salvagePlacements;
                using (new SalvageOutputScope(launcher, map, building, .5f))
                    GenLeaving.DoLeavingsFor(building, map, DestroyMode.Deconstruct, outputCell);
                Rand.PopState();
                if (expected == 0) zero++;
                else one++;
                if (salvagePlacements - before != expected || (expected > 0 && lastSalvageOutput.stackCount != expected))
                    throw new InvalidOperationException($"Cut yield differed from a single native rounding at seed {seed}.");
            }
            Check(zero > 0 && one > 0, "fractional building return matches one native roll before item creation");
            building.def.building.forcedCostLeavings = new List<ThingDef> { costs[0].thingDef };
            Rand.PushState(54);
            int expectedForced = GenMath.RoundRandom(1.5f);
            Rand.PopState();
            Rand.PushState(54);
            using (new SalvageOutputScope(launcher, map, building, .5f))
                GenLeaving.DoLeavingsFor(building, map, DestroyMode.Deconstruct, outputCell);
            Rand.PopState();
            Check(lastSalvageOutput.stackCount == expectedForced,
                $"forced native dismantling materials also use half their normal return ({lastSalvageOutput.stackCount}, expected {expectedForced})");
        }
        finally
        {
            building.def.building.forcedCostLeavings = forced;
            building.def.costList = costs;
            CostListCalculator.Reset();
            salvagePlacementSucceeds = placementSucceeded;
        }
    }

    static bool CombatLeavingsSpawnable(IntVec3 c, Map map, ref bool __result)
    {
        if (!useNativeDropLocation) return true;
        __result = c.InBounds(map);
        return false;
    }

    static bool CombatLeavingsRoom(ref Room __result)
    {
        if (!useNativeDropLocation) return true;
        __result = null;
        return false;
    }

    static IntVec3 combatLeavingsCell;
    static MapInfo combatGroundInfo;
    static Action<Map> combatExplosionDrop;

    // The production prefix/finalizer still run. Only the Unity blast cell body is replaced.
    static bool CombatLeavingsBlast(Explosion explosion)
    {
        combatExplosionDrop(explosion.Map);
        return false;
    }

    static bool SalvageFixtureTile(MapInfo __instance, ref PlanetTile __result)
    {
        __result = __instance == combatGroundInfo ? new PlanetTile(0, 0) : PlanetTile.Invalid;
        return false;
    }

    static bool RecordCombatLeavingsPlacement(Thing thing, IntVec3 loc, Map map, ref Thing resultingThing, ref bool __result)
    {
        if (!useNativeDropLocation) return true;
        combatLeavingsCell = loc;
        salvagePlacements++;
        salvageOutputMap = map;
        lastSalvageOutput = resultingThing = thing;
        __result = true;
        return false;
    }

    static void CheckCombatLeavings(UniverseManager universe, Harmony harness, ThingDef steel)
    {
        var scene = new Harmony("DeltaV.Regression.CombatLeavingsLocations");
        var oldGrid = Find.World.grid;
        try
        {
            var surfaceLayer = (PlanetLayer)Raw(typeof(PlanetLayer));
            Set(surfaceLayer, "def", new PlanetLayerDef { isSpace = false });
            Find.World.grid = (WorldGrid)Raw(typeof(WorldGrid));
            Set(Find.World.grid, "planetLayers", new Dictionary<int, PlanetLayer> { [0] = surfaceLayer });
            Map ground = TestMap(47010), groundPocket = TestMap(47011), space = TestMap(47012);
            ground.info.parent = (MapParent)Raw(typeof(MapParent));
            combatGroundInfo = ground.info;
            groundPocket.info.parent = new PocketMapParent { sourceMap = ground };
            groundPocket.info.isPocketMap = true;
            var spaceFrame = new MapSpaceObject(47012, space, new SpacePose(Vector3.forward, 0));
            universe.objects.Add(spaceFrame);
            foreach (Map map in new[] { ground, groundPocket, space })
            {
                map.edificeGrid = new EdificeGrid(map);
                map.areaManager = new AreaManager(map);
                var home = new Area_Home { areaManager = map.areaManager };
                Set(typeof(Area), home, "innerGrid", new BoolGrid(map));
                map.areaManager.AllAreas.Add(home);
            }
            var device = (Building)Raw(typeof(Building));
            device.def = TestBuildingDef("combatLeavingsEquipment");
            device.def.leaveResourcesWhenKilled = true;
            device.def.forceLeavingsAllowed = true;
            device.def.resourcesFractionWhenDeconstructed = .5f;
            device.def.costList = new List<ThingDefCountClass> { new ThingDefCountClass(steel, 21) };
            var terrain = (TerrainDef)Raw(typeof(TerrainDef));
            terrain.defName = "surfaceLeavingsTerrain";
            terrain.resourcesFractionWhenDeconstructed = .5f;
            terrain.costList = new List<ThingDefCountClass> { new ThingDefCountClass(steel, 7) };
            Set(typeof(Thing), device, "positionInt", new IntVec3(2, 0, 2));
            Set(typeof(Thing), device, "mapIndexOrState", (sbyte)-2);
            var position = device.Position;
            var allowed = position + IntVec3.East;
            if (steel.size == IntVec2.Zero) steel.size = new IntVec2(1, 1);

            scene.Patch(typeof(ModsConfig).TypeInitializer,
                prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
            scene.Patch(AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.CanSpawnAt)),
                prefix: new HarmonyMethod(typeof(Program), nameof(CombatLeavingsSpawnable)));
            scene.Patch(AccessTools.Method(typeof(RegionAndRoomQuery), nameof(RegionAndRoomQuery.RoomAt)),
                prefix: new HarmonyMethod(typeof(Program), nameof(CombatLeavingsRoom)));
            scene.Patch(AccessTools.Method(typeof(GenPlace), "TryPlaceDirect"),
                prefix: new HarmonyMethod(typeof(Program), nameof(RecordCombatLeavingsPlacement)));
            useNativeDropLocation = true;

            void DropNative(Map map, List<Thing> results, DestroyMode mode)
            {
                Rand.PushState(254);
                try
                {
                    GenLeaving.DoLeavingsFor(device, map, mode, CellRect.SingleCell(position),
                        cell => cell == allowed, results);
                }
                finally { Rand.PopState(); }
            }

            void Drop(Map map, List<Thing> results) => DropNative(map, results, DestroyMode.KillFinalize);

            (int count, IntVec3 cell) DropTerrain(Map map)
            {
                Rand.PushState(254);
                try { GenLeaving.DoLeavingsFor(terrain, position, map); }
                finally { Rand.PopState(); }
                return (lastSalvageOutput.stackCount, combatLeavingsCell);
            }

            var drops = new List<Thing>();
            var projectile = TestBuildingDef("combatLeavingsShell");
            projectile.comps.Add(new CompProperties_SpaceProjectile());
            void Blast(Map map, ThingDef originatingProjectile)
            {
                var explosion = (Explosion)Raw(typeof(Explosion));
                explosion.projectile = originatingProjectile;
                Set(typeof(Thing), explosion, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
                new DamageWorker().ExplosionAffectCell(explosion, position, new List<Thing>(), null, false);
            }
            var baselines = new Dictionary<Map, (int count, IntVec3 cell)>();
            var buildingReturns = new Dictionary<Map, (int count, IntVec3 cell)>();
            var terrainReturns = new Dictionary<Map, (int count, IntVec3 cell)>();
            harness.Unpatch(AccessTools.Method(typeof(GenLeaving), nameof(GenLeaving.DoLeavingsFor),
                new[] { typeof(Thing), typeof(Map), typeof(DestroyMode), typeof(CellRect),
                    typeof(Predicate<IntVec3>), typeof(List<Thing>) }), HarmonyPatchType.All, harness.Id);
            harness.Unpatch(AccessTools.Method(typeof(GenLeaving), nameof(GenLeaving.DoLeavingsFor),
                new[] { typeof(TerrainDef), typeof(IntVec3), typeof(Map) }), HarmonyPatchType.All, harness.Id);
            foreach (Map map in new[] { ground, groundPocket })
            {
                Drop(map, drops);
                baselines.Add(map, (drops.Single().stackCount, combatLeavingsCell));
                Check(combatLeavingsCell == allowed && salvageOutputMap == map, "vanilla finds the allowed neighboring cell");
                drops.Clear();
                DropNative(map, drops, DestroyMode.Deconstruct);
                buildingReturns.Add(map, (drops.Single().stackCount, combatLeavingsCell));
                drops.Clear();
                terrainReturns.Add(map, DropTerrain(map));
            }
            harness.CreateClassProcessor(typeof(Patch_CombatLeavings)).Patch();
            harness.CreateClassProcessor(typeof(Patch_CombatExplosionLeavings)).Patch();
            harness.CreateClassProcessor(typeof(Patch_SalvageForcedBuildingYield)).Patch();
            harness.CreateClassProcessor(typeof(Patch_SalvageTerrainYield)).Patch();
            combatExplosionDrop = map => Drop(map, drops);
            scene.Patch(AccessTools.Method(typeof(DamageWorker), nameof(DamageWorker.ExplosionAffectCell)),
                prefix: new HarmonyMethod(typeof(Program), nameof(CombatLeavingsBlast)) { priority = Priority.Last });
            foreach (Map map in new[] { ground, groundPocket })
            {
                int placementsBefore = salvagePlacements;
                Blast(map, projectile);
                Check(salvagePlacements == placementsBefore + 1 && salvageOutputMap == map
                    && (drops.Single().stackCount, combatLeavingsCell) == baselines[map],
                    map == ground ? "ordinary surface kill keeps vanilla amount and final cell"
                        : "RimExodus surface pocket kill keeps vanilla amount and final cell");
                drops.Clear();
                string surface = map == ground ? "ordinary surface" : "RimExodus surface pocket";
                DropNative(map, drops, DestroyMode.Deconstruct);
                Check(salvagePlacements == placementsBefore + 2 && salvageOutputMap == map
                    && (drops.Single().stackCount, combatLeavingsCell) == buildingReturns[map],
                    $"{surface} building dismantling keeps vanilla amount and final cell");
                drops.Clear();
                Check(DropTerrain(map) == terrainReturns[map] && salvagePlacements == placementsBefore + 3
                    && salvageOutputMap == map, $"{surface} terrain dismantling keeps vanilla amount and final cell");
            }

            var existing = (Thing)Raw(typeof(ReceivingItem));
            existing.def = steel;
            existing.stackCount = 9;
            ProbeSpawnCargo(existing, position, space);
            int before = salvagePlacements;
            using (var scope = CombatLeavingsScope.Enter(space))
            {
                Check(scope != null, "registered space pocket permits combat ejection");
                Drop(space, drops);
                Check(salvagePlacements == before && drops.Single().stackCount == baselines[ground].count
                    && existing.stackCount == 9 && existing.Map == space,
                    "actual space kill drop is captured before placement without disturbing the deck stack");
                device.def.leaveResourcesWhenKilled = false;
                Drop(space, new List<Thing>());
            }
            Call(universe, "RunFrameActions");
            var cargo = universe.Scatter.Candidates.Single(candidate => candidate.Payload?.Entity == drops[0]);
            Check(universe.Scatter.Candidates.Count() == 1 && !cargo.Payload.Entity.Spawned,
                "positive yield appears once as tractor cargo, zero yield creates no object");
            cargo.Payload.GetDirectlyHeldThings().Remove(drops[0]);
            universe.UnregisterObject(cargo);
            drops.Clear();
            device.def.leaveResourcesWhenKilled = true;

            before = salvagePlacements;
            Blast(space, null);
            Check(salvagePlacements == before + 1 && drops.Count == 1,
                "unrelated space explosions use the native drop path");
            drops.Clear();
            Blast(space, projectile);
            Thing delayedDrop = drops.Single();
            drops.Clear();
            Drop(space, drops);
            Check(salvagePlacements == before + 2 && drops.Count == 1,
                "the delayed blast ejects once and restores the regular drop path on return");
            Call(universe, "RunFrameActions");
            cargo = universe.Scatter.Candidates.Single();
            Check(cargo.Payload.Entity == delayedDrop && delayedDrop.stackCount == baselines[ground].count,
                "deferred shell blast publishes the unmodified native yield as tractor cargo");
            cargo.Payload.GetDirectlyHeldThings().Remove(delayedDrop);
            universe.UnregisterObject(cargo);
        }
        finally
        {
            useNativeDropLocation = false;
            combatGroundInfo = null;
            combatExplosionDrop = null;
            scene.UnpatchAll(scene.Id);
            Find.World.grid = oldGrid;
        }
    }

    static void CheckSmallMapDisposal(UniverseManager universe)
    {
        Map map = TestMap(47009, 12);
        map.listerBuildings = new ListerBuildings();
        map.mapPawns = new MapPawns(map);
        map.passingShipManager = new PassingShipManager(map);
        var obj = new MapSpaceObject(47009, map, new SpacePose(Vector3.forward, 0));
        bool IsDisposable() => (bool)Call(universe.MapStates, "IsDisposableContainer", obj, map);

        TerrainDef ground = map.terrainGrid.topGrid[0];
        for (int i = 64; i < map.terrainGrid.topGrid.Length; i++) map.terrainGrid.topGrid[i] = TerrainDefOf.Space;
        for (int i = 1; i < 64; i++) map.terrainGrid.topGrid[i] = ground;
        Check(IsDisposable(), "64 support cells count as a small wreck despite map padding");
        map.terrainGrid.topGrid[64] = ground;
        SubstructureOwnership.For(map).UpdateSupportCell(map.cellIndices.IndexToCell(64));
        Check(!IsDisposable(), "65 support cells retain normal archiving");
        obj.sourcePrefabPath = "Sector/Asteroids/SectorAsteroid.xml";
        Check(!IsDisposable(), "a generation prefab name does not override actual large-structure disposal");
        map.listerBuildings.allBuildingsColonist.Add((Building)Raw(typeof(Building)));
        Check(!IsDisposable(), "a player building protects an asteroid from distance disposal");
        map.listerBuildings.allBuildingsColonist.Clear();
        map.terrainGrid.topGrid[64] = TerrainDefOf.Space;
        SubstructureOwnership.For(map).UpdateSupportCell(map.cellIndices.IndexToCell(64));
        obj.sourcePrefabPath = "Sector/Stations/SectorAbandonedSatellite.xml";
        Check(IsDisposable(), "a small empty structure is disposable independently of its generation label");
    }
}
