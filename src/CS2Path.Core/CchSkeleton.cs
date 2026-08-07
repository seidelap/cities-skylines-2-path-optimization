using System;
using System.Collections.Generic;

namespace CS2Path.Core
{
    /// <summary>
    /// Layer 0 (part 2): the Customizable Contraction Hierarchy skeleton
    /// (plan §4 Layer 0). Nodes are contracted in elimination order inserting
    /// ALL potential shortcuts — no witness searches — yielding the chordal
    /// supergraph: the symbolic factorization of the graph in the (min,+)
    /// semiring, computed once, valid under every metric.
    ///
    /// Topology edits are handled per plan §4 Layer 0 / §3: closures and
    /// removals apply instantly through metric weights (+inf via partial
    /// customization — nothing blocks); additions trigger an asynchronous
    /// background rebuild with atomic swap (see RoutingEngine).
    /// </summary>
    public sealed class CchSkeleton
    {
        public Graph G = null!;
        public int NodeCount;
        public int ArcCount;

        public int[] Rank = null!;        // rank[v]: elimination position
        public int[] NodeAtRank = null!;  // inverse permutation

        // Upward arcs (v,w), rank[v] < rank[w]. CSR over tails; within a
        // node, arcs sorted by head node id (for merge-scans / binary search).
        public int[] UpStart = null!;
        public int[] UpHead = null!;      // arc id -> head node
        public int[] UpTail = null!;      // arc id -> tail node

        // Downward CSR: for node w, the arcs (v,w) coming from below,
        // sorted by tail node id.
        public int[] DownStart = null!;
        public int[] DownArc = null!;     // arc ids
        public int[] DownTail = null!;    // tail node per entry (== UpTail[DownArc[i]])

        // Original edges realizing an arc directly, or -1. Parallel edges
        // between the same node pair (real in lane graphs) go to the overflow
        // maps; customization takes the per-metric min over all of them.
        public int[] OrigFwd = null!;     // edge id tail->head
        public int[] OrigBwd = null!;     // edge id head->tail
        public Dictionary<int, int[]> ExtraFwd = new Dictionary<int, int[]>();
        public Dictionary<int, int[]> ExtraBwd = new Dictionary<int, int[]>();

        public int[] EtParent = null!;    // elimination tree parent (lowest-ranked up-neighbor), -1 at roots
        public int TreeHeight;

        // Elimination-tree levels — the parallel schedule for customization.
        // Nodes of level L are LevelNodes[LevelStart[L] .. LevelStart[L+1]).
        public int LevelCount;
        public int[] LevelStart = null!;
        public int[] LevelNodes = null!;

        // Task decomposition for parallel customization (the level scheme's
        // replacement — see FullCustomizeParallelTasks). Each task is a
        // dissection-cell subtree, i.e. a CONTIGUOUS, downward-closed rank
        // range [TaskLo[i], TaskHi[i]); Phase2Ranks are the ranks of enclosing
        // separators above the task frontier, sorted ascending, contracted
        // sequentially after all tasks complete. Null until BuildTaskFrontier
        // is called (needs the dissection cell paths, which the skeleton alone
        // does not have).
        public int[]? TaskLo, TaskHi;
        public int[]? Phase2Ranks;

