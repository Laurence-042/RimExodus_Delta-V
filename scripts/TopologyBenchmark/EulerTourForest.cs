namespace TopologyBenchmark;

// Euler tour = one permanent self occurrence per vertex, two occurrences per tree edge.
// Implicit treap priorities use a fixed PRNG seed for reproducibility. This is an
// expected-balanced implementation, not the original paper's deterministic balanced tree.
internal sealed class EulerTourForest
{
    internal sealed class Node
    {
        internal Node Left, Right, Parent;
        internal readonly uint Priority;
        internal readonly int Vertex, Edge;
        internal int Length = 1, Vertices, TreeMarks, NonTreeMarks;
        internal bool TreeMark, NonTreeMark;
        internal Node(int vertex, int edge, uint priority)
        { Vertex = vertex; Edge = edge; Priority = priority; Vertices = vertex >= 0 ? 1 : 0; }
    }

    private readonly Node[] vertices;
    private readonly Dictionary<int, (Node A, Node B)> arcs = new();
    private uint random = 0x91e10da5;
    internal EulerTourForest(int n) { vertices = new Node[n]; }
    private Node New(int v, int e)
    {
        random ^= random << 13; random ^= random >> 17; random ^= random << 5;
        return new Node(v, e, random);
    }
    internal Node Vertex(int v) => vertices[v] ??= New(v, -1);
    internal static Node Root(Node n) { while (n.Parent != null) n = n.Parent; return n; }
    internal Node Root(int v) => Root(Vertex(v));
    internal bool Connected(int u, int v) => Root(u) == Root(v);
    private static int Len(Node n) => n?.Length ?? 0;
    private static void Pull(Node n)
    {
        n.Length = 1 + Len(n.Left) + Len(n.Right);
        n.Vertices = (n.Vertex >= 0 ? 1 : 0) + (n.Left?.Vertices ?? 0) + (n.Right?.Vertices ?? 0);
        n.TreeMarks = (n.TreeMark ? 1 : 0) + (n.Left?.TreeMarks ?? 0) + (n.Right?.TreeMarks ?? 0);
        n.NonTreeMarks = (n.NonTreeMark ? 1 : 0) + (n.Left?.NonTreeMarks ?? 0) + (n.Right?.NonTreeMarks ?? 0);
        if (n.Left != null) n.Left.Parent = n;
        if (n.Right != null) n.Right.Parent = n;
    }
    private static void Refresh(Node n) { while (n != null) { Pull(n); n = n.Parent; } }
    private static Node Merge(Node a, Node b)
    {
        if (a == null) { if (b != null) b.Parent = null; return b; }
        if (b == null) { a.Parent = null; return a; }
        if (a.Priority < b.Priority)
        { a.Right = Merge(a.Right, b); Pull(a); a.Parent = null; return a; }
        b.Left = Merge(a, b.Left); Pull(b); b.Parent = null; return b;
    }
    private static void Split(Node n, int count, out Node a, out Node b)
    {
        if (n == null) { a = b = null; return; }
        if (Len(n.Left) >= count)
        {
            Split(n.Left, count, out a, out Node right);
            n.Left = right; Pull(n); b = n;
        }
        else
        {
            Split(n.Right, count - Len(n.Left) - 1, out Node left, out b);
            n.Right = left; Pull(n); a = n;
        }
        if (a != null) a.Parent = null;
        if (b != null) b.Parent = null;
    }
    private static int Index(Node n)
    {
        int result = Len(n.Left);
        while (n.Parent != null)
        {
            if (n == n.Parent.Right) result += 1 + Len(n.Parent.Left);
            n = n.Parent;
        }
        return result;
    }
    private static Node RotateTo(Node n)
    {
        int index = Index(n);
        Split(Root(n), index, out Node a, out Node b);
        return Merge(b, a);
    }
    internal void Link(int edge, int u, int v, bool exactLevel)
    {
        Node a = New(-1, edge), b = New(-1, edge);
        a.TreeMark = exactLevel; Pull(a);
        Node ru = RotateTo(Vertex(u)), rv = RotateTo(Vertex(v));
        if (ru == rv) throw new InvalidOperationException("ETT link would create a cycle");
        Merge(Merge(Merge(ru, a), rv), b);
        arcs.Add(edge, (a, b));
    }
    internal void Cut(int edge)
    {
        var pair = arcs[edge];
        Node root = RotateTo(pair.A);
        int end = Index(pair.B);
        Split(root, 1, out _, out Node rest);
        Split(rest, end - 1, out _, out Node tail);
        Split(tail, 1, out _, out _);
        arcs.Remove(edge);
    }
    internal void ClearTreeMark(int edge)
    { Node n = arcs[edge].A; n.TreeMark = false; Refresh(n); }
    internal void SetNonTreeMark(int v, bool value)
    {
        Node n = Vertex(v);
        if (n.NonTreeMark == value) return;
        n.NonTreeMark = value; Refresh(n);
    }
    internal static Node FindMarked(Node root, bool tree)
    {
        if ((tree ? root.TreeMarks : root.NonTreeMarks) == 0) return null;
        Node n = root;
        while (true)
        {
            if (n.Left != null && (tree ? n.Left.TreeMarks : n.Left.NonTreeMarks) > 0) n = n.Left;
            else if (tree ? n.TreeMark : n.NonTreeMark) return n;
            else n = n.Right;
        }
    }
    internal void WriteLabels(bool[] active, int[] labels)
    {
        Array.Clear(labels);
        int next = 0;
        var stack = new Stack<Node>();
        for (int v = 0; v < active.Length; v++)
        {
            if (!active[v] || labels[v] != 0) continue;
            ++next; stack.Push(Root(v));
            while (stack.Count != 0)
            {
                Node n = stack.Pop();
                if (n.Vertex >= 0) labels[n.Vertex] = next;
                if (n.Left != null) stack.Push(n.Left);
                if (n.Right != null) stack.Push(n.Right);
            }
        }
    }
    // Used only by differential tests, never inside timed updates.
    internal void Validate(int level, int n)
    {
        var roots = new HashSet<Node>();
        foreach (Node v in vertices) if (v != null) roots.Add(Root(v));
        foreach (Node r in roots)
        {
            if (r.Vertices > (n >> level)) throw new Exception($"HLT size invariant level {level}: {r.Vertices}");
            ValidateNode(r, null);
        }
    }
    private static void ValidateNode(Node n, Node parent)
    {
        if (n == null) return;
        if (n.Parent != parent || (parent != null && n.Priority < parent.Priority)) throw new Exception("ETT parent/heap invariant");
        int len = n.Length, v = n.Vertices, t = n.TreeMarks, nt = n.NonTreeMarks;
        ValidateNode(n.Left, n); ValidateNode(n.Right, n); Pull(n);
        if (len != n.Length || v != n.Vertices || t != n.TreeMarks || nt != n.NonTreeMarks) throw new Exception("ETT aggregate invariant");
    }
}
