using System;
using System.Collections.Generic;

namespace CS2Path.Core
{
    public sealed class PlannerConfig
    {
        public int MaxAlternatives = 5;
        public float OverlapMax = 0.70f;      // bounded overlap (plan §4 Layer 2)
        public float StretchMax = 1.25f;      // bounded stretch
        public float PenaltyFactor = 1.3f;    // penalty-method multiplier
        public int PenaltyIters = 2;
        public float LogitScale = 0.04f;      // Gumbel noise scale, relative to best cost
        public float EnvelopeEps = 0.30f;     // ε-envelope retention (§4.8)
        public float CertTolerance = 2e-3f;   // certified-exact threshold (float slack)
        public float RepairGapThreshold = 0.01f;
        public int RepairMaxSettled = 2000;   // repair is best-effort: past this the potential was too loose
        public int PenaltyMaxSettled = 8000;
        public bool UseFreeFlowDiversity = true;
    }

    /// <summary>Full state of one planned trip: the via-node portfolio, the
    /// chosen alternative's expanded path, and certificate telemetry.</summary>
    public sealed class TripPlan
    {
        public int AgentId;
        public int Origin, Destination;
        public Preference Alpha;
        public ulong Seed;
        public int NearestProfile;
        public List<Alternative> Alts = new List<Alternative>(6);
        public float[] LiveAnchorDists = Array.Empty<float>(); // d*_p per live profile (certificate inputs)
        public int ChosenIdx = -1;
        public List<int> ChosenEdgePath = new List<int>();
        public float ChosenAlphaCost, BestAlphaCost;
        public float CertLowerBound, CertGap;
        public bool Certified, Repaired;
        public bool Unreachable;
        public bool IsFlexible;

        public bool HasPlan => ChosenIdx >= 0;
    }

    /// <summary>
    /// Layer 2: fixed-destination trip planning (plan §4 Layer 2, §4.7, §4.8).
    /// Anchor-grid CCH queries produce candidate via-nodes; the penalty method
    /// adds structurally distinct extras; long/transit trips get a
    /// Suurballe-disjoint backup; candidates are filtered for bounded overlap
    /// and stretch; the agent scores the survivors with its TRUE continuous
    /// preference vector plus logit noise; and every portfolio receives an
    /// exactness certificate with on-demand repair for the uncertified tail.
    ///
    /// NOT thread-safe: create one planner per worker thread.
    /// </summary>
    public sealed class TripPlanner
    {
        public PlannerConfig Cfg = new PlannerConfig();
        public Telemetry Stats = new Telemetry();

        private readonly Graph _g;
        private readonly CchSkeleton _c;
        private readonly CchMetrics _m;
        private readonly CchQuery _q;
        private readonly AnchorGrid _anchors;
        private readonly QueryContext _ctx;
        private readonly PotentialAStar _astar;

        private readonly List<int> _pathBuf = new List<int>(512);
        private readonly List<int> _pathBuf2 = new List<int>(512);
        private readonly float[] _multiDist = new float[CchQuery.MaxBatch];
        private readonly int[] _multiMeet = new int[CchQuery.MaxBatch];
        private readonly List<(int profile, float lambda)> _lambda = new List<(int, float)>(4);
        private readonly Dictionary<int, float> _penalty = new Dictionary<int, float>(512);
        private readonly List<int> _sbP1 = new List<int>(512), _sbP2 = new List<int>(512);

        public TripPlanner(CchMetrics m, CchQuery q)
        {
            _m = m; _c = m.C; _g = _c.G; _q = q; _anchors = m.Anchors;
            _ctx = q.CreateContext();
            _astar = new PotentialAStar(m, q);
        }

        public QueryContext Ctx => _ctx;

        private int LiveMetric(int profile) => _anchors.ScenarioBlockStart(Scenario.Live) + profile;

