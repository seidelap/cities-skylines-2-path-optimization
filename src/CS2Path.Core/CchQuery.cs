using System;
using System.Collections.Generic;

namespace CS2Path.Core
{
    /// <summary>Per-thread reusable query state (no allocation per query).</summary>
    public sealed class QueryContext
    {
        internal float[] Df = null!, Db = null!;
        internal int[] PredF = null!, PredB = null!;
        internal int[] StampF = null!, StampB = null!;
        internal int Stamp;
        internal int[] Chain = null!, ChainT = null!;
        internal List<(int arc, bool fwd)> ArcPath = new List<(int, bool)>(256);
        internal Stack<CchQuery.UnpackFrame> UnpackStack = new Stack<CchQuery.UnpackFrame>(64);
        public int LastMeetNode = -1;
        // lazily allocated lane-batched labels (portfolio anchor sweeps)
        internal float[]? MultiDf, MultiDb;

        internal static QueryContext Create(int n)
        {
            return new QueryContext
            {
                Df = new float[n], Db = new float[n],
                PredF = new int[n], PredB = new int[n],
                StampF = new int[n], StampB = new int[n],
                Chain = new int[n], ChainT = new int[n],
            };
        }
    }

    /// <summary>
    /// Layer 2 (part 1): up-then-down CCH queries over the elimination tree —
    /// no priority queue (plan §4 Layer 2). Both ancestor chains are walked
    /// MERGED in rank order with best-meet pruning: once a tentative s-t cost
    /// exists, any chain node whose label already exceeds it is skipped, which
    /// removes most of the top-separator clique work for typical trips.
    /// DistanceMulti batches all anchor lanes through one sweep (the weights
    /// are lane-contiguous, so the extra lanes ride the same cache lines).
    /// Also exposes the forward/backward upward label sweeps reused by Layer 3
    /// buckets and the CH-potential searches.
    /// </summary>
    public sealed class CchQuery
    {
        public CchSkeleton C = null!;
        public CchMetrics M = null!;
        private int K => M.K;

        public const int MaxBatch = 16;

        public static CchQuery Create(CchMetrics m) => new CchQuery { C = m.C, M = m };

        public QueryContext CreateContext() => QueryContext.Create(C.NodeCount);

        /// <summary>Advance the query stamp, clearing stamp arrays on wraparound
        /// (reachable in a long game session: ~2^31 queries).</summary>
        private static int BumpStamp(QueryContext ctx)
        {
            if (ctx.Stamp >= int.MaxValue - 1)
            {
                Array.Clear(ctx.StampF, 0, ctx.StampF.Length);
                Array.Clear(ctx.StampB, 0, ctx.StampB.Length);
                ctx.Stamp = 0;
            }
            return ++ctx.Stamp;
        }

        /// <summary>Distance s->t under metric k; meet node in ctx.LastMeetNode.</summary>
        public float Distance(QueryContext ctx, int s, int t, int k)
        {
            if (s == t) { ctx.LastMeetNode = s; return 0f; }
            var c = C;
            int stamp = BumpStamp(ctx);
            int lenS = 0, lenT = 0;
            for (int v2 = s; v2 >= 0; v2 = c.EtParent[v2]) ctx.Chain[lenS++] = v2;
            for (int v2 = t; v2 >= 0; v2 = c.EtParent[v2]) ctx.ChainT[lenT++] = v2;
            ctx.Df[s] = 0f; ctx.StampF[s] = stamp; ctx.PredF[s] = -1;
            ctx.Db[t] = 0f; ctx.StampB[t] = stamp; ctx.PredB[t] = -1;
            float best = float.PositiveInfinity; int meet = -1;
            var wf = M.WFwd; var wb = M.WBwd;
            int i = 0, j = 0;
            while (i < lenS || j < lenT)
            {
                int v; bool onS = false, onT = false;
                if (i < lenS && j < lenT)
                {
                    int vs = ctx.Chain[i], vt = ctx.ChainT[j];
                    if (vs == vt) { v = vs; onS = onT = true; i++; j++; }
                    else if (c.Rank[vs] < c.Rank[vt]) { v = vs; onS = true; i++; }
                    else { v = vt; onT = true; j++; }
                }
                else if (i < lenS) { v = ctx.Chain[i++]; onS = true; }
                else { v = ctx.ChainT[j++]; onT = true; }

                if (onS && onT && ctx.StampF[v] == stamp && ctx.StampB[v] == stamp)
                {
                    float cand = ctx.Df[v] + ctx.Db[v];
                    if (cand < best) { best = cand; meet = v; }
                }
                if (onS && ctx.StampF[v] == stamp)
                {
                    float dv = ctx.Df[v];
                    if (dv < best) // prune: labels >= best cannot improve any meet above
                    {
                        for (int a = c.UpStart[v]; a < c.UpStart[v + 1]; a++)
                        {
                            int h = c.UpHead[a];
                            float cand = dv + wf[a * K + k];
                            if (ctx.StampF[h] != stamp || cand < ctx.Df[h])
                            {
                                ctx.Df[h] = cand; ctx.StampF[h] = stamp; ctx.PredF[h] = a;
                            }
                        }
                    }
                }
                if (onT && ctx.StampB[v] == stamp)
                {
                    float dv = ctx.Db[v];
                    if (dv < best)
                    {
                        for (int a = c.UpStart[v]; a < c.UpStart[v + 1]; a++)
                        {
                            int h = c.UpHead[a];
                            float cand = dv + wb[a * K + k];
                            if (ctx.StampB[h] != stamp || cand < ctx.Db[h])
                            {
                                ctx.Db[h] = cand; ctx.StampB[h] = stamp; ctx.PredB[h] = a;
                            }
                        }
                    }
                }
            }
            ctx.LastMeetNode = meet;
            return best;
        }

