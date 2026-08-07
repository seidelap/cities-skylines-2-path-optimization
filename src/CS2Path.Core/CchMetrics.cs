using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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
    public sealed unsafe class CchMetrics
    {
        public CchSkeleton C = null!;
        public AnchorGrid Anchors = null!;
        public int K;                       // number of metrics (lanes)

        // Arc-major lane layout: weight of arc a under metric k at [a*K + k].
        public float[] WFwd = null!;        // tail -> head
        public float[] WBwd = null!;        // head -> tail

        public long LastPartialArcsRecomputed; // telemetry

        /// <summary>Levels narrower than this run serially (dispatch would cost
        /// more than the split saves). Public so the bit-identity test can assert
        /// the parallel path was actually EXERCISED — if every level of the test
        /// graph fell below this, the test would be silently vacuous.</summary>
        public const int MinLevelNodesToSplit = 96;

        /// <summary>Levels that actually ran multi-threaded in the last
        /// FullCustomizeParallel call. Zero means the run was serial throughout.</summary>
        public int LastParallelLevelsSplit;

        /// <summary>Tasks executed by the last FullCustomizeParallelTasks call
        /// (0 = fell back to sequential because no frontier was built).</summary>
        public int LastTasksRun;

        /// <summary>Monotonic epoch, bumped per (partial) customization. Together
        /// with NodeArcChangeEpoch this is the "dirty flag" consumers use to
        /// event-drive their own refreshes (Layer 3 buckets): a backward search
        /// space rooted at d depends exactly on the up-arcs whose TAILS lie on
        /// d's ancestor chain, so "max NodeArcChangeEpoch over my chain &gt; my
        /// last refresh epoch" is an exact staleness test.</summary>
        public int ChangeEpoch;
        public int[] NodeArcChangeEpoch = null!;

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
            m.NodeArcChangeEpoch = new int[c.NodeCount];
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
            ChangeEpoch++;
            Array.Fill(NodeArcChangeEpoch, ChangeEpoch); // everything (re)computed
            fixed (float* wf = WFwd, wb = WBwd)
            fixed (int* upStart = c.UpStart, upHead = c.UpHead, nodeAtRank = c.NodeAtRank)
                BurstKernels.FullCustomizeRange(wf, wb, upStart, upHead, nodeAtRank, 0, c.NodeCount, K);
        }

        /// <summary>
        /// Level-parallel full customization (plan §6: the sweep is
        /// level-parallelizable). Nodes within one elimination-tree level have no
        /// dependencies on each other, so each level is dispatched across threads
        /// and levels are separated by a barrier.
        ///
        /// Bit-identical to <see cref="FullCustomize"/> — proven by
        /// TestRunner.VerifyParallelCustomization, not merely asserted. Writes use
        /// an atomic min, which is exact and order-independent, so no float
        /// reassociation can occur.
        ///
        /// This is the exact shape the in-game Burst port takes: one
        /// IJobParallelFor per level over the same kernel
        /// (<see cref="BurstKernels.ContractLevelRange"/>), scheduled with a
        /// dependency chain between levels.
        /// </summary>
        public void FullCustomizeParallel(int threads = 0)
        {
            var c = C;
            ChangeEpoch++;
            Array.Fill(NodeArcChangeEpoch, ChangeEpoch);
            if (threads <= 0) threads = Environment.ProcessorCount;
            LastParallelLevelsSplit = 0;
            // Below this, thread dispatch costs more than splitting the level
            // saves. Measured: at 131k nodes the tree has 331 levels averaging
            // ~400 nodes, so a threshold of 512 sent nearly every level down the
            // serial path — and the serial path was still paying atomic-CAS cost,
            // which is why the first cut of this ran at 0.8x sequential. Levels
            // below the threshold now run the plain non-atomic kernel.
            const int MinNodesToSplit = MinLevelNodesToSplit;

            fixed (float* wf = WFwd, wb = WBwd)
            fixed (int* upStart = c.UpStart, upHead = c.UpHead, levelNodes = c.LevelNodes)
            {
                float* wfp = wf; float* wbp = wb;
                int* usp = upStart; int* uhp = upHead; int* lnp = levelNodes;
                for (int lvl = 0; lvl < c.LevelCount; lvl++)
                {
                    int from = c.LevelStart[lvl], to = c.LevelStart[lvl + 1];
                    int count = to - from;
                    if (count <= 0) continue;
                    if (count < MinNodesToSplit || threads == 1)
                    {
                        // Single-threaded over the whole level => no concurrent
                        // writer => the fast non-atomic, vectorizable kernel.
                        BurstKernels.ContractLevelRange(wfp, wbp, usp, uhp, lnp, from, to, K, 0);
                        continue;
                    }
                    // Contiguous chunks, NOT round-robin. Nodes within a level are
                    // stored in ascending rank, and rank order correlates with
                    // arc-block order, so contiguous chunks keep each thread on
                    // its own stretch of the weight arrays. With K=16 an arc's
                    // lanes are exactly one 64-byte cache line, so interleaving
                    // threads across neighbouring arcs would make every write a
                    // false-sharing event — the suspected cause of the 2-thread
                    // regression measured before this was tuned.
                    LastParallelLevelsSplit++;
                    int chunk = (count + threads - 1) / threads;
                    Parallel.For(0, threads, ti =>
                    {
                        int lo = from + ti * chunk;
                        int hi = Math.Min(to, lo + chunk);
                        if (lo < hi)
                            BurstKernels.ContractLevelRange(wfp, wbp, usp, uhp, lnp, lo, hi, K, 1);
                    });
                }
            }
        }

        /// <summary>
        /// Task-parallel full customization over dissection-cell subtrees — the
        /// replacement for the level scheme after measurement showed the level
        /// approach nets ~1.08× (a 38% level-order locality tax plus a ~2×
        /// atomic kernel on ~95% of triangles).
        ///
        /// Each task is a contiguous, downward-closed rank range, contracted in
        /// RANK ORDER with the plain vectorizable kernel; only writes into
        /// enclosing separators (target tail outside the task) pay an atomic
        /// min. Phase 2 contracts the remaining separator ranks sequentially,
        /// ascending — by then every task input below them is final.
        ///
        /// Bit-identical to <see cref="FullCustomize"/>: the candidate multiset
        /// per arc is unchanged, min is exact and order-independent, and the
        /// verify suite asserts identity plus the structural invariants
        /// (partition, downward closure, cross-task contention counted
        /// deterministically).
        /// </summary>
        public void FullCustomizeParallelTasks(int threads = 0)
        {
            var c = C;
            if (c.TaskLo == null || c.TaskHi == null || c.Phase2Ranks == null || c.TaskLo.Length < 2)
            {
                LastTasksRun = 0;
                FullCustomize();
                return;
            }
            ChangeEpoch++;
            Array.Fill(NodeArcChangeEpoch, ChangeEpoch);
            if (threads <= 0) threads = Environment.ProcessorCount;
            int nTasks = c.TaskLo.Length;
            LastTasksRun = nTasks;

            // Largest tasks first: with ~4 tasks per core, finishing a monster
            // last is the main load-imbalance risk.
            var order = new int[nTasks];
            for (int i = 0; i < nTasks; i++) order[i] = i;
            var sizes = new int[nTasks];
            for (int i = 0; i < nTasks; i++) sizes[i] = c.TaskHi[i] - c.TaskLo[i];
            Array.Sort(sizes, order);
            Array.Reverse(order);

            fixed (float* wf = WFwd, wb = WBwd)
            fixed (int* upStart = c.UpStart, upHead = c.UpHead, nodeAtRank = c.NodeAtRank, rank = c.Rank)
            {
                float* wfp = wf; float* wbp = wb;
                int* usp = upStart; int* uhp = upHead; int* nrp = nodeAtRank; int* rkp = rank;
                int next = -1;
                var lo = c.TaskLo; var hi = c.TaskHi;
                var ordLocal = order;
                Parallel.For(0, Math.Min(threads, nTasks), _ =>
                {
                    while (true)
                    {
                        int slot = Interlocked.Increment(ref next);
                        if (slot >= nTasks) return;
                        int t = ordLocal[slot];
                        BurstKernels.CustomizeTaskRange(wfp, wbp, usp, uhp, nrp, rkp, lo[t], hi[t], K);
                    }
                });
                // Phase 2: enclosing separators, ascending rank, single thread.
                var p2 = c.Phase2Ranks;
                for (int i = 0; i < p2.Length; i++)
                    BurstKernels.ContractNode(wfp, wbp, usp, uhp, nrp[p2[i]], K, 0);
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
            ChangeEpoch++;
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
                    NodeArcChangeEpoch[c.UpTail[a]] = ChangeEpoch;
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
            fixed (float* wf = WFwd, wb = WBwd, sf = _scratchF, sb = _scratchB)
            {
                while (p < pe && q < qe)
                {
                    int tv = c.DownTail[p], tw = c.DownTail[q];
                    if (tv < tw) p++;
                    else if (tv > tw) q++;
                    else
                    {
                        int axv = c.DownArc[p], axw = c.DownArc[q]; // (x,v), (x,w)
                        BurstKernels.FoldLowerTriangle(
                            wf, wb, sf, sb, axv * K + kFrom, axw * K + kFrom, kCount);
                        p++; q++;
                    }
                }
                return BurstKernels.CommitArc(wf, wb, sf, sb, a * K + kFrom, kCount) != 0;
            }
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
