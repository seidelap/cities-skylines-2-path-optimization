using System;
using System.Collections.Generic;

namespace CS2Path.Core
{
    /// <summary>
    /// Potential-guided A* on the ORIGINAL graph with lazily evaluated CCH
    /// potentials (the CH-Potentials technique, plan §4.7 Repair): exact
    /// queries for an arbitrary metric, guided by preprocessing for related
    /// metrics. h(v) = sum_j lambda_j * dist_j(v, t), where each dist_j is the
    /// exact distance under anchor metric j, evaluated lazily over the upward
    /// chordal graph. Admissible whenever the true edge weight dominates the
    /// lambda-combination of anchor weights — which holds exactly for
    /// alpha = sum lambda_j a_j (repair) and for penalized metrics
    /// (penalty >= 1) over their own base anchor.
    ///
    /// Used by: §4.7 repair searches, and Layer-2 penalty-method alternatives.
    /// </summary>
    public sealed class PotentialAStar
    {
        private readonly CchSkeleton _c;
        private readonly CchMetrics _m;
        private readonly CchQuery _q;

        public const int MaxLanes = 6;
        private readonly float[] _pot;      // n * MaxLanes, memoized potentials
        private readonly int[] _potStamp;
        private readonly float[] _bLab;     // n * MaxLanes, backward upward labels
        private readonly int[] _bStamp;
        private readonly float[] _g;
        private readonly int[] _gStamp;
        private readonly int[] _predEdge;
        private readonly bool[] _settled;
        private readonly int[] _settledStamp;
        private readonly int[] _chainBuf;
        private int _stamp;
        private FloatHeap _heap;

        public long LastSettledCount; // telemetry: corridor size

        public PotentialAStar(CchMetrics m, CchQuery q)
        {
            _c = m.C; _m = m; _q = q;
            int n = _c.NodeCount;
            _pot = new float[n * MaxLanes];
            _potStamp = new int[n];
            _bLab = new float[n * MaxLanes];
            _bStamp = new int[n];
            _g = new float[n];
            _gStamp = new int[n];
            _predEdge = new int[n];
            _settled = new bool[n];
            _settledStamp = new int[n];
            _chainBuf = new int[n];
            _heap = new FloatHeap(4096);
        }

        /// <summary>Exact shortest path s->t under edgeWeight, guided by the
        /// lambda-combined anchor potential. Returns distance (or +inf) and
        /// fills edgePathOut in travel order. maxSettled bounds the search:
        /// past it the result is +inf (best-effort semantics — callers keep
        /// their portfolio incumbent and its gap bound).</summary>
        public float Search(QueryContext qctx, int s, int t,
                            (int metric, float lambda)[] lambda,
                            Func<int, float> edgeWeight,
                            List<int> edgePathOut,
                            int maxSettled = int.MaxValue)
        {
            edgePathOut.Clear();
            int L = lambda.Length;
            if (L > MaxLanes) throw new ArgumentException($"at most {MaxLanes} potential lanes");
            _stamp++;
            int stamp = _stamp;

            // Backward upward labels from t for every potential lane.
            for (int j = 0; j < L; j++)
            {
                int jj = j;
                _q.BackwardUpwardLabels(qctx, t, lambda[j].metric, (node, dist) =>
                {
                    if (_bStamp[node] != stamp)
                    {
                        _bStamp[node] = stamp;
                        for (int x = 0; x < L; x++) _bLab[node * MaxLanes + x] = float.PositiveInfinity;
                    }
                    _bLab[node * MaxLanes + jj] = dist;
                });
            }

            _heap.Clear();
            LastSettledCount = 0;
            _g[s] = 0f; _gStamp[s] = stamp; _predEdge[s] = -1; _settled[s] = false; _settledStamp[s] = stamp;
            float hs = Potential(s, lambda, stamp);
            if (float.IsPositiveInfinity(hs)) return float.PositiveInfinity; // t unreachable
            _heap.Push(hs, s);

            var g = _c.G;
            while (_heap.Count > 0)
            {
                int v = _heap.PopPayload();
                if (_settledStamp[v] == stamp && _settled[v]) continue;
                _settled[v] = true; _settledStamp[v] = stamp;
                LastSettledCount++;
                if (v == t) break;
                if (LastSettledCount > maxSettled) return float.PositiveInfinity;
                float gv = _g[v];
                for (int e = g.OutStart[v]; e < g.OutStart[v + 1]; e++)
                {
                    int w = g.Head[e];
                    float we = edgeWeight(e);
                    if (float.IsPositiveInfinity(we)) continue;
                    float cand = gv + we;
                    if (_gStamp[w] != stamp || cand < _g[w])
                    {
                        _g[w] = cand; _gStamp[w] = stamp; _predEdge[w] = e;
                        if (_settledStamp[w] == stamp && _settled[w]) continue; // consistent h: never better
                        float hw = Potential(w, lambda, stamp);
                        if (float.IsPositiveInfinity(hw)) continue;
                        if (_settledStamp[w] != stamp) { _settled[w] = false; _settledStamp[w] = stamp; }
                        _heap.Push(cand + hw, w);
                    }
                }
            }

            if (_gStamp[t] != stamp || !(_settledStamp[t] == stamp && _settled[t]))
                return float.PositiveInfinity;

            int cur = t;
            while (cur != s)
            {
                int e = _predEdge[cur];
                edgePathOut.Add(e);
                cur = g.Tail[e];
            }
            edgePathOut.Reverse();
            return _g[t];
        }