        /// <summary>Batched distances s->t for lanes [kFrom, kFrom+laneCount):
        /// one merged sweep computes every anchor metric at once. Fills
        /// distsOut[l] and meetsOut[l] (the via-node candidates). No path
        /// reconstruction — used by portfolio generation and certificates.</summary>
        public void DistanceMulti(QueryContext ctx, int s, int t, int kFrom, int laneCount,
                                  float[] distsOut, int[] meetsOut)
        {
            if (laneCount > MaxBatch) throw new ArgumentException($"laneCount > {MaxBatch}");
            if (s == t)
            {
                for (int l = 0; l < laneCount; l++) { distsOut[l] = 0f; meetsOut[l] = s; }
                return;
            }
            var c = C;
            int n = c.NodeCount;
            ctx.MultiDf ??= new float[n * MaxBatch];
            ctx.MultiDb ??= new float[n * MaxBatch];
            var mdf = ctx.MultiDf; var mdb = ctx.MultiDb;
            int stamp = BumpStamp(ctx);
            int lenS = 0, lenT = 0;
            for (int v2 = s; v2 >= 0; v2 = c.EtParent[v2]) ctx.Chain[lenS++] = v2;
            for (int v2 = t; v2 >= 0; v2 = c.EtParent[v2]) ctx.ChainT[lenT++] = v2;
            for (int l = 0; l < laneCount; l++)
            {
                mdf[s * MaxBatch + l] = 0f; mdb[t * MaxBatch + l] = 0f;
                distsOut[l] = float.PositiveInfinity; meetsOut[l] = -1;
            }
            ctx.StampF[s] = stamp; ctx.StampB[t] = stamp;
            var wf = M.WFwd; var wb = M.WBwd;
            int i = 0, j = 0;
            while (i < lenS || j < lenT)
            {
                int v; bool onS = false, onT = false;
                if (i < lenS && j < lenT)
                {
                    int vs = ctx.Chain[i], vt = ctx.ChainT[j];
                    if (vs == vt) { v = vs; onS = onT = true; i++; j++; }
                    else if (c.Rank[vs] < c.Rank[vt]) { v = vs; onS = true; i++; }
                    else { v = vt; onT = true; j++; }
                }
                else if (i < lenS) { v = ctx.Chain[i++]; onS = true; }
                else { v = ctx.ChainT[j++]; onT = true; }

                int bv = v * MaxBatch;
                if (onS && onT && ctx.StampF[v] == stamp && ctx.StampB[v] == stamp)
                {
                    for (int l = 0; l < laneCount; l++)
                    {
                        float cand = mdf[bv + l] + mdb[bv + l];
                        if (cand < distsOut[l]) { distsOut[l] = cand; meetsOut[l] = v; }
                    }
                }
                if (onS && ctx.StampF[v] == stamp && AnyActive(mdf, bv, distsOut, laneCount))
                {
                    for (int a = c.UpStart[v]; a < c.UpStart[v + 1]; a++)
                    {
                        int h = c.UpHead[a];
                        int bh = h * MaxBatch, bw = a * K + kFrom;
                        if (ctx.StampF[h] != stamp)
                        {
                            ctx.StampF[h] = stamp;
                            for (int l = 0; l < laneCount; l++) mdf[bh + l] = float.PositiveInfinity;
                        }
                        for (int l = 0; l < laneCount; l++)
                        {
                            float cand = mdf[bv + l] + wf[bw + l];
                            if (cand < mdf[bh + l]) mdf[bh + l] = cand;
                        }
                    }
                }
                if (onT && ctx.StampB[v] == stamp && AnyActive(mdb, bv, distsOut, laneCount))
                {
                    for (int a = c.UpStart[v]; a < c.UpStart[v + 1]; a++)
                    {
                        int h = c.UpHead[a];
                        int bh = h * MaxBatch, bw = a * K + kFrom;
                        if (ctx.StampB[h] != stamp)
                        {
                            ctx.StampB[h] = stamp;
                            for (int l = 0; l < laneCount; l++) mdb[bh + l] = float.PositiveInfinity;
                        }
                        for (int l = 0; l < laneCount; l++)
                        {
                            float cand = mdb[bv + l] + wb[bw + l];
                            if (cand < mdb[bh + l]) mdb[bh + l] = cand;
                        }
                    }
                }
            }
        }

