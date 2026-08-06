## Imported city (real CS2 export)

`paris.cs2city` — **202,291 nodes / 564,272 directed edges**, 0 edges carrying an exported congestion value, 0 recorded trips. Anchors: 4 profiles × 3 scenarios.

### A1 — separator quality (does the CCH hierarchy hold up on a real map?)

| measure | this city | synthetic 131k baseline | reads as |
|---|---|---|---|
| nested dissection order | 1,195 ms | 470–870 ms | — |
| contraction | 1,903 ms | ~1,100–3,400 ms | — |
| chordal arcs | 2,258,374 (8.0× undirected edges) | 9.2× | lower is better |
| elimination tree height | 1,019 | 264 | **the key number** — height drives query cost |
| full customization (12 metrics) | 3,414 ms | ~1,100 ms @16 | — |

### Query performance on the real graph

| measure | CCH | reference Dijkstra |
|---|---|---|
| median | 849 µs | 21,258 µs |
| p99 | 1,415 µs | 51,218 µs |
| speedup (mean) | 27× | — |
| correctness | 200/200 exact vs Dijkstra | (reference) |

### A2 — demand locality

_No demand trace in this export — re-export with trip recording enabled to measure cache hit rate._

