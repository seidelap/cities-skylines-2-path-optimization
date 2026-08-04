using System;
using System.Collections.Generic;

namespace CS2Path.Core
{
    /// <summary>One in-flight trip as the update engine sees it.</summary>
    public sealed class ActiveTrip
    {
        public TripPlan Plan = null!;
        public int PathCursor;          // index of the edge currently being driven
        public int CurrentNode;         // tail of the current edge
        public int RouteVersion;        // bumps on every route switch (stale-index check)
        public float LastRemainingCost; // anchor units at last (re)price
        public bool Finished;
        // sim-owned movement state (harness):
        public int CurEdge = -1;        // edge currently being driven, -1 before departure
        public float EdgeTimeLeft;
        public int QueuedTicks;
        public int DepartTick;
    }

    public sealed class UpdateConfig
    {
        public float Hysteresis = 0.07f;          // switch margin (plan: ~5-10%)
        public float DegradeThreshold = 0.10f;    // per-edge live-time change that counts as degradation
        public float ImproveThreshold = 0.08f;
        public float RegionImproveMassSec = 30f;  // summed improvement (s) to wake a region
        public int MaxWakesPerTick = 4000;
        public float SweepFraction = 0.003f;      // blind sweeper, fraction of agents per tick (plan: 0.1-0.5%)
        public int QueuedHops = 2;                // agents this close to a closure wait instead of replanning
        public float EventGateFactor = 1.5f;      // notified upstream agents per tick ≈ gate * service rate
        public float ProbeDegradeFactor = 1.10f;  // whole-portfolio degradation triggering a probe
    }

    /// <summary>
    /// Layer 4: continuous updates (plan §4 Layer 4). Maintains the
    /// edge → active-agents index and drives the three trigger channels:
    /// (1) push on degradation via the index, (2) improvement wakeups by
    /// via-node region plus a low-rate hash-staggered sweeper, and (3) metered,
    /// upstream-first event invalidation where the notified fraction is sized
    /// to the closed edge's service rate. Refresh work is tiered: re-price the
    /// via-node portfolio (two CCH queries per alternative) → one probe query →
    /// full regeneration, each gated by hysteresis so flapping cannot form.
    /// </summary>
    public sealed class UpdateEngine
    {
        public UpdateConfig Cfg = new UpdateConfig();
        public Telemetry Stats = new Telemetry();

        private readonly Graph _g;
        private readonly TripPlanner _planner;
        private readonly int[] _regionOf;

        // edge -> (agent, routeVersion) entries; validated lazily on read.
        private readonly List<(int agent, int version)>?[] _edgeIndex;
        // region -> agents with a portfolio via-node there.
        private readonly List<(int agent, int version)>?[] _viaRegionIndex;

        private readonly HashSet<int> _wakeSet = new HashSet<int>();
        private readonly List<int> _wakeList = new List<int>();
        private readonly Queue<int> _deferredWakes = new Queue<int>();
        private readonly Dictionary<int, Queue<int>> _closureQueues = new Dictionary<int, Queue<int>>();
        private readonly float[] _regionImprovement;
        private int _sweepCursor;

        public UpdateEngine(Graph g, TripPlanner planner, int[] regionOf, int regionCount)
        {
            _g = g; _planner = planner; _regionOf = regionOf;
            _edgeIndex = new List<(int, int)>?[g.EdgeCount];
            _viaRegionIndex = new List<(int, int)>?[regionCount];
            _regionImprovement = new float[regionCount];
        }

        public void RegisterRoute(int agentId, ActiveTrip trip)
        {
            var path = trip.Plan.ChosenEdgePath;
            for (int i = trip.PathCursor; i < path.Count; i++)
            {
                var list = _edgeIndex[path[i]];
                if (list == null) { list = new List<(int, int)>(4); _edgeIndex[path[i]] = list; }
                list.Add((agentId, trip.RouteVersion));
            }
            foreach (var alt in trip.Plan.Alts)
            {
                int r = _regionOf[alt.ViaNode];
                var list = _viaRegionIndex[r];
                if (list == null) { list = new List<(int, int)>(8); _viaRegionIndex[r] = list; }
                list.Add((agentId, trip.RouteVersion));
            }
        }

