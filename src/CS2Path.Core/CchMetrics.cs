using System;
using System.Collections.Generic;
using System.Numerics;

namespace CS2Path.Core
{
    /// <summary>
    /// Layer 1 (part 2): metric customization — the numeric factorization step
    /// (plan §4 Layer 1). Weights for all K anchor metrics ride together
    /// through one bottom-up min-plus triangle sweep over the fixed shortcut
    /// structure (no searches, no structural decisions), laid out arc-major so
    /// all lanes vectorize. Partial customization re-relaxes only arcs whose
    /// triangles contain changed edges and propagates up the elimination tree
    /// only while a min actually changes.
    /// </summary>
    public sealed class CchMetrics
    {
        public CchSkeleton C = null!;
        public AnchorGrid Anchors = null!;
        public int K;                       // number of metrics (lanes)

        // Arc-major lane layout: weight of arc a under metric k at [a*K + k].
        public float[] WFwd = null!;        // tail -> head
        public float[] WBwd = null!;        // head -> tail

        public long LastPartialArcsRecomputed; // telemetry

        private bool[] _inQueue = null!;
        private LongHeap _heap;
        private float[] _scratchF = null!, _scratchB = null!;

        public static CchMetrics Create(CchSkeleton c, AnchorGrid anchors)
        {
            var m = new CchMetrics
            {
                C = c,
                Anchors = anchors,
                K = anchors.MetricCount,
            };
            m.WFwd = new float[(long)c.ArcCount * m.K <= int.MaxValue ? c.ArcCount * m.K : throw new OutOfMemoryException("arc*K too large")];
            m.WBwd = new float[c.ArcCount * m.K];
            m._inQueue = new bool[c.ArcCount];
            m._heap = new LongHeap(1024);
            m._scratchF = new float[m.K];
            m._scratchB = new float[m.K];
            return m;
        }

        /// <summary>Original-edge weight of an arc direction under metric k:
        /// min over the primary edge and any parallel edges (+inf for pure shortcuts).</summary>
        public float OriginalArcWeight(int a, int k, bool fwd)
        {
            int e = fwd ? C.OrigFwd[a] : C.OrigBwd[a];
            if (e < 0) return float.PositiveInfinity;
            float w = Anchors.EdgeWeight(C.G, e, k);
            var map = fwd ? C.ExtraFwd : C.ExtraBwd;
            if (map.Count > 0 && map.TryGetValue(a, out var extras))
                foreach (var e2 in extras)
                {
                    float w2 = Anchors.EdgeWeight(C.G, e2, k);
                    if (w2 < w) w = w2;
                }
            return w;
        }

        /// <summary>Load original-edge weights (or +inf for pure shortcuts) for
        /// lanes [kFrom, kFrom+kCount).</summary>
        public void Reset(int kFrom, int kCount)
        {
            var c = C;
            for (int a = 0; a < c.ArcCount; a++)
            {
                int baseA = a * K;
                for (int k = kFrom; k < kFrom + kCount; k++)
                {
                    WFwd[baseA + k] = OriginalArcWeight(a, k, true);
                    WBwd[baseA + k] = OriginalArcWeight(a, k, false);
                }
            }
        }

        public void ResetAll() => Reset(0, K);

        /// <summary>Full customization: bottom-up triangle sweep in elimination
        /// order. After this, arc weights are exact shortest-distance weights
        /// respecting the hierarchy, for every lane.</summary>
        public void FullCustomize()
        {
            var c = C;
            int n = c.NodeCount;
            for (int r = 0; r < n; r++)
            {
                int x = c.NodeAtRank[r];
                int s = c.UpStart[x], e = c.UpStart[x + 1];
                for (int i = s; i < e; i++)
                {
                    int a1 = i;                 // arc (x, A)
                    int A = c.UpHead[i];
                    // Merge-scan Up[x] and Up[A] by head id: common head B gives
                    // triangle (x, A, B) with target arc t = (A, B) at its
                    // position inside Up[A].
                    int p = s, q = c.UpStart[A], qe = c.UpStart[A + 1];
                    while (p < e && q < qe)
                    {
                        int hb = c.UpHead[p], hb2 = c.UpHead[q];
                        if (hb < hb2) p++;
                        else if (hb > hb2) q++;
                        else
                        {
                            RelaxTriangle(a1, p, q); // a2 = (x,B) at p, t = (A,B) at q
                            p++; q++;
                        }
                    }
                }
            }
        }

        /// <summary>fwd: A -> x -> B relaxes WFwd[t]; bwd: B -> x -> A relaxes WBwd[t].</summary>
        private void RelaxTriangle(int a1, int a2, int t)
        {
            int b1 = a1 * K, b2 = a2 * K, bt = t * K;
            int k = 0;
            int vc = Vector<float>.Count;
            if (Vector.IsHardwareAccelerated && K >= vc)
            {
                for (; k <= K - vc; k += vc)
                {
                    var w1b = new Vector<float>(WBwd, b1 + k);
                    var w1f = new Vector<float>(WFwd, b1 + k);
                    var w2b = new Vector<float>(WBwd, b2 + k);
                    var w2f = new Vector<float>(WFwd, b2 + k);
                    var tf = new Vector<float>(WFwd, bt + k);
                    var tb = new Vector<float>(WBwd, bt + k);
                    Vector.Min(tf, w1b + w2f).CopyTo(WFwd, bt + k);
                    Vector.Min(tb, w2b + w1f).CopyTo(WBwd, bt + k);
                }
            }
            for (; k < K; k++)
            {
                float f = WBwd[b1 + k] + WFwd[b2 + k];
                if (f < WFwd[bt + k]) WFwd[bt + k] = f;
                float b = WBwd[b2 + k] + WFwd[b1 + k];
                if (b < WBwd[bt + k]) WBwd[bt + k] = b;
            }
        }

