using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(ITab_ContentsTransporter), "OnDropThing", typeof(Thing), typeof(int))]
    internal static class Patch_TransporterCargoDrop
    {
        private static bool Prefix(ITab_ContentsTransporter __instance, Thing t, int count)
        {
            if (t == null || !SpaceScatterService.IsCargo(t)) return true;
            var universe = Current.Game?.GetComponent<UniverseManager>();
            return universe?.Scatter.TryQueueTransporterEjection(__instance.Transporter, t, count,
                __instance.DropOffset) != true;
        }
    }
}
