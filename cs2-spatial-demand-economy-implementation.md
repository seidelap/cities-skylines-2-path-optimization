# CS2 Spatial Demand & Land Economy — Implementation Requirements

**Status:** Survey — companion to [`cs2-spatial-demand-economy.md`](cs2-spatial-demand-economy.md) (the design) and to the routing project this repository implements (see [`README.md`](README.md), [`cs2-trip-simulation-rebuild.md`](cs2-trip-simulation-rebuild.md) — "the routing doc").
**Purpose:** Answer *what has to be built and against which game surfaces* before any code exists: which vanilla systems get replaced, which components get read and written, what the new-view (UI/overlay) workload actually requires, what new save-state the design introduces, and how the project wires into the routing rebuild's core. Everything below is marked **verified** (checked against the vendored ECS dumps in [`reference/`](reference/README.md) or against shipped open-source mods) or **unverified** (needs the game machine — same discipline as the routing project's "Game-API status" section).

---

## 1. How this project differs from the routing rebuild, as a mod

The routing rebuild replaces one Burst-compiled hot path behind two adapters, with no player-facing UI beyond diagnostics. This project is the opposite shape:

* Every system it replaces is a **managed** `GameSystemBase` — demand, land value, rent, leveling, trade are all managed simulation systems (verified: all appear in `reference/cs2-ecs-systems.json`, none are Burst-only hot paths). The sanctioned wholesale-replacement pattern from the routing project (`World.GetOrCreateSystemManaged<T>().Enabled = false`, register replacements via `UpdateSystem.UpdateAt<T>`) carries over unchanged, and Harmony is *also* viable here for small seams (tooltip injection, UI-system patches) since nothing critical is Burst-compiled — matching design §1.3.
* It carries a **large UI surface** (design §4.7: overlays, tooltips, panels, demand-bar replacement) — a whole workload class the routing project never touched. Section 4 below is the survey of that surface.
* It introduces **mod-native persistent state** (escrow balances, migration scalars, trade EMAs, claims ledger) that must round-trip through save/load — a requirement the routing project deliberately avoided (via-node portfolios are reconstructible). Section 5.
* It **consumes** the routing rebuild's core rather than duplicating it (design §3, §5): CCH cluster access costs, nested-dissection cells, corridor dirty flags, Layer 3 buckets, travel telemetry. Section 6 defines the seam.

---

## 2. Vanilla systems to replace or mirror (verified names)

Ground truth: `reference/cs2-ecs-systems.json` — 693 systems with their component reads and system dependencies, vendored from the same community dump as the components file (see [`reference/README.md`](reference/README.md) for vintage caveats). The dependency edges in the dump independently confirm the design doc's §1.1 background: `ZoneSpawnSystem` reads the three demand systems **and** `Game.Net.LandValue`; `HouseholdSpawnSystem` reads `ResidentialDemandSystem` (the hidden household demand); `ResourceExporterSystem` routes through `PathfindSetupSystem` (seller-delivered freight).

