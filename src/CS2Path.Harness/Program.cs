using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CS2Path.Core;

namespace CS2Path.Harness
{
    /// <summary>
    /// Standalone harness (plan §5): `verify` asserts algorithmic correctness
    /// against reference Dijkstra; `bench` measures Layer 0-3 costs against the
    /// §6 acceptance targets at 10^5-node scale; `herding` runs the
    /// synchronized-demand A/B; `sim` runs the full integrated simulation at
    /// 100k trips; `all` runs everything and writes RESULTS.md.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0] : "all";
            var opts = ParseOpts(args);
            ulong seed = (ulong)GetOpt(opts, "seed", 20260804);
            if (opts.TryGetValue("partitioner", out var part))
            {
                NestedDissection.UseInertialFlow = part != "geometric";
                Console.WriteLine($"partitioner: {(NestedDissection.UseInertialFlow ? "inertial-flow" : "geometric")}");
            }
            switch (cmd)
            {
                case "verify":
                    return TestRunner.RunAll(seed, out _);
                case "bench":
                {
                    var md = Bench(opts, seed);
                    File.WriteAllText("results-bench.md", md);
                    Console.WriteLine("\nwrote results-bench.md");
                    return 0;
                }
                case "herding":
                {
                    var md = Herding.RunAB(out _);
                    File.WriteAllText("results-herding.md", md);
                    Console.WriteLine("\nwrote results-herding.md");
                    return 0;
                }
                case "sim":
                {
                    var md = SimAtScale(opts, seed);
                    File.WriteAllText("results-sim.md", md);
                    Console.WriteLine("\nwrote results-sim.md");
                    return 0;
                }
                case "commute":
                {
                    var md = CommuteExperiment(opts, seed);
                    File.WriteAllText("results-commute.md", md);
                    Console.WriteLine("\nwrote results-commute.md");
                    return 0;
                }
                case "import":
                {
                    if (!opts.TryGetValue("file", out var path))
                    { Console.WriteLine("usage: harness import --file <city.cs2city> [--profiles N] [--queries N]"); return 2; }
                    var md = ImportedCity.Analyze(path, (int)GetOpt(opts, "profiles", 8), seed,
                                                  (int)GetOpt(opts, "queries", 20_000));
                    File.WriteAllText("results-import.md", md);
                    Console.WriteLine("\nwrote results-import.md");
                    return 0;
                }
                case "import-dimacs":
                {
                    if (!opts.TryGetValue("file", out var gr))
                    { Console.WriteLine("usage: harness import-dimacs --file <graph.gr> [--out city.cs2city]"); return 2; }
                    string dst = opts.TryGetValue("out", out var od) ? od
                        : Path.ChangeExtension(Path.GetFileName(gr), ".cs2city");
                    Console.WriteLine($"import-dimacs: parsing {gr} ...");
                    CityExport city2;
                    if (opts.TryGetValue("co", out var co) && opts.TryGetValue("bbox", out var bb))
                    {
                        var p = bb.Split(',');
                        if (p.Length != 4) { Console.WriteLine("--bbox lonMin,latMin,lonMax,latMax"); return 2; }
                        double lo1 = double.Parse(p[0], CultureInfo.InvariantCulture);
                        double la1 = double.Parse(p[1], CultureInfo.InvariantCulture);
                        double lo2 = double.Parse(p[2], CultureInfo.InvariantCulture);
                        double la2 = double.Parse(p[3], CultureInfo.InvariantCulture);
                        Console.WriteLine($"  bbox lon [{lo1},{lo2}] lat [{la1},{la2}] with real coordinates from {co}");
                        city2 = DimacsImport.LoadWithCoords(gr, co, lo1, la1, lo2, la2, seed);
                    }
                    else city2 = DimacsImport.Load(gr, seed);
                    city2.Validate();
                    using (var fs = File.Create(dst)) city2.Write(fs);
                    bool hasCoords = opts.ContainsKey("co") && opts.ContainsKey("bbox");
                    Console.WriteLine($"wrote {dst}: {city2.NodeCount:N0} nodes, {city2.EdgeCount:N0} directed edges " +
                                      $"({new FileInfo(dst).Length / 1e6:0.0} MB)");
                    Console.WriteLine("NOTE: topology is real; edge weights are synthesised (structure, not cost, is");
                    Console.WriteLine("      what A1 measures — the CCH skeleton is metric-independent).");
                    Console.WriteLine(hasCoords
                        ? "      Real coordinates present: inertial-flow nested dissection is used."
                        : "      No coordinates: nested dissection uses a BFS-level embedding as the flow");
                    if (!hasCoords)
                        Console.WriteLine("      projection — weaker seeding than real geometry, but still a min-cut.");
                    return 0;
                }
                case "export-synthetic":
                {
                    // Produces a .cs2city from the synthetic city — lets the whole
                    // export→transfer→import loop be exercised before the in-game
                    // exporter exists.
                    string outPath = opts.TryGetValue("out", out var o) ? o : "synthetic.cs2city";
                    int c2 = (int)GetOpt(opts, "cols", 120), r2 = (int)GetOpt(opts, "rows", 120);
                    var sc = SyntheticCity.Build(c2, r2, 5000, 300, seed);
                    var exp = CityExport.FromGraph(sc.G, sc.JamCapacity);
                    var erng = new SplitMix64(seed);
                    for (int i = 0; i < 5000; i++)
                        exp.Demand.Add(new CityExport.DemandSample
                        {
                            Tick = erng.NextInt(1000),
                            Origin = erng.NextInt(sc.G.NodeCount), Dest = erng.NextInt(sc.G.NodeCount),
                            Alpha = sc.Citizens[erng.NextInt(sc.Citizens.Length)],
                        });
                    // Synthetic congestion trace: drifting pockets emitted through
                    // the same delta-filter semantics the in-game sampler uses, so
                    // the A3 replay (grouping → partial customization → clustering
                    // stat) is exercisable end-to-end before a real export exists.
                    // Deliberately CLUSTERED — real locality is what the game
                    // session measures; this only proves the pipeline.
                    int traceTicks = (int)GetOpt(opts, "trace-ticks", 12);
                    if (traceTicks > 0)
                    {
                        var trng = new SplitMix64(seed ^ 0xA3);
                        const int pockets = 6;
                        var centers = new int[pockets];
                        for (int p = 0; p < pockets; p++) centers[p] = trng.NextInt(sc.G.NodeCount);
                        var lastRec = new float[sc.G.EdgeCount];
                        var ballNodes = new List<int>();
                        var seen = new HashSet<int>();
                        for (int t = 0; t < traceTicks; t++)
                        {
                            for (int p = 0; p < pockets; p++)
                            {
                                // pocket = out-edges of a BFS ball around the center
                                ballNodes.Clear(); seen.Clear();
                                ballNodes.Add(centers[p]); seen.Add(centers[p]);
                                for (int bi = 0; bi < ballNodes.Count && ballNodes.Count < 60; bi++)
                                {
                                    int v = ballNodes[bi];
                                    for (int e = sc.G.OutStart[v]; e < sc.G.OutStart[v + 1]; e++)
                                        if (seen.Add(sc.G.Head[e])) ballNodes.Add(sc.G.Head[e]);
                                }
                                float factor = 1.6f + 0.7f * (float)Math.Sin(0.9 * t + p);
                                foreach (int v in ballNodes)
                                    for (int e = sc.G.OutStart[v]; e < sc.G.OutStart[v + 1]; e++)
                                    {
                                        float val = exp.TimeFree[e] * factor;
                                        if (lastRec[e] > 0 && Math.Abs(val - lastRec[e]) <= 0.02f * lastRec[e]) continue;
                                        lastRec[e] = val;
                                        exp.Traffic.Add(new CityExport.TrafficSample { Tick = t, Edge = e, LiveSeconds = val });
                                    }
                                // slow drift so consecutive change sets overlap partially
                                if ((t & 1) == 1)
                                {
                                    int c = centers[p];
                                    if (sc.G.OutStart[c + 1] > sc.G.OutStart[c])
                                        centers[p] = sc.G.Head[sc.G.OutStart[c]];
                                }
                            }
                        }
                        Console.WriteLine($"  trace: {traceTicks} snapshots, {exp.Traffic.Count:N0} delta samples ({pockets} drifting pockets)");
                    }
                    using (var fs = File.Create(outPath)) exp.Write(fs);
                    Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length / 1e6:0.0} MB, " +
                                      $"{exp.NodeCount:N0} nodes, {exp.EdgeCount:N0} edges, {exp.Demand.Count:N0} trips)");
                    return 0;
                }
                case "all":
                {
                    var sb = new StringBuilder(Header());
                    int rc = TestRunner.RunAll(seed, out var verifyReport);
                    sb.AppendLine("## Correctness verification");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.Append(verifyReport);
                    sb.AppendLine("```");
                    sb.AppendLine();
                    sb.Append(Bench(opts, seed));
                    sb.AppendLine();
                    sb.Append(Herding.RunAB(out _));
                    sb.AppendLine();
                    sb.Append(SimAtScale(opts, seed));
                    File.WriteAllText("RESULTS.md", sb.ToString());
                    Console.WriteLine("\nwrote RESULTS.md");
                    return rc;
                }
                default:
                    Console.WriteLine("usage: harness [verify|bench|herding|sim|commute|all] [--key value ...]");
                    Console.WriteLine("       harness export-synthetic [--out city.cs2city]");
                    Console.WriteLine("       harness import --file <city.cs2city>   # replay a real exported city");
                    return 2;
            }
        }

        private static string Header()
            => $"# CS2 Trip Simulation Rebuild — Harness Results\n\nGenerated by `CS2Path.Harness` on {Environment.OSVersion.Platform}, {Environment.ProcessorCount} cores, .NET {Environment.Version}.\n\n";

        private static Dictionary<string, string> ParseOpts(string[] args)
        {
            var d = new Dictionary<string, string>();
            for (int i = 1; i < args.Length - 1; i++)
                if (args[i].StartsWith("--")) d[args[i].Substring(2)] = args[i + 1];
            return d;
        }

        private static long GetOpt(Dictionary<string, string> o, string k, long dflt)
            => o.TryGetValue(k, out var v) ? long.Parse(v) : dflt;

        private static double Pct(List<double> xs, double p)
        {
            if (xs.Count == 0) return double.NaN;
            var s = xs.OrderBy(x => x).ToList();
            int i = Math.Min(s.Count - 1, (int)(p * s.Count));
            return s[i];
        }

        // ==================================================================
        private static string Bench(Dictionary<string, string> opts, ulong seed)
        {
            int cols = (int)GetOpt(opts, "cols", 362), rows = (int)GetOpt(opts, "rows", 362);
            // k-sweep result (§4.8 "sit at the knee"): 12 profiles certified no
            // more trips than 8 (82.1% both) while costing ~40% per query, so
            // the knee for this alpha distribution is 8.
            int profiles = (int)GetOpt(opts, "profiles", 8);
            int nQueries = (int)GetOpt(opts, "queries", 100_000);
            int nTrips = (int)GetOpt(opts, "trips", 20_000);
            var sb = new StringBuilder();
            var sw = Stopwatch.StartNew();

            Console.WriteLine($"bench: building synthetic city {cols}x{rows}...");
            var city = SyntheticCity.Build(cols, rows, 50_000, 10_000, seed);
            var g = city.G;
            Console.WriteLine($"  nodes={g.NodeCount:N0} edges={g.EdgeCount:N0}");
            var anchors = city.BuildAnchors(profiles);
            int K = anchors.MetricCount;

            Console.WriteLine($"bench: Layer 0/1 build (K={K} metrics)...");
            var eng = RoutingEngine.Build(g, anchors);
            long weightBytes = (long)eng.Skeleton.ArcCount * K * 4 * 2;
            Console.WriteLine($"  order={eng.BuildOrderMs:0}ms skeleton={eng.BuildSkeletonMs:0}ms customize={eng.FullCustomizeMs:0.0}ms arcs={eng.Skeleton.ArcCount:N0} height={eng.Skeleton.TreeHeight}");

            sb.AppendLine("## Scale benchmark (plan §6 targets)");
            sb.AppendLine();
            sb.AppendLine($"Graph: **{g.NodeCount:N0} nodes / {g.EdgeCount:N0} directed lane-edges** (three road tiers, holes). Anchor grid: **{profiles} preference profiles × 2 scenarios = {K} metrics**.");
            sb.AppendLine();
            sb.AppendLine("### Layer 0 — structure (once per topology edit, async)");
            sb.AppendLine();
            sb.AppendLine("| stage | result |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| nested dissection order | {eng.BuildOrderMs:N0} ms |");
            sb.AppendLine($"| contraction (all shortcuts) | {eng.BuildSkeletonMs:N0} ms |");
            sb.AppendLine($"| chordal arcs | {eng.Skeleton.ArcCount:N0} ({(double)eng.Skeleton.ArcCount / (g.EdgeCount / 2):0.0}x undirected edges) |");
            sb.AppendLine($"| elimination tree height | {eng.Skeleton.TreeHeight} |");
            sb.AppendLine($"| multi-metric weight memory | {weightBytes / 1e6:N0} MB ({K} metrics, fwd+bwd) |");
            sb.AppendLine();

            // --- customization ---
            Console.WriteLine("bench: full + partial customization...");
            var rng = new SplitMix64(seed + 100);
            sw.Restart();
            eng.Metrics.ResetAll();
            eng.Metrics.FullCustomize();
            double fullMs = sw.Elapsed.TotalMilliseconds;

            // Level-parallel A/B. Out of game this measures the SCHEDULE, not
            // Burst codegen: same kernel, same arithmetic, dispatched across
            // elimination-tree levels. In-game the identical decomposition
            // becomes one IJobParallelFor per level and additionally gets
            // Burst's vectorizer, so this ratio is a floor on the in-game win,
            // not an estimate of it.
            // Split the two halves of "customization". ResetAll seeds every arc
            // lane from its original edge weight; FullCustomize is the triangle
            // sweep. They have completely different bottlenecks — seeding is
            // scattered random access over edge attributes, the sweep is a
            // streaming min-plus pass — so a single combined number hides which
            // one any given change actually moved.
            var resetMs = new List<double>();
            var sweepMs = new List<double>();
            for (int rep = 0; rep < 5; rep++)
            {
                sw.Restart();
                eng.Metrics.ResetAll();
                resetMs.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                eng.Metrics.FullCustomize();
                sweepMs.Add(sw.Elapsed.TotalMilliseconds);
            }
            resetMs.Sort(); sweepMs.Sort();
            double resetMed = resetMs[resetMs.Count / 2], sweepMed = sweepMs[sweepMs.Count / 2];
            Console.WriteLine($"  split: ResetAll median {resetMed:N0} ms (spread {resetMs[0]:N0}-{resetMs[resetMs.Count - 1]:N0}) | " +
                              $"sweep median {sweepMed:N0} ms (spread {sweepMs[0]:N0}-{sweepMs[sweepMs.Count - 1]:N0})");

            // Thread scaling, not just a single parallel number: if the sweep is
            // memory-bandwidth-bound rather than compute-bound, time flattens
            // early and no decomposition will fix it. That distinction decides
            // whether the remaining in-game win comes from more threads or from
            // Burst's codegen and tighter data layout, so it is worth measuring
            // rather than assuming.
            // APPLES TO APPLES. Two earlier bugs made every ratio in this
            // section wrong, both found by adversarial review:
            //  (1) the numerator was fullMs, which includes ResetAll (~190 ms),
            //      while the parallel timings exclude it — inflating every
            //      speedup by roughly that ratio;
            //  (2) threads==1 routes 100% of the work through the fast
            //      non-atomic kernel while threads>=2 sends ~95% of it through
            //      the ~2x slower atomic kernel, so a 1t-vs-2t "scaling" curve
            //      compared two different computations.
            // The baseline is therefore the SWEEP ONLY (sweepMed, rank order),
            // and each parallel point is reported against it.
            var scaling = new List<(int t, double ms)>();
            foreach (int t in new[] { 1, 2, 4, Environment.ProcessorCount })
            {
                if (scaling.Exists(s => s.t == t)) continue;
                var reps = new List<double>();
                for (int rep = 0; rep < 3; rep++)
                {
                    eng.Metrics.ResetAll();           // outside the stopwatch, as before
                    sw.Restart();
                    eng.Metrics.FullCustomizeParallel(t);
                    reps.Add(sw.Elapsed.TotalMilliseconds);
                }
                reps.Sort();
                scaling.Add((t, reps[reps.Count / 2]));
            }
            double fullParMs = scaling[scaling.Count - 1].ms;
            string scalingStr = string.Join(", ", scaling.ConvertAll(s =>
                $"{s.t}t={s.ms:N0}ms({sweepMed / Math.Max(0.001, s.ms):0.00}x)"));
            Console.WriteLine($"  sweep baseline (rank order, non-atomic) {sweepMed:N0} ms | level-parallel {scalingStr} " +
                              $"| {eng.Skeleton.LevelCount} levels, {Environment.ProcessorCount} cores");
            Console.WriteLine($"  NOTE: 1t uses the non-atomic kernel; 2t+ uses the atomic kernel for " +
                              $"~95% of triangles, so 1t is NOT the same computation as 2t+.");

            // Task decomposition (dissection-cell subtrees): rank-order inside
            // each task, atomics only on enclosing-separator writes.
            var taskScaling = new List<(int t, double ms)>();
            foreach (int t in new[] { 1, 2, 4, Environment.ProcessorCount })
            {
                if (taskScaling.Exists(s => s.t == t)) continue;
                var reps = new List<double>();
                for (int rep = 0; rep < 3; rep++)
                {
                    eng.Metrics.ResetAll();
                    sw.Restart();
                    eng.Metrics.FullCustomizeParallelTasks(t);
                    reps.Add(sw.Elapsed.TotalMilliseconds);
                }
                reps.Sort();
                taskScaling.Add((t, reps[reps.Count / 2]));
            }
            double fullTaskMs = taskScaling[taskScaling.Count - 1].ms;
            string taskStr = string.Join(", ", taskScaling.ConvertAll(s =>
                $"{s.t}t={s.ms:N0}ms({sweepMed / Math.Max(0.001, s.ms):0.00}x)"));
            int nT = eng.Skeleton.TaskLo?.Length ?? 0;
            int p2 = eng.Skeleton.Phase2Ranks?.Length ?? 0;
            Console.WriteLine($"  task-parallel {taskStr} | {nT} tasks + {p2} phase-2 separator nodes");
            // Leave the engine in the canonical sequential state for everything
            // that follows, so no later measurement inherits the parallel run.
            eng.Metrics.ResetAll();
            eng.Metrics.FullCustomize();

            // Typical congestion deltas: ±10% multiplicative drift on the live
            // time of spatially random edges — what a 3%-threshold EMA feed
            // produces each refresh. One large-shock round is reported
            // separately (mass closure / event churn).
            var changed = new List<int>();
            var partial100 = new List<double>(); var partial1000 = new List<double>();
            long arcs100 = 0, arcs1000 = 0;
            const int Rounds = 12;
            for (int round = 0; round < Rounds; round++)
            {
                foreach (var (list, count) in new[] { (partial100, 100), (partial1000, 1000) })
                {
                    changed.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        int e = rng.NextInt(g.EdgeCount);
                        float mult = 0.9f + 0.2f * rng.NextFloat();
                        g.TimeLive[e] = Math.Max(g.TimeFree[e] * 0.5f, Math.Min(g.TimeFree[e] * 6f, g.TimeLive[e] * mult));
                        changed.Add(e);
                    }
                    sw.Restart();
                    eng.RefreshLive(changed);
                    list.Add(sw.Elapsed.TotalMilliseconds);
                    if (count == 100) arcs100 += eng.Metrics.LastPartialArcsRecomputed;
                    else arcs1000 += eng.Metrics.LastPartialArcsRecomputed;
                }
            }
            // clustered case: congestion is spatial — perturb ~150 edges around
            // one center (BFS ball), the shape a real refresh delta has
            var partialClustered = new List<double>();
            long arcsClustered = 0;
            for (int round = 0; round < Rounds; round++)
            {
                changed.Clear();
                int center = rng.NextInt(g.NodeCount);
                var q2 = new Queue<int>(); var seen = new HashSet<int> { center };
                q2.Enqueue(center);
                while (q2.Count > 0 && changed.Count < 150)
                {
                    int v = q2.Dequeue();
                    for (int e = g.OutStart[v]; e < g.OutStart[v + 1] && changed.Count < 150; e++)
                    {
                        float mult = 0.9f + 0.2f * rng.NextFloat();
                        g.TimeLive[e] = Math.Max(g.TimeFree[e] * 0.5f, Math.Min(g.TimeFree[e] * 6f, g.TimeLive[e] * mult));
                        changed.Add(e);
                        if (seen.Add(g.Head[e])) q2.Enqueue(g.Head[e]);
                    }
                }
                sw.Restart();
                eng.RefreshLive(changed);
                partialClustered.Add(sw.Elapsed.TotalMilliseconds);
                arcsClustered += eng.Metrics.LastPartialArcsRecomputed;
            }

            // worst case: 1000 edges jump 0.8x-2.4x at once
            changed.Clear();
            for (int i = 0; i < 1000; i++)
            {
                int e = rng.NextInt(g.EdgeCount);
                g.TimeLive[e] = g.TimeFree[e] * (0.8f + 1.6f * rng.NextFloat());
                changed.Add(e);
            }
            sw.Restart();
            eng.RefreshLive(changed);
            double shockMs = sw.Elapsed.TotalMilliseconds;
            long shockArcs = eng.Metrics.LastPartialArcsRecomputed;

            sb.AppendLine("### Layer 1 — customization (per traffic refresh)");
            sb.AppendLine();
            sb.AppendLine("| operation | time | §6 target |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine($"| full customization, all {K} metrics (sequential) | {fullMs:N0} ms | < 10 ms (Burst/SIMD budget) |");
            sb.AppendLine($"| full customization, level-parallel thread scaling ({eng.Skeleton.LevelCount} levels) | {scalingStr} | flattening ⇒ bandwidth-bound, not thread-starved |");
            sb.AppendLine($"| partial, 100 edges ±10% drift, scattered (live lanes) | median {Pct(partial100, 0.5):0.00} ms, p99 {Pct(partial100, 0.99):0.00} ms ({arcs100 / Rounds:N0} arcs) | < 1 ms |");
            sb.AppendLine($"| partial, 150 edges ±10% drift, clustered (one congestion pocket) | median {Pct(partialClustered, 0.5):0.00} ms, p99 {Pct(partialClustered, 0.99):0.00} ms ({arcsClustered / Rounds:N0} arcs) | < 1 ms |");
            sb.AppendLine($"| partial, 1000 edges ±10% drift, scattered (live lanes) | median {Pct(partial1000, 0.5):0.00} ms, p99 {Pct(partial1000, 0.99):0.00} ms ({arcs1000 / Rounds:N0} arcs) | — |");
            sb.AppendLine($"| partial, 1000-edge large shock (0.8-2.4x) | {shockMs:N0} ms ({shockArcs:N0} arcs) | worst case, amortizable |");
            sb.AppendLine();

            // --- queries ---
            Console.WriteLine($"bench: {nQueries:N0} point-to-point queries (4 threads)...");
            int liveStart = anchors.ScenarioBlockStart(CS2Path.Core.Scenario.Live);
            var pairs = new (int s, int t, int k)[nQueries];
            for (int i = 0; i < nQueries; i++)
                pairs[i] = (rng.NextInt(g.NodeCount), rng.NextInt(g.NodeCount), liveStart + rng.NextInt(profiles));
            // warmup
            {
                var wctx = eng.Query.CreateContext();
                for (int i = 0; i < 2000; i++) eng.Query.Distance(wctx, pairs[i].s, pairs[i].t, pairs[i].k);
            }
            int nThreads = Math.Min(4, Environment.ProcessorCount);
            var lat = new List<double>[nThreads];
            sw.Restart();
            Parallel.For(0, nThreads, th =>
            {
                var ctx = eng.Query.CreateContext();
                var mine = new List<double>(nQueries / nThreads + 1);
                for (int i = th; i < nQueries; i += nThreads)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    eng.Query.Distance(ctx, pairs[i].s, pairs[i].t, pairs[i].k);
                    mine.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
                }
                lat[th] = mine;
            });
            double wallS = sw.Elapsed.TotalSeconds;
            var allLat = lat.SelectMany(x => x).ToList();

            // spot correctness at scale
            int bad = 0;
            {
                var ctx = eng.Query.CreateContext();
                for (int i = 0; i < 300; i++)
                {
                    var (s, t, k) = pairs[i];
                    float dc = eng.Query.Distance(ctx, s, t, k);
                    float dr = Reference.Dijkstra(g, s, t, e => anchors.EdgeWeight(g, e, k));
                    bool close = float.IsPositiveInfinity(dc) && float.IsPositiveInfinity(dr)
                                 || Math.Abs(dc - dr) <= 1e-3f * Math.Max(1f, Math.Max(dc, dr));
                    if (!close) bad++;
                }
            }

            // vanilla-style Dijkstra cost on the same graph
            Console.WriteLine("bench: reference Dijkstra timing (the vanilla per-query cost)...");
            var djLat = new List<double>();
            for (int i = 0; i < 150; i++)
            {
                var (s, t, k) = pairs[i];
                long t0 = Stopwatch.GetTimestamp();
                Reference.Dijkstra(g, s, t, e => anchors.EdgeWeight(g, e, k));
                djLat.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
            }

            sb.AppendLine("### Layer 2 — point-to-point queries (per trip, live metric)");
            sb.AppendLine();
            sb.AppendLine("| measure | CCH (this mod) | reference Dijkstra (vanilla-style) |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine($"| median latency | {Pct(allLat, 0.5):0.0} µs | {Pct(djLat, 0.5):N0} µs |");
            sb.AppendLine($"| mean latency | {allLat.Average():0.0} µs | {djLat.Average():N0} µs |");
            sb.AppendLine($"| p99 latency | {Pct(allLat, 0.99):0.0} µs | {Pct(djLat, 0.99):N0} µs |");
            sb.AppendLine($"| throughput ({nThreads} threads) | {nQueries / wallS:N0} queries/s | — |");
            sb.AppendLine($"| correctness spot-check | {300 - bad}/300 exact | (reference) |");
            sb.AppendLine();
            sb.AppendLine($"**Speedup: {djLat.Average() / allLat.Average():N0}x per query** (plan §2 asks ~3 orders of magnitude; §6 target p99 < 20 µs).");
            sb.AppendLine();
            Console.WriteLine($"  CCH mean={allLat.Average():0.0}us p99={Pct(allLat, 0.99):0.0}us | Dijkstra mean={djLat.Average():N0}us | correct {300 - bad}/300");

            // --- portfolios ---
            Console.WriteLine($"bench: {nTrips:N0} full trip plans (portfolio + certificate), {nThreads} threads...");
            var trips = new TripRequest[nTrips];
            for (int i = 0; i < nTrips; i++)
            {
                int s = rng.NextInt(g.NodeCount), t = rng.NextInt(g.NodeCount);
                trips[i] = new TripRequest
                {
                    AgentId = i, Origin = s, Destination = t,
                    Alpha = city.Citizens[rng.NextInt(city.Citizens.Length)],
                    Seed = seed ^ (ulong)(i * 6364136223846793005L),
                    LongOrTransit = i % 20 == 0,
                };
            }
            var telemetry = new Telemetry();
            var planLat = new List<double>[nThreads];
            var altCounts = new List<double>[nThreads];
            sw.Restart();
            Parallel.For(0, nThreads, th =>
            {
                var planner = new TripPlanner(eng.Metrics, eng.Query);
                planner.Stats.RepairTimesUs = new List<double>();
                var mine = new List<double>(nTrips / nThreads + 1);
                var alts = new List<double>(nTrips / nThreads + 1);
                for (int i = th; i < nTrips; i += nThreads)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    var plan = planner.PlanFixed(in trips[i]);
                    mine.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
                    if (plan.HasPlan) alts.Add(plan.Alts.Count);
                }
                lock (telemetry) telemetry.AddFrom(planner.Stats);
                planLat[th] = mine; altCounts[th] = alts;
            });
            double planWallS = sw.Elapsed.TotalSeconds;
            var allPlan = planLat.SelectMany(x => x).ToList();
            var allAlts = altCounts.SelectMany(x => x).ToList();
            double certFrac = (double)telemetry.CertifiedTrips / Math.Max(1, telemetry.TripsPlanned);
            long uncert = telemetry.TripsPlanned - telemetry.CertifiedTrips;
            double meanGap = telemetry.CertGapSum / Math.Max(1, uncert + telemetry.RepairSearches);
            var repairs = telemetry.RepairTimesUs ?? new List<double>();

            sb.AppendLine("### Layer 2 — full trip planning (portfolio + choice + certificate)");
            sb.AppendLine();
            sb.AppendLine("| measure | value | §6 target |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine($"| plan latency median / p99 | {Pct(allPlan, 0.5):0.0} µs / {Pct(allPlan, 0.99):N0} µs | — |");
            sb.AppendLine($"| planning throughput ({nThreads} threads) | {nTrips / planWallS:N0} trips/s | — |");
            sb.AppendLine($"| mean portfolio size | {allAlts.DefaultIfEmpty(0).Average():0.0} alternatives | 3-5 |");
            sb.AppendLine($"| certified-exact fraction | {certFrac:P1} | ≥ 90% |");
            sb.AppendLine($"| mean certified gap (uncertified tail) | {meanGap:P2} | < 1% |");
            sb.AppendLine($"| repair searches | {telemetry.RepairSearches:N0} ({(double)telemetry.RepairSearches / Math.Max(1, telemetry.TripsPlanned):P1} of trips, {telemetry.RepairBudgetExhausted:N0} hit budget) | 2-10% |");
            sb.AppendLine($"| repair p99 latency | {Pct(repairs, 0.99):N0} µs | < 500 µs |");
            sb.AppendLine($"| Suurballe backups | {telemetry.DisjointBackups:N0} | — |");
            sb.AppendLine($"| unreachable trips | {telemetry.UnreachableTrips:N0} | no increase vs vanilla (= genuine) |");
            sb.AppendLine();
            Console.WriteLine($"  plan median={Pct(allPlan, 0.5):0.0}us certified={certFrac:P1} repairs={telemetry.RepairSearches} repairP99={Pct(repairs, 0.99):N0}us");

            // --- §4.9 route-knowledge cache under zonal demand ---
            Console.WriteLine("bench: §4.9 cluster cache under zonal demand...");
            {
                var planner = new TripPlanner(eng.Metrics, eng.Query);
                var cache = new ClusterCache(eng.CellPaths);
                planner.Cache = cache;
                planner.Stats.SyncFallbackTimesUs = new List<double>();
                // demand model: 65% of trips among Z zone pairs (commuter flows),
                // 35% uniform — cache hit rate is a demand-locality property
                int Z = 40;
                var zones = new (int o, int d)[Z];
                for (int z = 0; z < Z; z++) zones[z] = (rng.NextInt(g.NodeCount), rng.NextInt(g.NodeCount));
                int[] NearbyPool(int center)
                {
                    var pool = new List<int> { center };
                    var q2 = new Queue<int>(); q2.Enqueue(center);
                    var seen = new HashSet<int> { center };
                    while (q2.Count > 0 && pool.Count < 120)
                    {
                        int v = q2.Dequeue();
                        for (int e = g.OutStart[v]; e < g.OutStart[v + 1]; e++)
                            if (seen.Add(g.Head[e])) { pool.Add(g.Head[e]); q2.Enqueue(g.Head[e]); }
                    }
                    return pool.ToArray();
                }
                var oPools = new int[Z][]; var dPools = new int[Z][];
                for (int z = 0; z < Z; z++) { oPools[z] = NearbyPool(zones[z].o); dPools[z] = NearbyPool(zones[z].d); }

                int nCacheTrips = 30_000;
                var warmLat = new List<double>(nCacheTrips); var coldLat = new List<double>(nCacheTrips);
                long altBytes = 0, pathBytes = 0, planCount = 0;
                for (int i = 0; i < nCacheTrips; i++)
                {
                    int s2, t2;
                    if (rng.NextFloat() < 0.65f)
                    {
                        int z = rng.NextInt(Z);
                        s2 = oPools[z][rng.NextInt(oPools[z].Length)];
                        t2 = dPools[z][rng.NextInt(dPools[z].Length)];
                    }
                    else { s2 = rng.NextInt(g.NodeCount); t2 = rng.NextInt(g.NodeCount); }
                    var req = new TripRequest
                    {
                        AgentId = i, Origin = s2, Destination = t2,
                        Alpha = city.Citizens[rng.NextInt(city.Citizens.Length)],
                        Seed = seed ^ (ulong)(i * 48271),
                    };
                    long t0 = Stopwatch.GetTimestamp();
                    var plan = planner.PlanFixed(in req);
                    double us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
                    (plan.ServedFromCache ? warmLat : coldLat).Add(us);
                    if (plan.HasPlan)
                    {
                        planCount++;
                        altBytes += 32 + plan.Alts.Count * 24; // cursor: held-branch view + stamps
                        pathBytes += plan.ChosenEdgePath.Count * 4;
                    }
                    // off-critical-path exploration trickle, as the sim runs it
                    if (i % 200 == 199) planner.RunExploration(4);
                }
                var st2 = planner.Stats;
                double hitRate = (double)st2.ServedFromCache / Math.Max(1, st2.ServedFromCache + st2.DirectGenerations);
                sb.AppendLine("### Layer 2 v2 — §4.9 route-knowledge cache (zonal demand, 65% on 40 zone pairs)");
                sb.AppendLine();
                sb.AppendLine("| measure | value | design claim |");
                sb.AppendLine("|---|---|---|");
                sb.AppendLine($"| cache-served share | {hitRate:P1} ({st2.ServedFromCache:N0} of {st2.ServedFromCache + st2.DirectGenerations:N0}) | high under commuter locality |");
                sb.AppendLine($"| warm plan latency (cache-served) | median {Pct(warmLat, 0.5):N0} µs, p99 {Pct(warmLat, 0.99):N0} µs | — |");
                sb.AppendLine($"| cold plan latency (direct generation, seeds entry) | median {Pct(coldLat, 0.5):N0} µs, p99 {Pct(coldLat, 0.99):N0} µs | demoted to seeding fallback |");
                sb.AppendLine($"| warm/cold speedup | {Pct(coldLat, 0.5) / Math.Max(1, Pct(warmLat, 0.5)):0.0}x median | — |");
                sb.AppendLine($"| quarantine diversions (thin entry -> direct gen) | {st2.QuarantineDiversions:N0} | surge never funneled onto one path |");
                sb.AppendLine($"| sync fallbacks (portfolio collapse) | {st2.SyncFallbacks:N0}, p99 {(st2.SyncFallbackTimesUs!.Count > 0 ? Pct(st2.SyncFallbackTimesUs, 0.99) : 0):N0} µs | p99 < 500 µs (§6) |");
                sb.AppendLine($"| exploration | {st2.ExplorationTasks:N0} tasks, {st2.ExplorationDonated:N0} donated vias, {st2.ExplorationTimeUs / Math.Max(1, st2.ExplorationTasks):N0} µs/task | off the critical path |");
                sb.AppendLine($"| certificate gaps -> exploration demand | {st2.ExplorationDemandLogged:N0} (no synchronous repairs: {st2.RepairSearches}) | §4.7 v2 |");
                sb.AppendLine($"| cache footprint | {cache.EntryCount:N0} entries, {cache.EstimatedBytes() / 1e6:0.0} MB | tens of MB at 10⁴-10⁵ entries |");
                sb.AppendLine($"| retained per-agent cursor | {altBytes / Math.Max(1, planCount):N0} B (+{pathBytes / Math.Max(1, planCount):N0} B driven geometry) | tens of bytes + driven route |");
                sb.AppendLine();
                Console.WriteLine($"  hit={hitRate:P1} warm={Pct(warmLat, 0.5):N0}us cold={Pct(coldLat, 0.5):N0}us entries={cache.EntryCount} cacheMB={cache.EstimatedBytes() / 1e6:0.0} cursorB={altBytes / Math.Max(1, planCount)}");
            }

            // --- re-pricing ---
            Console.WriteLine("bench: via-node re-pricing...");
            {
                var planner = new TripPlanner(eng.Metrics, eng.Query);
                var plans = new List<TripPlan>();
                for (int i = 0; i < 300; i++)
                {
                    var p = planner.PlanFixed(in trips[i]);
                    if (p.HasPlan) plans.Add(p);
                }
                var repLat = new List<double>(20000);
                for (int i = 0; i < 20000; i++)
                {
                    var p = plans[i % plans.Count];
                    var alt = p.Alts[i % p.Alts.Count];
                    long t0 = Stopwatch.GetTimestamp();
                    planner.RepriceAlternative(p.Origin, in alt, p.NearestProfile);
                    repLat.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
                }
                sb.AppendLine("### Layer 4 — via-node re-pricing (per alternative, two CCH queries)");
                sb.AppendLine();
                sb.AppendLine($"median {Pct(repLat, 0.5):0.0} µs, p99 {Pct(repLat, 0.99):0.0} µs — a 5-alternative portfolio re-prices in ~{5 * Pct(repLat, 0.5):0} µs.");
                sb.AppendLine();
            }

            // --- Layer 3 buckets + dispatch ---
            Console.WriteLine("bench: destination buckets + dispatch...");
            {
                var ctx = eng.Query.CreateContext();
                int refMetric = anchors.MetricIndex(0, CS2Path.Core.Scenario.Live);
                sw.Restart();
                var buckets = DestinationBuckets.Build(eng.Query, ctx, city.Dests, refMetric);
                double buildMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                buckets.RefreshSlice(ctx, 500);
                double sliceMs = sw.Elapsed.TotalMilliseconds;

                // v3 event-driven refresh: quiescent (no metric change) vs after a
                // clustered congestion delta — cost scales with change, not count
                buckets.DriftSweepScans = int.MaxValue;
                sw.Restart();
                var (scanQ, refQ) = buckets.RefreshSliceEventDriven(ctx, city.Dests.Count, city.Dests.Count);
                double quiescentMs = sw.Elapsed.TotalMilliseconds;
                var chg = new List<int>();
                int ctr = rng.NextInt(g.NodeCount);
                var qq = new Queue<int>(); var seenN = new HashSet<int> { ctr }; qq.Enqueue(ctr);
                while (qq.Count > 0 && chg.Count < 150)
                {
                    int v = qq.Dequeue();
                    for (int e = g.OutStart[v]; e < g.OutStart[v + 1] && chg.Count < 150; e++)
                    {
                        g.TimeLive[e] *= 1.4f; chg.Add(e);
                        if (seenN.Add(g.Head[e])) qq.Enqueue(g.Head[e]);
                    }
                }
                eng.RefreshLive(chg);
                sw.Restart();
                var (scanD, refD) = buckets.RefreshSliceEventDriven(ctx, city.Dests.Count, city.Dests.Count);
                double dirtyMs = sw.Elapsed.TotalMilliseconds;

                var buf = new DestinationCandidate[8];
                var scanLat = new List<double>(10000);
                long entries = 0;
                for (int i = 0; i < 10000; i++)
                {
                    int s = rng.NextInt(g.NodeCount);
                    int cat = rng.NextInt(city.Dests.CategoryCount);
                    long t0 = Stopwatch.GetTimestamp();
                    buckets.Scan(ctx, s, cat, buf, 6);
                    scanLat.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
                    entries += buckets.LastScanEntries;
                }

                var fleet = FleetIndex.Create(eng.Query, 200, refMetric);
                for (int v = 0; v < 200; v++) fleet.PostVehicle(ctx, v, rng.NextInt(g.NodeCount));
                var dispLat = new List<double>(10000);
                for (int i = 0; i < 10000; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    fleet.Dispatch(ctx, rng.NextInt(g.NodeCount));
                    dispLat.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
                }

                sb.AppendLine("### Layer 3 — flexible destinations + service dispatch");
                sb.AppendLine();
                sb.AppendLine("| measure | value |");
                sb.AppendLine("|---|---|");
                sb.AppendLine($"| bucket build, {city.Dests.Count:N0} destinations | {buildMs:N0} ms ({buckets.EntriesTotal:N0} entries) |");
                sb.AppendLine($"| blind staggered refresh, 500 backward searches (v2, kept for A/B) | {sliceMs:0.0} ms |");
                sb.AppendLine($"| event-driven refresh, quiescent full rotation of {city.Dests.Count:N0} dests | {quiescentMs:0.0} ms ({refQ} re-searches) |");
                sb.AppendLine($"| event-driven refresh after a 150-edge congestion pocket | {dirtyMs:0.0} ms ({refD} of {scanD} scanned re-searched) |");
                sb.AppendLine($"| shopper query (scan {city.Dests.Count / city.Dests.CategoryCount:N0} dests/category) | median {Pct(scanLat, 0.5):0.0} µs, p99 {Pct(scanLat, 0.99):N0} µs, {entries / 10000} entries scanned |");
                sb.AppendLine($"| dispatch query (fleet of 200) | median {Pct(dispLat, 0.5):0.0} µs, p99 {Pct(dispLat, 0.99):N0} µs |");
                sb.AppendLine();
                Console.WriteLine($"  buckets build={buildMs:N0}ms scan median={Pct(scanLat, 0.5):0.0}us dispatch median={Pct(dispLat, 0.5):0.0}us");
            }

            sb.AppendLine($"Process memory after benchmark: {GC.GetTotalMemory(false) / 1e6:N0} MB managed.");
            sb.AppendLine();
            return sb.ToString();
        }

        // ==================================================================
        private static string SimAtScale(Dictionary<string, string> opts, ulong seed)
        {
            int cols = (int)GetOpt(opts, "cols", 362), rows = (int)GetOpt(opts, "rows", 362);
            int nTrips = (int)GetOpt(opts, "trips", 100_000);
            int ticks = (int)GetOpt(opts, "ticks", 1500);
            int profiles = (int)GetOpt(opts, "profiles", 8);
            var sb = new StringBuilder();

            Console.WriteLine($"sim: integrated simulation, {nTrips:N0} trips over {ticks} ticks...");
            var city = SyntheticCity.Build(cols, rows, 50_000, 5_000, seed);
            var anchors = city.BuildAnchors(profiles, includeTypical: true);
            var eng = RoutingEngine.Build(city.G, anchors);
            var sim = TrafficSim.Create(city.G, city.JamCapacity, SimMode.Rebuild, eng);
            // integration-sim planner config: per-trip micro-costs are measured
            // precisely in `bench`; here the single-threaded harness stands in
            // for Burst-parallel jobs, so plan with fewer expensive side
            // searches to keep wall time sane at 100k trips
            sim.Planner!.Cfg.PenaltyIters = 1;
            sim.Planner.Cfg.RepairGapThreshold = 0.05f;
            sim.Planner.Cfg.MaxAlternatives = 4;
            // coarser live-report deadband: the update engine's own degradation
            // threshold is 10%, so sub-8% wobble only churns customization
            sim.LiveChangeThreshold = 0.08f;
            var rng = new SplitMix64(seed + 7);
            int n = city.G.NodeCount;
            int departWindow = (int)(ticks * 0.6);
            for (int i = 0; i < nTrips; i++)
            {
                sim.AddTrip(1 + rng.NextInt(departWindow), new TripRequest
                {
                    AgentId = i, Origin = rng.NextInt(n), Destination = rng.NextInt(n),
                    Alpha = city.Citizens[rng.NextInt(city.Citizens.Length)],
                    Seed = seed ^ (ulong)(i * 2862933555777941757L),
                    LongOrTransit = i % 100 == 0,
                });
            }
            var sw = Stopwatch.StartNew();
            int lastReport = 0;
            sim.Run(ticks, tick =>
            {
                if (tick - lastReport >= 250)
                {
                    lastReport = tick;
                    Console.WriteLine($"  tick {tick}: active={CountActive(sim)} arrived={sim.FinishedTrips} softClosed={sim.Detector!.SoftClosedCount} switches={sim.Upd!.Stats.Switches}");
                }
            });
            double wallS = sw.Elapsed.TotalSeconds;
            var ps = sim.Planner!.Stats;
            var us = sim.Upd!.Stats;
            double simSecondsPerTick = 4.0; // Dt
            double realtimeFactor = ticks * simSecondsPerTick / wallS;

            sb.AppendLine("## Integrated simulation at scale");
            sb.AppendLine();
            sb.AppendLine($"{city.G.NodeCount:N0}-node city, {nTrips:N0} trips, {ticks} ticks of {simSecondsPerTick}s (~{ticks * simSecondsPerTick / 3600:0.0} sim-hours), single-threaded harness on {Environment.ProcessorCount} cores.");
            sb.AppendLine();
            sb.AppendLine("| measure | value |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| wall time | {wallS:N0} s ({realtimeFactor:0.0}x realtime) |");
            sb.AppendLine($"| arrivals | {sim.FinishedTrips:N0} / {sim.Trips.Count:N0} |");
            sb.AppendLine($"| trips planned (incl. regenerations) | {ps.TripsPlanned:N0} |");
            sb.AppendLine($"| certified-exact fraction | {(double)ps.CertifiedTrips / Math.Max(1, ps.TripsPlanned):P1} |");
            sb.AppendLine($"| planning time | {sim.MsPlanning / 1000:0.0} s total, {sim.MsPlanning / Math.Max(1, ps.TripsPlanned) * 1000:N0} µs/trip |");
            sb.AppendLine($"| movement | {sim.MsMovement / ticks:0.00} ms/tick |");
            sb.AppendLine($"| live-metric partial customization | {sim.MsCustomize / (ticks / sim.RefreshInterval):0.00} ms/refresh |");
            sb.AppendLine($"| Layer-4 wakes + re-pricing | {sim.MsWakes / (ticks / sim.RefreshInterval):0.00} ms/refresh |");
            sb.AppendLine($"| re-prices / probes / regenerations | {us.Reprices:N0} / {us.Probes:N0} / {us.Regenerations:N0} |");
            sb.AppendLine($"| route switches (past hysteresis) | {us.Switches:N0} |");
            sb.AppendLine($"| event wakes (metered) / region wakes / sweeper | {us.EventWakes:N0} / {us.RegionWakes:N0} / {us.SweeperWakes:N0} |");
            sb.AppendLine($"| soft-closed edges at end | {sim.Detector!.SoftClosedCount:N0} |");
            sb.AppendLine($"| unreachable-trip events | {ps.UnreachableTrips:N0} |");
            sb.AppendLine();
            Console.WriteLine($"  done in {wallS:N0}s: arrived={sim.FinishedTrips}/{sim.Trips.Count} planned={ps.TripsPlanned} switches={us.Switches}");
            return sb.ToString();
        }

        // ==================================================================
        /// <summary>
        /// Register opportunity #1: adaptive departure timing. Arrival-constrained
        /// commuters set depart = target arrival − (learned commute mean +
        /// k_i·deviation) − jitter_i, learning from the cluster entries' realized
        /// travel telemetry (register #2) with slow EMAs and per-agent
        /// heterogeneity to prevent departure-herding. A/B against fixed
        /// departures over D simulated days: expect lateness and peak
        /// concentration to fall without a global scheduler (kills gap #5 at
        /// the source).
        /// </summary>
        private static string CommuteExperiment(Dictionary<string, string> opts, ulong seed)
        {
            int cols = (int)GetOpt(opts, "cols", 120), rows = (int)GetOpt(opts, "rows", 120);
            int commuters = (int)GetOpt(opts, "commuters", 10_000);
            int days = (int)GetOpt(opts, "days", 8);
            int dayTicks = (int)GetOpt(opts, "dayticks", 700);
            var sb = new StringBuilder();
            var city = SyntheticCity.Build(cols, rows, commuters, 500, seed);
            var g = city.G;
            var anchors = city.BuildAnchors(6, includeTypical: true);

            // commuter population: zonal homes, concentrated work destinations,
            // normal target-arrival times, heterogeneous risk factors + jitter
            var rng0 = new SplitMix64(seed + 55);
            int[] Pool(int center, int size)
            {
                var pool = new List<int> { center };
                var q = new Queue<int>(); q.Enqueue(center);
                var seen = new HashSet<int> { center };
                while (q.Count > 0 && pool.Count < size)
                {
                    int v = q.Dequeue();
                    for (int e = g.OutStart[v]; e < g.OutStart[v + 1]; e++)
                        if (seen.Add(g.Head[e])) { pool.Add(g.Head[e]); q.Enqueue(g.Head[e]); }
                }
                return pool.ToArray();
            }
            int H = 25, W = 6;
            var homePools = new int[H][]; var workPools = new int[W][];
            for (int z = 0; z < H; z++) homePools[z] = Pool(rng0.NextInt(g.NodeCount), 150);
            for (int z = 0; z < W; z++) workPools[z] = Pool(rng0.NextInt(g.NodeCount), 200);
            var home = new int[commuters]; var work = new int[commuters];
            var target = new int[commuters]; var kRisk = new float[commuters]; var jit = new int[commuters];
            for (int i = 0; i < commuters; i++)
            {
                home[i] = homePools[rng0.NextInt(H)][rng0.NextInt(150)];
                work[i] = workPools[rng0.NextInt(W)][rng0.NextInt(200)];
                double u1 = 1 - rng0.NextDouble(), u2 = rng0.NextDouble();
                float gauss = (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
                target[i] = (int)(dayTicks * 0.45f + dayTicks * 0.06f * gauss);
                kRisk[i] = 0.5f + 1.5f * rng0.NextFloat();
                jit[i] = rng0.NextInt(7) - 3;
            }

            sb.AppendLine("## Adaptive departure timing (register opportunity #1)");
            sb.AppendLine();
            sb.AppendLine($"{commuters:N0} commuters, {days} days × {dayTicks} ticks ({cols}x{rows} city). " +
                          "Adaptive: depart = target − (entry commute mean + k·deviation) − jitter, learned from " +
                          "cluster-entry realized telemetry. Fixed: depart = target − 1.25×free-flow estimate − jitter.");
            sb.AppendLine();
            sb.AppendLine("| day | mode | mean travel (ticks) | mean lateness | mean \\|lateness\\| | >10 ticks late | depart std | arrive std |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");

            foreach (bool adaptive in new[] { false, true })
            {
                var eng = RoutingEngine.Build(g, anchors);
                // reset live components between modes for fairness
                Array.Copy(g.TimeFree, g.TimeLive, g.EdgeCount);
                Array.Copy(g.TimeFree, g.TimeTypical, g.EdgeCount);
                eng.Metrics.ResetAll(); eng.Metrics.FullCustomize();
                var sim = TrafficSim.Create(g, city.JamCapacity, SimMode.Rebuild, eng);
                sim.TicksPerDay = dayTicks;
                sim.Planner!.Cfg.PenaltyIters = 1;
                sim.Planner.Cache!.DirectGenBudget = 400; // warmup governor active
                var ffCtx = eng.Query.CreateContext();
                int ffK = anchors.ScenarioBlockStart(CS2Path.Core.Scenario.FreeFlow);
                var est0 = new float[commuters];
                for (int i = 0; i < commuters; i++)
                    est0[i] = eng.Query.Distance(ffCtx, home[i], work[i], ffK) / sim.Dt; // ticks

                int agentBase = 0;
                var rng = new SplitMix64(seed + 99);
                for (int day = 0; day < days; day++)
                {
                    int dayStart = day * dayTicks;
                    for (int i = 0; i < commuters; i++)
                    {
                        // read-only lookup (no entry creation / stat pollution);
                        // telemetry is keyed by DEPART bucket, so read via a
                        // one-step fixed point on the anticipated depart time
                        int lead = (int)(est0[i] * 1.25f);
                        var entry = adaptive ? sim.Planner.Cache!.TryGet(home[i], work[i]) : null;
                        if (adaptive && entry != null)
                        {
                            int bucket = sim.TodBucketOf(Math.Max(dayStart + 1, dayStart + target[i] - lead));
                            if (entry.RealizedCount[bucket] >= 3)
                            {
                                lead = (int)((entry.RealizedMean[bucket] + kRisk[i] * entry.RealizedDev[bucket]) / sim.Dt);
                                int bucket2 = sim.TodBucketOf(Math.Max(dayStart + 1, dayStart + target[i] - lead));
                                if (bucket2 != bucket && entry.RealizedCount[bucket2] >= 3)
                                    lead = (int)((entry.RealizedMean[bucket2] + kRisk[i] * entry.RealizedDev[bucket2]) / sim.Dt);
                            }
                        }
                        int depart = Math.Max(dayStart + 1, dayStart + target[i] - lead - jit[i]);
                        sim.AddTrip(depart, new TripRequest
                        {
                            AgentId = agentBase + i, Origin = home[i], Destination = work[i],
                            Alpha = city.Citizens[i % city.Citizens.Length],
                            Seed = seed ^ (ulong)((agentBase + i) * 6364136223846793005L),
                        });
                    }
                    int tripsBefore = sim.Trips.Count;
                    sim.Run(dayTicks);

                    // per-day stats over this day's agents — trips enter
                    // sim.Trips in DEPART order, so recover the commuter index
                    // from the trip's AgentId, never from list position
                    double tSum = 0, lSum = 0, lAbs = 0, dSum = 0, dSq = 0, aSum = 0, aSq = 0;
                    int n = 0, late = 0, arrived = 0;
                    for (int k = tripsBefore; k < sim.Trips.Count; k++)
                    {
                        var t = sim.Trips[k];
                        int i = t.Plan.AgentId - agentBase;
                        if (i < 0 || i >= commuters) continue;
                        if (!t.Finished || t.FinishTick < 0) continue;
                        arrived++;
                        double travel = t.FinishTick - t.DepartTick;
                        double lateness = t.FinishTick - (dayStart + target[i]);
                        tSum += travel; lSum += lateness; lAbs += Math.Abs(lateness);
                        if (lateness > 10) late++;
                        dSum += t.DepartTick; dSq += (double)t.DepartTick * t.DepartTick;
                        aSum += t.FinishTick; aSq += (double)t.FinishTick * t.FinishTick;
                        n++;
                    }
                    if (n > 0)
                    {
                        double dStd = Math.Sqrt(Math.Max(0, dSq / n - (dSum / n) * (dSum / n)));
                        double aStd = Math.Sqrt(Math.Max(0, aSq / n - (aSum / n) * (aSum / n)));
                        string mode = adaptive ? "adaptive" : "fixed";
                        sb.AppendLine($"| {day} | {mode} | {tSum / n:0.0} | {lSum / n:+0.0;-0.0} | {lAbs / n:0.0} | {(double)late / n:P1} | {dStd:0.0} | {aStd:0.0} |");
                        Console.WriteLine($"  {mode} day {day}: travel={tSum / n:0.0} late={lSum / n:+0.0;-0.0} |late|={lAbs / n:0.0} >10={((double)late / n):P1} arrived={arrived}");
                    }
                    agentBase += commuters;
                }
            }
            sb.AppendLine();
            sb.AppendLine("Adaptive departures learn from the same shared telemetry that feeds predictive " +
                          "pricing; per-agent heterogeneity (k, jitter) plus slow EMAs prevent departure-herding.");
            return sb.ToString();
        }

        private static int CountActive(TrafficSim sim)
        {
            int c = 0;
            foreach (var t in sim.Trips) if (!t.Finished) c++;
            return c;
        }
    }
}
