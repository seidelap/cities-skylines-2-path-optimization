## Integrated simulation at scale

131,039-node city, 100,000 trips, 1500 ticks of 4s (~1.7 sim-hours), single-threaded harness on 4 cores.

| measure | value |
|---|---|
| wall time | 1,880 s (3.2x realtime) |
| arrivals | 74,743 / 100,000 |
| trips planned (incl. regenerations) | 104,869 |
| certified-exact fraction | 77.4 % |
| planning time | 1483.5 s total, 14,147 µs/trip |
| movement | 13.62 ms/tick |
| live-metric partial customization | 587.14 ms/refresh |
| Layer-4 wakes + re-pricing | 660.47 ms/refresh |
| re-prices / probes / regenerations | 1,304,457 / 5,809 / 4,869 |
| route switches (past hysteresis) | 5,332 |
| event wakes (metered) / region wakes / sweeper | 200 / 72,528 / 602 |
| soft-closed edges at end | 24 |
| unreachable-trip events | 0 |

