## Scale benchmark (plan §6 targets)

Graph: **131,042 nodes / 495,612 directed lane-edges** (three road tiers, holes). Anchor grid: **8 preference profiles × 2 scenarios = 16 metrics**.

### Layer 0 — structure (once per topology edit, async)

| stage | result |
|---|---|
| nested dissection order | 575 ms |
| contraction (all shortcuts) | 8,880 ms |
| chordal arcs | 4,933,897 (19.9x undirected edges) |
| elimination tree height | 1059 |
| multi-metric weight memory | 632 MB (16 metrics, fwd+bwd) |

### Layer 1 — customization (per traffic refresh)

| operation | time | §6 target |
|---|---|---|
| full customization, all 16 metrics | 24,713 ms | < 10 ms (Burst/SIMD budget) |
| partial, 100 edges ±10% drift (live lanes) | median 17056.52 ms, p99 26494.81 ms (673,850 arcs) | < 1 ms |
| partial, 1000 edges ±10% drift (live lanes) | median 39900.08 ms, p99 42848.80 ms (2,280,999 arcs) | — |
| partial, 1000-edge large shock (0.8-2.4x) | 46,605 ms (2,271,363 arcs) | worst case, amortizable |

### Layer 2 — point-to-point queries (per trip, live metric)

| measure | CCH (this mod) | reference Dijkstra (vanilla-style) |
|---|---|---|
| median latency | 5967.3 µs | 21,282 µs |
| mean latency | 5973.4 µs | 20,178 µs |
| p99 latency | 10927.1 µs | 41,395 µs |
| throughput (4 threads) | 661 queries/s | — |
| correctness spot-check | 300/300 exact | (reference) |

**Speedup: 3x per query** (plan §2 asks ~3 orders of magnitude; §6 target p99 < 20 µs).

### Layer 2 — full trip planning (portfolio + choice + certificate)

| measure | value | §6 target |
|---|---|---|
| plan latency median / p99 | 175668.1 µs / 458,428 µs | — |
| planning throughput (4 threads) | 19 trips/s | — |
| mean portfolio size | 2.9 alternatives | 3-5 |
| certified-exact fraction | 70.9 % | ≥ 90% |
| mean certified gap (uncertified tail) | 2.39 % | < 1% |
| repair searches | 12,938 (64.7 % of trips) | 2-10% |
| repair p99 latency | 119,043 µs | < 500 µs |
| Suurballe backups | 453 | — |
| unreachable trips | 0 | no increase vs vanilla (= genuine) |

### Layer 4 — via-node re-pricing (per alternative, two CCH queries)

median 6686.1 µs, p99 15582.3 µs — a 5-alternative portfolio re-prices in ~33430 µs.

### Layer 3 — flexible destinations + service dispatch

| measure | value |
|---|---|
| bucket build, 10,000 destinations | 30,229 ms (10,017,151 entries) |
| staggered refresh, 500 backward searches | 1221.6 ms |
| shopper query (scan 2,500 dests/category) | median 3003.8 µs, p99 4,066 µs, 12146 entries scanned |
| dispatch query (fleet of 200) | median 2991.5 µs, p99 4,036 µs |

Process memory after benchmark: 1,806 MB managed.

