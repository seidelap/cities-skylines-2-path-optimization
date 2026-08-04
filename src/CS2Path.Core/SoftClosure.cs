using System;
using System.Collections.Generic;

namespace CS2Path.Core
{
    /// <summary>
    /// Layer 1: effective-closure handling as a first-class edge state (plan §4
    /// Layer 1), detected once at infrastructure level rather than rediscovered
    /// per agent by wait-timers. Sustained outflow ≈ 0 at occupancy ≈ capacity
    /// for T ticks marks an edge soft-closed, with hysteresis on entry and exit.
    /// The planning-cost multiplier inflates CONTINUOUSLY with jam duration
    /// (1.2 → 1.4 → effectively ∞), so the divert-drain-flood-back oscillation
    /// cannot form. Hard closures set the multiplier to +inf.
    /// </summary>
    public sealed class SoftClosureDetector
    {
        public float OccupancyEnter = 0.9f;   // fraction of jam capacity
        public float OutflowEnterFrac = 0.05f;// outflow below this fraction of service rate counts as "≈ 0"
        public int EnterTicks = 5;            // sustained duration to enter (hysteresis)
        public float OutflowExitFrac = 0.35f;
        public int ExitTicks = 4;
        public float MultStart = 1.2f;
        public float MultGrowth = 1.06f;      // per tick while jammed
        public float MultCap = 64f;           // "effectively ∞" for planning
        public float MultDecay = 0.85f;       // per tick while recovering

        private readonly Graph _g;
        private readonly byte[] _state;       // 0 open, 1 entering, 2 soft-closed, 3 exiting
        private readonly short[] _counter;
        // Edges with a live state machine (state != 0, or an entry counter
        // accumulating). Windowed ticking must keep stepping these even after
        // they leave the caller's hot window, or exits/decay/counters stall.
        private readonly HashSet<int> _active = new HashSet<int>();
        private readonly List<int> _activeScratch = new List<int>(256);
        public int SoftClosedCount { get; private set; }
        public long LastExamined; // telemetry: edges stepped last tick

        public SoftClosureDetector(Graph g)
        {
            _g = g;
            _state = new byte[g.EdgeCount];
            _counter = new short[g.EdgeCount];
        }

        /// <summary>Advance the per-edge state machines over ALL edges — linear
        /// in network size; kept for testing/A-B. Prefer TickWindowed.</summary>
        public void Tick(float[] occupancy, float[] outflow, float[] jamCapacity, float[] serviceRate, List<int> changedEdges)
        {
            LastExamined = _g.EdgeCount;
            for (int e = 0; e < _g.EdgeCount; e++)
                StepEdge(e, occupancy, outflow, jamCapacity, serviceRate, changedEdges);
        }

        /// <summary>
        /// Windowed detection (design v3): step only <paramref name="hotEdges"/>
        /// (caller-maintained: occupancy above a threshold — the movement code
        /// already touches those) plus the detector's own active set. LOSSLESS
        /// versus the full scan as long as the hot threshold is at or below
        /// OccupancyEnter x jam: an edge cannot begin entering while outside the
        /// window, and everything mid-state-machine stays in the active set
        /// until it returns to idle.
        /// </summary>
        public void TickWindowed(HashSet<int> hotEdges, float[] occupancy, float[] outflow,
                                 float[] jamCapacity, float[] serviceRate, List<int> changedEdges)
        {
            LastExamined = 0;
            foreach (var e in hotEdges)
            {
                StepEdge(e, occupancy, outflow, jamCapacity, serviceRate, changedEdges);
                LastExamined++;
            }
            _activeScratch.Clear();
            foreach (var e in _active) if (!hotEdges.Contains(e)) _activeScratch.Add(e);
            foreach (var e in _activeScratch)
            {
                StepEdge(e, occupancy, outflow, jamCapacity, serviceRate, changedEdges);
                LastExamined++;
            }
        }

        private void StepEdge(int e, float[] occupancy, float[] outflow, float[] jamCapacity, float[] serviceRate, List<int> changedEdges)
        {
            var g = _g;
            // hard closed (owned by WorldEvents): skip only when the detector
            // itself is idle for the edge, so a runaway MultCap can never be
            // confused with a hard closure and freeze the state machine
            if (float.IsPositiveInfinity(g.ClosureMult[e]) && _state[e] == 0) { _active.Remove(e); return; }
            float jc = jamCapacity[e], sr = serviceRate[e];
            if (jc <= 0 || sr <= 0) return;
            bool jammed = occupancy[e] >= OccupancyEnter * jc && outflow[e] <= OutflowEnterFrac * sr;
            bool flowing = outflow[e] >= OutflowExitFrac * sr || occupancy[e] < 0.5f * jc;
            float oldMult = g.ClosureMult[e];
            switch (_state[e])
            {
                case 0:
                    if (jammed && ++_counter[e] >= EnterTicks)
                    {
                        _state[e] = 2; _counter[e] = 0;
                        g.ClosureMult[e] = MultStart;
                        SoftClosedCount++;
                    }
                    else if (!jammed) _counter[e] = 0;
                    break;
                case 2:
                    if (flowing && ++_counter[e] >= ExitTicks)
                    {
                        _state[e] = 3; _counter[e] = 0;
                    }
                    else
                    {
                        if (!flowing) _counter[e] = 0;
                        // clamp below +inf regardless of user MultCap: +inf is
                        // the hard-closure sentinel and must stay reserved
                        g.ClosureMult[e] = Math.Min(Math.Min(MultCap, 1e30f), g.ClosureMult[e] * MultGrowth);
                    }
                    break;
                case 3:
                    g.ClosureMult[e] = Math.Max(1f, g.ClosureMult[e] * MultDecay);
                    if (g.ClosureMult[e] <= 1.001f)
                    {
                        g.ClosureMult[e] = 1f; _state[e] = 0; _counter[e] = 0;
                        SoftClosedCount--;
                    }
                    else if (jammed) { _state[e] = 2; _counter[e] = 0; } // re-jam: back to closed
                    break;
            }
            if (_state[e] != 0 || _counter[e] > 0) _active.Add(e);
            else _active.Remove(e);
            if (g.ClosureMult[e] != oldMult) changedEdges.Add(e);
        }

        public bool IsSoftClosed(int edge) => _state[edge] == 2;

        /// <summary>Hard closure / reopen (player edits, WorldEvents).</summary>
        public void SetHardClosed(int edge, bool closed, List<int> changedEdges)
        {
            float target = closed ? float.PositiveInfinity : 1f;
            if (_g.ClosureMult[edge] != target)
            {
                if (_state[edge] == 2 || _state[edge] == 3) SoftClosedCount--; // soft state absorbed by the hard closure
                _g.ClosureMult[edge] = target;
                _state[edge] = 0; _counter[edge] = 0;
                _active.Remove(edge);
                changedEdges.Add(edge);
            }
        }
    }
}
