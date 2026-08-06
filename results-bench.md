## Scale benchmark (plan §6 targets)

Graph: **131,039 nodes / 482,554 directed lane-edges** (three road tiers, holes). Anchor grid: **8 preference profiles × 2 scenarios = 16 metrics**.

### Layer 0 — structure (once per topology edit, async)

| stage | result |
|---|---|
| nested dissection order | 2,906 ms |
| contraction (all shortcuts) | 998 ms |
| chordal arcs | 1,686,815 (7.0x undirected edges) |
| elimination tree height | 331 |
| multi-metric weight memory | 216 MB (16 metrics, fwd+bwd) |

### Layer 1 — customization (per traffic refresh)

| operation | time | §6 target |
|---|---|---|
| full customization, all 16 metrics (sequential) | 909 ms | < 10 ms (Burst/SIMD budget) |
| full customization, level-parallel thread scaling (331 levels) | 1t=983ms(0.92x), 2t=1,024ms(0.89x), 4t=751ms(1.21x) | flattening ⇒ bandwidth-bound, not thread-starved |
| partial, 100 edges ±10% drift, scattered (live lanes) | median 81.10 ms, p99 219.80 ms (43,016 arcs) | < 1 ms |
| partial, 150 edges ±10% drift, clustered (one congestion pocket) | median 5.85 ms, p99 13.15 ms (3,362 arcs) | < 1 ms |
| partial, 1000 edges ±10% drift, scattered (live lanes) | median 444.23 ms, p99 462.60 ms (282,503 arcs) | — |
| partial, 1000-edge large shock (0.8-2.4x) | 409 ms (265,118 arcs) | worst case, amortizable |

### Layer 2 — point-to-point queries (per trip, live metric)

| measure | CCH (this mod) | reference Dijkstra (vanilla-style) |
|---|---|---|
| median latency | 101.6 µs | 11,588 µs |
| mean latency | 120.2 µs | 12,323 µs |
| p99 latency | 453.6 µs | 27,524 µs |
| throughput (4 threads) | 31,181 queries/s | — |
| correctness spot-check | 300/300 exact | (reference) |

**Speedup: 103x per query** (plan §2 asks ~3 orders of magnitude; §6 target p99 < 20 µs).

### Layer 2 — full trip planning (portfolio + choice + certificate)

| measure | value | §6 target |
|---|---|---|
| plan latency median / p99 | 8598.9 µs / 60,097 µs | — |
| planning throughput (4 threads) | 265 trips/s | — |
| mean portfolio size | 2.3 alternatives | 3-5 |
| certified-exact fraction | 78.9 % | ≥ 90% |
| mean certified gap (uncertified tail) | 2.44 % | < 1% |
| repair searches | 382 (38.2 % of trips, 97 hit budget) | 2-10% |
| repair p99 latency | 10,216 µs | < 500 µs |
| Suurballe backups | 1 | — |
| unreachable trips | 0 | no increase vs vanilla (= genuine) |

### Layer 2 v2 — §4.9 route-knowledge cache (zonal demand, 65% on 40 zone pairs)

| measure | value | design claim |
|---|---|---|
| cache-served share | 81.8 % (24,535 of 30,000) | high under commuter locality |
| warm plan latency (cache-served) | median 1,266 µs, p99 2,684 µs | — |
| cold plan latency (direct generation, seeds entry) | median 3,056 µs, p99 9,406 µs | demoted to seeding fallback |
| warm/cold speedup | 2.4x median | — |
| quarantine diversions (thin entry -> direct gen) | 4,883 | surge never funneled onto one path |
| sync fallbacks (portfolio collapse) | 0, p99 0 µs | p99 < 500 µs (§6) |
| exploration | 600 tasks, 0 donated vias, 2,080 µs/task | off the critical path |
| certificate gaps -> exploration demand | 14,565 (no synchronous repairs: 0) | §4.7 v2 |
| cache footprint | 582 entries, 0.1 MB | tens of MB at 10⁴-10⁵ entries |
| retained per-agent cursor | 138 B (+938 B driven geometry) | tens of bytes + driven route |

### Layer 4 — via-node re-pricing (per alternative, two CCH queries)

median 96.7 µs, p99 183.0 µs — a 5-alternative portfolio re-prices in ~483 µs.

### Layer 3 — flexible destinations + service dispatch

| measure | value |
|---|---|
| bucket build, 10,000 destinations | 835 ms (2,091,240 entries) |
| blind staggered refresh, 500 backward searches (v2, kept for A/B) | 31.6 ms |
| event-driven refresh, quiescent full rotation of 10,000 dests | 5.5 ms (0 re-searches) |
| event-driven refresh after a 150-edge congestion pocket | 59.8 ms (962 of 10000 scanned re-searched) |
| shopper query (scan 2,500 dests/category) | median 91.7 µs, p99 584 µs, 2896 entries scanned |
| dispatch query (fleet of 200) | median 87.9 µs, p99 372 µs |

Process memory after benchmark: 803 MB managed.

