## Integrated simulation at scale

131,039-node city, 100,000 trips, 1500 ticks of 4s (~1.7 sim-hours), single-threaded harness on 4 cores.

| measure | value |
|---|---|
| wall time | 993 s (6.0x realtime) |
| arrivals | 99,978 / 100,000 |
| trips planned (incl. regenerations) | 100,000 |
| certified-exact fraction | 52.5 % |
| planning time | 402.2 s total, 4,022 µs/trip |
| movement | 5.99 ms/tick |
| live-metric partial customization | 1247.90 ms/refresh |
| Layer-4 wakes + re-pricing | 679.27 ms/refresh |
| re-prices / probes / regenerations | 2,066,099 / 0 / 0 |
| route switches (past hysteresis) | 575 |
| event wakes (metered) / region wakes / sweeper | 0 / 0 / 4,478 |
| soft-closed edges at end | 0 |
| unreachable-trip events | 0 |

