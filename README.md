# CS2 Trip Simulation Rebuild

Implementation of the design in [`cs2-trip-simulation-rebuild.md`](cs2-trip-simulation-rebuild.md):
a replacement for Cities: Skylines II's per-query brute-force routing pipeline, built as a
preprocessing-based architecture (Customizable Contraction Hierarchies + via-node route
portfolios + continuous updates), shipped as a code mod.

Vanilla CS2 answers every trip with an independent A*-family search over the full lane graph
against a lagged congestion snapshot, then freezes the plan. That is why late-game simulation
speed collapses with pending pathfinding queries, and why corridors herd and oscillate. This
rebuild moves the work to preprocessing: **structure once, metrics per traffic refresh,
queries in microseconds** — and makes change propagation, not recomputation, the default.

A companion project is designed in
[`cs2-spatial-demand-economy.md`](cs2-spatial-demand-economy.md): a spatially
disaggregated replacement for CS2's demand, migration, land-value, rent,
leveling, and trade-pricing systems under Georgist land accounting, consuming
this rebuild's CCH/cluster infrastructure. Its modding-surface survey —
systems to replace, component touchpoints, the new-views/UI workload, and
save-state requirements — is in
[`cs2-spatial-demand-economy-implementation.md`](cs2-spatial-demand-economy-implementation.md).

## Layout

```
src/CS2Path.Core/      Pure routing core — NO game assembly references (plan §5)
  NestedDissection.cs    Layer 0: metric-independent elimination order (Inertial Flow
                                  min-cut separators; geometric sweep on small cells)
  CchSkeleton.cs         Layer 0: contraction with ALL shortcuts (chordal supergraph)
  AnchorGrid.cs          Layer 1: preference-profile × scenario anchor metrics
  CchMetrics.cs          Layer 1: vectorized multi-metric triangle-sweep customization,
                                  sub-ms partial customization on congestion deltas
  SoftClosure.cs         Layer 1: graded soft-closure edge states with hysteresis
  CchQuery.cs            Layer 2: elimination-tree up/down queries + path unpacking
  TripPlanner.cs         Layer 2: via-node portfolios, penalty-method alternatives,
                                  logit choice, §4.7 exactness certificates + repair
  PotentialAStar.cs      §4.7:    lazy CH-potential A* (repair + penalty searches)
  Suurballe.cs           Layer 2: edge-disjoint backup paths
  Buckets.cs             Layer 3: destination buckets (joint destination+route choice),
                                  fleet dispatch mirror construction
  UpdateEngine.cs        Layer 4: edge→agent index, push/wakeup/sweeper channels,
                                  tiered refresh, metered upstream-first invalidation
  RoutingEngine.cs       Facade + async Layer-0 rebuild with atomic swap
  Ports.cs               The stable interfaces the game adapters talk through

src/CS2Path.Mod/       The ONLY code allowed to touch Colossal Order assemblies:
  Mod.cs                 IMod entry point (observe-only: plan §5 step 1, no vanilla
                         system disabled, nothing written back — cannot corrupt a save)
  GraphExporterSystem.cs ECS → CityExport: walks Game.Net lanes into our graph
  GraphExporter.cs       trace accumulation + file writing
  GameAdapters.cs        reader (ECS → GraphSource/MetricFeed/WorldEvents) and
                         writer (chosen plans → PathOwner/PathElement) adapter stubs,
                         with the in-game wiring documented inline

src/CS2Path.Harness/   Standalone validation outside the game (plan §5)
  SyntheticCity.cs       City-like graphs (3 road tiers, holes) + reference Dijkstra
  TrafficSim.cs          Mesoscopic queue sim; Rebuild mode and Vanilla-baseline mode
  Herding.cs             Synchronized-demand corridor stress test (A/B)
  TestRunner.cs          Correctness suite vs reference Dijkstra
  Program.cs             verify | bench | herding | sim | all
```

## Running

```bash
dotnet run -c Release --project src/CS2Path.Harness -- verify    # correctness suite
dotnet run -c Release --project src/CS2Path.Harness -- bench     # §6 scale benchmarks (~131k nodes)
dotnet run -c Release --project src/CS2Path.Harness -- herding   # vanilla-vs-rebuild oscillation A/B
dotnet run -c Release --project src/CS2Path.Harness -- sim       # integrated 100k-trip simulation
dotnet run -c Release --project src/CS2Path.Harness -- all       # everything → RESULTS.md
```

