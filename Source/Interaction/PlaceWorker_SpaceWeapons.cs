using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 轨道炮部件允许间隔规划；放置只校验整脚印归属，不要求已有连续成品链。
    /// </summary>
    public class PlaceWorker_RailgunPart : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef def, IntVec3 center, Rot4 rot,
            Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null || universe.FrameOf(map) == null) return true; // 地表：原版规则
            var props = (def as ThingDef)?.GetCompProperties<CompProperties_SpaceRailgun>();
            if (props == null && (def as ThingDef)?.GetCompProperties<CompProperties_SpaceWeapon>() == null)
                return true;
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
            if (ownership == null) return false;
            int owner = 0;
            foreach (IntVec3 cell in GenAdj.OccupiedRect(center, rot, def.Size))
            {
                if (!cell.InBounds(map)) return false;
                int at = ownership.OwnerAt(cell);
                if (at <= 0 || (owner != 0 && at != owner)) return "DeltaV_WeaponNeedsSingleOwner".Translate();
                owner = at;
            }
            return true;
        }

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot,
            UnityEngine.Color ghostCol, Thing thing = null)
        {
            var cells = new System.Collections.Generic.List<IntVec3>();
            CompProperties_SpaceRailgun rail = def.GetCompProperties<CompProperties_SpaceRailgun>();
            if (rail != null)
            {
                IntVec3 next = center + rail.nextRootOffset.RotatedBy(rot);
                foreach (IntVec3 cell in GenAdj.OccupiedRect(next, rot, def.Size)) cells.Add(cell);
            }
            CompProperties_SpaceWeapon weapon = def.GetCompProperties<CompProperties_SpaceWeapon>();
            if (weapon != null)
            {
                foreach (IntVec3 offset in weapon.acceleratorStarts)
                {
                    IntVec3 next = center + offset.RotatedBy(rot);
                    ThingDef nextDef = !weapon.allowedAcceleratorDefs.NullOrEmpty()
                        ? weapon.allowedAcceleratorDefs[0]
                        : null;
                    if (nextDef == null) cells.Add(next);
                    else foreach (IntVec3 cell in GenAdj.OccupiedRect(next, rot, nextDef.Size))
                        cells.Add(cell);
                }
            }
            if (cells.Count > 0) GenDraw.DrawFieldEdges(cells, UnityEngine.Color.yellow);
        }
    }

    public sealed class PlaceWorker_SpaceWeaponAttachment : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef def, IntVec3 center, Rot4 rot,
            Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            CompSpaceWeapon weapon = center.GetEdifice(map)?.TryGetComp<CompSpaceWeapon>();
            if (weapon == null || SpaceWeaponGeometry.OwnerIdOf(weapon.parent) <= 0)
                return "DeltaV_AttachmentNeedsSlot".Translate();
            if (!weapon.WeaponProps.allowedAttachmentDefs.NullOrEmpty()
                && (def is not ThingDef thingDef
                    || !weapon.WeaponProps.allowedAttachmentDefs.Contains(thingDef)))
                return "DeltaV_AttachmentNeedsSlot".Translate();

            CellRect hostRect = weapon.parent.OccupiedRect();
            foreach (IntVec3 occupied in GenAdj.OccupiedRect(center, rot, def.Size))
                if (!hostRect.Contains(occupied)) return "DeltaV_AttachmentNeedsSlot".Translate();

            return weapon.AttachmentSlotAt(center) != null
                ? AcceptanceReport.WasAccepted
                : "DeltaV_AttachmentNeedsSlot".Translate();
        }
    }
}
