using System;
using System.IO;
using System.Linq;
using DeltaV;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static bool ReadRegressionPrefabDirectory(ref string __result)
    {
        __result = Path.Combine(Path.GetTempPath(), "DeltaVRegressionPrefabs");
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
}
