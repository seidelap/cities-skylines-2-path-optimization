using System;
using System.Collections.Generic;
using CS2Path.Core;

namespace CS2Path.Harness
{
    /// <summary>
    /// Synthetic city generator (plan §5: export/replay harness). Grid street
    /// network with three road tiers (local / arterial / highway) carrying
    /// distinct time, money (tolls) and comfort trade-offs, random holes for
    /// irregularity, plus a citizen population with heterogeneous continuous
    /// preferences and a destination set for flexible trips.
    /// </summary>
    public sealed class SyntheticCity
    {
        public Graph G = null!;
        public int Cols, Rows;
        public float[] JamCapacity = null!;   // max vehicles stored per edge
        public Preference[] Citizens = null!;
        public float[] CitizenTripWeight = null!;
        public DestinationSet Dests = null!;

        // meters between intersections
        public const float Spacing = 100f;

        public static SyntheticCity Build(int cols, int rows, int citizens, int destinations, ulong seed,
                                          float holeFraction = 0.06f)
        {
            var rng = new SplitMix64(seed);
            int n0 = cols * rows;
            var removed = new HashSet<long>();
            long PairKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

            // Random holes in the local grid (parks, water). Arterials/highways kept.
            // District walls: local streets do NOT cross superblock boundaries
            // (every 32nd line) — only arterials do, as in real city layouts.
            // This gives the road network its real hierarchical separator
            // structure instead of full-grid treewidth.
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int v = r * cols + c;
                    if (c + 1 < cols &&
                        (!IsArterialRow(r) && (rng.NextFloat() < holeFraction || (c + 1) % 32 == 0)))
                        removed.Add(PairKey(v, v + 1));
                    if (r + 1 < rows &&
                        (!IsArterialCol(c) && (rng.NextFloat() < holeFraction || (r + 1) % 32 == 0)))
                        removed.Add(PairKey(v, v + cols));
                }

            // Undirected connectivity (edges are symmetric pairs) -> largest component.
            var adj = new List<int>[n0];
            for (int v = 0; v < n0; v++) adj[v] = new List<int>(4);
            void TryLink(int a, int b) { if (!removed.Contains(PairKey(a, b))) { adj[a].Add(b); adj[b].Add(a); } }
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int v = r * cols + c;
                    if (c + 1 < cols) TryLink(v, v + 1);
                    if (r + 1 < rows) TryLink(v, v + cols);
                }
            var comp = new int[n0];
            Array.Fill(comp, -1);
            int nc = 0, bestComp = -1, bestSize = 0;
            var stack = new Stack<int>();
            for (int v = 0; v < n0; v++)
            {
                if (comp[v] >= 0) continue;
                int size = 0;
                stack.Push(v); comp[v] = nc;
                while (stack.Count > 0)
                {
                    int u = stack.Pop(); size++;
                    foreach (var w in adj[u]) if (comp[w] < 0) { comp[w] = nc; stack.Push(w); }
                }
                if (size > bestSize) { bestSize = size; bestComp = nc; }
                nc++;
            }
            var newId = new int[n0];
            Array.Fill(newId, -1);
            int nn = 0;
            for (int v = 0; v < n0; v++) if (comp[v] == bestComp) newId[v] = nn++;

