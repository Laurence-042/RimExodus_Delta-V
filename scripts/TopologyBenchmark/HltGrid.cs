namespace TopologyBenchmark;

// Holm--de Lichtenberg--Thorup (HLT / HDT) hierarchy:
// F_i contains tree edges with level >= i. Tree size <= floor(n / 2^i).
// Non-tree edge endpoints must be connected in F_level(edge).
// The graph is specialized only in its degree-4 adjacency storage; replacement
// searches use augmented ETT marks, never BFS or a scan of all component vertices.
internal sealed class HltGrid : IGrid
{
    private sealed class Edge
    {
        internal readonly int U, V;
        internal int Level;
        internal bool Active, Tree;
        internal Edge(int u, int v) { U = u; V = v; }
    }
    private readonly int width;
    private readonly bool[] present;
    private readonly Edge[] edges;
    private readonly int[][] incident;
    private readonly EulerTourForest[] forests;
    public int Count { get; private set; }
    public long Promotions, Probes, Links, Cuts;
    public HltGrid(int w, int h, bool[] initial)
    {
        width = w; int n = w * h;
        present = new bool[n]; incident = new int[n][];
        var lists = new List<int>[n];
        for (int v = 0; v < n; v++) lists[v] = new List<int>(4);
        var all = new List<Edge>();
        void Edge(int u, int v)
        { int id = all.Count; all.Add(new Edge(u, v)); lists[u].Add(id); lists[v].Add(id); }
        for (int v = 0; v < n; v++)
        { if (v % w + 1 < w) Edge(v, v + 1); if (v + w < n) Edge(v, v + w); }
        edges = all.ToArray();
        for (int v = 0; v < n; v++) incident[v] = lists[v].ToArray();
        int levels = 1; while ((1L << levels) <= n) levels++;
        forests = new EulerTourForest[levels];
        for (int l = 0; l < levels; l++) forests[l] = new EulerTourForest(n);
        for (int v = 0; v < n; v++) if (initial[v]) Set(v, true);
        ResetStats();
    }
    public void ResetStats() { Promotions = Probes = Links = Cuts = 0; }
    public void Apply(Change change)
    { foreach (int cell in change.Cells) Set(cell, change.Add); }
    private void Set(int v, bool add)
    {
        if (present[v] == add) return;
        if (add)
        {
            present[v] = true; Count++;
            foreach (int id in incident[v])
            {
                Edge e = edges[id]; int other = e.U == v ? e.V : e.U;
                if (present[other]) Insert(id);
            }
        }
        else
        {
            foreach (int id in incident[v]) if (edges[id].Active) Delete(id);
            present[v] = false; Count--;
        }
    }
    private void Insert(int id)
    {
        Edge e = edges[id]; e.Active = true; e.Level = 0;
        e.Tree = !forests[0].Connected(e.U, e.V);
        if (e.Tree) { forests[0].Link(id, e.U, e.V, true); Links++; Count--; }
        else { Refresh(e.U, 0); Refresh(e.V, 0); }
    }
    private void Refresh(int v, int level)
    {
        bool any = false;
        foreach (int id in incident[v])
        { Edge e = edges[id]; if (e.Active && !e.Tree && e.Level == level) { any = true; break; } }
        forests[level].SetNonTreeMark(v, any);
    }
    private void Delete(int id)
    {
        Edge e = edges[id]; e.Active = false;
        int level = e.Level;
        if (!e.Tree) { Refresh(e.U, level); Refresh(e.V, level); return; }
        for (int i = 0; i <= level; i++) { forests[i].Cut(id); Cuts++; }
        e.Tree = false; Count++;
        for (int i = level; i >= 0; i--)
        {
            EulerTourForest f = forests[i];
            var a = f.Root(e.U); var b = f.Root(e.V);
            int small = a.Vertices <= b.Vertices ? e.U : e.V;
            // Raise all exact-level tree edges of the smaller component first.
            EulerTourForest.Node mark;
            while ((mark = EulerTourForest.FindMarked(f.Root(small), true)) != null)
            {
                int tid = mark.Edge; Edge t = edges[tid];
                f.ClearTreeMark(tid); t.Level++; Promotions++;
                forests[i + 1].Link(tid, t.U, t.V, true); Links++;
            }
            while ((mark = EulerTourForest.FindMarked(f.Root(small), false)) != null)
            {
                int nid = -1;
                foreach (int candidate in incident[mark.Vertex])
                {
                    Edge q = edges[candidate];
                    if (q.Active && !q.Tree && q.Level == i) { nid = candidate; break; }
                }
                if (nid < 0) throw new Exception("Stale non-tree mark");
                Edge replacement = edges[nid]; Probes++;
                if (f.Connected(replacement.U, replacement.V))
                {
                    replacement.Level++; Promotions++;
                    Refresh(replacement.U, i); Refresh(replacement.V, i);
                    Refresh(replacement.U, i + 1); Refresh(replacement.V, i + 1);
                }
                else
                {
                    replacement.Tree = true;
                    Refresh(replacement.U, i); Refresh(replacement.V, i);
                    for (int j = 0; j <= i; j++)
                    { forests[j].Link(nid, replacement.U, replacement.V, j == i); Links++; }
                    Count--; return;
                }
            }
        }
    }
    public bool Connected(int a, int b) => present[a] && present[b] && forests[0].Connected(a, b);
    public void WriteLabels(int[] labels) => forests[0].WriteLabels(present, labels);
    public void Validate()
    {
        for (int i = 0; i < forests.Length; i++) forests[i].Validate(i, present.Length);
        foreach (Edge e in edges)
            if (e.Active && !e.Tree && !forests[e.Level].Connected(e.U, e.V))
                throw new Exception("HLT non-tree edge level invariant");
    }
}
