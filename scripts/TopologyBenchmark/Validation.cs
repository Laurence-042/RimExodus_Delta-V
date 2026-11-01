namespace TopologyBenchmark;

internal static class Validation
{
    internal static long States, Partitions, Invariants;
    internal static void Run(bool quick)
    {
        States = Partitions = Invariants = 0;
        // Exhaustive one-cell deletion/insertion from every 3x3 occupancy pattern.
        for (int mask = 0; mask < 512; mask++)
        {
            var initial = Enumerable.Range(0, 9).Select(v => (mask & (1 << v)) != 0).ToArray();
            var changes = new List<Change>();
            for (int v = 0; v < 9; v++)
            { changes.Add(new Change(!initial[v], new[] { v })); changes.Add(new Change(initial[v], new[] { v })); }
            CheckTrace(new("exhaustive3", "", 3, 3, initial, changes.ToArray(), mask), true);
        }
        Console.WriteLine($"Validation: exhaustive 3x3 passed ({States} states)");
        // Rectangular dimensions, empty/full, duplicate edits, mixed batches, reinsertion,
        // random splitting/merging and shrinking large components exercise level promotion.
        foreach (int seed in new[] { 17, 29, 101, 20261101 })
        {
            var r = new Random(seed); int w = seed % 2 == 0 ? 17 : 13, h = 11;
            bool[] initial = Enumerable.Range(0, w * h).Select(_ => r.NextDouble() < 0.59).ToArray();
            var changes = new List<Change>();
            for (int i = 0; i < (quick ? 200 : 1600); i++)
                changes.Add(new Change(r.Next(2) == 0,
                    Enumerable.Range(0, i % 3 == 0 ? r.Next(2, 18) : 1).Select(_ => r.Next(w * h)).ToArray()));
            CheckTrace(new("random_rect", "", w, h, initial, changes.ToArray(), seed), true);
        }
        foreach (var size in new[] { (1, 1), (1, 19), (19, 1), (2, 2) })
        {
            var all = Enumerable.Range(0, size.Item1 * size.Item2).ToArray();
            CheckTrace(new("boundaries", "", size.Item1, size.Item2, new bool[all.Length],
                new[] { new Change(true, all), new Change(true, all), new Change(false, all), new Change(false, all) }, 0), true);
        }
        // Exceed reference's 4*paddedArea node-pool threshold repeatedly.
        var churn = new Workload("compaction", "", 16, 16, new bool[256],
            Enumerable.Range(0, 12000).Select(i => new Change(i % 2 == 0, new[] { 100 })).ToArray(), 0);
        var topology = new TopologyGrid(16, 16, churn.Initial, false);
        var oracle = new BfsGrid(16, 16, churn.Initial);
        foreach (Change c in churn.Changes)
        { topology.Apply(c); oracle.Apply(c); Compare(topology, oracle, 256, "compaction"); }
        topology.Validate();
        if (topology.Topology.StatCompactions == 0) throw new Exception("Compaction test did not reach threshold");
        Console.WriteLine($"Validation: random/batch/compaction passed ({States} states; compactions={topology.Topology.StatCompactions})");
    }
    internal static void CheckTrace(Workload work, bool internals)
    {
        var oracle = new BfsGrid(work.Width, work.Height, work.Initial);
        IGrid[] candidates = {
            new TopologyGrid(work.Width, work.Height, work.Initial, false),
            new TopologyGrid(work.Width, work.Height, work.Initial, true),
            new HltGrid(work.Width, work.Height, work.Initial)
        };
        for (int step = -1; step < work.Changes.Length; step++)
        {
            if (step >= 0)
            {
                Change c = work.Changes[step]; oracle.Apply(c);
                foreach (IGrid candidate in candidates) candidate.Apply(c);
            }
            foreach (IGrid candidate in candidates)
                Compare(candidate, oracle, work.Initial.Length, $"{work.Name}/seed={work.Seed}/step={step}/{candidate.GetType().Name}");
            if (internals || step == -1 || step == work.Changes.Length - 1)
            {
                foreach (IGrid candidate in candidates)
                {
                    if (candidate is HltGrid hlt) hlt.Validate();
                    if (candidate is TopologyGrid topo) topo.Validate();
                    Invariants++;
                }
            }
        }
    }
    private static void Compare(IGrid candidate, BfsGrid oracle, int n, string context)
    {
        States++;
        if (candidate.Count != oracle.Count) throw new Exception($"{context}: count {candidate.Count} != {oracle.Count}");
        var labels = new int[n]; candidate.WriteLabels(labels);
        var forward = new Dictionary<int, int>(); var inverse = new Dictionary<int, int>();
        for (int v = 0; v < n; v++)
        {
            int a = labels[v], b = oracle.Labels[v];
            if ((a == 0) != (b == 0)) throw new Exception($"{context}: occupancy at {v}");
            if (a == 0) continue;
            if (forward.TryGetValue(a, out int b0) && b0 != b) throw new Exception($"{context}: merged disconnected components");
            if (inverse.TryGetValue(b, out int a0) && a0 != a) throw new Exception($"{context}: split connected component");
            forward[a] = b; inverse[b] = a;
        }
        // Every vertex: self query, and query against a deterministic other vertex.
        // Full bijective partition check above is stronger than sampled pair queries.
        for (int v = 0; v < n; v += n <= 512 ? 1 : 97)
        {
            int b = (v * 31 + 7) % n;
            if (candidate.Connected(v, v) != oracle.Present[v] || candidate.Connected(v, b) != oracle.Connected(v, b))
                throw new Exception($"{context}: Connected API mismatch at {v},{b}");
        }
        Partitions++;
    }
}
