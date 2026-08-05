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

## 0. Prerequisite: GPU quota — DONE for `cs2path-lab`

**Already granted; kept here as the recipe for any new project.**

The blocker is *not* the per-SKU regional quotas. On a fresh project those are
already sufficient — measured on two separate projects:

| quota (us-central1) | fresh-project default | needed |
|---|---|---|
| `NVIDIA_L4_GPUS` | 1 | 1 |
| `NVIDIA_T4_VWS_GPUS` | 1 | 1 |
| `CPUS` | 200 | 8 |
| `SSD_TOTAL_GB` / `DISKS_TOTAL_GB` | 500 / 4096 | ~250 |

The one thing at zero is the **global** `GPUS_ALL_REGIONS`, which silently
vetoes every regional GPU quota. Raise that alone:

```bash
gcloud services enable compute.googleapis.com cloudquotas.googleapis.com --project PROJECT
gcloud alpha quotas preferences create \
  --service=compute.googleapis.com --project=PROJECT \
  --quota-id=GPUS-ALL-REGIONS-per-project --preferred-value=1 \
  --preference-id=cs2path-gpus-all-regions --email=YOU@example.com \
  --justification="Single-GPU Windows workstation, started on demand."

# confirm (this is the authoritative read; the preference object lags)
gcloud compute project-info describe --project PROJECT \
  --format=json | python3 -c "import json,sys;[print(q) for q in json.load(sys.stdin)['quotas'] if q['metric']=='GPUS_ALL_REGIONS']"
```

Both requests auto-approved in under two minutes, including on a project
created minutes earlier with no usage history — so this is a gate, not a wait.

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
