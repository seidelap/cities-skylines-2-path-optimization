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
        public double MsPlanning, MsMovement, MsRefresh, MsCustomize, MsWakes;

        private int _scheduleCursor;
        private float[]? _lastNotifiedMult;
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
            var sw = new Stopwatch();
            for (int i = 0; i < ticks; i++)
            {
                Tick++;
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
                    trip.LastRemainingCost = plan.Alts[plan.ChosenIdx].AnchorCost;
                    Trips.Add(trip);
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
                }
            }
        }

        // ------------------------------------------------------------------
        private void Movement()
        {
            Array.Clear(ExitsThisTick, 0, ExitsThisTick.Length);
            var g = G;
            for (int a = 0; a < Trips.Count; a++)
            {
                var t = Trips[a];
                if (t.Finished || !t.Plan.HasPlan) continue;
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
                    MeasureExit(a, t, e);
                    Finish(a, t);
                    continue;
                }
                int nxt = path[t.PathCursor];
                if (Occ[nxt] >= JamCap[nxt]) { t.QueuedTicks++; MaybeVanillaWaitReplan(a, t); continue; } // spillback
                ExitsThisTick[e]++; OutflowWindow[e]++;
                Occ[e] = Math.Max(0, Occ[e] - 1);
                MeasureExit(a, t, e);
                Enter(a, t, nxt);
            }
        }

        private void Enter(int agent, ActiveTrip t, int e)
        {
            Occ[e] += 1;
            t.CurEdge = e;
            t.CurrentNode = G.Head[e];
            t.PathCursor++;
            t.QueuedTicks = 0;
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
        public float LiveEstimate(int e)
        {
            float drain = G.TimeFree[e] + Occ[e] / Math.Max(0.5f, G.Capacity[e]) * Dt;
            return Math.Max(LiveEst[e], drain);
        }

        private void Finish(int agent, ActiveTrip t)
        {
            t.Finished = true;
            t.CurEdge = -1;
            FinishedTrips++;
            TotalTravelTicks += Tick - t.DepartTick;
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

            // live estimate -> TimeLive for materially changed edges
            for (int e = 0; e < g.EdgeCount; e++)
            {
                float old = g.TimeLive[e], now = LiveEstimate(e);
                if (Math.Abs(now - old) > LiveChangeThreshold * old)
                {
                    g.TimeLive[e] = now;
                    _changedEdges.Add(e);
                    _changedTriples.Add((e, old, now));
                }
            }

            // Layer-1 soft-closure state machines (uses the refresh window's outflow)
            var closureNotify = new List<int>();
            if (Detector != null)
            {
                var serviceRate = g.Capacity;
                var outflowAvg = OutflowWindow;
                for (int e = 0; e < outflowAvg.Length; e++) outflowAvg[e] /= RefreshInterval;
                Detector.Tick(Occ, outflowAvg, JamCap, serviceRate, _closureChanged);
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
        }

        private void OnSwitched(int agent, int altIdx)
        {
            var t = Trips[agent];
            Planner!.ExpandAlternative(t.Plan, altIdx, t.CurrentNode, _expandBuf);
            t.Plan.ChosenEdgePath.Clear();
            t.Plan.ChosenEdgePath.AddRange(_expandBuf);
            t.PathCursor = 0;
            t.RouteVersion++;
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
            t.Plan = fresh;
            t.PathCursor = 0;
            t.RouteVersion++;
            t.LastRemainingCost = fresh.Alts[fresh.ChosenIdx].AnchorCost;
            Upd!.RegisterRoute(agent, t);
            return true;
        }
    }
}
