using HarmonyLib;
using Verse;

namespace DeltaV
{
    // GasGrid's Rand.Range(0, Area/2) consumes zero draws for Area=1. MapPreview's
    // FillComponents check expects the usual one draw, even for real pocket maps.
    [HarmonyPatch(typeof(GasGrid), MethodType.Constructor, new[] { typeof(Map) })]
    internal static class Patch_SingleCellMapGeneration
    {
        private static void Postfix(Map map)
        {
            if (map.Area == 1 && map.Parent is ShipPocketParent_DeltaV
                && ReferenceEquals(MapGenerator.mapBeingGenerated, map))
                _ = Rand.Int;
        }
    }
}
