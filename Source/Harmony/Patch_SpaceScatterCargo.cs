using HarmonyLib;
using Verse;

namespace DeltaV
{
    // ThingGrid's per-cell callbacks cover spawn, despawn and ordinary position changes.
    [HarmonyPatch(typeof(ThingGrid), "RegisterInCell")]
    internal static class Patch_SpaceScatterCargo_Register
    {
        private static void Postfix(Thing t, IntVec3 c, Map ___map)
        {
            if (___map == null || !c.InBounds(___map) || t == null || c != t.Position) return;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (t is Building) return; // Building spawn/despawn supplies the complete footprint at the end of the operation.
            universe?.ShipStructure.NotifyItem(___map, t, added: true);
        }
    }

    [HarmonyPatch(typeof(ThingGrid), "DeregisterInCell")]
    internal static class Patch_SpaceScatterCargo_Deregister
    {
        private static void Postfix(Thing t, IntVec3 c, Map ___map)
        {
            if (___map == null || !c.InBounds(___map) || t == null || t is Building || c != t.Position) return;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            universe?.ShipStructure.NotifyItem(___map, t, added: false);
        }
    }
}
