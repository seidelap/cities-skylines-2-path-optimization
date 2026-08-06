using System.Threading;

namespace CS2Path.Core
{
    /// <summary>
    /// The hot inner loops, written so that ONE implementation compiles under
    /// both toolchains: Roslyn for the out-of-game harness, and Unity's Burst
    /// when the mod ships inside the game.
    ///
    /// WHY POINTERS. Burst rejects every managed type — no classes, no <c>T[]</c>,
    /// no <c>List</c>/<c>Dictionary</c>, no strings, no delegates, no exceptions
    /// carrying messages, no boxing. In-game the buffers are
    /// <c>NativeArray&lt;T&gt;</c>; out-of-game they are ordinary C# arrays. The
    /// ONLY representation common to both is a raw pointer plus an explicit
    /// length, so that is the calling convention. The harness pins arrays with
    /// <c>fixed</c>; a Burst job passes <c>NativeArray.GetUnsafePtr()</c>. Neither
    /// side needs the other's container type, which is what lets CS2Path.Core keep
    /// its hard rule of ZERO game-assembly references (plan §5).
    ///
    /// RULES FOR THIS FILE (enforced by a linter in the verify suite — see
    /// TestRunner.VerifyBurstKernelPortability):
    ///   * static methods only, no instance state, no mutable statics
    ///   * parameters and locals are primitives, pointers, or unmanaged structs
    ///   * no allocation of any kind on any path
    ///   * no System.Numerics (that is .NET SIMD; Burst uses its own vectorizer)
    ///   * no try/catch, no throw, no string formatting
    ///
    /// VECTORIZATION. The previous implementation used
    /// <c>System.Numerics.Vector&lt;float&gt;</c>, which Burst cannot see. These
    /// loops are instead written as a 4-wide unrolled scalar form over the
    /// lane-contiguous (arc-major) layout. Both RyuJIT and Burst auto-vectorize
    /// that shape, and under Burst it maps onto the same <c>float4</c> registers
    /// Unity.Mathematics would have produced — without Core naming a Unity type.
    /// </summary>
    public static unsafe class BurstKernels
    {
        /// <summary>
        /// Relax one triangle (x, A, B) into the target arc t = (A, B).
        /// Forward: A → x → B improves WFwd[t]. Backward: B → x → A improves WBwd[t].
        /// b1/b2/bt are lane-base offsets (arc * K) for arcs (x,A), (x,B) and t.
        ///
        /// Sequential form: safe only when no other thread writes lane range
        /// [bt, bt+K). Used by the single-threaded sweep and by any parallel
        /// scheme that partitions by target arc.
        /// </summary>
        public static void RelaxTriangle(float* wFwd, float* wBwd, int b1, int b2, int bt, int K)
        {
            int k = 0;
            // 4-wide body: independent lanes, no cross-lane carries, so both
            // compilers turn this into packed min/add.
            for (; k <= K - 4; k += 4)
            {
                float f0 = wBwd[b1 + k + 0] + wFwd[b2 + k + 0];
                float f1 = wBwd[b1 + k + 1] + wFwd[b2 + k + 1];
                float f2 = wBwd[b1 + k + 2] + wFwd[b2 + k + 2];
                float f3 = wBwd[b1 + k + 3] + wFwd[b2 + k + 3];
                if (f0 < wFwd[bt + k + 0]) wFwd[bt + k + 0] = f0;
                if (f1 < wFwd[bt + k + 1]) wFwd[bt + k + 1] = f1;
                if (f2 < wFwd[bt + k + 2]) wFwd[bt + k + 2] = f2;
                if (f3 < wFwd[bt + k + 3]) wFwd[bt + k + 3] = f3;

                float g0 = wBwd[b2 + k + 0] + wFwd[b1 + k + 0];
                float g1 = wBwd[b2 + k + 1] + wFwd[b1 + k + 1];
                float g2 = wBwd[b2 + k + 2] + wFwd[b1 + k + 2];
                float g3 = wBwd[b2 + k + 3] + wFwd[b1 + k + 3];
                if (g0 < wBwd[bt + k + 0]) wBwd[bt + k + 0] = g0;
                if (g1 < wBwd[bt + k + 1]) wBwd[bt + k + 1] = g1;
                if (g2 < wBwd[bt + k + 2]) wBwd[bt + k + 2] = g2;
                if (g3 < wBwd[bt + k + 3]) wBwd[bt + k + 3] = g3;
            }
            for (; k < K; k++)
            {
                float f = wBwd[b1 + k] + wFwd[b2 + k];
                if (f < wFwd[bt + k]) wFwd[bt + k] = f;
                float b = wBwd[b2 + k] + wFwd[b1 + k];
                if (b < wBwd[bt + k]) wBwd[bt + k] = b;
            }
        }

