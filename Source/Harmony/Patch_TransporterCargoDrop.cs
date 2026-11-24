using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(ITab_ContentsTransporter), "OnDropThing", typeof(Thing), typeof(int))]
    internal static class Patch_TransporterCargoDrop
    {
        private static bool Prefix(ITab_ContentsTransporter __instance, Thing t, int count)
        {
            TransporterDropResult result = TransporterDropService.Request(
                __instance.Transporter, t, count, __instance.DropOffset);
            return result == TransporterDropResult.NotHandled;
        }
    }

    [HarmonyPatch(typeof(CompTransporter), nameof(CompTransporter.CompGetGizmosExtra))]
    internal static class Patch_TransporterOccupantExit
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, CompTransporter __instance)
            => Append(__result, __instance, TexCommand.DropCarriedPawn);

        internal static IEnumerable<Gizmo> Append(IEnumerable<Gizmo> source, CompTransporter transporter,
            Texture2D icon)
        {
            foreach (Gizmo gizmo in source) yield return gizmo;
            if (ShuttleOccupantExitService.CanShow(transporter))
                yield return ShuttleOccupantExitService.CommandFor(transporter, icon);
        }
    }
}
