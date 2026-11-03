using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public partial class CompSpaceCargo
    {
        internal Color TerminalColor => !Cargo.IsUnlocked ? new Color(1f, .62f, .16f)
            : StorageMode ? new Color(.25f, .85f, .38f) : new Color(.2f, .65f, 1f);
        // Read the actual native inventory, without materialising Contents each frame.
        internal float CapacityFraction
        {
            get
            {
                int count = parent is Building_SpaceCargoContainer storage
                    ? storage.StoredThings.Count
                    : ((Building_SealedSpaceCargoContainer)parent).GetDirectlyHeldThings().Count;
                return Cargo.CurrentSlotLimit > 0 ? Mathf.Clamp01((float)count / Cargo.CurrentSlotLimit)
                    : count > 0 ? 1f : 0f;
            }
        }

        public override void PostDraw()
        {
            var settings = (CompProperties_SpaceCargo)props;
            Vector2 offset = SpaceTransform.RotatePlacementOffset(settings.capacityBarOffset, parent.Rotation);
            const int segments = 6;
            float fraction = CapacityFraction;
            float width = settings.capacityBarSize.x / segments;
            for (int i = 0; i < segments; i++)
            {
                Vector2 segment = SpaceTransform.RotatePlacementOffset(
                    new Vector2((i + .5f) * width - settings.capacityBarSize.x * .5f, 0f), parent.Rotation);
                GenDraw.DrawFillableBar(new GenDraw.FillableBarRequest
                {
                    center = parent.DrawPos + new Vector3(offset.x + segment.x, .1f, offset.y + segment.y),
                    size = new Vector2(width - .035f, settings.capacityBarSize.y),
                    fillPercent = Mathf.Clamp01(fraction * segments - i),
                    filledMat = SolidColorMaterials.SimpleSolidColorMaterial(new Color(.9f, .85f, .2f)),
                    unfilledMat = SolidColorMaterials.SimpleSolidColorMaterial(new Color(.12f, .14f, .15f)),
                    margin = 0f,
                    rotation = parent.Rotation
                });
            }
        }
    }
}