| Design tier | Vanilla systems (all `Game.Simulation.*` unless noted) | Action |
|---|---|---|
| A — Migration | `HouseholdSpawnSystem`, `HouseholdMoveAwaySystem`, `CommuterSpawnSystem`, `TouristSpawnSystem` | Replace spawn/despawn rate logic with the attractiveness-vs-outside-utility flow; keep entity construction plumbing |
| B — Allocation | `HouseholdFindPropertySystem`, `CommercialFindPropertySystem`, `IndustrialFindPropertySystem`, `CitizenFindJobSystem`, `HomelessShelterAISystem`, `AttractionSystem` | Replace scoring/search with market-access bids + doubly-constrained balancing; the FindProperty family is also a top pathfind-queue producer (routing doc §1.2), so replacing it with bucket-based search serves both projects |
| B — Demand bars | `ResidentialDemandSystem`, `CommercialDemandSystem`, `IndustrialDemandSystem` | Replace outputs with residual-demand aggregates; keep the UI-facing contract (§4.2a) |
| C — Land accounting | `LandValueSystem`, `RentAdjustSystem`, `RentInitializeSystem`, `PropertyRenterSystem` (rent collection side), `TaxSystem`, `BudgetSystem`, `CityServiceBudgetSystem`, `BudgetApplySystem` | Replace with S + tax + wedge assessment, split-rate LVT, treasury-as-residual-claimant; budget systems gain the LR revenue line |
| C′ — Leveling/condition | `BuildingUpkeepSystem` (owns `BuildingCondition` and, post-Economy-2.0, level-up/down — **unverified**, confirm by decompile), `PropertyRenterSystem` (upkeep absorption), `CondemnedBuildingSystem`, `DestroyAbandonedSystem`, `Game.Buildings.ZoneCheckSystem` | Replace with escrow/renovation clock, ℓ* supported level, decay-as-underfunded-S |
| C′/§4.6 — Construction | `ZoneSpawnSystem` (site selection + spawn), `BuildingConstructionSystem` (build lag — vanilla already models `Game.Objects.UnderConstruction`; keep, feed milestone re-evaluation) | Replace uniform spawn with softmax-over-developer-returns; keep construction visuals |
| D — Trade | `TradeSystem`, `ResourceExporterSystem`, `ResourceBuyerSystem`, `StorageCompanySystem`, `BuyingCompanySystem`, `ResourceAvailabilitySystem`, `Game.Prefabs.ResourceSystem` (static prices → parity-band state) | Replace fixed prices with per-(resource × exit) supply/demand laws; company-side P&L systems (`ProcessingCompanySystem`, `ServiceCompanySystem`, `ExtractorCompanySystem`, `CompanyBankruptcySystem`, `CompanyDividendSystem`) read the new prices |
| Read-only inputs | `CitySystem`, `CityStatisticsSystem`, `CountEmploymentSystem`, `CountCompanyDataSystem`, `CountConsumptionSystem`, `CountPopulationSystem`, `GroundPollutionSystem`/`AirPollutionSystem`/`NoisePollutionSystem`, `CitizenHappinessSystem`, `MilestoneSystem` | Read, never replace |

