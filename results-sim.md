## Integrated simulation at scale

131,039-node city, 100,000 trips, 1500 ticks of 4s (~1.7 sim-hours), single-threaded harness on 4 cores.

| measure | value |
|---|---|
| wall time | 1,737 s (3.5x realtime) |
| arrivals | 99,976 / 100,000 |
| trips planned (incl. regenerations) | 100,001 |
| certified-exact fraction | 55.7 % |
| planning time | 740.4 s total, 7,404 µs/trip |
| movement | 12.30 ms/tick |
| live-metric partial customization | 2676.78 ms/refresh |
| Layer-4 wakes + re-pricing | 572.54 ms/refresh |
| re-prices / probes / regenerations | 901,669 / 1 / 1 |
| route switches (past hysteresis) | 1,004 |
| event wakes (metered) / region wakes / sweeper | 0 / 0 / 53,525 |
| soft-closed edges at end | 0 |
| unreachable-trip events | 0 |

