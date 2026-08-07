using CS2Path.Core;
#if !OUT_OF_GAME_BUILD
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
#endif

namespace CS2Path.Mod
{
    /// <summary>
    /// In-game Burst wrappers for the Layer-1 customization sweep.
    ///
    /// These structs contain NO algorithm. Every line of arithmetic lives in
    /// <see cref="BurstKernels"/> in CS2Path.Core, which the harness exercises
    /// and the verify suite proves bit-identical between its sequential and
    /// level-parallel forms. That is deliberate: the code the game runs and the
    /// code the correctness oracle checks must be the same code, or the oracle
    /// is measuring a different program than the one that ships.
    ///
    /// The decomposition is elimination-tree levels. Nodes within one level have
    /// no data dependencies on each other (every input arc was finalized at a
    /// strictly lower level), so a level is one IJobParallelFor and levels are
    /// chained by JobHandle dependency. Two nodes in the same level can target
    /// the same arc, which is why the kernel writes through an atomic min.
    /// </summary>
    public static class BurstCustomizationJobs
    {
#if !OUT_OF_GAME_BUILD
        /// <summary>
        /// Eliminate the nodes of one elimination-tree level.
        ///
        /// FloatMode.Strict is REQUIRED and must not be "optimized" to Fast.
        /// Burst's fast-math permits reassociation and fused multiply-add
        /// contraction, which would change results in the last ulp. Every
        /// exactness claim in this repo — the certificate lower bounds, the
        /// 200/200 Dijkstra agreement, and the bit-identical sequential-vs-
        /// parallel test — depends on the arithmetic being exactly IEEE-754 as
        /// written. Strict costs a little throughput and buys the entire
        /// verification story.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High,
                      CompileSynchronously = true)]
        public unsafe struct ContractLevelJob : IJobParallelFor
        {
            [NativeDisableUnsafePtrRestriction] public float* WFwd;
            [NativeDisableUnsafePtrRestriction] public float* WBwd;
            [NativeDisableUnsafePtrRestriction] public int* UpStart;
            [NativeDisableUnsafePtrRestriction] public int* UpHead;
            [NativeDisableUnsafePtrRestriction] public int* LevelNodes;
            public int From;
            public int K;

            public void Execute(int i)
            {
                // atomic = 1: same-level siblings may share a target arc.
                BurstKernels.ContractNode(WFwd, WBwd, UpStart, UpHead, LevelNodes[From + i], K, 1);
            }
        }

        /// <summary>
        /// Schedule a full customization as one parallel job per level, chained.
        /// Buffers stay pinned for the whole sweep; the caller owns them.
        ///
        /// innerBatch trades scheduling overhead against load balance. Levels
        /// near the leaves are wide and cheap (large batches win); levels near
        /// the top separators are narrow and expensive (small batches win), so
        /// it is scaled by level width rather than fixed.
        /// </summary>
        public static unsafe JobHandle ScheduleFullCustomize(
            float* wFwd, float* wBwd,
            int* upStart, int* upHead, int* levelNodes,
            int* levelStart, int levelCount, int k,
            JobHandle dependency = default)
        {
            var handle = dependency;
            for (int lvl = 0; lvl < levelCount; lvl++)
            {
                int from = levelStart[lvl];
                int count = levelStart[lvl + 1] - from;
                if (count <= 0) continue;

                var job = new ContractLevelJob
                {
                    WFwd = wFwd, WBwd = wBwd,
                    UpStart = upStart, UpHead = upHead,
                    LevelNodes = levelNodes,
                    From = from, K = k,
                };
                int batch = count >= 4096 ? 64 : count >= 256 ? 16 : 1;
                // Each level depends on the previous one completing: the barrier
                // is what makes the atomic-min writes sufficient for correctness.
                handle = job.Schedule(count, batch, handle);
            }
            return handle;
        }

        /// <summary>
        /// One dissection-cell task: the sequential rank-order sweep over the
        /// task's contiguous range, atomics only for enclosing-separator
        /// writes. This is the production decomposition — measured 3.5x on 4
        /// cores in the harness, versus 1.2x for the level scheme above (kept
        /// for A/B). innerloopBatchCount = 1 gives Unity's scheduler work-
        /// stealing over whole tasks, replacing the harness's Interlocked
        /// largest-first queue.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High,
                      CompileSynchronously = true)]
        public unsafe struct CustomizeTaskJob : IJobParallelFor
        {
            [NativeDisableUnsafePtrRestriction] public float* WFwd;
            [NativeDisableUnsafePtrRestriction] public float* WBwd;
            [NativeDisableUnsafePtrRestriction] public int* UpStart;
            [NativeDisableUnsafePtrRestriction] public int* UpHead;
            [NativeDisableUnsafePtrRestriction] public int* NodeAtRank;
            [NativeDisableUnsafePtrRestriction] public int* Rank;
            [NativeDisableUnsafePtrRestriction] public int* TaskLo;   // sorted largest-first by the scheduler
            [NativeDisableUnsafePtrRestriction] public int* TaskHi;
            public int K;

            public void Execute(int i)
            {
                BurstKernels.CustomizeTaskRange(WFwd, WBwd, UpStart, UpHead, NodeAtRank, Rank,
                                               TaskLo[i], TaskHi[i], K);
            }
        }

        /// <summary>Phase 2: contract the enclosing separators sequentially in
        /// ascending rank, after every task has completed.</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High,
                      CompileSynchronously = true)]
        public unsafe struct Phase2Job : IJob
        {
            [NativeDisableUnsafePtrRestriction] public float* WFwd;
            [NativeDisableUnsafePtrRestriction] public float* WBwd;
            [NativeDisableUnsafePtrRestriction] public int* UpStart;
            [NativeDisableUnsafePtrRestriction] public int* UpHead;
            [NativeDisableUnsafePtrRestriction] public int* NodeAtRank;
            [NativeDisableUnsafePtrRestriction] public int* Phase2Ranks;
            public int Phase2Count;
            public int K;

            public void Execute()
            {
                for (int i = 0; i < Phase2Count; i++)
                    BurstKernels.ContractNode(WFwd, WBwd, UpStart, UpHead,
                                              NodeAtRank[Phase2Ranks[i]], K, 0);
            }
        }

        /// <summary>
        /// Pin managed weight arrays and run the sweep. Convenience for the
        /// adapter, which holds the Core engine's arrays rather than
        /// NativeArrays. GCHandle pinning is fine here because the sweep is a
        /// bounded, synchronous unit of work. Uses the task decomposition when
        /// the skeleton carries a frontier; falls back to the level jobs.
        /// </summary>
        public static unsafe void RunFullCustomize(CchMetrics metrics)
        {
            var c = metrics.C;
            if (c.TaskLo != null && c.TaskHi != null && c.Phase2Ranks != null && c.TaskLo.Length >= 2)
            {
                fixed (float* wf = metrics.WFwd, wb = metrics.WBwd)
                fixed (int* us = c.UpStart, uh = c.UpHead, nr = c.NodeAtRank, rk = c.Rank,
                       tl = c.TaskLo, th = c.TaskHi, p2 = c.Phase2Ranks)
                {
                    var tasks = new CustomizeTaskJob
                    {
                        WFwd = wf, WBwd = wb, UpStart = us, UpHead = uh,
                        NodeAtRank = nr, Rank = rk, TaskLo = tl, TaskHi = th,
                        K = metrics.K,
                    };
                    var h = tasks.Schedule(c.TaskLo.Length, 1);
                    var phase2 = new Phase2Job
                    {
                        WFwd = wf, WBwd = wb, UpStart = us, UpHead = uh,
                        NodeAtRank = nr, Phase2Ranks = p2,
                        Phase2Count = c.Phase2Ranks.Length, K = metrics.K,
                    };
                    phase2.Schedule(h).Complete();
                }
                return;
            }
            fixed (float* wf = metrics.WFwd, wb = metrics.WBwd)
            fixed (int* us = c.UpStart, uh = c.UpHead, ln = c.LevelNodes, ls = c.LevelStart)
            {
                ScheduleFullCustomize(wf, wb, us, uh, ln, ls, c.LevelCount, metrics.K).Complete();
            }
        }
#else
        /// <summary>
        /// Out-of-game build: the harness drives
        /// <see cref="CchMetrics.FullCustomizeParallelTasks"/> directly, which
        /// runs the identical kernels over the identical task decomposition on
        /// plain threads. Only the scheduler differs.
        /// </summary>
        public static void RunFullCustomize(CchMetrics metrics) => metrics.FullCustomizeParallelTasks();
#endif
    }
}
