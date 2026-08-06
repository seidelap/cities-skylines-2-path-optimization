using System;
using System.Collections.Generic;
using System.Diagnostics;
using CS2Path.Core;

namespace CS2Path.Harness
{
    public enum SimMode
    {
        /// <summary>This mod: CCH portfolios + logit + hysteresis + staggered
        /// continuous updates + soft closures.</summary>
        Rebuild,
        /// <summary>Vanilla-style control: per-trip exact Dijkstra against a
        /// LAGGED shared cost snapshot, plans frozen at trip start, re-plan only
        /// via wait-timers (plan §1.1/§1.2). This reproduces the herding
        /// mechanism: deterministic best-response to a stale shared signal.</summary>
        Vanilla,
    }

    /// <summary>
    /// Mesoscopic traffic simulation: per-edge storage capacity (spillback) and
    /// per-edge service rate (queues). Drives either routing mode over an
    /// identical demand schedule so behavior is A/B comparable.
    /// </summary>
    public sealed class TrafficSim
    {
        public Graph G = null!;
        public SimMode Mode;
        public RoutingEngine? Eng;              // Rebuild mode
        public TripPlanner? Planner;
        public UpdateEngine? Upd;
        public SoftClosureDetector? Detector;
        public float[] JamCap = null!;

        public float Dt = 4f;                   // sim-seconds per tick
        public int TicksPerDay = int.MaxValue;  // time-of-day bucketing for entry telemetry
        public int RefreshInterval = 5;         // ticks between live-cost refreshes (Rebuild)
        public int SnapshotInterval = 40;       // ticks between snapshot updates (Vanilla lag)
        public int VanillaWaitReplanTicks = 25;
        public float LiveChangeThreshold = 0.03f;

        public List<ActiveTrip> Trips = new List<ActiveTrip>();
        public List<TripRequest> Schedule = new List<TripRequest>(); // sorted by DepartTick
        public List<int> DepartTicks = new List<int>();

        // sim state
        public float[] Occ = null!;
        public int[] ExitsThisTick = null!;
        public float[] OutflowWindow = null!;   // exits accumulated over the refresh window
        public float[] LiveEst = null!;         // EMA of measured traverse times
        public int[] EnterTick = null!;         // per-agent
        public float[] Snapshot = null!;        // Vanilla: lagged shared cost snapshot
        public int Tick;

        // measurement hooks
        public Action<int, int>? OnEdgeEnter;   // (agent, edge)
        public long FinishedTrips, TotalTravelTicks, VanillaQueries, VanillaWaitReplans;
        public double MsPlanning, MsMovement, MsRefresh, MsCustomize, MsWakes, MsExploration;
        public int ExplorationBudgetPerRefresh = 6;

        private int _scheduleCursor;
        private float[]? _lastNotifiedMult;
        private float[]? _typicalCustomized;
        private readonly List<int> _typicalChanged = new List<int>();
        // hot window for the soft-closure detector (design v3): edges above half
        // jam occupancy, maintained O(1) by the movement code that already
        // touches them. Threshold 0.5 < OccupancyEnter 0.9 => provably lossless.
        private readonly HashSet<int> _hotEdges = new HashSet<int>();
        private const float HotFrac = 0.5f;
        private readonly List<int> _changedEdges = new List<int>();
        private readonly List<(int, float, float)> _changedTriples = new List<(int, float, float)>();
        private readonly List<int> _closureChanged = new List<int>();
        private readonly List<int> _expandBuf = new List<int>(512);
        private SplitMix64 _rng = new SplitMix64(999);

        public static TrafficSim Create(Graph g, float[] jamCap, SimMode mode, RoutingEngine? eng)
        {
            var sim = new TrafficSim
            {
                G = g, JamCap = jamCap, Mode = mode, Eng = eng,
                Occ = new float[g.EdgeCount],
                ExitsThisTick = new int[g.EdgeCount],
                OutflowWindow = new float[g.EdgeCount],
                LiveEst = (float[])g.TimeFree.Clone(),
                Snapshot = (float[])g.TimeFree.Clone(),
            };
            if (mode == SimMode.Rebuild)
            {
                sim.Planner = new TripPlanner(eng!.Metrics, eng.Query);
                sim.Planner.Cache = new ClusterCache(eng.CellPaths); // §4.9
                sim.Upd = new UpdateEngine(g, sim.Planner, eng.RegionOf, eng.RegionCount);
                sim.Detector = new SoftClosureDetector(g);
            }
            return sim;
        }

        public void AddTrip(int departTick, in TripRequest req)
        {
            DepartTicks.Add(departTick);
            Schedule.Add(req);
        }

