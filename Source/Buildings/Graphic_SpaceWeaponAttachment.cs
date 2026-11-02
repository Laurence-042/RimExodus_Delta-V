using UnityEngine;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Draws a one-cell attachment texture at the host weapon's slot center. The
    /// building keeps its actual cell for construction, selection and mechanics.
    /// </summary>
    public class Graphic_SpaceWeaponAttachment : Graphic_Single
    {
        private bool TrySlot(Thing thing, out Rot4 hostRotation,
            out SpaceWeaponAttachmentSlot slot)
        {
            hostRotation = Rot4.North;
            slot = null;
            if (thing is not ThingWithComps attachment) return false;
            CompSpaceWeaponAttachment attachmentComp = attachment.TryGetComp<CompSpaceWeaponAttachment>();
            CompSpaceWeapon host = attachmentComp?.HostWeapon;
            CompSpaceRailgun rail = attachmentComp?.HostRailgun;
            Building_SpaceCargoContainer cargo = attachmentComp?.HostCargo;
            slot = host?.AttachmentSlotAt(thing.Position)
                ?? rail?.AttachmentSlotAt(thing.Position) ?? cargo?.AttachmentSlotAt(thing.Position);
            if (slot == null) return false;
            hostRotation = host?.parent.Rotation ?? rail?.parent.Rotation ?? cargo.Rotation;
            return true;
        }

        private Vector3 VisualCenter(Vector3 cellCenter, Rot4 thingRotation,
            Rot4 hostRotation, SpaceWeaponAttachmentSlot slot)
        {
            Vector2 offset = SpaceTransform.RotatePlacementOffset(slot.drawOffset, hostRotation);
            Vector3 center = cellCenter + DrawOffset(thingRotation);
            center.x += offset.x;
            center.z += offset.y;
            return center;
        }

        public override void Print(SectionLayer layer, Thing thing, float extraRotation)
        {
            if (!TrySlot(thing, out Rot4 rotation, out SpaceWeaponAttachmentSlot slot))
            {
                base.Print(layer, thing, extraRotation);
                return;
            }

            Vector3 center = VisualCenter(thing.TrueCenter(), thing.Rotation, rotation, slot);
            Vector2 size = drawSize * slot.drawScale;
            Material material = MatAt(rotation, thing);
            TryGetTextureAtlasReplacementInfo(material, thing.def.category.ToAtlasGroup(),
                false, true, out material, out Vector2[] uvs, out Color32 vertexColor);
            Printer_Plane.PrintPlane(layer, center, size, material,
                AngleFromRot(rotation) + extraRotation, false, uvs,
                new[] { vertexColor, vertexColor, vertexColor, vertexColor });
        }

        public override void DrawWorker(Vector3 loc, Rot4 rot, ThingDef thingDef,
            Thing thing, float extraRotation)
        {
            if (!TrySlot(thing, out Rot4 hostRotation, out SpaceWeaponAttachmentSlot slot))
            {
                base.DrawWorker(loc, rot, thingDef, thing, extraRotation);
                return;
            }

            Vector3 center = VisualCenter(loc, rot, hostRotation, slot);
            Quaternion rotation = QuatFromRot(hostRotation)
                * Quaternion.Euler(0f, extraRotation, 0f);
            Graphics.DrawMesh(MeshPool.GridPlane(drawSize * slot.drawScale),
                center, rotation, MatAt(hostRotation, thing), 0);
        }
    }
}
