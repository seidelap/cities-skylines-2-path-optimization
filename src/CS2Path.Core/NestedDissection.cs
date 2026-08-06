using System;
using System.Collections.Generic;

namespace CS2Path.Core
{
    /// <summary>
    /// Layer 0 (part 1): metric-independent contraction order via nested
    /// dissection (plan §4 Layer 0). Road networks are near-planar, so small
    /// separators exist; separator nodes are ranked highest and we recurse.
    ///
    /// Separator quality is THE lever on query cost: the top separators become
    /// cliques in the chordal supergraph, so elimination-tree height and
    /// per-query work grow with the square of separator sizes. A straight
    /// geometric cut finds thin lines on gridded synthetic cities but overpays
    /// on organic street networks — a straight line through Paris crosses
    /// hundreds of streets, while the good cuts hug the river and the rail
    /// trenches. Large cells therefore use Inertial Flow (Schild &amp; Sommer
    /// 2015): keep the geometric axis as the seeding — contract the outer
    /// quarters of the projection into source and sink — but let a max-flow
    /// min-cut decide WHERE the cut runs between them. Balance ≥ 25% holds by
    /// construction (the terminals are quarters); the cut is a minimum vertex
    /// separator via the standard node-splitting reduction. Small cells keep
    /// the cheap min-crossing geometric sweep; base cells are ordered by
    /// minimum degree with fill tracking rather than sequentially; graphs
    /// without coordinates get a BFS-level embedding as the projection axis
    /// instead of falling back to plain level-set bisection.
    /// </summary>
    public static class NestedDissection
    {
        /// <summary>A/B switch: false restores the pure geometric cuts.</summary>
        public static bool UseInertialFlow = true;

        private const int BaseCase = 48;           // ≤ 64 so local ids fit a ulong mask
        private const int FlowMinCell = 600;       // below this the geometric sweep is fine
        private const int TwoDirThreshold = 60000; // above: probe {x,y}; at/below: + diagonals

        /// <summary>Per-node position in the dissection-cell hierarchy (§4.9):
        /// the cluster-cache hierarchy IS the elimination hierarchy. PathBits
        /// records the left/right branch taken at each recursion level; Depth is
        /// how many levels deep the node's cell sits. Separator nodes carry the
        /// path of the cell they separate.</summary>
        public struct CellPath
        {
            public ulong PathBits;
            public byte Depth;
        }

        public static int[] ComputeOrder(Graph g) => ComputeOrder(g, out _);

