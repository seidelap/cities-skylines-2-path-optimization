# CS2 Trip Simulation Rebuild — Design Document

**Status:** Proposed — full from-scratch rebuild of all layers
**Scope:** Replace Cities: Skylines II's trip computation and route-update pipeline (pathfinding, destination choice, service dispatch, replanning) with a preprocessing-based architecture, shipped as a code mod.

---

## 1. Background

### 1.1 How the vanilla system works

CS2's simulation runs on Unity DOTS/ECS with Burst-compiled jobs. Routing is a continuous asynchronous pipeline: agents submit queries to a central shared queue when a trip starts (or when a re-plan is triggered, e.g. an agent has waited too long behind a blockage), and parallel workers drain the queue. Each query is an independent A*-family cost-minimizing search over a lane-level multimodal graph — every path action (turns, lane changes, jaywalking, U-turns, mode transfers, parking) carries a cost, and per-agent preference weights (time, money, comfort, by citizen type) shape the total. There is no shared preprocessing structure; every query pays full price.

Colossal Order's stated performance strategy is parallel brute force: no hard agent limits, with multicore scaling expected to absorb the load. It does parallelize — late-game saves peg all cores — but demand outruns throughput.

### 1.2 Observed consequences

Simulation speed directly tracks the number of pending pathfinding queries. Query volume grows with agent count while per-query cost grows with network size, so total cost is superlinear in city size; late-game sim speed collapses (~50–75% at 250–400k population on high-end CPUs). Non-obvious subsystems flood the same queue: taxi dispatch has been measured as a top-5 query producer (removing three depots in a 360k city moved sim speed from 74% to 94%), and housing search (FindProperty) runs through the same infrastructure, with a homelessness bug producing query storms.

The vendor's own 2026 fixes are demand rationing, which confirms the diagnosis: per-trip-type pathfinding range caps, less frequent household re-evaluation, and graceful failure semantics (workers dropping jobs, move-outs teleporting off-map) when routes can't be found. You do not ration query length and engineer graceful failure around query starvation if queries are cheap.

Separately, route choice exhibits herding waves: cohorts of agents flip between corridors on a several-sim-minute rhythm. This is emergent, not a timer. Plans are computed once at trip start and essentially frozen; the congestion costs feeding the planner lag reality, so all agents planning in a window read the same stale snapshot; departures are synchronized (uniform schedules); and events invalidate paths en masse. Deterministic best-response to a lagged shared signal is an unstable control loop — the oscillation period is the feedback delay plus typical trip duration.

### 1.3 Modding surface

Harmony can patch only managed code; the routing hot path is Burst-compiled (unmanaged) and cannot be patched. The sanctioned pattern is wholesale replacement: disable a vanilla ECS system, register your own. The official toolchain ships the Burst compiler, so replacement systems can be performance-competitive. Mods load locally from the game's Mods folder without publishing. The cost is maintenance: replaced systems are effectively a fork of the game's hottest, most patch-churned code.

No existing mod replaces the search algorithmically. The ecosystem covers cost-parameter tweaks (Pathfinding Customizer, Realistic PathFinding), behavior patches, and demand staggering (Time2Work). The algorithmic niche is empty.

---

## 2. Problem Statement

Replace CS2's per-query brute-force routing with a system that (a) computes trips roughly three orders of magnitude cheaper, (b) updates in-progress routes continuously without herding oscillation, (c) respects heterogeneous per-agent preferences exactly, (d) treats flexible-destination trips (shopping, leisure, service dispatch) as joint destination-and-route choices rather than destination-then-route, and (e) handles both literal closures (player edits) and effective closures (gridlock) with responses matched to their dynamics.

## 3. Constraints

**Heterogeneous preferences.** Agents weight time, money, and comfort differently and continuously; any preprocessing that bakes in a single metric is per se insufficient. Preferences must be exact at the point of choice, even if approximated inside the search.

**Multimodal, lane-level graph.** Trips compose walk → car → park → walk, or walk → transit (with transfers) → walk, at lane granularity. The road-leg accelerator must stitch to transit routing; turn costs require edge-based graph treatment.

**Player-editable topology.** The road network changes constantly during play. Structural preprocessing must tolerate splices lazily and rebuild asynchronously; nothing may block on it.

