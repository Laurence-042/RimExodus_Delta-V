using HarmonyLib;
using Verse;

namespace DeltaV
{
    /// <summary>船体能力、对象角色、物理和碰撞共用结构版本；只在纳管地图的结构事件失效。</summary>
    internal static class ShipStructureChanges
    {
        internal static void Notify(Map map)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject owner = map == null ? null : universe?.FrameOf(map);
            if (owner?.baseTag == SpaceObjectTag.Projectile)
            {
                // A projectile building may disappear before its container returns to the pool.
                SpaceCollision.Forget(map);
                return;
            }
            if (owner != null)
            {
                SubstructureOwnership.BumpStructureVersion(map);
                universe.Lifecycle.QueueStructureCheck(map);
            }
        }
    }

    [HarmonyPatch(typeof(Building), nameof(Building.SpawnSetup))]
    internal static class Patch_ShipStructure_Spawn
    {
        private static void Postfix(Building __instance, Map map)
        {
            ShipStructureChanges.Notify(map);
            // PostSpawnSetup runs before this version bump. Resolve newly connected parts only now.
            var attachment = __instance.TryGetComp<CompSpaceWeaponAttachment>();
            var weapon = __instance.TryGetComp<CompSpaceWeapon>() ?? attachment?.HostWeapon;
            weapon?.parent.TryGetComp<CompRailgunReadinessAppearance>()?.Rebind();
            var segment = __instance.TryGetComp<CompSpaceRailgun>() ?? attachment?.HostRailgun;
            segment?.ConnectedWeapon()?.parent.TryGetComp<CompRailgunReadinessAppearance>()?.Rebind();
        }
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
        private static void Postfix(TerrainGrid __instance, IntVec3 c, TerrainDef newTerr,
            TerrainDef oldFoundation)
        {
            Map map = TerrainGridOwnershipAccess.MapOf(__instance);
            ShipStructureChanges.Notify(map);
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (map == null || universe?.FrameOf(map) == null) return;
            if (oldFoundation != null || newTerr?.isFoundation == true)
                universe.NotifyFoundationChanged(map, c);
            universe.NotifySupportChanged(map, c);
        }
    }
}