        /// <summary>
        /// Carve the elimination order into parallel tasks using the nested
        /// dissection structure itself. Nested dissection emits ranks as
        /// recurse(A), recurse(B), separator — so every cell subtree occupies a
        /// contiguous rank range with its separator at the top. Recursively
        /// split ranges at cell boundaries until tasks are small enough,
        /// diverting each split cell's separator nodes to the sequential
        /// phase-2 list. Cells that cannot be split (degenerate/BFS-ordered
        /// chunks, or below minTaskSize) become tasks whole.
        /// </summary>
        public void BuildTaskFrontier(NestedDissection.CellPath[] cellPaths, int minTaskSize)
        {
            var los = new List<int>();
            var his = new List<int>();
            var phase2 = new List<int>();

            void Split(int lo, int hi, ulong path, byte depth)
            {
                if (hi - lo <= 0) return;
                // Separator nodes of THIS cell sit at the top of the range and
                // carry exactly (path, depth).
                int sepStart = hi;
                while (sepStart > lo)
                {
                    var cp = cellPaths[NodeAtRank[sepStart - 1]];
                    if (cp.Depth == depth && cp.PathBits == path) sepStart--;
                    else break;
                }

                bool tooSmall = (long)(hi - lo) < Math.Max(2L, (long)minTaskSize) * 2;
                if (tooSmall || sepStart == lo || depth >= 62)
                {
                    los.Add(lo); his.Add(hi);
                    return;
                }
                // Children: bit `depth` of the path distinguishes side A (0)
                // from side B (1); all child-subtree nodes have Depth > depth.
                int mid = lo;
                while (mid < sepStart)
                {
                    var cp = cellPaths[NodeAtRank[mid]];
                    if (cp.Depth > depth && ((cp.PathBits >> depth) & 1UL) == 1UL) break;
                    mid++;
                }
                if (mid == lo || mid == sepStart)
                {
                    // No usable A/B split below (degenerate chunk): keep whole.
                    los.Add(lo); his.Add(hi);
                    return;
                }
                for (int r = sepStart; r < hi; r++) phase2.Add(r);
                Split(lo, mid, path, (byte)(depth + 1));
                Split(mid, sepStart, path | (1UL << depth), (byte)(depth + 1));
            }

            Split(0, NodeCount, 0UL, 0);
            phase2.Sort(); // ascending rank = valid sequential contraction order
            TaskLo = los.ToArray();
            TaskHi = his.ToArray();
            Phase2Ranks = phase2.ToArray();
        }

        public static CchSkeleton Build(Graph g, int[] rank)
        {
            var c = new CchSkeleton { G = g, NodeCount = g.NodeCount, Rank = rank };
            int n = g.NodeCount;
            c.NodeAtRank = new int[n];
            for (int v = 0; v < n; v++) c.NodeAtRank[rank[v]] = v;

            // Upward adjacency sets, seeded from original edges (undirected skeleton).
            var upAdj = new HashSet<int>[n];
            for (int v = 0; v < n; v++) upAdj[v] = new HashSet<int>();
            for (int e = 0; e < g.EdgeCount; e++)
            {
                int u = g.Tail[e], v = g.Head[e];
                if (u == v) continue;
                if (rank[u] < rank[v]) upAdj[u].Add(v); else upAdj[v].Add(u);
            }

            // Contract in elimination order: clique the higher neighbors.
            var buf = new List<int>(64);
            for (int r = 0; r < n; r++)
            {
                int v = c.NodeAtRank[r];
                var set = upAdj[v];
                if (set.Count < 2) continue;
                buf.Clear();
                buf.AddRange(set);
                buf.Sort((a, b) => rank[a].CompareTo(rank[b]));
                for (int i = 0; i < buf.Count; i++)
                    for (int j = i + 1; j < buf.Count; j++)
                        upAdj[buf[i]].Add(buf[j]);
            }

            // Freeze to CSR.
            int m = 0;
            for (int v = 0; v < n; v++) m += upAdj[v].Count;
            c.ArcCount = m;
            c.UpStart = new int[n + 1];
            c.UpHead = new int[m];
            c.UpTail = new int[m];
            c.EtParent = new int[n];
            int pos = 0;
            var sortBuf = new List<int>(64);
            for (int v = 0; v < n; v++)
            {
                c.UpStart[v] = pos;
                sortBuf.Clear();
                sortBuf.AddRange(upAdj[v]);
                sortBuf.Sort(); // by head node id
                int parent = -1, parentRank = int.MaxValue;
                foreach (int w in sortBuf)
                {
                    c.UpHead[pos] = w; c.UpTail[pos] = v; pos++;
                    if (rank[w] < parentRank) { parentRank = rank[w]; parent = w; }
                }
                c.EtParent[v] = parent;
                upAdj[v] = null!; // release as we go
            }
            c.UpStart[n] = pos;

            // Downward CSR.
            c.DownStart = new int[n + 1];
            c.DownArc = new int[m];
            c.DownTail = new int[m];
            for (int a = 0; a < m; a++) c.DownStart[c.UpHead[a] + 1]++;
            for (int v = 0; v < n; v++) c.DownStart[v + 1] += c.DownStart[v];
            var cur = new int[n];
            Array.Copy(c.DownStart, cur, n);
            // Arcs are enumerated in ascending tail id (CSR order), so per-head
            // down lists come out sorted by tail id automatically.
            for (int a = 0; a < m; a++)
            {
                int h = c.UpHead[a];
                int p = cur[h]++;
                c.DownArc[p] = a; c.DownTail[p] = c.UpTail[a];
            }

            // Original edge mapping, parallel edges included.
            c.OrigFwd = new int[m];
            c.OrigBwd = new int[m];
            Array.Fill(c.OrigFwd, -1);
            Array.Fill(c.OrigBwd, -1);
            var extraF = new Dictionary<int, List<int>>();
            var extraB = new Dictionary<int, List<int>>();
            for (int e = 0; e < g.EdgeCount; e++)
            {
                int u = g.Tail[e], v = g.Head[e];
                if (u == v) continue;
                bool fwd = rank[u] < rank[v];
                int a = fwd ? c.FindArc(u, v) : c.FindArc(v, u);
                var prim = fwd ? c.OrigFwd : c.OrigBwd;
                if (prim[a] < 0) prim[a] = e;
                else
                {
                    var map = fwd ? extraF : extraB;
                    if (!map.TryGetValue(a, out var l)) { l = new List<int>(2); map[a] = l; }
                    l.Add(e);
                }
            }
            foreach (var kv in extraF) c.ExtraFwd[kv.Key] = kv.Value.ToArray();
            foreach (var kv in extraB) c.ExtraBwd[kv.Key] = kv.Value.ToArray();

            // Elimination tree height (query cost is O(height * avg degree)).
            var depth = new int[n];
            int h2 = 0;
            for (int r = n - 1; r >= 0; r--)
            {
                int v = c.NodeAtRank[r];
                int p = c.EtParent[v];
                depth[v] = p < 0 ? 0 : depth[p] + 1;
                if (depth[v] > h2) h2 = depth[v];
            }
            c.TreeHeight = h2 + 1;
            c.BuildLevels();
            return c;
        }

