using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Delta-V shell policies around REF draw capture.</summary>
    internal static class ShellDrawCapture
    {
        public static bool Active => RimExodus.Maps.MapDrawCapture.Active;
        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(Building_Door), "OpenPct"),
                prefix: new HarmonyMethod(typeof(Patch_ShellDoorOpenPct), "Prefix"));
        }
        public static void Capture(Action draw, Matrix4x4 transform, ShellSurfaceLayer surface, float zIndex)
        {
            Matrix4x4 lift = Matrix4x4.Translate(new Vector3(0, zIndex * Altitudes.AltInc, 0));
            RimExodus.Maps.MapDrawCapture.Collect(draw, transform * lift,
                command => MapOverlayRenderer.AddSharedDraw(command, surface));
        }
    }
    internal static class Patch_ShellDoorOpenPct
    {
        private static bool Prefix(Building_Door __instance, ref float __result)
        {
            if (!ShellDrawCapture.Active) return true;
            __result = __instance.Open ? 1f : 0f;
            return false;
        }
    }
}
