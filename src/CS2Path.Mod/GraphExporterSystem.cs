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

                float speed = SpeedMetersPerSecond(em, prefabs[i].m_Prefab, carLane);
                if (!(speed > 0.1f)) { skippedDegenerate++; continue; }

                var lane = lanes[i];
                int u = NodeOf(lane.m_StartNode, curve.m_Bezier.a);
                int v = NodeOf(lane.m_EndNode, curve.m_Bezier.d);
                if (u == v) { skippedDegenerate++; continue; }

                bool highway = (carLane.m_Flags & CarLaneFlags.Highway) != 0;

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
        /// Free-flow speed for a lane, in m/s.
        ///
        /// THIS IS THE ONE FIELD MOST LIKELY TO NEED A ONE-LINE FIX: the exact
        /// speed member on CarLane / CarLaneData was not confirmed from source.
        /// It is isolated here on purpose — a wrong name is a compile error on the
        /// build machine, fixed in seconds, rather than a silent wrong metric.
        /// Candidates seen in the wild: CarLane.m_SpeedLimit,
        /// CarLane.m_DefaultSpeedLimit, CarLaneData.m_SpeedLimit.
        /// CS2 stores speed in game units; the constant below converts to m/s.
        /// </summary>
        private static float SpeedMetersPerSecond(EntityManager em, Entity prefab, CarLane carLane)
        {
            const float GameSpeedToMetersPerSecond = 1f;  // verify: units may need scaling
            const float FallbackSpeed = 11.1f;            // ~40 km/h

            float raw = carLane.m_SpeedLimit;
            if (!(raw > 0f) && em.HasComponent<CarLaneData>(prefab))
                raw = em.GetComponentData<CarLaneData>(prefab).m_SpeedLimit;

            return raw > 0f ? raw * GameSpeedToMetersPerSecond : FallbackSpeed;
        }
    }
#endif
}