        public void Run(int ticks, Action<int>? perTick = null)
        {
            // departures must be time-ordered or the cursor blocks on the first
            // late trip and then releases everything as one synchronized flood.
            // Sort the PENDING suffix each Run call so multi-day drivers can
            // append the next day's trips between runs.
            if (Schedule.Count - _scheduleCursor > 1)
            {
                int pend = Schedule.Count - _scheduleCursor;
                var order = new int[pend];
                for (int i = 0; i < pend; i++) order[i] = _scheduleCursor + i;
                var keys = new int[pend];
                for (int i = 0; i < pend; i++) keys[i] = DepartTicks[_scheduleCursor + i];
                Array.Sort(keys, order);
                var sched2 = new List<TripRequest>(pend);
                var ticks2 = new List<int>(pend);
                foreach (var i in order) { sched2.Add(Schedule[i]); ticks2.Add(DepartTicks[i]); }
                for (int i = 0; i < pend; i++)
                {
                    Schedule[_scheduleCursor + i] = sched2[i];
                    DepartTicks[_scheduleCursor + i] = ticks2[i];
                }
            }
            var sw = new Stopwatch();
            for (int i = 0; i < ticks; i++)
            {
                Tick++;
                if (Planner != null)
                {
                    Planner.Cache?.RefillDirectGenBudget();
                    if (TicksPerDay != int.MaxValue)
                        Planner.CurrentTodBucket = TodBucketOf(Tick);
                }
                sw.Restart(); Departures(); MsPlanning += sw.Elapsed.TotalMilliseconds;
                sw.Restart(); Movement(); MsMovement += sw.Elapsed.TotalMilliseconds;
                if (Mode == SimMode.Rebuild && Tick % RefreshInterval == 0) RefreshRebuild();
                if (Mode == SimMode.Vanilla && Tick % SnapshotInterval == 0)
                    for (int e = 0; e < G.EdgeCount; e++) Snapshot[e] = LiveEstimate(e);
                perTick?.Invoke(Tick);
            }
        }

        // ------------------------------------------------------------------
        private void Departures()
        {
            while (_scheduleCursor < Schedule.Count && DepartTicks[_scheduleCursor] <= Tick)
            {
                var req = Schedule[_scheduleCursor++];
                var trip = new ActiveTrip { DepartTick = Tick };
                if (Mode == SimMode.Rebuild)
                {
                    var plan = Planner!.PlanFixed(in req);
                    trip.Plan = plan;
                    if (!plan.HasPlan) { trip.Finished = true; Trips.Add(trip); continue; }
                    trip.CurrentNode = req.Origin;
                    // seed the bound stamp in the same BLENDED units Layer 4 compares in
                    trip.LastRemainingCost = Planner.RemainingPathCostBlended(
                        plan.ChosenEdgePath, 0, plan.NearestProfile, plan.StableBlend);
                    trip.DepartureEntry = plan.Entry;
                    float pred = 0f;
                    foreach (var e in plan.ChosenEdgePath) pred += G.TimeLive[e];
                    trip.PredictedSeconds = pred;
                    ArmTriggers(trip);
                    Trips.Add(trip);
                    _activeIdx.Add(Trips.Count - 1);
                    Upd!.RegisterRoute(Trips.Count - 1, trip);
                }
                else
                {
                    var path = new List<int>(256);
                    var alpha = req.Alpha;
                    float d = Reference.Dijkstra(G, req.Origin, req.Destination,
                        e => alpha.Dot(Snapshot[e] * G.ClosureMult[e], G.Money[e], G.Comfort[e]), path);
                    VanillaQueries++;
                    var plan = new TripPlan { AgentId = req.AgentId, Origin = req.Origin, Destination = req.Destination, Alpha = alpha };
                    if (!float.IsPositiveInfinity(d))
                    {
                        plan.ChosenIdx = 0;
                        plan.Alts.Add(new Alternative { ViaNode = req.Origin, Destination = req.Destination });
                        plan.ChosenEdgePath.AddRange(path);
                    }
                    trip.Plan = plan;
                    trip.CurrentNode = req.Origin;
                    if (!plan.HasPlan) trip.Finished = true;
                    Trips.Add(trip);
                    if (!trip.Finished) _activeIdx.Add(Trips.Count - 1);
                }
            }
        }

        // ------------------------------------------------------------------
        // live agent index: Movement is O(active), not O(all trips ever) —
        // multi-day runs would otherwise scan every finished trip each tick
        private readonly List<int> _activeIdx = new List<int>();

