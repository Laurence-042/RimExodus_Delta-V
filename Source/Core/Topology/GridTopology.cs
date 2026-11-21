// TopologyLab 增量连通算法来自《新边疆》开发组的浪淘沙。
// 原始 GridTopology.cs SHA256: 038c5d7181c179918612f6bc209a4575d65c1ed54d7b9689195bde8c3bc628ce
// 运行时摘取：保留单格更新、批量初始化与节点池整理；移除实验驱动和诊断。
// 署名与 Steam Workshop 发布要求见根目录 CONTRIBUTOR.md。

using System;
using System.Collections.Generic;

namespace DeltaV.Topology
{
    /// <summary>连通性约定。前景与背景必须互补配对。</summary>
    internal enum Conn
    {
        /// <summary>前景 4 邻域 + 背景 8 邻域（"必须共边才算连"）。</summary>
        Fg4Bg8 = 0,

        /// <summary>前景 8 邻域 + 背景 4 邻域（"对角相碰也算连"）。</summary>
        Fg8Bg4 = 1,
    }
    /// <summary>互补邻接及局部欧拉角点贡献。</summary>
    internal static class GridCorners
    {
        private static readonly int[] Off4 = { 1, 0, -1, 0, 0, 1, 0, -1 };
        private static readonly int[] Off8 =
        {
            1, 0, -1, 0, 0, 1, 0, -1,
            1, 1, 1, -1, -1, 1, -1, -1,
        };

        public static int[] FgOffsets(Conn conn) => conn == Conn.Fg4Bg8 ? Off4 : Off8;
        public static int[] BgOffsets(Conn conn) => conn == Conn.Fg4Bg8 ? Off8 : Off4;
        /// <summary>
        /// 单个格点的绕数贡献。恰好 1 格 = 凸角 +1；恰好 3 格 = 凹角 −1；
        /// 2 格对角 = ±2（符号即"共点算不算连"的开关）。
        /// </summary>
        public static int VertexContrib(bool[] present, int size, int hsize, int vx, int vy, Conn conn)
        {
            bool a = CellAt(present, size, hsize, vx - 1, vy - 1); // NW
            bool b = CellAt(present, size, hsize, vx, vy - 1);     // NE
            bool c = CellAt(present, size, hsize, vx - 1, vy);     // SW
            bool d = CellAt(present, size, hsize, vx, vy);         // SE
            int n = (a ? 1 : 0) + (b ? 1 : 0) + (c ? 1 : 0) + (d ? 1 : 0);
            if (n == 1) return 1;
            if (n == 3) return -1;
            if (n == 2 && ((a && d) || (b && c))) return conn == Conn.Fg4Bg8 ? 2 : -2;
            return 0;
        }

        public static bool CellAt(bool[] present, int size, int hsize, int x, int y)
        {
            if (x < 0 || y < 0 || x >= size || y >= hsize) return false;
            return present[y * size + x];
        }
    }

    /// <summary>Union-find with bounded reclamation of nodes unreachable from either grid or child nodes.</summary>
    internal sealed class UnionFind
    {
        private int[] parent = new int[1024];
        private int[] references = new int[1024];
        private bool[] queued = new bool[1024];
        private readonly Stack<int> free = new Stack<int>();
        private readonly Queue<int> unreachable = new Queue<int>();
        public int[] Size = new int[1024];
        public int NodeCount { get; private set; } = 1;
        internal int LastReclaimWork { get; private set; }
        internal int Reclaimed { get; private set; }

        public int NewSingleton()
        {
            int node = free.Count == 0 ? NodeCount++ : free.Pop();
            if (node == parent.Length)
            {
                Array.Resize(ref parent, parent.Length * 2);
                Array.Resize(ref Size, Size.Length * 2);
                Array.Resize(ref references, references.Length * 2);
                Array.Resize(ref queued, queued.Length * 2);
            }
            parent[node] = node;
            Size[node] = 1;
            return node;
        }

        public void LinkCell(ref int cellNode, int node)
        {
            if (cellNode == node) return;
            if (node != 0) references[node]++;
            Release(cellNode);
            cellNode = node;
        }

        private void Release(int node)
        {
            if (node == 0 || --references[node] != 0 || queued[node]) return;
            queued[node] = true;
            unreachable.Enqueue(node);
        }