        /// <summary>Lazily evaluated lambda-combined potential. pot_j(v) =
        /// min(bLabel_j(v), min over up-arcs (v,u) of w_j(v,u) + pot_j(u)),
        /// computed top-down along v's ancestor chain, memoized per query.</summary>
        private float Potential(int v, (int metric, float lambda)[] lambda, int stamp)
        {
            int L = lambda.Length;
            if (_potStamp[v] != stamp)
            {
                int len = 0, u = v;
                while (u >= 0 && _potStamp[u] != stamp) { _chainBuf[len++] = u; u = _c.EtParent[u]; }
                for (int i = len - 1; i >= 0; i--)
                {
                    int x = _chainBuf[i];
                    int bx = x * MaxLanes;
                    bool hasB = _bStamp[x] == stamp;
                    for (int j = 0; j < L; j++)
                        _pot[bx + j] = hasB ? _bLab[bx + j] : float.PositiveInfinity;
                    int K = _m.K;
                    for (int a = _c.UpStart[x]; a < _c.UpStart[x + 1]; a++)
                    {
                        int h = _c.UpHead[a];
                        int bh = h * MaxLanes;
                        for (int j = 0; j < L; j++)
                        {
                            float cand = _m.WFwd[a * K + lambda[j].metric] + _pot[bh + j];
                            if (cand < _pot[bx + j]) _pot[bx + j] = cand;
                        }
                    }
                    _potStamp[x] = stamp;
                }
            }
            float hsum = 0f;
            int bv = v * MaxLanes;
            for (int j = 0; j < L; j++)
            {
                float p = _pot[bv + j];
                if (float.IsPositiveInfinity(p)) { if (lambda[j].lambda > 0) return float.PositiveInfinity; continue; }
                hsum += lambda[j].lambda * p;
            }
            return hsum;
        }

        internal struct FloatHeap
        {
            private float[] _keys;
            private int[] _payloads;
            public int Count;
            public FloatHeap(int cap) { _keys = new float[cap]; _payloads = new int[cap]; Count = 0; }
            public void Clear() => Count = 0;
            public void Push(float key, int payload)
            {
                if (Count == _keys.Length)
                {
                    Array.Resize(ref _keys, Count * 2);
                    Array.Resize(ref _payloads, Count * 2);
                }
                int i = Count++;
                _keys[i] = key; _payloads[i] = payload;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (_keys[p] <= _keys[i]) break;
                    (_keys[p], _keys[i]) = (_keys[i], _keys[p]);
                    (_payloads[p], _payloads[i]) = (_payloads[i], _payloads[p]);
                    i = p;
                }
            }
            public int PopPayload()
            {
                int res = _payloads[0];
                Count--;
                _keys[0] = _keys[Count]; _payloads[0] = _payloads[Count];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, sm = i;
                    if (l < Count && _keys[l] < _keys[sm]) sm = l;
                    if (r < Count && _keys[r] < _keys[sm]) sm = r;
                    if (sm == i) break;
                    (_keys[sm], _keys[i]) = (_keys[i], _keys[sm]);
                    (_payloads[sm], _payloads[i]) = (_payloads[i], _payloads[sm]);
                    i = sm;
                }
                return res;
            }
        }
    }
}