        /// <summary>Channel 1 + 2: traffic refresh. changed edges carry
        /// (edge, oldTime, newTime). Fills the wake set, bounded per tick.</summary>
        public void OnTrafficRefresh(List<(int edge, float oldT, float newT)> changed, IReadOnlyList<ActiveTrip> trips)
        {
            Array.Clear(_regionImprovement, 0, _regionImprovement.Length);
            foreach (var (edge, oldT, newT) in changed)
            {
                if (newT > oldT * (1 + Cfg.DegradeThreshold))
                {
                    // push (degradation): a route getting worse is locally detectable
                    var list = _edgeIndex[edge];
                    if (list != null)
                    {
                        int w = 0;
                        for (int i = 0; i < list.Count; i++)
                        {
                            var (agent, ver) = list[i];
                            var t = trips[agent];
                            if (t.Finished || t.RouteVersion != ver) continue; // stale
                            list[w++] = list[i];
                            Wake(agent);
                        }
                        list.RemoveRange(w, list.Count - w);
                    }
                }
                else if (newT < oldT * (1 - Cfg.ImproveThreshold))
                {
                    // improvement is not detectable from the agent's own path,
                    // but it IS detectable from the graph: aggregate by region
                    _regionImprovement[_regionOf[_g.Tail[edge]]] += oldT - newT;
                }
            }
            for (int r = 0; r < _regionImprovement.Length; r++)
            {
                if (_regionImprovement[r] < Cfg.RegionImproveMassSec) continue;
                var list = _viaRegionIndex[r];
                if (list == null) continue;
                int w = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var (agent, ver) = list[i];
                    var t = trips[agent];
                    if (t.Finished || t.RouteVersion != ver) continue;
                    list[w++] = list[i];
                    if (Wake(agent)) Stats.RegionWakes++;
                }
                list.RemoveRange(w, list.Count - w);
            }
        }

        /// <summary>Channel 2b: blind hash-staggered sweeper — the safety net for
        /// improvements in no one's portfolio. Rate is a small fraction per tick,
        /// i.e. every agent reconsidered within a few sim-minutes.</summary>
        public void Sweep(IReadOnlyList<ActiveTrip> trips)
        {
            int n = trips.Count;
            if (n == 0) return;
            int count = Math.Max(1, (int)(n * Cfg.SweepFraction));
            for (int i = 0; i < count; i++)
            {
                _sweepCursor = (_sweepCursor + 1) % n;
                var t = trips[_sweepCursor];
                if (!t.Finished && t.Plan.HasPlan && Wake(_sweepCursor)) Stats.SweeperWakes++;
            }
        }

        /// <summary>Channel 3: closures — metered and upstream-first. Agents
        /// already queued (within QueuedHops) wait: their recovery is the
        /// upstream diversion draining ahead of them. Upstream agents are
        /// notified a few per tick, sized to the edge's service rate — inverting
        /// the vanilla priority, which recomputes for the boxed-in waiter.</summary>
        public void OnClosure(int edge, IReadOnlyList<ActiveTrip> trips)
        {
            var list = _edgeIndex[edge];
            if (list == null) return;
            var upstream = new List<(int agent, int hops)>();
            foreach (var (agent, ver) in list)
            {
                var t = trips[agent];
                if (t.Finished || t.RouteVersion != ver) continue;
                int hops = -1;
                var path = t.Plan.ChosenEdgePath;
                for (int i = t.PathCursor; i < path.Count && i < t.PathCursor + 4096; i++)
                    if (path[i] == edge) { hops = i - t.PathCursor; break; }
                if (hops < 0) continue;
                if (hops <= Cfg.QueuedHops) continue; // boxed in: waiting IS the plan
                upstream.Add((agent, hops));
            }
            upstream.Sort((a, b) => b.hops.CompareTo(a.hops)); // farthest first
            var q = new Queue<int>();
            foreach (var (agent, _) in upstream) q.Enqueue(agent);
            _closureQueues[edge] = q;
        }

