using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>Magazine sockets are declared on the launcher in north-facing root coordinates.</summary>
    public static class SpaceMissileMagazineGeometry
    {
        public static bool Fits(ThingDef launcherDef, IntVec3 launcherRoot, Rot4 launcherRot,
            ThingDef magazineDef, IntVec3 magazineRoot, Rot4 magazineRot)
        {
            CompProperties_MissileAutoloader props = launcherDef?.GetCompProperties<CompProperties_MissileAutoloader>();
            if (props == null || !props.magazineDefs.Contains(magazineDef)
                || magazineRot != launcherRot) return false;
            foreach (IntVec3 offset in props.magazineRootOffsets)
            {
                if (magazineRoot == launcherRoot + offset.RotatedBy(launcherRot)) return true;
            }
            return false;
        }

        public static IEnumerable<Building_Storage> ConnectedMagazines(Thing launcher)
        {
            CompProperties_MissileAutoloader props = launcher.def.GetCompProperties<CompProperties_MissileAutoloader>();
            if (props == null || launcher.Map == null) yield break;
            var seen = new HashSet<Building_Storage>();
            foreach (IntVec3 offset in props.magazineRootOffsets)
            {
                IntVec3 root = launcher.Position + offset.RotatedBy(launcher.Rotation);
                if (!root.InBounds(launcher.Map)) continue;
                foreach (Thing thing in launcher.Map.thingGrid.ThingsListAt(root))
                {
                    if (thing is Building_Storage magazine && seen.Add(magazine)
                        && Fits(launcher.def, launcher.Position, launcher.Rotation,
                            magazine.def, magazine.Position, magazine.Rotation))
                        yield return magazine;
                }
            }
        }
    }
}