        /// <summary>
        /// Group nodes into elimination-tree levels for parallel customization.
        /// Level(v) = 1 + max(Level(children)); leaves are 0. Emitted CSR-style:
        /// the nodes of level L are LevelNodes[LevelStart[L] .. LevelStart[L+1]).
        ///
        /// WHY THIS IS THE RIGHT PARALLEL DECOMPOSITION. Eliminating x relaxes
        /// triangles (x, A, B), reading arcs (x,A)/(x,B) and writing arc (A,B).
        /// Every contribution to (x,A) comes from a triangle (y, x, A) where y is
        /// a DESCENDANT of x in the elimination tree — and descendants have
        /// strictly lower level by construction. So once level L−1 is complete,
        /// every input the level-L nodes read is final, and the nodes within a
        /// level can run in any order or concurrently.
        ///
        /// Two same-level nodes CAN write the same target arc (A,B), so the write
        /// must be an atomic min — but the result is still bit-identical to the
        /// sequential sweep, because min is order-independent and exact.
        /// </summary>
        private void BuildLevels()
        {
            int n = NodeCount;
            var level = new int[n];
            int maxLevel = 0;
            // Ascending rank guarantees children (lower rank) are done first.
            for (int r = 0; r < n; r++)
            {
                int v = NodeAtRank[r];
                int p = EtParent[v];
                if (p >= 0 && level[v] + 1 > level[p]) level[p] = level[v] + 1;
                if (level[v] > maxLevel) maxLevel = level[v];
            }
            LevelCount = maxLevel + 1;
            LevelStart = new int[LevelCount + 1];
            for (int v = 0; v < n; v++) LevelStart[level[v] + 1]++;
            for (int l = 0; l < LevelCount; l++) LevelStart[l + 1] += LevelStart[l];
            LevelNodes = new int[n];
            var cursor = (int[])LevelStart.Clone();
            // Fill in ascending rank so a level's node order is deterministic —
            // parallel results must not depend on scheduling, and a stable order
            // makes the sequential-vs-parallel equivalence test meaningful.
            for (int r = 0; r < n; r++)
            {
                int v = NodeAtRank[r];
                LevelNodes[cursor[level[v]]++] = v;
            }
        }

        /// <summary>Arc id of upward arc (v,w) where rank[v] &lt; rank[w], or -1.
        /// Binary search over v's up-list (sorted by head id).</summary>
        public int FindArc(int v, int w)
        {
            int lo = UpStart[v], hi = UpStart[v + 1] - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int h = UpHead[mid];
                if (h == w) return mid;
                if (h < w) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        /// <summary>Arc for an original edge (either orientation), or -1.</summary>
        public int ArcOfEdge(int edge, out bool forward)
        {
            int u = G.Tail[edge], v = G.Head[edge];
            if (Rank[u] < Rank[v]) { forward = true; return FindArc(u, v); }
            forward = false; return FindArc(v, u);
        }
    }
}
