using System;
using System.Collections.Generic;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static int salvageMessages, salvagePlacements, salvageWarnings;
    static bool salvagePlacementSucceeds;
    static Map salvageOutputMap;
    static Thing lastSalvageOutput;

    // No Unity scene: substitute only final placement and UI, retaining production cutting,
    // vanilla material generation/ThingOwner/GenDrop and the actual Harmony result hook.
    static bool PlaceSalvage(Thing thing, Map map, ref Thing lastResultingThing, ref bool __result)
    {
        salvagePlacements++;
        salvageOutputMap = map;
        lastSalvageOutput = thing;
        lastResultingThing = thing;
        __result = salvagePlacementSucceeds;
        return false;
    }

    static bool MakeSalvage(ThingDef def, ref Thing __result)
    {
        var thing = (Thing)Raw(typeof(Thing));
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
        public override int HitPoints { get => hp; set => hp = value; }
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Check(mode == DestroyMode.Vanish, "cut removes target without duplicate native source drops");
            destroyCalls++;
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
        Thing launcher = AddTestHull(sourceMap, player, 47003).parent;
        var target = new MapSpaceObject(47004, targetMap, new SpacePose(Vector3.forward, 0));
        universe.objects.Add(target);
        var steel = (ThingDef)Raw(typeof(ThingDef));
        steel.defName = "salvageSteel"; steel.label = "steel";
        steel.category = ThingCategory.Item; steel.stackLimit = 75;
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
            defenderBuilding = building, hitCell = building.Position, damageScale = 1 };
        var part = new ImpactOutcomePart { kind = ImpactEffectKind.Salvage, damage = 60 };
        var oldState = Current.ProgramState;
        Current.ProgramState = ProgramState.Playing;
        Current.Game.storyteller = (Storyteller)Raw(typeof(Storyteller));
        Current.Game.storyteller.difficulty = new Difficulty();
        try
        {
            harness.Patch(Patch_SalvageOutput_Placement.TargetMethod(),
                prefix: new HarmonyMethod(typeof(Program), nameof(PlaceSalvage)));
            harness.CreateClassProcessor(typeof(Patch_SalvageOutput_Placement)).Patch();
            harness.CreateClassProcessor(typeof(Patch_SalvageOutput_Warning)).Patch();
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
            SpaceImpactEffects.ApplyToDefender(evt, part);
            Check(building.Destroyed && building.destroyCalls == 1 && salvagePlacements == 1
                && salvageOutputMap == sourceMap && lastSalvageOutput.def == steel
                && lastSalvageOutput.stackCount == 10,
                "final cut destroys target and vanilla deconstruction generates half cost on receiver");
            Check(salvageMessages == 1 && salvageWarnings == 0,
                $"real drop failure produces one loss notice, no warning spam (messages={salvageMessages}, warnings={salvageWarnings})");
            SpaceImpactEffects.ApplyToDefender(evt, part);
            Check(salvagePlacements == 1, "destroyed target cannot pay materials twice");

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
            Thing conduit = AddTestBuilding(targetMap, enemy, 47007, conduitDef, new CompPowerTransmitter());
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
                && salvagePlacements == dropsBefore + 1 && lastSalvageOutput.stackCount == 4
                && salvageOutputMap == sourceMap,
                "actual salvage removes conduit-covered foundation and hands its materials to native receiver placement");
            salvagePlacementSucceeds = false;
            GenPlace.TryPlaceThing(lastSalvageOutput, launcher.Position, null, ThingPlaceMode.Near);
            Check(salvageMessages == 5, "null-map failure outside mining scope remains untouched");
        }
        finally
        {
            harness.UnpatchAll(harness.Id);
            Current.ProgramState = oldState;
            universe.objects.Clear();
            Find.Maps.Clear();
            ShipRegistry.ClearCaches();
            universe.Weapons.ResetFlow();
        }
    }
}
