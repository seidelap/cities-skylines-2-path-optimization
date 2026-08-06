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

            // ---- A3: congestion-change locality (clustered vs scattered) ----
            // The §6 partial-customization target lives entirely inside a 13×
            // cost swing between clustered and scattered change (6.9 ms vs
            // 92 ms at 131k synthetic). The trace's delta-filtered samples at
            // each tick ARE that refresh's changed-edge set, so we can replay
            // them through partial customization and measure the real thing.
            var byTick = new SortedDictionary<int, List<CityExport.TrafficSample>>();
            foreach (var s in city.Traffic)
            {
                if (!byTick.TryGetValue(s.Tick, out var lst)) byTick[s.Tick] = lst = new List<CityExport.TrafficSample>();
                lst.Add(s);
            }
            if (byTick.Count >= 2)
            {
                var refreshMs = new List<double>();
                var arcsTouched = new List<double>();
                var changedCounts = new List<double>();
                var comps = new List<double>();
                var shares = new List<double>();
                bool first = true;
                var changed = new List<int>();
                foreach (var kv in byTick)
                {
                    changed.Clear();
                    foreach (var s in kv.Value)
                        if (s.Edge >= 0 && s.Edge < g.EdgeCount && float.IsFinite(s.LiveSeconds) && s.LiveSeconds > 0)
                        { g.TimeLive[s.Edge] = s.LiveSeconds; changed.Add(s.Edge); }
                    if (changed.Count == 0) continue;
                    if (first) { eng.RefreshLive(changed); first = false; continue; } // pin to trace start, untimed
                    long rt0 = Stopwatch.GetTimestamp();
                    eng.RefreshLive(changed);
                    refreshMs.Add((Stopwatch.GetTimestamp() - rt0) * 1000.0 / Stopwatch.Frequency);
                    arcsTouched.Add(eng.Metrics.LastPartialArcsRecomputed);
                    changedCounts.Add(changed.Count);
                    ClusterChangedEdges(g, changed, out int nComp, out _, out float share);
                    comps.Add(nComp);
                    shares.Add(share);
                }
                if (refreshMs.Count > 0)
                {
                    sb.AppendLine("### A3 — congestion-change locality (is real change clustered or scattered?)");
                    sb.AppendLine();
                    sb.AppendLine("| measure | this trace | synthetic reference |");
                    sb.AppendLine("|---|---|---|");
                    sb.AppendLine($"| refresh transitions replayed | {refreshMs.Count} | — |");
                    sb.AppendLine($"| changed edges / transition (median) | {Pct(changedCounts, 0.5):N0} | 100–1,000 |");
                    sb.AppendLine($"| partial customization median / p99 | {Pct(refreshMs, 0.5):0.0} ms / {Pct(refreshMs, 0.99):0.0} ms | clustered 6.9 ms, scattered 92 ms @131k |");
                    sb.AppendLine($"| arcs recomputed (median) | {Pct(arcsTouched, 0.5):N0} | clustered ~3.4k, scattered ~43k |");
                    sb.AppendLine($"| connected components per change set (median) | {Pct(comps, 0.5):N0} | clustered: few |");
                    sb.AppendLine($"| share of changed edges in components ≥ 3 | {shares.Average():P0} | clustered: high |");
                    sb.AppendLine();
                    Console.WriteLine($"  A3: transitions={refreshMs.Count} changed-median={Pct(changedCounts, 0.5):N0} " +
                                      $"refresh median={Pct(refreshMs, 0.5):0.0}ms p99={Pct(refreshMs, 0.99):0.0}ms " +
                                      $"clustered-share={shares.Average():P0}");
                }
            }
            else if (city.Traffic.Count > 0)
            {
                sb.AppendLine("### A3 — congestion-change locality");
                sb.AppendLine();
                sb.AppendLine("_Trace carries a single snapshot — record with the cadenced sampler (StartTrace) to measure change locality._");
                sb.AppendLine();
            }

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

        /// <summary>Union-find over a changed-edge set, joining edges that share
        /// an endpoint. A congestion pocket reads as one big component; scattered
        /// noise reads as singletons. clusteredShare = fraction of changed edges
        /// in components of size ≥ 3. Public so the verify suite can pin the
        /// classifier on hand-built patterns.</summary>
        public static void ClusterChangedEdges(Graph g, List<int> edges,
            out int components, out int largest, out float clusteredShare)
        {
            int n = edges.Count;
            components = 0; largest = 0; clusteredShare = 0f;
            if (n == 0) return;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

            var nodeToEdge = new Dictionary<int, int>(n * 2);
            for (int i = 0; i < n; i++)
            {
                int e = edges[i];
                foreach (int v in new[] { g.Tail[e], g.Head[e] })
                {
                    if (nodeToEdge.TryGetValue(v, out int j)) Union(i, j);
                    else nodeToEdge[v] = i;
                }
            }
            var sizes = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                sizes.TryGetValue(r, out int c);
                sizes[r] = c + 1;
            }
            components = sizes.Count;
            int inBig = 0;
            foreach (var c in sizes.Values)
            {
                if (c > largest) largest = c;
                if (c >= 3) inBig += c;
            }
            clusteredShare = (float)inBig / n;
        }
    }
}
