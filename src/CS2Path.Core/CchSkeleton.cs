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
            return c;
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
