using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Moves one stored missile into the launcher's ready chamber.</summary>
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

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent.Faction != Faction.OfPlayer || !parent.Spawned
                || parent.TryGetComp<CompChangeableProjectile>()?.Loaded != true) yield break;
            yield return new Command_BatchSetting<CompMissileAutoloader>("MissileLauncherEject", this,
                loaders =>
                {
                    foreach (CompMissileAutoloader loader in loaders) loader.EjectLoadedShell();
                })
            {
                defaultLabel = "CommandExtractShell".Translate(),
                defaultDesc = "CommandExtractShellDesc".Translate(),
                icon = parent.TryGetComp<CompChangeableProjectile>().LoadedShell.uiIcon,
                iconDrawScale = GenUI.IconDrawScale(parent.TryGetComp<CompChangeableProjectile>().LoadedShell)
            };
        }

        private void EjectLoadedShell()
        {
            CompChangeableProjectile chamber = parent.TryGetComp<CompChangeableProjectile>();
            if (!parent.Spawned || chamber?.Loaded != true) return;
            Thing shell = ThingMaker.MakeThing(chamber.LoadedShell);
            shell.stackCount = chamber.loadedCount;
            if (GenPlace.TryPlaceThing(shell, parent.Position, parent.Map, ThingPlaceMode.Near))
                chamber.LoadShell(null, 0);
            else shell.Destroy(DestroyMode.Vanish);
        }

        internal void QueueReload()
        {
            if (parent.Spawned) Universe?.Weapons.QueueMissileReload(this);
        }

        internal void TryReload()
        {
            if (!parent.Spawned || parent.Map == null || parent.Map.Disposed) return;
            CompChangeableProjectile chamber = parent.TryGetComp<CompChangeableProjectile>();
            if (chamber == null) return;
            if (chamber.Loaded && !chamber.allowedShellsSettings.AllowedToAccept(chamber.LoadedShell))
                GenPlace.TryPlaceThing(chamber.RemoveShell(), parent.Position, parent.Map, ThingPlaceMode.Near);
            if (chamber.Loaded) return;
            CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn) return;

            int ownerId = SpaceWeaponGeometry.OwnerIdOf(parent);
            if (ownerId <= 0) return;
            foreach (CompMissileMagazineFacility magazine in SpaceMissileMagazineGeometry.ConnectedMagazines(parent))
            {
                if (magazine.parent.Faction != parent.Faction
                    || SpaceWeaponGeometry.OwnerIdOf(magazine.parent) != ownerId) continue;
                foreach (ThingDef shell in magazine.StoredMissiles.ToList())
                {
                    if (!chamber.allowedShellsSettings.AllowedToAccept(shell)
                        || !magazine.TryTake(shell)) continue;
                    chamber.LoadShell(shell, 1);
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

    public class CompProperties_MissileMagazineFacility : CompProperties_Facility
    {
        public int totalCapacity = 20;

        public CompProperties_MissileMagazineFacility()
        {
            compClass = typeof(CompMissileMagazineFacility);
        }
    }

    /// <summary>原版设施链接负责发现相邻发射架；弹药只以 Def 和数量保存在本 Comp。</summary>
    public class CompMissileMagazineFacility : CompFacility
    {
        private Dictionary<ThingDef, int> contents = new Dictionary<ThingDef, int>();
        private Dictionary<ThingDef, int> limits = new Dictionary<ThingDef, int>();
        private List<ThingDef> contentKeys;
        private List<int> contentValues;
        private List<ThingDef> limitKeys;
        private List<int> limitValues;

        public int TotalCapacity => ((CompProperties_MissileMagazineFacility)props).totalCapacity;
        public int StoredCount => contents.Values.Sum();
        public IEnumerable<ThingDef> StoredMissiles => contents.Where(pair => pair.Value > 0)
            .Select(pair => pair.Key).OrderBy(def => def.defName);

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref contents, "missileContents", LookMode.Def, LookMode.Value,
                ref contentKeys, ref contentValues);
            Scribe_Collections.Look(ref limits, "missileLimits", LookMode.Def, LookMode.Value,
                ref limitKeys, ref limitValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                contents ??= new Dictionary<ThingDef, int>();
                limits ??= new Dictionary<ThingDef, int>();
            }
        }

        public int CountOf(ThingDef missile) => missile != null && contents.TryGetValue(missile, out int count)
            ? count : 0;

        public int LimitOf(ThingDef missile) => missile != null && limits.TryGetValue(missile, out int limit)
            ? limit : TotalCapacity;

        public bool CanAccept(ThingDef missile) => missile?.GetModExtension<DefModExtension_SpaceMissileAmmo>() != null
            && missile.projectileWhenLoaded != null && StoredCount < TotalCapacity
            && CountOf(missile) < LimitOf(missile);

        public bool TryStore(ThingDef missile)
        {
            if (!CanAccept(missile)) return false;
            contents[missile] = CountOf(missile) + 1;
            NotifyStockChanged();
            return true;
        }

        public bool TryTake(ThingDef missile)
        {
            int count = CountOf(missile);
            if (count <= 0) return false;
            if (count == 1) contents.Remove(missile);
            else contents[missile] = count - 1;
            NotifyStockChanged();
            return true;
        }

        private void SetLimit(ThingDef missile, int count)
        {
            limits[missile] = count;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra()) yield return gizmo;
            if (parent.Faction != Faction.OfPlayer) yield break;
            yield return new Command_BatchSetting<CompMissileMagazineFacility>("MissileMagazineLimits",
                this, magazines =>
                {
                    var options = new List<FloatMenuOption>();
                    foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading
                        .Where(d => d.GetModExtension<DefModExtension_SpaceMissileAmmo>() != null)
                        .OrderBy(d => d.label))
                    {
                        ThingDef missile = def;
                        int firstLimit = magazines[0].LimitOf(def);
                        string limitLabel = magazines.All(m => m.LimitOf(def) == firstLimit)
                            ? firstLimit.ToString() : "DeltaV_MixedSettings".Translate();
                        options.Add(new FloatMenuOption(
                            "DeltaV_MissileMagazineLimitEntry".Translate(def.LabelCap, limitLabel),
                            () => Find.WindowStack.Add(new Dialog_Slider(
                                value => "DeltaV_MissileMagazineLimitSlider".Translate(missile.LabelCap, value),
                                0, magazines.Min(m => m.TotalCapacity), value =>
                                {
                                    foreach (CompMissileMagazineFacility magazine in magazines)
                                        if (magazine.parent.Spawned && !magazine.parent.Destroyed)
                                            magazine.SetLimit(missile, value);
                                }, magazines[0].LimitOf(missile)))));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                })
            {
                defaultLabel = "DeltaV_MissileMagazineLimits".Translate(),
                defaultDesc = "DeltaV_MissileMagazineLimitsDesc".Translate(TotalCapacity),
                icon = TexCommand.Install,
            };
            if (StoredCount > 0)
                yield return new Command_BatchSetting<CompMissileMagazineFacility>("MissileMagazineEject",
                    this, magazines =>
                    {
                        var options = new List<FloatMenuOption>();
                        foreach (ThingDef def in magazines.SelectMany(m => m.StoredMissiles).Distinct()
                            .OrderBy(d => d.label))
                        {
                            ThingDef missile = def;
                            options.Add(new FloatMenuOption(
                                "DeltaV_MissileMagazineEjectEntry".Translate(def.LabelCap,
                                    magazines.Sum(m => m.CountOf(def))),
                                () =>
                                {
                                    foreach (CompMissileMagazineFacility magazine in magazines)
                                        magazine.Eject(missile);
                                }));
                        }
                        Find.WindowStack.Add(new FloatMenu(options));
                    })
                {
                    defaultLabel = "DeltaV_MissileMagazineEject".Translate(),
                    icon = TexCommand.SelectCarriedThing,
                };
        }

        private void Eject(ThingDef missile)
        {
            if (!parent.Spawned || CountOf(missile) == 0) return;
            Thing item = ThingMaker.MakeThing(missile);
            int amount = System.Math.Min(CountOf(missile), missile.stackLimit);
            item.stackCount = amount;
            if (GenPlace.TryPlaceThing(item, parent.Position, parent.Map, ThingPlaceMode.Near))
            {
                int left = CountOf(missile) - amount;
                if (left == 0) contents.Remove(missile);
                else contents[missile] = left;
                NotifyStockChanged();
            }
            else item.Destroy(DestroyMode.Vanish);
        }

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

        public override string CompInspectStringExtra()
        {
            string facilityInfo = base.CompInspectStringExtra();
            string inventory = "DeltaV_MissileMagazineStored".Translate(StoredCount, TotalCapacity)
                + (StoredCount == 0 ? "" : "\n" + string.Join("\n", StoredMissiles.Select(def =>
                    "DeltaV_MissileMagazineCountEntry".Translate(def.LabelCap, CountOf(def)).ToString())));
            return string.IsNullOrEmpty(facilityInfo) ? inventory : facilityInfo + "\n" + inventory;
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            base.PostDestroy(mode, previousMap);
            if (mode == DestroyMode.Vanish || previousMap == null) return;
            foreach (var pair in contents)
            {
                int remaining = pair.Value;
                while (remaining > 0)
                {
                    Thing item = ThingMaker.MakeThing(pair.Key);
                    item.stackCount = System.Math.Min(remaining, pair.Key.stackLimit);
                    remaining -= item.stackCount;
                    if (!GenPlace.TryPlaceThing(item, parent.Position, previousMap, ThingPlaceMode.Near))
                        item.Destroy(DestroyMode.Vanish);
                }
            }
        }
    }
}
