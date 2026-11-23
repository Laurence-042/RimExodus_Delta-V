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
            if (owner != null || map?.Parent is ShipPocketParent_DeltaV || universe?.ShipStructure.Tracks(map) == true)
            {
                var ownership = SubstructureOwnership.TryGet(map);
                if (building != null) universe?.ShipStructure.NotifyBuilding(map, building, previousFootprint);
                if (terrainCell.HasValue) ownership?.UpdateSupportCell(terrainCell.Value);
            }
            if (owner != null)
            {
                if (!terrainCell.HasValue && building == null) universe.ShipStructure.Changed(map);
                universe.MapStates.QueueStructureCheck(map);
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

    [HarmonyPatch(typeof(Thing), nameof(Thing.Rotation), MethodType.Setter)]
    internal static class Patch_ShipStructure_Rotation
    {
        private static void Prefix(Thing __instance, Rot4 value, out (Rot4 rotation, CellRect? footprint) __state)
        {
            Rot4 rotation = __instance.Rotation;
            __state = (rotation, rotation != value && __instance is Building && __instance.Spawned
                ? __instance.OccupiedRect() : (CellRect?)null);
        }

        internal static void Postfix(Thing __instance, (Rot4 rotation, CellRect? footprint) __state)
        {
            if (__state.footprint.HasValue && __state.rotation != __instance.Rotation && __instance is Building building)
                ShipStructureChanges.Notify(building.Map, building, null, __state.footprint);
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.Position), MethodType.Setter)]
    internal static class Patch_ShipStructure_Position
    {
        private static void Prefix(Thing __instance, IntVec3 value, out CellRect? __state) =>
            __state = __instance is Building && __instance.Spawned && __instance.Position != value
                ? __instance.OccupiedRect() : (CellRect?)null;

        private static void Postfix(Thing __instance, CellRect? __state)
        {
            if (__state.HasValue && __instance is Building building)
                ShipStructureChanges.Notify(building.Map, building, null, __state);
        }
    }

    [HarmonyPatch(typeof(TerrainGrid), "DoTerrainChangedEffects")]
    internal static class Patch_ShipStructure_Terrain
    {
        private static void Postfix(TerrainGrid __instance, IntVec3 c, TerrainDef newTerr,
            TerrainDef oldTerr, TerrainDef oldFoundation)
        {
            Map map = TerrainGridOwnershipAccess.MapOf(__instance);
            if (newTerr == oldTerr && (oldFoundation == null || oldFoundation == SubstructureOwnership.FoundationAt(map, c))) return;
            UniverseManager universe = ShipStructureChanges.Notify(map, terrainCell: c);
            if (universe == null) return;
            if (oldFoundation == null && (newTerr?.isFoundation != true || oldTerr?.tempTerrain != null))
            {
                int owner = SubstructureOwnership.TryGet(map)?.OwnerAt(c) ?? 0;
                universe.ShipStructure.NotifyCell(map, c, ShipStructureAspect.Terrain, oldTerr, newTerr, owner, owner);
            }
            universe.NotifySupportChanged(map, c);
        }
    }

    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetUnderTerrain))]
    internal static class Patch_ShipStructure_UnderTerrain
    {
        private static void Prefix(TerrainGrid __instance, IntVec3 c, out TerrainDef __state)
        {
            Map map = TerrainGridOwnershipAccess.MapOf(__instance);
            __state = map != null && c.InBounds(map) ? __instance.UnderTerrainAt(c) : null;
        }

        private static void Postfix(TerrainGrid __instance, IntVec3 c, TerrainDef __state)
        {
            Map map = TerrainGridOwnershipAccess.MapOf(__instance);
            if (map == null || !c.InBounds(map)) return;
            UniverseManager universe = ShipStructureChanges.Notify(map, terrainCell: c);
            int owner = SubstructureOwnership.TryGet(map)?.OwnerAt(c) ?? 0;
            universe?.ShipStructure.NotifyCell(map, c, ShipStructureAspect.Terrain, __state, __instance.UnderTerrainAt(c), owner, owner);
            universe?.NotifySupportChanged(map, c);
        }
    }
}
