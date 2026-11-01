using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace TopologyBenchmark;

internal static class Program
{
    private static long sink;
    private static readonly string[] Algorithms = { "BFS-rebuild", "Topology-single", "Topology-batch", "HLT-ETT" };
    private sealed record Sample(string Scenario, string Algorithm, int Repeat, double InitMs,
        long InitRetainedBytes, long FinalRetainedBytes, long AllocatedBytes, int Gen0, int Gen1, int Gen2,
        double[] UpdateUs, double QueryNs, double ExportUs, long RepairCells, long Compactions,
        long Promotions, long Probes, long Links, long Cuts);
    private sealed record Summary(string Scenario, string Algorithm, int Events, int ChangedCells,
        double InitMs, double UpdateMeanUs, double UpdateP50Us, double UpdateP95Us, double UpdateP99Us,
        double UpdateMaxUs, double FirstEventUs, double MeanPerCellUs, double DeleteMeanUs, double AddMeanUs,
        double QueryNs, double ExportUs, double BytesPerEvent, double InitMiB, double FinalMiB,
        double MinRepeatMeanUs, double MaxRepeatMeanUs, long RepairCells, long Compactions, long Promotions, long Probes);

    private static int Main(string[] args)
    {
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            bool quick = args.Contains("--quick"), testOnly = args.Contains("--test-only");
            string Value(string key, string fallback) { int i = Array.IndexOf(args, key); return i >= 0 ? args[i + 1] : fallback; }
            int events = int.Parse(Value("--events", quick ? "120" : "1200"));
            int repeats = int.Parse(Value("--repeats", quick ? "2" : "5"));
            int seed = int.Parse(Value("--seed", "20261101"));
            if (events < 4 || repeats < 1) throw new ArgumentException("events >= 4, repeats >= 1 required");
            string output = Path.GetFullPath(Value("--output", "scripts/TopologyBenchmark/results/latest"));
            string source = Path.GetFullPath(Value("--reference", "references/TopologyLab/GridTopology.cs"));
            Directory.CreateDirectory(output);
            Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; seed={seed}");
            Validation.Run(quick);
            if (testOnly) { Console.WriteLine("PASS (test only)"); return 0; }
            Workload[] workloads = Workloads.Create(events, seed);
            string filter = Value("--scenario", "");
            if (filter.Length > 0) workloads = workloads.Where(w => w.Name == filter).ToArray();
            if (workloads.Length == 0) throw new ArgumentException("Unknown scenario");
            var samples = new List<Sample>(); var summaries = new List<Summary>();
            foreach (Workload work in workloads)
            {
                Console.WriteLine($"Validating {work.Name}: {work.Changes.Length} events, {work.ChangedCells} cell changes (every event, full partition)");
                Validation.CheckTrace(work, false);
                // Native batch and sequential adapter are identical for single-cell traces.
                string[] algorithms = work.Changes.All(c => c.Cells.Length == 1)
                    ? Algorithms.Where(a => a != "Topology-batch").ToArray() : Algorithms;
                foreach (string a in algorithms)
                {
                    IGrid warm = Create(a, work);
                    foreach (Change c in work.Changes.Take(100)) warm.Apply(c);
                    warm.WriteLabels(new int[work.Initial.Length]); warm.Connected(0, 1);
                }
                for (int rep = 0; rep < repeats; rep++)
                {
                    // Rotate execution order to reduce thermal/order bias.
                    for (int j = 0; j < algorithms.Length; j++)
                    {
                        string a = algorithms[(j + rep) % algorithms.Length];
                        samples.Add(Measure(work, a, rep));
                    }
                }
                foreach (string a in algorithms)
                {
                    Summary s = Aggregate(work, a, samples.Where(s => s.Scenario == work.Name && s.Algorithm == a).ToArray());
                    summaries.Add(s);
                    Console.WriteLine($"  {a,-16} mean {s.UpdateMeanUs,10:F3} us/event, p99 {s.UpdateP99Us,10:F3}, max {s.UpdateMaxUs,10:F3}, export {s.ExportUs,9:F1} us");
                }
                Save(output, source, seed, repeats, workloads, samples, summaries);
            }
            Console.WriteLine($"PASS: {Validation.States} states, {Validation.Partitions} full partitions, {Validation.Invariants} internal invariant checks; {output}; checksum={sink}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static IGrid Create(string algorithm, Workload w) => algorithm switch
    {
        "BFS-rebuild" => new BfsGrid(w.Width, w.Height, w.Initial),
        "Topology-single" => new TopologyGrid(w.Width, w.Height, w.Initial, false),
        "Topology-batch" => new TopologyGrid(w.Width, w.Height, w.Initial, true),
        "HLT-ETT" => new HltGrid(w.Width, w.Height, w.Initial),
        _ => throw new ArgumentException(algorithm)
    };
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Sample Measure(Workload work, string algorithm, int repeat)
    {
        var times = new double[work.Changes.Length]; var labels = new int[work.Initial.Length];
        long beforeMemory = GC.GetTotalMemory(true);
        long start = Stopwatch.GetTimestamp(); IGrid grid = Create(algorithm, work);
        double init = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long initMemory = GC.GetTotalMemory(true) - beforeMemory;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        for (int i = 0; i < work.Changes.Length; i++)
        {
            start = Stopwatch.GetTimestamp();
            grid.Apply(work.Changes[i]);
            sink += grid.Count;
            times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        g0 = GC.CollectionCount(0) - g0; g1 = GC.CollectionCount(1) - g1; g2 = GC.CollectionCount(2) - g2;
        long finalMemory = GC.GetTotalMemory(true) - beforeMemory;
        grid.WriteLabels(labels);
        var occupied = Enumerable.Range(0, labels.Length).Where(i => labels[i] != 0).ToArray();
        var rng = new Random(work.Seed + 53); var pairs = new (int A, int B)[4096];
        for (int i = 0; i < pairs.Length; i++)
        {
            // Half active/active pairs; half whole-map pairs. Sparse maps cannot win
            // the entire query benchmark just by early-out on empty cells.
            pairs[i] = i % 2 == 0 && occupied.Length > 0
                ? (occupied[rng.Next(occupied.Length)], occupied[rng.Next(occupied.Length)])
                : (rng.Next(labels.Length), rng.Next(labels.Length));
        }
        foreach (var p in pairs) sink += grid.Connected(p.A, p.B) ? 1 : 0;
        start = Stopwatch.GetTimestamp();
        for (int k = 0; k < 8; k++) foreach (var p in pairs) sink += grid.Connected(p.A, p.B) ? 1 : 0;
        double query = Stopwatch.GetElapsedTime(start).TotalNanoseconds / (pairs.Length * 8);
        start = Stopwatch.GetTimestamp();
        for (int k = 0; k < 8; k++) { grid.WriteLabels(labels); sink += labels[labels.Length / 2]; }
        double export = Stopwatch.GetElapsedTime(start).TotalMicroseconds / 8;
        var topo = grid as TopologyGrid; var hlt = grid as HltGrid;
        GC.KeepAlive(grid);
        return new(work.Name, algorithm, repeat, init, initMemory, finalMemory, allocated, g0, g1, g2,
            times, query, export, topo?.Topology.StatRepairCells ?? 0, topo?.Topology.StatCompactions ?? 0,
            hlt?.Promotions ?? 0, hlt?.Probes ?? 0, hlt?.Links ?? 0, hlt?.Cuts ?? 0);
    }
    private static Summary Aggregate(Workload w, string a, Sample[] samples)
    {
        var all = samples.SelectMany(s => s.UpdateUs).OrderBy(v => v).ToArray();
        double P(double p) => all[Math.Clamp((int)Math.Ceiling(p * all.Length) - 1, 0, all.Length - 1)];
        double ByKind(bool add)
        {
            var values = samples.SelectMany(s => s.UpdateUs.Where((_, i) => w.Changes[i].Add == add)).ToArray();
            return values.Length == 0 ? 0 : values.Average();
        }
        return new(w.Name, a, w.Changes.Length, w.ChangedCells, samples.Average(s => s.InitMs), all.Average(),
            P(0.50), P(0.95), P(0.99), all[^1], samples.Average(s => s.UpdateUs[0]),
            samples.Average(s => s.UpdateUs.Sum()) / w.ChangedCells, ByKind(false), ByKind(true),
            samples.Average(s => s.QueryNs), samples.Average(s => s.ExportUs), samples.Average(s => (double)s.AllocatedBytes) / w.Changes.Length,
            samples.Average(s => s.InitRetainedBytes) / 1048576, samples.Average(s => s.FinalRetainedBytes) / 1048576,
            samples.Min(s => s.UpdateUs.Average()), samples.Max(s => s.UpdateUs.Average()),
            samples[0].RepairCells, samples[0].Compactions, samples[0].Promotions, samples[0].Probes);
    }
    private static void Save(string output, string reference, int seed, int repeats, Workload[] workloads, List<Sample> samples, List<Summary> summaries)
    {
        string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        var options = new JsonSerializerOptions { WriteIndented = true };
        var manifest = new
        {
            TimestampUtc = DateTime.UtcNow, Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            LogicalProcessors = Environment.ProcessorCount, Cpu = Environment.GetEnvironmentVariable("TOPOLOGY_BENCH_CPU"),
            StopwatchFrequency = Stopwatch.Frequency, TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            Seed = seed, Repeats = repeats, ReferenceSha256 = Hash(File.ReadAllBytes(reference)),
            BenchmarkSourceSha256 = Directory.GetFiles(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(reference)!, "../../scripts/TopologyBenchmark")))
                .Where(p => p.EndsWith(".cs") || p.EndsWith(".csproj") || p.EndsWith(".ps1"))
                .OrderBy(p => p).ToDictionary(p => Path.GetFileName(p), p => Hash(File.ReadAllBytes(p))),
            AssemblySha256 = Hash(File.ReadAllBytes(typeof(Program).Assembly.Location)),
            Validation.States, Validation.Partitions, Validation.Invariants,
            Workloads = workloads.Select(w => new { w.Name, w.Description, w.Width, w.Height, w.Seed,
                InitialCells = w.Initial.Count(v => v), Events = w.Changes.Length, w.ChangedCells,
                TraceSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(w)) })
        };
        File.WriteAllText(Path.Combine(output, "environment.json"), JsonSerializer.Serialize(manifest, options));
        File.WriteAllText(Path.Combine(output, "samples.json"), JsonSerializer.Serialize(samples, options));
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summaries, options));
        var props = typeof(Summary).GetProperties();
        File.WriteAllLines(Path.Combine(output, "summary.csv"), new[] { string.Join(",", props.Select(p => p.Name)) }
            .Concat(summaries.Select(s => string.Join(",", props.Select(p => Convert.ToString(p.GetValue(s), CultureInfo.InvariantCulture))))));
    }
}
