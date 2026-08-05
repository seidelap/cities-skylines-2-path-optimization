using System;
using System.Collections.Generic;
using CS2Path.Core;
#if !OUT_OF_GAME_BUILD
using Game;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using ObjTransform = Game.Objects.Transform;
#endif

namespace CS2Path.Mod
{
    /// <summary>
    /// Reads the live lane network out of ECS into a <see cref="CityExport"/>.
    ///
    /// THE KEY MODELLING FACT (verified against shipped CS2 mods, and it
    /// simplifies the original plan substantially): CS2's lane network is
    /// ALREADY an edge-based graph. Every <c>Game.Net.Lane</c> entity is a
    /// directed traversal from <c>m_StartNode</c> to <c>m_EndNode</c>, both of
    /// type <c>PathNode</c>; and intersection movements are themselves lane
    /// entities (the connector/"connection" lanes generated at nodes). So:
    ///
    ///     our graph NODE  =  one distinct PathNode (a lane endpoint)
    ///     our graph EDGE  =  one Lane entity
    ///
    /// which means turn costs come for free — they live on the connector lanes —
    /// and we do NOT need the lane/turn expansion the design doc originally
    /// specified (plan §3 "turn costs require edge-based graph treatment": the
    /// game already did it).
    ///
    /// API surface used here, all verified against krzychu124/Traffic:
    ///   Game.Net:      Node, Edge(m_Start,m_End), Lane(m_StartNode,m_MiddleNode,
    ///                  m_EndNode), Curve(m_Bezier,m_Length), CarLane(m_Flags),
    ///                  ConnectedEdge(m_Edge,m_End), SubLane(m_SubLane)
    ///   Game.Prefabs:  PrefabRef(m_Prefab), CarLaneData, NetCompositionLane
    ///   Game.Common:   Deleted
    ///   Game.Tools:    Temp
    ///
    /// Runs on demand (never per-frame) on the main thread: it executes once per
    /// export, so clarity beats a Burst job here.
    /// </summary>
#if !OUT_OF_GAME_BUILD
    public partial class GraphExporterSystem : GameSystemBase
    {
        private EntityQuery _laneQuery;
        private EntityQuery _tripQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            // Every lane with geometry and a prefab, excluding entities the game
            // has marked deleted or that belong to an in-progress tool preview.
            _laneQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Lane>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            // Every agent the game has computed a path for. PathInformation is
            // the RESULT record (origin, destination, cost) — reading it observes
            // the vanilla pathfinder's own output without touching its inputs.
            _tripQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PathInformation>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            Enabled = false; // enabled only while a trace is running (BeginTrace)
        }

        // ---- trace driver state ----
        private GraphExporter? _traceSink;
        private int _traceCadence = 64;   // frames between snapshots
        private long _frame;
        private int _traceTick;
        private int _tripsRecorded, _tripsUnresolved, _tripsOutOfRange;
        // agent entity -> destination we last recorded, so one commute = one
        // sample regardless of how many frames its PathInformation persists.
        private readonly Dictionary<Entity, Entity> _tripSeen = new Dictionary<Entity, Entity>();

        /// <summary>Trips get a constant, documented preference vector: the A2
        /// question (is demand spatially concentrated?) is a pure OD-geometry
        /// question and does not depend on alpha. CS2 has no per-cim (time,
        /// money, comfort) triple to read; deriving one from citizen archetypes
        /// is future work and would land here.</summary>
        public static readonly Preference UnknownAlpha = new Preference(1.5f, 0.55f, 0.25f);

        /// <summary>Start cadenced trace recording into <paramref name="sink"/>.
        /// Requires a prior Capture() in this session (the trace attributes
        /// samples to that capture's edge/node ids — do not edit roads while a
        /// trace runs; lanes that vanish are skipped, lanes built after the
        /// capture are invisible to the trace).</summary>
        public string BeginTrace(GraphExporter sink, int cadenceFrames = 64)
        {
            if (_nodeIndex == null)
                throw new InvalidOperationException("BeginTrace requires a prior Capture() — the trace maps onto that capture's ids");
            _traceSink = sink ?? throw new ArgumentNullException(nameof(sink));
            _traceCadence = Math.Max(1, cadenceFrames);
            _frame = 0;
            _tripsRecorded = _tripsUnresolved = _tripsOutOfRange = 0;
            _tripSeen.Clear();
            Enabled = true;
            return $"trace started: snapshot every {_traceCadence} frames";
        }

