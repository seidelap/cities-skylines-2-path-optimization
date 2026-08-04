## Imported city (real CS2 export)

`synth.cs2city` — **8,100 nodes / 29,796 directed edges**, 0 edges carrying an exported congestion value, 5,000 recorded trips. Anchors: 8 profiles × 3 scenarios.

### A1 — separator quality (does the CCH hierarchy hold up on a real map?)

| measure | this city | synthetic 131k baseline | reads as |
|---|---|---|---|
| nested dissection order | 120 ms | 470–870 ms | — |
| contraction | 127 ms | ~1,100–3,400 ms | — |
| chordal arcs | 130,863 (8.8× undirected edges) | 9.2× | lower is better |
| elimination tree height | 152 | 264 | **the key number** — height drives query cost |
| full customization (24 metrics) | 103 ms | ~1,100 ms @16 | — |

### Query performance on the real graph

| measure | CCH | reference Dijkstra |
|---|---|---|
| median | 49 µs | 1,115 µs |
| p99 | 108 µs | 5,546 µs |
| speedup (mean) | 22× | — |
| correctness | 200/200 exact vs Dijkstra | (reference) |

### A2 — demand locality (does the §4.9 cluster cache pay off on real trips?)

| measure | this city | synthetic zonal baseline |
|---|---|---|
| trips replayed | 5,000 | 30,000 |
| cache-served share | **75.7 %** | 84.2% |
| warm plan median | 619 µs | ~1,130 µs |
| cold plan median | 868 µs | ~2,972 µs |
| entries / footprint | 386 / 0.1 MB | 539 / 0.1 MB |
| certified-exact | 51.6 % | 77.8% |
| unreachable | 0 | 0 |

