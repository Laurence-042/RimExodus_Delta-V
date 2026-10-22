using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(Dialog_EnterPortal), "TryAccept")]
    internal static class Patch_SoftDockPortalDialog
    {
        // The window can outlive the connection. Vanilla owns selection and all
        // acceptance behavior; only reject a stale soft-docking endpoint.
        private static bool Prefix(MapPortal ___portal, ref bool __result)
        {
            if (!(___portal is SoftDockPortal portal) || portal.IsEnterable(out _)) return true;
            Messages.Message("DeltaV_SoftDockTransferDisconnected".Translate(), MessageTypeDefOf.RejectInput, false);
            __result = false;
            return false;
        }
    }
}
