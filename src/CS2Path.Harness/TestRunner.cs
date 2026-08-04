using System;
using System.Collections.Generic;
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
            int blendViolations = 0;
            for (int rep = 0; rep < 3; rep++)
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
