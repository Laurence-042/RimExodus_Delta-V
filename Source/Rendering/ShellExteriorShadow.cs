using UnityEngine;
using Verse;

namespace DeltaV
{
    // Two non-overlapping swept edges project the raised rectangle onto the hull.
    // The box covers the remaining footprint. No per-frame mesh or game state.
    internal static class ShellExteriorShadow
    {
        internal static bool TryMatrix(CellRect footprint, float height, Vector2 localSun,
            int side, out Matrix4x4 matrix)
        {
            Vector3 offset = new Vector3(-localSun.x * height, 0f, -localSun.y * height);
            matrix = Matrix4x4.identity;
            if (height <= 0f || Mathf.Abs(side == 0 ? offset.x : offset.z) < .00001f) return false;
            Vector3 across, along, center;
            if (side == 0)
            {
                across = offset;
                along = new Vector3(0f, 0f, footprint.Height);
                center = new Vector3(offset.x > 0f ? footprint.maxX + 1 : footprint.minX,
                    0f, footprint.minZ + footprint.Height * .5f) + offset * .5f;
            }
            else
            {
                across = new Vector3(footprint.Width, 0f, 0f);
                along = offset;
                center = new Vector3(footprint.minX + footprint.Width * .5f, 0f,
                    offset.z > 0f ? footprint.maxZ + 1 : footprint.minZ) + offset * .5f;
            }
            // Keep the shared plane front-facing for both light directions.
            if (Vector3.Cross(along, across).y < 0f) across = -across;
            matrix.SetColumn(0, new Vector4(across.x, 0f, across.z, 0f));
            matrix.SetColumn(2, new Vector4(along.x, 0f, along.z, 0f));
            matrix.SetColumn(3, new Vector4(center.x, AltitudeLayer.Building.AltitudeFor(), center.z, 1f));
            return true;
        }
    }
}
