using HarmonyLib;
using Verse;

namespace DeltaV
{
    // Hull inserts keep their own layers and contribute Wall to the vanilla LinkGrid.
    [HarmonyPatch(typeof(ThingDef), nameof(ThingDef.CanAffectLinker), MethodType.Getter)]
    public static class Patch_MissileLauncherWallLink
    {
        public static void Postfix(ThingDef __instance, ref bool __result)
        {
            if (__result || __instance.graphicData == null
                || __instance.graphicData.linkFlags == LinkFlags.None) return;
            if (__instance == DeltaVDefOf.DeltaV_Weapon_MissileLauncher
                || __instance.thingClass == typeof(Building_Casemate))
                __result = true;
        }
    }

    [HarmonyPatch(typeof(Graphic_Linked), nameof(Graphic_Linked.ShouldLinkWith))]
    public static class Patch_LinkedGraphic_HullInsertSide
    {
        public static void Postfix(IntVec3 c, Thing parent, ref bool __result)
        {
            if (!__result || !(parent.def.IsWall || parent is Building_VacuumBarrier)
                || !c.InBounds(parent.Map)) return;
            Building insert = c.GetEdificeSafe(parent.Map);
            if (insert is Building_Casemate)
                __result = HullWallInsert.LinksFrom(insert, parent.Position);
        }
    }
}
