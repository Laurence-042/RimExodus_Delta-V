using System;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    public interface IPocketMapHostProvider
    {
        Map GetOrCreateHostMap();
    }

    /// <summary>Delta-V host contract backed by the framework's non-worldtile host service.</summary>
    public sealed class PocketMapHostService : IPocketMapHostProvider
    {
        private readonly RimExodus.Maps.PocketMapHostService shared;
        public Map Current => shared.Current;
        public PocketMapHostService(Func<PocketMapParent, bool> isHost,
            Func<PocketMapParent> createHost, MapGeneratorDef generator, IntVec3 size)
        {
            shared = new RimExodus.Maps.PocketMapHostService(isHost, createHost, generator, size);
        }
        public Map GetOrCreateHostMap() => shared.GetOrCreateHostMap();
        public void Reset() => shared.Reset();
    }
}
