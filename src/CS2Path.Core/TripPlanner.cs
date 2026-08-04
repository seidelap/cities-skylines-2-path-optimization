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
        /// <summary>Weight of the "typical" (rolling-average) scenario in the
        /// CHOICE utility (plan §4 L1's third scenario axis). Live costs swing
        /// with every queue; typical costs are the stable signal that lets logit
        /// split cohorts across genuinely comparable routes instead of herding
        /// onto the momentarily cheapest. 0 = pure live. Ignored when the
        /// anchor grid carries no Typical scenario. Certificates always run on
        /// pure live costs regardless.</summary>
        public float TypicalBlend = 0.3f;
        public float CertTolerance = 2e-3f;   // certified-exact threshold (float slack)
        public float RepairGapThreshold = 0.01f;
        public int RepairMaxSettled = 2000;   // repair is best-effort: past this the potential was too loose
        public int PenaltyMaxSettled = 8000;
        public bool UseFreeFlowDiversity = true;
        /// <summary>§4.7 v2: a material certificate gap is logged as exploration
        /// demand on the cluster entry instead of stalling the trip on a
        /// synchronous repair search. Requires a ClusterCache.</summary>
        public bool AsyncRepair = true;
        /// <summary>§4.9: nested logit (corridor first, then variant) fixes the
        /// IIA overlap bias flat logit has over near-duplicate routes.</summary>
        public bool NestedLogit = true;
        public float NestedLogitTheta = 0.5f; // variant-scale / corridor-scale ratio
        /// <summary>§4.9 quarantine: a below-coverage entry may serve a trip only
        /// if it can offer at least this many distinct corridors — otherwise the
        /// trip falls through to direct generation (which adds diversity and
        /// seeds the entry) so a surge is never funneled onto one known path.</summary>
        public int QuarantineMinCorridors = 2;
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
        /// <summary>§4.9: the shared route-knowledge entry this trip keys into.
        /// The retained per-agent state is the cursor — held branches in Alts
        /// (small), the driven geometry, and bound stamps; route knowledge and
        /// exploration demand live on the entry.</summary>
        public ClusterEntry? Entry;
        public bool ServedFromCache;

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
        /// <summary>§4.9 route-knowledge cache, shared across planners/threads.
        /// Null disables the cache layer (feature flag).</summary>
        public ClusterCache? Cache;

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
        private readonly ClusterEntry.ViaCandidate[] _viaBuf = new ClusterEntry.ViaCandidate[16];
        private readonly HashSet<int> _tripHarvested = new HashSet<int>();
        private readonly float[] _exploreDstar = new float[64];

        public TripPlanner(CchMetrics m, CchQuery q)
        {
            _m = m; _c = m.C; _g = _c.G; _q = q; _anchors = m.Anchors;
            _ctx = q.CreateContext();
            _astar = new PotentialAStar(m, q);
            _hasTypical = _anchors.HasScenario(Scenario.Typical);
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
            // sweep. The certificates reuse these exact distances, and the
            // meeting nodes are the §4.9 harvest — real trips are the sampler.
            var vias = new List<(int via, bool backup)>(8);
            var viaSet = new HashSet<int>();
            _tripHarvested.Clear();
            int liveStart = _anchors.ScenarioBlockStart(Scenario.Live);
            for (int p0 = 0; p0 < P; p0 += CchQuery.MaxBatch)
            {
                int chunk = Math.Min(CchQuery.MaxBatch, P - p0);
                _q.DistanceMulti(_ctx, s, t, liveStart + p0, chunk, _multiDist, _multiMeet);
                for (int p = 0; p < chunk; p++)
                {
                    plan.LiveAnchorDists[p0 + p] = _multiDist[p];
                    if (!float.IsPositiveInfinity(_multiDist[p]) && viaSet.Add(_multiMeet[p]))
                    {
                        vias.Add((_multiMeet[p], false));
                        _tripHarvested.Add(_multiMeet[p]);
                    }
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
                {
                    vias.Add((_ctx.LastMeetNode, false));
                    _tripHarvested.Add(_ctx.LastMeetNode);
                }
            }

            // --- 1b. §4.9 cache-first: key into the cluster entry; harvest this
            // trip's meeting nodes; take held candidates from the shared entry.
            // Cold or quarantined-thin entries fall through to direct generation,
            // which SEEDS the entry (generation demoted to a seeding fallback).
            ClusterEntry? entry = null;
            bool needDirect = true;
            if (Cache != null)
            {
                entry = Cache.GetOrCreate(s, t, out bool created);
                plan.Entry = entry;
                Cache.Arrive(entry);
                short nearMetricId = (short)nearK;
                foreach (var h in _tripHarvested) Cache.Harvest(entry, h, nearMetricId);
                if (!created)
                {
                    int nCached = Cache.CopyVias(entry, _viaBuf);
                    for (int i = 0; i < nCached; i++)
                        if (viaSet.Add(_viaBuf[i].Via)) vias.Add((_viaBuf[i].Via, false));
                    int corridors = DistinctCorridors(vias, entry.Level);
                    if (entry.Covered && vias.Count >= 1)
                    {
                        needDirect = false;
                    }
                    else
                    {
                        // §4.9 quarantine: urgent exploration fires, and a
                        // below-coverage entry may only serve if it offers real
                        // held-branch diversity — never funnel a surge onto a
                        // single known path.
                        entry.Urgent = true;
                        if (corridors >= Cfg.QuarantineMinCorridors) needDirect = false;
                        else Stats.QuarantineDiversions++;
                    }
                }
            }
            if (!needDirect) { plan.ServedFromCache = true; Stats.ServedFromCache++; }
            else Stats.DirectGenerations++;

            // --- 2. Penalty-method extras on the nearest live anchor ---
            if (needDirect && vias.Count < Cfg.MaxAlternatives && Cfg.PenaltyIters > 0)
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
            if (needDirect && req.LongOrTransit)
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
            if (plan.Alts.Count == 0)
            {
                // §4.7 v2 synchronous fallback: portfolio collapse (no feasible
                // held route) gets a PLAIN anchor-metric CCH query — not an
                // exactness repair. This is the only synchronous search left.
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                float d = _q.DistanceWithPath(_ctx, s, t, nearK, _pathBuf2);
                double us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
                Stats.SyncFallbacks++;
                Stats.SyncFallbackTimesUs?.Add(us);
                if (float.IsPositiveInfinity(d))
                {
                    plan.Unreachable = true;
                    Stats.UnreachableTrips++;
                    return plan;
                }
                float alphaCost = 0f;
                var alphaLocal = plan.Alpha;
                foreach (var e in _pathBuf2) alphaCost += AnchorGrid.AlphaWeightLive(_g, e, in alphaLocal);
                int fvia = RankMaxNode(_pathBuf2);
                plan.Alts.Add(new Alternative { ViaNode = fvia >= 0 ? fvia : s, Destination = t, AnchorCost = d, AlphaCost = alphaCost });
                _acceptedGeomsScratch = new List<List<int>> { new List<int>(_pathBuf2) };
                _choiceScores.Clear();
                _choiceScores.Add(BlendedAlphaCost(_pathBuf2, in alphaLocal, alphaCost));
                plan.BestAlphaCost = alphaCost;
            }

            // --- 4b. §4.8/§4.9: donate accepted held branches to the entry so
            // discovery is paid once per corridor, not per trip.
            if (entry != null && Cache != null)
                foreach (var alt in plan.Alts)
                    if (!_tripHarvested.Contains(alt.ViaNode))
                        Cache.Harvest(entry, alt.ViaNode, (short)nearK);

            // --- 5. Certificate, async exploration demand, nested-logit choice ---
            Certify(plan, s, t);
            Choose(plan, s, t, nearK);

            // §4.9 memory model: shared entries + private cursor. The retained
            // per-agent state is the held-branch view (Alts, small), the driven
            // geometry, and bound stamps — certificate inputs are transient.
            plan.LiveAnchorDists = Array.Empty<float>();
            Stats.TripsPlanned++;
            return plan;
        }

        private int DistinctCorridors(List<(int via, bool backup)> vias, int level)
        {
            if (Cache == null || vias.Count == 0) return vias.Count;
            var seen = new HashSet<ulong>();
            foreach (var (via, _) in vias) seen.Add(Cache.CorridorOf(via, level));
            return seen.Count;
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
            _choiceScores.Clear();
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
                _choiceScores.Add(BlendedAlphaCost(cand.edges, in alpha, cand.alt.AlphaCost));
                acceptedGeoms.Add(cand.edges);
                var hs = new HashSet<int>();
                foreach (var e in cand.edges) hs.Add(e);
                acceptedEdges.Add(hs);
            }
            _acceptedGeomsScratch = acceptedGeoms;
            plan.BestAlphaCost = plan.Alts[0].AlphaCost;
        }

        private List<List<int>> _acceptedGeomsScratch = new List<List<int>>();
        private readonly List<float> _choiceScores = new List<float>(); // aligned with plan.Alts
        private readonly bool _hasTypical;

        private float BlendedAlphaCost(List<int> edges, in Preference alpha, float alphaLive)
        {
            float w = _hasTypical ? Cfg.TypicalBlend : 0f;
            if (w <= 0f) return alphaLive;
            float typ = 0f;
            foreach (var e in edges)
                typ += alpha.Dot(_g.TimeTypical[e], _g.Money[e], _g.Comfort[e]);
            return (1 - w) * alphaLive + w * typ;
        }

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

            if (plan.CertGap > Cfg.RepairGapThreshold && Cfg.AsyncRepair && plan.Entry != null && Cache != null)
            {
                // §4.7 v2: the gap does NOT stall the trip. The agent proceeds on
                // its best held branch (sound under logit + hysteresis); the gap
                // becomes exploration demand on the cluster entry, and the
                // exploration budget fixes the hole for every trip on the corridor.
                Cache.LogGap(plan.Entry, plan.CertGap, s, t, in plan.Alpha);
                Stats.ExplorationDemandLogged++;
                return;
            }

            if (plan.CertGap > Cfg.RepairGapThreshold && _lambda.Count <= PotentialAStar.MaxLanes)
            {
                // Legacy synchronous repair (feature-flag fallback when no cache
                // is attached): exact A* under true alpha, LP lambda potential.
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
                    _choiceScores.Insert(0, BlendedAlphaCost(_pathBuf2, in alpha, d));
                    plan.BestAlphaCost = d;
                    Stats.RepairImproved++;
                }
                // Repair completed => the best candidate IS the exact alpha-optimum.
                plan.Certified = true;
                Stats.CertifiedTrips++;
            }
        }

        /// <summary>Choice over the portfolio with the agent's true alpha.
        /// With a cache attached and ≥3 alternatives, NESTED logit (§4.9):
        /// corridor first via inclusive values, then variant within — fixing
        /// the IIA overlap bias flat logit has over near-duplicate routes.
        /// Flat logit otherwise: u_i = -cost_i + eta*Gumbel_i.</summary>
        private void Choose(TripPlan plan, int s, int t, int nearK)
        {
            var rng = new SplitMix64(plan.Seed);
            bool blended = _choiceScores.Count == plan.Alts.Count;
            float eta = Cfg.LogitScale * plan.BestAlphaCost;
            int best = -1;
            float Score(int i) => blended ? _choiceScores[i] : plan.Alts[i].AlphaCost;

            if (Cfg.NestedLogit && Cache != null && plan.Alts.Count >= 3 && eta > 0)
            {
                int level = plan.Entry?.Level ?? 6;
                // group by corridor identity of the via node
                var nestOf = new ulong[plan.Alts.Count];
                var nests = new List<ulong>(4);
                for (int i = 0; i < plan.Alts.Count; i++)
                {
                    nestOf[i] = Cache.CorridorOf(plan.Alts[i].ViaNode, level);
                    if (!nests.Contains(nestOf[i])) nests.Add(nestOf[i]);
                }
                float etaV = Math.Max(1e-6f, Cfg.NestedLogitTheta * eta);
                // stage 1: corridor by inclusive value IV_c = etaV * lse(-cost/etaV)
                ulong bestNest = 0; float bestNestU = float.NegativeInfinity;
                foreach (var c in nests)
                {
                    float m = float.NegativeInfinity;
                    for (int i = 0; i < plan.Alts.Count; i++)
                        if (nestOf[i] == c) m = Math.Max(m, -Score(i));
                    double sum = 0;
                    for (int i = 0; i < plan.Alts.Count; i++)
                        if (nestOf[i] == c) sum += Math.Exp((-Score(i) - m) / etaV);
                    float iv = m + etaV * (float)Math.Log(sum);
                    float u = iv + eta * rng.NextGumbel();
                    if (u > bestNestU) { bestNestU = u; bestNest = c; }
                }
                // stage 2: variant within the chosen corridor
                float bestU2 = float.NegativeInfinity;
                for (int i = 0; i < plan.Alts.Count; i++)
                {
                    if (nestOf[i] != bestNest) continue;
                    float u = -Score(i) + etaV * rng.NextGumbel();
                    if (u > bestU2) { bestU2 = u; best = i; }
                }
            }
            else
            {
                float bestU = float.NegativeInfinity;
                for (int i = 0; i < plan.Alts.Count; i++)
                {
                    float u = -Score(i) + eta * rng.NextGumbel();
                    if (u > bestU) { bestU = u; best = i; }
                }
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

        /// <summary>§4.9: decision-point probes harvest their meeting nodes too —
        /// every bidirectional search a trip runs samples the choice set.</summary>
        public float ProbeAndHarvest(TripPlan plan, int fromNode, int dest, int profile)
        {
            int k = LiveMetric(profile);
            float d = _q.Distance(_ctx, fromNode, dest, k);
            if (Cache != null && plan.Entry != null && !float.IsPositiveInfinity(d) && _ctx.LastMeetNode >= 0)
                Cache.Harvest(plan.Entry, _ctx.LastMeetNode, (short)k);
            return d;
        }

        /// <summary>§4.8 v2: "individual holdings refresh from the shared tree at
        /// the next decision point" — adopt the best entry via not already held,
        /// if it prices within the envelope of the incumbent. Returns true if a
        /// branch was adopted (caller re-runs its comparison).</summary>
        public bool TryAdoptFromEntry(TripPlan plan, int fromNode, int profile, float incumbentCost)
        {
            if (Cache == null || plan.Entry == null || plan.Alts.Count >= Cfg.MaxAlternatives) return false;
            int n = Cache.CopyVias(plan.Entry, _viaBuf);
            if (n == 0) return false;
            int bestVia = -1; float bestCost = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                int via = _viaBuf[i].Via;
                bool held = false;
                for (int j = 0; j < plan.Alts.Count; j++)
                    if (plan.Alts[j].ViaNode == via) { held = true; break; }
                if (held) continue;
                int k = LiveMetric(profile);
                float d1 = _q.Distance(_ctx, fromNode, via, k);
                if (float.IsPositiveInfinity(d1)) continue;
                float c = d1 + _q.Distance(_ctx, via, plan.Destination, k);
                Stats.Reprices++;
                if (c < bestCost) { bestCost = c; bestVia = via; }
            }
            if (bestVia < 0 || bestCost > incumbentCost * (1 + Cfg.EnvelopeEps)) return false;
            plan.Alts.Add(new Alternative
            {
                ViaNode = bestVia, Destination = plan.Destination,
                AnchorCost = bestCost, AlphaCost = bestCost, // anchor proxy: exact alpha materializes on expansion
            });
            Stats.EntryAdoptions++;
            return true;
        }

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

        /// <summary>
        /// §4.7/§4.9 exploration budget: OFF the trip-planning critical path.
        /// Serves entries with logged certificate gaps (potential-guided A*
        /// under the sampled true alpha — the async repair) and quarantined
        /// entries (free-flow-scenario draw + penalty draw for structural
        /// diversity). Discovered via-nodes are donated to the entry, fixing
        /// the hole for every current and future trip on that corridor.
        /// Returns tasks executed.
        /// </summary>
        public int RunExploration(int budget)
        {
            if (Cache == null || budget <= 0) return 0;
            var entries = Cache.TopExplorationDemand(budget);
            int done = 0;
            foreach (var e in entries)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (e.GapSampleS < 0) { e.Urgent = false; continue; }
                int s = e.GapSampleS, t = e.GapSampleT;
                var alpha = e.GapSampleAlpha;
                int nearProf = _anchors.NearestProfile(alpha);
                int nearK = LiveMetric(nearProf);

                if (e.GapCount > 0)
                {
                    // async repair: exact A* under the sampled alpha, LP potential
                    int P = _anchors.ProfileCount;
                    int liveStart = _anchors.ScenarioBlockStart(Scenario.Live);
                    for (int p0 = 0; p0 < P; p0 += CchQuery.MaxBatch)
                    {
                        int chunk = Math.Min(CchQuery.MaxBatch, P - p0);
                        _q.DistanceMulti(_ctx, s, t, liveStart + p0, chunk, _multiDist, _multiMeet);
                        for (int p = 0; p < chunk; p++) _exploreDstar[p0 + p] = _multiDist[p];
                    }
                    float lb = _anchors.BestLowerBound(in alpha, _exploreDstar, _lambda);
                    if (lb > 0 && _lambda.Count > 0 && _lambda.Count <= PotentialAStar.MaxLanes)
                    {
                        var lamArr = new (int metric, float lambda)[_lambda.Count];
                        for (int i = 0; i < _lambda.Count; i++)
                            lamArr[i] = (LiveMetric(_lambda[i].profile), _lambda[i].lambda);
                        float d = _astar.Search(_ctx, s, t, lamArr,
                            edge => AnchorGrid.AlphaWeightLive(_g, edge, in alpha), _pathBuf2, Cfg.RepairMaxSettled);
                        if (!float.IsPositiveInfinity(d))
                        {
                            int via = RankMaxNode(_pathBuf2);
                            if (via >= 0 && Cache.Harvest(e, via, (short)nearK)) Stats.ExplorationDonated++;
                        }
                    }
                    Cache.DrainGap(e);
                }
                else if (e.Urgent)
                {
                    // quarantine coverage: free-flow-scenario draw (the route that
                    // is excellent once traffic clears must be in the tree) ...
                    int ffK = _anchors.ScenarioBlockStart(Scenario.FreeFlow) + nearProf;
                    float dff = _q.Distance(_ctx, s, t, ffK);
                    if (!float.IsPositiveInfinity(dff))
                    {
                        int via = _ctx.LastMeetNode;
                        if (via >= 0 && Cache.Harvest(e, via, (short)ffK)) Stats.ExplorationDonated++;
                    }
                    // ... plus one penalty draw for structural diversity
                    _penalty.Clear();
                    _q.DistanceWithPath(_ctx, s, t, nearK, _pathBuf);
                    foreach (var edge in _pathBuf) _penalty[edge] = Cfg.PenaltyFactor;
                    float dp = _astar.Search(_ctx, s, t, new[] { (nearK, 1f) },
                        edge =>
                        {
                            float baseW = _anchors.EdgeWeight(_g, edge, nearK);
                            if (!_penalty.TryGetValue(edge, out var mu)) return baseW;
                            return baseW * mu + (mu - 1f) * 0.001f * _g.TimeFree[edge];
                        },
                        _pathBuf2, Cfg.PenaltyMaxSettled);
                    if (!float.IsPositiveInfinity(dp))
                    {
                        int via = RankMaxNode(_pathBuf2);
                        if (via >= 0 && Cache.Harvest(e, via, (short)nearK)) Stats.ExplorationDonated++;
                    }
                    // ... plus a noise-perturbed draw (§4.9): mild multiplicative
                    // edge noise recovers ε-dominated routes that are competitive
                    // everywhere but win nowhere. Noise >= 1 keeps the base-metric
                    // potential admissible.
                    uint salt = (uint)(e.Arrivals * 2654435761u + 12345u);
                    float dn = _astar.Search(_ctx, s, t, new[] { (nearK, 1f) },
                        edge =>
                        {
                            float baseW = _anchors.EdgeWeight(_g, edge, nearK);
                            float u = ((((uint)edge * 2654435761u) ^ salt) >> 16 & 0xFFFFu) / 65536f;
                            return baseW * (1f + 0.15f * u);
                        },
                        _pathBuf2, Cfg.PenaltyMaxSettled);
                    if (!float.IsPositiveInfinity(dn))
                    {
                        int via = RankMaxNode(_pathBuf2);
                        if (via >= 0 && Cache.Harvest(e, via, (short)nearK)) Stats.ExplorationDonated++;
                    }
                }
                e.Urgent = false;
                done++;
                Stats.ExplorationTasks++;
                Stats.ExplorationTimeUs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
            return done;
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
        // §4.9 cache + §4.7 v2 async-repair telemetry
        public long ServedFromCache, DirectGenerations, QuarantineDiversions;
        public long SyncFallbacks;
        public List<double>? SyncFallbackTimesUs;
        public long ExplorationDemandLogged, ExplorationTasks, ExplorationDonated;
        public double ExplorationTimeUs;
        public long DecisionEvents, EntryAdoptions;

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
            ServedFromCache += o.ServedFromCache; DirectGenerations += o.DirectGenerations;
            QuarantineDiversions += o.QuarantineDiversions;
            SyncFallbacks += o.SyncFallbacks;
            if (o.SyncFallbackTimesUs != null) (SyncFallbackTimesUs ??= new List<double>()).AddRange(o.SyncFallbackTimesUs);
            ExplorationDemandLogged += o.ExplorationDemandLogged;
            ExplorationTasks += o.ExplorationTasks; ExplorationDonated += o.ExplorationDonated;
            ExplorationTimeUs += o.ExplorationTimeUs;
            DecisionEvents += o.DecisionEvents; EntryAdoptions += o.EntryAdoptions;
        }
    }
}
