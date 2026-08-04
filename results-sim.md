## Integrated simulation at scale

131,039-node city, 100,000 trips, 1500 ticks of 4s (~1.7 sim-hours), single-threaded harness on 4 cores.

| measure | value |
|---|---|
| wall time | 2,133 s (2.8x realtime) |
| arrivals | 91,731 / 100,000 |
| trips planned (incl. regenerations) | 108,235 |
| certified-exact fraction | 76.9 % |
| planning time | 1509.9 s total, 13,950 µs/trip |
| movement | 14.41 ms/tick |
| live-metric partial customization | 898.62 ms/refresh |
| Layer-4 wakes + re-pricing | 1097.75 ms/refresh |
| re-prices / probes / regenerations | 719,318 / 43,117 / 8,235 |
| route switches (past hysteresis) | 54,747 |
| event wakes (metered) / region wakes / sweeper | 0 / 1,116 / 2 |
| soft-closed edges at end | 0 |
| unreachable-trip events | 0 |