        private void Movement()
        {
            Array.Clear(ExitsThisTick, 0, ExitsThisTick.Length);
            var g = G;
            int w0 = 0;
            for (int ii = 0; ii < _activeIdx.Count; ii++)
            {
                int a = _activeIdx[ii];
                var t = Trips[a];
                if (t.Finished || !t.Plan.HasPlan) continue;
                _activeIdx[w0++] = a;
                var path = t.Plan.ChosenEdgePath;

                if (t.CurEdge < 0)
                {
                    // waiting to enter first edge — the wait-timer applies here
                    // too, or origin-blocked vanilla agents can never replan
                    if (t.PathCursor >= path.Count) { Finish(a, t); continue; }
                    int e0 = path[t.PathCursor];
                    if (Occ[e0] < JamCap[e0]) Enter(a, t, e0);
                    else { t.QueuedTicks++; MaybeVanillaWaitReplan(a, t); }
                    continue;
                }

                t.EdgeTimeLeft -= Dt;
                if (t.EdgeTimeLeft > 0) continue;

                int e = t.CurEdge;
                if (ExitsThisTick[e] >= (int)Math.Max(1, g.Capacity[e])) { t.QueuedTicks++; MaybeVanillaWaitReplan(a, t); continue; }

                if (t.PathCursor >= path.Count)
                {
                    // exit network: arrival
                    ExitsThisTick[e]++; OutflowWindow[e]++;
                    Occ[e] = Math.Max(0, Occ[e] - 1);
                    if (Occ[e] < HotFrac * JamCap[e]) _hotEdges.Remove(e);
                    MeasureExit(a, t, e);
                    Finish(a, t);
                    continue;
                }
                int nxt = path[t.PathCursor];
                if (Occ[nxt] >= JamCap[nxt]) { t.QueuedTicks++; MaybeVanillaWaitReplan(a, t); continue; } // spillback
                ExitsThisTick[e]++; OutflowWindow[e]++;
                Occ[e] = Math.Max(0, Occ[e] - 1);
                if (Occ[e] < HotFrac * JamCap[e]) _hotEdges.Remove(e);
                MeasureExit(a, t, e);
                Enter(a, t, nxt);
            }
            _activeIdx.RemoveRange(w0, _activeIdx.Count - w0);
        }

        /// <summary>§4 L4 v2: decision-point triggers spaced along the trunk
        /// (approximating the nest tree's branch points + sparse virtual
        /// checkpoints on long trunks).</summary>
        private void ArmTriggers(ActiveTrip t)
        {
            int len = t.Plan.ChosenEdgePath.Count;
            t.TriggerSpacing = Math.Max(8, len / 6);
            t.NextTriggerAt = t.PathCursor + t.TriggerSpacing;
            var cache = Planner?.Cache;
            if (cache != null)
                t.LastCorridorCode = cache.CorridorOf(t.CurrentNode, t.Plan.Entry?.Level ?? 6);
        }

        private void Enter(int agent, ActiveTrip t, int e)
        {
            Occ[e] += 1;
            if (Occ[e] >= HotFrac * JamCap[e]) _hotEdges.Add(e);
            t.CurEdge = e;
            t.CurrentNode = G.Head[e];
            t.PathCursor++;
            t.QueuedTicks = 0;
            if (Upd != null && t.PathCursor < t.Plan.ChosenEdgePath.Count)
            {
                // structural branch points: corridor-cell boundary crossings
                bool fire = false;
                var cache = Planner?.Cache;
                if (cache != null)
                {
                    ulong code = cache.CorridorOf(t.CurrentNode, t.Plan.Entry?.Level ?? 6);
                    if (code != t.LastCorridorCode) { t.LastCorridorCode = code; fire = true; }
                }
                // virtual checkpoints on long trunks
                if (t.PathCursor >= t.NextTriggerAt) fire = true;
                if (fire)
                {
                    t.NextTriggerAt = t.PathCursor + t.TriggerSpacing;
                    Upd.OnDecisionPoint(agent);
                }
            }
            float density = Occ[e] / Math.Max(1f, JamCap[e]);
            t.EdgeTimeLeft = G.TimeFree[e] * (1f + 0.3f * density * density);
            if (EnterTick == null || EnterTick.Length < Trips.Count) Array.Resize(ref EnterTick, Math.Max(Trips.Count * 2, 1024));
            EnterTick[agent] = Tick;
            OnEdgeEnter?.Invoke(agent, e);
        }

        private void MeasureExit(int agent, ActiveTrip t, int e)
        {
            float measured = Math.Max(1, Tick - EnterTick[agent]) * Dt;
            LiveEst[e] = 0.85f * LiveEst[e] + 0.15f * measured;
        }

