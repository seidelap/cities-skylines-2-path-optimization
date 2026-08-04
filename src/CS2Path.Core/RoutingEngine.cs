using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace CS2Path.Core
{
    /// <summary>
    /// Facade over Layers 0-4: owns the structure (order + skeleton), the
    /// metrics, and the query engine, and implements the Layer-0 edit
    /// discipline (plan §4 Layer 0 / §3): nothing blocks on structure.
    /// Closures and removals apply INSTANTLY through metric weights (partial
    /// customization with +inf); structural additions trigger an asynchronous
    /// background rebuild that atomically swaps in when ready.
    /// </summary>
    public sealed class RoutingEngine
    {
        public Graph G = null!;
        public AnchorGrid Anchors = null!;
        public CchSkeleton Skeleton = null!;
        public CchMetrics Metrics = null!;
        public CchQuery Query = null!;
        public int[] RegionOf = null!;
        public int RegionCount;
        public FeatureFlags Flags = new FeatureFlags();

        public double BuildOrderMs, BuildSkeletonMs, FullCustomizeMs;

        private volatile RoutingEngine? _pendingRebuild;
        private Thread? _rebuildThread;

        public static RoutingEngine Build(Graph g, AnchorGrid anchors, int regionTiles = 8)
        {
            var e = new RoutingEngine { G = g, Anchors = anchors };
            var sw = Stopwatch.StartNew();
            var rank = NestedDissection.ComputeOrder(g);
            e.BuildOrderMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            e.Skeleton = CchSkeleton.Build(g, rank);
            e.BuildSkeletonMs = sw.Elapsed.TotalMilliseconds;

            e.Metrics = CchMetrics.Create(e.Skeleton, anchors);
            sw.Restart();
            e.Metrics.ResetAll();
            e.Metrics.FullCustomize();
            e.FullCustomizeMs = sw.Elapsed.TotalMilliseconds;

            e.Query = CchQuery.Create(e.Metrics);
            e.RegionOf = NestedDissection.ComputeRegions(g, regionTiles, out e.RegionCount);
            return e;
        }

        /// <summary>Refresh the live-scenario lanes after a traffic update.
        /// Millisecond-scale partial customization (plan §4 Layer 1).
        /// Single-writer contract: no queries may run concurrently with a
        /// refresh — in-game this is a sync point between the customization job
        /// and the query jobs, in the harness the tick loop serializes them.</summary>
        public void RefreshLive(List<int> changedEdges)
        {
            int start = Anchors.ScenarioBlockStart(Scenario.Live);
            Metrics.PartialCustomize(changedEdges, start, Anchors.ProfileCount);
        }

        /// <summary>Refresh ALL lane blocks for closure-state transitions. Hard
        /// closures (+inf) define the edge weight in EVERY scenario, so a
        /// close/reopen must recustomize free-flow and typical lanes too —
        /// otherwise those lanes desync from their own metric definition until
        /// the next structural rebuild. Closure transitions are rare; the extra
        /// lanes are cheap.</summary>
        public void RefreshClosures(List<int> changedEdges)
        {
            if (changedEdges.Count > 0)
                Metrics.PartialCustomize(changedEdges, 0, Anchors.MetricCount);
        }

        /// <summary>Structural edit: kick off a background rebuild; queries keep
        /// running on the current structure until the swap.</summary>
        public void BeginTopologyRebuild(Graph newGraph)
        {
            if (_rebuildThread != null && _rebuildThread.IsAlive) return; // coalesce
            _rebuildThread = new Thread(() =>
            {
                var rebuilt = Build(newGraph, Anchors);
                _pendingRebuild = rebuilt;
            }) { IsBackground = true, Name = "cs2path-rebuild" };
            _rebuildThread.Start();
        }

        /// <summary>Adopt a finished background rebuild, if any. Returns true on
        /// swap; callers must recreate per-thread contexts/planners after a swap.</summary>
        public bool TryAdoptRebuild()
        {
            var p = _pendingRebuild;
            if (p == null) return false;
            _pendingRebuild = null;
            G = p.G; Skeleton = p.Skeleton; Metrics = p.Metrics; Query = p.Query;
            RegionOf = p.RegionOf; RegionCount = p.RegionCount;
            return true;
        }
    }
}
