using HarmonyLib;
using Verse;

namespace DeltaV
{
    // Vanilla only populates LinkGrid for a linked Graphic. The launcher keeps its
    // full 1x3 layered Graphic_Single, but still contributes Wall to neighboring walls.
    [HarmonyPatch(typeof(ThingDef), nameof(ThingDef.CanAffectLinker), MethodType.Getter)]
    public static class Patch_MissileLauncherWallLink
    {
        public static void Postfix(ThingDef __instance, ref bool __result)
        {
            if (__result || __instance.graphicData == null
                || __instance.graphicData.linkFlags == LinkFlags.None) return;
            if (__instance.GetCompProperties<CompProperties_MissileAutoloader>() != null)
                __result = true;
        }
    }
}
