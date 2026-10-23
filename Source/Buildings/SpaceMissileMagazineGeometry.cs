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

        public static IEnumerable<CompMissileMagazineFacility> ConnectedMagazines(Thing launcher)
        {
            if (launcher?.Map == null) yield break;
            CompAffectedByFacilities links = launcher.TryGetComp<CompAffectedByFacilities>();
            if (links == null) yield break;
            foreach (Thing facility in links.LinkedFacilitiesListForReading)
            {
                if (facility.TryGetComp<CompMissileMagazineFacility>() is { } magazine && facility.Spawned
                    && facility.Map == launcher.Map
                    && Fits(launcher.def, launcher.Position, launcher.Rotation,
                        facility.def, facility.Position, facility.Rotation))
                    yield return magazine;
            }
        }
    }
}