        private static bool AnyActive(float[] lab, int baseIdx, float[] best, int laneCount)
        {
            for (int l = 0; l < laneCount; l++)
                if (lab[baseIdx + l] < best[l]) return true;
            return false;
        }

        /// <summary>Distance with the chordal arc path recorded in ctx.ArcPath
        /// (travel order, with traversal direction per arc).</summary>
        public float DistanceWithArcPath(QueryContext ctx, int s, int t, int k)
        {
            float d = Distance(ctx, s, t, k);
            ctx.ArcPath.Clear();
            if (float.IsPositiveInfinity(d) || ctx.LastMeetNode < 0) return d;
            int meet = ctx.LastMeetNode;
            // forward half: meet .. s, then reverse
            int v = meet;
            int mark = ctx.ArcPath.Count;
            while (v != s)
            {
                int a = ctx.PredF[v];
                ctx.ArcPath.Add((a, true));
                v = C.UpTail[a];
            }
            ctx.ArcPath.Reverse(mark, ctx.ArcPath.Count - mark);
            // backward half: meet .. t (arcs traversed head->tail)
            v = meet;
            while (v != t)
            {
                int a = ctx.PredB[v];
                ctx.ArcPath.Add((a, false));
                v = C.UpTail[a];
            }
            return d;
        }

        /// <summary>Distance + fully unpacked original-edge path under metric k.</summary>
        public float DistanceWithPath(QueryContext ctx, int s, int t, int k, List<int> edgePathOut)
        {
            float d = DistanceWithArcPath(ctx, s, t, k);
            edgePathOut.Clear();
            if (float.IsPositiveInfinity(d)) return d;
            foreach (var (arc, fwd) in ctx.ArcPath)
                UnpackArc(ctx.UnpackStack, arc, fwd, k, edgePathOut);
            return d;
        }

        internal struct UnpackFrame { public int Arc; public bool Fwd; }

        /// <summary>Expand one chordal arc into original edges, in travel order.
        /// The realizing lower triangle is metric-dependent, so unpacking takes
        /// the metric; geometry is expanded only for routes actually driven.
        /// Requires the graph's live components to be in sync with the last
        /// customization — mutating Graph.TimeLive without a RefreshLive breaks
        /// realization matching (loudly: unpack throws).</summary>
        public void UnpackArc(int arc, bool fwd, int k, List<int> edgesOut)
            => UnpackArc(new Stack<UnpackFrame>(8), arc, fwd, k, edgesOut);

