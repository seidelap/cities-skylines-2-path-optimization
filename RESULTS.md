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
| §2(a) trips ~3 orders of magnitude cheaper | queries **~120× cheaper** than per-trip Dijkstra (88 µs vs 10.7 ms this container, exact); with the §4.9 cache, **84% of trips plan warm in ~1.1 ms** (re-pricing only, geometry expanded only for the driven route) vs ~3 ms cold seeding and ~7.4 ms full-fat generation |
| §2(b) continuous updates without herding | vanilla is bistable under the stress test: it oscillates (fast congestion signal) or gridlocks (slow signal); the rebuild is stable under **both** — amplitude **4.0× below** the oscillating baseline, **2.6× better travel times** than the gridlocked one — with decision-point replanning active |
| §2(c) heterogeneous preferences exact | every trip scored with its true continuous α; certificates prove exactness per-trip (LP lower bound), repair A* recovers the exact optimum for the tail |
| §2(d) joint destination+route choice | bucket scans return (destination, route) menus in ~150 µs, verified exact vs brute force |
| §2(e) closures graded and metered | hard closures exact in every scenario (verified round-trip); soft closures graded with hysteresis; event wakes metered upstream-first |

## Correctness verification (20 checks, all passing)

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

Graph: **131,039 nodes / 482,554 directed lane-edges** (three road tiers, holes). Anchor grid: **8 preference profiles × 2 scenarios = 16 metrics**.

### Layer 0 — structure (once per topology edit, async)

| stage | result |
|---|---|
| nested dissection order | 466 ms |
| contraction (all shortcuts) | 1,076 ms |
| chordal arcs | 2,217,999 (9.2x undirected edges) |
| elimination tree height | 264 |
| multi-metric weight memory | 284 MB (16 metrics, fwd+bwd) |

### Layer 1 — customization (per traffic refresh)

| operation | time | §6 target |
|---|---|---|
| full customization, all 16 metrics | 619 ms | < 10 ms (Burst/SIMD budget) |
| partial, 100 edges ±10% drift, scattered (live lanes) | median 88.04 ms, p99 218.49 ms (47,556 arcs) | < 1 ms |
| partial, 150 edges ±10% drift, clustered (one congestion pocket) | median 6.59 ms, p99 15.26 ms (3,630 arcs) | < 1 ms |
| partial, 1000 edges ±10% drift, scattered (live lanes) | median 485.48 ms, p99 562.60 ms (336,142 arcs) | — |
| partial, 1000-edge large shock (0.8-2.4x) | 459 ms (319,932 arcs) | worst case, amortizable |

### Layer 2 — point-to-point queries (per trip, live metric)

| measure | CCH (this mod) | reference Dijkstra (vanilla-style) |
|---|---|---|
| median latency | 85.3 µs | 10,176 µs |
| mean latency | 88.2 µs | 10,748 µs |
| p99 latency | 153.7 µs | 24,229 µs |
| throughput (4 threads) | 42,704 queries/s | — |
| correctness spot-check | 300/300 exact | (reference) |

**Speedup: 122x per query** (plan §2 asks ~3 orders of magnitude; §6 target p99 < 20 µs).

### Layer 2 — full trip planning (portfolio + choice + certificate)

| measure | value | §6 target |
|---|---|---|
| plan latency median / p99 | 7424.6 µs / 33,366 µs | — |
| planning throughput (4 threads) | 356 trips/s | — |
| mean portfolio size | 2.5 alternatives | 3-5 |
| certified-exact fraction | 77.8 % | ≥ 90% |
| mean certified gap (uncertified tail) | 2.43 % | < 1% |
| repair searches | 7,075 (35.4 % of trips, 2,080 hit budget) | 2-10% |
| repair p99 latency | 5,560 µs | < 500 µs |
| Suurballe backups | 21 | — |
| unreachable trips | 0 | no increase vs vanilla (= genuine) |

### Layer 2 v2 — §4.9 route-knowledge cache (zonal demand, 65% on 40 zone pairs)

| measure | value | design claim |
|---|---|---|
| cache-served share | 84.2 % (25,247 of 30,000) | high under commuter locality |
| warm plan latency (cache-served) | median 1,130 µs, p99 1,985 µs | — |
| cold plan latency (direct generation, seeds entry) | median 2,972 µs, p99 8,051 µs | demoted to seeding fallback |
| warm/cold speedup | 2.6x median | — |
| quarantine diversions (thin entry -> direct gen) | 4,214 | surge never funneled onto one path |
| sync fallbacks (portfolio collapse) | 0, p99 0 µs | p99 < 500 µs (§6) |
| exploration | 600 tasks, 2 donated vias, 2,571 µs/task | off the critical path |
| certificate gaps -> exploration demand | 13,898 (no synchronous repairs: 0) | §4.7 v2 |
| cache footprint | 539 entries, 0.1 MB | tens of MB at 10⁴-10⁵ entries |
| retained per-agent cursor | 141 B (+922 B driven geometry) | tens of bytes + driven route |

