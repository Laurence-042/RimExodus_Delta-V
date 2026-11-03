using System.Collections.Generic;
using System.Linq;
using AdaptiveStorage;
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
        private CompSpaceCargo Cargo => GetComp<CompSpaceCargo>();
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
            ReceivedThing -= Cargo.NotifyInventoryChanged;
            LostThing -= Cargo.NotifyInventoryChanged;
            ItemStackChanged -= Cargo.NotifyInventoryChanged;
            ReceivedThing += Cargo.NotifyInventoryChanged;
            LostThing += Cargo.NotifyInventoryChanged;
            ItemStackChanged += Cargo.NotifyInventoryChanged;
        }

        public bool TryStore(Thing thing)
        {
            if (!Spawned || !CompSpaceCargo.IsLooseCargo(thing) || !AnyFreeSlots) return false;
            foreach (IntVec3 cell in AllSlotCellsList())
            {
                if (cell.GetThingList(Map).Count(t => t.def.category == ThingCategory.Item)
                    >= GetMaxItemsForCell(cell)) continue;
                GenSpawn.Spawn(thing, cell, Map);
                Cargo.NotifyInventoryChanged(thing);
                return true;
            }
            return false;
        }

        public Thing Take(Thing thing, int count)
        {
            if (thing == null || count <= 0 || !StoredThings.Contains(thing)) return null;
            Thing taken = thing.SplitOff(count);
            Cargo.NotifyInventoryChanged(thing);
            return taken;
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Cargo.ApplyDestructionLoss(mode);
            if (Spawned && (mode == DestroyMode.Deconstruct || mode == DestroyMode.WillReplace
                || mode == DestroyMode.KillFinalize || mode == DestroyMode.KillFinalizeLeavingsOnly))
                foreach (Thing thing in Contents) Eject(thing, forbid: false);
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
            Cargo.NotifyInventoryChanged(thing);
            return true;
        }

        public override bool Accepts(Thing thing)
            => thing?.def.category == ThingCategory.Item && innerContainer.Count < CurrentSlotLimit && base.Accepts(thing);

        public Thing Take(Thing thing, int count)
        {
            if (thing == null || count <= 0 || !innerContainer.Contains(thing)) return null;
            Thing taken = innerContainer.Take(thing, count);
            BroadcastCompSignal(CrateContentsChanged);
            Cargo.NotifyInventoryChanged(thing);
            return taken;
        }

        // Native Building_Crate receives Hacked and calls Open/EjectContents once.
        public void OnLockedOut(Pawn pawn = null) { }
        public void OnHacked(Pawn pawn = null) => SetFaction(pawn?.Faction ?? Faction.OfPlayer);

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Cargo.ApplyDestructionLoss(mode);
            if (Spawned && (mode == DestroyMode.WillReplace || mode == DestroyMode.KillFinalizeLeavingsOnly))
                innerContainer.TryDropAll(Position, Map, ThingPlaceMode.Near);
            base.Destroy(mode);
        }
    }
}
