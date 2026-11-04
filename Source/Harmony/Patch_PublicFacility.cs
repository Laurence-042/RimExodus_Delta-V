using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    [HarmonyPatch]
    internal static class Patch_PublicFacilityConstruction
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(GenConstruct), "CanPlaceBlueprintAt_NewTemp")
            ?? AccessTools.Method(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintAt));

        private static void Postfix(BuildableDef entDef, IntVec3 center, Rot4 rot, Map map, bool godMode, ref AcceptanceReport __result)
        {
            if (!__result.Accepted || godMode) return;
            foreach (IntVec3 cell in GenAdj.OccupiedRect(center, rot, entDef.Size))
                if (PublicFacility.ConsoleAt(map, cell) != null)
                { __result = "DeltaV_PublicFacility_NoConstruction".Translate(); return; }
        }
    }

    [HarmonyPatch(typeof(Building), nameof(Building.ClaimableBy))]
    internal static class Patch_PublicFacilityClaim
    {
        private static bool Prefix(Building __instance, Faction by, ref AcceptanceReport __result)
        {
            if (by == Faction.OfPlayer && PublicFacility.ConsoleAt(__instance.Map, __instance.Position) != null)
            {
                __result = "DeltaV_PublicFacility_NoClaim".Translate();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Building), nameof(Building.DeconstructibleBy))]
    internal static class Patch_PublicFacilityDeconstruct
    {
        private static void Postfix(Building __instance, Faction faction, ref AcceptanceReport __result)
        {
            if (faction == Faction.OfPlayer && __instance.def.building.IsDeconstructible
                && PublicFacility.ConsoleAt(__instance.Map, __instance.Position) != null) __result = true;
        }
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_BuildingRemoved))]
    internal static class Patch_PublicFacilityRemoved
    {
        private static void Prefix(Building building, Pawn deconstructor)
        {
            Faction owner = PublicFacility.ConsoleAt(building.Map, building.Position)?.parent.Faction;
            PublicFacility.Penalize(owner, deconstructor, building);
        }
    }

    // Save the owner before removal clears the foundation stamp. Transfer pipelines never enter these jobs.
    [HarmonyPatch(typeof(JobDriver_RemoveFoundation), "DoEffect")]
    internal static class Patch_PublicFacilityRemoveFoundation
    {
        private static void Prefix(JobDriver_RemoveFoundation __instance, IntVec3 c, out Faction __state) =>
            __state = PublicFacility.ConsoleAt(__instance.pawn.Map, c)?.parent.Faction;
        private static void Postfix(JobDriver_RemoveFoundation __instance, IntVec3 c, Faction __state)
        {
            Map map = __instance.pawn.Map;
            if (!SubstructureOwnership.HasFoundation(map, c))
                PublicFacility.Penalize(__state, __instance.pawn, new GlobalTargetInfo(c, map));
        }
    }

    [HarmonyPatch(typeof(DamageWorker), "ExplosionDamageTerrain")]
    internal static class Patch_PublicFacilityExplosion
    {
        private static void Prefix(Explosion explosion, IntVec3 c, out Faction __state) =>
            __state = PublicFacility.ConsoleAt(explosion.Map, c)?.parent.Faction;
        private static void Postfix(Explosion explosion, IntVec3 c, Faction __state)
        {
            if (!SubstructureOwnership.HasFoundation(explosion.Map, c))
                PublicFacility.Penalize(__state, explosion.instigator, new GlobalTargetInfo(c, explosion.Map));
        }
    }

    [HarmonyPatch(typeof(JobDriver_RemoveFloor), "DoEffect")]
    internal static class Patch_PublicFacilityRemoveFloor
    {
        private static void Prefix(JobDriver_RemoveFloor __instance, IntVec3 c, out TerrainDef __state) =>
            __state = __instance.pawn.Map.terrainGrid.TerrainAt(c);
        private static void Postfix(JobDriver_RemoveFloor __instance, IntVec3 c, TerrainDef __state)
        {
            Map map = __instance.pawn.Map;
            if (map.terrainGrid.TerrainAt(c) != __state)
                PublicFacility.Penalize(PublicFacility.ConsoleAt(map, c)?.parent.Faction,
                    __instance.pawn, new GlobalTargetInfo(c, map));
        }
    }

    [HarmonyPatch(typeof(JobDriver_RemoveRoof), "DoEffect")]
    internal static class Patch_PublicFacilityRemoveRoof
    {
        private static void Prefix(JobDriver_RemoveRoof __instance, out bool __state) =>
            __state = __instance.job.targetA.Cell.Roofed(__instance.pawn.Map);
        private static void Postfix(JobDriver_RemoveRoof __instance, bool __state)
        {
            Map map = __instance.pawn.Map;
            IntVec3 cell = __instance.job.targetA.Cell;
            if (__state && !cell.Roofed(map))
                PublicFacility.Penalize(PublicFacility.ConsoleAt(map, cell)?.parent.Faction,
                    __instance.pawn, new GlobalTargetInfo(cell, map));
        }
    }

    [HarmonyPatch(typeof(Designator_AreaBuildRoof), nameof(Designator_AreaBuildRoof.CanDesignateCell))]
    internal static class Patch_PublicFacilityRoofConstruction
    {
        private static void Postfix(IntVec3 c, ref AcceptanceReport __result)
        {
            if (__result.Accepted && !DebugSettings.godMode && PublicFacility.ConsoleAt(Find.CurrentMap, c) != null)
                __result = "DeltaV_PublicFacility_NoConstruction".Translate();
        }
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_RelationKindChanged))]
    internal static class Patch_PublicFacilityRelations
    {
        private static void Postfix(Faction __instance, Faction other)
        {
            if (__instance != Faction.OfPlayer && other != Faction.OfPlayer) return;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null) return;
            foreach (Map map in Find.Maps)
                foreach (ShipBase ship in ShipRegistry.AllShipsOnMap(map))
                    if (ship.IsPublicFacility && (ship.Faction == __instance || ship.Faction == other))
                        universe.Controls.ReconcileOwner(ship.Identity);
        }
    }

    [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor))]
    internal static class Patch_PublicFacilityStorage
    {
        private static void Postfix(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction,
            ref IntVec3 foundCell, bool needAccurateResult, ref bool __result)
        {
            if (faction != Faction.OfPlayer || Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map) == null) return;
            StoragePriority priority = __result ? foundCell.GetSlotGroup(map).Settings.Priority : currentPriority;
            IntVec3 origin = t.SpawnedOrAnyParentSpawned ? t.PositionHeld : carrier.PositionHeld;
            float distance = __result ? (origin - foundCell).LengthHorizontalSquared : float.MaxValue;
            foreach (SlotGroup group in map.haulDestinationManager.AllGroupsListInPriorityOrder)
            {
                if (group.Settings.Priority < priority || group.Settings.Priority <= currentPriority) break;
                if (!(group.parent is Building_Storage shelf) || !PublicFacility.PlayerMayUse(shelf)
                    || !shelf.HaulDestinationEnabled) continue;
                // Native worker retains forbidden, reservation, fire, blocker and reachability checks.
                if (!StoreUtility.TryFindBestBetterStoreCellForIn(t, carrier, map, currentPriority, faction,
                    group, out IntVec3 candidate, needAccurateResult)) continue;
                float candidateDistance = (origin - candidate).LengthHorizontalSquared;
                if (group.Settings.Priority == priority && candidateDistance >= distance) continue;
                foundCell = candidate;
                priority = group.Settings.Priority;
                distance = candidateDistance;
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.StoragePriorityAtFor),
        new Type[] { typeof(IHaulDestination), typeof(Thing), typeof(bool) })]
    internal static class Patch_PublicFacilityStoragePriority
    {
        private static void Postfix(IHaulDestination at, Thing t, bool forced, ref StoragePriority __result)
        {
            if (forced && at is Building_Storage shelf && PublicFacility.PlayerMayUse(shelf) && at.Accepts(t))
                __result = at.GetStoreSettings().Priority;
        }
    }

    [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.IsInValidBestStorage))]
    internal static class Patch_PublicFacilityBestStorage
    {
        private static void Postfix(Thing t, ref bool __result)
        {
            if (!__result && StoreUtility.CurrentHaulDestinationOf(t) is Building_Storage shelf
                && PublicFacility.PlayerMayUse(shelf) && shelf.Accepts(t))
                __result = !StoreUtility.TryFindBestBetterStorageFor(t, null, t.MapHeld, shelf.GetStoreSettings().Priority,
                    Faction.OfPlayer, out _, out _, needAccurateResult: false);
        }
    }
}
