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
    public class CompMissileAutoloader : ThingComp, ISpaceWeaponFireCommit
    {
        private UniverseManager Universe => Current.Game?.GetComponent<UniverseManager>();

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            QueueReload();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            Universe?.Weapons.CancelMissileReload(this);
        }

        public override void ReceiveCompSignal(string signal)
        {
            base.ReceiveCompSignal(signal);
            if (signal == "PowerTurnedOn" || signal == "PowerTurnedOff") QueueReload();
        }

        public void NotifyShotCommitted(SpaceWeaponShot shot) => QueueReload();

        internal void QueueReload()
        {
            if (parent.Spawned) Universe?.Weapons.QueueMissileReload(this);
        }

        internal bool TryReload()
        {
            if (!parent.Spawned || parent.Map == null || parent.Map.Disposed) return false;
            CompChangeableProjectile chamber = parent.TryGetComp<CompChangeableProjectile>();
            if (chamber == null) return false;
            if (chamber.Loaded && !chamber.allowedShellsSettings.AllowedToAccept(chamber.LoadedShell))
                GenPlace.TryPlaceThing(chamber.RemoveShell(), parent.Position, parent.Map, ThingPlaceMode.Near);
            if (chamber.Loaded) return false;
            CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn) return false;

            int ownerId = SpaceWeaponGeometry.OwnerIdOf(parent);
            if (ownerId <= 0) return false;
            bool waitingForReservation = false;
            foreach (Building_Storage magazine in SpaceMissileMagazineGeometry.ConnectedMagazines(parent))
            {
                if (magazine.Faction != parent.Faction
                    || SpaceWeaponGeometry.OwnerIdOf(magazine) != ownerId) continue;
                foreach (Thing item in magazine.GetSlotGroup().HeldThings)
                {
                    if (item == null || !item.Spawned || !magazine.OccupiedRect().Contains(item.Position)
                        || item.stackCount <= 0 || item.def.projectileWhenLoaded == null
                        || !magazine.GetStoreSettings().AllowedToAccept(item)
                        || !chamber.allowedShellsSettings.AllowedToAccept(item)) continue;
                    if (parent.Map.reservationManager.IsReservedByAnyoneOf(item, parent.Faction))
                    {
                        waitingForReservation = true;
                        continue;
                    }
                    Thing missile = item.SplitOff(1);
                    if (missile == null) continue;
                    chamber.LoadShell(missile.def, 1);
                    missile.Destroy(DestroyMode.Vanish);
                    return false;
                }
            }
            return waitingForReservation;
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

    /// <summary>原版设施链接负责发现相邻发射架；最终槽位仍由 Fits 判定。</summary>
    public class CompMissileMagazineFacility : CompFacility
    {
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            OnLinkAdded += NotifyLinkChanged;
            OnLinkRemoved += NotifyLinkChanged;
            base.PostSpawnSetup(respawningAfterLoad);
            NotifyStockChanged();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            OnLinkAdded -= NotifyLinkChanged;
            OnLinkRemoved -= NotifyLinkChanged;
        }

        private void NotifyLinkChanged(CompFacility facility, Thing launcher) =>
            launcher.TryGetComp<CompMissileAutoloader>()?.QueueReload();

        internal void NotifyStockChanged()
        {
            foreach (Thing launcher in LinkedBuildings)
                launcher.TryGetComp<CompMissileAutoloader>()?.QueueReload();
        }
    }
}
