using System;
using System.Collections.Generic;
using System.Text;
using CS2Path.Core;

namespace CS2Path.Harness
{
    /// <summary>
    /// The synchronized-demand stress test (plan §6): an origin cluster and a
    /// destination cluster joined by three near-equivalent parallel corridors,
    /// with cohort departures. Vanilla-style routing (exact best-response to a
    /// lagged shared snapshot, frozen plans) produces corridor herding waves;
    /// the rebuild (portfolios + logit + hysteresis + staggered refresh +
    /// graded soft closures) should cut oscillation amplitude ≥ 5x.
    /// </summary>
    public static class Herding
    {
        public const int CorridorCount = 3;
        public const int CorridorLen = 24;

        public sealed class ScenarioGraph
        {
            public Graph G = null!;
            public float[] Jam = null!;
            public int[] MarkerEdges = null!;   // first edge of each corridor
            public int[] OriginNodes = null!;
            public int[] DestNodes = null!;
        }

        public static ScenarioGraph BuildScenario()
        {
            var edges = new List<(int, int, float, float, float, float)>();
            var jam = new List<float>();
            var xs = new List<float>(); var ys = new List<float>();
            int NewNode(float x, float y) { xs.Add(x); ys.Add(y); return xs.Count - 1; }

            void Link(int a, int b, float t, float cap, float jamCap)
            {
                float money = t * 0.01f, comfort = t * 0.1f;
                edges.Add((a, b, t, money, comfort, cap)); jam.Add(jamCap);
                edges.Add((b, a, t, money, comfort, cap)); jam.Add(jamCap);
            }

            // origin cluster 6x6
            var oc = new int[6, 6];
            for (int r = 0; r < 6; r++)
                for (int c = 0; c < 6; c++)
                    oc[r, c] = NewNode(c * 100, r * 100);
            for (int r = 0; r < 6; r++)
                for (int c = 0; c < 6; c++)
                {
                    if (c + 1 < 6) Link(oc[r, c], oc[r, c + 1], 9f, 8f, 40f);
                    if (r + 1 < 6) Link(oc[r, c], oc[r + 1, c], 9f, 8f, 40f);
                }
            int gateA = NewNode(700, 250);
            for (int r = 0; r < 6; r++) Link(oc[r, 5], gateA, 7f, 12f, 60f);

            // three corridors, slightly different free-flow times
            int corStartX = 900;
            var chainStarts = new int[CorridorCount];
            var chainEnds = new int[CorridorCount];
            for (int i = 0; i < CorridorCount; i++)
            {
                float perEdge = 6f * (1f + 0.03f * i);
                int prev = -1;
                for (int j = 0; j < CorridorLen; j++)
                {
                    int nd = NewNode(corStartX + j * 100, i * 400);
                    if (j == 0) chainStarts[i] = nd;
                    else Link(prev, nd, perEdge, 4f, 30f);
                    prev = nd;
                }
                chainEnds[i] = prev;
            }
            int gateB = NewNode(corStartX + CorridorLen * 100 + 200, 250);
            for (int i = 0; i < CorridorCount; i++)
            {
                Link(gateA, chainStarts[i], 6f, 8f, 40f);
                Link(chainEnds[i], gateB, 6f, 8f, 40f);
            }

            // destination cluster 6x6
            var dc = new int[6, 6];
            float dx = corStartX + CorridorLen * 100 + 400;
            for (int r = 0; r < 6; r++)
                for (int c = 0; c < 6; c++)
                    dc[r, c] = NewNode(dx + c * 100, r * 100);
            for (int r = 0; r < 6; r++)
                for (int c = 0; c < 6; c++)
                {
                    if (c + 1 < 6) Link(dc[r, c], dc[r, c + 1], 9f, 8f, 40f);
                    if (r + 1 < 6) Link(dc[r, c], dc[r + 1, c], 9f, 8f, 40f);
                }
            for (int r = 0; r < 6; r++) Link(gateB, dc[r, 0], 7f, 12f, 60f);

            var g = Graph.Build(xs.Count, edges, xs.ToArray(), ys.ToArray());
            var sc = new ScenarioGraph { G = g, Jam = jam.ToArray() };
            sc.MarkerEdges = new int[CorridorCount];
            for (int i = 0; i < CorridorCount; i++)
                sc.MarkerEdges[i] = g.FindEdge(gateA, chainStarts[i]);
            sc.OriginNodes = new int[36]; sc.DestNodes = new int[36];
            int p = 0;
            for (int r = 0; r < 6; r++) for (int c = 0; c < 6; c++) sc.OriginNodes[p++] = oc[r, c];
            p = 0;
            for (int r = 0; r < 6; r++) for (int c = 0; c < 6; c++) sc.DestNodes[p++] = dc[r, c];
            return sc;
        }

        public sealed class Result
        {
            public double Amplitude;          // mean over corridors of std of corridor share
            public double MeanTravelTicks;
            public long Finished, TripCount, Queries;
            public double[][] Shares = null!; // [corridor][window] for reporting
        }

