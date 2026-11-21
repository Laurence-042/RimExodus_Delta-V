using System.Collections.Generic;
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
            CompSpaceRailgun rail = null;
            CompSpaceCargo cargo = null;
            if (weapon == null)
            {
                foreach (Thing candidate in map.thingGrid.ThingsListAtFast(center))
                {
                    if (candidate.TryGetComp<CompSpaceCargo>() is { } bay && bay.AttachmentSlotAt(center) != null)
                        cargo = bay;
                    rail = (candidate as ThingWithComps)?.TryGetComp<CompSpaceRailgun>();
                    if (rail != null && rail.AttachmentSlotAt(center) != null) break;
                    rail = null;
                }
            }
            ThingWithComps host = weapon?.parent ?? rail?.parent ?? cargo?.parent;
            if (host == null || SpaceWeaponGeometry.OwnerIdOf(host) <= 0)
                return "DeltaV_AttachmentNeedsSlot".Translate();
            var allowed = weapon != null
                ? weapon.WeaponProps.allowedAttachmentDefs : rail != null
                    ? rail.RailProps.allowedAttachmentDefs : cargo.AttachmentProps.allowed;
            if (!allowed.NullOrEmpty()
                && (def is not ThingDef thingDef || !allowed.Contains(thingDef)))
                return "DeltaV_AttachmentNeedsSlot".Translate();
            var attachmentProps = (def as ThingDef)?
                .GetCompProperties<CompProperties_SpaceWeaponAttachment>();
            if (attachmentProps != null && !attachmentProps.allowedHostDefs.NullOrEmpty()
                && !attachmentProps.allowedHostDefs.Contains(host.def))
                return "DeltaV_AttachmentNeedsSlot".Translate();

            CellRect hostRect = host.OccupiedRect();
            foreach (IntVec3 occupied in GenAdj.OccupiedRect(center, rot, def.Size))
                if (!hostRect.Contains(occupied)) return "DeltaV_AttachmentNeedsSlot".Translate();

            if ((weapon?.AttachmentSlotAt(center) ?? rail?.AttachmentSlotAt(center)
                ?? cargo?.AttachmentSlotAt(center)) == null)
                return "DeltaV_AttachmentNeedsSlot".Translate();

            foreach (IntVec3 occupied in GenAdj.OccupiedRect(center, rot, def.Size))
            {
                foreach (Thing existing in map.thingGrid.ThingsListAtFast(occupied))
                {
                    if (existing == thingToIgnore || existing.Destroyed) continue;
                    ThingDef existingDef = existing.def.entityDefToBuild as ThingDef ?? existing.def;
                    if (existingDef.GetCompProperties<CompProperties_SpaceWeaponAttachment>() != null)
                        return "DeltaV_AttachmentSlotOccupied".Translate();
                }
            }
            return AcceptanceReport.WasAccepted;
        }
    }

    public sealed class PlaceWorker_MissileMagazine : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef def, IntVec3 center, Rot4 rot,
            Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            if (def is not ThingDef magazineDef || map == null) return false;
            bool found = false;
            var universe = Current.Game?.GetComponent<UniverseManager>();
            foreach (Thing host in SpaceWeaponAttachmentSlots.ExteriorHosts(magazineDef, center, rot, map))
            {
                if (host == thingToIgnore) continue;
                found = true;
                if (universe?.FrameOf(map) == null) return AcceptanceReport.WasAccepted;
                int owner = SpaceWeaponGeometry.OwnerIdOf(host);
                if (owner > 0 && ShipStructureIndex.OwnerOfFootprint(SubstructureOwnership.TryGet(map),
                    GenAdj.OccupiedRect(center, rot, magazineDef.Size)) == owner)
                    return AcceptanceReport.WasAccepted;
            }
            if (found) return "DeltaV_WeaponNeedsSingleOwner".Translate();
            return "DeltaV_MissileMagazineNeedsLauncher".Translate();
        }
    }

    public sealed class PlaceWorker_MissileLauncher : PlaceWorker_RailgunPart
    {
        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot,
            UnityEngine.Color ghostCol, Thing thing = null)
        {
            base.DrawGhost(def, center, rot, ghostCol, thing);
            var weapon = def.GetCompProperties<CompProperties_SpaceWeapon>();
            if (weapon == null) return;
            var cells = new List<IntVec3>();
            foreach (var slot in weapon.attachmentSlots)
                if (slot.external)
                    foreach (ThingDef attachment in weapon.allowedAttachmentDefs)
                        foreach (IntVec3 cell in GenAdj.OccupiedRect(center + slot.cell.RotatedBy(rot), rot, attachment.Size))
                            cells.Add(cell);
            GenDraw.DrawFieldEdges(cells, UnityEngine.Color.yellow);
        }
    }
}
