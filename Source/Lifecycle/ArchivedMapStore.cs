using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    /// <summary>Delta-V performance scopes around REF whole-map archives.</summary>
    internal static class ArchivedMapStore
    {
        public static bool TryCapture(Map map, out string data)
        {
            using var timing = Current.Game?.GetComponent<UniverseManager>()?.Performance.Measure(PerformanceStage.ArchiveCapture) ?? default;
            return RimExodus.Maps.MapPrefabs.CaptureWholeMap(map, out data);
        }
        public static bool CanUnloadLosslessly(Map map) => RimExodus.Maps.MapArchive.CanUnloadLosslessly(map);
        public static bool TryUnload(Map map, bool preserveExternalEvents = true)
            => RimExodus.Maps.MapArchive.TryUnload(map, preserveExternalEvents);
        public static bool TryRestore(string data, int expectedMapId, PocketMapParent expectedParent, out Map map)
        {
            using var timing = Current.Game?.GetComponent<UniverseManager>()?.Performance.Measure(PerformanceStage.ArchiveRestore) ?? default;
            return RimExodus.Maps.MapPrefabs.RestoreWholeMap(data, expectedMapId, expectedParent, out map,
                ArchivedSpaceObject.TolerateUnspawned);
        }
    }
}
