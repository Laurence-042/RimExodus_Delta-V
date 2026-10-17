using HarmonyLib;
using Verse;

namespace DeltaV
{
    /// <summary>船体能力、对象角色、物理和碰撞共用结构版本；只在纳管地图的结构事件失效。</summary>
    internal static class ShipStructureChanges
    {
        internal static void Notify(Map map)
        {
            if (map != null && Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map) != null)
                SubstructureOwnership.BumpStructureVersion();
        }
    }

    [HarmonyPatch(typeof(Building), nameof(Building.SpawnSetup))]
    internal static class Patch_ShipStructure_Spawn
    {
        private static void Postfix(Map map) => ShipStructureChanges.Notify(map);
    }

    [HarmonyPatch(typeof(Building), nameof(Building.DeSpawn))]
    internal static class Patch_ShipStructure_Despawn
    {
        private static void Prefix(Building __instance, out Map __state) => __state = __instance.Map;
        private static void Postfix(Map __state) => ShipStructureChanges.Notify(__state);
    }

    [HarmonyPatch(typeof(TerrainGrid), "DoTerrainChangedEffects")]
    internal static class Patch_ShipStructure_Terrain
    {
        private static void Postfix(TerrainGrid __instance) =>
            ShipStructureChanges.Notify(TerrainGridOwnershipAccess.MapOf(__instance));
    }
}
