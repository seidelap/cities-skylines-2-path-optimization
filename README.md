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

## Layout

```
src/CS2Path.Core/      Pure routing core — NO game assembly references (plan §5)
  NestedDissection.cs    Layer 0: metric-independent elimination order (separator-based)
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
| §4 L0 structure, no witness searches | `NestedDissection`, `CchSkeleton` | geometric bisection when coordinates exist, BFS level-set fallback otherwise |
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
