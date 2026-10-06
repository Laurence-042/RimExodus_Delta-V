using UnityEngine;
using Verse;

namespace DeltaV
{
    public class CompProperties_LoadedProjectileGraphic : CompProperties
    {
        public Vector2 drawSize = Vector2.one;
        public Vector2 drawOffset = Vector2.zero;
        public float altitudeOffset = 0.015f;

        public CompProperties_LoadedProjectileGraphic()
        {
            compClass = typeof(CompLoadedProjectileGraphic);
        }
    }

    /// <summary>Draws the projectile chosen by the weapon's existing magazine selection.</summary>
    public class CompLoadedProjectileGraphic : ThingComp
    {
        private ThingDef cachedProjectile;
        private Graphic cachedGraphic;

        public override void PostDraw()
        {
            CompSpaceWeapon weapon = parent.TryGetComp<CompSpaceWeapon>();
            CompSpaceWeaponMagazine magazine = weapon?.SelectMagazine();
            if (magazine == null)
                return;

            ThingDef projectile = magazine.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef;
            GraphicData source = projectile?.graphicData;
            if (source == null || source.texPath.NullOrEmpty())
                return;

            if (cachedProjectile != projectile)
            {
                CompProperties_LoadedProjectileGraphic settings = (CompProperties_LoadedProjectileGraphic)props;
                cachedGraphic = GraphicDatabase.Get(source.graphicClass, source.texPath,
                    source.shaderType?.Shader ?? ShaderDatabase.Cutout, settings.drawSize,
                    source.color, source.colorTwo, source.maskPath);
                cachedProjectile = projectile;
            }

            CompProperties_LoadedProjectileGraphic visual = (CompProperties_LoadedProjectileGraphic)props;
            Vector3 offset = parent.Rotation.AsQuat *
                new Vector3(visual.drawOffset.x, 0f, visual.drawOffset.y);
            Vector3 center = parent.DrawPos + offset;
            center.y = parent.def.Altitude + visual.altitudeOffset;
            cachedGraphic.Draw(center, parent.Rotation, parent);
        }
    }
}
