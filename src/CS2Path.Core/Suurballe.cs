using System;
using System.Collections.Generic;

namespace CS2Path.Core
{
    /// <summary>
    /// Suurballe-style edge-disjoint path pair (plan §4 Layer 2): long or
    /// transit-dependent trips carry one backup guaranteed to survive any
    /// single-link failure on the primary. Classic two-phase construction on
    /// reduced costs with reversed primary edges; the search is corridor-bounded
    /// (nodes beyond CorridorFactor x d(t) are not explored), which keeps the
    /// cost near two bounded Dijkstras.
    /// </summary>
    public static class Suurballe
    {
        public const float CorridorFactor = 1.5f;
        /// <summary>Hard cap on phase-1 settled nodes: backups are a per-trip
        /// nicety, not worth an unbounded search on city-scale graphs.</summary>
        public const int MaxSettled = 25_000;

        /// <summary>Find two edge-disjoint s->t paths with small total weight.
        /// Returns false if no disjoint pair exists inside the corridor.</summary>
        public static bool FindDisjointPair(Graph g, int s, int t, Func<int, float> w,
                                            List<int> path1Out, List<int> path2Out)
        {
            path1Out.Clear(); path2Out.Clear();
            int n = g.NodeCount;
            var d = new Dictionary<int, float>();
            var pred = new Dictionary<int, int>();
            var heap = new PotentialAStar.FloatHeap(1024);

            // Phase 1: Dijkstra from s, corridor-bounded.
            d[s] = 0; heap.Push(0, s);
            var settled = new HashSet<int>();
            float dt = float.PositiveInfinity;
            while (heap.Count > 0)
            {
                int v = heap.PopPayload();
                if (settled.Contains(v)) continue;
                settled.Add(v);
                float dv = d[v];
                if (v == t) dt = dv;
                // additive slack keeps the corridor meaningful under
                // near-zero-cost metrics (e.g. pure-money with toll-free edges)
                if (dv > dt * CorridorFactor + 1e-3f) break;
                if (settled.Count > MaxSettled && float.IsPositiveInfinity(dt)) return false;
                if (settled.Count > MaxSettled * 2) break;
                for (int e = g.OutStart[v]; e < g.OutStart[v + 1]; e++)
                {
                    float we = w(e);
                    if (float.IsPositiveInfinity(we)) continue;
                    int h = g.Head[e];
                    float cand = dv + we;
                    if (!d.TryGetValue(h, out var dh) || cand < dh)
                    {
                        d[h] = cand; pred[h] = e;
                        heap.Push(cand, h);
                    }
                }
            }
            if (float.IsPositiveInfinity(dt)) return false;

            // Primary path.
            var p1Edges = new List<int>();
            var p1In = new Dictionary<int, int>();     // node -> P1 edge entering it
            var p1EdgeSet = new HashSet<int>();
            {
                int cur = t;
                while (cur != s)
                {
                    int e = pred[cur];
                    p1Edges.Add(e); p1EdgeSet.Add(e); p1In[cur] = e;
                    cur = g.Tail[e];
                }
                p1Edges.Reverse();
            }

            // Phase 2: Dijkstra on reduced costs, P1 forward edges forbidden,
            // P1 edges traversable in reverse at cost 0. Restricted to the
            // settled corridor (outside it reduced costs are undefined).
            var d2 = new Dictionary<int, float>();
            var pred2 = new Dictionary<int, (int edge, bool rev)>();
            var settled2 = new HashSet<int>();
            heap.Clear();
            d2[s] = 0; heap.Push(0, s);
            bool found = false;
            while (heap.Count > 0)
            {
                int v = heap.PopPayload();
                if (settled2.Contains(v)) continue;
                settled2.Add(v);
                if (v == t) { found = true; break; }
                float dv2 = d2[v];
                float dv = d[v];
                for (int e = g.OutStart[v]; e < g.OutStart[v + 1]; e++)
                {
                    if (p1EdgeSet.Contains(e)) continue;
                    int h = g.Head[e];
                    if (!d.TryGetValue(h, out var dh) || !settled.Contains(h)) continue;
                    float we = w(e);
                    if (float.IsPositiveInfinity(we)) continue;
                    float red = Math.Max(0f, we + dv - dh);
                    float cand = dv2 + red;
                    if (!d2.TryGetValue(h, out var dh2) || cand < dh2)
                    {
                        d2[h] = cand; pred2[h] = (e, false); heap.Push(cand, h);
                    }
                }
                // reverse traversal of the P1 edge entering v (reduced cost 0)
                if (p1In.TryGetValue(v, out int pe))
                {
                    int u = g.Tail[pe];
                    if (!d2.TryGetValue(u, out var du2) || dv2 < du2)
                    {
                        d2[u] = dv2; pred2[u] = (pe, true); heap.Push(dv2, u);
                    }
                }
            }
            if (!found) return false;

            // Collect P2' moves; cancelled pairs are P1 edges traversed in reverse.
            var cancelled = new HashSet<int>();
            var p2Normal = new List<int>();
            {
                int cur = t;
                while (cur != s)
                {
                    var (e, rev) = pred2[cur];
                    if (rev) { cancelled.Add(e); cur = g.Head[e]; }
                    else { p2Normal.Add(e); cur = g.Tail[e]; }
                }
            }

            // Union multigraph = (P1 \ cancelled) + P2' normal edges: walk out
            // two edge-disjoint s->t paths.
            var outAdj = new Dictionary<int, List<int>>();
            void AddE(int e)
            {
                int u = g.Tail[e];
                if (!outAdj.TryGetValue(u, out var l)) { l = new List<int>(2); outAdj[u] = l; }
                l.Add(e);
            }
            foreach (var e in p1Edges) if (!cancelled.Contains(e)) AddE(e);
            foreach (var e in p2Normal) AddE(e);

            bool Walk(List<int> pathOut)
            {
                int cur = s;
                int guard = 0;
                var firstAt = new Dictionary<int, int> { [s] = 0 };
                while (cur != t)
                {
                    if (!outAdj.TryGetValue(cur, out var l) || l.Count == 0) return false;
                    int e = l[l.Count - 1];
                    l.RemoveAt(l.Count - 1);
                    pathOut.Add(e);
                    cur = g.Head[e];
                    // splice out cycles so the walk is a simple path — dropping
                    // union edges never breaks the pair's edge-disjointness
                    if (firstAt.TryGetValue(cur, out var pos))
                    {
                        pathOut.RemoveRange(pos, pathOut.Count - pos);
                        foreach (var kv2 in new List<KeyValuePair<int, int>>(firstAt))
                            if (kv2.Value > pos) firstAt.Remove(kv2.Key);
                    }
                    else firstAt[cur] = pathOut.Count;
                    if (++guard > 4 * n) return false;
                }
                return true;
            }
            if (!Walk(path1Out)) return false;
            if (!Walk(path2Out)) { path1Out.Clear(); return false; }
            return true;
        }
    }
}
