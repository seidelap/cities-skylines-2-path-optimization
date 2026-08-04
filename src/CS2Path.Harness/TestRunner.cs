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
            Console.WriteLine("verify: end-to-end sim smoke test (switches, arrivals, no faults)...");
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
            Console.WriteLine($"  smoke: {sim.FinishedTrips}/{sim.Trips.Count} arrived, reprices={us.Reprices}, switches={us.Switches}, regens={us.Regenerations}, softClosed={sim.Detector!.SoftClosedCount}");
            Check(sim.FinishedTrips > sim.Trips.Count * 0.7, $"too few arrivals ({sim.FinishedTrips}/{sim.Trips.Count})");
            Check(us.Reprices > 0, "update engine never re-priced");
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
