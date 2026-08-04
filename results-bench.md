## Scale benchmark (plan §6 targets)

Graph: **131,039 nodes / 482,554 directed lane-edges** (three road tiers, holes). Anchor grid: **8 preference profiles × 2 scenarios = 16 metrics**.

### Layer 0 — structure (once per topology edit, async)

| stage | result |
|---|---|
| nested dissection order | 875 ms |
| contraction (all shortcuts) | 1,269 ms |
| chordal arcs | 2,217,999 (9.2x undirected edges) |
| elimination tree height | 264 |
| multi-metric weight memory | 284 MB (16 metrics, fwd+bwd) |

### Layer 1 — customization (per traffic refresh)

| operation | time | §6 target |
|---|---|---|
| full customization, all 16 metrics | 1,058 ms | < 10 ms (Burst/SIMD budget) |
| partial, 100 edges ±10% drift, scattered (live lanes) | median 132.24 ms, p99 248.81 ms (47,556 arcs) | < 1 ms |
| partial, 150 edges ±10% drift, clustered (one congestion pocket) | median 9.39 ms, p99 22.45 ms (3,630 arcs) | < 1 ms |
| partial, 1000 edges ±10% drift, scattered (live lanes) | median 742.57 ms, p99 786.98 ms (336,142 arcs) | — |
| partial, 1000-edge large shock (0.8-2.4x) | 759 ms (319,932 arcs) | worst case, amortizable |

### Layer 2 — point-to-point queries (per trip, live metric)

| measure | CCH (this mod) | reference Dijkstra (vanilla-style) |
|---|---|---|
| median latency | 171.6 µs | 18,378 µs |
| mean latency | 180.4 µs | 19,873 µs |
| p99 latency | 314.9 µs | 43,303 µs |
| throughput (4 threads) | 20,877 queries/s | — |
| correctness spot-check | 300/300 exact | (reference) |

**Speedup: 110x per query** (plan §2 asks ~3 orders of magnitude; §6 target p99 < 20 µs).

### Layer 2 — full trip planning (portfolio + choice + certificate)

| measure | value | §6 target |
|---|---|---|
| plan latency median / p99 | 15376.5 µs / 76,294 µs | — |
| planning throughput (4 threads) | 179 trips/s | — |
| mean portfolio size | 2.6 alternatives | 3-5 |
| certified-exact fraction | 82.1 % | ≥ 90% |
| mean certified gap (uncertified tail) | 2.63 % | < 1% |
| repair searches | 7,075 (35.4 % of trips, 1,211 hit budget) | 2-10% |
| repair p99 latency | 21,292 µs | < 500 µs |
| Suurballe backups | 185 | — |
| unreachable trips | 0 | no increase vs vanilla (= genuine) |

### Layer 4 — via-node re-pricing (per alternative, two CCH queries)

median 173.2 µs, p99 298.1 µs — a 5-alternative portfolio re-prices in ~866 µs.

### Layer 3 — flexible destinations + service dispatch

| measure | value |
|---|---|
| bucket build, 10,000 destinations | 1,208 ms (2,019,898 entries) |
| staggered refresh, 500 backward searches | 45.0 ms |
| shopper query (scan 2,500 dests/category) | median 152.4 µs, p99 565 µs, 2995 entries scanned |
| dispatch query (fleet of 200) | median 132.0 µs, p99 413 µs |

Process memory after benchmark: 750 MB managed.