Useful options: `--cols/--rows` (city size), `--profiles` (anchor preference profiles),
`--trips`, `--ticks`, `--queries`, `--seed`.

Measured results: see [`RESULTS.md`](RESULTS.md).

## How the plan maps to code

| Plan | Where | Notes |
|---|---|---|
| §4 L0 structure, no witness searches | `NestedDissection`, `CchSkeleton` | Inertial Flow separators (max-flow min-cut seeded by geometric quarters) on large cells, min-crossing geometric sweep on small ones, min-degree base-cell ordering; coordinate-less graphs use a BFS-level embedding as the flow projection |
| §4 L0 lazy edits | `RoutingEngine`, `CchMetrics` | closures/removals are instant metric edits (+inf via partial customization); additions rebuild asynchronously and swap atomically — nothing blocks |
| §4 L1 anchor grid | `AnchorGrid` | axes mandatory (positively spans the preference cone), k-means centroids for the rest, trip-frequency weighted |
| §4 L1 customization | `CchMetrics` | one bottom-up min-plus sweep carries all K metrics through SIMD lanes; partial customization propagates only while a min actually changes |
| §4 L1 soft closures | `SoftClosure` | occupancy/outflow state machine, entry/exit hysteresis, multiplier 1.2→∞ graded with jam duration |
| §4 L2 portfolios | `TripPlanner` | anchor winners → penalty extras → Suurballe backup, in budget order (§4.8); overlap ≤ 0.7, stretch ≤ 1.25, ε-envelope retention |
| §4 L2 via-node storage | `Alternative` | one node id per route; live cost = two CCH queries; geometry expanded only for the driven route |
| §4.7 certificates | `TripPlanner.Certify` | conic λ-decomposition over anchors, LB = Σλᵢdᵢ*, per-trip a posteriori gap; repair = potential-guided A\* under true α (exact), portfolios self-correct |
| §4 L3 buckets | `DestinationBuckets` | distance-only entries, category-partitioned, dist-sorted, early-terminating scan, attraction as scan-time offset; staggered refresh |
| §4 L3 dispatch | `FleetIndex` | fleet posts forward labels; one backward search per request |
| §4 L4 channels | `UpdateEngine` | push (degradation) / region wakeups + sweeper (improvement) / metered upstream-first events; tiered re-price → probe → regenerate; ~7% hysteresis |
| §5 ports & adapters | `Ports.cs`, `CS2Path.Mod` | core is `netstandard2.1` with zero game references |
| §5 feature flags | `FeatureFlags` | every layer independently revertible |

## Known simplifications vs the full design

### §4.9 / Layer 4 v2 (route-knowledge cache era)

* **Per-nest lower bounds / branch-and-bound expansion** — decision-point refresh
  re-prices all held branches (≤5 at ~190 µs each) instead of maintaining per-nest
  bounds and expanding lazily; at this portfolio size the B&B saving is smaller than
  its bookkeeping. The nest structure is used for nested logit and corridor triggers.
* **Corridors as globally shared objects** — corridor identity is the via-node's
  dissection cell (implicit); donation reaches one entry, not every entry referencing
  the corridor. Global corridor objects (bound value, flow state, closure status)
  are the designed extension for the mesoscopic model.
* **Entry composition for long trips** (fine access + coarse corridor + fine egress)
  — trips key into a single well-separated level; composition is future work.
* **(via-node, metric-id)** — the metric-id is stored as provenance but re-pricing
  currently always uses the agent's nearest live anchor.
* **Platoon self-metering** — decision events are evaluated arrival-ordered (FIFO),
  but cost updates land at refresh granularity, so a platoon's later members see
  the diversions of those ahead only across refresh boundaries.

### Game-API status (verified vs still unverified)

The mod-side code was written against shipped open-source CS2 mods, not from
guesswork. **Verified:** the official toolchain build (`CSII_TOOLPATH` →
`Mod.props`/`Mod.targets`); `Game.Modding.IMod` with `OnLoad(UpdateSystem)`;
`updateSystem.UpdateAt<T>(SystemUpdatePhase.X)` registration; and the `Game.Net`
traversal API — `Node`, `Edge(m_Start,m_End)`, `Lane(m_StartNode,m_MiddleNode,
m_EndNode)`, `Curve(m_Bezier,m_Length)`, `CarLane(m_Flags)`,
`ConnectedEdge(m_Edge,m_End)`, `SubLane(m_SubLane)`, `PrefabRef(m_Prefab)`,
`CarLaneData`.

