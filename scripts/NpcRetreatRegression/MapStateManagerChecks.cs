using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using DeltaV;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

internal sealed class RegressionLandingMapParent : Settlement
{
    internal int removalProbeCalls;

    internal RegressionLandingMapParent(string defName, int tile, int creationTick)
    {
        def = new WorldObjectDef { defName = defName, label = defName, canBePlayerHome = true };
        creationGameTicks = creationTick;
        var field = HarmonyLib.AccessTools.Field(typeof(WorldObject), "tile");
        var write = new DynamicMethod("InitializeLandingWorksiteTile", typeof(void),
            new[] { field.DeclaringType, field.FieldType }, typeof(RegressionLandingMapParent), true);
        var il = write.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        write.CreateDelegate<Action<WorldObject, PlanetTile>>()(this, new PlanetTile(tile, 0));
    }

    public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
    {
        removalProbeCalls++;
        alsoRemoveWorldObject = false;
        return false;
    }
}

internal static partial class Program
{
    static Map retiredLandingWorksiteMap;
    static MapParent retiredLandingWorksiteParent;

    static bool ReadRegressionPrefabDirectory(ref string __result)
    {
        __result = Path.Combine(Path.GetTempPath(), "DeltaVRegressionPrefabs");
        return false;
    }

    static bool RemoveLandingWorksiteMap(Map map, bool notifyPlayer)
    {
        retiredLandingWorksiteMap = map;
        Find.Maps.Remove(map);
        return false;
    }

    static bool DestroyLandingWorksiteParent(MapParent __instance)
    {
        retiredLandingWorksiteParent = __instance;
        OrbitalPoiRegistry.NotifyDestroyed(__instance);
        return false;
    }

    static bool EmptyLandingWorksiteHasNoBlockingBuilding(ref bool __result)
    {
        __result = false;
        return false;
    }

    static bool EmptyLandingWorksiteHasNoBlockingPawn(ref bool __result)
    {
        __result = false;
        return false;
    }

    static bool EmptyLandingWorksiteHasNoIncomingTransport(ref bool __result)
    {
        __result = false;
        return false;
    }

