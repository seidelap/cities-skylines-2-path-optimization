using System;
using System.Collections.Generic;

namespace CS2Path.Core
{
    /// <summary>
    /// Agent preference vector over the three cost components (plan §3):
    /// weights for time, money and comfort. Anchor metrics are preferences
    /// too — the anchor grid always contains the coordinate axes so that any
    /// alpha decomposes conically over anchors (§4.7).
    /// </summary>
    public struct Preference
    {
        public float Time, Money, Comfort;

        public Preference(float time, float money, float comfort)
        {
            Time = time; Money = money; Comfort = comfort;
        }

        public static readonly Preference AxisTime = new Preference(1f, 0f, 0f);
        public static readonly Preference AxisMoney = new Preference(0f, 1f, 0f);
        public static readonly Preference AxisComfort = new Preference(0f, 0f, 1f);

        public float Dot(float time, float money, float comfort)
            => Time * time + Money * money + Comfort * comfort;

        public override string ToString() => $"(t={Time:0.###},m={Money:0.###},c={Comfort:0.###})";
    }

    /// <summary>
    /// Directed graph in CSR form with per-edge cost components. This is the
    /// lane-level abstraction the core routes on; the reader adapter flattens
    /// the game's lane/turn graph into it. Edge id == CSR position, stable
    /// until the next Layer-0 rebuild.
    /// </summary>
    public sealed class Graph
    {
        public int NodeCount;
        public int EdgeCount;

        // Forward CSR: out-edges of node v are OutStart[v] .. OutStart[v+1]-1.
        public int[] OutStart = Array.Empty<int>();
        public int[] Head = Array.Empty<int>();     // edge target, indexed by edge id
        public int[] Tail = Array.Empty<int>();     // edge source, indexed by edge id

        // Reverse CSR: in-edge ids of node v are InStart[v] .. InStart[v+1]-1.
        public int[] InStart = Array.Empty<int>();
        public int[] InEdge = Array.Empty<int>();

        // Cost components per edge. Time components in seconds.
        public float[] TimeFree = Array.Empty<float>();     // free-flow scenario
        public float[] TimeTypical = Array.Empty<float>();  // rolling-average scenario
        public float[] TimeLive = Array.Empty<float>();     // live scenario, fed by IMetricFeed
        public float[] Money = Array.Empty<float>();
        public float[] Comfort = Array.Empty<float>();

        // Soft/hard closure multiplier on the live time component (Layer 1).
        // 1 = open; grows continuously with jam duration; +inf = hard closed.
        public float[] ClosureMult = Array.Empty<float>();

        // Optional node coordinates (used by nested dissection and regions).
        public float[]? X, Y;

        // Harness simulation attributes (ignored by the routing core proper).
        public float[] Capacity = Array.Empty<float>();     // vehicles per tick

        public static Graph Build(int nodeCount, List<(int u, int v, float timeFree, float money, float comfort, float capacity)> edges,
                                  float[]? x = null, float[]? y = null)
        {
            var g = new Graph { NodeCount = nodeCount, EdgeCount = edges.Count, X = x, Y = y };
            int n = nodeCount, m = edges.Count;
            g.OutStart = new int[n + 1];
            g.Head = new int[m]; g.Tail = new int[m];
            g.TimeFree = new float[m]; g.TimeTypical = new float[m]; g.TimeLive = new float[m];
            g.Money = new float[m]; g.Comfort = new float[m]; g.Capacity = new float[m];
            g.ClosureMult = new float[m];

            foreach (var e in edges) g.OutStart[e.u + 1]++;
            for (int i = 0; i < n; i++) g.OutStart[i + 1] += g.OutStart[i];
            var cursor = new int[n];
            Array.Copy(g.OutStart, cursor, n);
            foreach (var e in edges)
            {
                int id = cursor[e.u]++;
                g.Tail[id] = e.u; g.Head[id] = e.v;
                g.TimeFree[id] = e.timeFree; g.TimeTypical[id] = e.timeFree; g.TimeLive[id] = e.timeFree;
                g.Money[id] = e.money; g.Comfort[id] = e.comfort; g.Capacity[id] = e.capacity;
                g.ClosureMult[id] = 1f;
            }

            g.InStart = new int[n + 1];
            g.InEdge = new int[m];
            for (int e = 0; e < m; e++) g.InStart[g.Head[e] + 1]++;
            for (int i = 0; i < n; i++) g.InStart[i + 1] += g.InStart[i];
            var cur2 = new int[n];
            Array.Copy(g.InStart, cur2, n);
            for (int e = 0; e < m; e++) g.InEdge[cur2[g.Head[e]]++] = e;
            return g;
        }

        /// <summary>Find the edge id u->v, or -1. If parallel edges exist,
        /// returns the one with minimal free-flow time.</summary>
        public int FindEdge(int u, int v)
        {
            int best = -1; float bestT = float.PositiveInfinity;
            for (int i = OutStart[u]; i < OutStart[u + 1]; i++)
                if (Head[i] == v && TimeFree[i] < bestT) { best = i; bestT = TimeFree[i]; }
            return best;
        }
    }
}
