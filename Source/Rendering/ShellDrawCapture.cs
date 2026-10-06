using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace DeltaV
{
    /// <summary>Captures only draw calls made while replaying one source-map Thing for a shell.</summary>
    internal static class ShellDrawCapture
    {
        [ThreadStatic] private static CaptureContext current;
        private static bool installed;

        private sealed class CaptureContext
        {
            public Matrix4x4 transform;
            public ShellSurfaceLayer surface;
            public float lift;
        }

        public static bool Active => current != null;

        public static void Install(Harmony harmony)
        {
            Type[] drawSignature = { typeof(Mesh), typeof(Matrix4x4), typeof(Material),
                typeof(int), typeof(Camera), typeof(int), typeof(MaterialPropertyBlock),
                typeof(ShadowCastingMode), typeof(bool), typeof(Transform),
                typeof(LightProbeUsage), typeof(LightProbeProxyVolume) };
            MethodInfo draw = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh), drawSignature);
            MethodInfo door = AccessTools.PropertyGetter(typeof(Building_Door), "OpenPct");
            if (draw == null || door == null)
            {
                Log.Error("[DeltaV] Shell draw capture targets missing; native multilayer shells disabled.");
                return;
            }
            try
            {
                harmony.Patch(draw, prefix: new HarmonyMethod(typeof(Patch_ShellDrawMesh), "Prefix"));
                harmony.Patch(door, prefix: new HarmonyMethod(typeof(Patch_ShellDoorOpenPct), "Prefix"));
                installed = Harmony.GetPatchInfo(draw)?.Prefixes.Any(p => p.owner == harmony.Id) == true
                    && Harmony.GetPatchInfo(door)?.Prefixes.Any(p => p.owner == harmony.Id) == true;
                if (!installed)
                    Log.Error("[DeltaV] Shell draw capture Harmony binding did not complete.");
            }
            catch (Exception ex)
            {
                harmony.Unpatch(draw, HarmonyPatchType.Prefix, harmony.Id);
                harmony.Unpatch(door, HarmonyPatchType.Prefix, harmony.Id);
                Log.Error($"[DeltaV] Shell draw capture Harmony binding failed: {ex}");
            }
        }

        public static void Capture(Action draw, Matrix4x4 transform, ShellSurfaceLayer surface, float zIndex)
        {
            if (!installed) return;
            if (current != null) throw new InvalidOperationException("Nested shell draw capture");
            current = new CaptureContext { transform = transform, surface = surface,
                lift = zIndex * Altitudes.AltInc };
            try { draw(); }
            finally { current = null; }
        }

        public static bool TryCollect(Mesh mesh, Matrix4x4 matrix, Material material,
            int submeshIndex, MaterialPropertyBlock properties)
        {
            CaptureContext capture = current;
            if (capture == null) return false;
            if (mesh != null && material != null)
            {
                matrix.m13 += capture.lift;
                MapOverlayRenderer.AddCapturedDraw(mesh, capture.transform * matrix, material,
                    submeshIndex, properties, capture.surface);
            }
            return true;
        }
    }

    internal static class Patch_ShellDrawMesh
    {
        private static bool Prefix(Mesh __0, Matrix4x4 __1, Material __2, int __5,
            MaterialPropertyBlock __6)
        {
            return !ShellDrawCapture.TryCollect(__0, __1, __2, __5, __6);
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