        public static Result Run(SimMode mode, int ticks = 1600, ulong seed = 42)
        {
            var sc = BuildScenario();
            RoutingEngine? eng = null;
            if (mode == SimMode.Rebuild)
            {
                // small alpha spread: mostly time-sensitive travelers
                var pop = new List<Preference>();
                var prng = new SplitMix64(seed);
                for (int i = 0; i < 500; i++)
                    pop.Add(new Preference(1f + 0.1f * prng.NextFloat(), 0.02f * prng.NextFloat(), 0.02f * prng.NextFloat()));
                var anchors = AnchorGrid.Build(pop, null, 5, new[] { CS2Path.Core.Scenario.FreeFlow, CS2Path.Core.Scenario.Live }, seed);
                eng = RoutingEngine.Build(sc.G, anchors);
            }
            var sim = TrafficSim.Create(sc.G, sc.Jam, mode, eng);
            sim.RefreshInterval = 5;
            sim.SnapshotInterval = 40;
            if (sim.Planner != null)
            {
                // congestion swings make corridors comparable over a wide band:
                // widen the envelope and the choice noise accordingly (§4 L4)
                sim.Planner.Cfg.EnvelopeEps = 0.60f;
                sim.Planner.Cfg.LogitScale = 0.10f;
            }

            // identical synchronized cohort demand in both modes
            var rng = new SplitMix64(seed);
            int agent = 0;
            for (int t0 = 1; t0 < ticks - 400; t0 += 8)
            {
                for (int i = 0; i < 40; i++)
                {
                    sim.AddTrip(t0, new TripRequest
                    {
                        AgentId = agent,
                        Origin = sc.OriginNodes[rng.NextInt(sc.OriginNodes.Length)],
                        Destination = sc.DestNodes[rng.NextInt(sc.DestNodes.Length)],
                        Alpha = new Preference(1f + 0.1f * rng.NextFloat(), 0.02f * rng.NextFloat(), 0.02f * rng.NextFloat()),
                        Seed = seed ^ (ulong)(agent * 2654435761L),
                    });
                    agent++;
                }
            }

            const int Window = 20;
            int nw = ticks / Window + 1;
            var flow = new double[CorridorCount, nw];
            sim.OnEdgeEnter = (a, e) =>
            {
                for (int i = 0; i < CorridorCount; i++)
                    if (sc.MarkerEdges[i] == e) { flow[i, sim.Tick / Window] += 1; break; }
            };

            sim.Run(ticks);

            // corridor shares per window, warmup discarded
            int w0 = 400 / Window;
            var shares = new double[CorridorCount][];
            var used = new List<int>();
            for (int w = w0; w < nw; w++)
            {
                double tot = 0;
                for (int i = 0; i < CorridorCount; i++) tot += flow[i, w];
                if (tot >= 8) used.Add(w);
            }
            for (int i = 0; i < CorridorCount; i++)
            {
                shares[i] = new double[used.Count];
                for (int j = 0; j < used.Count; j++)
                {
                    double tot = 0;
                    for (int c = 0; c < CorridorCount; c++) tot += flow[c, used[j]];
                    shares[i][j] = flow[i, used[j]] / tot;
                }
            }
            double amp = 0;
            for (int i = 0; i < CorridorCount; i++)
            {
                double mean = 0;
                foreach (var v in shares[i]) mean += v;
                mean /= Math.Max(1, shares[i].Length);
                double var = 0;
                foreach (var v in shares[i]) var += (v - mean) * (v - mean);
                var /= Math.Max(1, shares[i].Length);
                amp += Math.Sqrt(var);
            }
            amp /= CorridorCount;

            return new Result
            {
                Amplitude = amp,
                MeanTravelTicks = sim.FinishedTrips > 0 ? (double)sim.TotalTravelTicks / sim.FinishedTrips : double.NaN,
                Finished = sim.FinishedTrips,
                TripCount = sim.Trips.Count,
                Queries = mode == SimMode.Vanilla ? sim.VanillaQueries : (sim.Planner!.Stats.TripsPlanned),
                Shares = shares,
            };
        }

        public static string RunAB(out double ratio)
        {
            var sb = new StringBuilder();
            Console.WriteLine("herding: running vanilla baseline (lagged-snapshot Dijkstra, frozen plans)...");
            var van = Run(SimMode.Vanilla);
            Console.WriteLine($"  vanilla: amplitude={van.Amplitude:0.0000}, meanTravel={van.MeanTravelTicks:0.0} ticks, arrived={van.Finished}/{van.TripCount}");
            Console.WriteLine("herding: running rebuild (portfolios + logit + hysteresis + staggered updates)...");
            var reb = Run(SimMode.Rebuild);
            Console.WriteLine($"  rebuild: amplitude={reb.Amplitude:0.0000}, meanTravel={reb.MeanTravelTicks:0.0} ticks, arrived={reb.Finished}/{reb.TripCount}");
            ratio = van.Amplitude / Math.Max(1e-9, reb.Amplitude);
            sb.AppendLine("## Herding A/B (synchronized-demand stress test)");
            sb.AppendLine();
            sb.AppendLine("| mode | corridor-share oscillation (std) | mean travel (ticks) | arrived |");
            sb.AppendLine("|---|---|---|---|");
            sb.AppendLine($"| vanilla baseline (lagged Dijkstra, frozen) | {van.Amplitude:0.0000} | {van.MeanTravelTicks:0.0} | {van.Finished}/{van.TripCount} |");
            sb.AppendLine($"| rebuild (this mod) | {reb.Amplitude:0.0000} | {reb.MeanTravelTicks:0.0} | {reb.Finished}/{reb.TripCount} |");
            sb.AppendLine();
            sb.AppendLine($"**Oscillation amplitude reduction: {ratio:0.0}x** (target ≥ 5x, plan §6)");
            return sb.ToString();
        }
    }
}
