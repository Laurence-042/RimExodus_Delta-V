using System.Reflection;
using TopologyLab;

namespace TopologyBenchmark;

internal sealed record Change(bool Add, int[] Cells);
internal interface IGrid
{
    int Count { get; }
    void Apply(Change change);
    bool Connected(int a, int b);
    void WriteLabels(int[] labels);
}

internal sealed class TopologyGrid : IGrid
{
    internal readonly IncTopology Topology;
    private readonly int width;
    private readonly bool batch;
    private readonly int[] node, padded;
    private readonly UnionFind foreground;
    private int[] batchBuffer = Array.Empty<int>();
    // Bind the missing foreground query API once, outside timing. Reference source
    // is compiled unchanged. Timed calls only index arrays and call its own Find.
    internal TopologyGrid(int w, int h, bool[] initial, bool nativeBatch)
    {
        width = w; batch = nativeBatch;
        Topology = new IncTopology(w, h, Conn.Fg4Bg8);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        node = (int[])typeof(IncTopology).GetField("_fgNode", flags).GetValue(Topology);
        foreground = (UnionFind)typeof(IncTopology).GetField("_fg", flags).GetValue(Topology);
        padded = new int[w * h];
        var occupied = new List<int>();
        for (int v = 0; v < padded.Length; v++)
        { padded[v] = (v / w + 1) * (w + 2) + v % w + 1; if (initial[v]) occupied.Add(padded[v]); }
        Topology.AddBatch(occupied);
        Topology.StatRepairCells = Topology.StatCompactions = 0;
    }
    public int Count => Topology.ComponentCount;
    public void Apply(Change change)
    {
        if (!batch || change.Cells.Length == 1)
        {
            foreach (int v in change.Cells)
                if (change.Add) Topology.Add(v % width, v / width);
                else Topology.Remove(v % width, v / width);
        }
        else
        {
            if (batchBuffer.Length != change.Cells.Length) batchBuffer = new int[change.Cells.Length];
            for (int i = 0; i < batchBuffer.Length; i++) batchBuffer[i] = padded[change.Cells[i]];
            if (change.Add) Topology.AddBatch(batchBuffer); else Topology.RemoveBatch(batchBuffer);
        }
    }
    private int Label(int v) => node[padded[v]] == 0 ? 0 : foreground.Find(node[padded[v]]) + 1;
    public bool Connected(int a, int b) { int r = Label(a); return r != 0 && r == Label(b); }
    public void WriteLabels(int[] labels)
    { for (int i = 0; i < labels.Length; i++) labels[i] = Label(i); }
    internal void Validate()
    {
        string error = Topology.Verify(Topology.ClonePresent(), Topology.Size, Topology.HSize);
        if (error.Length != 0) throw new Exception(error);
    }
}

// Independent, foreground-only BFS oracle and optimized full-rebuild baseline.
// It deliberately does not compute holes: production fracture queries do not need them.
// Buffers are reused, so this is not a benchmark of GC-heavy reference GridBrute.
internal sealed class BfsGrid : IGrid
{
    private readonly int width;
    internal readonly bool[] Present;
    internal readonly int[] Labels;
    private readonly int[] queue;
    private bool dirty = true;
    private int count;
    public int Count { get { Rebuild(); return count; } }
    internal BfsGrid(int w, int h, bool[] initial)
    { width = w; Present = (bool[])initial.Clone(); Labels = new int[w * h]; queue = new int[w * h]; Rebuild(); }
    public void Apply(Change change)
    {
        foreach (int v in change.Cells) { dirty |= Present[v] != change.Add; Present[v] = change.Add; }
        Rebuild(); // One scan per externally visible event/batch, not per cell.
    }
    private void Rebuild()
    {
        if (!dirty) return;
        dirty = false; Array.Clear(Labels); count = 0;
        for (int v = 0; v < Present.Length; v++)
        {
            if (!Present[v] || Labels[v] != 0) continue;
            ++count; int head = 0, tail = 0; Labels[v] = count; queue[tail++] = v;
            while (head < tail)
            {
                int c = queue[head++], x = c % width;
                if (x > 0) Visit(c - 1, ref tail);
                if (x + 1 < width) Visit(c + 1, ref tail);
                if (c >= width) Visit(c - width, ref tail);
                if (c + width < Present.Length) Visit(c + width, ref tail);
            }
        }
    }
    private void Visit(int v, ref int tail)
    { if (Present[v] && Labels[v] == 0) { Labels[v] = count; queue[tail++] = v; } }
    public bool Connected(int a, int b) { Rebuild(); return Labels[a] != 0 && Labels[a] == Labels[b]; }
    public void WriteLabels(int[] labels) { Rebuild(); Array.Copy(Labels, labels, labels.Length); }
}
