## Scale benchmark (plan §6 targets)

Graph: **131,039 nodes / 482,554 directed lane-edges** (three road tiers, holes). Anchor grid: **8 preference profiles × 2 scenarios = 16 metrics**.

### Layer 0 — structure (once per topology edit, async)

| stage | result |
|---|---|
| nested dissection order | 1,012 ms |
| contraction (all shortcuts) | 2,336 ms |
| chordal arcs | 2,217,999 (9.2x undirected edges) |
| elimination tree height | 264 |
| multi-metric weight memory | 284 MB (16 metrics, fwd+bwd) |

### Layer 1 — customization (per traffic refresh)

| operation | time | §6 target |
|---|---|---|
| full customization, all 16 metrics | 1,059 ms | < 10 ms (Burst/SIMD budget) |
| partial, 100 edges ±10% drift, scattered (live lanes) | median 124.70 ms, p99 248.02 ms (47,556 arcs) | < 1 ms |
| partial, 150 edges ±10% drift, clustered (one congestion pocket) | median 9.30 ms, p99 21.68 ms (3,630 arcs) | < 1 ms |
| partial, 1000 edges ±10% drift, scattered (live lanes) | median 719.15 ms, p99 762.31 ms (336,142 arcs) | — |
| partial, 1000-edge large shock (0.8-2.4x) | 696 ms (319,932 arcs) | worst case, amortizable |

### Layer 2 — point-to-point queries (per trip, live metric)

| measure | CCH (this mod) | reference Dijkstra (vanilla-style) |
|---|---|---|
| median latency | 191.1 µs | 18,510 µs |
| mean latency | 203.8 µs | 19,894 µs |
| p99 latency | 367.3 µs | 45,946 µs |
| throughput (4 threads) | 19,035 queries/s | — |
| correctness spot-check | 300/300 exact | (reference) |

**Speedup: 98x per query** (plan §2 asks ~3 orders of magnitude; §6 target p99 < 20 µs).

### Layer 2 — full trip planning (portfolio + choice + certificate)

| measure | value | §6 target |
|---|---|---|
| plan latency median / p99 | 15474.2 µs / 59,035 µs | — |
| planning throughput (4 threads) | 189 trips/s | — |
| mean portfolio size | 2.5 alternatives | 3-5 |
| certified-exact fraction | 77.8 % | ≥ 90% |
| mean certified gap (uncertified tail) | 2.43 % | < 1% |
| repair searches | 7,075 (35.4 % of trips, 2,080 hit budget) | 2-10% |
| repair p99 latency | 13,685 µs | < 500 µs |
| Suurballe backups | 21 | — |
| unreachable trips | 0 | no increase vs vanilla (= genuine) |

### Layer 2 v2 — §4.9 route-knowledge cache (zonal demand, 65% on 40 zone pairs)

| measure | value | design claim |
|---|---|---|
| cache-served share | 84.2 % (25,247 of 30,000) | high under commuter locality |
| warm plan latency (cache-served) | median 2,366 µs, p99 5,906 µs | — |
| cold plan latency (direct generation, seeds entry) | median 6,005 µs, p99 21,408 µs | demoted to seeding fallback |
| warm/cold speedup | 2.5x median | — |
| quarantine diversions (thin entry -> direct gen) | 4,214 | surge never funneled onto one path |
| sync fallbacks (portfolio collapse) | 0, p99 0 µs | p99 < 500 µs (§6) |
| exploration | 600 tasks, 2 donated vias, 4,439 µs/task | off the critical path |
| certificate gaps -> exploration demand | 13,898 (no synchronous repairs: 0) | §4.7 v2 |
| cache footprint | 539 entries, 0.1 MB | tens of MB at 10⁴-10⁵ entries |
| retained per-agent cursor | 141 B (+922 B driven geometry) | tens of bytes + driven route |

### Layer 4 — via-node re-pricing (per alternative, two CCH queries)

median 232.6 µs, p99 663.6 µs — a 5-alternative portfolio re-prices in ~1163 µs.

### Layer 3 — flexible destinations + service dispatch

| measure | value |
|---|---|
| bucket build, 10,000 destinations | 2,500 ms (2,019,898 entries) |
| blind staggered refresh, 500 backward searches (v2, kept for A/B) | 47.7 ms |
| event-driven refresh, quiescent full rotation of 10,000 dests | 9.3 ms (0 re-searches) |
| event-driven refresh after a 150-edge congestion pocket | 555.3 ms (5207 of 10000 scanned re-searched) |
| shopper query (scan 2,500 dests/category) | median 163.4 µs, p99 656 µs, 3008 entries scanned |
| dispatch query (fleet of 200) | median 133.7 µs, p99 419 µs |

Process memory after benchmark: 823 MB managed.

