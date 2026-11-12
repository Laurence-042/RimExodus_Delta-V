using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>放开太空图的相机边界，并在原版写入相机之前应用船壳跟随。</summary>
    [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.Update))]
    internal static class Patch_CameraCenterClamp
    {
        private static readonly MethodInfo VanillaClamp = AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp),
            new[] { typeof(float), typeof(float), typeof(float) });
        private static readonly MethodInfo ScopedClamp = AccessTools.Method(typeof(Patch_CameraCenterClamp),
            nameof(ClampForCurrentMap));
        internal static bool SupportsTargetFocus { get; private set; }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            SupportsTargetFocus = false;
            // RimWorld 1.6 IL_01eb/IL_0221: rootPos.x/z = Mathf.Clamp(..., 2, map.Size.x/z - 2).
            var matches = new List<int>();
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode == OpCodes.Call && Equals(code[i].operand, VanillaClamp)) matches.Add(i);
            }
            if (matches.Count != 2)
            {
                Log.Warning($"[DeltaV] CameraDriver.Update center clamp changed ({matches.Count} matches); leaving camera movement unchanged.");
                return code;
            }
            foreach (int i in matches) code[i].operand = ScopedClamp;

            MethodInfo applyPosition = AccessTools.Method(typeof(CameraDriver), "ApplyPositionToGameObject");
            FieldInfo rootPos = AccessTools.Field(typeof(CameraDriver), "rootPos");
            FieldInfo velocity = AccessTools.Field(typeof(CameraDriver), "velocity");
            var applyCalls = code.Where(instruction => instruction.Calls(applyPosition)).ToList();
            if (applyCalls.Count != 1 || rootPos?.FieldType != typeof(Vector3) || velocity?.FieldType != typeof(Vector3))
            {
                Log.Warning("[DeltaV] CameraDriver.Update position write changed; target camera focus is unavailable.");
                return code;
            }

            // Update 的唯一 Apply 调用位于缩放和原版平移之后，子相机和记忆位置之前。
            CodeInstruction apply = applyCalls[0];
            code.InsertRange(code.IndexOf(apply), new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(apply).MoveBlocksFrom(apply),
                new CodeInstruction(OpCodes.Ldflda, rootPos),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldflda, velocity),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patch_CameraCenterClamp), nameof(ApplyTargetCenter)))
            });
            SupportsTargetFocus = true;
            return code;
        }

        private static float ClampForCurrentMap(float value, float min, float max)
        {
            Map map = Find.CurrentMap;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            return map != null && universe?.FrameOf(map) != null ? value : Mathf.Clamp(value, min, max);
        }

        private static void ApplyTargetCenter(ref Vector3 rootPos, ref Vector3 velocity)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            TargetCameraFocus focus = universe?.TargetCamera;
            if (focus == null) return;
            if (!focus.TryGetCenter(universe, out Vector3 center))
            {
                universe.TargetCamera = null;
                return;
            }

            rootPos.x = center.x;
            rootPos.z = center.z;
            velocity = Vector3.zero;
        }
    }

    [HarmonyPatch(typeof(CameraDriver), "CalculateCurInputDollyVect")]
    internal static class Patch_CameraTargetFocusInput
    {
        private static readonly Func<CameraDriver, bool> MotionBlocked = AccessTools.MethodDelegate<Func<CameraDriver, bool>>(
            AccessTools.PropertyGetter(typeof(CameraDriver), "AnythingPreventsCameraMotion"));

        private static void Postfix(CameraDriver __instance, Vector2 __result, Vector2 ___desiredDollyRaw,
            Vector3 ___rootPos, CameraPanner ___panner)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            TargetCameraFocus focus = universe?.TargetCamera;
            if (focus == null) return;

            if (___panner.Moving || ((__result != Vector2.zero || ___desiredDollyRaw != Vector2.zero)
                && !MotionBlocked(__instance)))
                universe.TargetCamera = null;
            else
                focus.CaptureStart(___rootPos);
        }
    }

    [HarmonyPatch]
    internal static class Patch_CameraTargetFocusJump
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CameraDriver), nameof(CameraDriver.JumpToCurrentMapLoc), new[] { typeof(Vector3) });
            yield return AccessTools.Method(typeof(CameraDriver), nameof(CameraDriver.PanToMapLoc));
            yield return AccessTools.Method(typeof(CameraDriver), nameof(CameraDriver.PanToMapLocAndSize));
        }

        private static void Prefix()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe != null) universe.TargetCamera = null;
        }
    }
}