        public TripPlan PlanFixed(in TripRequest req)
        {
            int s = req.Origin, t = req.Destination;
            var plan = new TripPlan
            {
                AgentId = req.AgentId, Origin = s, Destination = t,
                Alpha = req.Alpha, Seed = req.Seed,
                NearestProfile = _anchors.NearestProfile(req.Alpha),
            };
            int P = _anchors.ProfileCount;
            plan.LiveAnchorDists = new float[P];
            int nearK = LiveMetric(plan.NearestProfile);

            if (s == t)
            {
                plan.Alts.Add(new Alternative { ViaNode = s, Destination = t });
                plan.ChosenIdx = 0;
                plan.Certified = true;
                Stats.TripsPlanned++; Stats.CertifiedTrips++;
                _acceptedGeomsScratch = new List<List<int>> { new List<int>() };
                return plan;
            }

            // --- 1. Anchor-grid winners: ALL live anchor lanes in one batched
            // sweep (the certificates reuse these exact distances) ---
            var vias = new List<(int via, bool backup)>(8);
            var viaSet = new HashSet<int>();
            int liveStart = _anchors.ScenarioBlockStart(Scenario.Live);
            for (int p0 = 0; p0 < P; p0 += CchQuery.MaxBatch)
            {
                int chunk = Math.Min(CchQuery.MaxBatch, P - p0);
                _q.DistanceMulti(_ctx, s, t, liveStart + p0, chunk, _multiDist, _multiMeet);
                for (int p = 0; p < chunk; p++)
                {
                    plan.LiveAnchorDists[p0 + p] = _multiDist[p];
                    if (!float.IsPositiveInfinity(_multiDist[p]) && viaSet.Add(_multiMeet[p]))
                        vias.Add((_multiMeet[p], false));
                }
            }
            float dNear = plan.LiveAnchorDists[plan.NearestProfile];
            if (float.IsPositiveInfinity(dNear))
            {
                // Layer 5: genuine unreachability — logged, graceful degradation upstream.
                plan.Unreachable = true;
                Stats.UnreachableTrips++;
                return plan;
            }
            if (Cfg.UseFreeFlowDiversity)
            {
                int ffK = _anchors.ScenarioBlockStart(Scenario.FreeFlow) + plan.NearestProfile;
                float d = _q.Distance(_ctx, s, t, ffK);
                if (!float.IsPositiveInfinity(d) && viaSet.Add(_ctx.LastMeetNode))
                    vias.Add((_ctx.LastMeetNode, false));
            }

            // --- 2. Penalty-method extras on the nearest live anchor ---
            if (vias.Count < Cfg.MaxAlternatives && Cfg.PenaltyIters > 0)
            {
                _penalty.Clear();
                _q.DistanceWithPath(_ctx, s, t, nearK, _pathBuf); // incumbent geometry
                foreach (var e in _pathBuf) _penalty[e] = Cfg.PenaltyFactor;
                var lam = new[] { (nearK, 1f) };
                for (int it = 0; it < Cfg.PenaltyIters && vias.Count < Cfg.MaxAlternatives; it++)
                {
                    // multiplicative penalty plus a small additive term so the
                    // penalty still bites on zero-weight edges (e.g. toll-free
                    // edges under a money-dominated anchor); weight stays >= the
                    // base metric, keeping the potential admissible
                    float d = _astar.Search(_ctx, s, t, lam,
                        e =>
                        {
                            float baseW = _anchors.EdgeWeight(_g, e, nearK);
                            if (!_penalty.TryGetValue(e, out var mu)) return baseW;
                            return baseW * mu + (mu - 1f) * 0.001f * _g.TimeFree[e];
                        },
                        _pathBuf2, Cfg.PenaltyMaxSettled);
                    Stats.PenaltySearches++;
                    Stats.PenaltySettled += _astar.LastSettledCount;
                    if (float.IsPositiveInfinity(d)) break;
                    int via = RankMaxNode(_pathBuf2);
                    if (via >= 0 && viaSet.Add(via)) vias.Add((via, false));
                    foreach (var e in _pathBuf2)
                        _penalty[e] = (_penalty.TryGetValue(e, out var mu) ? mu : 1f) * Cfg.PenaltyFactor;
                }
            }

            // --- 3. Suurballe-disjoint backup for long / transit trips ---
            if (req.LongOrTransit)
            {
                if (Suurballe.FindDisjointPair(_g, s, t, e => _anchors.EdgeWeight(_g, e, nearK), _sbP1, _sbP2))
                {
                    // the disjoint pair comes back in arbitrary order — adopt
                    // whichever walk contributes a NEW via node
                    int via = RankMaxNode(_sbP2);
                    if (!(via >= 0 && viaSet.Add(via))) via = RankMaxNode(_sbP1);
                    if (via >= 0 && viaSet.Add(via))
                    {
                        vias.Add((via, true));
                        Stats.DisjointBackups++;
                    }
                }
            }

            // --- 4. Admissibility filter + exact true-alpha scoring ---
            BuildScoredAlternatives(plan, s, t, nearK, dNear, vias);
            if (plan.Alts.Count == 0) { plan.Unreachable = true; Stats.UnreachableTrips++; return plan; }

            // --- 5. Certificate, repair, logit choice ---
            Certify(plan, s, t);
            Choose(plan, s, t, nearK);
            Stats.TripsPlanned++;
            return plan;
        }

