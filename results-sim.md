## Integrated simulation at scale

131,039-node city, 100,000 trips, 1500 ticks of 4s (~1.7 sim-hours), single-threaded harness on 4 cores.

| measure | value |
|---|---|
| wall time | 1,938 s (3.1x realtime) |
| arrivals | 99,975 / 100,000 |
| trips planned (incl. regenerations) | 100,000 |
| certified-exact fraction | 55.7 % |
| planning time | 723.8 s total, 7,238 µs/trip |
| movement | 11.84 ms/tick |
| live-metric partial customization | 3156.58 ms/refresh |
| Layer-4 wakes + re-pricing | 821.45 ms/refresh |
| re-prices / probes / regenerations | 1,250,600 / 0 / 0 |
| route switches (past hysteresis) | 1,631 |
| event wakes (metered) / region wakes / sweeper | 0 / 2,902 / 601 |
| soft-closed edges at end | 0 |
| unreachable-trip events | 0 |