This produced one substantive simplification: **CS2's lane network is already an
edge-based graph.** Each `Lane` entity is a directed traversal between two
`PathNode`s, and intersection movements are themselves lanes — so turn costs come
for free and the lane/turn expansion the design doc specified is unnecessary.

**Still unverified, and marked in-code:** the exact speed-limit member on
`CarLane`/`CarLaneData` (isolated in one method so a wrong name is a compile
error, not a silent wrong metric) and the game-units→m/s scale factor
(`CalibrateSpeedUnits` resolves it empirically from `LaneFlow`). The vanilla
pathfinding system type names to disable are no longer guessed at all: the mod
enumerates every world system matching "Pathfind" into the log at first boot.
`GraphExporterSystem` also emits a connectivity diagnostic (mean node degree) so
a wrong `PathNode` identity assumption shows up loudly rather than as a
plausible-looking graph.

The exporter also records **traces**, not just structure: cadenced
delta-filtered traffic samples (each tick's samples are exactly that refresh's
changed-edge set) and one demand sample per observed commute
(`PathInformation` endpoints → `Transform` positions → nearest exported node,
radius-bounded). `harness import` replays them as A2 (cache hit rate on real
demand) and A3 (partial-customization cost + clustering on real change sets).
See [`deploy/gcp/SESSION-RUNBOOK.md`](deploy/gcp/SESSION-RUNBOOK.md) for the
session that collects everything, including the frame-time share of vanilla
pathfinding (the Amdahl ceiling).

### Modeling constraints (documented decisions, not code)

* **Transit time-dependence** — CCH requires time-independent edge costs; transit
  legs use the frequency-based resolution (expected wait = headway/2). Real
  limitation: no schedule-exact transfers. Schedule-based transit would need a
  separate time-expanded layer stitched at mode-transfer nodes.
* **Parking** — a capacity-constrained sub-destination between Layers 2 and 3
  (choose a lot jointly with the route, spill to the next lot when full). Not
  modeled; the bucket machinery is the natural host (lots as a destination
  category with occupancy-driven attraction), future work.
* **Corridor ratio fold-back** — calibrating edge sums by realized corridor
  traversals (multiplicative correction at customization, shrunk toward 1.0 on
  sparse corridors) matters where telemetry is partial, i.e. the real game's
  lane graph. The harness observes every traveler at edge granularity, so folded
  ratios are ~1.0 by construction; the harness instead measures the
  realized-vs-predicted ratio per cluster entry (prediction bias), which is the
  confidence input the predictive blend consumes. Fold-back lands with corridor
  objects in the adapter.

* **Turn costs / lane changes** — the core routes on a directed edge graph; the plan's
  lane-level treatment needs the reader adapter to expand the game's lane/turn graph into
  edge-based form (node = lane-end, edge = lane traversal or permitted turn). The routing
  core is agnostic to which expansion is used.
* **Transit legs** — multimodal stitching (walk → transit → walk) is not yet composed;
  the CCH machinery is mode-agnostic, so transit becomes additional edges plus transfer
  nodes in the adapter's expansion.
* **Structural additions** are handled by the async rebuild path only; top-of-order lazy
  splicing (an optimization of the same semantics) is future work. Removals/closures are
  instant via metrics, as designed.
* **Flexible-trip certificates** — exactness certificates run for fixed-destination trips;
  flexible trips get exact re-pricing of their (destination, via) menu instead.
* **In-game ECS wiring** — `CS2Path.Mod` documents but stubs the two adapters; compiling
  against `Game.dll` and shipping through the game's Mods folder happens in the game build,
  per the version-pinning discipline in plan §5.

## Running the game (and this mod) without a Windows PC

`deploy/gcp/` provisions a Windows + NVIDIA workstation on GCP that you stream to a
Mac, doubling as the build/test machine for plan §5 steps 1–3. Compute scales to zero
between sessions (idle shutdown + a hard session cap); a hibernate command drops idle
cost to roughly snapshot storage. See [`deploy/gcp/README.md`](deploy/gcp/README.md)
for the cost model, the GPU-quota prerequisite, and the calibration loop:

```
CS2 + exporter mod  ──►  .cs2city export  ──►  harness import  ──►  A1 / A2 verdicts
```

`harness import` replays a real exported city and reports the two assumptions every
current benchmark rests on — separator quality (elimination-tree height vs the
synthetic 264) and demand locality (cache hit rate vs the synthetic 84.2%). The loop
is exercisable today with `export-synthetic` before the in-game exporter exists.