        /// <summary>Score candidate vias exactly under alpha; enforce stretch,
        /// overlap and the ε-envelope; write plan.Alts (best alpha first).
        /// bestNearSeed is the exact nearest-anchor optimum (dNear): seeding the
        /// stretch bound with it keeps candidate order from loosening the filter.</summary>
        private void BuildScoredAlternatives(TripPlan plan, int s, int t, int nearK, float bestNearSeed, List<(int via, bool backup)> vias)
        {
            var alpha = plan.Alpha;
            float bestNear = bestNearSeed;
            float bestFf = float.PositiveInfinity;
            int ffK = _anchors.ScenarioBlockStart(Scenario.FreeFlow) + plan.NearestProfile;
            var scored = new List<(Alternative alt, List<int> edges, float refTime, float ffCost)>(vias.Count);
            var leg1Arcs = new List<(int arc, bool fwd)>(64);

            foreach (var (via, backup) in vias)
            {
                // one arc-path query per leg; geometry unpacked only for survivors
                float d1 = _q.DistanceWithArcPath(_ctx, s, via, nearK);
                if (float.IsPositiveInfinity(d1)) continue;
                leg1Arcs.Clear();
                leg1Arcs.AddRange(_ctx.ArcPath);
                float d2 = _q.DistanceWithArcPath(_ctx, via, t, nearK);
                float costNear = d1 + d2;
                if (float.IsPositiveInfinity(costNear)) continue;
                if (costNear < bestNear) bestNear = costNear;
                // Retention is judged ACROSS scenarios (§4.8): a corridor that is
                // transiently jammed (bad live cost) but structurally sound (good
                // free-flow cost) stays in the portfolio — collapsing to the
                // momentarily-cheapest corridor is precisely the herding failure.
                float ffCost = _q.Distance(_ctx, s, via, ffK) + _q.Distance(_ctx, via, t, ffK);
                if (ffCost < bestFf) bestFf = ffCost;
                bool nearOk = costNear <= bestNear * Cfg.StretchMax;
                bool ffOk = ffCost <= bestFf * Cfg.StretchMax;
                if (!backup && !nearOk && !ffOk) continue;

                var edges = new List<int>(256);
                foreach (var (arc, fwd) in leg1Arcs) _q.UnpackArc(_ctx.UnpackStack, arc, fwd, nearK, edges);
                foreach (var (arc, fwd) in _ctx.ArcPath) _q.UnpackArc(_ctx.UnpackStack, arc, fwd, nearK, edges);

                float alphaCost = 0f, refTime = 0f;
                foreach (var e in edges)
                {
                    alphaCost += AnchorGrid.AlphaWeightLive(_g, e, in alpha);
                    refTime += _g.TimeLive[e];
                }
                scored.Add((new Alternative
                {
                    ViaNode = via, Destination = t,
                    AnchorCost = costNear, AlphaCost = alphaCost,
                    IsDisjointBackup = backup,
                }, edges, refTime, ffCost));
            }
            if (scored.Count == 0) return;

            scored.Sort((a, b) => a.alt.AlphaCost.CompareTo(b.alt.AlphaCost));
            float bestAlpha = scored[0].alt.AlphaCost;
            float bestFfScored = float.PositiveInfinity;
            foreach (var cand in scored) if (cand.ffCost < bestFfScored) bestFfScored = cand.ffCost;

            // Overlap filter against already-accepted alternatives (shared live-time share).
            var acceptedEdges = new List<HashSet<int>>();
            plan.Alts.Clear();
            var acceptedGeoms = new List<List<int>>();
            foreach (var cand in scored)
            {
                if (plan.Alts.Count >= Cfg.MaxAlternatives) break;
                bool isBackup = cand.alt.IsDisjointBackup;
                // ε-envelope retention (§4.8), across scenarios: keep candidates
                // within (1+ε) of the best under live OR free-flow.
                bool liveIn = cand.alt.AlphaCost <= bestAlpha * (1 + Cfg.EnvelopeEps);
                bool ffIn = cand.ffCost <= bestFfScored * (1 + Cfg.EnvelopeEps);
                if (!isBackup && plan.Alts.Count > 0 && !liveIn && !ffIn) continue;
                bool tooSimilar = false;
                if (!isBackup)
                {
                    foreach (var seen in acceptedEdges)
                    {
                        float shared = 0f;
                        foreach (var e in cand.edges) if (seen.Contains(e)) shared += _g.TimeLive[e];
                        if (shared > Cfg.OverlapMax * cand.refTime) { tooSimilar = true; break; }
                    }
                }
                if (tooSimilar) continue;
                plan.Alts.Add(cand.alt);
                acceptedGeoms.Add(cand.edges);
                var hs = new HashSet<int>();
                foreach (var e in cand.edges) hs.Add(e);
                acceptedEdges.Add(hs);
            }
            _acceptedGeomsScratch = acceptedGeoms;
            plan.BestAlphaCost = plan.Alts[0].AlphaCost;
        }

