using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>One top draw contract for map turrets, component projectors and transformed shells.</summary>
    public static class SpaceTurretTopRenderer
    {
        public static IShellTurretTopSource SourceFor(Thing thing)
        {
            if (thing is IShellTurretTopSource source) return source;
            if (thing is ThingWithComps comps)
                foreach (ThingComp comp in comps.AllComps)
                    if (comp is IShellTurretTopSource componentSource) return componentSource;
            return null;
        }

        public static bool Supports(ThingDef def)
        {
            if (def?.thingClass != null && typeof(IShellTurretTopSource).IsAssignableFrom(def.thingClass)) return true;
            if (def?.comps != null)
                foreach (CompProperties comp in def.comps)
                    if (comp.compClass != null && typeof(IShellTurretTopSource).IsAssignableFrom(comp.compClass)) return true;
            return false;
        }

        public static Matrix4x4 MatrixFor(Thing thing, IShellTurretTopSource source, Vector3 drawLoc,
            Vector3 recoilOffset, float recoilAngle, Matrix4x4 transform, Quaternion rotation, float zIndex = 0f)
        {
            var building = thing.def.building;
            float size = building.turretTopDrawSize;
            Vector3 scale = new Vector3(size, 1f, size);
            Vector3 offset;
            if (thing is Building_Casemate)
            {
                Vector2 pivot = SpaceWeaponGeometry.CasematePivotOffset(thing);
                offset = new Vector3(pivot.x, 0f, pivot.y);
                scale.x *= 1.5f;
                scale.z *= 1.5f;
            }
            else
                offset = new Vector3(building.turretTopOffset.x, 0f, building.turretTopOffset.y).RotatedBy(recoilAngle);
            Vector3 center = drawLoc + Altitudes.AltIncVect + offset + recoilOffset + Vector3.up * (zIndex * Altitudes.AltInc);
            return Matrix4x4.TRS(transform.MultiplyPoint3x4(center),
                rotation * (TurretTop.ArtworkRotation + source.ShellTopAngle).ToQuat(), scale);
        }

        public static void Draw(Thing thing, IShellTurretTopSource source, Vector3 drawLoc,
            Vector3 recoilOffset, float recoilAngle)
        {
            Graphics.DrawMesh(MeshPool.plane10,
                MatrixFor(thing, source, drawLoc, recoilOffset, recoilAngle, Matrix4x4.identity, Quaternion.identity),
                source.ShellTopMaterial, 0);
        }
    }
}
