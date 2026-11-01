// Only the map/identity boundary is substituted. Algorithm, owner-count index and
// event coalescing/cache lifetime service compile directly from production source.
namespace Verse
{
    public readonly record struct IntVec3(int x, int y, int z)
    {
        public bool InBounds(Map map) => x >= 0 && z >= 0 && x < map.Size.x && z < map.Size.z;
    }
    public sealed class CellIndices
    {
        private readonly int width;
        public CellIndices(int width) { this.width = width; }
        public int CellToIndex(IntVec3 c) => c.z * width + c.x;
        public IntVec3 IndexToCell(int i) => new(i % width, 0, i / width);
    }
    public sealed class Map
    {
        public readonly IntVec3 Size;
        public readonly CellIndices cellIndices;
        public readonly int[] Owners;
        public readonly bool[] Foundations;
        public readonly DeltaV.SubstructureOwnership Ownership;
        public bool Disposed;
        public long FoundationReads;
        public Map(int w, int h)
        {
            Size = new(w, 1, h); cellIndices = new(w);
            Owners = new int[w * h]; Foundations = new bool[w * h]; Ownership = new(this);
        }
        public void Set(int i, int owner) { Foundations[i] = owner >= 0; Owners[i] = Math.Max(0, owner); }
        public int Read(int i) => Foundations[i] ? Owners[i] : -1;
    }
}
namespace DeltaV
{
    public sealed class SubstructureOwnership
    {
        private readonly Verse.Map map;
        public SubstructureOwnership(Verse.Map map) { this.map = map; }
        public static SubstructureOwnership TryGet(Verse.Map map) => map.Ownership;
        public static bool HasFoundation(Verse.Map map, Verse.IntVec3 cell)
        { map.FoundationReads++; return cell.InBounds(map) && map.Foundations[map.cellIndices.CellToIndex(cell)]; }
        public int OwnerAt(Verse.IntVec3 cell) => Math.Max(0, map.Read(map.cellIndices.CellToIndex(cell)));
    }
    public sealed class ConsoleBody { public Verse.IntVec3 Position; }
    public sealed class ConsoleFixture { public readonly ConsoleBody parent = new(); }
    public readonly struct Ship
    {
        public readonly Verse.Map map;
        public readonly int ownerId;
        public readonly ConsoleFixture console;
        public Ship(Verse.Map map, int owner, int seed)
        { this.map = map; ownerId = owner; console = new(); console.parent.Position = map.cellIndices.IndexToCell(seed); }
    }
}
