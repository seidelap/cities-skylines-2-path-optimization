## Scale benchmark (plan §6 targets)

Graph: **131,039 nodes / 482,554 directed lane-edges** (three road tiers, holes). Anchor grid: **12 preference profiles × 2 scenarios = 24 metrics**.

### Layer 0 — structure (once per topology edit, async)

| stage | result |
|---|---|
| nested dissection order | 764 ms |
| contraction (all shortcuts) | 1,333 ms |
| chordal arcs | 2,217,999 (9.2x undirected edges) |
| elimination tree height | 264 |
| multi-metric weight memory | 426 MB (24 metrics, fwd+bwd) |

### Layer 1 — customization (per traffic refresh)

| operation | time | §6 target |
|---|---|---|
| full customization, all 24 metrics | 1,414 ms | < 10 ms (Burst/SIMD budget) |
| partial, 100 edges ±10% drift, scattered (live lanes) | median 149.24 ms, p99 277.08 ms (47,745 arcs) | < 1 ms |
| partial, 150 edges ±10% drift, clustered (one congestion pocket) | median 11.41 ms, p99 28.19 ms (3,636 arcs) | < 1 ms |
| partial, 1000 edges ±10% drift, scattered (live lanes) | median 834.61 ms, p99 912.13 ms (337,085 arcs) | — |
| partial, 1000-edge large shock (0.8-2.4x) | 900 ms (321,700 arcs) | worst case, amortizable |

### Layer 2 — point-to-point queries (per trip, live metric)

| measure | CCH (this mod) | reference Dijkstra (vanilla-style) |
|---|---|---|
| median latency | 236.8 µs | 20,392 µs |
| mean latency | 257.5 µs | 21,730 µs |
| p99 latency | 427.6 µs | 55,028 µs |
| throughput (4 threads) | 14,439 queries/s | — |
| correctness spot-check | 300/300 exact | (reference) |

**Speedup: 84x per query** (plan §2 asks ~3 orders of magnitude; §6 target p99 < 20 µs).

### Layer 2 — full trip planning (portfolio + choice + certificate)

| measure | value | §6 target |
|---|---|---|
| plan latency median / p99 | 17385.5 µs / 69,689 µs | — |
| planning throughput (4 threads) | 164 trips/s | — |
| mean portfolio size | 2.4 alternatives | 3-5 |
| certified-exact fraction | 82.1 % | ≥ 90% |
| mean certified gap (uncertified tail) | 2.38 % | < 1% |
| repair searches | 5,418 (27.1 % of trips, 1,619 hit budget) | 2-10% |
| repair p99 latency | 14,420 µs | < 500 µs |
| Suurballe backups | 186 | — |
| unreachable trips | 0 | no increase vs vanilla (= genuine) |

### Layer 4 — via-node re-pricing (per alternative, two CCH queries)

median 220.9 µs, p99 372.5 µs — a 5-alternative portfolio re-prices in ~1105 µs.

### Layer 3 — flexible destinations + service dispatch

| measure | value |
|---|---|
| bucket build, 10,000 destinations | 1,450 ms (2,019,898 entries) |
| staggered refresh, 500 backward searches | 54.9 ms |
| shopper query (scan 2,500 dests/category) | median 174.0 µs, p99 580 µs, 2995 entries scanned |
| dispatch query (fleet of 200) | median 159.1 µs, p99 453 µs |

Process memory after benchmark: 767 MB managed.