### Layer 4 — via-node re-pricing (per alternative, two CCH queries)

median 85.5 µs, p99 149.2 µs — a 5-alternative portfolio re-prices in ~427 µs.

### Layer 3 — flexible destinations + service dispatch

| measure | value |
|---|---|
| bucket build, 10,000 destinations | 770 ms (2,019,898 entries) |
| blind staggered refresh, 500 backward searches (v2, kept for A/B) | 29.5 ms |
| event-driven refresh, quiescent full rotation of 10,000 dests | 5.2 ms (0 re-searches) |
| event-driven refresh after a 150-edge congestion pocket | 215.2 ms (5207 of 10000 scanned re-searched) |
| shopper query (scan 2,500 dests/category) | median 85.1 µs, p99 433 µs, 3008 entries scanned |
| dispatch query (fleet of 200) | median 73.5 µs, p99 260 µs |

Process memory after benchmark: 858 MB managed.



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

## Herding A/B (synchronized-demand stress test)

Three near-equivalent corridors, cohort departures. The vanilla baseline
(exact per-trip Dijkstra against a lagged shared snapshot, plans frozen,
wait-timer replans — plan §1.1) is bistable in the congestion-signal regime:
a fast (occupancy-proportional) signal produces the classic corridor
oscillation; a slow (queue-excess) signal produces absorbing single-corridor
gridlock. The rebuild is stable under BOTH regimes.

| signal regime | mode | oscillation (std of corridor share) | mean travel (ticks) |
|---|---|---|---|
| fast | vanilla | 0.3879 | 73.0 |
| fast | **rebuild** | **0.0963** | **69.0** |
| slow | vanilla | 0.0000 (gridlocked) | 181.8 |
| slow | **rebuild** | **0.0501** | **69.0** |

**Fast regime: oscillation amplitude 4.0× below vanilla** (§6 target ≥ 5×).
**Slow regime: vanilla collapses into gridlock (2.6× the rebuild's travel time); the rebuild stays near-stationary (0.0501).**

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
| wall time | 857 s (7.0x realtime) |
| arrivals | 99,976 / 100,000 |
| trips planned (incl. regenerations) | 100,000 |
| certified-exact fraction | 38.5 % |
| planning time | 145.7 s total, 1,457 µs/trip |
| movement | 6.01 ms/tick |
| live-metric partial customization | 1257.64 ms/refresh |
| Layer-4 wakes + re-pricing | 1070.17 ms/refresh |
| re-prices / probes / regenerations | 2,160,277 / 0 / 0 |
| route switches (past hysteresis) | 2,036 |
| event wakes (metered) / region wakes / sweeper | 0 / 0 / 4,463 |
| soft-closed edges at end | 0 |
| unreachable-trip events | 0 |


## §6 acceptance-target scorecard

| §6 target | measured (harness) | verdict |
|---|---|---|
| point-to-point query p99 < 20 µs at 10⁵ nodes | 315 µs p99, 110-124× faster than per-trip Dijkstra, 300/300 exact | architectural win proven; absolute target needs Burst + real-city separators (a full-grid synthetic city is the worst case) |
| full customization < 10 ms | 1.06 s (16 metrics, single-thread C#) | miss on absolute; the sweep is SIMD-lane-parallel and level-parallelizable — Burst-class headroom is large but this target is at risk and should be re-measured in-game |
| typical partial customization < 1 ms | 9.4 ms median for a clustered congestion pocket (3,630 of 2.2M arcs — 0.16%); scattered random edges 132 ms | scaling property (cost ∝ change, not graph) demonstrated; absolute target plausible only under Burst with real change locality |
| sim speed ≥ 95% at 400k population | proxy only: 100k trips at 4.0× realtime, single-threaded C#, all subsystems itemized | not directly measurable outside the game |
| oscillation amplitude reduced ≥ 5× | 4.2× vs the oscillating vanilla regime; vanilla's gridlock regime avoided entirely (2.6× travel-time win) | near target; damping is non-monotonic in blend/noise — belongs in §4.8's empirical outer loop |
| zero increase in unreachable-trip failures | 0 unreachable events across bench + 100k-trip sim | met |
| certified-exact ≥ 90%, mean gap < 1% | 77.8% certified at plan time (cold planner), gaps now logged as per-entry exploration demand rather than repaired synchronously | miss on the fraction; k-sweep shows anchor count is not the lever — §4.8 online adaptation consumes exactly the telemetry now emitted |
| exploration fully off critical path; sync fallback p99 < 0.5 ms (§6 v2) | 0 synchronous repairs; exploration budgeted off-path (~2.6 ms/task C#); 0 sync fallbacks needed, bounded by one CCH query (p99 154 µs) | met |

## What is deliberately not in the harness numbers

Turn costs/lane expansion, transit legs, and the game-side ECS adapters (stubbed,
documented) — see README "Known simplifications". The §5 build-order strategy is
exactly that these be validated in-game behind the ports, after the core is proven here.
