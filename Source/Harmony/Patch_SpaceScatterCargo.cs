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
            if (___map == null || !c.InBounds(___map) || t == null
                || !SpaceScatterService.IsCargo(t)) return;
            Current.Game?.GetComponent<UniverseManager>()?.NotifyCargoRegistered(___map, t, c);
        }
    }

    [HarmonyPatch(typeof(ThingGrid), "DeregisterInCell")]
    internal static class Patch_SpaceScatterCargo_Deregister
    {
        private static void Postfix(Thing t, IntVec3 c, Map ___map)
        {
            if (___map == null || !c.InBounds(___map) || t == null
                || !SpaceScatterService.IsCargo(t)) return;
            Current.Game?.GetComponent<UniverseManager>()?.NotifyCargoDeregistered(___map, t, c);
        }
    }
}