        private List<List<int>> _acceptedGeomsScratch = new List<List<int>>();

        /// <summary>§4.7: LB(alpha) = max over conic decompositions of
        /// sum(lambda_i * d_i*), with lambda chosen by the tiny LP
        /// (AnchorGrid.BestLowerBound). Equality with the best candidate
        /// certifies exact optimality; otherwise the gap is bounded a
        /// posteriori, and the uncertified tail runs one budgeted
        /// potential-guided repair A* — exact if it completes, in which case
        /// the trip certifies post-repair.</summary>
        private void Certify(TripPlan plan, int s, int t)
        {
            float lb = _anchors.BestLowerBound(in plan.Alpha, plan.LiveAnchorDists, _lambda);
            plan.CertLowerBound = lb;
            if (lb <= 0f || _lambda.Count == 0) { plan.Certified = false; plan.CertGap = float.NaN; return; }
            plan.CertGap = plan.BestAlphaCost / lb - 1f;
            plan.Certified = plan.CertGap <= Cfg.CertTolerance;
            if (plan.Certified) { Stats.CertifiedTrips++; return; }
            Stats.CertGapSum += plan.CertGap;

            if (plan.CertGap > Cfg.RepairGapThreshold && _lambda.Count <= PotentialAStar.MaxLanes)
            {
                // Repair: exact A* under true alpha, guided by the LP lambda potential.
                var lamArr = new (int metric, float lambda)[_lambda.Count];
                for (int i = 0; i < _lambda.Count; i++)
                    lamArr[i] = (LiveMetric(_lambda[i].profile), _lambda[i].lambda);
                var alpha = plan.Alpha;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                float d = _astar.Search(_ctx, s, t, lamArr,
                    e => AnchorGrid.AlphaWeightLive(_g, e, in alpha), _pathBuf2, Cfg.RepairMaxSettled);
                long dtTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                plan.Repaired = true;
                Stats.RepairSearches++;
                Stats.RepairSettled += _astar.LastSettledCount;
                Stats.RepairStopwatchTicks += dtTicks;
                Stats.RepairTimesUs?.Add(dtTicks * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency);
                if (float.IsPositiveInfinity(d))
                {
                    // budget exhausted: keep the portfolio best, keep the gap bound
                    Stats.RepairBudgetExhausted++;
                    return;
                }
                if (d < plan.BestAlphaCost * (1f - 1e-6f) && RankMaxNode(_pathBuf2) >= 0)
                {
                    // Self-correcting portfolio (§4.8): adopt the repaired route.
                    // Give it a real anchor cost — NaN would poison Layer-4
                    // re-pricing comparisons downstream.
                    int via = RankMaxNode(_pathBuf2);
                    int nearK2 = LiveMetric(plan.NearestProfile);
                    float anchorCost = _q.Distance(_ctx, s, via, nearK2) + _q.Distance(_ctx, via, t, nearK2);
                    plan.Alts.Insert(0, new Alternative
                    {
                        ViaNode = via, Destination = t,
                        AnchorCost = anchorCost, AlphaCost = d,
                    });
                    _acceptedGeomsScratch.Insert(0, new List<int>(_pathBuf2));
                    plan.BestAlphaCost = d;
                    Stats.RepairImproved++;
                }
                // Repair completed => the best candidate IS the exact alpha-optimum.
                plan.Certified = true;
                Stats.CertifiedTrips++;
            }
        }

        /// <summary>Logit choice over the portfolio with the agent's true alpha:
        /// u_i = -cost_i + eta*Gumbel_i (plan: noise splits agents exactly where
        /// the cost envelope says routes are genuinely comparable).</summary>
        private void Choose(TripPlan plan, int s, int t, int nearK)
        {
            var rng = new SplitMix64(plan.Seed);
            float eta = Cfg.LogitScale * plan.BestAlphaCost;
            int best = -1; float bestU = float.NegativeInfinity;
            for (int i = 0; i < plan.Alts.Count; i++)
            {
                float u = -plan.Alts[i].AlphaCost + eta * rng.NextGumbel();
                if (u > bestU) { bestU = u; best = i; }
            }
            plan.ChosenIdx = best;
            plan.ChosenAlphaCost = plan.Alts[best].AlphaCost;
            plan.ChosenEdgePath.Clear();
            plan.ChosenEdgePath.AddRange(_acceptedGeomsScratch[best]);
        }

