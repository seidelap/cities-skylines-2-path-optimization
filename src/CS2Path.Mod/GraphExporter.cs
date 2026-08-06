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
        /// keybind or dev console command), never on the hot path.
        ///
        /// The ECS reading lives in <see cref="GraphExporterSystem"/>; pass the
        /// system in so this class stays a plain container that the harness-side
        /// tests can exercise without a game world.
        ///
        /// Note on node identity (matters for ClusterCache.RemapAfterRebuild):
        /// our node ids are dense indices assigned in PathNode-discovery order,
        /// so they are NOT stable across exports. A review of this repo proved
        /// that range-checking ids across a rebuild silently re-keys cache
        /// entries onto wrong corridors — so if the live adapter ever rebuilds
        /// the graph, it must retain its PathNode -> index map and hand the
        /// old→new mapping to RemapAfterRebuild. Exports are one-shot snapshots
        /// and do not have this problem.</summary>
        public CityExport CaptureGraph(
#if OUT_OF_GAME_BUILD
            object? exporterSystem = null
#else
            GraphExporterSystem exporterSystem
#endif
        )
        {
#if OUT_OF_GAME_BUILD
            throw new NotSupportedException(
                "Out-of-game build. Build with -p:InGame=true on a machine that has CS2 and the " +
                "official modding toolchain installed (deploy/gcp provisions one).");
#else
            if (exporterSystem == null) throw new ArgumentNullException(nameof(exporterSystem));
            return exporterSystem.Capture();
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

        // Delta filter state: last value actually recorded per edge.
        private readonly System.Collections.Generic.Dictionary<int, float> _lastRecorded
            = new System.Collections.Generic.Dictionary<int, float>();

        /// <summary>Delta-filtered traffic recording: append only when the value
        /// moved more than <paramref name="minRelDelta"/> since the last sample
        /// recorded for this edge. Two things follow, both load-bearing:
        /// (1) a full-city trace stays tens of MB instead of hundreds — a
        /// quiescent lane costs nothing after its first sample; and (2) the
        /// samples at tick T are exactly the CHANGED-edge set of that refresh,
        /// which is what the harness A3 analysis replays through partial
        /// customization to measure real change locality (clustered vs
        /// scattered — a 13× cost swing in the synthetic benchmark).</summary>
        public bool TryRecordTraffic(int tick, int edge, float liveSeconds, float minRelDelta)
        {
            if (_traffic.Count >= MaxTrafficSamples) return false;
            if (!(liveSeconds > 0) || float.IsNaN(liveSeconds) || float.IsInfinity(liveSeconds)) return false;
            if (_lastRecorded.TryGetValue(edge, out float prev)
                && Math.Abs(liveSeconds - prev) <= minRelDelta * Math.Max(1e-3f, prev))
                return false;
            _lastRecorded[edge] = liveSeconds;
            _traffic.Add(new CityExport.TrafficSample { Tick = tick, Edge = edge, LiveSeconds = liveSeconds });
            return true;
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
