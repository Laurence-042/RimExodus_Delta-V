using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    public class CompProperties_MissileAutoloader : CompProperties
    {
        public List<ThingDef> magazineDefs = new List<ThingDef>();
        public List<IntVec3> magazineRootOffsets = new List<IntVec3>();

        public CompProperties_MissileAutoloader()
        {
            compClass = typeof(CompMissileAutoloader);
        }
    }

    /// <summary>Moves one stocked missile into the launcher's ready chamber.</summary>
    public class CompMissileAutoloader : ThingComp
    {
        public override void CompTick()
        {
            base.CompTick();
            if (!parent.IsHashIntervalTick(60) || !parent.Spawned || parent.Map == null) return;
            CompChangeableProjectile chamber = parent.TryGetComp<CompChangeableProjectile>();
            if (chamber == null) return;
            if (chamber.Loaded && !chamber.allowedShellsSettings.AllowedToAccept(chamber.LoadedShell))
                GenPlace.TryPlaceThing(chamber.RemoveShell(), parent.Position, parent.Map, ThingPlaceMode.Near);
            if (chamber.Loaded) return;
            CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn) return;

            int ownerId = SpaceWeaponGeometry.OwnerIdOf(parent);
            if (ownerId <= 0) return;
            foreach (Building_Storage magazine in SpaceMissileMagazineGeometry.ConnectedMagazines(parent))
            {
                if (magazine.Faction != parent.Faction
                    || SpaceWeaponGeometry.OwnerIdOf(magazine) != ownerId) continue;
                foreach (Thing item in magazine.GetSlotGroup().HeldThings)
                {
                    if (item == null || !item.Spawned || !magazine.OccupiedRect().Contains(item.Position)
                        || item.stackCount <= 0 || item.def.projectileWhenLoaded == null
                        || parent.Map.reservationManager.IsReservedByAnyoneOf(item, parent.Faction)
                        || !magazine.GetStoreSettings().AllowedToAccept(item)
                        || !chamber.allowedShellsSettings.AllowedToAccept(item)) continue;
                    Thing missile = item.SplitOff(1);
                    if (missile == null) continue;
                    chamber.LoadShell(missile.def, 1);
                    missile.Destroy(DestroyMode.Vanish);
                    return;
                }
            }
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            base.PostDestroy(mode, previousMap);
            if (mode == DestroyMode.Vanish || previousMap == null) return;
            CompChangeableProjectile chamber = parent.TryGetComp<CompChangeableProjectile>();
            if (chamber?.Loaded == true)
                GenPlace.TryPlaceThing(chamber.RemoveShell(), parent.Position, previousMap, ThingPlaceMode.Near);
        }
    }
}