        /// <summary>Release metered closure notifications for this tick.</summary>
        public void DrainClosureQueues()
        {
            List<int>? done = null;
            foreach (var kv in _closureQueues)
            {
                int gate = Math.Max(1, (int)(_g.Capacity[kv.Key] * Cfg.EventGateFactor));
                for (int i = 0; i < gate && kv.Value.Count > 0; i++)
                    if (Wake(kv.Value.Dequeue())) Stats.EventWakes++;
                if (kv.Value.Count == 0) (done ??= new List<int>()).Add(kv.Key);
            }
            if (done != null) foreach (var e in done) _closureQueues.Remove(e);
        }

        private bool Wake(int agent)
        {
            if (!_wakeSet.Add(agent)) return false;
            if (_wakeList.Count < Cfg.MaxWakesPerTick) _wakeList.Add(agent);
            else _deferredWakes.Enqueue(agent);
            return true;
        }

        /// <summary>Tiered refresh of every woken agent. Returns number refreshed.
        /// switchExpand re-expands geometry + re-registers when a switch happens.</summary>
        public int ProcessWakes(IReadOnlyList<ActiveTrip> trips, Action<int, int> onSwitched, Func<int, bool> onRegenerate)
        {
            // top up from deferred backlog
            while (_wakeList.Count < Cfg.MaxWakesPerTick && _deferredWakes.Count > 0)
            {
                int a = _deferredWakes.Dequeue();
                if (_wakeSet.Contains(a)) _wakeList.Add(a);
            }
            int processed = 0;
            foreach (var agent in _wakeList)
            {
                var t = trips[agent];
                _wakeSet.Remove(agent);
                if (t.Finished || !t.Plan.HasPlan) continue;
                processed++;
                RefreshAgent(agent, t, onSwitched, onRegenerate);
            }
            _wakeList.Clear();
            // carry any remaining deferred wakes into next tick's list lazily
            return processed;
        }

        private void RefreshAgent(int agent, ActiveTrip t, Action<int, int> onSwitched, Func<int, bool> onRegenerate)
        {
            var plan = t.Plan;
            int profile = plan.NearestProfile;
            int cur = t.CurrentNode;

            // Tier 1: re-price the portfolio — O(k) via-node re-pricing.
            int bestIdx = -1; float bestCost = float.PositiveInfinity;
            float curCost = float.PositiveInfinity;
            for (int i = 0; i < plan.Alts.Count; i++)
            {
                float c = _planner.RepriceAlternative(cur, plan.Alts[i], profile);
                Stats.Reprices++;
                if (i == plan.ChosenIdx) curCost = c;
                if (c < bestCost) { bestCost = c; bestIdx = i; }
            }
            if (bestIdx < 0) return;

            bool wholePortfolioDegraded = bestCost > t.LastRemainingCost * Cfg.ProbeDegradeFactor && t.LastRemainingCost > 0;

            // Tier 1 outcome: switch only past the hysteresis margin.
            if (bestIdx != plan.ChosenIdx && bestCost < curCost * (1 - Cfg.Hysteresis))
            {
                plan.ChosenIdx = bestIdx;
                plan.ChosenAlphaCost = plan.Alts[bestIdx].AlphaCost;
                Stats.Switches++;
                onSwitched(agent, bestIdx);
                t.LastRemainingCost = bestCost;
                return;
            }

            // Tier 2: one probe query against the agent's nearest anchor.
            if (wholePortfolioDegraded || float.IsPositiveInfinity(bestCost))
            {
                float probe = _planner.ProbeDirect(cur, plan.Destination, profile);
                Stats.Probes++;
                // Tier 3: full regeneration only if the probe beats the whole
                // portfolio by the hysteresis margin.
                if (probe < bestCost * (1 - Cfg.Hysteresis))
                {
                    Stats.Regenerations++;
                    onRegenerate(agent);
                    return;
                }
            }
            t.LastRemainingCost = bestCost;
        }
    }
}