**Precedent proving the seam (verified):** [LandValueOverhaul](https://github.com/Jimmyokok/LandValueOverhaul) ships a working overhaul of exactly `BuildingUpkeepSystem`, `LandValueSystem`, `PropertyRenterSystem`, `RentAdjustSystem`, and `LandValueTooltipSystem` — the core of Tier C/C′ — confirming these five are replaceable in the live game without save corruption. [RealEco](https://github.com/Infixo/CS2-RealEco) replaces `HouseholdBehaviorSystem`, `ResourceBuyerSystem`, and `CommercialDemandSystem` the same way.

**Dump-vintage caveat:** the systems dump predates Economy 2.0 (patch 1.1.5f1, June 2024, which removed the virtual landlord and moved rent into upkeep/condition). The five systems above are confirmed alive post-2.0 by LandValueOverhaul, but the internal division of labor (especially leveling inside `BuildingUpkeepSystem`, and `LandValueTooltipSystem` living under `Game.UI.Tooltip`, which the dump under-covers) must be re-verified by decompiling the current game version on the dev box — the routing project's per-patch decompile-diff-repair loop (routing doc §5) applies from day one here.

---

## 3. Component touchpoints (verified fields)

All checked against `reference/cs2-ecs-components.json` with the query snippet in `reference/README.md`. These are the design's read/write surfaces:

| Component | Fields (verified) | Role in the design |
|---|---|---|
| `Game.Net.LandValue` | `m_LandValue: float`, `m_Weight: float` | The vanilla land-value cell field — becomes the **write target** for assessed LR/P_L so every vanilla consumer (zone spawn, UI) sees our values; note it lives on **net entities** (road edges), which *is* vanilla's spatial discretization of land value |
| `Game.Buildings.BuildingCondition` | `m_Condition: int` | Reused as-is for condition/V; underfunded S decrements it (C′ downgrade path) |
| `Game.Buildings.PropertyRenter` | `m_Property: Entity`, `m_Rent: int`, `m_MaxRent: int` | Occupancy + payment; our S+tax+wedge decomposition must fold into `m_Rent` so vanilla consumers stay coherent; the decomposition itself is mod-side state (§5) |
| `Game.Buildings.PropertyOnMarket` | `m_AskingRent: int` | Vacancy + asking price; warehousing (C′ scrape gate) = withholding this component |
| `Game.Prefabs.SpawnableBuildingData` | `m_ZonePrefab: Entity`, `m_Level: byte` | The discrete 1–5 level (design §3 vanilla-interop constraint); renovation fires by swapping to the target-level prefab — same mechanism vanilla level-up uses |
| `Game.Prefabs.BuildingPropertyData` | `m_ResidentialProperties: int`, `m_AllowedSold/Manufactured/Stored: Resource`, `m_SpaceMultiplier: float` | The vanilla rent formula's terms (§1.1) — inputs to S and unit counts |
| `Game.Prefabs.ZoneData` / `ZonePropertiesData` | zone type, area type, height bounds; `m_ScaleResidentials`, `m_SpaceMultiplier` | Permitted-configuration set U in the LR(p) max (C) |
| `Game.Zones.Block` + `BuildOrder`, `ValidArea` | `m_Position`, `m_Direction`, `m_Size: int2` | The parcel/cell substrate for per-parcel state keying and overlay geometry |
| `Game.Citizens.Household` | `m_Flags`, `m_Resources: int`, `m_LastConsumption` | Income/consumption side of bids and the insolvency pipeline; `HomelessHousehold.m_TempHome` is the sheltered floor state |
| `Game.Citizens.Worker` | `m_Workplace`, `m_LastCommuteTime: float`, `m_Level` | Realized commute telemetry → residential access term (shared with routing Layer 4 telemetry) |
| `Game.Agents.TaxPayer` | `m_UntaxedIncome`, `m_AverageTaxRate` | Retained income-tax side (C) |
| `Game.Agents.PropertySeeker` | `m_BestProperty`, `m_BestPropertyScore`, `m_PropertiesEvaluated: byte` | The truncated-queue search the bucket-based allocation replaces (§4.2's stuck-homeless bug class) |
| `Game.Prefabs.ResourceData` | `m_Price: float`, `m_IsTradable`, `m_Weight` | The flat vanilla price — becomes the anchor `a` in p(Q); `m_Weight` feeds haul cost |
| `Game.Prefabs.OutsideTradeParameterData` | per-mode `*WeightMultiplier`, `*DistanceMultiplier`, utility import/export prices | Vanilla's only mode differentiation — replaced by the d-dimensional catchment laws, but these parameters seed t calibration |
| `Game.Companies.ResourceExporter` / `ResourceBuyer` | `m_Resource`, `m_Amount`; `m_ResourceNeeded`, `m_AmountNeeded`, `m_Location` | Quantized offers entering the Layer 3 offer-choice machinery (D) |
| `Game.Economy.ResourceInfo` | `m_Resource`, `m_Price`, `m_TradeDistance` | Per-instance price/trade-distance record — candidate carrier for local parity-band prices |
| `Game.Companies.Profitability`, `WorkProvider`, `FreeWorkplaces`, `Employer` | profitability byte; max workers; per-education free slots | Firm entry margin (A) and the jobs side of doubly-constrained balancing (B) |
| `Game.Simulation.Landlord`, `Loan`, `Creditworthiness` | (landlord is a tag; loan amount/timestamps) | The phantom-bank boundary objects (design §3); `Landlord` is the Economy-2.0-removed virtual landlord's residue — verify current usage by decompile |
| `Game.Prefabs.DemandParameterData`, `EconomyParameterData`, `TaxParameterData` | full global-scalar demand knobs, wages/pensions/rent-returns, tax slider limits | The vanilla parameter space being replaced — also the **feature-flag fallback contract**: flags off must restore behavior driven by exactly these |

---

## 4. New views — the UI surface (the big new requirement class)

### 4.1 How CS2 UI modding works (verified against toolchain templates and shipped mods)

* The game UI is **React + TypeScript on Coherent Gameface (cohtml)** — HTML/CSS/JS middleware, one shared React instance injected into all mods, webpack-bundled ([wiki: UI Modding](https://cs2.paradoxwikis.com/UI_Modding), [Modding Toolchain](https://cs2.paradoxwikis.com/Modding_Toolchain)).
* Two official templates (verified via [StockModTemplatesDiffer](https://github.com/CitiesSkylinesModding/StockModTemplatesDiffer)): `dotnet new csiimod` for the C# side, `npm x create-csii-ui-mod` for the UI side. The UI template ships `mod.json`, `src/`, `types/` (game typings), `tsconfig.json`, `webpack.config.js`.
* UI entry point: an exported `ModRegistrar` (from the `cs2/modding` package) receiving a `moduleRegistry`; `registry.append('Game' | 'Menu', Component)` mounts new components, `registry.find(...)`/`extend(...)`/`override(...)` locate and wrap existing vanilla components — this is how existing panels (demand bars, budget panel, tooltips) are extended without forking them.
* C# → UI data flow (verified concretely in [InfoLoom](https://github.com/Infixo/CS2-InfoLoom)'s `ResidentialDemandUISystem.cs`): a system extending **`Game.UI.UISystemBase`** registers bindings from **`Colossal.UI.Binding`** — e.g. `AddBinding(new RawValueBinding("cityInfo", "ilResidential", (IJsonWriter w) => { ... }))` — and the TS side subscribes by (group, key). The binding family (`ValueBinding<T>`, `GetterValueBinding<T>`, `TriggerBinding` for UI→C# calls, `RawValueBinding` for hand-written JSON) plus `AddUpdateBinding` for per-frame refresh is the entire seam; on the TS side `bindValue`/`trigger`/`useValue` from `cs2/api` consume it. `Game.UI.UISystemBase` and `Game.UI.Tooltip.TooltipSystemBase` are both in the systems dump.
* Dev loop: launch with `--uiDeveloperMode` for UI live-reload; JS debugging via Chromium devtools at `localhost:9444` (verified via [HallOfFame](https://github.com/toverux/HallOfFame)'s dev docs). C# hot reload does not exist — UI iteration is cheap, system iteration is a game restart.
* Localization: UI strings ship as locale sources registered with the game's localization manager (every shipped UI mod carries an `l10n/` or equivalent). Options screen: `Game.Settings.ModSetting` subclasses render attribute-driven settings UI — this is where the per-tier feature flags (design §3) and per-district policy toggles surface.
* Distribution: Paradox Mods via the toolchain's publish flow (`PublishConfiguration.xml`), local `Mods/` folder during development — identical to the routing mod's packaging (already documented in `CS2Path.Mod.csproj`).

### 4.2 Mapping design §4.7's overlays onto the available mechanisms

The design demands four distinct view workloads; they land on **three implementation routes** with different risk:

**(a) Demand bars as residual aggregates — extend/replace existing UI values.**
The vanilla demand bars are fed by UI systems reading the three demand systems. Since our replacements *are* those systems (same registered type or same published bindings), the cheapest correct move is to keep the vanilla binding contract: our `ResidentialDemandSystem` replacement exposes the same outputs the vanilla UI system reads, now computed as per-type residual aggregates. The exact binding names the vanilla demand UI reads are **unverified** — decompile `Game.UI.InGame.*` demand UI systems on the dev box (the dump under-covers `Game.UI`). Fallback that cannot fail: leave vanilla bars dead behind a flag and ship our own bar cluster via `registry.extend` on the toolbar component (InfoLoom demonstrates the full pattern of independent demand panels).

**(b) Map overlays — expected rent/sqft, time-to-fill, supported level ℓ*, redevelopment pressure, escrow fill, net fiscal yield.** Three routes, in recommended order:

1. **Pure-UI panels and per-selection detail first** (lowest risk, verified): panels via `moduleRegistry.append`, selected-parcel detail via extending the selected-info panel. This carries stage 1 of the build order (read-only shadow assessment, calibration against realized outcomes) with zero rendering work.
2. **`Game.Rendering.OverlayRenderSystem` custom drawing** (system verified in dump; in-world overlay drawing is established practice in shipped mods — Traffic's tool overlays, ImageOverlay): per-parcel colored quads over `Game.Zones.Block` geometry, color-ramped by the chosen scalar, plus escrow progress bars as world-space bars only when zoomed. This is the workhorse for the parcel-painted overlays and needs a UI toggle row (route 1) to switch scalars. `Game.Rendering.OverlayInfomodeSystem` (also in dump) is the vanilla bridge between infomodes and overlay rendering — decompile it first; it is the template for how vanilla paints infoview colors.
3. **Native infoview/infomode integration** (highest polish, **unverified**): infoviews are data-driven prefabs — the dump carries the full `Game.Prefabs.Infoview*Data` family (`InfoviewHeatmapData.m_Type: HeatmapData`, `InfoviewBuildingStatusData.m_Type: BuildingStatusType`, `InfomodeActive`, `InfoviewData`, `Game.Prefabs.InfoviewInitializeSystem`) — so adding an infoview means injecting a prefab via `PrefabSystem.AddPrefab` with infomode children. The constraint: infomode *types* are engine enums (`HeatmapData`, `BuildingStatusType`, …), so novel scalars must either reuse an existing enum slot whose renderer semantics fit (risky across patches) or fall back to route 2. Treat as stretch goal; verify feasibility on the dev box before promising the native infoview menu.

**(c) Price-decomposition tooltip (S / tax / wedge).** `Game.UI.Tooltip.TooltipSystemBase` is the verified base; LandValueOverhaul already replaces `LandValueTooltipSystem`, proving tooltip systems swap cleanly. Ship our tooltip system emitting the three-part decomposition plus per-resource parity-band position on trade buildings.

**(d) Budget panel LR revenue line + trade panel parity bands.** The budget UI's binding surface is **unverified** (dump gap); the safe sequencing is: land-tax revenue appears first in our own fiscal panel (route 1) with the citywide net-fiscal-yield comparator, and only then is grafted into the vanilla budget panel via `registry.extend` once the binding names are decompiled. Trade: per-resource local price vs [export parity, import parity] band and current marginal offers — a new panel, no vanilla equivalent exists.

### 4.3 Repo integration

The UI module lives inside the mod project (`src/CS2Econ.Mod/UI/` with the template's `mod.json`/`src`/`webpack.config.js`), built by npm and packaged by the toolchain's targets alongside the DLL — requiring **Node.js on the dev box**: extend `deploy/gcp/startup.ps1` provisioning accordingly. The out-of-game build (`OUT_OF_GAME_BUILD`) skips UI packaging entirely; UI code has no analogue in the harness, which asserts on the *numbers behind the bindings* instead (overlay-honesty targets, §6 of the design).

---

## 5. Save/load — the new state inventory

The routing mod persists nothing (portfolios are reconstructible). This design cannot avoid persistence; the inventory, from the design doc:

| State | Granularity | Notes |
|---|---|---|
| Upgrade escrow balance + target configuration | per parcel/building | The TIF bank (§4.3); reduces assessed conversion deduction, so it is *assessment-visible* state |
| Assessment anniversary phase | per household/unit | Anti-synchronization staggering (§3) — must survive load or reassessment synchronizes |
| Owner tag + moving-cost draw + tenure clock | per household | C′ tenure gating; the draw must persist or reload re-rolls redevelopment resistance |
| Tenant-protection phase-in state | per unit (districted) | Sitting-tenant discounted assessment vs market |
| Sustained trade position Q (EMA) + transient deviation | per (resource × exit) | Tier D price state; small and flat |
| Migration scalars: reservation threshold, prominence, network memory | 3 city-level scalars per segment | Tier A |
| Claims ledger + construction pipeline commitments | per active project | §4.6; on load, must reconcile against `UnderConstruction` entities actually present |
| Calibration correction factors (shrunk predicted-vs-realized ratios) | per submarket/cluster | §4.6 loop; safe to reset, better to keep |
| Condition, rent, level, land value | — | **Not mod state**: reuse `BuildingCondition`, `PropertyRenter.m_Rent`, `SpawnableBuildingData.m_Level` (via prefab swap), `Game.Net.LandValue` — vanilla components stay authoritative so a save opened *without* the mod is coherent (design §3 interop constraint) |

**Mechanism (partially verified):** CS2 saves serialize ECS state through `Colossal.Serialization.Entities` (namespace verified via the `Game.Serialization.*` system family in the dump — `SerializerSystem`, `LoadGameSystem`, per-feature serialization systems, and a `DataMigration` sub-namespace showing versioned migration precedent). The community pattern is custom `IComponentData` implementing the serialization interface (`ISerializable` with `Serialize<TWriter>`/`Deserialize<TReader>`) persisting automatically with the entity. **Rank this the #1 in-game verification item**: write a spike mod that attaches one serializable component + one singleton blob, save, reload, verify — *before* any tier is built, because the negative result changes the architecture (fallback: a versioned sidecar blob in a singleton entity, or worst case a companion file keyed to the save — precedent exists in shipped mods, but in-save is strictly better for cloud saves and save-sharing). Two invariants regardless of mechanism: (1) all mod state carries a schema version from day one (the game's own `DataMigration` precedent), and (2) the feature-flag-off path must leave vanilla components in a state vanilla systems can drive — that is what "every tier flags back independently" (§3) means for saves.

---

## 6. Shared infrastructure — the seam to the routing core

Design §5: this project is a second consumer of the routing core, not a second core. Concretely, against this repository's existing code:

| Routing-core asset (existing file) | Economy consumer |
|---|---|
| `CS2Path.Core/CchQuery.cs`, `ClusterCache.cs` — cached cluster-level CCH costs | All Tier B access terms w(p,q) = e^(−θ·c(p,q)); **no independent distance computation anywhere in CS2Econ** (design §3 constraint) |
| `CS2Path.Core/NestedDissection.cs` — dissection cells | Cluster keys for access matrices, submarkets, calibration granularity |
| `CS2Path.Core/UpdateEngine.cs` — corridor dirty flags, push channels | Refresh triggers for access-matrix rows and bucket-based capture (no per-tick recompute — §6 performance target) |
| `CS2Path.Core/Buckets.cs` — Layer 3 destination buckets | Phantom-entrant commercial capture (hypothetical bucket run), trade offer selection (D), re-housing search (B floor state) |
| Layer 4 realized-travel telemetry | Commute terms in residential access; congestion-inclusive parity-band widths |

Mechanically: new **ports on the economy side** (`IAccessProvider`, `ICellIndex`, `ICorridorEvents`, `IBucketProbe`) implemented by thin bridges over the routing core's public API — `CS2Econ.Core` references `CS2Path.Core` only through its `Ports.cs`-style interfaces, never game assemblies, keeping the two-thin-adapters discipline *and* letting the economy harness run against `SyntheticCity`/`ImportedCity` without the routing engine swapped in-game (access costs can come from the harness's reference Dijkstra at small scale).

Proposed layout, mirroring the existing structure:

```
src/CS2Econ.Core/      Pure economy core — no game refs (Tiers A–D, C, C′, §4.6, ports)
src/CS2Econ.Mod/       Game adapters + replacement systems + UI systems (bindings)
src/CS2Econ.Mod/UI/    React/TS UI module (create-csii-ui-mod layout)
src/CS2Econ.Harness/   Benchmark scenarios: boomtown ramp, demand collapse,
                       monoculture-export ramp, gentrification frontier (design §5),
                       with overlay predictions logged against realized outcomes
                       and the §3 anti-synchronization invariants asserted
```

---

## 7. Build order → concrete work items

Design §5's six stages, each with its game-surface prerequisite and its verification gate:

1. **Tier B read-only + overlays** — replace nothing; new `UISystemBase` panels + shadow access matrices over routing-core costs (or vanilla-derived proxies before the routing engine ships). *Gate:* overlay predictions logged against vanilla outcomes; UI toolchain proven (template instantiated, bindings live, `--uiDeveloperMode` loop working).
2. **Tier D trade scalars** — replace `TradeSystem`/`ResourceExporterSystem`/`ResourceBuyerSystem` pricing; parity-band panel. *Gate:* §6 monoculture target (≥30% marginal-price bend) in the harness export ramp.
3. **Tier C shadow accounting** — assessment computed and displayed (tooltip decomposition), τ_L levied at zero; serialization spike (§5 above) must land *before* this stage writes state. *Gate:* assessment stability, no vanilla divergence with flags off.
4. **Construction rewiring** — `ZoneSpawnSystem` softmax + claims ledger + milestone re-evaluation against live residuals. *Gate:* §6 vacancy-localization ratio (≥10:1).
5. **Tier C′ leveling/escrow** — `BuildingUpkeepSystem` replacement, prefab-swap renovation, warehousing. *Gate:* level-map/access rank correlation; no synchronized displacement cliffs.
6. **Tier A migration endogeneity** — spawn-system replacement last, everything before it running against vanilla migration. *Gate:* boom/bust asymmetry present; §6 dynamics targets.

Each stage keeps the game playable and independently feature-flags back to vanilla (`DemandParameterData`-driven behavior) — the flags are the same bisection-and-A/B instrument the routing mod ships.

---

## 8. In-game verification checklist (ordered by architectural risk)

1. **Custom-component save round-trip** (§5) — spike before Tier C writes state; a negative flips the persistence architecture.
2. **Post-Economy-2.0 system decompile diff** — confirm `BuildingUpkeepSystem` owns leveling, locate `LandValueTooltipSystem`, enumerate `Game.UI.InGame` demand/budget UI systems and their binding names (dump under-covers UI; enumerate empirically like the routing mod's `DumpPathfindSystems`).
3. **Demand-bar binding contract** — can replacement systems feed vanilla bars, or ship our own cluster (§4.2a fallback)?
4. **`OverlayInfomodeSystem` decompile** — the vanilla infomode→overlay bridge; determines how much of route 2 comes free.
5. **`PrefabSystem.AddPrefab` infoview injection** — route 3 feasibility (stretch).
6. **`Game.Net.LandValue` write semantics** — write-frequency/propagation interaction when `LandValueSystem` is disabled (its componentTypes list confirms net-edge granularity).
7. **Prefab-swap renovation** — level change with tenants in place: verify vanilla level-up's prefab swap path is callable with our trigger and preserves renters.
8. **`ResourceInfo`/price plumbing** — where company buy/sell decisions read prices post-2.0; confirms the Tier D injection point.
9. **Node on the dev box** — extend `deploy/gcp/startup.ps1`; verify toolchain UI packaging in the same session as the routing mod's export session (`deploy/gcp/SESSION-RUNBOOK.md`).

---

## Sources

Verified locally: [`reference/cs2-ecs-components.json`](reference/README.md) (928 components) and [`reference/cs2-ecs-systems.json`](reference/README.md) (693 systems), both from [captain-of-coit/cs2-ecs-explorer](https://github.com/captain-of-coit/cs2-ecs-explorer). Community sources: [UI Modding wiki](https://cs2.paradoxwikis.com/UI_Modding) · [Modding Toolchain wiki](https://cs2.paradoxwikis.com/Modding_Toolchain) · [Creating UI And Code Mods wiki](https://cs2.paradoxwikis.com/Creating_UI_And_Code_Mods) · [StockModTemplatesDiffer](https://github.com/CitiesSkylinesModding/StockModTemplatesDiffer) (official template contents) · [LandValueOverhaul](https://github.com/Jimmyokok/LandValueOverhaul) (Tier C/C′ system-replacement precedent) · [RealEco](https://github.com/Infixo/CS2-RealEco) (demand/economy replacement precedent) · [InfoLoom](https://github.com/Infixo/CS2-InfoLoom) (economy-panel UI precedent; `UISystemBase` + `RawValueBinding` verified in source) · [HallOfFame](https://github.com/toverux/HallOfFame) (`--uiDeveloperMode`, `localhost:9444` dev loop) · [UrbanDevKit](https://github.com/CitiesSkylinesModding/UrbanDevKit) (typings/shared-state utilities) · [Traffic](https://github.com/krzychu124/Traffic) (toolchain csproj + overlay-drawing precedent, already the routing mod's verified reference).
