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
        public int SoftClosedCount { get; private set; }

        public SoftClosureDetector(Graph g)
        {
            _g = g;
            _state = new byte[g.EdgeCount];
            _counter = new short[g.EdgeCount];
        }

        /// <summary>Advance the per-edge state machines. occupancy/outflow are the
        /// sim's (or the adapter's) per-edge measurements this tick; jamCapacity is
        /// the max vehicles the edge holds; serviceRate the max per-tick outflow.
        /// Appends edges whose ClosureMult changed to <paramref name="changedEdges"/>
        /// (they need partial customization of the live lanes).</summary>
        public void Tick(float[] occupancy, float[] outflow, float[] jamCapacity, float[] serviceRate, List<int> changedEdges)
        {
            var g = _g;
            for (int e = 0; e < g.EdgeCount; e++)
            {
                if (float.IsPositiveInfinity(g.ClosureMult[e])) continue; // hard closed: owned by WorldEvents
                float jc = jamCapacity[e], sr = serviceRate[e];
                if (jc <= 0 || sr <= 0) continue;
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
                            g.ClosureMult[e] = Math.Min(MultCap, g.ClosureMult[e] * MultGrowth);
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
                if (g.ClosureMult[e] != oldMult) changedEdges.Add(e);
            }
        }

        public bool IsSoftClosed(int edge) => _state[edge] == 2;

        /// <summary>Hard closure / reopen (player edits, WorldEvents).</summary>
        public void SetHardClosed(int edge, bool closed, List<int> changedEdges)
        {
            float target = closed ? float.PositiveInfinity : 1f;
            if (_g.ClosureMult[edge] != target)
            {
                _g.ClosureMult[edge] = target;
                _state[edge] = 0; _counter[edge] = 0;
                changedEdges.Add(edge);
            }
        }
    }
}
