## Scale benchmark (plan §6 targets)

Graph: **131,039 nodes / 482,554 directed lane-edges** (three road tiers, holes). Anchor grid: **8 preference profiles × 2 scenarios = 16 metrics**.

### Layer 0 — structure (once per topology edit, async)

| stage | result |
|---|---|
| nested dissection order | 462 ms |
| contraction (all shortcuts) | 1,038 ms |
| chordal arcs | 2,217,999 (9.2x undirected edges) |
| elimination tree height | 264 |
| multi-metric weight memory | 284 MB (16 metrics, fwd+bwd) |

### Layer 1 — customization (per traffic refresh)

| operation | time | §6 target |
|---|---|---|
| full customization, all 16 metrics | 695 ms | < 10 ms (Burst/SIMD budget) |
| partial, 100 edges ±10% drift, scattered (live lanes) | median 81.30 ms, p99 216.93 ms (47,556 arcs) | < 1 ms |
| partial, 150 edges ±10% drift, clustered (one congestion pocket) | median 6.29 ms, p99 14.87 ms (3,630 arcs) | < 1 ms |
| partial, 1000 edges ±10% drift, scattered (live lanes) | median 486.24 ms, p99 539.59 ms (336,142 arcs) | — |
| partial, 1000-edge large shock (0.8-2.4x) | 481 ms (319,932 arcs) | worst case, amortizable |

### Layer 2 — point-to-point queries (per trip, live metric)

| measure | CCH (this mod) | reference Dijkstra (vanilla-style) |
|---|---|---|
| median latency | 87.2 µs | 10,303 µs |
| mean latency | 90.4 µs | 11,036 µs |
| p99 latency | 148.5 µs | 23,821 µs |
| throughput (4 threads) | 42,630 queries/s | — |
| correctness spot-check | 300/300 exact | (reference) |

**Speedup: 122x per query** (plan §2 asks ~3 orders of magnitude; §6 target p99 < 20 µs).

### Layer 2 — full trip planning (portfolio + choice + certificate)

| measure | value | §6 target |
|---|---|---|
| plan latency median / p99 | 7403.2 µs / 32,905 µs | — |
| planning throughput (4 threads) | 351 trips/s | — |
| mean portfolio size | 2.5 alternatives | 3-5 |
| certified-exact fraction | 77.8 % | ≥ 90% |
| mean certified gap (uncertified tail) | 2.43 % | < 1% |
| repair searches | 7,075 (35.4 % of trips, 2,080 hit budget) | 2-10% |
| repair p99 latency | 5,665 µs | < 500 µs |
| Suurballe backups | 21 | — |
| unreachable trips | 0 | no increase vs vanilla (= genuine) |

### Layer 2 v2 — §4.9 route-knowledge cache (zonal demand, 65% on 40 zone pairs)

| measure | value | design claim |
|---|---|---|
| cache-served share | 84.2 % (25,247 of 30,000) | high under commuter locality |
| warm plan latency (cache-served) | median 2,880 µs, p99 6,967 µs | — |
| cold plan latency (direct generation, seeds entry) | median 3,014 µs, p99 8,119 µs | demoted to seeding fallback |
| warm/cold speedup | 1.0x median | — |
| quarantine diversions (thin entry -> direct gen) | 4,214 | surge never funneled onto one path |
| sync fallbacks (portfolio collapse) | 0, p99 0 µs | p99 < 500 µs (§6) |
| exploration | 600 tasks, 1 donated vias, 2,812 µs/task | off the critical path |
| certificate gaps -> exploration demand | 10,553 (no synchronous repairs: 0) | §4.7 v2 |
| cache footprint | 539 entries, 0.1 MB | tens of MB at 10⁴-10⁵ entries |
| retained per-agent cursor | 108 B (+921 B driven geometry) | tens of bytes + driven route |

### Layer 4 — via-node re-pricing (per alternative, two CCH queries)

median 77.2 µs, p99 124.6 µs — a 5-alternative portfolio re-prices in ~386 µs.

### Layer 3 — flexible destinations + service dispatch

| measure | value |
|---|---|
| bucket build, 10,000 destinations | 706 ms (2,019,898 entries) |
| staggered refresh, 500 backward searches | 29.1 ms |
| shopper query (scan 2,500 dests/category) | median 83.8 µs, p99 380 µs, 3003 entries scanned |
| dispatch query (fleet of 200) | median 71.4 µs, p99 285 µs |

Process memory after benchmark: 1,294 MB managed.

