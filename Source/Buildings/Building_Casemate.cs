using UnityEngine;
using Verse;

namespace DeltaV
{
    internal static class HullWallInsert
    {
        internal static bool LinksFrom(Thing insert, IntVec3 wallCell)
        {
            if (insert is not (Building_Casemate or Building_VacuumBarrier)) return false;
            CellRect rect = insert.OccupiedRect();
            bool westOrEast = wallCell.z >= rect.minZ && wallCell.z <= rect.maxZ
                && (wallCell.x == rect.minX - 1 || wallCell.x == rect.maxX + 1);
            bool northOrSouth = wallCell.x >= rect.minX && wallCell.x <= rect.maxX
                && (wallCell.z == rect.minZ - 1 || wallCell.z == rect.maxZ + 1);
            if (insert is Building_VacuumBarrier) return westOrEast || northOrSouth;
            return insert.Rotation.IsHorizontal ? northOrSouth : westOrEast;
        }
    }

    public sealed class Building_Casemate : Building, IShellTurretTopSource
    {
        private Material barrel, cover;

        public Material ShellTopMaterial => barrel ??= MaterialPool.MatFrom(
            "Things/Building/SpaceWeapons/Casemate_Barrel", ShaderDatabase.Cutout);

        public float ShellTopAngle => 90f - this.TryGetComp<CompSpaceWeapon>().FacingAngle * Mathf.Rad2Deg;

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc, flip);
            SpaceTurretTopRenderer.Draw(this, this, drawLoc, Vector3.zero, 0f);
            cover ??= MaterialPool.MatFrom("Things/Building/SpaceWeapons/Casemate_Cover", ShaderDatabase.Cutout);
            Vector2 pivot = SpaceWeaponGeometry.CasematePivotOffset(this);
            var coverCenter = drawLoc + new Vector3(pivot.x, 2f * Altitudes.AltInc, pivot.y);
            float size = def.building.turretTopDrawSize;
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(coverCenter,
                Rotation.AsQuat, new Vector3(size, 1f, size)), cover, 0);
        }
    }
}
