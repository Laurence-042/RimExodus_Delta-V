using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    public sealed class CompProperties_StructureMass : CompProperties
    {
        public CompProperties_StructureMass() => compClass = typeof(CompStructureMass);
    }

    public sealed class CompStructureMass : ThingComp
    {
        private readonly Dictionary<Thing, double> contributions = new Dictionary<Thing, double>();
        private bool initialized;
        private bool storageReady;
        private double current;
        private double published;
        internal int DueTick { get; private set; } = -1;

        private ShipStructureIndex Structures => Current.Game?.GetComponent<UniverseManager>()?.ShipStructure;
        public float CurrentMass { get { Initialize(); return (float)Math.Max(0, current); } }
        internal double PublishedMass { get { Initialize(); return published; } }

        internal static double StackMass(Thing item) => (double)item.GetStatValue(StatDefOf.Mass) * item.stackCount;

        private double Contribution(Thing item)
        {
            if (parent is ISpaceCargoContainer) return StackMass(item);
            CompTransporter transporter = parent.GetComp<CompTransporter>();
            return transporter is CompVehicleCargo eva ? eva.MassOf(item) : CompSpaceShuttle.TransportedMass(item);
        }

        private void Initialize()
        {
            if (initialized || parent is Building_SpaceCargoContainer && !storageReady) return;
            var cabinet = parent as ISpaceCargoContainer;
            ThingOwner inventory = parent.GetComp<CompTransporter>()?.innerContainer;
            if (cabinet == null && inventory == null) return;
            initialized = true;
            contributions.Clear();
            current = 0;
            IEnumerable<Thing> contents = cabinet != null ? cabinet.Contents : inventory;
            foreach (Thing item in contents)
            {
                double weight = Contribution(item);
                contributions[item] = weight;
                current += weight;
            }
            if (DueTick < 0) published = Math.Max(0, current);
        }

        internal void NotifyItem(Thing item, bool present)
        {
            if (item == null) return;
            Initialize();
            if (!initialized) return;
            contributions.TryGetValue(item, out double before);
            double after = present && !item.Destroyed ? Contribution(item) : 0d;
            if (present && !item.Destroyed) contributions[item] = after;
            else contributions.Remove(item);
            if (before == after) return;
            current += after - before;
            if (parent is ISpaceCargoContainer)
            {
                if (DueTick >= 0) return;
                DueTick = (Find.TickManager?.TicksGame ?? 0) + 600;
                if (parent.Spawned) Structures?.QueueMass(this);
            }
            else Publish();
        }

        internal void Publish()
        {
            Initialize();
            DueTick = -1;
            double before = published;
            published = Math.Max(0, current);
            if (parent.Spawned && before != published)
                Structures?.PublishMass(this, before, published);
        }

        internal (double mass, int due) CapturePublication() => (PublishedMass, DueTick);

        internal void RestorePublication((double mass, int due) state)
        {
            Initialize();
            Structures?.CancelMass(this);
            double before = published;
            published = state.mass;
            DueTick = state.due;
            if (DueTick < 0 && published != Math.Max(0, current))
                DueTick = (Find.TickManager?.TicksGame ?? 0) + 600;
            if (parent.Spawned)
            {
                Structures?.PublishMass(this, before, published);
                if (DueTick >= 0) Structures?.QueueMass(this);
            }
        }

        internal void StorageReady()
        {
            storageReady = true;
            double before = published;
            Initialize();
            if (!parent.Spawned) return;
            Structures?.PublishMass(this, before, published);
            if (DueTick >= 0) Structures?.QueueMass(this);
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            if (parent is Building_SpaceCargoContainer) return;
            Initialize();
            if (DueTick >= 0) Structures?.QueueMass(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            Structures?.CancelMass(this);
            if (parent is Building_SpaceCargoContainer)
            {
                initialized = false;
                storageReady = false;
            }
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap) => Structures?.CancelMass(this);
    }
}
