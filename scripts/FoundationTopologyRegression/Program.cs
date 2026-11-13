using System.Diagnostics;
using System.Reflection;
using DeltaV;
using DeltaV.Topology;
using Verse;

internal static class Program
{
    private const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int checks, states;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new Exception(message); }
    private static GridTopology Core(FoundationConnectivity index) =>
        (GridTopology)typeof(FoundationConnectivity).GetField("topology", Fields).GetValue(index);

    private static int Main()
    {
        try
        {
            Exhaustive(); RandomRectangles(); LargeMaps(); Lifetimes(); Membership();
            Console.WriteLine($"PASS {checks} checks; {states} differential states (production-source algorithm/index/service)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Exhaustive()
    {
        for (int mask = 0; mask < 512; mask++)
        {
            var map = new Map(3, 3);
            for (int i = 0; i < 9; i++) map.Set(i, (mask & (1 << i)) != 0 ? 1 : -1);
            var index = new FoundationConnectivity(3, 3, map.Read);
            Verify(map, index);
            for (int i = 0; i < 9; i++)
            {
                int old = map.Read(i);
                foreach (int value in new[] { old == -1 ? 1 : -1, old, 2, 0, old })
                { map.Set(i, value); index.Update(i, value); Verify(map, index); }
            }
        }
        foreach (var size in new[] { (1, 1), (1, 13), (13, 1) })
        {
            var map = new Map(size.Item1, size.Item2);
            var index = new FoundationConnectivity(size.Item1, size.Item2, map.Read);
            for (int i = 0; i < map.Owners.Length; i++)
            { map.Set(i, 1); index.Update(i, 1); Verify(map, index); }
        }
        Console.WriteLine($"Exhaustive masks, diagonal contacts, mixed/unassigned owners, zero/one-cell maps: {states} states");
    }

    private static void RandomRectangles()
    {
        foreach (int seed in new[] { 17, 29, 20261101, 20261102 })
        {
            var random = new Random(seed); var map = new Map(19, 13);
            for (int i = 0; i < map.Owners.Length; i++) map.Set(i, random.NextDouble() < .6 ? 1 : -1);
            var index = new FoundationConnectivity(19, 13, map.Read);
            for (int step = 0; step < 1800; step++)
            {
                int i = random.Next(map.Owners.Length), owner = step % 3 == 0 ? random.Next(-1, 4) : map.Read(i) == -1 ? 1 : -1;
                map.Set(i, owner); index.Update(i, owner); Verify(map, index);
                if (step % 211 == 0)
                { Core(index).CompactAll(); Verify(map, index); }
            }
        }
        var churn = new Map(4, 4); var churnIndex = new FoundationConnectivity(4, 4, churn.Read);
        var core = Core(churnIndex);
        var fg = (UnionFind)typeof(GridTopology).GetField("_fg", Fields).GetValue(core);
        int previousNodes = fg.NodeCount, compactions = 0;
        for (int step = 0; step < 4000; step++)
        {
            int owner = step % 2 == 0 ? 1 : -1;
            churn.Set(5, owner); churnIndex.Update(5, owner);
            if (fg.NodeCount < previousNodes) compactions++;
            previousNodes = fg.NodeCount; Verify(churn, churnIndex);
        }
        Check(compactions > 0, "must exercise automatic node-pool compaction");
        Console.WriteLine($"Random rectangles and reinsertion/compaction passed; automatic compactions={compactions}");
    }

    private static void LargeMaps()
    {
        foreach (string shape in new[] { "plate", "bridge", "ring", "random" })
        {
            var map = new Map(250, 250); var random = new Random(20261101);
            for (int y = 10; y < 240; y++) for (int x = 10; x < 240; x++)
            {
                bool occupied = shape switch
                {
                    "plate" => true,
                    "bridge" => x != 125 || y == 125,
                    "ring" => x == 10 || x == 239 || y == 10 || y == 239,
                    _ => random.NextDouble() < .59
                };
                if (occupied) map.Set(y * 250 + x, 1);
            }
            var index = new FoundationConnectivity(250, 250, map.Read);
            var service = new FoundationTopologyService(); var ship = new Ship(map, 1, 2510);
            for (int step = 0; step < 400; step++)
            {
                int i = shape == "bridge" ? 125 * 250 + 125 : shape == "ring" ? 10 * 250 + 125 : random.Next(62500);
                int owner = map.Read(i) >= 0 ? -1 : 1;
                map.Set(i, owner); index.Update(i, owner);
                service.NotifyChanged(map, map.cellIndices.IndexToCell(i));
                Verify(map, index);
                Check(service.HasResidual(ship) == BruteResidual(map, 1, 2510), $"service {shape}/{step}");
            }
            Console.WriteLine($"250x250 {shape}: 400 updates with exact residual/domain checks passed");
        }
    }

    private static void Lifetimes()
    {
        var map = new Map(250, 250);
        for (int y = 100; y < 125; y++) for (int x = 100; x < 140; x++) map.Set(y * 250 + x, 1);
        var service = new FoundationTopologyService(); var ship = new Ship(map, 1, 100 * 250 + 100);
        Check(!service.HasResidual(ship), "clean ship initialization");
        map.FoundationReads = 0;
        var clock = Stopwatch.StartNew();
        for (int n = 0; n < 10000; n++) Check(!service.HasResidual(ship), "stable clean query");
        clock.Stop();
        Check(map.FoundationReads == 0, "stable queries must not rescan foundation");
        int hole = 110 * 250 + 110;
        map.Foundations[hole] = false; // Persistent old owner may still be present during terrain callback.
        for (int n = 0; n < 1000; n++) service.NotifyChanged(map, map.cellIndices.IndexToCell(hole));
        Check(!service.HasResidual(ship) && map.FoundationReads == 1, "hole expansion coalesces and reads only one changed cell");
        map.Set(hole, 2); service.NotifyChanged(map, map.cellIndices.IndexToCell(hole));
        Check(service.HasResidual(ship), "touching foreign owner must remain a distinct domain");
        map.Set(hole, 1); service.NotifyChanged(map, map.cellIndices.IndexToCell(hole));
        Check(!service.HasResidual(ship), "ownership reassignment repairs clean conclusion");
        map.Set(0, 0); service.NotifyChanged(map, map.cellIndices.IndexToCell(0));
        Check(service.HasResidual(ship), "unassigned island blocks thrust");
        map.Set(0, -1); service.NotifyChanged(map, map.cellIndices.IndexToCell(0));
        Check(!service.HasResidual(ship), "removal of unassigned island clears residual");
        Check(service.HasResidual(new Ship(map, 1, 0)), "moving/replacing seed requires live anchor membership");
        map.Set(0, 1); service.Forget(map);
        Check(service.HasResidual(ship), "restore/representation replacement rebuilds from current facts");
        var freshGame = new FoundationTopologyService();
        Check(freshGame.HasResidual(ship), "new game service does not reuse old cache");
        service.Forget(map); map.Disposed = true;
        service.NotifyChanged(map, map.cellIndices.IndexToCell(0));
        var entries = (System.Collections.IDictionary)typeof(FoundationTopologyService).GetField("maps", Fields).GetValue(service);
        Check(entries.Count == 0, "removed map events do not resurrect entries");
        Console.WriteLine($"Lifecycle/coalescing passed; 10,000 stable service queries: {clock.Elapsed.TotalMilliseconds:F3} ms, zero foundation reads");
    }

    private static void Verify(Map map, FoundationConnectivity index)
    {
        states++;
        int w = map.Size.x, h = map.Size.z;
        var forward = new Dictionary<int, (int owner, int component)>();
        var inverse = new Dictionary<(int owner, int component), int>();
        foreach (int owner in map.Owners.Distinct())
        {
            var mask = map.Owners.Select((value, index) => value == owner && map.Foundations[index]).ToArray();
            var labels = Analyze(mask, w, h, false, out _);
            for (int i = 0; i < labels.Length; i++)
            {
                if (labels[i] == 0) continue;
                int root = index.ComponentAt(i);
                var label = (owner, labels[i]);
                Check(root != 0 && (!forward.TryGetValue(root, out var existing) || existing == label),
                    "different owner domains or disconnected cells merged");
                Check(!inverse.TryGetValue(label, out int oldRoot) || oldRoot == root, "owner domain split");
                forward[root] = label;
                inverse[label] = root;
            }
        }
        Check(index.ComponentCount == inverse.Count, "owner component count");
        foreach (int owner in new[] { 0, 1, 2, 3 })
            foreach (int seed in new[] { 0, map.Owners.Length / 2, map.Owners.Length - 1 })
                Check(index.HasResidual(owner, seed) == BruteResidual(map, owner, seed), "residual from the same graph");
    }

    private static void Membership()
    {
        var map = new Map(6, 3);
        var service = new FoundationTopologyService();
        var left = new IntVec3(1, 0, 1);
        var right = new IntVec3(3, 0, 1);
        var gap = new IntVec3(2, 0, 1);
        map.Set(map.cellIndices.CellToIndex(left), 7);
        map.Set(map.cellIndices.CellToIndex(right), 7);
        Check(service.DomainCount(map) == 2, "same owner can have two domains");
        Check(ReferenceEquals(service.DomainUnder(map, new[] { left, gap }), service.DomainAt(map, left)),
            "overhanging footprint belongs to the only covered domain");
        Check(service.DomainUnder(map, new[] { left, gap, right }) == null, "straddling footprint has no domain");
        Check(service.DomainUnder(map, new[] { gap }) == null, "adjacency without overlap has no domain");
        var building = new Building { Map = map, Footprint = new[] { left, gap, right } };
        map.thingGrid.Buildings.Add(building);
        Check(!service.BuildingsIn(map, service.DomainAt(map, left)).Contains(building),
            "ordinary cross-domain building is excluded from the move");
        map.Set(map.cellIndices.CellToIndex(left), -1);
        service.NotifyChanged(map, left);
        Check(ReferenceEquals(service.DomainUnder(map, new[] { left, gap, right }), service.DomainAt(map, right)),
            "footprint resolves to the remaining domain after removal");
        Check(service.BuildingsIn(map, service.DomainAt(map, right)).Contains(building),
            "same building moves with the remaining domain");
        building.Footprint = new[] { left, gap };
        map.StructureVersion++;
        Check(!service.BuildingsIn(map, service.DomainAt(map, right)).Contains(building),
            "building change refreshes membership without changing topology");
    }

    private static bool BruteResidual(Map map, int owner, int seed)
    {
        var mask = new bool[map.Owners.Length];
        if (owner > 0) for (int i = 0; i < mask.Length; i++) mask[i] = map.Foundations[i] && map.Owners[i] == owner;
        var labels = Analyze(mask, map.Size.x, map.Size.z, false, out _);
        int root = seed >= 0 && seed < labels.Length ? labels[seed] : 0;
        for (int i = 0; i < labels.Length; i++)
            if (map.Foundations[i] && (root == 0 || labels[i] != root)) return true;
        return false;
    }
    private static int[] Analyze(bool[] mask, int w, int h, bool eight, out int count)
    {
        int[] labels = new int[mask.Length], queue = new int[mask.Length]; count = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || labels[i] != 0) continue;
            labels[i] = ++count; int head = 0, tail = 0; queue[tail++] = i;
            while (head < tail)
            {
                int v = queue[head++], x = v % w, y = v / w;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx == 0 && dy == 0) || (!eight && dx != 0 && dy != 0)) continue;
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || xx >= w || yy < 0 || yy >= h) continue;
                    int q = yy * w + xx;
                    if (mask[q] && labels[q] == 0) { labels[q] = count; queue[tail++] = q; }
                }
            }
        }
        return labels;
    }
}
