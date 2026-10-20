using HarmonyLib;
using Verse;

namespace DeltaV
{
    /// <summary>纳管地图上的基架及其地板必须先由存活建筑承伤；搬运和拆除不走此门禁。</summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.Notify_FoundationDestroyed))]
    internal static class Patch_SubstructureDamage
    {
        internal static bool AllowTerrainDamage(Map map, IntVec3 cell) =>
            Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map) == null
            || !SubstructureOwnership.HasFoundation(map, cell)
            || SubstructureDamageUtility.CanDamage(map, cell);

        private static bool Prefix(TerrainGrid __instance, IntVec3 c, out bool __state)
        {
            Map map = TerrainGridOwnershipAccess.MapOf(__instance);
            __state = false;
            if (Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map) == null
                || !SubstructureOwnership.HasFoundation(map, c)) return true;
            __state = SubstructureDamageUtility.CanDamage(map, c);
            return __state;
        }

        private static void Postfix(TerrainGrid __instance, IntVec3 c, bool __state)
        {
            if (__state)
                SubstructureDamageUtility.CompleteDestruction(TerrainGridOwnershipAccess.MapOf(__instance), c);
        }
    }

    [HarmonyPatch(typeof(DamageWorker), "ExplosionDamageTerrain")]
    internal static class Patch_SubstructureExplosionDamage
    {
        private static bool Prefix(Explosion explosion, IntVec3 c) =>
            Patch_SubstructureDamage.AllowTerrainDamage(explosion.Map, c);
    }
}
