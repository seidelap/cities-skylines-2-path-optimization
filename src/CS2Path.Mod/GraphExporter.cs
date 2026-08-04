using System;
using System.Collections.Generic;
using System.IO;
using CS2Path.Core;

namespace CS2Path.Mod
{
    /// <summary>
    /// Plan §5 build-order step 1: export a REAL city to the harness format.
    ///
    /// Why this is the first thing to build in-game, before any system is
    /// replaced: it is pure observation. It reads ECS state, writes a file, and
    /// changes nothing about how the game behaves — so it proves the reader half
    /// of the seam with zero risk to a save, and it produces the artifact that
    /// lets the standalone harness answer the two questions the synthetic city
    /// cannot (see CS2Path.Harness/ImportedCity.cs):
    ///
    ///   A1. do real road networks have the small separators the CCH needs?
    ///   A2. is real demand concentrated enough for the §4.9 cluster cache?
    ///
    /// Every number this repo currently reports is conditional on those two
    /// assumptions. This class is how they stop being assumptions.
    /// </summary>
    public sealed class GraphExporter
    {
        /// <summary>Accumulated congestion samples; the collector appends on a
        /// stagger while the city runs, so the export carries a real trace rather
        /// than a single instant.</summary>
        private readonly List<CityExport.TrafficSample> _traffic = new List<CityExport.TrafficSample>();
        private readonly List<CityExport.DemandSample> _demand = new List<CityExport.DemandSample>();

        public int MaxTrafficSamples = 4_000_000;
        public int MaxDemandSamples = 500_000;

        /// <summary>Snapshot the lane graph. Called once, on demand (a mod
        /// keybind or dev console command), never on the hot path.</summary>
        public CityExport CaptureGraph()
        {
#if OUT_OF_GAME_BUILD
            throw new NotSupportedException(
                "Out-of-game build. Build with -p:InGame=true on a machine with CS2 installed " +
                "(deploy/gcp provisions one). See the ECS reading plan in the comments below.");
#else
            // ===============================================================
            // IN-GAME IMPLEMENTATION PLAN
            //
            // The core routes on a directed edge graph whose nodes are lane
            // endpoints and whose edges are (lane traversal | permitted turn).
            // That expansion is what makes turn costs representable at all
            // (plan §3: "turn costs require edge-based graph treatment").
            //
            // 1. Enumerate lanes:
            //      EntityQuery over Game.Net.Lane + Game.Net.Curve, filtered to
            //      car lanes (Game.Net.CarLane) for the first export. Each lane
            //      becomes a node pair (start, end) and one edge between them.
            //        timeFree = curve length / lane speed limit
            //        money    = length * fuel coefficient (+ toll if present)
            //        comfort  = length * road-class discomfort coefficient
            //        capacity = per-tick service rate from the lane's road class
            //        jamCap   = length / vehicle spacing
            //
            // 2. Enumerate connections:
            //      Game.Net.LaneConnection / the node's connected-lane buffer
            //      gives permitted lane-to-lane transitions. Each becomes a
            //      zero-length edge carrying the TURN cost, which is exactly the
            //      thing an edge-based graph exists to represent.
            //
            // 3. Coordinates:
            //      Game.Net.Node.m_Position (or the curve midpoint) -> X/Y.
            //      These feed nested dissection's geometric bisection, so their
            //      quality directly drives A1. Do not skip them.
            //
            // 4. Stable id table:
            //      Keep Entity -> int index in a NativeHashMap and RETAIN it.
            //      ClusterCache.RemapAfterRebuild requires an old->new node id
            //      mapping across rebuilds; a review of this repo proved that
            //      range-checking ids instead silently re-keys cache entries onto
            //      wrong corridors. The exporter is where that table is born.
            //
            // Validation before writing: CityExport.Validate() catches
            // out-of-range endpoints and non-finite times — an exporter bug must
            // fail here, loudly, not surface later as a mysterious routing result.
            // ===============================================================
            throw new NotImplementedException("in-game ECS capture");
#endif
        }

        /// <summary>Append one congestion observation. Wire to the same refresh
        /// the metric feed uses; sampling a fraction of edges per tick keeps the
        /// trace representative without a per-tick full scan.</summary>
        public void RecordTraffic(int tick, int edge, float liveSeconds)
        {
            if (_traffic.Count >= MaxTrafficSamples) return;
            if (!(liveSeconds > 0) || float.IsNaN(liveSeconds) || float.IsInfinity(liveSeconds)) return;
            _traffic.Add(new CityExport.TrafficSample { Tick = tick, Edge = edge, LiveSeconds = liveSeconds });
        }

        /// <summary>Append one observed trip. This is the A2 evidence: the real
        /// origin-destination distribution, which is the only thing that can
        /// confirm or refute the cluster cache's hit rate.</summary>
        public void RecordDemand(int tick, int origin, int dest, in Preference alpha)
        {
            if (_demand.Count >= MaxDemandSamples) return;
            _demand.Add(new CityExport.DemandSample { Tick = tick, Origin = origin, Dest = dest, Alpha = alpha });
        }

        /// <summary>Write the export. Path should live on the game disk; the
        /// upload-export.ps1 helper ships it to GCS from there.</summary>
        public void Write(CityExport graph, string path)
        {
            graph.Traffic.AddRange(_traffic);
            graph.Demand.AddRange(_demand);
            graph.Validate();
            using (var fs = File.Create(path)) graph.Write(fs);
        }

        public int TrafficSampleCount => _traffic.Count;
        public int DemandSampleCount => _demand.Count;
    }
}
