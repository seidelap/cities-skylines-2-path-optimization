# CS2 Trip Simulation Rebuild — Harness Results

Measured by `CS2Path.Harness` (see README for commands) on a 4-core Linux container,
.NET 8, single machine. All correctness checks run against reference Dijkstra.
Full design: [`cs2-trip-simulation-rebuild.md`](cs2-trip-simulation-rebuild.md).

**Reading the numbers against the §6 targets:** the §6 budgets assume the game's
Burst compiler (SIMD + multicore jobs) and real CS2 road topology. This harness is
single-machine C# — architectural properties (speedup ratios, scaling with change
size rather than graph size, certified fractions, oscillation damping) transfer;
absolute wall-clock numbers carry a known constant-factor gap that Burst closes.
Where a target is missed even accounting for that, it is called out honestly.

## Headline results

| plan claim | measured |
|---|---|
| §2(a) trips ~3 orders of magnitude cheaper | queries **~122× cheaper** than per-trip Dijkstra on the synthetic city (94.5 µs vs 11.1 ms this container, exact) and **112–203× on real road networks** (Ruhr/Paris/NY, Inertial Flow separators); with the §4.9 cache, **81% of trips plan warm in ~1.3 ms** (re-pricing only, geometry expanded only for the driven route) vs ~2.9 ms cold seeding and ~7.4 ms full-fat generation |
| §2(b) continuous updates without herding | vanilla is bistable under the stress test: it oscillates (fast congestion signal) or gridlocks (slow signal); the rebuild is stable under **both** — amplitude **5.9× below** the oscillating baseline, **2.6× better travel times** than the gridlocked one — with decision-point replanning active |
| §2(c) heterogeneous preferences exact | every trip scored with its true continuous α; certificates prove exactness per-trip (LP lower bound), repair A* recovers the exact optimum for the tail |
| §2(d) joint destination+route choice | bucket scans return (destination, route) menus in ~150 µs, verified exact vs brute force |
| §2(e) closures graded and metered | hard closures exact in every scenario (verified round-trip); soft closures graded with hysteresis; event wakes metered upstream-first |

## Correctness verification (72 checks, all passing)

CCH distances ≡ Dijkstra across every anchor metric (city + random graphs, BFS-fallback
order); partial customization ≡ full recustomization; hard-closure + reopen round-trips
exact in ALL scenarios; unpacked paths valid and cost-exact; potential-guided A* exact
under penalized metrics; certificate lower bounds sound (LB ≤ optimum, certified ⇒ exact,
repaired ⇒ exact); Suurballe pairs edge-disjoint and simple; bucket scans and fleet
dispatch ≡ brute force; soft-closure hysteresis state machine; end-to-end sim smoke test.

An adversarial review pass (44 agents, 6 dimensions, per-finding refutation) found 35
confirmed defects beyond the suite — closure-lane desync, NaN paths, wake starvation,
consumed-via mispricing, Suurballe cycle emission, stamp wraparound, and more — all fixed,
with the two worst given regression tests (all-scenario closure round-trip; the suite
previously sampled only live lanes).


## Scale benchmark (plan §6 targets)

Measured with the Inertial Flow partitioner (see the A1 section for the real-map morphology sweep and the geometric A/B).

Graph: **131,039 nodes / 482,554 directed lane-edges** (three road tiers, holes). Anchor grid: **8 preference profiles × 2 scenarios = 16 metrics**.

### Layer 0 — structure (once per topology edit, async)

| stage | result |
|---|---|
| nested dissection order | 2,220 ms |
| contraction (all shortcuts) | 759 ms |
| chordal arcs | 1,686,815 (7.0x undirected edges) |
| elimination tree height | 331 |
| multi-metric weight memory | 216 MB (16 metrics, fwd+bwd) |

### Layer 1 — customization (per traffic refresh)