            // Build directed edges with tiered attributes.
            var edges = new List<(int u, int v, float t, float m, float c, float cap)>(nn * 4);
            var x = new float[nn]; var y = new float[nn];
            var jam = new List<float>(nn * 4);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int v = r * cols + c;
                    if (newId[v] < 0) continue;
                    x[newId[v]] = c * Spacing; y[newId[v]] = r * Spacing;
                    Emit(edges, jam, newId, removed, PairKey, v, c + 1 < cols ? v + 1 : -1, TierOfRow(r));
                    Emit(edges, jam, newId, removed, PairKey, v, r + 1 < rows ? v + cols : -1, TierOfCol(c));
                }

            var city = new SyntheticCity { Cols = cols, Rows = rows };
            city.G = Graph.Build(nn, edges, x, y);
            city.JamCapacity = jam.ToArray();

            // Citizens: heterogeneous continuous preferences (plan §3).
            city.Citizens = new Preference[citizens];
            city.CitizenTripWeight = new float[citizens];
            for (int i = 0; i < citizens; i++)
            {
                float t = 0.5f + (float)Math.Exp(0.5 * Gauss(ref rng));       // value of time, ~lognormal
                float m = 0.05f + 0.6f * (float)(-Math.Log(1 - rng.NextDouble() * 0.999)); // money sensitivity
                float cf = 0.9f * rng.NextFloat() * rng.NextFloat();          // comfort sensitivity
                city.Citizens[i] = new Preference(t, m, cf);
                city.CitizenTripWeight[i] = 0.5f + 2f * rng.NextFloat();      // heavy travelers weigh more
            }

            // Destinations for flexible trips.
            int cats = 4;
            city.Dests = new DestinationSet
            {
                Node = new int[destinations],
                Category = new int[destinations],
                AttractionSeconds = new float[destinations],
                Price = new float[destinations],
                CategoryCount = cats,
            };
            for (int d = 0; d < destinations; d++)
            {
                city.Dests.Node[d] = rng.NextInt(nn);
                city.Dests.Category[d] = rng.NextInt(cats);
                city.Dests.AttractionSeconds[d] = 60f * (float)(-Math.Log(1 - rng.NextDouble() * 0.999));
                city.Dests.Price[d] = 20f * rng.NextFloat();
            }
            return city;
        }

        private static bool IsArterialRow(int r) => r % 8 == 0;
        private static bool IsArterialCol(int c) => c % 8 == 0;
        private static int TierOfRow(int r) => r % 32 == 0 ? 2 : (r % 8 == 0 ? 1 : 0);
        private static int TierOfCol(int c) => c % 32 == 0 ? 2 : (c % 8 == 0 ? 1 : 0);

        private static void Emit(List<(int, int, float, float, float, float)> edges, List<float> jam,
                                 int[] newId, HashSet<long> removed, Func<int, int, long> pairKey,
                                 int v, int w, int tier)
        {
            if (w < 0 || newId[v] < 0 || newId[w] < 0) return;
            if (removed.Contains(pairKey(v, w))) return;
            // tier: 0 local 40km/h, 1 arterial 60km/h, 2 highway 100km/h
            float speed = tier == 2 ? 27.8f : tier == 1 ? 16.7f : 11.1f;   // m/s
            float capacity = tier == 2 ? 10f : tier == 1 ? 5f : 2f;        // vehicles/tick service rate
            float lanes = tier == 2 ? 3f : tier == 1 ? 2f : 1f;
            float t = Spacing / speed;
            float money = Spacing * 0.001f * (tier == 2 ? 4f : 1f);        // fuel + highway toll
            float comfort = t * (tier == 2 ? 0.5f : tier == 1 ? 0.15f : 0.3f); // discomfort units
            int a = newId[v], b = newId[w];
            edges.Add((a, b, t, money, comfort, capacity));
            jam.Add(lanes * Spacing / 8f);                                  // ~8 m/vehicle storage
            edges.Add((b, a, t, money, comfort, capacity));
            jam.Add(lanes * Spacing / 8f);
        }

        private static float Gauss(ref SplitMix64 rng)
        {
            double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
            return (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        public AnchorGrid BuildAnchors(int profileCount, bool includeTypical = false)
        {
            var scen = includeTypical
                ? new[] { Scenario.FreeFlow, Scenario.Typical, Scenario.Live }
                : new[] { Scenario.FreeFlow, Scenario.Live };
            return AnchorGrid.Build(Citizens, CitizenTripWeight, profileCount, scen);
        }
    }

    /// <summary>Reference shortest-path implementations for correctness checks
    /// and the vanilla baseline (per-trip Dijkstra, plan §1.1).</summary>
    public static class Reference
    {
        [ThreadStatic] private static float[]? _dist;
        [ThreadStatic] private static int[]? _pred;
        [ThreadStatic] private static int[]? _stampArr;
        [ThreadStatic] private static int[]? _doneArr;
        [ThreadStatic] private static int _stamp;

        public static float Dijkstra(Graph g, int s, int t, Func<int, float> w, List<int>? pathOut = null)
        {
            int n = g.NodeCount;
            if (_dist == null || _dist.Length < n) { _dist = new float[n]; _pred = new int[n]; _stampArr = new int[n]; _doneArr = new int[n]; _stamp = 0; }
            var dist = _dist; var pred = _pred!; var stampArr = _stampArr!; var done = _doneArr!;
            int stamp = ++_stamp;
            var heap = new SimpleHeap(1024);
            dist[s] = 0; stampArr[s] = stamp; pred[s] = -1;
            heap.Push(0, s);
            bool reached = false;
            while (heap.Count > 0)
            {
                var (dv, v) = heap.Pop();
                if (done[v] == stamp) continue;
                done[v] = stamp;
                if (v == t) { reached = true; break; }
                for (int e = g.OutStart[v]; e < g.OutStart[v + 1]; e++)
                {
                    float we = w(e);
                    if (float.IsPositiveInfinity(we)) continue;
                    int h = g.Head[e];
                    float cand = dv + we;
                    if (stampArr[h] != stamp || cand < dist[h])
                    {
                        dist[h] = cand; stampArr[h] = stamp; pred[h] = e;
                        heap.Push(cand, h);
                    }
                }
            }
            if (!reached) return float.PositiveInfinity;
            if (pathOut != null)
            {
                pathOut.Clear();
                int cur = t;
                while (cur != s) { int e = pred[cur]; pathOut.Add(e); cur = g.Tail[e]; }
                pathOut.Reverse();
            }
            return dist[t];
        }

        private struct SimpleHeap
        {
            private float[] _k; private int[] _p; public int Count;
            public SimpleHeap(int cap) { _k = new float[cap]; _p = new int[cap]; Count = 0; }
            public void Push(float k, int p)
            {
                if (Count == _k.Length) { Array.Resize(ref _k, Count * 2); Array.Resize(ref _p, Count * 2); }
                int i = Count++;
                _k[i] = k; _p[i] = p;
                while (i > 0)
                {
                    int q = (i - 1) >> 1;
                    if (_k[q] <= _k[i]) break;
                    (_k[q], _k[i]) = (_k[i], _k[q]); (_p[q], _p[i]) = (_p[i], _p[q]);
                    i = q;
                }
            }
            public (float, int) Pop()
            {
                var res = (_k[0], _p[0]);
                Count--;
                _k[0] = _k[Count]; _p[0] = _p[Count];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, sm = i;
                    if (l < Count && _k[l] < _k[sm]) sm = l;
                    if (r < Count && _k[r] < _k[sm]) sm = r;
                    if (sm == i) break;
                    (_k[sm], _k[i]) = (_k[i], _k[sm]); (_p[sm], _p[i]) = (_p[i], _p[sm]);
                    i = sm;
                }
                return res;
            }
        }
    }
}