        private void SetParent(int node, int root)
        {
            int previous = parent[node];
            if (previous == root) return;
            references[root]++;
            if (previous != node) Release(previous);
            parent[node] = root;
        }

        public int Find(int i)
        {
            int root = i;
            while (parent[root] != root) root = parent[root];
            while (parent[i] != root)
            {
                int next = parent[i];
                SetParent(i, root);
                i = next;
            }
            return root;
        }

        public void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (Size[ra] < Size[rb]) (ra, rb) = (rb, ra);
            SetParent(rb, ra);
            Size[ra] += Size[rb];
            Size[rb] = 0;
        }

        public void Reclaim(int budget)
        {
            LastReclaimWork = 0;
            while (LastReclaimWork < budget && unreachable.Count > 0)
            {
                LastReclaimWork++;
                int node = unreachable.Dequeue();
                queued[node] = false;
                if (references[node] != 0) continue;
                int previous = parent[node];
                if (previous != node) Release(previous);
                parent[node] = 0;
                free.Push(node);
                Reclaimed++;
            }
        }

        public void Reset()
        {
            Array.Clear(references, 0, NodeCount);
            Array.Clear(queued, 0, NodeCount);
            free.Clear();
            unreachable.Clear();
            NodeCount = 1;
        }
    }

    /// <summary>
    /// 增量实现：绕数 w（O(1) 局部计数）+ 前景并查集 + 背景并查集 + 孔洞数 h。
    /// 恒等式 χ = w/4 = c − h 用来在 O(1) 内判断"这次改动到底有没有改变连通块数"，
    /// 只有真的变了才去 flood 修复（且只重挂受影响的那几块）。
    ///
    /// 已移除的格子仍可作为子树的中间节点。每次操作回收固定数量的不可达节点，
    /// 只复用从格索引和子节点均无法抵达的位置。
    /// </summary>
    internal sealed class GridTopology
    {
        public readonly int Size, HSize, W, H;
        private readonly Conn _conn;
        private readonly int[] _fgOff, _bgOff;
        private readonly bool[] _present;
        private readonly int _cells;

        // 每格 → 并查集节点（0 = 无）
        private readonly int[] _fgNode;
        private readonly int[] _bgNode;
        private readonly UnionFind _fg = new UnionFind();
        private readonly UnionFind _bg = new UnionFind();
        private bool[] _bgTouch = new bool[2048];
        private int _fgCount, _holes, _wCorner;

        private readonly int[] _mark;
        private int _markGen;
        private readonly int[] _queue;

        // 四种互不干扰的暂存（踩过坑：把 List 当定长数组用，靠"上一步残留的 Count"侥幸不炸）
        private readonly int[] _fgRoots = new int[8];
        private readonly int[] _bgRoots = new int[8];
        private readonly List<int> _bgSeeds = new List<int>(16);
        private readonly List<List<int>> _pieces = new List<List<int>>();

        public GridTopology(int w, int h, Conn conn)
        {
            W = w; H = h; Size = w + 2; HSize = h + 2;
            _conn = conn;
            _fgOff = GridCorners.FgOffsets(conn);
            _bgOff = GridCorners.BgOffsets(conn);
            _cells = Size * HSize;
            _present = new bool[_cells];
            _fgNode = new int[_cells];
            _bgNode = new int[_cells];
            _mark = new int[_cells];
            _queue = new int[_cells];
            if (_bgTouch.Length < 8) _bgTouch = new bool[8];

            // 初始全空 ⇒ 整张图（含 padding）是一个连外部的背景分量
            int root = NewBgNode();
            _bgTouch[root] = true;
            for (int i = 0; i < _cells; i++) _bg.LinkCell(ref _bgNode[i], root);
            _bg.Size[root] = _cells;
        }

        public bool Present(int x, int y) => _present[Idx(x, y)];
        internal int ForegroundRoot(int x, int y) => Present(x, y) ? _fg.Find(_fgNode[Idx(x, y)]) : 0;
        public int ComponentCount => _fgCount;
        public int HoleCount => _holes;
        public long CornerSum => _wCorner;

        private int Idx(int x, int y) => (y + 1) * Size + (x + 1);

        private int NewFgNode() => _fg.NewSingleton();

        private int NewBgNode()
        {
            int n = _bg.NewSingleton();
            if (n >= _bgTouch.Length) Array.Resize(ref _bgTouch, Math.Max(n + 1, _bgTouch.Length * 2));
            _bgTouch[n] = false;
            return n;
        }

        // ------------------------------------------------------------------
        //  绕数：只影响被改动格的 4 个角点 ⇒ O(1)
        // ------------------------------------------------------------------
        private int DeltaWForAdd(int x, int y)
        {
            int i = Idx(x, y);
            bool old = _present[i];
            _present[i] = false;
            int before = CellVertexSum(x, y);
            _present[i] = true;
            int after = CellVertexSum(x, y);
            _present[i] = old;
            return after - before;
        }

        private int CellVertexSum(int x, int y)
        {
            int vx = x + 1, vy = y + 1;
            return GridCorners.VertexContrib(_present, Size, HSize, vx, vy, _conn)
                 + GridCorners.VertexContrib(_present, Size, HSize, vx + 1, vy, _conn)
                 + GridCorners.VertexContrib(_present, Size, HSize, vx, vy + 1, _conn)
                 + GridCorners.VertexContrib(_present, Size, HSize, vx + 1, vy + 1, _conn);
        }

        // ------------------------------------------------------------------
        //  放上一格（入参：内部坐标）
        // ------------------------------------------------------------------
        public void Add(int x, int y)
        {
            int i = Idx(x, y);
            if (_present[i]) return;

            int dw = DeltaWForAdd(x, y);
            if (dw % 4 != 0) throw new InvalidOperationException($"Δw 不是 4 的倍数：{dw}");
            _wCorner += dw;
            _present[i] = true;

            // 前景：本格拿一个全新节点，再与邻居所在分量合并（精确给出 Δc）
            int me = NewFgNode();
            _fg.LinkCell(ref _fgNode[i], me);
            int k = GatherFgRoots(x, y);
            int dc;
            if (k == 0) dc = +1;
            else
            {
                for (int j = 0; j < k; j++) _fg.Union(_fgRoots[j], me);
                dc = -(k - 1);
            }
            _fgCount += dc;

            // 洞数由恒等式来（O(1)，不需要背景结构）
            int dhAdd = dc - dw / 4;
            _holes += dhAdd;

            // 背景：该格离开背景。它的 bg 节点留在树里当"穿线"，
            // 由 BgRepair 负责把被它连起来的部分拆开（或整块重挂）。
            int oldBgRoot = _bg.Find(_bgNode[i]);
            _bg.LinkCell(ref _bgNode[i], 0);
            _bg.Size[oldBgRoot]--;
            if (_bg.Size[oldBgRoot] < 0) _bg.Size[oldBgRoot] = 0;

            // 背景结构是否必须重排？有一条**可证明无害的跳过**：
            //   背景只会在"放上一格"时分裂。设原分量为 C（是否连外部 = _bgTouch[oldBgRoot]），
            //   分裂成若干块后 Δh = (其中"有限块"的个数) − (C 有限 ? 1 : 0)。
            //   于是：若 C 连外部且 Δh == 0 ⇒ **一块有限块都没产生** ⇒ 所有剩余块都连外部。
            //   而背景结构唯一被用到的地方是将来"拿掉一格"时数"有几个不同的邻居分量且有限"——
            //   把两个都连外部的块当成一块，`finiteBefore` 与合并后的 touch 都不变 ⇒ Δh 完全一致。
            //   ⇒ 这种情况可以整块保留，省掉一次泛洪（实测这是单格操作的最大开销来源）。
            if (_bg.Size[oldBgRoot] > 0)
            {
                bool harmlessSkip = dhAdd == 0 && _bgTouch[oldBgRoot];
                if (!harmlessSkip)
                {
                    int px = i % Size, py = i / Size;
                    _bgSeeds.Clear();
                    for (int t = 0; t < _bgOff.Length; t += 2)
                    {
                        int nx = px + _bgOff[t], ny = py + _bgOff[t + 1];
                        if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize) continue;
                        _bgSeeds.Add(ny * Size + nx);
                    }
                    if (_bgSeeds.Count > 0) BgRepair(_bgSeeds);
                }
            }

            ReclaimNodes();
        }

        // ------------------------------------------------------------------
        //  拿掉一格（入参：内部坐标）
        // ------------------------------------------------------------------
        public void Remove(int x, int y)
        {
            int i = Idx(x, y);
            if (!_present[i]) return;

            int dw = -DeltaWForAdd(x, y);
            if (dw % 4 != 0) throw new InvalidOperationException($"Δw 不是 4 的倍数：{dw}");
            _wCorner += dw;
            _present[i] = false;

            // 背景：只会合并 ⇒ 并查集 O(α) 精确得到 Δh
            int dh = BgAttach(i);
            _holes += dh;

            // 前景：Δc = Δw/4 + Δh ⇒ O(1) 得知是否断裂
            //   注意 Δc 的合法范围是 [−1, +3]：被拿掉的格子本身就是孤块时 c 会减少 1
            //   （我一开始写成"不可能减少块数"，是错的）。
            int dc = dw / 4 + dh;
            if (dc < -1) throw new InvalidOperationException($"拿掉一格最多减少 1 块，得到 {dc}");
            _fgCount += dc;

            _fg.LinkCell(ref _fgNode[i], 0);
            if (dc > 0)
            {
                int splits = FgRepairSplit(x, y);
                if (dc > 0 && splits != dc)
                    throw new InvalidOperationException($"实际断裂 {splits} 处，增量计数预测 {dc}");
            }

            ReclaimNodes();
        }

        // ------------------------------------------------------------------
        //  前景：收集邻居所在分量的根（入参：内部坐标）
        // ------------------------------------------------------------------
        private int GatherFgRoots(int x, int y)
        {
            int k = 0;
            for (int t = 0; t < _fgOff.Length; t += 2)
            {
                int nx = x + _fgOff[t], ny = y + _fgOff[t + 1];
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int ni = Idx(nx, ny);
                if (!_present[ni] || _fgNode[ni] == 0) continue;
                int r = _fg.Find(_fgNode[ni]);
                bool dup = false;
                for (int j = 0; j < k; j++) if (_fgRoots[j] == r) { dup = true; break; }
                if (!dup) _fgRoots[k++] = r;
            }
            return k;
        }

        // ------------------------------------------------------------------
        //  背景：把一格并入（入参：padded 索引；只会合并 ⇒ 精确 Δh）
        // ------------------------------------------------------------------
        private int BgAttach(int i)
        {
            int me = NewBgNode();
            _bg.LinkCell(ref _bgNode[i], me);
            int x = i % Size, y = i / Size;
            int kr = 0;
            for (int t = 0; t < _bgOff.Length; t += 2)
            {
                int nx = x + _bgOff[t], ny = y + _bgOff[t + 1];
                if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize) continue;
                int ni = ny * Size + nx;
                if (_present[ni] || _bgNode[ni] == 0) continue;
                int r = _bg.Find(_bgNode[ni]);
                bool dup = false;
                for (int j = 0; j < kr; j++) if (_bgRoots[j] == r) { dup = true; break; }
                if (!dup) _bgRoots[kr++] = r;
            }

            if (kr == 0)
            {
                _bgTouch[me] = false;
                return +1; // 凭空出现一个不含外部的背景区域 = 多一个洞
            }

            int finiteBefore = 0;
            for (int j = 0; j < kr; j++) if (!_bgTouch[_bgRoots[j]]) finiteBefore++;
            bool touch = false;
            for (int j = 0; j < kr; j++) touch |= _bgTouch[_bgRoots[j]];
            for (int j = 0; j < kr; j++) _bg.Union(_bgRoots[j], me);
            int root = _bg.Find(me);
            _bgTouch[root] = touch;
            return (touch ? 0 : 1) - finiteBefore;
        }

        /// <summary>
        /// 背景结构修复：种子 = 被改动格的背景邻居（padded 索引）。
        /// 必须分两阶段：先只泛洪标记、再统一换节点；
        /// 否则中途改指针会破坏 flood 自己用的 Find 判据（踩过一次）。
        /// </summary>
        private void BgRepair(List<int> seeds)
        {
            _markGen++;
            for (int s = 0; s < seeds.Count; s++)
            {
                int start = seeds[s];
                if (_present[start] || _bgNode[start] == 0 || _mark[start] == _markGen) continue;
                int oldRoot = _bg.Find(_bgNode[start]);

                int head = 0, tail = 0;
                _queue[tail++] = start;
                _mark[start] = _markGen;
                bool touch = false;
                int count = 0;
                while (head < tail)
                {
                    int cur = _queue[head++];
                    count++;
                    int cx = cur % Size, cy = cur / Size;
                    if (cx == 0 || cy == 0 || cx == Size - 1 || cy == HSize - 1) touch = true;
                    for (int t = 0; t < _bgOff.Length; t += 2)
                    {
                        int nx = cx + _bgOff[t], ny = cy + _bgOff[t + 1];
                        if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize) continue;
                        int ni = ny * Size + nx;
                        if (_present[ni] || _bgNode[ni] == 0 || _mark[ni] == _markGen) continue;
                        if (_bg.Find(_bgNode[ni]) != oldRoot) continue;
                        _mark[ni] = _markGen;
                        _queue[tail++] = ni;
                    }
                }

                // 第二阶段：整块换成一个新节点；老节点留给"没被泛洪到的那些块"
                int fresh = NewBgNode();
                _bgTouch[fresh] = touch;
                _bg.Size[fresh] = count;
                for (int q = 0; q < tail; q++) _bg.LinkCell(ref _bgNode[_queue[q]], fresh);
                if (fresh != oldRoot)
                {
                    _bg.Size[oldRoot] -= count;
                    if (_bg.Size[oldRoot] < 0) _bg.Size[oldRoot] = 0;
                }
            }
        }

        /// <summary>
        /// 前景断裂修复：从被拿掉格的邻居各自 flood，每块换一个新节点。
        /// 返回实际断裂出的块数（应等于 Δc）。入参：内部坐标。
        /// </summary>
        private int FgRepairSplit(int x, int y)
        {
            _pieces.Clear();
            _markGen++;
            for (int t = 0; t < _fgOff.Length; t += 2)
            {
                int nx = x + _fgOff[t], ny = y + _fgOff[t + 1];
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int ni = Idx(nx, ny);
                if (!_present[ni] || _mark[ni] == _markGen) continue;
                _pieces.Add(FloodFgPiece(ni));
            }

            int splits = _pieces.Count - 1;
            if (splits <= 0) return 0;

            for (int p = 0; p < _pieces.Count; p++)
            {
                var piece = _pieces[p];
                int node = NewFgNode();
                _fg.Size[node] = piece.Count;
                for (int c = 0; c < piece.Count; c++) _fg.LinkCell(ref _fgNode[piece[c]], node);
            }
            return splits;
        }

        private List<int> FloodFgPiece(int start)
        {
            var piece = new List<int>(64);
            int head = 0, tail = 0;
            _queue[tail++] = start;
            _mark[start] = _markGen;
            while (head < tail)
            {
                int cur = _queue[head++];
                piece.Add(cur);
                int cx = cur % Size, cy = cur / Size;
                for (int k = 0; k < _fgOff.Length; k += 2)
                {
                    int ax = cx + _fgOff[k], ay = cy + _fgOff[k + 1];
                    if (ax < 0 || ay < 0 || ax >= Size || ay >= HSize) continue;
                    int ai = ay * Size + ax;
                    if (!_present[ai] || _mark[ai] == _markGen) continue;
                    _mark[ai] = _markGen;
                    _queue[tail++] = ai;
                }
            }
            return piece;
        }

        // ------------------------------------------------------------------
        //  批量：一批只做一轮修复
        // ------------------------------------------------------------------
        /// <summary>一口气放多格（入参：padded 索引列表）。</summary>
        public void AddBatch(IReadOnlyList<int> cells)
        {
            _bgSeeds.Clear();
            for (int c = 0; c < cells.Count; c++)
            {
                int i = cells[c];
                if (_present[i]) continue;
                int x = i % Size, y = i / Size;
                int dw = DeltaWForAdd(x - 1, y - 1);
                _wCorner += dw;
                _present[i] = true;

                int me = NewFgNode();
                _fg.LinkCell(ref _fgNode[i], me);
                int k = GatherFgRoots(x - 1, y - 1);
                int dc;
                if (k == 0) dc = +1;
                else { for (int j = 0; j < k; j++) _fg.Union(_fgRoots[j], me); dc = -(k - 1); }
                _fgCount += dc;
                _holes += dc - dw / 4;

                int oldBgRoot = _bg.Find(_bgNode[i]);
                _bg.LinkCell(ref _bgNode[i], 0);
                _bg.Size[oldBgRoot]--;
                if (_bg.Size[oldBgRoot] < 0) _bg.Size[oldBgRoot] = 0;

                for (int t = 0; t < _bgOff.Length; t += 2)
                {
                    int nx = x + _bgOff[t], ny = y + _bgOff[t + 1];
                    if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize) continue;
                    int ni = ny * Size + nx;
                    if (!_present[ni]) _bgSeeds.Add(ni);
                }
            }
            if (_bgSeeds.Count > 0) BgRepair(_bgSeeds);
            ReclaimNodes();
        }
        // ------------------------------------------------------------------
        //  节点池整理：整图重建并查集（O(格子数)）
        // ------------------------------------------------------------------
        internal const int ReclaimBudget = 32;
        internal Func<long> StartReclaim { get; set; }
        internal Action<long, int, int> RecordReclaim { get; set; }

        private void ReclaimNodes()
        {
            long start = StartReclaim?.Invoke() ?? 0;
            int before = _fg.Reclaimed + _bg.Reclaimed;
            _fg.Reclaim(ReclaimBudget);
            _bg.Reclaim(ReclaimBudget);
            if (start != 0)
                RecordReclaim?.Invoke(start, _fg.LastReclaimWork + _bg.LastReclaimWork, _fg.Reclaimed + _bg.Reclaimed - before);
        }

        /// <summary>整图重建前景/背景结构，回收不再引用的节点。</summary>
        public void CompactAll()
        {
            CompactFg();
            CompactBg();
        }

        private void CompactFg()
        {
            _fg.Reset();
            Array.Clear(_fgNode, 0, _fgNode.Length);
            _markGen++;
            int comps = 0;
            for (int i = 0; i < _present.Length; i++)
            {
                if (!_present[i] || _fgNode[i] != 0) continue;
                var piece = FloodFgPiece(i);
                int node = NewFgNode();
                _fg.Size[node] = piece.Count;
                for (int c = 0; c < piece.Count; c++) _fg.LinkCell(ref _fgNode[piece[c]], node);
                comps++;
            }
            _fgCount = comps;
        }

        private void CompactBg()
        {
            _bg.Reset();
            _bgTouch = new bool[2048];
            Array.Clear(_bgNode, 0, _bgNode.Length);
            _markGen++;
            int holes = 0;
            for (int i = 0; i < _cells; i++)
            {
                if (_present[i] || _bgNode[i] != 0) continue;
                // 泛洪一个背景分量
                int head = 0, tail = 0;
                _queue[tail++] = i;
                _mark[i] = _markGen;
                bool touch = false;
                var cellsOfComp = new List<int>(64);
                while (head < tail)
                {
                    int cur = _queue[head++];
                    cellsOfComp.Add(cur);
                    int cx = cur % Size, cy = cur / Size;
                    if (cx == 0 || cy == 0 || cx == Size - 1 || cy == HSize - 1) touch = true;
                    for (int t = 0; t < _bgOff.Length; t += 2)
                    {
                        int nx = cx + _bgOff[t], ny = cy + _bgOff[t + 1];
                        if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize) continue;
                        int ni = ny * Size + nx;
                        if (_present[ni] || _mark[ni] == _markGen) continue;
                        _mark[ni] = _markGen;
                        _queue[tail++] = ni;
                    }
                }
                int node = NewBgNode();
                _bgTouch[node] = touch;
                _bg.Size[node] = cellsOfComp.Count;
                for (int c = 0; c < cellsOfComp.Count; c++) _bg.LinkCell(ref _bgNode[cellsOfComp[c]], node);
                if (!touch) holes++;
            }
            if (holes != _holes)
                throw new InvalidOperationException($"Compact 对账失败：重建孔洞 {holes} vs 维护值 {_holes}");
        }

    }
}
