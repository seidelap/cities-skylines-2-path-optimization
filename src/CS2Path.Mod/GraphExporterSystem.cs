using System;
using System.Collections.Generic;
using CS2Path.Core;
#if !OUT_OF_GAME_BUILD
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
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
            Enabled = false; // on-demand only; Capture() is driven by the mod UI
        }

        protected override void OnUpdate() { }

        /// <summary>Diagnostics from the last capture — printed by the caller so a
        /// wrong assumption is visible immediately instead of silently producing a
        /// plausible-looking graph.</summary>
        public string LastDiagnostics { get; private set; } = "";

        // Retained from the last Capture() so live traffic samples can be
        // attributed to the right exported edge.
        private readonly Dictionary<Entity, int> _laneToEdge = new Dictionary<Entity, int>();
        private readonly List<float> _edgeLength = new List<float>();

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
            return export;
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

                sink.RecordTraffic(tick, kv.Value, length / observedSpeed);
                taken++;
            }
            return taken;
        }
    }
#endif
}
