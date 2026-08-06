using System;
using System.Collections.Generic;

namespace CS2Path.Core
{
    // ---------------------------------------------------------------------
    // Ports (plan §5). The routing core communicates with the game ONLY
    // through these interfaces. Exactly two adapter classes (CS2Path.Mod)
    // are allowed to reference Colossal Order code; when a game patch
    // reshapes a component, the repair diff is confined to those adapters.
    // ---------------------------------------------------------------------

    /// <summary>Topology + static edge attributes in. One snapshot per rebuild.</summary>
    public interface IGraphSource
    {
        Graph BuildGraph();
    }

    /// <summary>Live costs and closure states in, once per traffic refresh.</summary>
    public interface IMetricFeed
    {
        /// <summary>Write current live travel times into <paramref name="timeLive"/>
        /// (seconds, indexed by edge id) and return the ids of edges whose live
        /// cost changed materially since the previous refresh.</summary>
        int FillLiveTimes(float[] timeLive, List<int> changedEdgesOut);
    }

    /// <summary>Topology edits and hard closures in.</summary>
    public interface IWorldEvents
    {
        event Action<int> EdgeHardClosed;
        event Action<int> EdgeReopened;
        /// <summary>Structural edit (new/removed road). Triggers async Layer-0 rebuild.</summary>
        event Action TopologyChanged;
    }

    /// <summary>Chosen plans back out to the movement systems.</summary>
    public interface IPlanWriter
    {
        /// <summary>Hand the expanded lane-level edge sequence for one agent
        /// back to the vehicle/pedestrian movement components.</summary>
        void WritePlan(int agentId, int[] edgePath);
    }

    // ---------------------------------------------------------------------
    // Trip request / plan types (queries in, via-node plans out).
    // ---------------------------------------------------------------------

    public enum TripKind : byte { FixedDestination, FlexibleDestination, ServiceDispatch }

    public struct TripRequest
    {
        public int AgentId;
        public int Origin;              // node id
        public int Destination;         // node id (FixedDestination) or -1
        public int Category;            // destination category (FlexibleDestination)
        public Preference Alpha;        // the agent's true continuous preference vector
        public ulong Seed;              // per-agent noise seed (determinism)
        public bool LongOrTransit;      // gets a Suurballe-disjoint backup
    }

    /// <summary>One route alternative, stored as a via-node, not a path
    /// (plan §4 Layer 2): its live cost is two CCH queries and full geometry
    /// is expanded only for the alternative actually driven.</summary>
    public struct Alternative
    {
        public int ViaNode;
        public int Destination;         // per-alternative: flexible trips store (destination, via) pairs
        public float AnchorCost;        // cost under the agent's nearest live anchor (re-pricing units)
        public float AlphaCost;         // exact cost under the agent's true alpha (choice units)
        public bool IsDisjointBackup;
    }

    /// <summary>Feature flags: every layer independently revertible (plan §5).</summary>
    public sealed class FeatureFlags
    {
        public bool UseCchQueries = true;        // off => vanilla-style per-trip Dijkstra
        public bool UsePortfolios = true;        // off => single best path, frozen
        public bool UseBucketDestinations = true;// off => nearest-destination-then-route
        public bool UseDispatchSuperSource = true;
        public bool UseContinuousUpdates = true; // off => plans frozen after start
        public bool UseSoftClosures = true;
        public bool UseCertificates = true;
    }
}
