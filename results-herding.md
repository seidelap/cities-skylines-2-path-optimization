## Herding A/B (synchronized-demand stress test)

Three near-equivalent corridors, cohort departures. The vanilla baseline
(exact per-trip Dijkstra against a lagged shared snapshot, plans frozen,
wait-timer replans — plan §1.1) is bistable in the congestion-signal regime:
a fast (occupancy-proportional) signal produces the classic corridor
oscillation; a slow (queue-excess) signal produces absorbing single-corridor
gridlock. The rebuild is stable under BOTH regimes.

| signal regime | mode | oscillation (std of corridor share) | mean travel (ticks) |
|---|---|---|---|
| fast | vanilla | 0.3879 | 73.0 |
| fast | **rebuild** | **0.0934** | **69.1** |
| slow | vanilla | 0.0000 (gridlocked) | 181.8 |
| slow | **rebuild** | **0.0444** | **69.0** |

**Fast regime: oscillation amplitude 4.2× below vanilla** (§6 target ≥ 5×).
**Slow regime: vanilla collapses into gridlock (2.6× the rebuild's travel time); the rebuild stays near-stationary (0.0444).**

The damping comes from the §4 trio — logit noise over genuinely comparable
alternatives, switch hysteresis, staggered refresh — plus the typical-scenario
blend in the choice utility (§4 L1's rolling-average scenario axis) and
cross-scenario portfolio retention (§4.8). Tuning note: damping is
non-monotonic in the blend/noise parameters (0.6/0.10 measured best;
0.7/0.13 regresses), so these belong in the empirical outer loop of §4.8.
