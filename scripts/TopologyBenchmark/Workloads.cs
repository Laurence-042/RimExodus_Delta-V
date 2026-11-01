namespace TopologyBenchmark;

internal sealed record Workload(string Name, string Description, int Width, int Height,
    bool[] Initial, Change[] Changes, int Seed)
{
    internal int ChangedCells => Changes.Sum(c => c.Cells.Length);
}

internal static class Workloads
{
    internal static Workload[] Create(int events, int seed)
    {
        const int w = 250, h = 250;
        bool[] Map() => new bool[w * h];
        void Rect(bool[] a, int x, int y, int sx, int sy)
        { for (int j = y; j < y + sy; j++) for (int i = x; i < x + sx; i++) a[j * w + i] = true; }
        var ship = Map(); Rect(ship, 105, 113, 40, 24);
        var fleet = Map();
        for (int y = 35; y <= 175; y += 70) for (int x = 30; x <= 170; x += 70) Rect(fleet, x, y, 40, 24);
        var dense = Map(); Rect(dense, 10, 10, 230, 230);
        var battle = Map(); Rect(battle, 65, 90, 120, 70);
        var bridge = Map(); Rect(bridge, 20, 35, 104, 180); Rect(bridge, 125, 35, 105, 180);
        bridge[125 * w + 124] = true;
        var ring = Map();
        for (int y = 10; y < 240; y++) for (int x = 10; x < 240; x++)
            if (x == 10 || x == 239 || y == 10 || y == 239) ring[y * w + x] = true;
        var noise = Map(); var rng = new Random(seed);
        for (int i = 0; i < noise.Length; i++) noise[i] = rng.NextDouble() < 0.59;
        return new[]
        {
            Paired("small_ship", "40x24 solid foundation, isolated hits and repairs", ship, 1, events, seed),
            Paired("nine_ships", "Nine separated 40x24 foundations, localized hits and repairs", fleet, 1, events, seed + 1),
            Paired("dense_plate", "230x230 solid foundation, isolated hits and repairs (dense stress)", dense, 1, events, seed + 2),
            Paired("blast_repair_3", "120x70 foundation, 3x3 destruction/repair batches", battle, 3, events / 2, seed + 3),
            Paired("blast_repair_9", "120x70 foundation, 9x9 destruction/repair batches (large batch stress)", battle, 9, events / 2, seed + 4),
            Deletion("battle_erosion", "120x70 foundation, persistent radius-1..3 blast damage until trace ends", battle, events / 2, seed + 5),
            Fixed("bridge_toggle", "Two large plates joined by one foundation cell; repeated real splits", bridge, 125 * w + 124, events, seed + 6),
            Fixed("ring_toggle", "One-cell ring; break/close a large background hole without foreground split", ring, 10 * w + 125, events, seed + 7),
            Mixed("random59", "59% iid occupancy, arbitrary toggles; fragmentation stress, not a typical ship", noise, events, seed + 8)
        };
    }
    private static Workload Paired(string name, string description, bool[] initial, int side, int events, int seed)
    {
        var random = new Random(seed); var changes = new List<Change>();
        var candidates = Enumerable.Range(0, initial.Length).Where(v => initial[v]).ToArray();
        for (int i = 0; i < Math.Max(2, events) / 2; i++)
        {
            int v = candidates[random.Next(candidates.Length)]; var cells = new List<int>();
            for (int y = Math.Max(0, v / 250 - side / 2); y <= Math.Min(249, v / 250 + side / 2); y++)
                for (int x = Math.Max(0, v % 250 - side / 2); x <= Math.Min(249, v % 250 + side / 2); x++)
                    if (initial[y * 250 + x]) cells.Add(y * 250 + x);
            changes.Add(new Change(false, cells.ToArray())); changes.Add(new Change(true, cells.ToArray()));
        }
        return new(name, description, 250, 250, initial, changes.ToArray(), seed);
    }
    private static Workload Fixed(string name, string description, bool[] initial, int cell, int events, int seed) =>
        new(name, description, 250, 250, initial,
            Enumerable.Range(0, events / 2 * 2).Select(i => new Change(i % 2 == 1, new[] { cell })).ToArray(), seed);
    private static Workload Mixed(string name, string description, bool[] initial, int events, int seed)
    {
        var random = new Random(seed); var state = (bool[])initial.Clone(); var changes = new List<Change>();
        for (int i = 0; i < events; i++)
        { int v = random.Next(state.Length); state[v] = !state[v]; changes.Add(new Change(state[v], new[] { v })); }
        return new(name, description, 250, 250, initial, changes.ToArray(), seed);
    }
    private static Workload Deletion(string name, string description, bool[] initial, int events, int seed)
    {
        var random = new Random(seed); var state = (bool[])initial.Clone(); var changes = new List<Change>();
        for (int i = 0; i < events; i++)
        {
            var remaining = Enumerable.Range(0, state.Length).Where(v => state[v]).ToArray();
            if (remaining.Length == 0) break;
            int c = remaining[random.Next(remaining.Length)], r = random.Next(1, 4); var cells = new List<int>();
            for (int y = Math.Max(0, c / 250 - r); y <= Math.Min(249, c / 250 + r); y++)
                for (int x = Math.Max(0, c % 250 - r); x <= Math.Min(249, c % 250 + r); x++)
                    if ((x - c % 250) * (x - c % 250) + (y - c / 250) * (y - c / 250) <= r * r && state[y * 250 + x])
                    { cells.Add(y * 250 + x); state[y * 250 + x] = false; }
            changes.Add(new Change(false, cells.ToArray()));
        }
        return new(name, description, 250, 250, initial, changes.ToArray(), seed);
    }
}
