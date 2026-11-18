using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>船体能力、对象角色、物理和碰撞共用结构版本；只在纳管地图的结构事件失效。</summary>
    internal static class ShipStructureChanges
    {
        internal static UniverseManager Notify(Map map, Building building = null, IntVec3? terrainCell = null) =>
            Notify(map, building, terrainCell, null);

        internal static UniverseManager Notify(Map map, Building building, IntVec3? terrainCell,
            CellRect? previousFootprint)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject owner = map == null ? null : universe?.FrameOf(map);
            if (owner?.baseTag == SpaceObjectTag.Projectile)
            {
                // A projectile building may disappear before its container returns to the pool.
                SpaceCollision.Forget(map);
                return null;
            }
            if (owner != null || map?.Parent is ShipPocketParent_DeltaV)
            {
                var ownership = SubstructureOwnership.TryGet(map);
                if (building != null) ownership?.UpdateSpawnedBuilding(building);
                if (terrainCell.HasValue) ownership?.UpdateSupportCell(terrainCell.Value);
            }
            if (owner != null)
            {
                if (building != null)
                {
                    SubstructureOwnership.BumpStructureVersion(map, previousFootprint ?? building.OccupiedRect());
                    if (building.TryGetComp<CompProjectileInterceptor>() != null) SpaceCollision.InvalidateShield(map);
                }
                else if (terrainCell.HasValue)
                    SubstructureOwnership.BumpStructureVersion(map, CellRect.SingleCell(terrainCell.Value));
                else SubstructureOwnership.BumpStructureVersion(map);
                universe.Lifecycle.QueueStructureCheck(map);
                universe.Weapons.QueuePower(map);
            }
            return owner != null ? universe : null;
        }
    }

    [HarmonyPatch(typeof(Building), nameof(Building.SpawnSetup))]
    internal static class Patch_ShipStructure_Spawn
    {
        private static void Postfix(Building __instance, Map map)
        {
            ShipStructureChanges.Notify(map, building: __instance);
        }
    }

    [HarmonyPatch(typeof(Building), nameof(Building.DeSpawn))]
    internal static class Patch_ShipStructure_Despawn
    {
        private static void Prefix(Building __instance, out (Map map, CellRect footprint) __state) =>
            __state = (__instance.Map, __instance.OccupiedRect());
        private static void Postfix(Building __instance, (Map map, CellRect footprint) __state) =>
            ShipStructureChanges.Notify(__state.map, __instance, null, __state.footprint);
    }

    [HarmonyPatch(typeof(TerrainGrid), "DoTerrainChangedEffects")]
    internal static class Patch_ShipStructure_Terrain
    {
        private static void Postfix(TerrainGrid __instance, IntVec3 c, TerrainDef newTerr,
            TerrainDef oldFoundation)
        {
            Map map = TerrainGridOwnershipAccess.MapOf(__instance);
            UniverseManager universe = ShipStructureChanges.Notify(map, terrainCell: c);
            if (universe == null) return;
            if (oldFoundation != null || newTerr?.isFoundation == true)
                universe.NotifyFoundationChanged(map, c);
            universe.NotifySupportChanged(map, c);
        }
    }

    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetUnderTerrain))]
    internal static class Patch_ShipStructure_UnderTerrain
    {
        private static void Postfix(TerrainGrid __instance, IntVec3 c)
        {
            Map map = TerrainGridOwnershipAccess.MapOf(__instance);
            if (map == null || !c.InBounds(map)) return;
            ShipStructureChanges.Notify(map, terrainCell: c)?.NotifySupportChanged(map, c);
        }
    }
}
