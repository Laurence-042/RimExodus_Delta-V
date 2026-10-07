using System.Collections.Generic;
using Verse;

namespace DeltaV
{
    public enum NpcDoctrine : byte
    {
        Standoff,
        Brawler,
        MissileBoat,
        Boarder,
        Retreat
    }

    public sealed class PrefabDoctrineRule : IExposable
    {
        public string prefabPath;
        public List<NpcDoctrine> priorities = new List<NpcDoctrine>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref prefabPath, "prefabPath");
            Scribe_Collections.Look(ref priorities, "priorities", LookMode.Value);
        }
    }

    public static class NpcDoctrineDefaults
    {
        public static List<NpcDoctrine> Create() => new List<NpcDoctrine>
        {
            NpcDoctrine.Standoff, NpcDoctrine.Brawler, NpcDoctrine.Retreat
        };
    }
}