        /// <summary>Current live-time estimate for an edge: the exit-measured EMA
        /// blended with a queue-drain bound (freeflow + occupancy/serviceRate).
        /// Exit measurements alone go blind under gridlock — nothing exits, so
        /// the signal never learns and a jammed corridor keeps attracting demand.
        /// Real traffic telemetry sees queue state; both routing modes get this
        /// same estimator.</summary>
        /// <summary>Congestion-signal regime. The vanilla baseline is bistable in
        /// it: a FAST signal (occupancy-proportional) makes lagged best-response
        /// oscillate between corridors; a SLOW signal (queue-excess only) makes
        /// it collapse into absorbing single-corridor gridlock. The rebuild is
        /// stable under both — the herding A/B reports both regimes.</summary>
        public bool OccupancyProportionalSignal;

        // EMA bank (register #4 / multi-timescale note): the queue-drain term is
        // itself EMA-damped (fast member) before max-ing with the exit-measured
        // medium EMA; the hours member is the typical scenario. One
        // multiply-accumulate per edge per refresh.
        private float[]? _drainEma;

        public float LiveEstimate(int e)
        {
            float cap = Math.Max(0.5f, G.Capacity[e]);
            float drain;
            if (OccupancyProportionalSignal)
            {
                drain = G.TimeFree[e] + Occ[e] / cap * Dt;
            }
            else
            {
                // queue delay = vehicles beyond free-flow transit, drained at the
                // service rate; lightly-occupied edges report exactly free-flow so
                // single-vehicle wobble never churns the customization layer
                float ffOcc = cap * (G.TimeFree[e] / Dt);
                float queueExcess = Math.Max(0f, Occ[e] - ffOcc);
                drain = G.TimeFree[e] + queueExcess / cap * Dt;
            }
            _drainEma ??= (float[])G.TimeFree.Clone();
            _drainEma[e] += 0.3f * (drain - _drainEma[e]);
            return Math.Max(LiveEst[e], _drainEma[e]);
        }

        public int TodBucketOf(int tick)
            => TicksPerDay == int.MaxValue ? 0
             : (int)((long)(tick % TicksPerDay) * ClusterEntry.TodBuckets / TicksPerDay);

        private void Finish(int agent, ActiveTrip t)
        {
            t.Finished = true;
            t.CurEdge = -1;
            t.FinishTick = Tick;
            FinishedTrips++;
            TotalTravelTicks += Tick - t.DepartTick;
            // realized travel-time telemetry, attributed to the entry the trip
            // DEPARTED under (regeneration re-keys Plan.Entry to a partial leg)
            var cache = Planner?.Cache;
            if (cache != null && t.DepartureEntry != null)
                cache.RecordRealized(t.DepartureEntry, TodBucketOf(t.DepartTick),
                    (Tick - t.DepartTick) * Dt, t.PredictedSeconds);
        }

        private void MaybeVanillaWaitReplan(int agent, ActiveTrip t)
        {
            if (Mode != SimMode.Vanilla || t.QueuedTicks < VanillaWaitReplanTicks) return;
            t.QueuedTicks = 0;
            VanillaWaitReplans++;
            var alpha = t.Plan.Alpha;
            var path = new List<int>(128);
            float d = Reference.Dijkstra(G, t.CurrentNode, t.Plan.Destination,
                e => alpha.Dot(Snapshot[e] * G.ClosureMult[e], G.Money[e], G.Comfort[e]), path);
            VanillaQueries++;
            if (float.IsPositiveInfinity(d)) return;
            t.Plan.ChosenEdgePath.Clear();
            t.Plan.ChosenEdgePath.AddRange(path);
            t.PathCursor = 0;
        }