        internal void UnpackArc(Stack<UnpackFrame> stack, int arc, bool fwd, int k, List<int> edgesOut)
        {
            var c = C; var g = c.G;
            stack.Clear();
            stack.Push(new UnpackFrame { Arc = arc, Fwd = fwd });
            while (stack.Count > 0)
            {
                var f = stack.Pop();
                int a = f.Arc;
                float w = f.Fwd ? M.WFwd[a * K + k] : M.WBwd[a * K + k];
                int orig = f.Fwd ? c.OrigFwd[a] : c.OrigBwd[a];
                if (orig >= 0)
                {
                    int match = -1;
                    if (M.Anchors.EdgeWeight(g, orig, k) <= w + Tol(w)) match = orig;
                    else
                    {
                        var map = f.Fwd ? c.ExtraFwd : c.ExtraBwd;
                        if (map.Count > 0 && map.TryGetValue(a, out var extras))
                            foreach (var e2 in extras)
                                if (M.Anchors.EdgeWeight(g, e2, k) <= w + Tol(w)) { match = e2; break; }
                    }
                    if (match >= 0) { edgesOut.Add(match); continue; }
                }
                // find realizing triangle: common down-neighbor x of (v, w)
                int v = c.UpTail[a], h = c.UpHead[a];
                int p = c.DownStart[v], pe = c.DownStart[v + 1];
                int q = c.DownStart[h], qe = c.DownStart[h + 1];
                int bestAxv = -1, bestAxw = -1; float bestErr = float.MaxValue;
                while (p < pe && q < qe)
                {
                    int tv = c.DownTail[p], tw = c.DownTail[q];
                    if (tv < tw) p++;
                    else if (tv > tw) q++;
                    else
                    {
                        int axv = c.DownArc[p], axw = c.DownArc[q];
                        float sum = f.Fwd
                            ? M.WBwd[axv * K + k] + M.WFwd[axw * K + k]   // v -> x -> h
                            : M.WBwd[axw * K + k] + M.WFwd[axv * K + k];  // h -> x -> v
                        float err = Math.Abs(sum - w);
                        if (err < bestErr) { bestErr = err; bestAxv = axv; bestAxw = axw; }
                        p++; q++;
                    }
                }
                if (bestAxv < 0 || bestErr > Tol(w) * 4 + 1e-3f)
                    throw new InvalidOperationException($"unpack failed for arc {a} (err={bestErr})");
                if (f.Fwd)
                {
                    // v -> x (arc (x,v) bwd), then x -> h (arc (x,h) fwd): push reversed
                    stack.Push(new UnpackFrame { Arc = bestAxw, Fwd = true });
                    stack.Push(new UnpackFrame { Arc = bestAxv, Fwd = false });
                }
                else
                {
                    // h -> x (arc (x,h) bwd), then x -> v (arc (x,v) fwd)
                    stack.Push(new UnpackFrame { Arc = bestAxv, Fwd = true });
                    stack.Push(new UnpackFrame { Arc = bestAxw, Fwd = false });
                }
            }
        }

        private static float Tol(float w) => Math.Max(1e-4f, w * 1e-5f);

        /// <summary>Forward upward labels from s: calls sink(node, dist) for every
        /// ancestor with a finite label. Used by shopper searches and dispatch.
        /// Full (unpruned) sweep — the labels ARE the product.</summary>
        public void ForwardUpwardLabels(QueryContext ctx, int s, int k, Action<int, float> sink)
        {
            var c = C; var w = M.WFwd;
            int stamp = BumpStamp(ctx);
            ctx.Df[s] = 0f; ctx.StampF[s] = stamp;
            int v = s;
            while (v >= 0)
            {
                if (ctx.StampF[v] == stamp && !float.IsPositiveInfinity(ctx.Df[v]))
                {
                    float dv = ctx.Df[v];
                    for (int a = c.UpStart[v]; a < c.UpStart[v + 1]; a++)
                    {
                        int h = c.UpHead[a];
                        float cand = dv + w[a * K + k];
                        if (ctx.StampF[h] != stamp || cand < ctx.Df[h])
                        {
                            ctx.Df[h] = cand; ctx.StampF[h] = stamp;
                        }
                    }
                    sink(v, dv);
                }
                v = c.EtParent[v];
            }
        }

        /// <summary>Backward upward labels toward t (dist node -> t). Used to
        /// build destination buckets and CH-potentials.</summary>
        public void BackwardUpwardLabels(QueryContext ctx, int t, int k, Action<int, float> sink)
        {
            var c = C; var w = M.WBwd;
            int stamp = BumpStamp(ctx);
            ctx.Db[t] = 0f; ctx.StampB[t] = stamp;
            int v = t;
            while (v >= 0)
            {
                if (ctx.StampB[v] == stamp && !float.IsPositiveInfinity(ctx.Db[v]))
                {
                    float dv = ctx.Db[v];
                    for (int a = c.UpStart[v]; a < c.UpStart[v + 1]; a++)
                    {
                        int h = c.UpHead[a];
                        float cand = dv + w[a * K + k];
                        if (ctx.StampB[h] != stamp || cand < ctx.Db[h])
                        {
                            ctx.Db[h] = cand; ctx.StampB[h] = stamp;
                        }
                    }
                    sink(v, dv);
                }
                v = c.EtParent[v];
            }
        }
    }
}