        /// <summary>
        /// Atomic variant, for level-parallel customization. Two nodes eliminated
        /// in the same elimination-tree level can target the SAME arc, so the
        /// read-modify-write must not race.
        ///
        /// Still bit-identical to the sequential sweep: min is commutative and
        /// associative and introduces no rounding, and every addend is read from
        /// an arc already finalized at a strictly lower level. So the final value
        /// is the min over the same multiset of candidates regardless of the
        /// order threads happen to apply them.
        /// </summary>
        public static void RelaxTriangleAtomic(float* wFwd, float* wBwd, int b1, int b2, int bt, int K)
        {
            for (int k = 0; k < K; k++)
            {
                // Plain read-and-compare BEFORE attempting any CAS. Most
                // relaxations do not improve the target, and an unconditional
                // CAS on every lane measured slower than the whole sequential
                // sweep. A stale read here is safe: it can only be too
                // pessimistic (another thread lowered the slot meanwhile), and
                // AtomicMin re-validates under the CAS anyway.
                float f = wBwd[b1 + k] + wFwd[b2 + k];
                if (f < wFwd[bt + k]) AtomicMin(wFwd + bt + k, f);
                float b = wBwd[b2 + k] + wFwd[b1 + k];
                if (b < wBwd[bt + k]) AtomicMin(wBwd + bt + k, b);
            }
        }

        /// <summary>
        /// Lock-free float min via integer CAS. Valid because every weight here
        /// is non-negative or +inf, and for non-negative IEEE-754 floats the bit
        /// patterns order identically to their signed-integer reinterpretation.
        /// NaN never reaches this path — AnchorGrid short-circuits non-finite
        /// inputs to +inf before customization sees them.
        /// </summary>
        public static void AtomicMin(float* slot, float candidate)
        {
            int* asInt = (int*)slot;
            int candBits = *(int*)&candidate;
            while (true)
            {
                int oldBits = *asInt;
                float oldVal = *(float*)&oldBits;
                if (!(candidate < oldVal)) return;              // already better (or NaN-safe no-op)
                if (Interlocked.CompareExchange(ref *asInt, candBits, oldBits) == oldBits) return;
            }
        }

        /// <summary>
        /// Eliminate one node: relax every triangle (x, A, B) formed by pairs of
        /// x's up-neighbours. Merge-scans Up[x] against Up[A] by head id, which is
        /// why both lists must stay sorted by head.
        ///
        /// This is the body of the full-customization outer loop, lifted so that
        /// the sequential sweep, the parallel level sweep, and an in-game
        /// IJobParallelFor can all share exactly one copy of the logic.
        /// </summary>
        public static void ContractNode(
            float* wFwd, float* wBwd, int* upStart, int* upHead, int x, int K, byte atomic)
        {
            int s = upStart[x], e = upStart[x + 1];
            for (int i = s; i < e; i++)
            {
                int a = upHead[i];
                int p = s, q = upStart[a], qe = upStart[a + 1];
                while (p < e && q < qe)
                {
                    int hb = upHead[p], hb2 = upHead[q];
                    if (hb < hb2) { p++; }
                    else if (hb > hb2) { q++; }
                    else
                    {
                        if (atomic != 0) RelaxTriangleAtomic(wFwd, wBwd, i * K, p * K, q * K, K);
                        else RelaxTriangle(wFwd, wBwd, i * K, p * K, q * K, K);
                        p++; q++;
                    }
                }
            }
        }

