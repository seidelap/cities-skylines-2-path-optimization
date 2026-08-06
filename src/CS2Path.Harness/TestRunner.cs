using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CS2Path.Core;

namespace CS2Path.Harness
{
    /// <summary>
    /// Correctness verification against reference Dijkstra (plan §5: assert on
    /// query correctness before the game ever loads the mod). No external test
    /// framework — runs everywhere the harness runs.
    /// </summary>
    public static class TestRunner
    {
        private static int _pass, _fail;
        private static readonly StringBuilder Log = new StringBuilder();

        private static void Check(bool cond, string what)
        {
            if (cond) { _pass++; return; }
            _fail++;
            Log.AppendLine($"FAIL: {what}");
            Console.WriteLine($"  FAIL: {what}");
        }

        public static int RunAll(ulong seed, out string report)
        {
            _pass = 0; _fail = 0; Log.Clear();

            Console.WriteLine("verify: city graph CCH vs Dijkstra (all metrics)...");
            VerifyCityCch(seed);
            Console.WriteLine("verify: random graph (BFS-fallback order) CCH vs Dijkstra...");
            VerifyRandomGraphCch(seed + 1);
            Console.WriteLine("verify: partial customization == full recustomization...");
            VerifyPartialCustomization(seed + 2);
            Console.WriteLine("verify: hard closures via metric weights...");
            VerifyClosures(seed + 3);
            Console.WriteLine("verify: path unpacking validity...");
            VerifyPaths(seed + 4);
            Console.WriteLine("verify: potential-guided A* exactness (penalty + repair metrics)...");
            VerifyPotentialAStar(seed + 5);
            Console.WriteLine("verify: certificates sound, repair exact...");
            VerifyCertificates(seed + 6);
            Console.WriteLine("verify: Suurballe disjoint pairs...");
            VerifySuurballe(seed + 7);
            Console.WriteLine("verify: destination buckets vs brute force...");
            VerifyBuckets(seed + 8);
            Console.WriteLine("verify: fleet dispatch vs brute force...");
            VerifyDispatch(seed + 9);
            Console.WriteLine("verify: soft-closure state machine...");
            VerifySoftClosure();
            Console.WriteLine("verify: windowed detector == full-scan detector...");
            VerifyWindowedDetector(seed + 12);
            Console.WriteLine("verify: event-driven bucket refresh == blind refresh...");
            VerifyEventDrivenBuckets(seed + 13);
            Console.WriteLine("verify: city-export round trip (format + corruption detection)...");
            VerifyCityExport(seed + 15);
            Console.WriteLine("verify: parallel customization == sequential (bit-identical)...");
            VerifyParallelCustomization(seed + 18);
            Console.WriteLine("verify: Burst kernel portability (no managed constructs)...");
            VerifyBurstKernelPortability();
            Console.WriteLine("verify: spatial nearest-node index vs brute force...");
            VerifySpatialIndex(seed + 16);
            Console.WriteLine("verify: A3 change-locality classifier (pocket vs scattered)...");
            VerifyChangeLocality(seed + 17);
            Console.WriteLine("verify: §4.9 cluster cache (hits, quarantine, async exploration)...");
            VerifyClusterCache(seed + 11);
            Console.WriteLine("verify: cache remap after re-dissection, warmup governor, telemetry...");
            VerifyCacheV4(seed + 14);
            Console.WriteLine("verify: end-to-end sim smoke test (switches, arrivals, decision points)...");
            VerifySimSmoke(seed + 10);

            report = $"verify: {_pass} passed, {_fail} failed\n" + Log;
            Console.WriteLine($"verify: {_pass} passed, {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }

        private static (SyntheticCity city, RoutingEngine eng) BuildSmallCity(ulong seed, int cols = 40, int rows = 40, int profiles = 6)
        {
            var city = SyntheticCity.Build(cols, rows, 2000, 300, seed);
            var anchors = city.BuildAnchors(profiles);
            var eng = RoutingEngine.Build(city.G, anchors);
            return (city, eng);
        }

        private static Func<int, float> MetricWeight(Graph g, AnchorGrid a, int k) => e => a.EdgeWeight(g, e, k);

        private static void VerifyCityCch(ulong seed)
        {
            var (city, eng) = BuildSmallCity(seed);
            var g = city.G;
            var ctx = eng.Query.CreateContext();
            var rng = new SplitMix64(seed);
            int bad = 0;
            for (int k = 0; k < eng.Anchors.MetricCount; k++)
            {
                for (int i = 0; i < 60; i++)
                {
                    int s = rng.NextInt(g.NodeCount), t = rng.NextInt(g.NodeCount);
                    float dc = eng.Query.Distance(ctx, s, t, k);
                    float dr = Reference.Dijkstra(g, s, t, MetricWeight(g, eng.Anchors, k));
                    if (!Close(dc, dr)) bad++;
                }
            }
            Check(bad == 0, $"city CCH distances: {bad} mismatches");
        }

        private static void VerifyRandomGraphCch(ulong seed)
        {
            var rng = new SplitMix64(seed);
            int n = 700, m = 3500;
            var edges = new List<(int, int, float, float, float, float)>(m);
            for (int i = 0; i < m; i++)
            {
                int u = rng.NextInt(n), v = rng.NextInt(n);
                if (u == v) { i--; continue; }
                edges.Add((u, v, 1f + 10f * rng.NextFloat(), rng.NextFloat(), rng.NextFloat(), 1f));
            }
            var g = Graph.Build(n, edges); // no coordinates: exercises BFS-fallback ND
            var anchors = AnchorGrid.Build(new List<Preference>(), null, 3, new[] { Scenario.FreeFlow, Scenario.Live });
            var eng = RoutingEngine.Build(g, anchors);
            var ctx = eng.Query.CreateContext();
            int bad = 0;
            for (int i = 0; i < 400; i++)
            {
                int s = rng.NextInt(n), t = rng.NextInt(n);
                int k = rng.NextInt(anchors.MetricCount);
                float dc = eng.Query.Distance(ctx, s, t, k);
                float dr = Reference.Dijkstra(g, s, t, MetricWeight(g, anchors, k));
                if (!Close(dc, dr)) bad++;
            }
            Check(bad == 0, $"random-graph CCH distances: {bad} mismatches");
        }

        private static void VerifyPartialCustomization(ulong seed)
        {
            var (city, eng) = BuildSmallCity(seed);
            var g = city.G;
            var rng = new SplitMix64(seed);
            var changed = new List<int>();
            for (int round = 0; round < 5; round++)
            {
                changed.Clear();
                int cnt = 5 + rng.NextInt(200);
                for (int i = 0; i < cnt; i++)
                {
                    int e = rng.NextInt(g.EdgeCount);
                    g.TimeLive[e] = g.TimeFree[e] * (0.7f + 2.5f * rng.NextFloat());
                    changed.Add(e);
                }
                eng.RefreshLive(changed);
            }
            // compare against a from-scratch customization on the same weights
            var fresh = CchMetrics.Create(eng.Skeleton, eng.Anchors);
            fresh.ResetAll();
            fresh.FullCustomize();
            var qa = CchQuery.Create(eng.Metrics);
            var qb = CchQuery.Create(fresh);
            var ca = qa.CreateContext(); var cb = qb.CreateContext();
            int bad = 0;
            int liveStart = eng.Anchors.ScenarioBlockStart(Scenario.Live);
            for (int i = 0; i < 400; i++)
            {
                int s = rng.NextInt(g.NodeCount), t = rng.NextInt(g.NodeCount);
                int k = liveStart + rng.NextInt(eng.Anchors.ProfileCount);
                if (!Close(qa.Distance(ca, s, t, k), qb.Distance(cb, s, t, k))) bad++;
            }
            Check(bad == 0, $"partial-customization equivalence: {bad} mismatches");
        }

        private static void VerifyClosures(ulong seed)
        {
            var (city, eng) = BuildSmallCity(seed);
            var g = city.G;
            var rng = new SplitMix64(seed);
            var det = new SoftClosureDetector(g);
            var ctx = eng.Query.CreateContext();

            int CompareAllMetrics(SplitMix64 r, int rounds)
            {
                int bad = 0;
                for (int i = 0; i < rounds; i++)
                {
                    int s = r.NextInt(g.NodeCount), t = r.NextInt(g.NodeCount);
                    // hard closures define the weight in EVERY scenario, so
                    // every lane block must agree with the reference
                    int k = r.NextInt(eng.Anchors.MetricCount);
                    float dc = eng.Query.Distance(ctx, s, t, k);
                    float dr = Reference.Dijkstra(g, s, t, MetricWeight(g, eng.Anchors, k));
                    if (!Close(dc, dr)) bad++;
                }
                return bad;
            }

            var changed = new List<int>();
            var closedEdges = new List<int>();
            for (int i = 0; i < 30; i++)
            {
                int e = rng.NextInt(g.EdgeCount);
                det.SetHardClosed(e, true, changed);
                closedEdges.Add(e);
            }
            eng.RefreshClosures(changed);
            Check(CompareAllMetrics(rng, 300) == 0, "hard-closure routing mismatches (all scenarios)");

            // reopen round-trip: non-live lanes must recover too
            changed.Clear();
            foreach (var e in closedEdges) det.SetHardClosed(e, false, changed);
            eng.RefreshClosures(changed);
            Check(CompareAllMetrics(rng, 300) == 0, "reopen round-trip mismatches (all scenarios)");
        }

        private static void VerifyPaths(ulong seed)
        {
            var (city, eng) = BuildSmallCity(seed);
            var g = city.G;
            var ctx = eng.Query.CreateContext();
            var rng = new SplitMix64(seed);
            var path = new List<int>();
            int bad = 0;
            for (int i = 0; i < 300; i++)
            {
                int s = rng.NextInt(g.NodeCount), t = rng.NextInt(g.NodeCount);
                int k = rng.NextInt(eng.Anchors.MetricCount);
                float d = eng.Query.DistanceWithPath(ctx, s, t, k, path);
                if (float.IsPositiveInfinity(d)) continue;
                float sum = 0; int cur = s; bool ok = true;
                foreach (var e in path)
                {
                    if (g.Tail[e] != cur) { ok = false; break; }
                    cur = g.Head[e];
                    sum += eng.Anchors.EdgeWeight(g, e, k);
                }
                if (!ok || cur != t || !Close(sum, d)) bad++;
            }
            Check(bad == 0, $"unpacked paths: {bad} invalid");
        }

        private static void VerifyPotentialAStar(ulong seed)
        {
            var (city, eng) = BuildSmallCity(seed);
            var g = city.G;
            var q = eng.Query;
            var ctx = q.CreateContext();
            var astar = new PotentialAStar(eng.Metrics, q);
            var rng = new SplitMix64(seed);
            var path = new List<int>();
            int liveStart = eng.Anchors.ScenarioBlockStart(Scenario.Live);
            int bad = 0;
            for (int i = 0; i < 120; i++)
            {
                int s = rng.NextInt(g.NodeCount), t = rng.NextInt(g.NodeCount);
                int k = liveStart + rng.NextInt(eng.Anchors.ProfileCount);
                // penalized metric: base anchor metric with random edge penalties >= 1
                var penalized = new Dictionary<int, float>();
                for (int j = 0; j < 40; j++) penalized[rng.NextInt(g.EdgeCount)] = 1.5f;
                Func<int, float> w = e => eng.Anchors.EdgeWeight(g, e, k) * (penalized.TryGetValue(e, out var mu) ? mu : 1f);
                float da = astar.Search(ctx, s, t, new[] { (k, 1f) }, w, path);
                float dr = Reference.Dijkstra(g, s, t, w);
                if (!Close(da, dr)) bad++;
            }
            Check(bad == 0, $"potential-guided A* (penalized): {bad} mismatches");
        }

        private static void VerifyCertificates(ulong seed)
        {
            var (city, eng) = BuildSmallCity(seed);
            var g = city.G;
            var planner = new TripPlanner(eng.Metrics, eng.Query);
            var rng = new SplitMix64(seed);
            int badLb = 0, badCert = 0, badRepair = 0;
            int trials = 200;
            for (int i = 0; i < trials; i++)
            {
                int ci = rng.NextInt(city.Citizens.Length);
                var req = new TripRequest
                {
                    AgentId = i, Origin = rng.NextInt(g.NodeCount), Destination = rng.NextInt(g.NodeCount),
                    Alpha = city.Citizens[ci], Seed = seed ^ (ulong)i,
                    LongOrTransit = i % 4 == 0,
                };
                var plan = planner.PlanFixed(in req);
                if (plan.Unreachable) continue;
                var alpha = req.Alpha;
                float opt = Reference.Dijkstra(g, req.Origin, req.Destination,
                    e => AnchorGrid.AlphaWeightLive(g, e, in alpha));
                // LB must lower-bound the true alpha-optimum
                if (plan.CertLowerBound > opt * (1 + 1e-3f)) badLb++;
                // certified => best candidate is the true optimum
                if (plan.Certified && !Close(plan.BestAlphaCost, opt)) badCert++;
                // repair ran => best is exact
                if (plan.Repaired && !Close(plan.BestAlphaCost, opt)) badRepair++;
            }
            Check(badLb == 0, $"certificate lower bound violated {badLb}x");
            Check(badCert == 0, $"certified-but-not-optimal {badCert}x");
            Check(badRepair == 0, $"repaired-but-not-optimal {badRepair}x");
            var st = planner.Stats;
            Console.WriteLine($"  certificates: {st.CertifiedTrips}/{st.TripsPlanned} certified, {st.RepairSearches} repairs, {st.DisjointBackups} backups");
        }

        private static void VerifySuurballe(ulong seed)
        {
            var (city, eng) = BuildSmallCity(seed);
            var g = city.G;
            var rng = new SplitMix64(seed);
            var p1 = new List<int>(); var p2 = new List<int>();
            int bad = 0, found = 0;
            for (int i = 0; i < 100; i++)
            {
                int s = rng.NextInt(g.NodeCount), t = rng.NextInt(g.NodeCount);
                if (s == t) continue;
                if (!Suurballe.FindDisjointPair(g, s, t, e => g.TimeFree[e], p1, p2)) continue;
                found++;
                if (!ValidPath(g, p1, s, t) || !ValidPath(g, p2, s, t)) { bad++; continue; }
                var set = new HashSet<int>(p1);
                foreach (var e in p2) if (set.Contains(e)) { bad++; break; }
            }
            Check(bad == 0, $"Suurballe pairs: {bad} invalid");
            Check(found > 50, $"Suurballe rarely finds pairs ({found}/100) on a grid city");
        }

        private static bool ValidPath(Graph g, List<int> path, int s, int t)
        {
            int cur = s;
            foreach (var e in path)
            {
                if (g.Tail[e] != cur) return false;
                cur = g.Head[e];
            }
            return cur == t;
        }

        private static void VerifyBuckets(ulong seed)
        {
            var (city, eng) = BuildSmallCity(seed);
            var g = city.G;
            var ctx = eng.Query.CreateContext();
            int refMetric = eng.Anchors.MetricIndex(0, Scenario.Live); // pure-time live
            var buckets = DestinationBuckets.Build(eng.Query, ctx, city.Dests, refMetric);
            var rng = new SplitMix64(seed);
            var buf = new DestinationCandidate[8];
            int bad = 0;
            for (int i = 0; i < 60; i++)
            {
                int s = rng.NextInt(g.NodeCount);
                int cat = rng.NextInt(city.Dests.CategoryCount);
                int got = buckets.Scan(ctx, s, cat, buf, 4);
                // brute force best attraction-adjusted cost
                float best = float.PositiveInfinity;
                for (int d = 0; d < city.Dests.Count; d++)
                {
                    if (city.Dests.Category[d] != cat) continue;
                    float dist = Reference.Dijkstra(g, s, city.Dests.Node[d], MetricWeight(g, eng.Anchors, refMetric));
                    if (float.IsPositiveInfinity(dist)) continue;
                    best = Math.Min(best, dist - city.Dests.AttractionSeconds[d]);
                }
                if (float.IsPositiveInfinity(best)) { if (got != 0) bad++; continue; }
                if (got == 0 || !Close(buf[0].ScanCost, best, 2e-3f)) bad++;
            }
            Check(bad == 0, $"bucket scans: {bad} mismatches vs brute force");
        }

        private static void VerifyDispatch(ulong seed)
        {
            var (city, eng) = BuildSmallCity(seed);
            var g = city.G;
            var ctx = eng.Query.CreateContext();
            int refMetric = eng.Anchors.MetricIndex(0, Scenario.Live);
            var rng = new SplitMix64(seed);
            int fleetSize = 25;
            var fleet = FleetIndex.Create(eng.Query, fleetSize, refMetric);
            var pos = new int[fleetSize];
            for (int v = 0; v < fleetSize; v++) { pos[v] = rng.NextInt(g.NodeCount); fleet.PostVehicle(ctx, v, pos[v]); }
            // move a few vehicles (stale entries must be ignored)
            for (int v = 0; v < 8; v++) { pos[v] = rng.NextInt(g.NodeCount); fleet.PostVehicle(ctx, v, pos[v]); }
            int bad = 0;
            for (int i = 0; i < 50; i++)
            {
                int r = rng.NextInt(g.NodeCount);
                var (veh, dist) = fleet.Dispatch(ctx, r);
                float best = float.PositiveInfinity;
                for (int v = 0; v < fleetSize; v++)
                    best = Math.Min(best, Reference.Dijkstra(g, pos[v], r, MetricWeight(g, eng.Anchors, refMetric)));
                if (float.IsPositiveInfinity(best)) { if (veh >= 0) bad++; continue; }
                if (veh < 0 || !Close(dist, best)) bad++;
            }
            Check(bad == 0, $"dispatch: {bad} mismatches vs brute force");
        }

        private static void VerifySoftClosure()
        {
            var edges = new List<(int, int, float, float, float, float)> { (0, 1, 10f, 0f, 0f, 2f), (1, 0, 10f, 0f, 0f, 2f) };
            var g = Graph.Build(2, edges);
            var det = new SoftClosureDetector(g);
            var occ = new float[2]; var outf = new float[2];
            var jam = new float[] { 10f, 10f }; var rate = new float[] { 2f, 2f };
            var changed = new List<int>();
            // jam edge 0: full occupancy, zero outflow
            occ[0] = 10f; outf[0] = 0f;
            for (int i = 0; i < det.EnterTicks - 1; i++) det.Tick(occ, outf, jam, rate, changed);
            Check(changed.Count == 0 && g.ClosureMult[0] == 1f, "soft closure fired before hysteresis window");
            det.Tick(occ, outf, jam, rate, changed);
            Check(det.IsSoftClosed(0) && g.ClosureMult[0] >= det.MultStart, "soft closure did not enter after sustained jam");
            float m1 = g.ClosureMult[0];
            det.Tick(occ, outf, jam, rate, changed);
            Check(g.ClosureMult[0] > m1, "multiplier does not grow with jam duration");
            // recover: outflow resumes
            occ[0] = 3f; outf[0] = 1.5f;
            for (int i = 0; i < 60 && g.ClosureMult[0] > 1f; i++) det.Tick(occ, outf, jam, rate, changed);
            Check(g.ClosureMult[0] == 1f && !det.IsSoftClosed(0), "soft closure did not release after recovery");
        }

        private static void VerifyWindowedDetector(ulong seed)
        {
            // identical random occupancy/outflow traces through a full-scan and a
            // windowed detector (hot window = occ >= 0.5*jam) must produce
            // bit-identical closure multipliers every tick
            int E = 400;
            var edges = new List<(int, int, float, float, float, float)>();
            for (int e = 0; e < E; e++) edges.Add((e % 20, (e + 1) % 20, 10f, 0f, 0f, 2f));
            var gA = Graph.Build(20, edges);
            var gB = Graph.Build(20, edges);
            var detA = new SoftClosureDetector(gA);
            var detB = new SoftClosureDetector(gB);
            var rng = new SplitMix64(seed);
            var occ = new float[gA.EdgeCount]; var outf = new float[gA.EdgeCount];
            var jam = new float[gA.EdgeCount]; var rate = new float[gA.EdgeCount];
            for (int e = 0; e < gA.EdgeCount; e++) { jam[e] = 10f; rate[e] = 2f; }
            var chA = new List<int>(); var chB = new List<int>();
            var hot = new HashSet<int>();
            int mismatches = 0;
            for (int tick = 0; tick < 300; tick++)
            {
                for (int e = 0; e < gA.EdgeCount; e++)
                {
                    // random walk occupancy with sticky jams
                    float delta = (rng.NextFloat() - 0.45f) * 4f;
                    occ[e] = Math.Max(0f, Math.Min(10f, occ[e] + delta));
                    outf[e] = occ[e] > 8.5f ? 0.05f * rng.NextFloat() : 0.5f + 1.5f * rng.NextFloat();
                }
                hot.Clear();
                for (int e = 0; e < gA.EdgeCount; e++) if (occ[e] >= 0.5f * jam[e]) hot.Add(e);
                chA.Clear(); chB.Clear();
                detA.Tick(occ, outf, jam, rate, chA);
                detB.TickWindowed(hot, occ, outf, jam, rate, chB);
                for (int e = 0; e < gA.EdgeCount; e++)
                    if (gA.ClosureMult[e] != gB.ClosureMult[e]) mismatches++;
            }
            Check(mismatches == 0, $"windowed detector diverged from full scan ({mismatches} edge-ticks)");
            Check(detA.SoftClosedCount == detB.SoftClosedCount, "windowed detector SoftClosedCount diverged");
        }

        private static void VerifyEventDrivenBuckets(ulong seed)
        {
            var (city, eng) = BuildSmallCity(seed);
            var g = city.G;
            var ctx = eng.Query.CreateContext();
            int refMetric = eng.Anchors.MetricIndex(0, Scenario.Live);
            var buckets = DestinationBuckets.Build(eng.Query, ctx, city.Dests, refMetric);
            var rng = new SplitMix64(seed);

            // clustered congestion delta -> RefreshLive -> event-driven refresh
            var changed = new List<int>();
            int center = rng.NextInt(g.NodeCount);
            var q2 = new Queue<int>(); var seen = new HashSet<int> { center };
            q2.Enqueue(center);
            while (q2.Count > 0 && changed.Count < 120)
            {
                int v = q2.Dequeue();
                for (int e = g.OutStart[v]; e < g.OutStart[v + 1] && changed.Count < 120; e++)
                {
                    g.TimeLive[e] *= 1.6f;
                    changed.Add(e);
                    if (seen.Add(g.Head[e])) q2.Enqueue(g.Head[e]);
                }
            }
            eng.RefreshLive(changed);
            buckets.DriftSweepScans = int.MaxValue; // isolate the dirty-test path
            var (scanned, refreshed) = buckets.RefreshSliceEventDriven(ctx, city.Dests.Count, city.Dests.Count);
            buckets.RebuildBuckets();

            // ground truth: buckets rebuilt from scratch on the same weights
            var fresh = DestinationBuckets.Build(eng.Query, ctx, city.Dests, refMetric);
            var bufA = new DestinationCandidate[8];
            var bufB = new DestinationCandidate[8];
            int bad = 0;
            for (int i = 0; i < 80; i++)
            {
                int s = rng.NextInt(g.NodeCount);
                int cat = rng.NextInt(city.Dests.CategoryCount);
                int na = buckets.Scan(ctx, s, cat, bufA, 4);
                int nb = fresh.Scan(ctx, s, cat, bufB, 4);
                if (na != nb) { bad++; continue; }
                for (int j = 0; j < na; j++)
                    if (bufA[j].Dest != bufB[j].Dest || !Close(bufA[j].ScanCost, bufB[j].ScanCost, 2e-3f)) { bad++; break; }
            }
            Check(bad == 0, $"event-driven bucket refresh diverged from fresh build ({bad} scans)");
            Check(refreshed < city.Dests.Count, $"event-driven refresh degenerated to full sweep ({refreshed}/{city.Dests.Count})");
            Console.WriteLine($"  buckets: scanned={scanned} refreshed={refreshed}/{city.Dests.Count} after a localized congestion delta");
        }

        private static void VerifyClusterCache(ulong seed)
        {
            var city = SyntheticCity.Build(40, 40, 2000, 200, seed);
            var anchors = city.BuildAnchors(6);
            var eng = RoutingEngine.Build(city.G, anchors);
            var g = city.G;
            var planner = new TripPlanner(eng.Metrics, eng.Query);
            var cache = new ClusterCache(eng.CellPaths);
            planner.Cache = cache;
            var rng = new SplitMix64(seed);

            // --- surge onto ONE od pair (a real corridor-length trip):
            // quarantine must keep portfolios diverse ---
            int so = rng.NextInt(g.NodeCount), to = rng.NextInt(g.NodeCount);
            for (int tries = 0; tries < 500; tries++)
            {
                float dff = Reference.Dijkstra(g, so, to, e => g.TimeFree[e]);
                if (!float.IsPositiveInfinity(dff) && dff > 200f) break;
                so = rng.NextInt(g.NodeCount); to = rng.NextInt(g.NodeCount);
            }
            int thinServed = 0;
            for (int i = 0; i < 40; i++)
            {
                var req = new TripRequest
                {
                    AgentId = i, Origin = so, Destination = to,
                    Alpha = city.Citizens[rng.NextInt(city.Citizens.Length)],
                    Seed = seed ^ (ulong)(i * 977),
                };
                var plan = planner.PlanFixed(in req);
                if (plan.ServedFromCache && plan.Entry != null && !plan.Entry.Covered && plan.Alts.Count < 2)
                    thinServed++;
            }
            Check(thinServed == 0, $"quarantine served {thinServed} thin portfolios from an uncovered entry during a surge");

            // --- zonal traffic: cache must actually get hits, and cached plans
            // must stay certificate-sound ---
            int badLb = 0;
            for (int i = 0; i < 150; i++)
            {
                int s = rng.NextInt(g.NodeCount), t = rng.NextInt(g.NodeCount);
                for (int rep = 0; rep < 3; rep++) // repeat OD pairs => hits
                {
                    var req = new TripRequest
                    {
                        AgentId = i, Origin = s, Destination = t,
                        Alpha = city.Citizens[rng.NextInt(city.Citizens.Length)],
                        Seed = seed ^ (ulong)(i * 31 + rep),
                    };
                    var plan = planner.PlanFixed(in req);
                    if (plan.Unreachable || !plan.HasPlan) continue;
                    if (rep == 2 && plan.ServedFromCache)
                    {
                        var alphaLocal = req.Alpha;
                        float opt = Reference.Dijkstra(g, s, t, e => AnchorGrid.AlphaWeightLive(g, e, in alphaLocal));
                        if (plan.CertLowerBound > opt * (1 + 1e-3f)) badLb++;
                    }
                }
            }
            Check(planner.Stats.ServedFromCache > 50, $"cache rarely serves ({planner.Stats.ServedFromCache} hits) under repeating demand");
            Check(badLb == 0, $"cache-served plans with unsound certificate LB: {badLb}");

            // --- async repair: no synchronous repair searches; demand -> exploration -> donation ---
            Check(planner.Stats.RepairSearches == 0, "async mode still ran synchronous repair searches");
            long demand = planner.Stats.ExplorationDemandLogged;
            int tasks = planner.RunExploration(64);
            Check(demand == 0 || tasks > 0, $"exploration demand logged ({demand}) but no exploration tasks ran");
            Console.WriteLine($"  cache: hits={cache.Hits} misses={cache.Misses} entries={cache.EntryCount} " +
                              $"servedFromCache={planner.Stats.ServedFromCache} quarantineDiversions={planner.Stats.QuarantineDiversions} " +
                              $"explored={tasks} donated={planner.Stats.ExplorationDonated}");
        }

        private static void VerifyCityExport(ulong seed)
        {
            // The export format is the seam between the game and this harness; a
            // silent drift here would invalidate every calibration measurement,
            // so round-trip it exactly and prove corruption is caught.
            var city = SyntheticCity.Build(30, 30, 500, 60, seed);
            var exp = CityExport.FromGraph(city.G, city.JamCapacity);
            var rng = new SplitMix64(seed);
            for (int i = 0; i < 400; i++)
                exp.Traffic.Add(new CityExport.TrafficSample
                { Tick = rng.NextInt(500), Edge = rng.NextInt(city.G.EdgeCount), LiveSeconds = 1f + 20f * rng.NextFloat() });
            for (int i = 0; i < 300; i++)
                exp.Demand.Add(new CityExport.DemandSample
                {
                    Tick = rng.NextInt(500),
                    Origin = rng.NextInt(city.G.NodeCount), Dest = rng.NextInt(city.G.NodeCount),
                    Alpha = city.Citizens[rng.NextInt(city.Citizens.Length)],
                });

            var ms = new System.IO.MemoryStream();
            exp.Write(ms);
            ms.Position = 0;
            var back = CityExport.Read(ms);

            int bad = 0;
            if (back.NodeCount != exp.NodeCount || back.EdgeCount != exp.EdgeCount) bad++;
            for (int e = 0; e < exp.EdgeCount && bad == 0; e++)
                if (back.Tail[e] != exp.Tail[e] || back.Head[e] != exp.Head[e]
                    || back.TimeFree[e] != exp.TimeFree[e] || back.Money[e] != exp.Money[e]
                    || back.Comfort[e] != exp.Comfort[e] || back.Capacity[e] != exp.Capacity[e]
                    || back.JamCapacity[e] != exp.JamCapacity[e]) bad++;
            for (int v = 0; v < exp.NodeCount && bad == 0; v++)
                if (back.X[v] != exp.X[v] || back.Y[v] != exp.Y[v]) bad++;
            Check(bad == 0, $"city export round trip lost data ({bad} mismatches)");
            Check(back.Traffic.Count == exp.Traffic.Count && back.Demand.Count == exp.Demand.Count,
                "city export lost trace sections");
            for (int i = 0; i < exp.Demand.Count; i++)
                if (back.Demand[i].Origin != exp.Demand[i].Origin || back.Demand[i].Dest != exp.Demand[i].Dest
                    || back.Demand[i].Alpha.Time != exp.Demand[i].Alpha.Time) { bad++; break; }
            Check(bad == 0, "city export demand trace mismatch");

            // rebuilt graph must route identically to the original
            var g2 = back.ToGraph();
            var a2 = AnchorGrid.Build(new List<Preference>(), null, 3, new[] { Scenario.FreeFlow, Scenario.Live });
            var eng2 = RoutingEngine.Build(g2, a2);
            var ctx2 = eng2.Query.CreateContext();
            int routeBad = 0;
            for (int i = 0; i < 100; i++)
            {
                int s = rng.NextInt(g2.NodeCount), t = rng.NextInt(g2.NodeCount);
                float dc = eng2.Query.Distance(ctx2, s, t, 0);
                float dr = Reference.Dijkstra(g2, s, t, e => a2.EdgeWeight(g2, e, 0));
                if (!Close(dc, dr)) routeBad++;
            }
            Check(routeBad == 0, $"imported graph routes differently ({routeBad} mismatches)");

            // corruption must be caught, not silently imported
            var bytes = ms.ToArray();
            bytes[bytes.Length / 2] ^= 0xFF;
            bool caught = false;
            try { CityExport.Read(new System.IO.MemoryStream(bytes)); }
            catch (System.IO.InvalidDataException) { caught = true; }
            catch (Exception) { caught = true; }
            Check(caught, "corrupted export was accepted instead of rejected");

            bool truncCaught = false;
            try { CityExport.Read(new System.IO.MemoryStream(ms.ToArray(), 0, (int)(ms.Length / 2))); }
            catch (Exception) { truncCaught = true; }
            Check(truncCaught, "truncated export was accepted instead of rejected");
        }

        private static void VerifyParallelCustomization(ulong seed)
        {
            // Bit-identical, not approximately-equal. The claim is that atomic-min
            // level-parallel customization cannot reorder any float arithmetic:
            // min is exact and order-independent, and every addend is read from an
            // arc finalized at a strictly lower level. Exact equality is therefore
            // the correct assertion, and a tolerance here would hide a real bug.
            var city = SyntheticCity.Build(48, 48, 2500, 300, seed);
            var anchors = city.BuildAnchors(8);
            var eng = RoutingEngine.Build(city.G, anchors);
            var m = eng.Metrics;

            m.ResetAll();
            m.FullCustomize();
            var seqF = (float[])m.WFwd.Clone();
            var seqB = (float[])m.WBwd.Clone();

            // Several thread counts: a scheduling-order-dependent bug will not
            // reproduce at every width.
            foreach (int threads in new[] { 2, 3, 4, 8 })
            {
                m.ResetAll();
                m.FullCustomizeParallel(threads);
                int mismatches = 0;
                for (int i = 0; i < seqF.Length; i++)
                {
                    if (BitConverter.SingleToInt32Bits(m.WFwd[i]) != BitConverter.SingleToInt32Bits(seqF[i])) mismatches++;
                    if (BitConverter.SingleToInt32Bits(m.WBwd[i]) != BitConverter.SingleToInt32Bits(seqB[i])) mismatches++;
                }
                Check(mismatches == 0, $"parallel customization diverged from sequential on {mismatches} lanes at {threads} threads");
                // Guard against a VACUOUS pass: if every level of this graph is
                // narrower than MinLevelNodesToSplit, FullCustomizeParallel runs
                // fully serially and "bit-identical" proves nothing about the
                // atomic path. Assert the concurrent path actually executed.
                Check(m.LastParallelLevelsSplit > 0,
                    $"parallel path never engaged at {threads} threads — test graph has no level " +
                    $"≥ {CchMetrics.MinLevelNodesToSplit} nodes, so bit-identity is vacuous");
                // HONESTY LIMIT, measured by adversarial review: this guard shows
                // the concurrent path RAN, not that any arc was CONTENDED. On
                // this graph only ~6% of triangles land in splittable levels and
                // just 6-118 arcs are written from different chunks, so a build
                // with the atomic min deleted still passes ~199 runs in 200.
                // Bit-identity here therefore demonstrates the LEVEL SCHEDULE is
                // sound; it does NOT prove the atomic is necessary or correct.
                // Contention is asserted separately below, where it can be
                // counted deterministically rather than raced for.
            }

            // Levels must actually partition the nodes, or the sweep silently
            // skips work and still "passes" the equality check above.
            var c = eng.Skeleton;
            Check(c.LevelCount > 1, $"degenerate level decomposition ({c.LevelCount} levels)");
            Check(c.LevelStart[c.LevelCount] == c.NodeCount,
                $"levels cover {c.LevelStart[c.LevelCount]}/{c.NodeCount} nodes");
            var seen = new bool[c.NodeCount];
            int dup = 0;
            foreach (var v in c.LevelNodes) { if (seen[v]) dup++; seen[v] = true; }
            Check(dup == 0, $"{dup} nodes appear in more than one level");

            // The load-bearing invariant: a node's elimination-tree parent must
            // sit at a strictly higher level, else a level could read an arc that
            // is not yet final and the parallel sweep would be racy-by-design.
            var lvlOf = new int[c.NodeCount];
            for (int l = 0; l < c.LevelCount; l++)
                for (int i = c.LevelStart[l]; i < c.LevelStart[l + 1]; i++) lvlOf[c.LevelNodes[i]] = l;
            int inversions = 0;
            for (int v = 0; v < c.NodeCount; v++)
            {
                int p = c.EtParent[v];
                if (p >= 0 && lvlOf[p] <= lvlOf[v]) inversions++;
            }
            Check(inversions == 0, $"{inversions} elimination-tree edges do not increase level");

            // THE INVARIANT THE PARALLEL SWEEP ACTUALLY DEPENDS ON, counted
            // directly instead of raced for. Two claims:
            //  (a) no arc READ by a level-L node is WRITTEN by a level-L node —
            //      if this fails the schedule is unsound and no atomic can save
            //      it, because a reader could see a half-updated input;
            //  (b) some arc IS written by two different nodes of one level —
            //      if this never happens the atomic min is dead weight, and any
            //      bit-identity result says nothing about it.
            var writerLevel = new int[c.ArcCount];
            var writerNode = new int[c.ArcCount];
            for (int i = 0; i < c.ArcCount; i++) { writerLevel[i] = -1; writerNode[i] = -1; }
            int readWriteConflicts = 0, contendedArcs = 0;
            for (int lvl = 0; lvl < c.LevelCount; lvl++)
            {
                for (int i = c.LevelStart[lvl]; i < c.LevelStart[lvl + 1]; i++)
                {
                    int x = c.LevelNodes[i];
                    int s = c.UpStart[x], e = c.UpStart[x + 1];
                    for (int p2 = s; p2 < e; p2++)
                    {
                        // (a) arcs (x, ·) are this node's INPUTS
                        if (writerLevel[p2] == lvl) readWriteConflicts++;
                    }
                    for (int ii = s; ii < e; ii++)
                    {
                        int A = c.UpHead[ii];
                        int p3 = s, q = c.UpStart[A], qe = c.UpStart[A + 1];
                        while (p3 < e && q < qe)
                        {
                            int hb = c.UpHead[p3], hb2 = c.UpHead[q];
                            if (hb < hb2) p3++;
                            else if (hb > hb2) q++;
                            else
                            {
                                if (writerLevel[q] == lvl && writerNode[q] != x) contendedArcs++;
                                writerLevel[q] = lvl; writerNode[q] = x;
                                p3++; q++;
                            }
                        }
                    }
                }
            }
            Check(readWriteConflicts == 0,
                $"UNSOUND SCHEDULE: {readWriteConflicts} arcs are read and written within one level");
            Check(contendedArcs > 0,
                "no arc is written by two nodes of the same level on this graph — the atomic min is " +
                "untested here, so bit-identity cannot be read as evidence for it");
        }

        private static void VerifyBurstKernelPortability()
        {
            // Burst rejects every managed construct. Rather than discover that
            // during an in-game build (where the toolchain is not available to us
            // here), assert it against the kernel source directly, so the property
            // is checked on every run of the suite.
            string path = FindRepoFile("src/CS2Path.Core/BurstKernels.cs");
            Check(path != null, "BurstKernels.cs not found for portability lint");
            if (path == null) return;
            var lines = File.ReadAllLines(path);

            // Constructs Burst cannot compile. Deliberately matched on code only:
            // comments explain WHY these are banned and must not trip the lint.
            var banned = new (string pattern, string why)[]
            {
                ("System.Numerics", ".NET SIMD is invisible to Burst"),
                ("List<", "managed collection"),
                ("Dictionary<", "managed collection"),
                ("string ", "managed type"),
                ("new ", "allocation"),
                ("throw ", "Burst has no managed exceptions"),
                ("try", "no exception handling under Burst"),
                ("foreach", "iterators allocate/box"),
                ("class ", "reference type"),
                ("delegate", "function pointers only"),
                ("Console.", "no managed IO"),
                ("Math.", "use branchless/intrinsic forms; Burst prefers its own math"),
                ("[]", "managed array"),
            };
            // `static class` is REQUIRED, not forbidden: Burst compiles static
            // methods, which must live in a static class or struct. Only a
            // non-static (instantiable, reference-type) class is a violation.
            bool BannedHere(string code, string pat)
            {
                if (pat != "class ") return code.Contains(pat);
                int at = code.IndexOf("class ", StringComparison.Ordinal);
                if (at < 0) return false;
                return !code.Substring(0, at).Contains("static");
            }
            int violations = 0;
            var detail = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                string code = raw;
                int slash = code.IndexOf("//", StringComparison.Ordinal);
                if (slash >= 0) code = code.Substring(0, slash);
                string t = code.TrimStart();
                if (t.StartsWith("*") || t.StartsWith("///")) continue; // doc comment body
                foreach (var (pat, why) in banned)
                {
                    if (BannedHere(code, pat))
                    {
                        violations++;
                        detail.Add($"L{i + 1}: '{pat}' ({why})");
                    }
                }
            }
            Check(violations == 0,
                $"BurstKernels.cs contains {violations} Burst-hostile construct(s): {string.Join("; ", detail.Take(6))}");

            // And it must genuinely be the code the engine runs, not a dead
            // parallel copy that drifts out of sync.
            string metrics = FindRepoFile("src/CS2Path.Core/CchMetrics.cs");
            if (metrics != null)
            {
                string src = File.ReadAllText(metrics);
                Check(src.Contains("BurstKernels.FullCustomizeRange"), "FullCustomize no longer routes through the kernel");
                Check(src.Contains("BurstKernels.ContractLevelRange"), "parallel sweep no longer routes through the kernel");
                Check(!src.Contains("System.Numerics"), "CchMetrics still imports .NET SIMD");
            }
        }

        private static string? FindRepoFile(string rel)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string p = Path.Combine(dir.FullName, rel);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private static void VerifySpatialIndex(ulong seed)
        {
            var rng = new SplitMix64(seed);
            int n = 3000;
            var x = new float[n]; var y = new float[n];
            for (int i = 0; i < n; i++)
            {
                // clumpy distribution: half uniform, half in tight clusters,
                // mimicking road nodes along corridors
                if ((i & 1) == 0) { x[i] = rng.NextFloat() * 8000f; y[i] = rng.NextFloat() * 6000f; }
                else { x[i] = x[i - 1] + (rng.NextFloat() - 0.5f) * 40f; y[i] = y[i - 1] + (rng.NextFloat() - 0.5f) * 40f; }
            }
            var idx = new SpatialNodeIndex(x, y);
            int wrong = 0, rejects = 0, found = 0;
            for (int q = 0; q < 500; q++)
            {
                float px = rng.NextFloat() * 9000f - 500f, py = rng.NextFloat() * 7000f - 500f;
                float radius = q % 3 == 0 ? 60f : 250f;
                int got = idx.NearestWithin(px, py, radius);
                int want = -1; float best = radius * radius;
                for (int i = 0; i < n; i++)
                {
                    float dx = x[i] - px, dy = y[i] - py, d2 = dx * dx + dy * dy;
                    if (d2 <= best) { best = d2; want = i; }
                }
                if (got != want)
                {
                    // ties at identical distance are acceptable either way
                    float D2(int v) { float dx = x[v] - px, dy = y[v] - py; return dx * dx + dy * dy; }
                    if (got < 0 || want < 0 || Math.Abs(D2(got) - D2(want)) > 1e-3f) wrong++;
                }
                if (got < 0) rejects++; else found++;
            }
            Check(wrong == 0, $"spatial index disagreed with brute force on {wrong}/500 queries");
            Check(found > 0 && rejects > 0, $"degenerate spatial test coverage (found={found} rejects={rejects})");
        }

        private static void VerifyChangeLocality(ulong seed)
        {
            var city = SyntheticCity.Build(30, 30, 500, 100, seed);
            var g = city.G;
            var rng = new SplitMix64(seed);

            // pocket: all out-edges of a BFS ball -> one dominant component
            var ball = new List<int> { g.NodeCount / 2 };
            var seen = new HashSet<int> { g.NodeCount / 2 };
            for (int bi = 0; bi < ball.Count && ball.Count < 40; bi++)
                for (int e = g.OutStart[ball[bi]]; e < g.OutStart[ball[bi] + 1]; e++)
                    if (seen.Add(g.Head[e])) ball.Add(g.Head[e]);
            var pocket = new List<int>();
            foreach (int v in ball)
                for (int e = g.OutStart[v]; e < g.OutStart[v + 1]; e++) pocket.Add(e);
            ImportedCity.ClusterChangedEdges(g, pocket, out int pComp, out int pLargest, out float pShare);
            Check(pComp <= 3, $"pocket read as {pComp} components (expected ~1)");
            Check(pShare > 0.9f, $"pocket clustered share {pShare:P0} (expected >90%)");
            Check(pLargest >= pocket.Count / 2, "pocket largest component implausibly small");

            // scattered: far-apart single edges -> many singleton components
            var scattered = new List<int>();
            var usedNodes = new HashSet<int>();
            while (scattered.Count < 40)
            {
                int e = rng.NextInt(g.EdgeCount);
                if (!usedNodes.Add(g.Tail[e]) || !usedNodes.Add(g.Head[e])) continue;
                scattered.Add(e);
            }
            ImportedCity.ClusterChangedEdges(g, scattered, out int sComp, out _, out float sShare);
            Check(sComp == scattered.Count, $"scattered read as {sComp} components (expected {scattered.Count})");
            Check(sShare < 0.1f, $"scattered clustered share {sShare:P0} (expected ~0)");
        }

        private static void VerifyCacheV4(ulong seed)
        {
            var city = SyntheticCity.Build(36, 36, 1500, 150, seed);
            var g = city.G;
            var anchors = city.BuildAnchors(5);
            var eng = RoutingEngine.Build(g, anchors);
            var planner = new TripPlanner(eng.Metrics, eng.Query);
            var cache = new ClusterCache(eng.CellPaths);
            planner.Cache = cache;
            var rng = new SplitMix64(seed);
            var ods = new List<(int s, int t)>();
            for (int i = 0; i < 50; i++) ods.Add((rng.NextInt(g.NodeCount), rng.NextInt(g.NodeCount)));
            // 6 reps so every OD's entry crosses the ≥5-sample confidence
            // support on its own, without depending on the partitioner's cell
            // shapes making two ODs share an entry.
            int blendViolations = 0;
            for (int rep = 0; rep < 6; rep++)
                foreach (var (s, t) in ods)
                {
                    var plan = planner.PlanFixed(new TripRequest
                    {
                        Origin = s, Destination = t,
                        Alpha = city.Citizens[rng.NextInt(city.Citizens.Length)],
                        Seed = seed ^ (ulong)(s * 31 + t),
                    });
                    if (plan.HasPlan && !plan.Unreachable &&
                        (plan.StableBlend < planner.Cfg.TypicalBlend - 1e-4f ||
                         plan.StableBlend > planner.Cfg.StableBlendMax + 1e-4f))
                        blendViolations++;
                    if (plan.Entry != null)
                        cache.RecordRealized(plan.Entry, 0, 500f + 40f * rng.NextFloat(), 480f);
                }
            Check(blendViolations == 0, $"dynamic stable blend outside [floor, max] on {blendViolations} plans");

            // telemetry sanity
            var probe = cache.GetOrCreate(ods[0].s, ods[0].t, out _);
            Check(probe.RealizedCount[0] >= 3, "entry telemetry not accumulating");
            Check(probe.RealizedMean[0] > 400f && probe.RealizedMean[0] < 700f, $"realized mean off ({probe.RealizedMean[0]:0})");
            float conf = probe.PredictionConfidence(0);
            Check(conf > 0f && conf <= 1f, $"prediction confidence out of range ({conf})");
            Check(probe.RatioCount > 0 && probe.RatioEma > 0.5f && probe.RatioEma < 2f, $"ratio EMA off ({probe.RatioEma:0.00})");

            // gap #3a: identity remap (stable node ids) preserves route knowledge
            var eng2 = RoutingEngine.Build(g, anchors);
            var remapped = cache.RemapAfterRebuild(eng2.CellPaths, g.NodeCount);
            Check(remapped.EntryCount == cache.EntryCount,
                $"remap lost entries ({remapped.EntryCount}/{cache.EntryCount})");
            int lost = 0;
            foreach (var (s, t) in ods)
            {
                var e2 = remapped.GetOrCreate(s, t, out bool created);
                if (created || (e2.Arrivals == 0 && e2.Vias.Count == 0)) lost++;
            }
            Check(lost == 0, $"remapped cache lost knowledge for {lost} OD pairs");

            // gap #3b: RENUMBERING rebuild (the real road-project case): with the
            // old->new mapping, knowledge must be found at the PHYSICAL ODs under
            // their new ids, with vias translated — never range-checked stale ids
            var perm = new int[g.NodeCount];
            for (int v = 0; v < g.NodeCount; v++) perm[v] = v;
            var prng = new SplitMix64(seed + 7);
            for (int v = g.NodeCount - 1; v > 0; v--)
            {
                int j = prng.NextInt(v + 1);
                (perm[v], perm[j]) = (perm[j], perm[v]);
            }
            var permCells = new NestedDissection.CellPath[g.NodeCount];
            for (int v = 0; v < g.NodeCount; v++) permCells[perm[v]] = eng.CellPaths[v];
            var remap2 = cache.RemapAfterRebuild(permCells, g.NodeCount, perm);
            int lost2 = 0, staleVias = 0;
            foreach (var (s, t) in ods)
            {
                var e2 = remap2.GetOrCreate(perm[s], perm[t], out bool created);
                if (created || (e2.Arrivals == 0 && e2.Vias.Count == 0)) { lost2++; continue; }
            }
            var entryOld = cache.GetOrCreate(ods[0].s, ods[0].t, out _);
            var entryNew = remap2.GetOrCreate(perm[ods[0].s], perm[ods[0].t], out _);
            var expectVias = new HashSet<int>();
            foreach (var v in entryOld.Vias) expectVias.Add(perm[v.Via]);
            foreach (var v in entryNew.Vias) if (!expectVias.Contains(v.Via)) staleVias++;
            Check(lost2 == 0, $"renumbering remap lost knowledge for {lost2} physical OD pairs");
            Check(staleVias == 0, $"renumbering remap kept {staleVias} untranslated via ids");

            // gap #2: warmup governor bounds direct generations, thin service still plans
            var planner2 = new TripPlanner(eng.Metrics, eng.Query);
            var cache2 = new ClusterCache(eng.CellPaths) { DirectGenBudget = 3 };
            planner2.Cache = cache2;
            cache2.RefillDirectGenBudget();
            int planned = 0;
            for (int i = 0; i < 25; i++)
            {
                var plan = planner2.PlanFixed(new TripRequest
                {
                    Origin = rng.NextInt(g.NodeCount), Destination = rng.NextInt(g.NodeCount),
                    Alpha = city.Citizens[rng.NextInt(city.Citizens.Length)],
                    Seed = seed ^ (ulong)(i * 7919),
                });
                if (plan.HasPlan) planned++;
            }
            Check(planner2.Stats.DirectGenerations <= 3,
                $"governor exceeded budget ({planner2.Stats.DirectGenerations} direct gens)");
            Check(cache2.GovernorDenials > 0, "governor never engaged on a cold storm");
            Check(planned >= 23, $"thin service failed to plan under the governor ({planned}/25)");
            Console.WriteLine($"  v4: conf={conf:0.00} ratio={probe.RatioEma:0.00} remapped={remapped.EntryCount} governorDenials={cache2.GovernorDenials}");
        }

        private static void VerifySimSmoke(ulong seed)
        {
            var city = SyntheticCity.Build(28, 28, 1000, 100, seed);
            var anchors = city.BuildAnchors(5);
            var eng = RoutingEngine.Build(city.G, anchors);
            var sim = TrafficSim.Create(city.G, city.JamCapacity, SimMode.Rebuild, eng);
            var rng = new SplitMix64(seed);
            int n = city.G.NodeCount;
            for (int i = 0; i < 1500; i++)
            {
                sim.AddTrip(1 + rng.NextInt(150), new TripRequest
                {
                    AgentId = i, Origin = rng.NextInt(n), Destination = rng.NextInt(n),
                    Alpha = city.Citizens[rng.NextInt(city.Citizens.Length)],
                    Seed = seed ^ (ulong)(i * 7919),
                });
            }
            sim.Run(600);
            var st = sim.Planner!.Stats;
            var us = sim.Upd!.Stats;
            Console.WriteLine($"  smoke: {sim.FinishedTrips}/{sim.Trips.Count} arrived, reprices={us.Reprices}, switches={us.Switches}, " +
                              $"regens={us.Regenerations}, decisionEvents={us.DecisionEvents}, cacheServed={st.ServedFromCache}, syncFallbacks={st.SyncFallbacks}");
            Check(sim.FinishedTrips > sim.Trips.Count * 0.7, $"too few arrivals ({sim.FinishedTrips}/{sim.Trips.Count})");
            Check(us.Reprices > 0, "update engine never re-priced");
            Check(us.DecisionEvents > 0, "decision-point channel never fired");
        }

        private static bool Close(float a, float b, float tol = 1e-3f)
        {
            if (float.IsPositiveInfinity(a) && float.IsPositiveInfinity(b)) return true;
            if (float.IsPositiveInfinity(a) || float.IsPositiveInfinity(b)) return false;
            float scale = Math.Max(1f, Math.Max(Math.Abs(a), Math.Abs(b)));
            return Math.Abs(a - b) <= tol * scale;
        }
    }
}