        /// <summary>Re-expand geometry for an alternative (used when Layer 4
        /// switches routes: geometry is only materialized for the driven route).</summary>
        public void ExpandAlternative(TripPlan plan, int altIdx, int fromNode, List<int> edgesOut)
        {
            int nearK = LiveMetric(plan.NearestProfile);
            var alt = plan.Alts[altIdx];
            edgesOut.Clear();
            _q.DistanceWithPath(_ctx, fromNode, alt.ViaNode, nearK, _pathBuf);
            edgesOut.AddRange(_pathBuf);
            _q.DistanceWithPath(_ctx, alt.ViaNode, alt.Destination, nearK, _pathBuf);
            edgesOut.AddRange(_pathBuf);
        }

        /// <summary>Live cost of one alternative from a given node: two CCH
        /// queries (plan: via-node storage makes re-pricing O(k) arithmetic).</summary>
        public float RepriceAlternative(int fromNode, in Alternative alt, int profile)
        {
            int k = LiveMetric(profile);
            float d1 = _q.Distance(_ctx, fromNode, alt.ViaNode, k);
            if (float.IsPositiveInfinity(d1)) return d1;
            return d1 + _q.Distance(_ctx, alt.ViaNode, alt.Destination, k);
        }

        public float ProbeDirect(int fromNode, int dest, int profile)
            => _q.Distance(_ctx, fromNode, dest, LiveMetric(profile));

        /// <summary>True remaining cost of the CURRENT plan: sum of live anchor
        /// weights along the not-yet-driven path. Used by Layer 4 instead of the
        /// via re-price once the agent may have passed its via node — pricing
        /// cur->via->dest for a consumed via would inflate the incumbent and
        /// make switches trigger-happy.</summary>
        public float RemainingPathCost(List<int> path, int cursor, int profile)
        {
            int k = LiveMetric(profile);
            float sum = 0f;
            for (int i = cursor; i < path.Count; i++)
            {
                float w = _anchors.EdgeWeight(_g, path[i], k);
                if (float.IsPositiveInfinity(w)) return float.PositiveInfinity;
                sum += w;
            }
            return sum;
        }

        private int RankMaxNode(List<int> edgePath)
        {
            int best = -1, bestRank = -1;
            foreach (var e in edgePath)
            {
                int u = _g.Tail[e];
                if (_c.Rank[u] > bestRank) { bestRank = _c.Rank[u]; best = u; }
            }
            if (edgePath.Count > 0)
            {
                int h = _g.Head[edgePath[edgePath.Count - 1]];
                if (_c.Rank[h] > bestRank) { best = h; }
            }
            return best;
        }
    }

    /// <summary>Live counters (plan §4.7: certified fraction and mean gap are
    /// telemetry — a direct instrument for anchor-set / preference-distribution fit).</summary>
    public sealed class Telemetry
    {
        public long TripsPlanned, CertifiedTrips, RepairSearches, RepairImproved, RepairBudgetExhausted;
        public long PenaltySearches, DisjointBackups, UnreachableTrips;
        public long RepairSettled, PenaltySettled;
        public double CertGapSum;
        public long RepairStopwatchTicks;
        public List<double>? RepairTimesUs;    // enable to collect repair latency distribution
        public long Reprices, Probes, Regenerations, Switches, EventWakes, SweeperWakes, RegionWakes;

        public void AddFrom(Telemetry o)
        {
            TripsPlanned += o.TripsPlanned; CertifiedTrips += o.CertifiedTrips;
            RepairSearches += o.RepairSearches; RepairImproved += o.RepairImproved;
            RepairBudgetExhausted += o.RepairBudgetExhausted;
            PenaltySearches += o.PenaltySearches; DisjointBackups += o.DisjointBackups;
            UnreachableTrips += o.UnreachableTrips; RepairSettled += o.RepairSettled;
            PenaltySettled += o.PenaltySettled; CertGapSum += o.CertGapSum;
            RepairStopwatchTicks += o.RepairStopwatchTicks;
            if (o.RepairTimesUs != null) (RepairTimesUs ??= new List<double>()).AddRange(o.RepairTimesUs);
            Reprices += o.Reprices; Probes += o.Probes; Regenerations += o.Regenerations;
            Switches += o.Switches; EventWakes += o.EventWakes; SweeperWakes += o.SweeperWakes;
            RegionWakes += o.RegionWakes;
        }
    }
}
