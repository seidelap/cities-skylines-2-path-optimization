using System;
using System.Collections.Generic;
using CS2Path.Core;

namespace CS2Path.Mod
{
    // =====================================================================
    // The ONLY two classes allowed to touch Colossal Order code (plan §5).
    // In this repository they compile against the Core ports with the game
    // touchpoints stubbed behind OUT_OF_GAME_BUILD; the in-game build fills
    // the marked sections with ECS reads/writes. When a game patch renames a
    // component or reshapes a system, the diff-and-repair work is confined
    // to this file.
    //
    // In-game wiring (documented here, implemented in the game build):
    //  * A ModSystemBase subclass disables the vanilla PathfindSetupSystem /
    //    query drain via World.GetOrCreateSystemManaged(...).Enabled = false
    //    (the sanctioned wholesale-replacement pattern — the Burst hot path
    //    cannot be Harmony-patched) and registers the replacement systems.
    //  * Feature flags (FeatureFlags) are surfaced as mod options so every
    //    layer is independently revertible to vanilla behavior at runtime.
    //  * Version discipline: pin game version, per patch decompile + diff the
    //    components read below, repair, re-run the external harness, unpin.
    //  * ORDERING CONSTRAINT (design v3, deliberate): decision-point events
    //    at one decision node must be evaluated ARRIVAL-ORDERED — the platoon
    //    self-metering property (each member sees costs updated by the
    //    diversions of those ahead) and seeded determinism both depend on it.
    //    In the Burst port this is a small serial section per node inside an
    //    otherwise parallel job: group decision events by node, sort each
    //    group by arrival stamp, parallelize ACROSS nodes, evaluate WITHIN a
    //    node sequentially. Do not "fix" this into a fully parallel loop.
    // =====================================================================

    /// <summary>
    /// Reader adapter: vanilla ECS state -> IGraphSource / IMetricFeed /
    /// IWorldEvents. Reads Net.Node / Net.Edge / Net.Curve / Lane buffers /
    /// PathfindCostData and flattens the lane/turn graph into the Core Graph
    /// (edge-based, so turn costs become node expansions).
    /// </summary>
    public sealed class VanillaStateReader : IGraphSource, IMetricFeed, IWorldEvents
    {
        public event Action<int>? EdgeHardClosed;
        public event Action<int>? EdgeReopened;
        public event Action? TopologyChanged;

        public Graph BuildGraph()
        {
#if OUT_OF_GAME_BUILD
            throw new NotSupportedException(
                "Out-of-game build: use the harness SyntheticCity or a serialized graph export. " +
                "In-game, this reads Game.Net entities (nodes, edges, lanes, curves) and " +
                "Game.Prefabs.PathfindCostData into a Core.Graph.");
#else
            // IN-GAME: iterate EntityQuery<Game.Net.Edge>, expand lanes,
            // emit (u, v, timeFree, money, comfort, capacity) tuples,
            // then Graph.Build(...). Node coordinates from Game.Net.Node.m_Position
            // feed nested dissection and the Layer-4 wake-up regions.
#endif
        }

        public int FillLiveTimes(float[] timeLive, List<int> changedEdgesOut)
        {
#if OUT_OF_GAME_BUILD
            throw new NotSupportedException("Out-of-game build: the harness TrafficSim feeds live times.");
#else
            // IN-GAME: read Game.Net.EdgeFlow / lane congestion values each
            // traffic refresh; write per-edge live seconds; report edges whose
            // value moved more than the refresh threshold.
#endif
        }

        internal void RaiseHardClosed(int edge) => EdgeHardClosed?.Invoke(edge);
        internal void RaiseReopened(int edge) => EdgeReopened?.Invoke(edge);
        internal void RaiseTopologyChanged() => TopologyChanged?.Invoke();
    }

    /// <summary>
    /// Writer adapter: chosen PlanPortfolio selections -> the components the
    /// movement systems consume (PathOwner, PathElement buffers, CarNavigation
    /// targets). The core never sees a game type; this class never computes.
    /// </summary>
    public sealed class VanillaPlanWriter : IPlanWriter
    {
        public void WritePlan(int agentId, int[] edgePath)
        {
#if OUT_OF_GAME_BUILD
            throw new NotSupportedException("Out-of-game build: the harness consumes plans directly.");
#else
            // IN-GAME: map core edge ids back to lane entities (the id table is
            // produced by VanillaStateReader.BuildGraph) and fill the agent's
            // PathElement dynamic buffer, resetting PathOwner state so the
            // vanilla movement systems drive the new path unchanged.
#endif
        }
    }
}
