using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>原版静态 interactionCell 无法表达自瞄启用后释放炮手位，故只追加动态约束。</summary>
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintAt))]
    public static class Patch_GenConstruct_SpaceWeaponCrewCell
    {
        public static void Postfix(BuildableDef entDef, IntVec3 center, Rot4 rot, Map map,
            Thing thingToIgnore, ref AcceptanceReport __result)
        {
            if (!__result.Accepted || map == null) return;
            ThingDef thingDef = entDef as ThingDef;
            CompProperties_SpaceWeaponCrew crewProps =
                thingDef?.GetCompProperties<CompProperties_SpaceWeaponCrew>();
            if (crewProps != null)
            {
                IntVec3 gunnerCell = SpaceWeaponCrewUtility.GunnerCellAt(center, rot, crewProps);
                if (!gunnerCell.InBounds(map) || !gunnerCell.Standable(map))
                {
                    __result = "DeltaV_GunnerCellBlocked".Translate();
                    return;
                }
            }

            CellRect placing = GenAdj.OccupiedRect(center, rot, entDef.Size);
            foreach (CompSpaceWeapon weapon in SpaceWeaponService.AllWeaponsOn(map))
            {
                if (weapon.parent == thingToIgnore || !SpaceWeaponCrewUtility.RequiresClearCell(weapon)) continue;
                if (placing.Contains(weapon.Crew.GunnerCell))
                {
                    bool attachment = thingDef?.GetCompProperties<CompProperties_SpaceWeaponAttachment>() != null;
                    if (attachment && center.GetEdifice(map) == weapon.parent
                        && weapon.WeaponProps.attachmentSlots.Exists(offset =>
                            SpaceWeaponGeometry.RotatedCell(weapon.parent, offset) == center))
                    {
                        continue;
                    }
                    __result = "DeltaV_WouldBlockGunnerCell".Translate(weapon.parent.LabelCap);
                    return;
                }
            }
        }
    }
}