        /// <summary>Compute an elimination order. rank[v] in [0, n); higher
        /// rank = later elimination = higher in the hierarchy. Also emits each
        /// node's dissection-cell path (§4.9).</summary>
        public static int[] ComputeOrder(Graph g, out CellPath[] cellPathsOut)
        {
            int n = g.NodeCount;
            var rank = new int[n];
            var nodes = new int[n];
            for (int i = 0; i < n; i++) nodes[i] = i;
            int next = 0; // next rank to assign, ascending
            var cellPaths = new CellPath[n];
            cellPathsOut = cellPaths;

            // Undirected adjacency (union of both directions) for separator finding.
            var side = new byte[n];     // scratch: 0 = A, 1 = B, 2 = separator
            var inSet = new int[n];     // scratch: recursion-set membership stamp
            var localIdx = new int[n];  // scratch: node -> index within current set
            int stamp = 0;

            void Recurse(int[] set, int count, ulong path, byte depth)
            {
                stamp++;
                for (int i = 0; i < count; i++) { inSet[set[i]] = stamp; localIdx[set[i]] = i; }

                if (count <= BaseCase)
                {
                    // Minimum-degree elimination with fill tracking: inside a
                    // base cell the order still decides local fill-in, and
                    // min-degree beats sequential on anything non-path-like.
                    var adj = new ulong[count];
                    for (int i = 0; i < count; i++)
                    {
                        int v = set[i];
                        for (int e = g.OutStart[v]; e < g.OutStart[v + 1]; e++)
                        {
                            int w = g.Head[e];
                            if (inSet[w] == stamp && w != v) adj[i] |= 1UL << localIdx[w];
                        }
                        for (int e = g.InStart[v]; e < g.InStart[v + 1]; e++)
                        {
                            int w = g.Tail[g.InEdge[e]];
                            if (inSet[w] == stamp && w != v) adj[i] |= 1UL << localIdx[w];
                        }
                    }
                    ulong remaining = count == 64 ? ulong.MaxValue : (1UL << count) - 1;
                    for (int k = 0; k < count; k++)
                    {
                        int pick = -1, pickDeg = int.MaxValue;
                        for (int i = 0; i < count; i++)
                        {
                            if ((remaining & (1UL << i)) == 0) continue;
                            int d = PopCount(adj[i] & remaining);
                            if (d < pickDeg) { pickDeg = d; pick = i; }
                        }
                        cellPaths[set[pick]] = new CellPath { PathBits = path, Depth = depth };
                        rank[set[pick]] = next++;
                        remaining &= ~(1UL << pick);
                        ulong nbrs = adj[pick] & remaining;
                        ulong rest = nbrs; // eliminated node's remaining neighbours become a clique
                        while (rest != 0)
                        {
                            int j = TrailingZeros(rest);
                            rest &= rest - 1;
                            adj[j] |= nbrs & ~(1UL << j);
                        }
                    }
                    return;
                }

                // --- Partition into A / B (+ separator when flow-cut succeeds) ---
                bool flowSplit = false;
                if (UseInertialFlow && count >= FlowMinCell)
                    flowSplit = InertialFlowSplit(g, set, count, side, inSet, stamp, localIdx);

                if (!flowSplit)
                {
                    float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                    if (g.X != null && g.Y != null)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            int v = set[i];
                            if (g.X[v] < minX) minX = g.X[v];
                            if (g.X[v] > maxX) maxX = g.X[v];
                            if (g.Y[v] < minY) minY = g.Y[v];
                            if (g.Y[v] > maxY) maxY = g.Y[v];
                        }
                    }
                    if (g.X != null && g.Y != null && ((maxX - minX) > 0f || (maxY - minY) > 0f))
                    {
                        bool splitX = (maxX - minX) >= (maxY - minY);
                        var keys = new float[count];
                        for (int i = 0; i < count; i++) keys[i] = splitX ? g.X![set[i]] : g.Y![set[i]];
                        var idx = new int[count];
                        for (int i = 0; i < count; i++) idx[i] = i;
                        Array.Sort(keys, idx);

                        // Min-crossing cut: instead of blindly cutting at the median,
                        // pick the split position in the middle window that severs the
                        // fewest edges (road networks concentrate crossings on
                        // arterial lines; the sparse boundaries between districts are
                        // where the small separators live).
                        var posOf = new int[count];
                        for (int i = 0; i < count; i++) posOf[idx[i]] = i; // set-local index -> sorted position
                        var cross = new int[count + 1];
                        for (int i = 0; i < count; i++)
                        {
                            int v = set[i];
                            int pv = posOf[i];
                            for (int e = g.OutStart[v]; e < g.OutStart[v + 1]; e++)
                            {
                                int w = g.Head[e];
                                if (inSet[w] != stamp) continue;
                                int pw = posOf[localIdx[w]];
                                int plo = Math.Min(pv, pw), phi = Math.Max(pv, pw);
                                if (plo == phi) continue;
                                cross[plo + 1]++; cross[phi + 1]--;
                            }
                        }
                        int loW = Math.Max(1, (int)(count * 0.30f)), hiW = Math.Min(count - 1, (int)(count * 0.70f));
                        int bestS = count / 2, bestCross = int.MaxValue, run = 0;
                        for (int s2 = 1; s2 <= hiW; s2++)
                        {
                            run += cross[s2];
                            if (s2 < loW) continue;
                            // prefer fewer crossings; tie-break toward balance
                            if (run < bestCross || (run == bestCross && Math.Abs(s2 - count / 2) < Math.Abs(bestS - count / 2)))
                            {
                                bestCross = run; bestS = s2;
                            }
                        }
                        for (int i = 0; i < count; i++) side[set[idx[i]]] = (byte)(i < bestS ? 0 : 1);
                    }
                    else
                    {
                        BfsBisect(g, set, count, side, inSet, stamp);
                    }

                    // Separator: nodes on side A adjacent (either direction) to side B.
                    for (int i = 0; i < count; i++)
                    {
                        int v = set[i];
                        if (side[v] != 0) continue;
                        bool boundary = false;
                        for (int e = g.OutStart[v]; e < g.OutStart[v + 1] && !boundary; e++)
                        {
                            int w = g.Head[e];
                            if (inSet[w] == stamp && side[w] == 1) boundary = true;
                        }
                        for (int e = g.InStart[v]; e < g.InStart[v + 1] && !boundary; e++)
                        {
                            int w = g.Tail[g.InEdge[e]];
                            if (inSet[w] == stamp && side[w] == 1) boundary = true;
                        }
                        if (boundary) side[v] = 2;
                    }
                }