**Two trip classes.** Fixed-destination trips (work, school, home) have a known terminal. Flexible-destination trips choose the destination and the route jointly, trading attraction (stock, quality, price) against access cost — and may re-open destination choice mid-trip.

**Endogenous congestion.** Effective closures are caused by routing itself; binary responses oscillate. Feedback must be graded and metered.

**Performance budget.** All per-tick work must fit in low single-digit milliseconds inside Burst-parallel jobs, at 100k+ active trips, on consumer CPUs. Memory for route state must stay in the low tens of MB.

**Maintenance reality.** Every game patch can change the ECS components and systems this mod touches. Exposure to vendor churn must be architecturally concentrated (see §5).

**Save compatibility and determinism.** State written back must round-trip through vanilla save/load, and behavior should be reproducible enough to debug (seeded noise, ordered reductions).

---

## 4. Proposed Solution

Six layers, ordered by execution frequency. The organizing principle: move work from query time to preprocessing time (structure once, metrics per traffic refresh, queries in microseconds), put average tastes in the graph and individual tastes in the choice over alternatives, and make change propagation — not recomputation — the default at every layer.

### Layer 0 — Structure (runs on topology edits, asynchronously)

Build the lane-level multimodal graph and compute a metric-independent contraction order via nested dissection: find small separators (road networks are near-planar, so separators are small), rank separator nodes highest, recurse. Contract in that order inserting **all** potential shortcuts — no witness searches — yielding the Customizable Contraction Hierarchy skeleton, the superset of shortcuts valid under any weight assignment. This is the symbolic factorization of the graph in the (min, +) semiring; it is computed once and remains valid for every metric.

On player edits: splice new nodes lazily at the top of the elimination order (correctness is unaffected; only efficiency degrades) and rebuild the order in a background thread every few sim-minutes. Nothing blocks on Layer 0.

### Layer 1 — Metrics (runs on traffic refresh, milliseconds)

Maintain an **anchor grid** of scalar metrics: ~8–16 preference profiles × ~3 traffic scenarios (free-flow, typical, live), where the profile set always includes the **coordinate axes** of preference space (pure-time, pure-money, pure-comfort) so it positively spans the preference cone — a requirement of the exactness certificate in §4.7 — with the remaining anchors placed and adapted per the optimization strategy in §4.8. Customization is a bottom-up min-plus triangle sweep over the fixed shortcut structure — no searches, no structural decisions — vectorized so all metrics ride SIMD lanes through one pass. This is the numeric factorization step; full customization is milliseconds at city scale, and partial customization (re-relaxing only triangles containing changed edges, propagating up the elimination tree only while a min actually changes) is sub-millisecond for typical congestion deltas.

Effective-closure handling lives here as a first-class **edge state**, detected once at infrastructure level rather than rediscovered per agent by wait-timers: sustained outflow ≈ 0 at occupancy ≈ capacity for T seconds marks an edge soft-closed, with hysteresis on entry and exit. Planning cost inflates continuously with jam duration (the online penalty method: 1.2 → 1.4 → effectively ∞) rather than flipping binary, so the divert-drain-flood-back oscillation cannot form.

### Layer 2 — Fixed-destination trips (microseconds per trip)

