using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CS2Path.Core;

namespace CS2Path.Harness
{
    /// <summary>
    /// Replays a real exported city (plan §5 step 1) through the same harness the
    /// synthetic city uses. This exists to test the two assumptions the synthetic
    /// benchmarks CANNOT test, both of which the whole performance story rests on:
    ///
    ///   A1. Real CS2 road networks have small separators — if they do not, the
    ///       elimination tree is tall, arcs explode, and query latency regresses.
    ///   A2. Real demand is spatially concentrated — if it is not, the §4.9
    ///       cluster cache hit rate collapses toward the uniform-random worst case.
    ///
    /// Both are reported as measured numbers next to the synthetic baseline, so a
    /// bad answer is visible immediately rather than after a full integration.
    /// </summary>
    public static class ImportedCity
    {
        public static string Analyze(string path, int profiles, ulong seed, int queryCount)
        {
            var sb = new StringBuilder();
            CityExport city;
            using (var fs = File.OpenRead(path)) city = CityExport.Read(fs);

            Console.WriteLine($"import: {Path.GetFileName(path)} — {city.NodeCount:N0} nodes, {city.EdgeCount:N0} edges, " +
                              $"{city.Traffic.Count:N0} traffic samples, {city.Demand.Count:N0} demand samples");
            var g = city.ToGraph();

            // Apply the exported congestion snapshot (last sample per edge) so the
            // live metric reflects the real city's state, not free flow.
            int applied = 0;
            if (city.Traffic.Count > 0)
            {
                var lastTick = new int[g.EdgeCount];
                for (int i = 0; i < g.EdgeCount; i++) lastTick[i] = int.MinValue;
                foreach (var s in city.Traffic)
                    if (s.Tick >= lastTick[s.Edge] && float.IsFinite(s.LiveSeconds) && s.LiveSeconds > 0)
                    { lastTick[s.Edge] = s.Tick; g.TimeLive[s.Edge] = s.LiveSeconds; applied++; }
            }

            // Preference population: from the demand trace when present, else a
            // spread comparable to the synthetic city's.
            var pop = new List<Preference>();
            var rng = new SplitMix64(seed);
            if (city.Demand.Count > 0)
                foreach (var d in city.Demand) pop.Add(d.Alpha);
            else
                for (int i = 0; i < 5000; i++)
                    pop.Add(new Preference(0.5f + rng.NextFloat() * 2f, 0.05f + rng.NextFloat(), rng.NextFloat() * 0.5f));
            var anchors = AnchorGrid.Build(pop, null, profiles,
                new[] { Scenario.FreeFlow, Scenario.Typical, Scenario.Live }, seed);

            sb.AppendLine("## Imported city (real CS2 export)");
            sb.AppendLine();
            sb.AppendLine($"`{Path.GetFileName(path)}` — **{g.NodeCount:N0} nodes / {g.EdgeCount:N0} directed edges**, " +
                          $"{applied:N0} edges carrying an exported congestion value, " +
                          $"{city.Demand.Count:N0} recorded trips. Anchors: {profiles} profiles × 3 scenarios.");
            sb.AppendLine();

            // ---- A1: separator quality ----
            var eng = RoutingEngine.Build(g, anchors);
            double arcRatio = (double)eng.Skeleton.ArcCount / Math.Max(1, g.EdgeCount / 2);
            sb.AppendLine("### A1 — separator quality (does the CCH hierarchy hold up on a real map?)");
            sb.AppendLine();
            sb.AppendLine("| measure | this city | synthetic 131k baseline | reads as |");
            sb.AppendLine("|---|---|---|---|");
            sb.AppendLine($"| nested dissection order | {eng.BuildOrderMs:N0} ms | 470–870 ms | — |");
            sb.AppendLine($"| contraction | {eng.BuildSkeletonMs:N0} ms | ~1,100–3,400 ms | — |");
            sb.AppendLine($"| chordal arcs | {eng.Skeleton.ArcCount:N0} ({arcRatio:0.0}× undirected edges) | 9.2× | lower is better |");
            sb.AppendLine($"| elimination tree height | {eng.Skeleton.TreeHeight:N0} | 264 | **the key number** — height drives query cost |");
            sb.AppendLine($"| full customization ({anchors.MetricCount} metrics) | {eng.FullCustomizeMs:N0} ms | ~1,100 ms @16 | — |");
            sb.AppendLine();

            // ---- query latency + correctness on the real graph ----
            var ctx = eng.Query.CreateContext();
            int liveStart = anchors.ScenarioBlockStart(Scenario.Live);
            var pairs = new (int s, int t, int k)[queryCount];
            for (int i = 0; i < queryCount; i++)
                pairs[i] = (rng.NextInt(g.NodeCount), rng.NextInt(g.NodeCount), liveStart + rng.NextInt(profiles));
            for (int i = 0; i < Math.Min(2000, queryCount); i++) eng.Query.Distance(ctx, pairs[i].s, pairs[i].t, pairs[i].k);
            var lat = new List<double>(queryCount);
            for (int i = 0; i < queryCount; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                eng.Query.Distance(ctx, pairs[i].s, pairs[i].t, pairs[i].k);
                lat.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
            }
            int bad = 0; var djLat = new List<double>();
            for (int i = 0; i < Math.Min(200, queryCount); i++)
            {
                var (s, t, k) = pairs[i];
                float dc = eng.Query.Distance(ctx, s, t, k);
                long t0 = Stopwatch.GetTimestamp();
                float dr = Reference.Dijkstra(g, s, t, e => anchors.EdgeWeight(g, e, k));
                djLat.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
                bool ok = (float.IsPositiveInfinity(dc) && float.IsPositiveInfinity(dr))
                          || Math.Abs(dc - dr) <= 1e-3f * Math.Max(1f, Math.Max(dc, dr));
                if (!ok) bad++;
            }
            sb.AppendLine("### Query performance on the real graph");
            sb.AppendLine();
            sb.AppendLine("| measure | CCH | reference Dijkstra |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine($"| median | {Pct(lat, 0.5):N0} µs | {Pct(djLat, 0.5):N0} µs |");
            sb.AppendLine($"| p99 | {Pct(lat, 0.99):N0} µs | {Pct(djLat, 0.99):N0} µs |");
            sb.AppendLine($"| speedup (mean) | {djLat.Average() / Math.Max(1e-9, lat.Average()):N0}× | — |");
            sb.AppendLine($"| correctness | {200 - bad}/200 exact vs Dijkstra | (reference) |");
            sb.AppendLine();
            if (bad > 0)
                sb.AppendLine($"> **{bad} mismatches on the real graph** — investigate before trusting any other number here.");

            // ---- A2: demand locality / cache hit rate ----
            if (city.Demand.Count > 0)
            {
                var planner = new TripPlanner(eng.Metrics, eng.Query);
                var cache = new ClusterCache(eng.CellPaths);
                planner.Cache = cache;
                planner.Stats.SyncFallbackTimesUs = new List<double>();
                var warm = new List<double>(); var cold = new List<double>();
                int i2 = 0;
                foreach (var d in city.Demand)
                {
                    var req = new TripRequest
                    {
                        AgentId = i2, Origin = d.Origin, Destination = d.Dest,
                        Alpha = d.Alpha, Seed = seed ^ (ulong)(i2 * 48271),
                    };
                    long t0 = Stopwatch.GetTimestamp();
                    var plan = planner.PlanFixed(in req);
                    double us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
                    (plan.ServedFromCache ? warm : cold).Add(us);
                    if (++i2 % 200 == 0) planner.RunExploration(4);
                }
                var st = planner.Stats;
                double hit = (double)st.ServedFromCache / Math.Max(1, st.ServedFromCache + st.DirectGenerations);
                sb.AppendLine("### A2 — demand locality (does the §4.9 cluster cache pay off on real trips?)");
                sb.AppendLine();
                sb.AppendLine("| measure | this city | synthetic zonal baseline |");
                sb.AppendLine("|---|---|---|");
                sb.AppendLine($"| trips replayed | {city.Demand.Count:N0} | 30,000 |");
                sb.AppendLine($"| cache-served share | **{hit:P1}** | 84.2% |");
                sb.AppendLine($"| warm plan median | {Pct(warm, 0.5):N0} µs | ~1,130 µs |");
                sb.AppendLine($"| cold plan median | {Pct(cold, 0.5):N0} µs | ~2,972 µs |");
                sb.AppendLine($"| entries / footprint | {cache.EntryCount:N0} / {cache.EstimatedBytes() / 1e6:0.0} MB | 539 / 0.1 MB |");
                sb.AppendLine($"| certified-exact | {(double)st.CertifiedTrips / Math.Max(1, st.TripsPlanned):P1} | 77.8% |");
                sb.AppendLine($"| unreachable | {st.UnreachableTrips:N0} | 0 |");
                sb.AppendLine();
                Console.WriteLine($"  A2: hit={hit:P1} warm={Pct(warm, 0.5):N0}us cold={Pct(cold, 0.5):N0}us entries={cache.EntryCount}");
            }
            else
            {
                sb.AppendLine("### A2 — demand locality");
                sb.AppendLine();
                sb.AppendLine("_No demand trace in this export — re-export with trip recording enabled to measure cache hit rate._");
                sb.AppendLine();
            }

            Console.WriteLine($"  A1: height={eng.Skeleton.TreeHeight} arcs={eng.Skeleton.ArcCount:N0} ({arcRatio:0.0}x) " +
                              $"| query median={Pct(lat, 0.5):N0}us p99={Pct(lat, 0.99):N0}us correct={200 - bad}/200");
            return sb.ToString();
        }

        private static double Pct(List<double> xs, double p)
        {
            if (xs.Count == 0) return double.NaN;
            var s = xs.OrderBy(x => x).ToList();
            return s[Math.Min(s.Count - 1, (int)(p * s.Count))];
        }
    }
}