        public string EndTrace()
        {
            Enabled = false;
            var sink = _traceSink;
            _traceSink = null;
            return sink == null
                ? "no trace was running"
                : $"trace ended: {_traceTick} snapshots, {sink.TrafficSampleCount} traffic samples, " +
                  $"{sink.DemandSampleCount} trips ({_tripsRecorded} recorded, " +
                  $"{_tripsUnresolved} endpoints without Transform, {_tripsOutOfRange} beyond snap radius)";
        }

        protected override void OnUpdate()
        {
            var sink = _traceSink;
            if (sink == null) return;
            if (++_frame % _traceCadence != 0) return;
            int tick = _traceTick++;
            SampleTraffic(tick, sink);
            SampleTrips(tick, sink);
        }

        /// <summary>Diagnostics from the last capture — printed by the caller so a
        /// wrong assumption is visible immediately instead of silently producing a
        /// plausible-looking graph.</summary>
        public string LastDiagnostics { get; private set; } = "";

        // Retained from the last Capture() so live traffic samples can be
        // attributed to the right exported edge, and trip endpoints snapped to
        // the right exported node.
        private readonly Dictionary<Entity, int> _laneToEdge = new Dictionary<Entity, int>();
        private readonly List<float> _edgeLength = new List<float>();
        private SpatialNodeIndex? _nodeIndex;

        /// <summary>Snap radius for trip endpoints (metres). A building sits at
        /// most a driveway from its road; anything farther means the endpoint's
        /// network was not exported (e.g. pedestrian-only) and the sample is
        /// dropped and counted rather than snapped somewhere wrong.</summary>
        public float SnapRadius = 250f;

        public CityExport Capture()
        {
            var em = EntityManager;
            using var entities = _laneQuery.ToEntityArray(Allocator.Temp);
            using var lanes = _laneQuery.ToComponentDataArray<Lane>(Allocator.Temp);
            using var curves = _laneQuery.ToComponentDataArray<Curve>(Allocator.Temp);
            using var prefabs = _laneQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);

            // PathNode -> our node index. PathNode is the game's own pathfinding
            // node key, so it is hash/equality-usable; if a future patch changes
            // that, the connectivity diagnostic below turns pathological
            // (node count ~= 2x edge count, every degree 1) rather than lying.
            var nodeIndex = new Dictionary<PathNode, int>(entities.Length * 2);
            var xs = new List<float>(entities.Length * 2);
            var ys = new List<float>(entities.Length * 2);
            _laneToEdge.Clear();
            _edgeLength.Clear();

            int NodeOf(PathNode p, float3 pos)
            {
                if (nodeIndex.TryGetValue(p, out int id)) return id;
                id = xs.Count;
                nodeIndex.Add(p, id);
                xs.Add(pos.x);
                ys.Add(pos.z); // CS2 is Y-up: the ground plane is (x, z)
                return id;
            }

            var tail = new List<int>(entities.Length);
            var head = new List<int>(entities.Length);
            var timeFree = new List<float>(entities.Length);
            var money = new List<float>(entities.Length);
            var comfort = new List<float>(entities.Length);
            var capacity = new List<float>(entities.Length);
            var jam = new List<float>(entities.Length);