        /// <summary>
        /// Sequential full customization over a rank range — the whole Layer-1
        /// sweep with zero managed types touched.
        /// </summary>
        public static void FullCustomizeRange(
            float* wFwd, float* wBwd, int* upStart, int* upHead, int* nodeAtRank,
            int rankFrom, int rankTo, int K)
        {
            for (int r = rankFrom; r < rankTo; r++)
                ContractNode(wFwd, wBwd, upStart, upHead, nodeAtRank[r], K, 0);
        }

        /// <summary>
        /// One level of the parallel sweep: eliminate the nodes listed in
        /// levelNodes[from..to). Callers must complete a level before starting the
        /// next; within a level, order is irrelevant (see RelaxTriangleAtomic).
        /// In-game this is the Execute body of an IJobParallelFor.
        ///
        /// atomic MUST be 1 whenever another thread is working the same level
        /// concurrently, and SHOULD be 0 when this range is the whole level and
        /// is being run by one thread. Road-network elimination trees are deep
        /// and narrow — 331 levels averaging ~400 nodes at 131k — so most levels
        /// are executed serially, and forcing the atomic path on them paid CAS
        /// cost for parallelism that was never happening. That alone made the
        /// first parallel implementation SLOWER than sequential (0.8x).
        /// </summary>
        public static void ContractLevelRange(
            float* wFwd, float* wBwd, int* upStart, int* upHead, int* levelNodes,
            int from, int to, int K, byte atomic)
        {
            for (int i = from; i < to; i++)
                ContractNode(wFwd, wBwd, upStart, upHead, levelNodes[i], K, atomic);
        }

        /// <summary>
        /// Min-plus fold of one arc's lower triangles into scratch lanes — the
        /// inner loop of partial customization's RecomputeArc.
        /// </summary>
        public static void FoldLowerTriangle(
            float* wFwd, float* wBwd, float* scratchF, float* scratchB,
            int bv, int bw, int kCount)
        {
            for (int k = 0; k < kCount; k++)
            {
                float f = wBwd[bv + k] + wFwd[bw + k];   // v -> x -> w
                if (f < scratchF[k]) scratchF[k] = f;
                float b = wBwd[bw + k] + wFwd[bv + k];   // w -> x -> v
                if (b < scratchB[k]) scratchB[k] = b;
            }
        }

        /// <summary>
        /// Commit recomputed lanes back to an arc; returns 1 if any lane moved.
        /// Exact inequality on purpose: partial customization's propagation
        /// termination depends on "changed" meaning bitwise-different, not
        /// approximately-different.
        /// </summary>
        public static int CommitArc(
            float* wFwd, float* wBwd, float* scratchF, float* scratchB, int ba, int kCount)
        {
            int changed = 0;
            for (int k = 0; k < kCount; k++)
            {
                if (wFwd[ba + k] != scratchF[k]) { wFwd[ba + k] = scratchF[k]; changed = 1; }
                if (wBwd[ba + k] != scratchB[k]) { wBwd[ba + k] = scratchB[k]; changed = 1; }
            }
            return changed;
        }

        /// <summary>
        /// Relax one upward chain step for a single lane — the innermost work of
        /// an elimination-tree query. Returns the improved label.
        /// </summary>
        public static float RelaxUpward(
            float* w, int* upStart, int* upHead, float* dist, int* stamp, int* pred,
            int v, int stampNow, float dv, int K, int k)
        {
            for (int i = upStart[v]; i < upStart[v + 1]; i++)
            {
                int h = upHead[i];
                float nd = dv + w[i * K + k];
                if (stamp[h] != stampNow || nd < dist[h])
                {
                    stamp[h] = stampNow;
                    dist[h] = nd;
                    pred[h] = v;
                }
            }
            return dv;
        }
    }
}