        /// <summary>
        /// Partial customization for lanes [kFrom, kFrom+kCount): reseed the
        /// arcs of changed original edges, recompute each from its lower
        /// triangles, and propagate to dependent arcs only while values change.
        /// Sub-millisecond for typical congestion deltas.
        /// </summary>
        public void PartialCustomize(IReadOnlyList<int> changedEdges, int kFrom, int kCount)
        {
            var c = C;
            LastPartialArcsRecomputed = 0;
            for (int i = 0; i < changedEdges.Count; i++)
            {
                int arc = c.ArcOfEdge(changedEdges[i], out _);
                if (arc >= 0) Enqueue(arc);
            }
            while (_heap.Count > 0)
            {
                int a = _heap.PopPayload();
                _inQueue[a] = false;
                LastPartialArcsRecomputed++;
                if (RecomputeArc(a, kFrom, kCount))
                {
                    // Affected targets: for A in Up[v] (v = tail(a)), the arc
                    // between A and head(a), if present, has `a` as a triangle side.
                    int v = c.UpTail[a], w = c.UpHead[a];
                    for (int i = c.UpStart[v]; i < c.UpStart[v + 1]; i++)
                    {
                        int A = c.UpHead[i];
                        if (A == w) continue;
                        int t = c.Rank[A] < c.Rank[w] ? c.FindArc(A, w) : c.FindArc(w, A);
                        if (t >= 0) Enqueue(t);
                    }
                }
            }
        }

        private void Enqueue(int arc)
        {
            if (_inQueue[arc]) return;
            _inQueue[arc] = true;
            long key = ((long)C.Rank[C.UpTail[arc]] << 32) | (uint)C.Rank[C.UpHead[arc]];
            _heap.Push(key, arc);
        }

        /// <summary>Recompute an arc's lanes from scratch: original edge weight
        /// (re-read from the graph, so weight changes land here) min-ed over all
        /// lower triangles. Returns true if any lane changed.</summary>
        private bool RecomputeArc(int a, int kFrom, int kCount)
        {
            var c = C;
            int v = c.UpTail[a], w = c.UpHead[a];
            for (int k = 0; k < kCount; k++)
            {
                _scratchF[k] = OriginalArcWeight(a, kFrom + k, true);
                _scratchB[k] = OriginalArcWeight(a, kFrom + k, false);
            }
            // Lower triangles: common down-neighbors x of v and w (merge by tail id).
            int p = c.DownStart[v], pe = c.DownStart[v + 1];
            int q = c.DownStart[w], qe = c.DownStart[w + 1];
            while (p < pe && q < qe)
            {
                int tv = c.DownTail[p], tw = c.DownTail[q];
                if (tv < tw) p++;
                else if (tv > tw) q++;
                else
                {
                    int axv = c.DownArc[p], axw = c.DownArc[q]; // (x,v), (x,w)
                    int bv = axv * K + kFrom, bw = axw * K + kFrom;
                    for (int k = 0; k < kCount; k++)
                    {
                        float f = WBwd[bv + k] + WFwd[bw + k]; // v -> x -> w
                        if (f < _scratchF[k]) _scratchF[k] = f;
                        float b = WBwd[bw + k] + WFwd[bv + k]; // w -> x -> v
                        if (b < _scratchB[k]) _scratchB[k] = b;
                    }
                    p++; q++;
                }
            }
            bool changed = false;
            int ba = a * K + kFrom;
            for (int k = 0; k < kCount; k++)
            {
                if (WFwd[ba + k] != _scratchF[k]) { WFwd[ba + k] = _scratchF[k]; changed = true; }
                if (WBwd[ba + k] != _scratchB[k]) { WBwd[ba + k] = _scratchB[k]; changed = true; }
            }
            return changed;
        }

        /// <summary>Binary min-heap keyed by (rank(tail), rank(head)) — ascending
        /// dependency order over the elimination tree.</summary>
        private struct LongHeap
        {
            private long[] _keys;
            private int[] _payloads;
            public int Count;
            public LongHeap(int cap) { _keys = new long[cap]; _payloads = new int[cap]; Count = 0; }
            public void Push(long key, int payload)
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
                    int l = 2 * i + 1, r = l + 1, s = i;
                    if (l < Count && _keys[l] < _keys[s]) s = l;
                    if (r < Count && _keys[r] < _keys[s]) s = r;
                    if (s == i) break;
                    (_keys[s], _keys[i]) = (_keys[i], _keys[s]);
                    (_payloads[s], _payloads[i]) = (_payloads[i], _payloads[s]);
                    i = s;
                }
                return res;
            }
        }
    }
}
