using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>只替换 CameraDriver.Update 的两处地图边缘 rootPos clamp。</summary>
    [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.Update))]
    internal static class Patch_CameraCenterClamp
    {
        private static readonly MethodInfo VanillaClamp = AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp),
            new[] { typeof(float), typeof(float), typeof(float) });
        private static readonly MethodInfo ScopedClamp = AccessTools.Method(typeof(Patch_CameraCenterClamp),
            nameof(ClampForCurrentMap));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
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
            return code;
        }

        private static float ClampForCurrentMap(float value, float min, float max)
        {
            Map map = Find.CurrentMap;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            return map != null && universe?.FrameOf(map) != null ? value : Mathf.Clamp(value, min, max);
        }
    }
}