| operation | time | §6 target |
|---|---|---|
| full customization, all 16 metrics | 531 ms | < 10 ms (Burst/SIMD budget) |
| full customization split: seeding vs sweep | ResetAll 190 ms (21%) + triangle sweep 717 ms (79%), sweep stable to ±0.7% | the ported half is the dominant half |
| full customization, level-parallel thread scaling (331 levels, 4-core container) | 1t=940 ms (0.98×), 2t=1,024 ms (0.90×), **4t=737 ms (1.26×)** | see "Burst port" below — this decomposition does **not** scale here |
| partial, 100 edges ±10% drift, scattered (live lanes) | median 92.38 ms, p99 218.56 ms (43,016 arcs) | < 1 ms |
| partial, 150 edges ±10% drift, clustered (one congestion pocket) | median 6.92 ms, p99 15.24 ms (3,362 arcs) | < 1 ms |
| partial, 1000 edges ±10% drift, scattered (live lanes) | median 500.60 ms, p99 533.74 ms (282,503 arcs) | — |
| partial, 1000-edge large shock (0.8-2.4x) | 475 ms (265,118 arcs) | worst case, amortizable |

### Layer 2 — point-to-point queries (per trip, live metric)

| measure | CCH (this mod) | reference Dijkstra (vanilla-style) |
|---|---|---|
| median latency | 94.5 µs | 11,138 µs |
| mean latency | 97.3 µs | 11,900 µs |
| p99 latency | 173.9 µs | 25,830 µs |
| throughput (4 threads) | 38,852 queries/s | — |
| correctness spot-check | 300/300 exact | (reference) |

**Speedup: 122x per query** (plan §2 asks ~3 orders of magnitude; §6 target p99 < 20 µs).

### Layer 2 — full trip planning (portfolio + choice + certificate)

| measure | value | §6 target |
|---|---|---|
| plan latency median / p99 | 7437.1 µs / 35,921 µs | — |
| planning throughput (4 threads) | 337 trips/s | — |
| mean portfolio size | 2.2 alternatives | 3-5 |
| certified-exact fraction | 77.8 % | ≥ 90% |
| mean certified gap (uncertified tail) | 2.43 % | < 1% |
| repair searches | 7,076 (35.4 % of trips, 2,080 hit budget) | 2-10% |
| repair p99 latency | 5,855 µs | < 500 µs |
| Suurballe backups | 19 | — |
| unreachable trips | 0 | no increase vs vanilla (= genuine) |

### Layer 2 v2 — §4.9 route-knowledge cache (zonal demand, 65% on 40 zone pairs)

| measure | value | design claim |
|---|---|---|
| cache-served share | 80.7 % (24,222 of 30,000) | high under commuter locality |
| warm plan latency (cache-served) | median 1,261 µs, p99 2,804 µs | — |
| cold plan latency (direct generation, seeds entry) | median 2,945 µs, p99 8,656 µs | demoted to seeding fallback |
| warm/cold speedup | 2.3x median | — |
| quarantine diversions (thin entry -> direct gen) | 5,228 | surge never funneled onto one path |
| sync fallbacks (portfolio collapse) | 0, p99 0 µs | p99 < 500 µs (§6) |
| exploration | 600 tasks, 1 donated vias, 2,275 µs/task | off the critical path |
| certificate gaps -> exploration demand | 14,015 (no synchronous repairs: 0) | §4.7 v2 |
| cache footprint | 550 entries, 0.1 MB | tens of MB at 10⁴-10⁵ entries |
| retained per-agent cursor | 136 B (+922 B driven geometry) | tens of bytes + driven route |

### Layer 4 — via-node re-pricing (per alternative, two CCH queries)

median 88.3 µs, p99 147.0 µs — a 5-alternative portfolio re-prices in ~441 µs.

### Layer 3 — flexible destinations + service dispatch