At trip start, key the OD pair into its cluster entry (§4.9) at the appropriate hierarchy level and compose: fine access-leg entries at each end plus the corridor-level nest tree for the long haul. Cold or below-coverage entries fall back to direct generation — up-then-down CCH queries across the anchor grid, penalty-method extras (multiply the incumbent's edges by ~1.2–1.4 in a scratch metric, re-query, iterate), and one Suurballe-disjoint backup for long or transit-dependent trips — whose results seed the entry asynchronously. Candidates are admissibility-filtered (bounded overlap <~70%, bounded stretch <~1.25×, local optimality) into 2–5 held branches.

Every alternative is represented as a **(via-node, metric-id)** pair, not a path: one node ID plus the metric it was optimal under characterizes the route ("via the bridge"), its live cost is two CCH queries, and full lane-level geometry is expanded only for the route actually driven. Route knowledge lives in shared cluster entries (§4.9); the agent privately holds only its cursor — tree reference, trunk position, next trigger, held branch IDs, bound stamps — tens of bytes per trip.

The agent then scores its portfolio with its **true continuous preference vector** — time, money, comfort, plus logit noise — and departs. This is the division of labor that makes heterogeneity free: anchors approximate inside the search (with a (1+O(δ)) a priori error bound for preferences within δ of an anchor), the final choice is exact, and individual variation never touches graph infrastructure. Each scored portfolio additionally receives a per-trip **exactness certificate** (§4.7) that either proves the chosen path is truly α-optimal or bounds its gap; material gaps are logged as exploration demand on the cluster entry, and a synchronous fallback query fires only when no adequate held route exists. It is also the structure transportation science prescribes (choice-set generation + random utility), so the cheap design and the behaviorally correct one coincide.

### Layer 3 — Flexible-destination trips and dispatch

Never pick a destination and then route. Each candidate destination (shop, venue) maintains **bucket entries**: a backward upward CCH search posts "reachable at cost d" at the few hundred hierarchy nodes it touches. Entries are **distance-only**, partitioned by destination category, and sorted by distance within each bucket; attractiveness (stock, price, quality) is applied as a scan-time offset converted to cost units per anchor profile. A shopper runs one forward upward search, scans the relevant category's buckets with early termination (stop once distance alone exceeds the best attraction-adjusted cost found so far), and receives a menu of (destination, route) pairs in commensurable units, then logit-chooses over the top few. Chosen candidates enter the trip's portfolio as **(destination, via-node) pairs**, so continuous updates re-price stored pairs exactly as for fixed trips, with a larger hysteresis margin for switching destination than route. Distance-only entries mean stock and price changes are scalar updates that never re-run a backward search; buckets are shared across all shoppers (many-to-many amortization), with live-scenario buckets refreshed on a stagger (~10k destinations over ~20 ticks ≈ 500 backward searches/tick) while free-flow and typical-scenario buckets are near-static. Expected cost: ~2–3x a fixed-trip query after these optimizations (~10–15μs), a ~30–50% increase in total query-layer cost at realistic flexible-trip shares — well under the tick budget.

This structurally eliminates the bug class the vendor has patched piecemeal (e.g., shopping at the highest-stock store regardless of distance): attraction and access cost cannot be mis-weighted independently because they are summed inside one search.

Service dispatch (taxis, hearses, garbage — measured top query producers) uses the mirror construction: a virtual super-source with zero-cost edges to the whole fleet, one search per request instead of per-vehicle queries.

### Layer 4 — Continuous updates

Maintain an **edge → active-agents index**. Three triggers, in priority order:

1. **Push (degradation).** Customization flags changed edges; the index identifies affected agents; each re-scores its via-node portfolio — O(k) arithmetic — and issues a fresh query only if the entire portfolio degraded together. A route getting worse is locally detectable; this path handles it.
2. **Decision-point replanning (improvements).** Computation is valuable only where it is actionable, so improvement-checking fires when an agent *reaches a branch point* of its held route tree, not on a timer. Each trip holds a cursor into its cluster's nest tree (§4.9): current trunk, the 2–4 materially divergent branches at the next decision point, and bound handles for the remaining nests. On arrival at a stored trigger node: warm-start from held state — re-price the diverging branches under the live metric, run the certificate probe from the current position, expand a distant nest only if its lower bound crosses the incumbent minus the hysteresis margin — and regenerate from scratch only on a material certificate gap, portfolio collapse, or an in-corridor topology edit. Mid-trunk interruptions come solely from the event channel (soft-closures ahead severe enough to justify an early exit or turn-back; destination-level events for flexible trips), with sparse virtual checkpoints on very long trunks; a residual blind sweeper (~0.1% of agents per tick) remains as a safety net for routes in no one's universe. Platoons arriving at a diverge evaluate sequentially, each seeing costs updated by the diversions of those ahead — self-metering in exactly the way batch snapshot planning is not.
3. **Events (closures, soft-closures).** Invalidate via the index, but **metered and upstream-first**: notify a fraction of upstream agents sized so predicted diverted inflow ≈ the edge's service rate (gating); agents already queued re-plan from the next physically reachable junction or simply wait — their recovery is the upstream diversion draining ahead of them. This inverts the vanilla priority, which recomputes for the boxed-in waiter, the one agent who cannot use the answer.

Every switch must beat the current plan's remaining cost by a hysteresis margin (~5–10%); flexible trips may re-open destination choice at decision points against a larger margin still, modeling commitment. Budget check: ~400k concurrent trips generate ~1.5–2k decision events per real second; scoped to 2–4 branches plus nest-bound checks each, this is ~1ms wall-clock Burst-parallel, plus ~1ms partial customization and the residual sweeper — comfortably below the vanilla queue's steady state, scaling with decision events and cost change rather than agent count.

The herding waves die three ways at once — logit noise splits agents exactly where the cost envelope says routes are genuinely comparable, hysteresis kills flapping, and arrival-ordered decision-point replanning desynchronizes cohorts — and together these constitute the Method of Successive Averages: the system converges toward stochastic user equilibrium instead of oscillating around it.

### Layer 5 — Failure semantics

Retain the vanilla graceful-degradation behaviors (unreachable → drop job, idle, teleport off-map), now firing on genuine unreachability rather than query starvation, and log every such event: under this architecture they should be rare, and each one is a probe of a real network defect.

### 4.7 Optimality Semantics and Exactness Certificates

**Semantics.** Vanilla's returned path is exactly optimal with respect to the cost model at query time — but against a lagged congestion snapshot, within a range-capped feasible set, and frozen thereafter: exact about a moment, not about the trip. This design is deliberately, boundedly suboptimal per decision (anchor approximation, logit noise, hysteresis) but re-decided continuously against fresh costs. The deliberate part is not a concession: deterministic per-agent exactness against a shared lagged signal is the mechanism of the herding waves, so bounded per-choice ε buys convergence to stochastic user equilibrium and better *realized* travel times.

**Certificate.** Because the anchor set positively spans the preference cone, any citizen's α decomposes conically over anchors: α = Σλᵢaᵢ, λᵢ ≥ 0. For any path P, α·C(P) = Σλᵢ(aᵢ·C(P)) ≥ Σλᵢdᵢ*, where dᵢ* are the anchors' exact shortest distances — already computed during portfolio generation. Thus LB(α) = Σλᵢdᵢ* lower-bounds the true α-optimum at the cost of one dot product. If the best candidate's true-α cost equals LB, the path is **certified exactly optimal**; otherwise the difference is a per-trip a posteriori gap bound, far tighter than the a priori grid bound. The decomposition λ depends only on (α, anchor set): compute once per agent or per α-cluster (choosing λ to maximize the bound via a tiny precomputed LP reduces false alarms), cache ~64B per agent, refresh only when the anchor set changes.

**Repair — asynchronous by default.** A material certificate gap does not stall the trip: the agent proceeds on its best held branch (behaviorally sound under logit + hysteresis) while the gap is logged as **exploration demand** on its cluster entry. The exploration budget (§4.9) then runs the **potential-guided A\*** off the critical path under a representative α — h(v) = Σλᵢdᵢ(v, dest) is admissible (the CH-Potentials technique) and tight wherever anchor-optimal and α-optimal paths agree, so the search explores a narrow corridor, ~50–200μs per task — and donates any discovered via-node to the entry, fixing the hole for every current and future trip on that corridor. Synchronous search survives only for portfolio collapse (no feasible held route), and is then a plain anchor-metric CCH query, not an exactness repair.

**Cost accounting.** The certificate is ~free (all inputs pre-exist; one dot product and a comparison). With anchors tuned to the α distribution, f ≈ 2–10%, putting exploration overhead at +2–15% of the trip-computation layer ≈ 0.1–0.3% of frame time — now off the critical path entirely. Strategically, certificates invert the exactness economics: profiles cost linearly everywhere (customization lanes, edge/shortcut weight memory, portfolio queries, bucket-scan columns) for exponentially diminishing coverage returns, while repairs cost only on failures. Hold k moderate and let the tail pay per-trip retail — at equal effective exactness this is expected to be net *cheaper* than a large-k anchor set. The certified fraction and mean gap are logged as live telemetry: a direct instrument for whether the anchor set matches the citizen preference distribution.

### 4.8 Anchor & Portfolio Optimization Strategy (Tentative)

**Anchor set — initialization.** Seed with the mandatory coordinate axes, plus preference anchors at trip-frequency-weighted k-means centroids of the empirical α distribution sampled from the citizen population (heavy travelers weigh more). Baseline k ≈ 12–16 preference × 3 scenario anchors. Scenario axis: free-flow and live always; "typical" scenarios learned from rolling congestion averages (e.g., rush vs. off-peak splits when their divergence is persistent).

**Anchor set — online adaptation.** Treat anchor placement as failure-driven facility location, minimizing E[certified gap] + c·k. Continuously log (α, certified?, gap) per trip; every N sim-days, cluster the uncertified/high-gap α region and spawn an anchor at the worst cluster's weighted centroid; optionally retire the anchor least often binding in λ-decompositions to keep k bounded. Customization is cheap enough that k moving 24 → 28 is a non-event, so coverage becomes a measured, data-driven property rather than a design guess. The empirical outer loop: sweep k in the harness against benchmark saves, measuring sim speed × certified fraction × mean gap, and sit at the knee.

**Per-trip portfolio composition.** Size portfolios adaptively: the value of alternatives scales with exposure (trip duration) and corridor volatility, so long trips through congested or historically oscillating corridors get ~5 candidates plus a Suurballe-disjoint backup; short local trips get 1–2. Generate in budget order — anchor-grid winners first (deduplicated by via-node), penalty-method extras only until the distinctness target is met, Suurballe only for long or transit-dependent trips. Retain by the ε-envelope criterion (every candidate within (1+ε) of the agent's best across scenarios) rather than a hard k, so portfolios naturally shrink where one route dominates and widen where routes genuinely compete — which is also exactly where logit load-spreading is wanted. Revisit composition on certificate failure at re-score time: exploration that finds a materially better path donates its via-node to the **cluster entry**, individual holdings refresh from the shared tree at the next decision point, and coverage becomes self-correcting through the same telemetry loop that adapts the anchors.

### 4.9 Route Knowledge Cache: Choice-Set Generation, Nest Trees, and the Cluster Hierarchy (Tentative)

**Choice-set generation — accretive and demand-driven.** Entries are populated by real traffic, not offline synthesis. A cold entry is seeded with a few anchor-metric queries; thereafter **actual trips are the sampler**: every decision-point probe and every exploration task harvests candidates from the **meeting nodes** of its bidirectional CCH search (every node settled by both upward searches defines a route, essentially free) under the trip's own (α, live-metric) parameters — importance sampling from the true demand distribution by construction. A small **exploration budget**, allocated per entry by demand × certificate-gap telemetry, covers what real trips systematically cannot: noise-perturbed draws (mild multiplicative edge noise recovers ε-dominated routes that are competitive everywhere but win nowhere, so aggregate flow does not starve them), free-flow/typical-scenario draws (rush-hour trips only ever sample the jammed metric, and the route that is excellent once traffic clears must stay in the tree), and penalty-method draws (×1.2–1.4 in a scratch metric) for structural diversity. Candidates are admissibility-filtered and stored as **(via-node, metric-id)** pairs. **Coverage has a per-entry stopping criterion — Good–Turing over the arrival stream:** P(next arrival discovers an unseen route) ≈ singletons/arrivals; an entry is *covered* below ~2%, throttling exploration to a trickle, while entries **below threshold are quarantined against herding** — urgent exploration fires and held-branch diversity is enforced before a demand surge can be funneled onto a single known path.

**Nest tree and nested logit.** Group candidates by shared high-ranked (separator) via-nodes into a corridor-first tree: top-level nodes are corridor decisions, leaves are variants. The tree serves four roles at once: branch-and-bound monitoring (per-nest lower bounds; expand only nests whose bound crosses the incumbent minus hysteresis), the decision-point trigger locations stored on each trunk, the correct choice model — **nested logit** (corridor first, then variant) fixing the IIA overlap bias that flat logit has over near-duplicate routes — and corridor-level flow telemetry for a future mesoscopic movement model.

**Cluster hierarchy.** Route knowledge is keyed by (origin-cell, destination-cell) pairs where cells are **nodes of the Layer 0 nested-dissection tree itself** — the cache hierarchy is the elimination hierarchy, defined recursively by separator decomposition, never hand-drawn and automatically tracking topology rebuilds. Entries exist at multiple levels; a trip keys into the level at which its endpoints are **well-separated** (cell diameter ≈ ¼ trip distance), so short trips use fine neighborhood pairs and long trips use coarse district pairs shared by enormous volumes — bounding total active entries near-linearly (the well-separated-pair-decomposition argument) at ~10⁴–10⁵ entries ≈ tens of MB, materialized lazily on first demand and LRU-evicted. Long trips **compose** entries: fine near the origin (access to the coarse corridors' entry points), coarse for the long haul, fine at the destination end. **Corridors are first-class pair-independent objects** — bound value, flow state, soft-closure status, monitored edge regions — referenced by nest trees at every level, so one customization event updates one corridor and every referencing entry sees it, and a repair-discovered corridor is donated once, globally: the cache self-heals at the population level, and per-cluster certificate-gap telemetry is exactly the granularity the anchor-spawning loop consumes.

**Tier summary.** Graph-level shared preprocessing (CCH + anchor metrics) → corridor-level shared route objects → cluster-level pair trees at multiple scales → agent-level private choice (α, cursor, held branch IDs, bound stamps — tens of bytes). Each fact lives at the widest, coarsest tier that can own it, and each tier heals on its own timescale.

---

## 5. Interface Architecture — Why It Matters More, Not Less, for a Full Rebuild

A from-scratch rebuild of all layers maximizes exposure to the one risk that kills system-replacement mods: **every game patch is a potential breaking change to the code you forked around.** The mitigation is to make vendor churn hit an adapter, never the algorithm.

**Ports-and-adapters split.** The routing core must be a pure .NET library with no reference to game assemblies, communicating through a handful of stable internal interfaces — approximately: `GraphSource` (topology + edge attributes in), `MetricFeed` (live costs and closure states in), `TripRequest`/`PlanPortfolio` (queries in, via-node plans out), `WorldEvents` (topology edits, closures in), and `PlanWriter` (chosen paths back out). Exactly two thin adapters touch Colossal Order's code: one reading vanilla ECS components into `GraphSource`/`MetricFeed`/`WorldEvents`, one translating `PlanPortfolio` selections back into the components the movement systems consume. When a patch renames a component or reshapes a system, the diff-and-repair work is confined to those two files.

**Testability outside the game.** Because the core is pure, it runs in a standalone harness: export real city graphs to a serialized format, replay recorded traffic traces, and assert on query correctness (against a reference Dijkstra), query latency, customization latency, and equilibrium behavior (wave amplitude under synthetic synchronized demand). This is the difference between debugging algorithms in a unit test and debugging them inside a 30GB game process — and it means the hard engineering (Layers 0–1) is validated before the game ever loads it.

**Feature flags per layer.** Every layer must be independently revertible to vanilla behavior at runtime: dispatch super-source off, bucket search off, CCH off (fall back to vanilla queue). This gives a bisection tool for patch breakage, an A/B instrument for measuring each layer's contribution, and a safety valve for save compatibility.

**Version discipline.** Pin the game version during development (Steam offline / depot download); per patch, decompile, diff the adapted components and any replaced systems, repair adapters, re-run the harness, then unpin. The maintenance loop is mechanical precisely because the interface made it narrow.

**Build order under the interface.** Even committed to all layers, sequence by risk isolation: (1) harness + graph export + adapters with vanilla passthrough, proving the seam; (2) Layer 4's behavioral pieces (noise, hysteresis, staggering) over vanilla queries — herding fix ships first and works at current query costs; (3) Layer 3 dispatch super-source — second-cheapest large win; (4) Layers 0–1 CCH core in the external harness to latency targets; (5) swap the query engine behind the existing interfaces; (6) Layer 2 portfolios and Layer 3 buckets on top; (7) closure gating and soft-closure states. At every stage the game is playable and each increment is measurable against the benchmark scenes.

---

## 6. Acceptance Targets

Point-to-point query p99 < 20μs at 10⁵-node graphs under the live metric; full customization < 10ms and typical partial customization < 1ms for ~24–48 metrics; simulation speed ≥ 95% at 400k population on the benchmark save; corridor flow oscillation amplitude reduced ≥ 5× versus vanilla under the synchronized-demand stress test; zero increase in unreachable-trip failure events versus vanilla on identical saves; certified-exact fraction ≥ 90% at steady state with mean certified gap < 1%; exploration tasks fully off the critical path with synchronous fallback query p99 < 0.5ms.
