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
        internal int[] Chain = null!;
        internal List<(int arc, bool fwd)> ArcPath = new List<(int, bool)>(256);
        public int LastMeetNode = -1;

        internal static QueryContext Create(int n)
        {
            return new QueryContext
            {
                Df = new float[n], Db = new float[n],
                PredF = new int[n], PredB = new int[n],
                StampF = new int[n], StampB = new int[n],
                Chain = new int[n],
            };
        }
    }

    /// <summary>
    /// Layer 2 (part 1): up-then-down CCH queries over the elimination tree —
    /// no priority queue, cost O(tree height x mean up-degree) per side,
    /// microseconds per trip (plan §4 Layer 2). Also exposes the forward /
    /// backward upward label sweeps reused by Layer 3 buckets and the
    /// CH-potential searches.
    /// </summary>
    public sealed class CchQuery
    {
        public CchSkeleton C = null!;
        public CchMetrics M = null!;
        private int K => M.K;

        public static CchQuery Create(CchMetrics m) => new CchQuery { C = m.C, M = m };

        public QueryContext CreateContext() => QueryContext.Create(C.NodeCount);

        /// <summary>Distance s->t under metric k; meet node in ctx.LastMeetNode.</summary>
        public float Distance(QueryContext ctx, int s, int t, int k)
        {
            if (s == t) { ctx.LastMeetNode = s; return 0f; }
            ctx.Stamp++;
            ForwardSweep(ctx, s, k);
            return BackwardSweepAndMeet(ctx, t, k);
        }

        private void ForwardSweep(QueryContext ctx, int s, int k)
        {
            var c = C; var w = M.WFwd;
            int stamp = ctx.Stamp;
            ctx.Df[s] = 0f; ctx.StampF[s] = stamp; ctx.PredF[s] = -1;
            int v = s;
            while (v >= 0)
            {
                if (ctx.StampF[v] == stamp)
                {
                    float dv = ctx.Df[v];
                    if (!float.IsPositiveInfinity(dv))
                    {
                        for (int a = c.UpStart[v]; a < c.UpStart[v + 1]; a++)
                        {
                            int h = c.UpHead[a];
                            float cand = dv + w[a * K + k];
                            if (ctx.StampF[h] != stamp || cand < ctx.Df[h])
                            {
                                ctx.Df[h] = cand; ctx.StampF[h] = stamp; ctx.PredF[h] = a;
                            }
                        }
                    }
                }
                v = c.EtParent[v];
            }
        }

        private float BackwardSweepAndMeet(QueryContext ctx, int t, int k)
        {
            var c = C; var w = M.WBwd;
            int stamp = ctx.Stamp;
            ctx.Db[t] = 0f; ctx.StampB[t] = stamp; ctx.PredB[t] = -1;
            float best = float.PositiveInfinity; int meet = -1;
            int v = t;
            while (v >= 0)
            {
                if (ctx.StampB[v] == stamp)
                {
                    float dv = ctx.Db[v];
                    if (!float.IsPositiveInfinity(dv))
                    {
                        for (int a = c.UpStart[v]; a < c.UpStart[v + 1]; a++)
                        {
                            int h = c.UpHead[a];
                            float cand = dv + w[a * K + k];
                            if (ctx.StampB[h] != stamp || cand < ctx.Db[h])
                            {
                                ctx.Db[h] = cand; ctx.StampB[h] = stamp; ctx.PredB[h] = a;
                            }
                        }
                    }
                    if (ctx.StampF[v] == stamp)
                    {
                        float sum = ctx.Df[v] + ctx.Db[v];
                        if (sum < best) { best = sum; meet = v; }
                    }
                }
                v = c.EtParent[v];
            }
            ctx.LastMeetNode = meet;
            return best;
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
                UnpackArc(arc, fwd, k, edgePathOut);
            return d;
        }

        private struct UnpackFrame { public int Arc; public bool Fwd; }

        /// <summary>Expand one chordal arc into original edges, in travel order.
        /// The realizing lower triangle is metric-dependent, so unpacking takes
        /// the metric; geometry is expanded only for routes actually driven.</summary>
        public void UnpackArc(int arc, bool fwd, int k, List<int> edgesOut)
        {
            var c = C; var g = c.G;
            var stack = new Stack<UnpackFrame>(8);
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
        /// ancestor with a finite label. Used by shopper searches and dispatch.</summary>
        public void ForwardUpwardLabels(QueryContext ctx, int s, int k, Action<int, float> sink)
        {
            ctx.Stamp++;
            ForwardSweep(ctx, s, k);
            int v = s;
            while (v >= 0)
            {
                if (ctx.StampF[v] == ctx.Stamp && !float.IsPositiveInfinity(ctx.Df[v]))
                    sink(v, ctx.Df[v]);
                v = C.EtParent[v];
            }
        }

        /// <summary>Backward upward labels toward t (dist node -> t). Used to
        /// build destination buckets and CH-potentials.</summary>
        public void BackwardUpwardLabels(QueryContext ctx, int t, int k, Action<int, float> sink)
        {
            ctx.Stamp++;
            var c = C; var w = M.WBwd;
            int stamp = ctx.Stamp;
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
