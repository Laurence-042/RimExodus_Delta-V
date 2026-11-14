using Verse;

namespace DeltaV
{
    /// <summary>Delta-V dormancy policy expressed through REF tick rates.</summary>
    internal sealed class MapDormancyService
    {
        public bool IsDormant(Map map) => RimExodus.Maps.MapRuntime.Current.TickRates.IsStopped(map);
        public void Sleep(Map map, string reason)
        {
            if (map == null || map.Disposed) return;
            RimExodus.Maps.MapRuntime.Current.TickRates.Set(map, SharedMapBackend.Owner, 0);
            Find.ColonistBar?.MarkColonistsDirty();
        }
        public void Wake(Map map, string reason)
        {
            RimExodus.Maps.MapRuntime.Current.TickRates.Release(map, SharedMapBackend.Owner);
            Find.ColonistBar?.MarkColonistsDirty();
        }
        public void Forget(Map map) => RimExodus.Maps.MapRuntime.Current.Forget(map);
        public void Reset() => RimExodus.Maps.MapRuntime.Current.TickRates.ReleaseOwner(SharedMapBackend.Owner);
    }
}
