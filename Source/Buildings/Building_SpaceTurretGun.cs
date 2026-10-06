using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed class DefModExtension_SpaceTurretArt : DefModExtension
    {
        public string topTexPath;
    }

    /// <summary>Only substitutes the rotating artwork; vanilla turret draw and rotation stay intact.</summary>
    public class Building_SpaceTurretGun : Building_TurretGun, IShellTurretTopSource
    {
        private Material topMaterial;

        public Material ShellTopMaterial => TurretTopMaterial;

        public float ShellTopAngle => CurrentEffectiveVerb?.AimAngleOverride ?? top?.CurRotation ?? Rotation.AsAngle;

        public override Material TurretTopMaterial
        {
            get
            {
                string path = def.GetModExtension<DefModExtension_SpaceTurretArt>()?.topTexPath;
                if (path.NullOrEmpty()) return base.TurretTopMaterial;
                if (topMaterial == null) topMaterial = MaterialPool.MatFrom(path);
                return topMaterial;
            }
        }

        public override LocalTargetInfo TryFindNewTarget()
        {
            if (this.TryGetComp<CompSpaceWeapon>()?.WeaponProps.pointDefenseOnly == true)
                return LocalTargetInfo.Invalid;
            return base.TryFindNewTarget();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            bool pointDefenseOnly = this.TryGetComp<CompSpaceWeapon>()?.WeaponProps.pointDefenseOnly == true;
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                // 原版停火钮只控制同图 Verb；专用点防由武器组统一启停。
                if (pointDefenseOnly && gizmo is Command_Toggle toggle
                    && toggle.defaultLabel == "CommandHoldFire".Translate()) continue;
                yield return gizmo;
            }
        }

        public override void DrawExtraSelectionOverlays()
        {
            if (this.TryGetComp<CompSpaceWeapon>()?.WeaponProps.pointDefenseOnly == true) return;
            base.DrawExtraSelectionOverlays();
        }
    }
}
