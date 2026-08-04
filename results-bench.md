## Scale benchmark (plan §6 targets)

Graph: **131,039 nodes / 482,554 directed lane-edges** (three road tiers, holes). Anchor grid: **8 preference profiles × 2 scenarios = 16 metrics**.

### Layer 0 — structure (once per topology edit, async)

| stage | result |
|---|---|
| nested dissection order | 853 ms |
| contraction (all shortcuts) | 3,386 ms |
| chordal arcs | 2,217,999 (9.2x undirected edges) |
| elimination tree height | 264 |
| multi-metric weight memory | 284 MB (16 metrics, fwd+bwd) |

### Layer 1 — customization (per traffic refresh)

| operation | time | §6 target |
|---|---|---|
| full customization, all 16 metrics | 1,170 ms | < 10 ms (Burst/SIMD budget) |
| partial, 100 edges ±10% drift, scattered (live lanes) | median 123.11 ms, p99 344.98 ms (47,556 arcs) | < 1 ms |
| partial, 150 edges ±10% drift, clustered (one congestion pocket) | median 10.35 ms, p99 21.58 ms (3,630 arcs) | < 1 ms |
| partial, 1000 edges ±10% drift, scattered (live lanes) | median 699.58 ms, p99 796.76 ms (336,142 arcs) | — |
| partial, 1000-edge large shock (0.8-2.4x) | 675 ms (319,932 arcs) | worst case, amortizable |

### Layer 2 — point-to-point queries (per trip, live metric)

| measure | CCH (this mod) | reference Dijkstra (vanilla-style) |
|---|---|---|
| median latency | 179.5 µs | 19,158 µs |
| mean latency | 185.2 µs | 20,536 µs |
| p99 latency | 317.5 µs | 47,585 µs |
| throughput (4 threads) | 20,829 queries/s | — |
| correctness spot-check | 300/300 exact | (reference) |

**Speedup: 111x per query** (plan §2 asks ~3 orders of magnitude; §6 target p99 < 20 µs).

### Layer 2 — full trip planning (portfolio + choice + certificate)

| measure | value | §6 target |
|---|---|---|
| plan latency median / p99 | 14599.1 µs / 54,744 µs | — |
| planning throughput (4 threads) | 197 trips/s | — |
| mean portfolio size | 2.5 alternatives | 3-5 |
| certified-exact fraction | 77.8 % | ≥ 90% |
| mean certified gap (uncertified tail) | 2.43 % | < 1% |
| repair searches | 7,075 (35.4 % of trips, 2,080 hit budget) | 2-10% |
| repair p99 latency | 12,156 µs | < 500 µs |
| Suurballe backups | 21 | — |
| unreachable trips | 0 | no increase vs vanilla (= genuine) |

### Layer 4 — via-node re-pricing (per alternative, two CCH queries)

median 171.9 µs, p99 278.2 µs — a 5-alternative portfolio re-prices in ~860 µs.

### Layer 3 — flexible destinations + service dispatch

| measure | value |
|---|---|
| bucket build, 10,000 destinations | 1,383 ms (2,019,898 entries) |
| staggered refresh, 500 backward searches | 48.1 ms |
| shopper query (scan 2,500 dests/category) | median 154.3 µs, p99 594 µs, 2995 entries scanned |
| dispatch query (fleet of 200) | median 128.8 µs, p99 409 µs |

Process memory after benchmark: 800 MB managed.

