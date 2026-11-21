using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    internal static class ShipStructureInventory
    {
        internal static CompStructureMass MassFor(ThingOwner inventory) => inventory?.Owner switch
        {
            Building_SealedSpaceCargoContainer crate => crate.GetComp<CompStructureMass>(),
            CompTransporter transporter => transporter.parent.GetComp<CompStructureMass>(),
            _ => null
        };
    }

    [HarmonyPatch(typeof(ThingOwner), "NotifyAdded")]
    internal static class Patch_ShipStructureInventory_Added
    {
        private static void Postfix(ThingOwner __instance, Thing item) =>
            ShipStructureInventory.MassFor(__instance)?.NotifyItem(item, true);
    }

    [HarmonyPatch(typeof(ThingOwner), "NotifyRemoved")]
    internal static class Patch_ShipStructureInventory_Removed
    {
        private static void Postfix(ThingOwner __instance, Thing item) =>
            ShipStructureInventory.MassFor(__instance)?.NotifyItem(item, false);
    }


    [HarmonyPatch(typeof(CompTransporter), nameof(CompTransporter.Notify_ThingRemoved))]
    internal static class Patch_ShipStructureInventory_TransporterRemoved
    {
        private static void Postfix(CompTransporter __instance, Thing t) =>
            ShipStructureInventory.MassFor(__instance.innerContainer)?.NotifyItem(
                t, t.holdingOwner == __instance.innerContainer);
    }

    [HarmonyPatch(typeof(CompTransporter), nameof(CompTransporter.Notify_ThingAddedAndMergedWith))]
    internal static class Patch_ShipStructureInventory_TransporterMerged
    {
        private static void Postfix(CompTransporter __instance, Thing t) =>
            ShipStructureInventory.MassFor(__instance.innerContainer)?.NotifyItem(t, true);
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.SplitOff))]
    internal static class Patch_ShipStructureInventory_Split
    {
        private static void Postfix(Thing __instance) =>
            ShipStructureInventory.MassFor(__instance.holdingOwner)?.NotifyItem(__instance, true);
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.TryAbsorbStack))]
    internal static class Patch_ShipStructureInventory_Merge
    {
        private static void Postfix(Thing __instance, Thing other)
        {
            ShipStructureInventory.MassFor(__instance.holdingOwner)?.NotifyItem(__instance, true);
            ShipStructureInventory.MassFor(other.holdingOwner)?.NotifyItem(other, true);
        }
    }
}
