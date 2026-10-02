using UnityEngine;
using Verse;

namespace DeltaV
{
    public class CompProperties_RailgunShell : CompProperties
    {
        public string texPath;

        public CompProperties_RailgunShell()
        {
            compClass = typeof(CompRailgunShell);
        }
    }

    /// <summary>
    /// The lower sprite is printed by RimWorld. The shell is centered on this part's
    /// north edge, so its opaque bridge covers the seam with the next part.
    /// </summary>
    public class CompRailgunShell : ThingComp
    {
        // Building base < plugin printed top (~+0.030) < shell < turret top (+0.036585).
        private const float ShellYOffset = 0.033f;

        private Material shellMaterial;

        public override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            CompProperties_RailgunShell shell = (CompProperties_RailgunShell)props;
            if (shellMaterial == null)
                shellMaterial = MaterialPool.MatFrom(shell.texPath, ShaderDatabase.Cutout);

            Rot4 rotation = flip ? parent.Rotation.Opposite : parent.Rotation;
            Vector3 north = rotation.AsQuat * Vector3.forward;
            Vector3 center = drawLoc + north * (parent.def.size.z * 0.5f);
            center.y = parent.def.Altitude + ShellYOffset;
            Vector2 size = parent.def.graphicData.drawSize;
            Graphics.DrawMesh(MeshPool.plane10,
                Matrix4x4.TRS(center, rotation.AsQuat, new Vector3(size.x, 1f, size.y)),
                shellMaterial, 0);
        }
    }
}