| measure | value |
|---|---|
| bucket build, 10,000 destinations | 1,149 ms (2,091,240 entries) |
| blind staggered refresh, 500 backward searches (v2, kept for A/B) | 31.3 ms |
| event-driven refresh, quiescent full rotation of 10,000 dests | 14.1 ms (0 re-searches) |
| event-driven refresh after a 150-edge congestion pocket | 8.8 ms (231 of 10000 scanned re-searched) |
| shopper query (scan 2,500 dests/category) | median 89.6 µs, p99 446 µs, 2894 entries scanned |
| dispatch query (fleet of 200) | median 92.8 µs, p99 423 µs |

Process memory after benchmark: 593 MB managed.


## Design v2/v3 delta (route-knowledge cache, async repair, event-driven channels)

The second design revision added §4.9 (cluster-entry route cache), made §4.7 repair
asynchronous, rebuilt Layer 4 channel 2 around decision points, and (v3) event-drove
the two remaining count-scaled channels. Measured effects at 131k nodes:

| change | measured effect |
|---|---|
| §4.9 cache, zonal demand (65% on 40 zone pairs) | **84.2% of trips served from shared entries**; warm plan 1,130 µs median vs 2,972 µs cold — and cold now *seeds* the entry |
| shared-entry memory model | cache: 539 entries / 0.1 MB total; retained per-agent cursor **141 B** (+ driven geometry) |
| quarantine (thin entries) | surge onto an uncovered entry is never served a single known path (verified); diversions fall through to seeding generation |
| §4.7 async repair | **0 synchronous repair searches**; 13,898 certificate gaps logged as exploration demand; exploration runs off the critical path at ~2.6 ms/task (C#), donating vias to entries; in-flight agents adopt donated vias at decision points |
| sync fallback (portfolio collapse) | **0 needed** across 30k zonal trips; bounded by a single CCH query (p99 154 µs) — §6 target < 500 µs met with margin |
| decision-point replanning | corridor-boundary + checkpoint triggers replace the timer sweeper (residual 0.1%); switching compares **stable-blended** costs — raw-live comparison measurably re-created herding (0.34 amplitude) until blended (0.096) |
| event-driven bucket refresh (v3) | quiescent: **0 re-searches** (5.2 ms of memoized chain checks per 10k-dest rotation) vs 500 blind searches/tick before; after a 150-edge pocket: 5,207/10,000 re-searched (215 ms) — the exactness test is conservative when changes reach top separators; per-arc subscriptions are the designed tightening |
| windowed soft-closure detection (v3) | bit-identical to the full scan (verified over random traces); sim examines only hot + active edges instead of all 482k |
| nested logit | corridor-first choice over cell-level nests, removing flat logit's IIA overlap bias |
| 100k-trip sim | arrivals 99,976/100,000, 0 unreachable, wall 1,938 s → **857 s** across v2+v3 |

The certified-exact fraction at plan time reads lower under async repair (gaps are
logged, not synchronously repaired) — the design's intent: exactness debt is paid
once per corridor by exploration instead of per trip on the critical path, and the
telemetry (gap mass per entry) is the §4.8 anchor-adaptation instrument.


## Telemetry / prediction round (register opportunities #1-#4, gaps #2-#3)

| change | measured effect |
|---|---|
| EMA bank in the live feed (damped queue-drain member) | herding damping intact under the smoothed feed: fast regime 0.418 → 0.133 (3.1×), slow regime 0.047 vs vanilla gridlock |
| entry realized-travel telemetry (mean + deviation per time-of-day bucket, ratio EMA) | prediction confidence 0.82, bias ratio 1.08 on verified entries; feeds the blend and adaptive departures |
| confidence-weighted predictive blend (damping floor raised by horizon × confidence) | per-plan StableBlend verified within [floor, ceiling] on every plan |
| cache remap after re-dissection (gap #3) | zero entries / zero OD knowledge lost across an engine rebuild (verified) |
| warm-up governor (gap #2, harness half) | 25-trip cold storm bounded to 3 direct generations; 25/25 trips still planned via thin service + urgent exploration |
| warm/cold with telemetry active | 84.2% hit rate; warm 2,366 µs vs cold 6,005 µs median (2.5×) |

### Adaptive departure timing (opportunity #1) — corrected stats (post-review fixes)

10,000 commuters, 8 days × 700 ticks (120x120 city). Adaptive: depart = target − (entry commute mean + k·deviation) − jitter, learned from cluster-entry realized telemetry. Fixed: depart = target − 1.25×free-flow estimate − jitter.

| day | mode | mean travel (ticks) | mean lateness | mean \|lateness\| | >10 ticks late | depart std | arrive std |
|---|---|---|---|---|---|---|---|
| 0 | fixed | 128.2 | +12.9 | 15.4 | 43.1 % | 64.7 | 50.4 |
| 1 | fixed | 128.9 | +13.6 | 16.2 | 41.7 % | 64.7 | 51.3 |
| 2 | fixed | 129.1 | +13.8 | 16.2 | 43.7 % | 64.7 | 51.1 |
| 3 | fixed | 128.9 | +13.6 | 15.9 | 44.7 % | 64.7 | 50.7 |
| 4 | fixed | 129.2 | +13.9 | 16.3 | 41.4 % | 64.7 | 51.4 |
| 5 | fixed | 129.4 | +14.1 | 16.5 | 45.5 % | 64.7 | 50.1 |
| 6 | fixed | 130.3 | +15.0 | 17.2 | 46.0 % | 64.7 | 50.8 |
| 7 | fixed | 129.1 | +13.8 | 15.9 | 48.7 % | 64.7 | 49.3 |
| 0 | adaptive | 128.2 | +12.9 | 15.4 | 43.1 % | 64.7 | 50.4 |
| 1 | adaptive | 125.9 | -66.8 | 68.6 | 2.4 % | 87.3 | 66.7 |
| 2 | adaptive | 126.5 | -57.4 | 59.3 | 2.5 % | 88.9 | 64.4 |
| 3 | adaptive | 125.0 | -59.4 | 60.9 | 2.0 % | 85.8 | 62.7 |
| 4 | adaptive | 124.4 | -60.1 | 61.4 | 1.8 % | 88.0 | 65.2 |
| 5 | adaptive | 123.7 | -58.9 | 60.1 | 1.7 % | 84.9 | 62.3 |
| 6 | adaptive | 122.9 | -58.5 | 59.7 | 1.7 % | 86.8 | 65.2 |
| 7 | adaptive | 123.3 | -56.0 | 57.3 | 2.0 % | 84.4 | 62.0 |

Adaptive departures learn from the same shared telemetry that feeds predictive pricing; per-agent heterogeneity (k, jitter) plus slow EMAs prevent departure-herding.

**Reading the corrected commute A/B** (the pre-fix per-day stats were computed over a
depart-order-shuffled index and are superseded): the reliability win is dramatic —
severe lateness (>10 ticks) collapses from ~44% to **~2%** and holds, and travel time
drifts down ~4-5% across days as the peak spreads. The cost is heavy early arrival
(mean ≈ −58 ticks): the mean + k·deviation rule with k ∈ [0.5, 2] prices commute
variance very conservatively. That asymmetry is a policy knob, not a bug — commuters
who must not be late rationally buy earliness — but k (and an asymmetric lateness
loss) belongs in the §4.8 empirical outer loop.

## Herding A/B (synchronized-demand stress test)

Three near-equivalent corridors, cohort departures. The vanilla baseline
(exact per-trip Dijkstra against a lagged shared snapshot, plans frozen,
wait-timer replans — plan §1.1) is bistable in the congestion-signal regime:
a fast (occupancy-proportional) signal produces the classic corridor
oscillation; a slow (queue-excess) signal produces absorbing single-corridor
gridlock. The rebuild is stable under BOTH regimes.

| signal regime | mode | oscillation (std of corridor share) | mean travel (ticks) |
|---|---|---|---|
| fast | vanilla | 0.4178 | 78.7 |
| fast | **rebuild** | **0.0709** | **69.0** |
| slow | vanilla | 0.0000 (gridlocked) | 181.8 |
| slow | **rebuild** | **0.0450** | **69.0** |

**Fast regime: oscillation amplitude 5.9× below vanilla** (§6 target ≥ 5×).
**Slow regime: vanilla collapses into gridlock (2.6× the rebuild's travel time); the rebuild stays near-stationary (0.0450).**

The damping comes from the §4 trio — logit noise over genuinely comparable
alternatives, switch hysteresis, arrival-ordered decision-point replanning — plus the typical-scenario
blend in the choice utility (§4 L1's rolling-average scenario axis) and
cross-scenario portfolio retention (§4.8). Tuning note: damping is
non-monotonic in the blend/noise parameters (0.6/0.10 measured best;
0.7/0.13 regresses), so these belong in the empirical outer loop of §4.8.


## Integrated simulation at scale

131,039-node city, 100,000 trips, 1500 ticks of 4s (~1.7 sim-hours), single-threaded harness on 4 cores.

| measure | value |
|---|---|
| wall time | 929 s (6.5x realtime) |
| arrivals | 99,975 / 100,000 |
| trips planned (incl. regenerations) | 100,000 |
| certified-exact fraction | 38.1 % |
| planning time | 181.1 s total, 1,811 µs/trip |
| movement | 5.83 ms/tick |
| live-metric partial customization | 1177.52 ms/refresh |
| Layer-4 wakes + re-pricing | 1271.60 ms/refresh |
| re-prices / probes / regenerations | 2,157,273 / 0 / 0 |
| route switches (past hysteresis) | 2,661 |
| event wakes (metered) / region wakes / sweeper | 0 / 0 / 4,518 |
| soft-closed edges at end | 0 |
| unreachable-trip events | 0 |

Re-measured under the flow partitioner: wall time fell 1,593 s -> 929 s and
planning 3,408 -> 1,811 µs/trip. Part of that is the smaller cliques (planning,
customization, wake re-pricing all scale with arc volume), part is container
variance — the partitioner-independent movement cost also moved, so read the
subsystem ratios, not the absolute wall-clock, as the structural signal.


## Burst port: compatibility achieved, parallel speedup NOT demonstrated

Every §6 absolute target that this repo misses was previously excused with
"needs Burst". The compiler port is now real; the *parallel* half of the story
is not, and the honest result is more useful than the hoped-for one.

### What is done and checked

`BurstKernels.cs` holds the hot loops as `static unsafe` pointer-only kernels.
A raw pointer plus an explicit length is the only buffer representation common
to C# arrays (pinned with `fixed`, out of game) and `NativeArray` (via
`GetUnsafePtr`, in game), so **one implementation compiles under both
toolchains** and `CS2Path.Core` keeps its zero-game-reference rule (plan §5).
`System.Numerics.Vector<float>`, invisible to Burst, is gone.

Two properties are *checked*, not asserted:

| property | how |
|---|---|
| parallel ≡ sequential, **bit-identical** | exact `SingleToInt32Bits` comparison at 2/3/4/8 threads; min is order-independent and adds no rounding, so a tolerance would have hidden bugs |
| kernels are Burst-legal | a linter in the verify suite rejects managed collections, allocation, exceptions, `foreach`, .NET SIMD, non-static classes — and asserts `CchMetrics` actually routes through the kernels rather than keeping a drifting copy |

`FloatMode.Strict` is pinned in the in-game job wrappers deliberately: Burst's
default fast-math permits reassociation and FMA contraction, which would move
results by an ulp and silently invalidate the certificate lower bounds, the
200/200 Dijkstra agreement, and the bit-identity test above.

### Where the time actually goes (measured, after fixing the instrument)

Customization is two passes with different bottlenecks, and reporting them as
one number hid which was which. Split, 5 reps each at 131k nodes:

| pass | median | share | ported to kernels? |
|---|---|---|---|
| `ResetAll` — seed every arc lane from its original edge weight | 190 ms (177–198) | **21%** | no |
| triangle sweep — the min-plus elimination pass | **717 ms** (714–724) | **79%** | yes |

An adversarial audit predicted the seeding pass was "almost certainly the
larger half". It is 21%. The port targeted the right pass, but for the wrong
stated reason — worth recording, because the same audit's other structural
findings were correct and it would be easy to accept all of them uniformly.

**Instrument note.** The sweep now measures to ±0.7% across reps. Earlier
conclusions in this section were drawn partly from `RoutingEngine.customize`,
which is a single *cold* run and swings **50% on identical code** (1,380–2,073
ms). Only warm, repeated measurements can resolve changes of the size being
attempted here. One claim was retracted on that basis: hoisting the per-lane
integer divide out of `EdgeWeight` was described as the biggest available win
and in fact moved nothing measurable — the seeding pass is memory-latency-bound
on scattered edge lookups, so a ~20-cycle divide hides behind the cache misses
it waits on. That change was kept for a different and real reason: the
descriptor table is blittable, which is what makes the seeding pass portable to
Burst at all.

### What did NOT work, and why

Level-parallel customization was supposed to be the payoff. Measured on the
4-core container, against 871 ms sequential:

| threads | time | vs sequential |
|---|---|---|
| 1 | 929 ms | 0.94× |
| 2 | 1,004 ms | **0.87×** |
| 4 | 748 ms | 1.16× |

**The curve is non-monotonic**, which rules out a clean bandwidth ceiling and
points at contention. Three contributing causes, in order of confidence:

1. **False sharing, structurally.** The layout is arc-major with K=16 lanes, so
   one arc's weights are `16 × 4 B = exactly one 64-byte cache line`. Two
   threads working adjacent arcs contend on the same line even when they touch
   no common data. That 2-thread regression is the signature.
2. **Level ordering costs locality.** Even at 1 thread the parallel path is 6%
   slower than the sequential sweep, because it visits nodes in level order
   while the sequential sweep visits in rank order, which matches the arc
   layout.
3. **Wrong axis for where the work is.** Road-network elimination trees are wide
   and cheap at the leaves but narrow and expensive at the top separators — 331
   levels averaging ~396 nodes. Node-parallelism is weakest exactly where the
   cost concentrates.

Two earlier defects were found and fixed along the way (a first cut ran at
0.8×): `ContractLevelRange` forced the atomic path even on levels executed
serially, and the atomic relax CAS'd unconditionally instead of testing for an
improvement first.

### Honest status

**Burst compatibility: delivered and enforced.** **Parallel speedup: not
demonstrated on this hardware** — 1.16× at 4 cores, non-monotonic, and the
sequential path remains the default (`FullCustomizeParallel` is opt-in).

This does not transfer as a negative result to the game: CS2 machines have
8–16 cores, and Unity's job system schedules far more cheaply than
`Parallel.For` per level. But that is a hypothesis, and it is not evidence.

**The next lever, now testable.** With the sweep resolving to ±0.7%, the
false-sharing hypothesis is finally falsifiable. The fix is alignment, not
padding: managed arrays are 8-byte aligned, so an arc's 16 lanes (64 B)
generally *straddle two* cache lines rather than occupying one. Moving `WFwd`/
`WBwd` to 64-byte-aligned unmanaged allocations would put each arc's block on
exactly one line — and would move the buffers closer to `NativeArray`
semantics, which the in-game path wants anyway. If that does not restore
scaling, node-level parallelism is the wrong axis here and the honest
conclusion is that customization throughput must come from Burst codegen
rather than from threads.

## A1 answered on REAL road networks (not the synthetic city)

The single biggest architectural risk was A1 — **do real road networks have the
small separators the CCH depends on?** Measured on real road topology from the
DIMACS/PACE corpus
([ben-strasser/road-graphs-pace16](https://github.com/ben-strasser/road-graphs-pace16))
via `harness import-dimacs`, this is a valid test despite the files carrying no
travel times: **the CCH skeleton is metric-independent by construction** (plan §4
Layer 0) — dissection, shortcut set, and elimination tree come from topology alone.

The first measurement (straight geometric cuts) said: architecture holds,
partitioner doesn't — tree height ~4× worse on real Paris than on the synthetic
city. The partitioner was replaced with **Inertial Flow** (Schild & Sommer 2015):
the geometric axis only *seeds* source/sink quarters, and a max-flow min-cut
(Dinic, node-splitting) decides where the cut actually runs — it finds the Seine
crossings and rail-trench bottlenecks a straight line cannot. Balance ≥ 25% holds
by construction; base cells are ordered by min-degree; coordinate-less graphs get
a BFS-level embedding as the projection instead of the old level-set bisection.

### Morphology sweep, geometric → Inertial Flow (`--partitioner` A/B, same harness)

The sweep spans city *shapes*, per the observation that a CS2 player city (deliberate
suburbs, engineered bottlenecks) is not any one real city: a dense monocentric core
(central Paris), a full metro (greater Paris), a polycentric conurbation (the Ruhr —
closest in spirit to a mature CS2 map with its multiple centers), a no-geometry
stress case (New York), and the synthetic grid.

| network | nodes | tree height | chordal arcs | query median | query p99 | vs Dijkstra | exact |
|---|---|---|---|---|---|---|---|
| central Paris core | 16,980 | 330 → **194** | 8.1× → **5.8×** | 164 → **50 µs** | 290 → **330 µs** | 11× → **32×** | 200/200 |
| Ruhr metro (polycentric) | 133,780 | 625 → **241** | 5.2× → **3.8×** | 428 → **87 µs** | 687 → **169 µs** | 33× → **151×** | 200/200 |
| greater Paris | 202,291 | 1,019 → **431** | 8.0× → **4.6×** | 849 → **216 µs** | 1,415 → **360 µs** | 27× → **112×** | 200/200 |
| New York (no coords) | 264,346 | 1,732 → **382** | 16.5× → **5.3×** | 4,169 → **132 µs** | 6,291 → **273 µs** | — → **203×** | 200/200 |
| synthetic grid | 14,399 | 136 → 159 | 6.9× → 6.7× | 33 → 31 µs | 75 → 75 µs | 54× → 45× | 200/200 |

### Verdict: the fix landed, and the diagnosis was right

**Real networks now sit at or beyond the synthetic benchmark.** The Ruhr — 133,780
real nodes, nearly the same scale as the 131k synthetic city — runs 87 µs median /
169 µs p99, matching the synthetic city's numbers at the same scale, at **151× over
per-query Dijkstra**. Greater Paris went from 27× to 112×. The earlier claim that
the synthetic city flattered the old partitioner is confirmed the other way around
too: on the grid, flow ≈ geometric (its district walls are exactly the cuts a
straight line finds), while on every real morphology flow wins 2–5× on height and
3–30× on query time.

**The BFS fallback is no longer a liability.** New York (no coordinates) was the
worst case at 4,169 µs median under level-set bisection; flow over a BFS-level
embedding makes it the *best* large case measured (132 µs, 203×). A CS2 export
always carries coordinates, but the coordinate-free path no longer needs that excuse.

**Height is not the whole cost story — arc volume is.** On the 131k synthetic
city, flow *raised* tree height (264 → 331) yet *cut* chordal arcs 24% (2.22M →
1.69M) and roughly halved measured query time, full customization, and
weight memory (284 → 216 MB). Smaller separators mean smaller cliques everywhere
even when the tree gets taller; partial customization and the event-driven bucket
channel (a congestion pocket now re-searches 231 destinations instead of 5,207)
inherit the same win. Part of the wall-clock delta is cache locality on smaller
cliques rather than pure arc count, so treat ratios other than the arc counts as
measured-on-this-container.

**Costs, stated plainly.** Order computation is 5–20× slower than the geometric
sweep (greater Paris 20.4 s, Ruhr 9.3 s, NY 7.2 s single-threaded C#) because each
cell may run up to four max-flows. That cost sits on the once-per-topology-edit
async rebuild path (§4 Layer 0), not on queries or refreshes, and directions are
independently parallelizable. The central-Paris flow p99 (330 µs vs geometric's
290 µs on a 3 s run) is measurement noise at that scale; its median is 3.3× better.

**A2 remains open** — demand locality needs recorded CS2 trips, which needs the
game. `harness import` already reports it whenever a demand trace is present
(the synthetic export replay measures 72–75% under either partitioner).

## §6 acceptance-target scorecard

| §6 target | measured (harness) | verdict |
|---|---|---|
| point-to-point query p99 < 20 µs at 10⁵ nodes, REAL topology | **169–360 µs p99, 112–203× vs Dijkstra** across the real-morphology sweep (Ruhr 134k: 87 µs median / 169 µs p99; greater Paris 202k: 216/360 µs; NY 264k: 132/273 µs) with Inertial Flow separators | architectural claim now holds on real maps at synthetic-benchmark levels; the absolute 20 µs still needs Burst-class constant factors |
| point-to-point query p99 < 20 µs at 10⁵ nodes, synthetic | 174 µs p99 (94.5 µs median), 122× faster than per-trip Dijkstra, 300/300 exact | architectural win proven; remaining gap to the absolute target is Burst-class constant factors |
| full customization < 10 ms | 531 ms (16 metrics, single-thread C#; halved by the smaller flow-cut cliques). Split: seeding 190 ms (21%) + triangle sweep 717 ms (79%). Level-parallel gives only **1.26× at 4 cores and is non-monotonic** (see the Burst-port section) | **miss, and the headroom claim is weaker than previously stated**: the sweep is Burst-legal and bit-identical under parallelism, but thread-parallelism did not deliver here. Leading suspect is 8-byte-aligned managed arrays making each arc's 64-byte lane block straddle two cache lines. Alignment work, not more threads, is the next lever — and the sweep now measures to ±0.7%, so it is finally falsifiable |
| typical partial customization < 1 ms | 6.9 ms median for a clustered congestion pocket (3,362 of 1.7M arcs — 0.2%); scattered random edges 92 ms | scaling property (cost ∝ change, not graph) demonstrated; absolute target plausible only under Burst with real change locality |
| sim speed ≥ 95% at 400k population | proxy only: 100k trips at 6.5× realtime, single-threaded C#, all subsystems itemized | not directly measurable outside the game |
| oscillation amplitude reduced ≥ 5× | **5.9×** vs the oscillating vanilla regime (fast signal, re-measured under the flow partitioner); vanilla's gridlock regime avoided entirely (2.6× travel-time win) | **met** |
| zero increase in unreachable-trip failures | 0 unreachable events across bench + 100k-trip sim | met |
| certified-exact ≥ 90%, mean gap < 1% | 77.8% certified at plan time (cold planner), gaps now logged as per-entry exploration demand rather than repaired synchronously | miss on the fraction; k-sweep shows anchor count is not the lever — §4.8 online adaptation consumes exactly the telemetry now emitted |
| exploration fully off critical path; sync fallback p99 < 0.5 ms (§6 v2) | 0 synchronous repairs; exploration budgeted off-path (~2.6 ms/task C#); 0 sync fallbacks needed, bounded by one CCH query (p99 154 µs) | met |

## What is deliberately not in the harness numbers

Turn costs/lane expansion, transit legs, and the game-side ECS adapters (stubbed,
documented) — see README "Known simplifications". The §5 build-order strategy is
exactly that these be validated in-game behind the ports, after the core is proven here.