        // ------------------------------------------------------------------
        private void RefreshRebuild()
        {
            var sw = Stopwatch.StartNew();
            var g = G;
            _changedEdges.Clear(); _changedTriples.Clear(); _closureChanged.Clear();

            // live estimate -> TimeLive for materially changed edges; the
            // typical scenario tracks a slow rolling average of the same signal
            // (plan §4 L1: "typical scenarios learned from rolling congestion
            // averages") and recustomizes only when it drifts materially
            bool hasTypical = Eng!.Anchors.HasScenario(CS2Path.Core.Scenario.Typical);
            if (hasTypical && _typicalCustomized == null)
                _typicalCustomized = (float[])g.TimeTypical.Clone();
            for (int e = 0; e < g.EdgeCount; e++)
            {
                float est = LiveEstimate(e);
                float old = g.TimeLive[e];
                if (Math.Abs(est - old) > LiveChangeThreshold * old)
                {
                    g.TimeLive[e] = est;
                    _changedEdges.Add(e);
                    _changedTriples.Add((e, old, est));
                }
                if (hasTypical)
                {
                    g.TimeTypical[e] = 0.98f * g.TimeTypical[e] + 0.02f * est;
                    if (Math.Abs(g.TimeTypical[e] - _typicalCustomized![e]) > LiveChangeThreshold * _typicalCustomized[e])
                    {
                        _typicalChanged.Add(e);
                        _typicalCustomized[e] = g.TimeTypical[e];
                    }
                }
            }

            // Layer-1 soft-closure state machines (uses the refresh window's outflow)
            var closureNotify = new List<int>();
            if (Detector != null)
            {
                var serviceRate = g.Capacity;
                var outflowAvg = OutflowWindow;
                for (int e = 0; e < outflowAvg.Length; e++) outflowAvg[e] /= RefreshInterval;
                Detector.TickWindowed(_hotEdges, Occ, outflowAvg, JamCap, serviceRate, _closureChanged);
                // notify Layer 4 on soft-closure onset AND on each doubling of the
                // multiplier while the jam persists (post-onset registrants and
                // sustained escalation both need wakes)
                if (_lastNotifiedMult == null || _lastNotifiedMult.Length < g.EdgeCount)
                    _lastNotifiedMult = new float[g.EdgeCount];
                foreach (var e in _closureChanged)
                {
                    float m = g.ClosureMult[e];
                    if (m <= 1.001f) { _lastNotifiedMult[e] = 0f; continue; }
                    if (_lastNotifiedMult[e] <= 0f || m >= 2f * _lastNotifiedMult[e])
                    {
                        closureNotify.Add(e);
                        _lastNotifiedMult[e] = m;
                    }
                }
                Array.Clear(OutflowWindow, 0, OutflowWindow.Length);
            }
            MsRefresh += sw.Elapsed.TotalMilliseconds;

            // partial customization: live lanes for traffic deltas, ALL lanes for
            // closure-state transitions (hard closures define every scenario)
            sw.Restart();
            if (_changedEdges.Count > 0) Eng!.RefreshLive(_changedEdges);
            if (_typicalChanged.Count > 0) { Eng!.RefreshTypical(_typicalChanged); _typicalChanged.Clear(); }
            if (_closureChanged.Count > 0) Eng!.RefreshClosures(_closureChanged);
            MsCustomize += sw.Elapsed.TotalMilliseconds;

            // Layer-4 channels
            sw.Restart();
            if (_changedTriples.Count > 0) Upd!.OnTrafficRefresh(_changedTriples, Trips);
            foreach (var e in closureNotify) Upd!.OnClosure(e, Trips);
            Upd!.DrainClosureQueues();
            Upd.Sweep(Trips, RefreshInterval);
            Upd.ProcessWakes(Trips, OnSwitched, OnRegenerate);
            MsWakes += sw.Elapsed.TotalMilliseconds;

            // §4.7/§4.9 exploration budget — off the trip-planning critical path
            // (in-game: a background job; here: a bounded slice per refresh)
            sw.Restart();
            Planner!.RunExploration(ExplorationBudgetPerRefresh);
            MsExploration += sw.Elapsed.TotalMilliseconds;
        }

        private void OnSwitched(int agent, int altIdx)
        {
            var t = Trips[agent];
            Planner!.ExpandAlternative(t.Plan, altIdx, t.CurrentNode, _expandBuf);
            t.Plan.ChosenEdgePath.Clear();
            t.Plan.ChosenEdgePath.AddRange(_expandBuf);
            t.PathCursor = 0;
            t.RouteVersion++;
            ArmTriggers(t);
            Upd!.RegisterRoute(agent, t);
        }

        private bool OnRegenerate(int agent)
        {
            var t = Trips[agent];
            var old = t.Plan;
            var req = new TripRequest
            {
                AgentId = old.AgentId, Origin = t.CurrentNode, Destination = old.Destination,
                Alpha = old.Alpha, Seed = old.Seed ^ 0x5DEECE66DUL,
            };
            var fresh = Planner!.PlanFixed(in req);
            if (!fresh.HasPlan) return false;
            fresh.Seed = old.Seed;
            t.Plan = fresh;                 // DepartureEntry intentionally kept: telemetry stays on the original OD
            t.PathCursor = 0;
            t.RouteVersion++;
            t.LastRemainingCost = Planner.RemainingPathCostBlended(
                fresh.ChosenEdgePath, 0, fresh.NearestProfile, fresh.StableBlend);
            ArmTriggers(t);
            Upd!.RegisterRoute(agent, t);
            return true;
        }
    }
}