            int skippedNonCar = 0, skippedDegenerate = 0;

            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];

                // Car lanes only for the first export. Pedestrian and track lanes
                // are the same construction and can be added the same way once the
                // multimodal stitching lands.
                if (!em.HasComponent<CarLane>(e)) { skippedNonCar++; continue; }
                var carLane = em.GetComponentData<CarLane>(e);

                var curve = curves[i];
                float length = curve.m_Length;
                if (!(length > 0.01f)) { skippedDegenerate++; continue; }

                float speed = SpeedGameUnits(carLane) * SpeedScale;
                if (!(speed > 0.1f)) { skippedDegenerate++; continue; }

                var lane = lanes[i];
                int u = NodeOf(lane.m_StartNode, curve.m_Bezier.a);
                int v = NodeOf(lane.m_EndNode, curve.m_Bezier.d);
                if (u == v) { skippedDegenerate++; continue; }

                bool highway = (carLane.m_Flags & CarLaneFlags.Highway) != 0;

                _laneToEdge[e] = tail.Count;
                _edgeLength.Add(length);
                tail.Add(u);
                head.Add(v);
                timeFree.Add(length / speed);                     // seconds
                money.Add(length * (highway ? 0.004f : 0.001f));  // fuel + toll proxy
                comfort.Add(length / speed * (highway ? 0.5f : 0.3f));
                capacity.Add(0.5f);                               // veh/s service rate, one lane
                jam.Add(math.max(1f, length / 8f));               // veh storage at ~8 m each
            }

            var export = new CityExport
            {
                NodeCount = xs.Count,
                X = xs.ToArray(),
                Y = ys.ToArray(),
                Tail = tail.ToArray(),
                Head = head.ToArray(),
                TimeFree = timeFree.ToArray(),
                Money = money.ToArray(),
                Comfort = comfort.ToArray(),
                Capacity = capacity.ToArray(),
                JamCapacity = jam.ToArray(),
            };

            // Connectivity sanity check. A healthy road network has mean degree
            // well above 2; if PathNode identity were wrong, every lane would get
            // its own private endpoints and this collapses to ~1.
            var degree = new int[export.NodeCount];
            for (int k = 0; k < export.EdgeCount; k++) { degree[export.Tail[k]]++; degree[export.Head[k]]++; }
            int isolated = 0; long degSum = 0;
            foreach (var d in degree) { if (d <= 1) isolated++; degSum += d; }
            double meanDeg = export.NodeCount > 0 ? (double)degSum / export.NodeCount : 0;

            LastDiagnostics =
                $"lanes scanned={entities.Length} exported={export.EdgeCount} " +
                $"(skipped: non-car={skippedNonCar} degenerate={skippedDegenerate}) " +
                $"nodes={export.NodeCount} meanDegree={meanDeg:0.00} degree<=1={isolated} " +
                (meanDeg < 1.5
                    ? "*** SUSPICIOUS: mean degree < 1.5 means lanes are not sharing endpoints — "
                    + "PathNode identity is wrong; switch the dictionary to an "
                    + "EqualsIgnoreCurvePos-based comparer ***"
                    : "connectivity looks sane");

            export.Validate();
            _nodeIndex = new SpatialNodeIndex(export.X!, export.Y!);
            return export;
        }

        /// <summary>Record one demand sample per (agent, destination): read the
        /// vanilla pathfinder's own result records, resolve both endpoint
        /// entities to world positions, snap to the nearest exported node.
        /// Verified components only: Game.Pathfind.PathInformation
        /// (m_Origin/m_Destination are Entity refs) and Game.Objects.Transform
        /// (m_Position). Endpoints without a Transform, or farther than
        /// SnapRadius from any exported node, are dropped and counted.</summary>
        private void SampleTrips(int tick, GraphExporter sink)
        {
            var em = EntityManager;
            var index = _nodeIndex;
            if (index == null) return;
            using var agents = _tripQuery.ToEntityArray(Allocator.Temp);
            using var infos = _tripQuery.ToComponentDataArray<PathInformation>(Allocator.Temp);
            for (int i = 0; i < agents.Length; i++)
            {
                var info = infos[i];
                if (info.m_Origin == Entity.Null || info.m_Destination == Entity.Null) continue;
                if (_tripSeen.TryGetValue(agents[i], out var seenDest) && seenDest == info.m_Destination)
                    continue; // same commute, already recorded
                _tripSeen[agents[i]] = info.m_Destination;

                if (!TryPosition(em, info.m_Origin, out float3 po) ||
                    !TryPosition(em, info.m_Destination, out float3 pd))
                { _tripsUnresolved++; continue; }

                int o = index.NearestWithin(po.x, po.z, SnapRadius); // ground plane is (x, z)
                int d = index.NearestWithin(pd.x, pd.z, SnapRadius);
                if (o < 0 || d < 0 || o == d) { _tripsOutOfRange++; continue; }

                sink.RecordDemand(tick, o, d, in UnknownAlpha);
                _tripsRecorded++;
            }
        }

        private static bool TryPosition(EntityManager em, Entity e, out float3 pos)
        {
            if (em.Exists(e) && em.HasComponent<ObjTransform>(e))
            {
                pos = em.GetComponentData<ObjTransform>(e).m_Position;
                return true;
            }
            pos = default;
            return false;
        }

        /// <summary>Enumerate every system in the world whose type name mentions
        /// pathfinding, with its enabled state. Two consumers: (1) the engine
        /// swap needs the exact vanilla system type names to disable — this
        /// answers that empirically on first boot instead of from guesswork;
        /// (2) the Amdahl measurement (what share of frame time is vanilla
        /// pathfinding?) needs to know which rows to read in the profiler.
        /// Call from OnLoad and log the result.</summary>
        public static string DumpPathfindSystems(World world)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("systems matching 'Pathfind' (candidates to profile, and later disable):");
            int n = 0;
            foreach (var sys in world.Systems)
            {
                var t = sys.GetType();
                if (t.FullName == null || t.FullName.IndexOf("Pathfind", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                sb.AppendLine($"  {t.FullName}  enabled={sys.Enabled}");
                n++;
            }
            sb.AppendLine(n == 0
                ? "  NONE FOUND — the game renamed its pathfinding namespace; grep a decompile"
                : $"  ({n} systems)");
            return sb.ToString();
        }

        /// <summary>
        /// Free-flow speed for a lane, in game speed units.
        ///
        /// Field names verified against a dump of the game's ECS component
        /// definitions: Game.Net.CarLane really does carry BOTH m_SpeedLimit and
        /// m_DefaultSpeedLimit (float). Game.Prefabs.CarLaneData does NOT carry a
        /// speed at all — it holds m_NotTrackLanePrefab, m_NotBusLanePrefab,
        /// m_RoadTypes, m_MaxSize — so an earlier fallback through it was simply
        /// wrong and has been removed.
        ///
        /// The unit scale is still unconfirmed, which is why SpeedScale is a
        /// field rather than a constant, and why CalibrateSpeedUnits() below
        /// derives it from observed lane flow instead of guessing.
        /// </summary>
        private float SpeedGameUnits(CarLane carLane)
        {
            float raw = carLane.m_SpeedLimit;
            if (!(raw > 0f)) raw = carLane.m_DefaultSpeedLimit;
            return raw;
        }

        /// <summary>Game-speed-units -> m/s. Starts at 1 and is corrected by
        /// CalibrateSpeedUnits() from the game's own measured lane flow.</summary>
        public float SpeedScale { get; private set; } = 1f;

        /// <summary>
        /// Resolve the speed-unit question empirically instead of asserting it.
        ///
        /// Game.Net.LaneFlow carries m_Duration and m_Distance (float4 rolling
        /// windows) — the game's OWN measurement of how long vehicles took over
        /// how far. On free-flowing lanes, distance/duration is the true speed in
        /// m/s, so the median ratio of that to our declared speed limit is the
        /// unit scale. Congested lanes are excluded via Game.Net.Density.
        ///
        /// Call once after a city has been running; it makes the export's
        /// free-flow times physically meaningful without anyone having to know
        /// CS2's internal units.
        /// </summary>
        public string CalibrateSpeedUnits()
        {
            var em = EntityManager;
            using var entities = _laneQuery.ToEntityArray(Allocator.Temp);
            var ratios = new List<float>();

            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (!em.HasComponent<CarLane>(e) || !em.HasComponent<LaneFlow>(e)) continue;
                // Only near-empty lanes: on a congested lane the measured speed is
                // the jam speed, not the free-flow speed.
                if (em.HasComponent<Density>(e) && em.GetComponentData<Density>(e).m_Density > 0.05f) continue;

                var flow = em.GetComponentData<LaneFlow>(e);
                float dist = math.csum(flow.m_Distance);
                float dur = math.csum(flow.m_Duration);
                if (!(dist > 5f) || !(dur > 0.5f)) continue;

                float declared = SpeedGameUnits(em.GetComponentData<CarLane>(e));
                if (!(declared > 0.01f)) continue;

                ratios.Add((dist / dur) / declared);   // observed m/s per game unit
            }

            if (ratios.Count < 25)
                return $"speed calibration skipped: only {ratios.Count} free-flowing sampled lanes (need 25)";

            ratios.Sort();
            SpeedScale = ratios[ratios.Count / 2];
            return $"speed scale = {SpeedScale:0.####} m/s per game unit " +
                   $"(median of {ratios.Count} free-flowing lanes; 1.0 means the game already stores m/s)";
        }

        /// <summary>
        /// Sample live congestion into the export's traffic trace, from the
        /// game's own per-lane measurements rather than anything we model.
        /// Verified components: Game.Net.LaneFlow(m_Duration,m_Distance) and
        /// Game.Net.Density(m_Density). Call on a stagger while the city runs.
        /// </summary>
        public int SampleTraffic(int tick, GraphExporter sink)
        {
            var em = EntityManager;
            int taken = 0;
            foreach (var kv in _laneToEdge)
            {
                if (!em.Exists(kv.Key) || !em.HasComponent<LaneFlow>(kv.Key)) continue;
                var flow = em.GetComponentData<LaneFlow>(kv.Key);
                float dist = math.csum(flow.m_Distance);
                float dur = math.csum(flow.m_Duration);
                if (!(dist > 1f) || !(dur > 0.1f)) continue;

                float observedSpeed = dist / dur;            // m/s, already real units
                float length = _edgeLength[kv.Value];
                if (!(observedSpeed > 0.1f) || !(length > 0f)) continue;

                // Delta-filtered: quiescent lanes cost one sample total, and the
                // samples at each tick ARE that refresh's changed-edge set (A3).
                if (sink.TryRecordTraffic(tick, kv.Value, length / observedSpeed, 0.02f))
                    taken++;
            }
            return taken;
        }
    }
#endif
}
