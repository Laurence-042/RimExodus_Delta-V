using System.Collections.Generic;
using Verse;

namespace DeltaV
{
    internal sealed class SpaceCargoMassUpdates
    {
        private const int Interval = 600;

        internal readonly struct Change
        {
            internal readonly CompSpaceCargo cabinet;
            internal readonly float previous;
            internal readonly float current;

            internal Change(CompSpaceCargo cabinet, float previous, float current)
            {
                this.cabinet = cabinet;
                this.previous = previous;
                this.current = current;
            }
        }

        private sealed class Batch
        {
            internal int nextTick = -1;
            internal readonly HashSet<CompSpaceCargo> pending = new HashSet<CompSpaceCargo>();
        }

        private readonly Dictionary<Map, Batch> maps = new Dictionary<Map, Batch>();

        private Batch For(Map map)
        {
            if (!maps.TryGetValue(map, out Batch batch))
            {
                batch = new Batch();
                maps.Add(map, batch);
            }
            return batch;
        }

        internal void Queue(Map map, CompSpaceCargo cabinet)
        {
            if (map != null && !map.Disposed) For(map).pending.Add(cabinet);
        }

        internal void Remove(Map map, CompSpaceCargo cabinet)
        {
            if (map != null && maps.TryGetValue(map, out Batch batch)) batch.pending.Remove(cabinet);
        }

        internal IReadOnlyList<Change> Collect(Map map)
        {
            Batch batch = For(map);
            int tick = Find.TickManager?.TicksGame ?? 0;
            if (batch.nextTick < 0) batch.nextTick = tick + Interval;
            if (batch.pending.Count == 0 || tick < batch.nextTick) return null;

            batch.nextTick = tick + Interval;
            List<Change> changes = null;
            foreach (CompSpaceCargo cabinet in batch.pending)
                if (cabinet.parent.Spawned && cabinet.parent.Map == map
                    && cabinet.RefreshStoredMass(out float previous, out float current))
                    (changes ??= new List<Change>()).Add(new Change(cabinet, previous, current));
            batch.pending.Clear();
            return changes;
        }

        internal void Forget(Map map) => maps.Remove(map);
    }
}
