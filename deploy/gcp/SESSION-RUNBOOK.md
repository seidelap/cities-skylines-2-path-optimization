# First game session — runbook

One session with the exporter mod closes every assumption the harness cannot
test by itself. Run it before any engine-swap work: the numbers it produces
decide what to build next.

| # | question | closed by | decision it feeds |
|---|---|---|---|
| A2 | is real demand zonal enough for the §4.9 cache? | demand trace → `harness import` | hit ≥ ~60%: cache thesis holds. Diffuse: warm-plan path matters less, cold-plan cost dominates |
| A3 | is congestion change clustered or scattered? | traffic trace → `harness import` | clustered (share high, median ms low): §6 partial-customization target plausible. Scattered: needs the per-arc subscription tightening before the swap |
| scale | how big is a real late-game lane graph? | capture diagnostics line | sizes memory + build budgets; 200k vs 2M nodes changes Burst planning |
| Amdahl | what share of frame time is vanilla pathfinding? | profiler + the mod's system dump | the ceiling on the whole project — decides how hard to push the swap |

## 0. Prerequisite: GPU quota (start this first — longest lead time)

New GCP projects have zero GPU quota; the request can take days. Request BOTH
SKUs so the L4-licensing question (see README) cannot block you:

```bash
gcloud compute regions describe us-central1 \
  --format="table(quotas.metric,quotas.limit)" | grep -i gpu
```

Then in Console → IAM & Admin → Quotas, request:
- `NVIDIA_L4_GPUS` ≥ 1 (primary: g2-standard-8)
- `NVIDIA_T4_VWS_GPUS` ≥ 1 (fallback: n1 + T4-vWS, licensing known-good)

Provisioning itself: [`README.md`](README.md) (`cs2 up`, Steam, Parsec).

## 1. Build and install the mod (on the workstation)

```powershell
# CSII_TOOLPATH is set by startup.ps1 after the official toolchain install
cd C:\repo\cities-skylines-2-path-optimization
dotnet build src/CS2Path.Mod -c Release -p:InGame=true
# copy output to %LOCALAPPDATA%Low\Colossal Order\Cities Skylines II\Mods\CS2Path\
```

First launch, check `Player.log` for two lines:

- `[CS2Path] systems matching 'Pathfind'` — **save this list.** It is the
  empirical answer to which vanilla systems to profile now and disable at the
  engine swap (previously marked "still unverified" in `GameAdapters.cs`).
- No exceptions from `OnLoad` — if `IMod`/`UpdateSystem` shapes drifted in a
  patch, it fails here, loudly, before touching anything.

## 2. Capture a trace (any built-up city, ~20–30 min of play)

From the mod's console/keybind surface, in order:

| call | what it does | watch for |
|---|---|---|
| `CalibrateSpeedUnits()` | derives game-units→m/s from LaneFlow on free lanes | needs ≥25 free-flowing lanes; run after the city has traffic |
| `StartTrace(64)` | captures the graph, then samples every 64 frames | `capture:` line — **meanDegree ≥ ~2.5, no SUSPICIOUS flag**; node/edge counts answer the scale question |
| *play normally 20–30 min* | traffic deltas + one demand sample per commute accumulate | do NOT edit roads mid-trace (ids are pinned to the capture) |
| `StopTrace()` | ends sampling, prints totals | unresolved/out-of-range trip counts should be a small fraction of recorded |
| `ExportCity(path)` | writes the pinned capture + both traces | traffic and demand counts nonzero |

Known limits, by design: trips carry a constant documented α (A2 is pure OD
geometry; CS2 has no per-cim time/money/comfort triple to read), and lanes
built after `StartTrace` are invisible until the next capture.

## 3. Amdahl measurement (same session, 5 minutes)

1. Launch with `-developerMode` (or attach the Unity profiler).
2. Open the profiler's system/CPU view at normal game speed in the busiest
   part of the day cycle.
3. Sum the frame-time share of every system named in the mod's
   `systems matching 'Pathfind'` dump; note total frame ms alongside.
4. Record three samples: quiet early morning, rush hour, and paused-UI (as the
   zero baseline).

Rush-hour share is the number that matters — it bounds the whole project's
win. Write all three down; they go next to the import results.

## 4. Close the loop

```bash
cs2 pull            # or upload-export.ps1 → gsutil cp
dotnet run -c Release --project src/CS2Path.Harness -- import --file city.cs2city
```

`results-import.md` then contains, against the synthetic baselines: A1
(separator quality on the real lane graph — expected fine after the Inertial
Flow rewrite, but this is its first contact with a *lane*-level graph), A2
(cache hit rate on real demand), A3 (partial-customization ms + clustering on
real change sets). The pipeline including A3 is verified end-to-end today via
`export-synthetic --trace-ticks 12` + `import`, so a surprising number from the
game is a finding about the game, not a pipeline bug.
