## Scale benchmark (plan §6 targets)

Graph: **131,039 nodes / 482,554 directed lane-edges** (three road tiers, holes). Anchor grid: **8 preference profiles × 2 scenarios = 16 metrics**.

### Layer 0 — structure (once per topology edit, async)

| stage | result |
|---|---|
| nested dissection order | 604 ms |
| contraction (all shortcuts) | 1,433 ms |
| chordal arcs | 2,217,999 (9.2x undirected edges) |
| elimination tree height | 264 |
| multi-metric weight memory | 284 MB (16 metrics, fwd+bwd) |

### Layer 1 — customization (per traffic refresh)

| operation | time | §6 target |
|---|---|---|
| full customization, all 16 metrics | 1,104 ms | < 10 ms (Burst/SIMD budget) |
| partial, 100 edges ±10% drift (live lanes) | median 131.61 ms, p99 248.65 ms (47,556 arcs) | < 1 ms |
| partial, 1000 edges ±10% drift (live lanes) | median 715.66 ms, p99 837.63 ms (336,142 arcs) | — |
| partial, 1000-edge large shock (0.8-2.4x) | 789 ms (315,309 arcs) | worst case, amortizable |

### Layer 2 — point-to-point queries (per trip, live metric)

| measure | CCH (this mod) | reference Dijkstra (vanilla-style) |
|---|---|---|
| median latency | 173.1 µs | 24,051 µs |
| mean latency | 182.4 µs | 22,659 µs |
| p99 latency | 311.8 µs | 46,444 µs |
| throughput (4 threads) | 20,958 queries/s | — |
| correctness spot-check | 300/300 exact | (reference) |

**Speedup: 124x per query** (plan §2 asks ~3 orders of magnitude; §6 target p99 < 20 µs).

### Layer 2 — full trip planning (portfolio + choice + certificate)

| measure | value | §6 target |
|---|---|---|
| plan latency median / p99 | 19484.6 µs / 216,490 µs | — |
| planning throughput (4 threads) | 82 trips/s | — |
| mean portfolio size | 2.5 alternatives | 3-5 |
| certified-exact fraction | 70.7 % | ≥ 90% |
| mean certified gap (uncertified tail) | 2.37 % | < 1% |
| repair searches | 12,887 (64.4 % of trips) | 2-10% |
| repair p99 latency | 67,436 µs | < 500 µs |
| Suurballe backups | 446 | — |
| unreachable trips | 0 | no increase vs vanilla (= genuine) |

### Layer 4 — via-node re-pricing (per alternative, two CCH queries)

median 193.1 µs, p99 333.8 µs — a 5-alternative portfolio re-prices in ~965 µs.

### Layer 3 — flexible destinations + service dispatch

| measure | value |
|---|---|
| bucket build, 10,000 destinations | 1,430 ms (2,019,898 entries) |
| staggered refresh, 500 backward searches | 48.1 ms |
| shopper query (scan 2,500 dests/category) | median 160.8 µs, p99 595 µs, 2996 entries scanned |
| dispatch query (fleet of 200) | median 134.8 µs, p99 500 µs |

Process memory after benchmark: 443 MB managed.

