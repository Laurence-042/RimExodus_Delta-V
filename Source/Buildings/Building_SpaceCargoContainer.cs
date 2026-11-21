using System.Collections.Generic;
using System.Linq;
using AdaptiveStorage;
using AdaptiveStorage.Utility;
using RimWorld;
using Verse;

namespace DeltaV
{
    // Each form keeps its native inventory and lifecycle; the terminal only needs
    // this common view of the real goods and the building holding them.
    public interface ISpaceCargoContainer : ILoadReferenceable
    {
        ThingWithComps Thing { get; }
        bool Spawned { get; }
        Map Map { get; }
        IntVec3 Position { get; }
        bool IsUnlocked { get; }
        IReadOnlyList<Thing> Contents { get; }
        int CurrentSlotLimit { get; }
        bool TryStore(Thing thing);
        Thing Take(Thing thing, int count);
    }

    public class Building_SpaceCargoContainer : ThingClass, ISpaceCargoContainer
    {
        public ThingWithComps Thing => this;
        private CompStructureMass Mass => GetComp<CompStructureMass>();
        public bool IsUnlocked => true;
        public IReadOnlyList<Thing> Contents => StoredThings.ToArray();

        public override void Notify_ColorChanged()
        {
            base.Notify_ColorChanged();
            // ASF retains its own printed materials beyond vanilla's Graphic cache.
            if (Spawned) Renderer?.SetAllPrintDatasDirty();
        }

        protected override void OnSpawn(Map map, SpawnMode spawnMode)
        {
            base.OnSpawn(map, spawnMode);
            // ASF reconstructs StoredThings after the vanilla comp spawn callbacks.
            Mass.StorageReady();
            ReceivedThing += GainMass;
            LostThing += LoseMass;
            ItemStackChanged += GainMass;
        }

        protected override void OnDeSpawn(DestroyMode destroyMode, SpawnMode deSpawnMode)
        {
            ReceivedThing -= GainMass;
            LostThing -= LoseMass;
            ItemStackChanged -= GainMass;
            base.OnDeSpawn(destroyMode, deSpawnMode);
        }

        private void GainMass(Thing item) => Mass.NotifyItem(item, true);
        private void LoseMass(Thing item) => Mass.NotifyItem(item, false);

        public bool TryStore(Thing thing)
        {
            if (!Spawned || !CompSpaceCargo.IsLooseCargo(thing) || !AnyFreeSlots) return false;
            foreach (IntVec3 cell in AllSlotCellsList())
            {
                if (cell.GetThingList(Map).Count(t => t.def.category == ThingCategory.Item)
                    >= GetMaxItemsForCell(cell)) continue;
                GenSpawn.Spawn(thing, cell, Map);
                return true;
            }
            return false;
        }

        public Thing Take(Thing thing, int count)
        {
            if (thing == null || count <= 0 || !StoredThings.Contains(thing)) return null;
            Thing taken = thing.SplitOff(count);
            return taken;
        }

        public override Thing Eject(Thing item, int count = int.MaxValue, bool forbid = true,
            in IntVec3 dropOffset = default)
        {
            Map map = Map;
            SpaceScatterService scatter = SpaceScatterService.ForCargoMap(map);
            IntVec3 dropCell = Position + dropOffset;
            if (scatter == null || !dropCell.InBounds(map))
                return base.Eject(item, count, forbid, in dropOffset);

            item = item.SplitOff(System.Math.Min(count, item.stackCount));
            if (!GenDrop.TryDropSpawn(item, dropCell, map, ThingPlaceMode.Near, out Thing placed,
                nearPlaceValidator: cell => !map.ContainsStorageBuildingAt(in cell)))
            {
                scatter.PlaceCargoOverflow(item, map, dropCell);
                placed = item;
            }
            if (forbid && placed?.TryGetComp<CompForbiddable>() is CompForbiddable forbiddable)
                forbiddable.Forbidden = true;
            return placed;
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            bool killed = CompSpaceCargo.IsCombatDestruction(mode);
            if (Spawned && (killed || mode == DestroyMode.Deconstruct || mode == DestroyMode.WillReplace))
            {
                SpaceScatterService scatter = killed ? SpaceScatterService.ForCargoMap(Map) : null;
                if (scatter != null)
                {
                    Map map = Map;
                    foreach (Thing item in Contents)
                        if (CompSpaceCargo.ApplyDestructionLoss(item))
                            scatter.QueueDestroyedStorageItem(item, map);
                }
                else
                    foreach (Thing thing in Contents) Eject(thing, forbid: false);
            }
            base.Destroy(mode);
        }
    }

    public class Building_SealedSpaceCargoContainer : Building_Crate, ISpaceCargoContainer, IHackable
    {
        public ThingWithComps Thing => this;
        private CompSpaceCargo Cargo => GetComp<CompSpaceCargo>();
        public bool IsUnlocked => GetComp<CompHackable>()?.IsHacked == true;
        public IReadOnlyList<Thing> Contents => innerContainer.ToArray();
        public int CurrentSlotLimit => Cargo.SavedSlotLimit;

        public bool TryStore(Thing thing)
        {
            if (!CompSpaceCargo.IsLooseCargo(thing) || innerContainer.Count >= CurrentSlotLimit) return false;
            // Preserve stack identity, quantity and component state across mode changes.
            if (!innerContainer.TryAdd(thing, canMergeWithExistingStacks: false)) return false;
            BroadcastCompSignal(CrateContentsChanged);
            return true;
        }

        public override bool Accepts(Thing thing)
            => thing?.def.category == ThingCategory.Item && innerContainer.Count < CurrentSlotLimit && base.Accepts(thing);

        public Thing Take(Thing thing, int count)
        {
            if (thing == null || count <= 0 || !innerContainer.Contains(thing)) return null;
            Thing taken = innerContainer.Take(thing, count);
            BroadcastCompSignal(CrateContentsChanged);
            return taken;
        }

        public void OnLockedOut(Pawn pawn = null) { }
        public void OnHacked(Pawn pawn = null) => SetFaction(pawn?.Faction ?? Faction.OfPlayer);

        protected override void ReceiveCompSignal(string signal)
        {
            // Building_Crate opens on Hacked. The native comp still receives the signal.
            if (signal != "Hacked") base.ReceiveCompSignal(signal);
        }

        internal void EjectIntoSpace()
        {
            if (Spawned && CanOpen)
                SpaceScatterService.ForCargoMap(Map)?.QueueCrateEjection(this);
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            if (Spawned && (mode == DestroyMode.WillReplace || (mode == DestroyMode.KillFinalizeLeavingsOnly
                && SpaceScatterService.ForCargoMap(Map) == null)))
                innerContainer.TryDropAll(Position, Map, ThingPlaceMode.Near);
            base.Destroy(mode);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            IntVec3 cell = Position;
            SpaceScatterService scatter = CompSpaceCargo.IsCombatDestruction(mode)
                ? SpaceScatterService.ForCargoMap(map)
                : null;
            Thing[] survivors = scatter == null ? null : Contents
                .Where(CompSpaceCargo.ApplyDestructionLoss).ToArray();
            base.DeSpawn(mode);
            // Release the surviving inventory before the native casket clears it.
            if (survivors != null)
                foreach (Thing item in survivors)
                    scatter.PlaceCargoOverflow(item, map, cell, fromDestruction: true);
        }
    }
}