                int nA = 0, nB = 0, nSep = 0;
                for (int i = 0; i < count; i++)
                {
                    byte sd = side[set[i]];
                    if (sd == 0) nA++;
                    else if (sd == 1) nB++;
                    else nSep++;
                }

                // Degenerate split guard (disconnected chunks etc.): fall back
                // to sequential ordering of the whole set.
                if (nA == 0 || nB == 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        cellPaths[set[i]] = new CellPath { PathBits = path, Depth = depth };
                        rank[set[i]] = next++;
                    }
                    return;
                }

                var a = new int[nA]; var b = new int[nB]; var s = new int[nSep];
                int ia = 0, ib = 0, isep = 0;
                for (int i = 0; i < count; i++)
                {
                    int v = set[i];
                    if (side[v] == 0) a[ia++] = v;
                    else if (side[v] == 1) b[ib++] = v;
                    else s[isep++] = v;
                }

                // Recurse: both halves first (lower ranks), separator last
                // (highest). Separator nodes belong to the cell they separate.
                byte childDepth = depth < 62 ? (byte)(depth + 1) : depth;
                ulong bBit = depth < 62 ? 1UL << depth : 0UL;
                Recurse(a, nA, path, childDepth);
                Recurse(b, nB, path | bBit, childDepth);
                for (int i = 0; i < nSep; i++)
                {
                    cellPaths[s[i]] = new CellPath { PathBits = path, Depth = depth };
                    rank[s[i]] = next++;
                }
            }

            Recurse(nodes, n, 0UL, 0);
            if (next != n) throw new InvalidOperationException($"order incomplete: {next}/{n}");
            return rank;
        }

        /// <summary>Inertial Flow split of one cell. On success fills side[]
        /// with 0 (A) / 1 (B) / 2 (separator) for every node of the set and
        /// returns true; on failure (every direction aborted past its flow
        /// budget) leaves side[] untouched and returns false so the caller
        /// falls back to the geometric sweep. The separator comes straight out
        /// of the min-cut, so no adjacency-derivation pass is needed — and it
        /// is a true minimum vertex cut between the outer quarters, not a
        /// straight line.</summary>
        private static bool InertialFlowSplit(
            Graph g, int[] set, int count, byte[] side, int[] inSet, int stamp, int[] localIdx)
        {
            // Unique in-cell adjacency pairs, directed (i -> j) once per side.
            // nbrMark[j] == i marks "arc i->j already recorded"; i only grows,
            // so stale marks never collide.
            var pairsI = new List<int>(count * 3);
            var pairsJ = new List<int>(count * 3);
            var nbrMark = new int[count];
            for (int i = 0; i < count; i++) nbrMark[i] = -1;
            for (int i = 0; i < count; i++)
            {
                int v = set[i];
                for (int e = g.OutStart[v]; e < g.OutStart[v + 1]; e++)
                {
                    int w = g.Head[e];
                    if (inSet[w] != stamp) continue;
                    int j = localIdx[w];
                    if (j != i && nbrMark[j] != i) { nbrMark[j] = i; pairsI.Add(i); pairsJ.Add(j); }
                }
                for (int e = g.InStart[v]; e < g.InStart[v + 1]; e++)
                {
                    int w = g.Tail[g.InEdge[e]];
                    if (inSet[w] != stamp) continue;
                    int j = localIdx[w];
                    if (j != i && nbrMark[j] != i) { nbrMark[j] = i; pairsI.Add(i); pairsJ.Add(j); }
                }
            }

            // Candidate projection axes. With coordinates: x and y, plus the
            // diagonals when the cell is small enough that two extra max-flows
            // are cheap. Without coordinates: a BFS-level embedding from a
            // pseudo-peripheral node — the flow then cuts the level structure
            // where it is thinnest instead of at the median level.
            var projections = new List<float[]>(4);
            if (g.X != null && g.Y != null)
            {
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                for (int i = 0; i < count; i++)
                {
                    int v = set[i];
                    if (g.X[v] < minX) minX = g.X[v];
                    if (g.X[v] > maxX) maxX = g.X[v];
                    if (g.Y[v] < minY) minY = g.Y[v];
                    if (g.Y[v] > maxY) maxY = g.Y[v];
                }
                if ((maxX - minX) > 0f || (maxY - minY) > 0f)
                {
                    var px = new float[count]; var py = new float[count];
                    for (int i = 0; i < count; i++) { px[i] = g.X[set[i]]; py[i] = g.Y[set[i]]; }
                    projections.Add(px);
                    projections.Add(py);
                    if (count <= TwoDirThreshold)
                    {
                        var pd = new float[count]; var pe = new float[count];
                        for (int i = 0; i < count; i++) { pd[i] = px[i] + py[i]; pe[i] = px[i] - py[i]; }
                        projections.Add(pd);
                        projections.Add(pe);
                    }
                }
            }
            if (projections.Count == 0)
            {
                var level = new Dictionary<int, int>(count);
                int start = FarthestBfs(g, set[0], set, count, inSet, stamp, level);
                level.Clear();
                FarthestBfs(g, start, set, count, inSet, stamp, level);
                int maxLv = 0;
                foreach (var l in level.Values) if (l > maxLv) maxLv = l;
                var pk = new float[count];
                for (int i = 0; i < count; i++)
                    pk[i] = level.TryGetValue(set[i], out var l) ? l : maxLv + 1;
                projections.Add(pk);
            }

            int q = count / 4; // terminal quarter size => balance b = 0.25
            // Flow budget: a separator past ~4·sqrt(n) is no better than the
            // geometric cut it replaces — abort and let the fallback run. Also
            // guards the source-adjacent-to-sink case (one ∞ augmentation
            // blows straight through the budget).
            long flowLimit = Math.Max(64, 4 * (long)Math.Sqrt(count));

            byte[]? bestSides = null;
            int bestSep = int.MaxValue, bestImbal = int.MaxValue;
            var order = new int[count];
            foreach (var keys in projections)
            {
                for (int i = 0; i < count; i++) order[i] = i;
                var sortKeys = (float[])keys.Clone(); // Array.Sort mutates the key array
                Array.Sort(sortKeys, order);
                var role = new byte[count]; // 0 = middle, 1 = source, 2 = sink
                for (int i = 0; i < q; i++) role[order[i]] = 1;
                for (int i = count - q; i < count; i++) role[order[i]] = 2;

                var sides = TryFlowCut(count, pairsI, pairsJ, role, flowLimit,
                                       out int sepCount, out int imbal);
                if (sides == null) continue;
                if (sepCount < bestSep || (sepCount == bestSep && imbal < bestImbal))
                {
                    bestSep = sepCount; bestImbal = imbal; bestSides = sides;
                }
            }
            if (bestSides == null) return false;
            for (int i = 0; i < count; i++) side[set[i]] = bestSides[i];
            return true;
        }

        /// <summary>One max-flow attempt for one projection. Node-splitting
        /// reduction: every middle node v becomes v_in -> v_out with capacity 1
        /// (terminal nodes get ∞ so they cannot be cut), undirected adjacency
        /// becomes u_out -> v_in ∞ both ways, super-source feeds every source
        /// v_in and every sink v_out drains to the super-sink. Max-flow equals
        /// the minimum vertex cut; the cut nodes are exactly those with v_in
        /// residual-reachable from the source but v_out not (∞ arcs can never
        /// be the saturated crossing). Sources always end reachable and sinks
        /// never do, so both sides keep at least their terminal quarter.</summary>
        private static byte[]? TryFlowCut(
            int count, List<int> pairsI, List<int> pairsJ, byte[] role, long flowLimit,
            out int sepCount, out int imbal)
        {
            sepCount = 0; imbal = 0;
            const int Inf = int.MaxValue / 4;
            int S = 2 * count, T = 2 * count + 1;
            int termArcs = 0;
            for (int i = 0; i < count; i++) if (role[i] != 0) termArcs++;
            var din = new Dinic(2 * count + 2, count + termArcs + pairsI.Count);
            for (int i = 0; i < count; i++)
                din.AddEdge(2 * i, 2 * i + 1, role[i] == 0 ? 1 : Inf);
            for (int i = 0; i < count; i++)
            {
                if (role[i] == 1) din.AddEdge(S, 2 * i, Inf);
                else if (role[i] == 2) din.AddEdge(2 * i + 1, T, Inf);
            }
            for (int p = 0; p < pairsI.Count; p++)
                din.AddEdge(2 * pairsI[p] + 1, 2 * pairsJ[p], Inf);

            long flow = din.Run(S, T, flowLimit);
            if (flow > flowLimit) return null;

            var sides = new byte[count];
            int nA = 0, nB = 0;
            for (int i = 0; i < count; i++)
            {
                bool inReach = din.Reachable(2 * i);
                if (inReach && !din.Reachable(2 * i + 1)) { sides[i] = 2; sepCount++; }
                else if (inReach) { sides[i] = 0; nA++; }
                else { sides[i] = 1; nB++; }
            }
            if (nA == 0 || nB == 0) return null;
            imbal = Math.Abs(nA - nB);
            return sides;
        }

        /// <summary>Array-based Dinic with a current-arc iterative augmenter
        /// (no recursion — augmenting paths can be graph-diameter long). After
        /// Run completes under its limit, level[] holds the final residual BFS,
        /// so Reachable() reads the min-cut side directly.</summary>
        private sealed class Dinic
        {
            private readonly int n;
            private int m;
            private readonly int[] head, nxt, to, cap;
            private readonly int[] level, iter, queue, pathEdge, pathNode;

            public Dinic(int nodes, int edgeCount)
            {
                n = nodes;
                head = new int[n];
                for (int i = 0; i < n; i++) head[i] = -1;
                nxt = new int[2 * edgeCount];
                to = new int[2 * edgeCount];
                cap = new int[2 * edgeCount];
                level = new int[n];
                iter = new int[n];
                queue = new int[n];
                pathEdge = new int[n];
                pathNode = new int[n];
            }

            public void AddEdge(int u, int v, int c)
            {
                to[m] = v; cap[m] = c; nxt[m] = head[u]; head[u] = m++;
                to[m] = u; cap[m] = 0; nxt[m] = head[v]; head[v] = m++;
            }

            public long Run(int s, int t, long limit)
            {
                long flow = 0;
                while (Bfs(s, t))
                {
                    Array.Copy(head, iter, n);
                    int f;
                    while ((f = Augment(s, t)) > 0)
                    {
                        flow += f;
                        if (flow > limit) return flow;
                    }
                }
                return flow; // final Bfs rebuilt level[] = residual reachability
            }

            public bool Reachable(int v) => level[v] >= 0;

            private bool Bfs(int s, int t)
            {
                for (int i = 0; i < n; i++) level[i] = -1;
                int qh = 0, qt = 0;
                queue[qt++] = s; level[s] = 0;
                while (qh < qt)
                {
                    int u = queue[qh++];
                    for (int e = head[u]; e != -1; e = nxt[e])
                    {
                        int w = to[e];
                        if (cap[e] > 0 && level[w] < 0)
                        {
                            level[w] = level[u] + 1;
                            queue[qt++] = w;
                        }
                    }
                }
                return level[t] >= 0;
            }

            private int Augment(int s, int t)
            {
                int top = 0;
                int u = s;
                while (true)
                {
                    if (u == t)
                    {
                        int f = int.MaxValue;
                        for (int k = 0; k < top; k++) if (cap[pathEdge[k]] < f) f = cap[pathEdge[k]];
                        for (int k = 0; k < top; k++)
                        {
                            cap[pathEdge[k]] -= f;
                            cap[pathEdge[k] ^ 1] += f;
                        }
                        return f;
                    }
                    int e = iter[u];
                    while (e != -1 && !(cap[e] > 0 && level[to[e]] == level[u] + 1)) e = nxt[e];
                    iter[u] = e;
                    if (e == -1)
                    {
                        level[u] = -1; // dead end this phase
                        if (top == 0) return 0;
                        top--;
                        u = pathNode[top];
                        iter[u] = nxt[iter[u]];
                    }
                    else
                    {
                        pathNode[top] = u;
                        pathEdge[top++] = e;
                        u = to[e];
                    }
                }
            }
        }

        private static void BfsBisect(Graph g, int[] set, int count, byte[] side, int[] inSet, int stamp)
        {
            // Level-set bisection from a pseudo-peripheral node (double BFS).
            var level = new Dictionary<int, int>(count);
            int start = set[0];
            start = FarthestBfs(g, start, set, count, inSet, stamp, level);
            level.Clear();
            FarthestBfs(g, start, set, count, inSet, stamp, level);
            // median level
            var lv = new int[count];
            for (int i = 0; i < count; i++) lv[i] = level.TryGetValue(set[i], out var l) ? l : int.MaxValue;
            var sorted = (int[])lv.Clone();
            Array.Sort(sorted);
            int med = sorted[count / 2];
            for (int i = 0; i < count; i++) side[set[i]] = (byte)(lv[i] < med ? 0 : 1);
        }

        private static int FarthestBfs(Graph g, int start, int[] set, int count, int[] inSet, int stamp, Dictionary<int, int> level)
        {
            var q = new Queue<int>();
            q.Enqueue(start); level[start] = 0;
            int last = start;
            while (q.Count > 0)
            {
                int v = q.Dequeue(); last = v;
                int lv = level[v];
                for (int e = g.OutStart[v]; e < g.OutStart[v + 1]; e++)
                {
                    int w = g.Head[e];
                    if (inSet[w] == stamp && !level.ContainsKey(w)) { level[w] = lv + 1; q.Enqueue(w); }
                }
                for (int e = g.InStart[v]; e < g.InStart[v + 1]; e++)
                {
                    int w = g.Tail[g.InEdge[e]];
                    if (inSet[w] == stamp && !level.ContainsKey(w)) { level[w] = lv + 1; q.Enqueue(w); }
                }
            }
            return last;
        }

        private static int PopCount(ulong x)
        {
            x -= (x >> 1) & 0x5555555555555555UL;
            x = (x & 0x3333333333333333UL) + ((x >> 2) & 0x3333333333333333UL);
            x = (x + (x >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((x * 0x0101010101010101UL) >> 56);
        }

        private static int TrailingZeros(ulong x)
        {
            int c = 0;
            while ((x & 1UL) == 0) { x >>= 1; c++; }
            return c;
        }

        /// <summary>Spatial wake-up regions for Layer 4 (coarse tiles over
        /// coordinates; hash regions when no geometry exists).</summary>
        public static int[] ComputeRegions(Graph g, int tilesPerAxis, out int regionCount)
        {
            int n = g.NodeCount;
            var region = new int[n];
            if (g.X == null || g.Y == null)
            {
                regionCount = 64;
                for (int v = 0; v < n; v++) region[v] = v % regionCount;
                return region;
            }
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int v = 0; v < n; v++)
            {
                if (g.X[v] < minX) minX = g.X[v];
                if (g.X[v] > maxX) maxX = g.X[v];
                if (g.Y[v] < minY) minY = g.Y[v];
                if (g.Y[v] > maxY) maxY = g.Y[v];
            }
            float sx = (maxX - minX) > 0 ? tilesPerAxis / (maxX - minX + 1e-3f) : 0;
            float sy = (maxY - minY) > 0 ? tilesPerAxis / (maxY - minY + 1e-3f) : 0;
            for (int v = 0; v < n; v++)
            {
                int tx = Math.Min(tilesPerAxis - 1, (int)((g.X[v] - minX) * sx));
                int ty = Math.Min(tilesPerAxis - 1, (int)((g.Y[v] - minY) * sy));
                region[v] = ty * tilesPerAxis + tx;
            }
            regionCount = tilesPerAxis * tilesPerAxis;
            return region;
        }
    }
}
