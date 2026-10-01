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
    public class Building_SpaceTurretGun : Building_TurretGun
    {
        private Material topMaterial;

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
    }
}