    static void CheckMapStateManager(UniverseManager universe)
    {
        int previousTick = Find.TickManager.TicksGame;
        int previousDays = DeltaVMod.CurrentSettings.exactMapRetentionDays;
        var prefabCatalogPatch = new HarmonyLib.Harmony("DeltaV.Regression.PrefabCatalog");
        prefabCatalogPatch.Patch(HarmonyLib.AccessTools.PropertyGetter(typeof(MapPrefab),
                nameof(MapPrefab.PrefabDirectory)),
            prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(ReadRegressionPrefabDirectory)));
        var marker = new PoiState(58901, "retention fixture", Vector3.forward, 0f, 123)
        {
            sourcePrefabPath = "Sector/Stations/SectorTradeStation.xml",
            sourcePrefabCategoryPath = new System.Collections.Generic.List<string> { "structures", "trade-station" },
            sourcePrefabSeed = 123,
            externalPrefabGenerationCompleted = true
        };
        try
        {
            universe.RegisterObject(marker);
            Check(universe.MapStates.StateOf(marker) == SpaceMapStateKind.Unmaterialized
                && universe.MapStates.RetentionInfo(marker.objectId).coldRebuild,
                "map-state manager derives cold unmaterialized state from the representation and one-shot fact");
            var copied = new SpaceObject();
            marker.CopyFactsTo(copied);
            Check(copied.objectId == marker.objectId && copied.sourcePrefabPath == marker.sourcePrefabPath
                && copied.sourcePrefabSeed == 123 && copied.externalPrefabGenerationCompleted
                && copied.sourcePrefabCategoryPath.SequenceEqual(marker.sourcePrefabCategoryPath),
                "SpaceObject identity and prefab provenance survive representation replacement");

            var content = (ModContentPack)Raw(typeof(ModContentPack));
            typeof(ModContentPack).GetField("rootDirInt", Private)
                .SetValue(content, new DirectoryInfo(Directory.GetCurrentDirectory()));
            typeof(Mod).GetField("intContent", Private)
                .SetValue(LoadedModManager.GetMod<DeltaVMod>(), content);
            var roots = SectorGenerationTable.Defaults();
            string categoryFallback = SectorGenerationTable.ResolveFrozenPrefab(roots,
                "Sector/Stations/RemovedTradeStation.xml", marker.sourcePrefabCategoryPath, marker.sourcePrefabSeed);
            Check(categoryFallback == "Sector/Stations/SectorTradeStation.xml",
                "missing frozen prefab falls back to the deepest surviving category");
            string globalFallback = SectorGenerationTable.ResolveFrozenPrefab(roots,
                "Sector/Stations/RemovedTradeStation.xml", new[] { "removed", "category" }, marker.sourcePrefabSeed);
            Check(!globalFallback.NullOrEmpty()
                && globalFallback == SectorGenerationTable.ResolveFrozenPrefab(roots,
                    "Sector/Stations/RemovedTradeStation.xml", new[] { "removed", "category" }, marker.sourcePrefabSeed),
                "missing category falls back to the global pool deterministically");

            Set(Find.TickManager, "ticksGameInt", 120000);
            DeltaVMod.CurrentSettings.exactMapRetentionDays = 2;
            universe.MapStates.SetManualKeepExact(marker.objectId, true);
            SpaceMapRetentionInfo kept = universe.MapStates.RetentionInfo(marker.objectId);
            Check(kept.manualKeepExact && kept.lastMeaningfulPlayerContactTick == 120000,
                "manual exact retention is manager-owned and refreshes meaningful contact");
            universe.MapStates.SetManualKeepExact(marker.objectId, false);
            SpaceMapRetentionInfo automatic = universe.MapStates.RetentionInfo(marker.objectId);
            Check(automatic.exactRetentionExpiryTick == 240000,
                "exact-retention expiry is derived from game ticks and current world setting");
            Set(Find.TickManager, "ticksGameInt", 240000);
            Check((bool)Call(universe.MapStates, "ExactRetentionExpired", marker),
                "retention expires at the derived game tick");

            Check((bool)Call(universe.MapStates, "TryBeginTransition", marker,
                    MapTransitionKind.Sleep, null)
                && !(bool)Call(universe.MapStates, "TryBeginTransition", marker,
                    MapTransitionKind.Sleep, null),
                "one manager transaction is admitted per object and duplicate intent is merged");
            Check((bool)Call(universe.MapStates, "TryBeginTransition", marker,
                    MapTransitionKind.Restore, null)
                && universe.MapStates.PendingTransitionName(marker.objectId) == "Restore",
                "higher-priority restoration replaces a lower-priority pending transition");
            Call(universe.MapStates, "CancelTransition", marker.objectId, MapTransitionKind.Restore);

            Check(universe.MapStates.RequestPermanentRetirement(marker, "regression fixture"),
                "permanent retirement is accepted only through the manager");
            Call(universe, "RunFrameActions");
            Check(universe.ObjectById(marker.objectId) == null
                && universe.MapStates.StateOf(marker.objectId) == SpaceMapStateKind.Deleted,
                "permanent retirement unregisters the object without a second identity or tombstone");
            CheckEmptyLandingWorksiteRetirement(universe);
        }
        finally
        {
            if (universe.ObjectById(marker.objectId) is SpaceObject remaining)
                universe.UnregisterObject(remaining);
            Set(Find.TickManager, "ticksGameInt", previousTick);
            DeltaVMod.CurrentSettings.exactMapRetentionDays = previousDays;
            prefabCatalogPatch.UnpatchAll(prefabCatalogPatch.Id);
        }
    }

    static void CheckEmptyLandingWorksiteRetirement(UniverseManager universe)
    {
        var harmony = new HarmonyLib.Harmony("DeltaV.Regression.EmptyLandingWorksite");
        sbyte oldMapIndex = Current.Game.currentMapIndex;
        WorldGrid oldGrid = Find.World.grid;
        var fixtureLayer = (PlanetLayer)Raw(typeof(PlanetLayer));
        Set(fixtureLayer, "def", new PlanetLayerDef { isSpace = true });
        Find.World.grid = (WorldGrid)Raw(typeof(WorldGrid));
        Set(Find.World.grid, "planetLayers", new Dictionary<int, PlanetLayer> { [0] = fixtureLayer });
        var map = TestMap(58902);
        map.generationTick = 24680;
        var parent = new RegressionLandingMapParent("SpaceSettlement", 0, map.generationTick);
        map.info.parent = parent;
        Array.Fill(map.terrainGrid.topGrid, TerrainDefOf.Space);
        map.wasSpawnedViaGravShipLanding = true;
        map.mapPawns = new MapPawns(map);
        var native = new PoiState
        {
            objectId = 58902,
            kind = PoiKind.NativeParent,
            nativeParent = parent,
            map = map,
            envLabel = "landing worksite",
            pose = new SpacePose(Vector3.forward, 0f),
            baseTag = SpaceObjectTag.Marker | SpaceObjectTag.GravityRegion
        };
        retiredLandingWorksiteMap = null;
        retiredLandingWorksiteParent = null;
        Current.Game.currentMapIndex = -1;
        universe.RegisterObject(native);
        DeltaVPocketMapMetadata.MarkWorksite(map);
        try
        {
            Check(SpaceMapGovernance.IsEmptyLandingWorksite(map, universe),
                "empty landing-created SpaceSettlement is eligible for retirement despite its home origin");
            harmony.Patch(HarmonyLib.AccessTools.PropertyGetter(typeof(Map), nameof(Map.AnyBuildingBlockingMapRemoval)),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(EmptyLandingWorksiteHasNoBlockingBuilding)));
            harmony.Patch(HarmonyLib.AccessTools.PropertyGetter(typeof(MapPawns), nameof(MapPawns.AnyPawnBlockingMapRemoval)),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(EmptyLandingWorksiteHasNoBlockingPawn)));
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(TransporterUtility),
                    nameof(TransporterUtility.IncomingTransporterPreventingMapRemoval)),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(EmptyLandingWorksiteHasNoIncomingTransport)));
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Game), nameof(Game.DeinitAndRemoveMap),
                    new[] { typeof(Map), typeof(bool) }),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(RemoveLandingWorksiteMap)));
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(MapParent), nameof(MapParent.Destroy)),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(DestroyLandingWorksiteParent)));
            Check(universe.MapStates.TryRemoveEmptyLandingWorksite(map)
                && retiredLandingWorksiteMap == map && retiredLandingWorksiteParent == parent
                && !Find.Maps.Contains(map) && universe.ObjectById(native.objectId) == null
                && parent.removalProbeCalls == 0,
                "retiring an empty landing Home bypasses only Settlement's permanent home veto and removes all mirrors");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            Current.Game.currentMapIndex = oldMapIndex;
            if (universe.ObjectById(native.objectId) == native)
            {
                native.map = null;
                universe.UnregisterObject(native);
            }
            Find.Maps.Remove(map);
            Find.World.grid = oldGrid;
        }
    }
}
