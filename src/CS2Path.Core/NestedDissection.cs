using System;
using System.Collections.Generic;

namespace CS2Path.Core
{
    /// <summary>
    /// Layer 0 (part 1): metric-independent contraction order via nested
    /// dissection (plan §4 Layer 0). Road networks are near-planar, so small
    /// separators exist; separator nodes are ranked highest and we recurse.
    /// Uses geometric recursive bisection when coordinates are available
    /// (median split along the wider extent, separator = boundary nodes),
    /// falling back to BFS level-set bisection otherwise.
    /// </summary>
    public static class NestedDissection
    {
        /// <summary>Compute an elimination order. rank[v] in [0, n); higher
        /// rank = later elimination = higher in the hierarchy.</summary>
        public static int[] ComputeOrder(Graph g)
        {
            int n = g.NodeCount;
            var rank = new int[n];
            var nodes = new int[n];
            for (int i = 0; i < n; i++) nodes[i] = i;
            int next = 0; // next rank to assign, ascending

            // Undirected adjacency (union of both directions) for separator finding.
            // Reuse CSR by scanning both out-edges and in-edges.
            var side = new byte[n];        // scratch: 0 = A, 1 = B, 2 = separator
            var inSet = new int[n];        // scratch: recursion-set membership stamp
            int stamp = 0;

            void Recurse(int[] set, int count)
            {
                const int BaseCase = 48;
                if (count <= BaseCase)
                {
                    // Local order: sequential is fine inside a small spatially
                    // coherent cell; fill-in is bounded by the cell size.
                    for (int i = 0; i < count; i++) rank[set[i]] = next++;
                    return;
                }

                stamp++;
                for (int i = 0; i < count; i++) inSet[set[i]] = stamp;

                // --- Partition into A / B ---
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
                    for (int i = 0; i < count; i++) posOf[idx[i]] = i;      // set-local index -> sorted position
                    var setPos = new Dictionary<int, int>(count);
                    for (int i = 0; i < count; i++) setPos[set[i]] = posOf[i];
                    var cross = new int[count + 1];
                    for (int i = 0; i < count; i++)
                    {
                        int v = set[i];
                        int pv = posOf[i];
                        for (int e = g.OutStart[v]; e < g.OutStart[v + 1]; e++)
                        {
                            int w = g.Head[e];
                            if (inSet[w] != stamp || !setPos.TryGetValue(w, out var pw)) continue;
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

                // --- Separator: nodes on side A adjacent (either direction) to side B ---
                int nSep = 0;
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
                    if (boundary) { side[v] = 2; nSep++; }
                }

                // Degenerate split guard (disconnected chunks etc.): fall back
                // to sequential ordering of the whole set.
                int nA = 0, nB = 0;
                for (int i = 0; i < count; i++)
                {
                    if (side[set[i]] == 0) nA++;
                    else if (side[set[i]] == 1) nB++;
                }
                if (nA == 0 || nB == 0)
                {
                    for (int i = 0; i < count; i++) rank[set[i]] = next++;
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

                // Recurse: both halves first (lower ranks), separator last (highest).
                Recurse(a, nA);
                Recurse(b, nB);
                for (int i = 0; i < nSep; i++) rank[s[i]] = next++;
            }

            Recurse(nodes, n);
            if (next != n) throw new InvalidOperationException($"order incomplete: {next}/{n}");
            return rank;
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
