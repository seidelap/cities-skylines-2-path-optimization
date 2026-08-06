# CS2 Modding Research Notes — Spatial Demand & Land Economy Mod

**Status:** Self-contained research handoff, August 2026. Everything below was gathered while surveying what it takes to implement the Spatial Demand & Land Economy design (`cs2-spatial-demand-economy.md`) as a Cities: Skylines II code mod. Written to be usable **from scratch in a fresh repository with no other context**. Facts are marked **[verified]** (checked against ECS dumps or shipped open-source mod source) or **[unverified]** (community knowledge / needs confirmation on a machine with the game installed).

---

## 1. CS2 mod fundamentals (how any code mod is built)

* Runtime: Unity 2022.3.7f1, .NET Standard 2.1 Mono. Simulation is Unity DOTS/ECS; heavy per-tick paths are Burst-compiled, most gameplay/economy systems are **managed** `GameSystemBase` subclasses. **[verified** via community docs + decompile-derived dumps**]**
* Entry point **[verified against shipped mods, e.g. krzychu124/Traffic]**:
  ```csharp
  public class Mod : Game.Modding.IMod
  {
      public void OnLoad(UpdateSystem updateSystem)
      {
          updateSystem.UpdateAt<MySystem>(SystemUpdatePhase.Modification5);
          // also available: UpdateBefore<A,B>(phase), UpdateAfter<A,B>(phase)
      }
      public void OnDispose() { }
  }
  ```
  Real `SystemUpdatePhase` names include: `Deserialize`, `Modification3/4/5`, `ModificationEnd`, `ToolUpdate`, `ApplyTool`, `Rendering`, `UIUpdate`.
* Getting at existing systems: `World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<T>()`.
* **Replacing a vanilla system** (the sanctioned pattern): `world.GetOrCreateSystemManaged<VanillaSystem>().Enabled = false;` then register your replacement via `UpdateSystem.UpdateAt<T>`. Harmony can patch **managed code only** — Burst-compiled jobs cannot be Harmony-patched. All economy/demand/land-value systems are managed, so both wholesale replacement and Harmony patching are available for this project; wholesale replacement is the cleaner pattern for full-system rewrites, Harmony for small seams (tooltips, UI systems). **[replacement pattern verified by shipped overhaul mods, §6 below]**
* **Empirical enumeration beats guessing**: rather than hardcoding vanilla system type names from decompile guesswork, enumerate `world.Systems` matching a name filter at first boot and log them. (The routing project did this for "Pathfind" systems; do the same for "Demand|LandValue|Rent|Upkeep|Trade".)

### 1.1 Build toolchain **[verified against krzychu124/Traffic's csproj]**

* Official modding toolchain is installed **from inside the game** (Options → Modding); it sets a user environment variable `CSII_TOOLPATH`.
* csproj imports `$(CSII_TOOLPATH)\Mod.props` and `$(CSII_TOOLPATH)\Mod.targets` — these resolve game + Unity assembly references (**no hand-rolled HintPaths** into `Cities2_Data\Managed`) and handle packaging/deployment to the local Mods folder. References look like:
  ```xml
  <Reference Include="Game" Private="false" />
  <Reference Include="Colossal.Core" Private="false" />
  <Reference Include="Colossal.Logging" Private="false" />
  <Reference Include="Colossal.Mathematics" Private="false" />
  <Reference Include="Unity.Entities" Private="false" />
  <Reference Include="Unity.Collections" Private="false" />
  <Reference Include="Unity.Mathematics" Private="false" />
  <Reference Include="Unity.Burst" Private="false" />
  <Reference Include="UnityEngine.CoreModule" Private="false" />
  ```
* Target `netstandard2.1`. A useful dual-build discipline (from the routing project, works well): default build defines `OUT_OF_GAME_BUILD` and stubs every game touchpoint behind `#if !OUT_OF_GAME_BUILD`, so the repo compiles and tests on any machine without the (non-redistributable) game assemblies; the in-game build (`-p:InGame=true`) flips the define on the machine that owns the game.
* Official project templates **[verified via CitiesSkylinesModding/StockModTemplatesDiffer]**:
  * C# code mod: `dotnet new csiimod`
  * UI mod: `npm x create-csii-ui-mod` (generates `mod.json`, `src/`, `types/` game typings, `tsconfig.json`, `webpack.config.js`, `package.json`)
* Distribution: Paradox Mods (PDX Mods) via the toolchain's publish flow (`PublishConfiguration.xml`); local `Mods/` folder for development. Thunderstore/BepInEx was the pre-official-toolchain era — avoid for new work.
* Maintenance/version discipline: pin the game version during development; per game patch, decompile, diff the components/systems you touch, repair, re-run out-of-game tests, unpin. Concentrate all game-assembly contact in one or two adapter files so patch churn hits adapters, never algorithms.

### 1.2 Debugging and dev loop

* UI live reload: launch the game with `--uiDeveloperMode`; debug UI JavaScript with Chromium DevTools at `http://localhost:9444` (Chrome recommended, Firefox flaky). **[verified via toverux/HallOfFame dev docs]**
* C# debugging: documented at the CS2 wiki (`cs2.paradoxwikis.com/Debugging`). No C# hot reload — system iteration means a game restart; UI iteration is cheap.
* In-game Developer Mode exists (`--developerMode`, wiki page "Developer mode") with useful runtime inspectors.
* Scene/ECS inspection at runtime: krzychu124/SceneExplorer (mod that browses entity component data live).
* Working without a Windows PC: a GCP Windows + NVIDIA workstation streamed to a Mac works well as the build+test machine (Terraform recipe existed in the routing repo: idle shutdown, session cost caps, hibernation). Provision **Node.js** on that box for the UI toolchain.

---

## 2. Ground-truth data sources (fetch these first in the new repo)

* **Component dump** (928 components, field names + types):
  `https://raw.githubusercontent.com/captain-of-coit/cs2-ecs-explorer/master/data/Components.json`
* **Systems dump** (693 systems, each with `componentTypes` it reads and `uses_system`/`used_in_system` dependency edges):
  `https://raw.githubusercontent.com/captain-of-coit/cs2-ecs-explorer/master/data/Systems.json`
  (note: branch is `master`, not `main`; interactive explorer at `https://captain-of-coit.github.io/cs2-ecs-explorer/`)
* Query snippet:
  ```bash
  python3 -c "
  import json,sys
  d=json.load(open('Components.json'))
  k=sys.argv[1]
  e=d.get(k) or next((v for n,v in d.items() if n.endswith('.'+k)), None)
  print(k+':' if e else k+' NOT FOUND')
  [print('  ',p['name'],':',p['type']) for p in (e or {}).get('properties',[])]
  " Game.Net.LandValue
  ```
* **Vintage caveats [important]:** the dumps are a community snapshot that **predates Economy 2.0** (game patch 1.1.5f1, June 2024 — removed the "virtual landlord", moved rent into upkeep/condition, rebalanced land value). They also **under-cover the `Game.UI` namespace** (almost no `Game.UI.InGame.*` systems appear, though decompiles show many, e.g. demand/budget UI systems). Treat as a strong prior; the final arbiter is a decompile of the current game version. Everything below marked [verified-dump] came from these files.

---

## 3. Verified component fields (economy-relevant, from the component dump)

All [verified-dump]. Format: `Component { field : type, ... }`. Components listed with `{}` have no fields (tags).

### Land, rent, property, condition

```
Game.Net.LandValue            { m_LandValue: float, m_Weight: float }     // lives on NET entities (road edges) — vanilla's spatial land-value field
Game.Buildings.BuildingCondition { m_Condition: int }
Game.Buildings.PropertyRenter { m_Property: Entity, m_Rent: int, m_MaxRent: int }
Game.Buildings.Property       {}                                          // tag
Game.Buildings.PropertyOnMarket { m_AskingRent: int }
Game.Buildings.PropertyToBeOnMarket {}
Game.Buildings.ResidentialProperty / CommercialProperty / IndustrialProperty / OfficeProperty / ExtractorProperty / StorageProperty {}  // tags
Game.Buildings.RentersUpdated {}
Game.Buildings.Abandoned      { m_AbandonmentTime: uint }
Game.Buildings.Condemned      {}
Game.Agents.PropertySeeker    { m_CurrentProperty: Entity, m_BestProperty: Entity, m_BestPropertyScore: float, m_PropertiesEvaluated: byte }
Game.Agents.PropertySeekerCooldown {}
Game.Agents.TaxPayer          { m_UntaxedIncome: int, m_AverageTaxRate: int }
Game.Simulation.Landlord      {}          // tag; Economy-2.0-removed virtual landlord's residue — check current usage by decompile
Game.Simulation.Loan          { m_Amount: int, m_LastModified: uint }
Game.Simulation.Creditworthiness { m_Amount: int }
```

### Buildings, zoning, parcels

```
Game.Prefabs.SpawnableBuildingData { m_ZonePrefab: Entity, m_Level: byte }   // the discrete 1–5 level lives on the PREFAB — level change = prefab swap
Game.Prefabs.BuildingData          { m_LotSize: int2, m_Flags: BuildingFlags }
Game.Prefabs.BuildingPropertyData  { m_ResidentialProperties: int, m_AllowedSold: Resource, m_AllowedManufactured: Resource, m_AllowedStored: Resource, m_SpaceMultiplier: float }
Game.Prefabs.ZoneData              { m_ZoneType: ZoneType, m_AreaType: AreaType, m_ZoneFlags: ZoneFlags, m_MinOddHeight/m_MinEvenHeight/m_MaxHeight: ushort }
Game.Prefabs.ZonePropertiesData    { m_ScaleResidentials: bool, m_ResidentialProperties: float, m_SpaceMultiplier: float, m_AllowedSold/Manufactured/Stored: Resource, m_FireHazardModifier: float }
Game.Prefabs.ZonePreferenceData, ZonePollutionData, ZoneServiceConsumptionData, ZoneBuiltRequirementData, ZoneBlockData, NetZoneData   // exist, fields not dumped here
Game.Zones.Block         { m_Position: float3, m_Direction: float2, m_Size: int2 }   // the zoning-grid parcel substrate
Game.Zones.BuildOrder    { m_Order: uint }
Game.Zones.ValidArea     { m_Area: int4 }
Game.Zones.CurvePosition {}
Game.Objects.UnderConstruction     // exists (used by BuildingConstructionSystem) — vanilla models construction lag
Game.Tools.Temp          { m_Original: Entity, m_CurvePosition: float, m_Cost: int, m_Flags: TempFlags }
```

### Households, citizens, workers

```
Game.Citizens.Household        { m_Flags: HouseholdFlags, m_Resources: int, m_LastConsumption: short }   // m_Resources = money
Game.Citizens.Citizen          { m_PseudoRandom: ushort, m_State: CitizenFlags, m_WellBeing: byte, m_Health: byte, m_LeisureCounter: byte, m_PenaltyCounter: byte, m_BirthDay: short }
Game.Citizens.Worker           { m_Workplace: Entity, m_LastCommuteTime: float, m_Level: byte, m_Shift: Workshift }   // realized commute telemetry!
Game.Citizens.HouseholdNeed    { m_Resource: Resource, m_Amount: int }
Game.Citizens.HomelessHousehold{ m_TempHome: Entity }
Game.Citizens.HouseholdMember, TouristHousehold, CommuterHousehold, Student, JobSeekerCooldown, SchoolSeeker, ResourceBought, ... (exist; tags/small)
```

### Companies, jobs

```
Game.Companies.CompanyData     { m_RandomSeed: Random, m_Brand: Entity }
Game.Companies.Profitability   { m_Profitability: byte }
Game.Companies.WorkProvider    { m_MaxWorkers: int, m_UneducatedCooldown: short, m_EducatedCooldown: short, ...notification entities..., m_EfficiencyCooldown: short }
Game.Companies.Employer        { m_Workers: int }
Game.Companies.FreeWorkplaces  { m_Uneducated/m_PoorlyEducated/m_Educated/m_WellEducated/m_HighlyEducated: byte }
Game.Companies.ResourceExporter{ m_Resource: Resource, m_Amount: int }
Game.Companies.ResourceBuyer   { m_Payer: Entity, m_Flags: SetupTargetFlags, m_ResourceNeeded: Resource, m_AmountNeeded: int, m_Location: float3 }
Game.Companies.ResourceSeller  {}
Game.Companies.OutsideTrader   {}
Game.Companies.StorageCompany, ProcessingCompany, ExtractorCompany, CommercialCompany, IndustrialCompany, ServiceAvailable, ServiceCompanyData, StorageLimitData, BuyingCompany, LodgingProvider, TransportCompany  // exist
Game.Buildings.ResourceConsumer{ m_ResourceAvailability: byte }
Game.Buildings.ResourceProducer{}
```

### Resources, trade, prices

```
Game.Prefabs.ResourceData      { m_Price: float, m_IsProduceable: bool, m_IsTradable: bool, m_IsMaterial: bool, m_IsLeisure: bool, m_Weight: float, m_WealthModifier: float, m_BaseConsumption: float, m_ChildWeight/m_TeenWeight/m_AdultWeight/m_ElderlyWeight: int, m_CarConsumption: int, m_RequireTemperature: bool, m_RequiredTemperature: float, m_RequireNaturalResource: bool }
Game.Economy.ResourceInfo      { m_Resource: Resource, m_Price: float, m_TradeDistance: float }
Game.Prefabs.OutsideTradeParameterData {
    m_ElectricityImportPrice/m_ElectricityExportPrice: float,
    m_WaterImportPrice/m_WaterExportPrice: float, m_WaterExportPollutionTolerance: float, m_SewageExportPrice: float,
    m_AirWeightMultiplier/m_RoadWeightMultiplier/m_TrainWeightMultiplier/m_ShipWeightMultiplier: float,
    m_AirDistanceMultiplier/m_RoadDistanceMultiplier/m_TrainDistanceMultiplier/m_ShipDistanceMultiplier: float }
    // ^ vanilla's ONLY per-mode trade differentiation: weight/distance multipliers. Seed data for calibrating t in p(Q) = a + t·(Q/ρ)^(1/d)
Game.Simulation.TradeNode      {}
Game.Prefabs.TaxableResourceData { m_TaxAreas: byte }
Game.Net.OutsideConnection / Game.Objects.OutsideConnection {}   // tags
Game.Prefabs.OutsideConnectionData  // exists
```

### Global parameter singletons (the vanilla knobs being replaced — and the feature-flag fallback contract)

```
Game.Prefabs.DemandParameterData {
    m_ForestryPrefab/m_OfficePrefab: Entity,
    m_MinimumHappiness: int, m_HappinessEffect: float, m_UnemploymentEffect: float, m_HomelessEffect: float,
    m_NeutralHappiness: int, m_NeutralUnemployment: float, m_NeutralHomelessness: float,
    m_FreeResidentialProportion/m_FreeCommercialProportion/m_FreeIndustrialProportion: float,
    m_CommercialStorageMinimum/m_CommercialStorageEffect/m_CommercialBaseDemand: float,
    m_IndustrialStorageMinimum/m_IndustrialStorageEffect/m_IndustrialBaseDemand: float,
    m_ExtractorBaseDemand: float, m_StorageDemandMultiplier: float,
    m_LowRentDefaultRent/m_ResidentialHighDefaultRent/m_ResidentialLowDefaultRent: int,
    m_CommuterWorkerRatioLimit: int, m_CommuterSlowSpawnFactor: int,
    m_CommuterOCSpawnParameters/m_TouristOCSpawnParameters/m_CitizenOCSpawnParameters: float4 }
    // ^ direct confirmation of the design doc's §1.1 claims: global demand = happiness/unemployment/homeless factors
    //   + free-space proportions + outside-connection spawn parameters.

Game.Prefabs.EconomyParameterData {
    m_CommercialDiscount: float, m_ExtractorCompanyExportMultiplier: float,
    m_Wage0..m_Wage4: int,                      // wages by education level
    m_CommuterWageMultiplier: float, m_CompanyBankruptcyLimit: int,
    m_ResidentialMinimumEarnings: int, m_UnemploymentBenefit: int, m_Pension: int, m_FamilyAllowance: int,
    m_ResourceConsumption: float, m_TouristConsumptionMultiplier: float,
    m_WealthModifierConvenienceFood/Paper/Vehicles/Meals: float,
    m_WorkDayStart/m_WorkDayEnd: float,
    m_RentReturnUneducated..m_RentReturnHighlyEducated: int,    // note: "rent return" by education — Economy-era rent plumbing
    m_IndustrialEfficiency/m_CommercialEfficiency/m_ExtractorEfficiency: int,
    m_IndustrialProfitFactor: float, m_TrafficReduction: float }
    // ^ pension/unemployment/family-allowance = the "national counterparty" transfer taps in the design's open-economy boundary.

Game.Prefabs.TaxParameterData {
    m_TotalTaxLimits, m_ResidentialTaxLimits, m_CommercialTaxLimits, m_IndustrialTaxLimits,
    m_OfficeTaxLimits, m_JobLevelTaxLimits, m_ResourceTaxLimits : int2 }   // slider bounds (~ -10%..30%)
```

### Infoview / infomode prefab family (overlay system is data-driven)

```
Game.Prefabs.InfoviewData             { m_NotificationMask: uint }
Game.Prefabs.InfomodeActive           { m_Priority: int, m_Index: int }
Game.Prefabs.InfomodeData / InfomodeGroup {}
Game.Prefabs.InfoviewHeatmapData      { m_Type: HeatmapData }               // typed ENUM — the key constraint
Game.Prefabs.InfoviewBuildingData     { m_Type: BuildingType }
Game.Prefabs.InfoviewBuildingStatusData { m_Type: BuildingStatusType, m_Range: Bounds1 }
Game.Prefabs.InfoviewObjectStatusData { m_Type: ObjectStatusType, m_Range: Bounds1 }
Game.Prefabs.InfoviewNetStatusData    { m_Type: NetStatusType, m_Range: Bounds1, m_Tiling: float }
Game.Prefabs.InfoviewNetGeometryData  { m_Type: NetType }
Game.Prefabs.InfoviewCoverageData     { m_Service: CoverageService, m_Range: Bounds1 }
Game.Prefabs.InfoviewAvailabilityData { m_AreaType: AreaType, m_Office: bool }
Game.Prefabs.InfoviewLocalEffectData  { m_Type: LocalModifierType, m_Color: float4 }
Game.Prefabs.InfoviewMarkerData / InfoviewRouteData / InfoviewTransportStopData / InfoviewVehicleData  // typed similarly
```

Reading: infoviews are prefabs with infomode children; each infomode's *renderer semantics* come from engine enum types (`HeatmapData`, `BuildingStatusType`, …). A mod can inject new Infoview prefabs (via `PrefabSystem.AddPrefab` — see PrefabSystem wiki page), but **novel scalar fields have no enum slot**, so truly custom overlays either reuse an existing enum's renderer (fragile across patches) or draw themselves (see §5).

---

## 4. Verified system catalog (from the systems dump)

All names [verified-dump] unless noted. Namespace `Game.Simulation` unless prefixed.

### Demand & migration
`ResidentialDemandSystem`, `CommercialDemandSystem`, `IndustrialDemandSystem` — the three global-scalar demand systems.
`HouseholdSpawnSystem`, `HouseholdMoveAwaySystem`, `CommuterSpawnSystem`, `TouristSpawnSystem`, `HouseholdBehaviorSystem`, `TouristHouseholdBehaviorSystem`, `CitizenBehaviorSystem`.

### Allocation / property market
`HouseholdFindPropertySystem`, `CommercialFindPropertySystem`, `IndustrialFindPropertySystem`, `PropertyRenterSystem`, `PropertyRenterRemoveSystem`, `RentAdjustSystem`, `RentInitializeSystem`, `CitizenFindJobSystem`, `HomelessShelterAISystem`, `AttractionSystem`, `Game.Serialization.RenterSystem`.

### Land value, condition, leveling, construction
`LandValueSystem`, `BuildingUpkeepSystem`, `ZoneSpawnSystem`, `BuildingConstructionSystem`, `CondemnedBuildingSystem`, `DestroyAbandonedSystem`, `CollapsedBuildingSystem`, `BuildingEfficiencySystem`, `Game.Buildings.ZoneCheckSystem`, `Game.Prefabs.ZoneBuiltRequirementSystem`, `Game.Prefabs.ZoneSystem`, `Game.Zones.BlockSystem`, `Game.Zones.SearchSystem`.
Note: **no `LevelupSystem` exists in the dump** — post-Economy-2.0, leveling is believed to live inside `BuildingUpkeepSystem` (condition-driven) **[unverified — decompile]**.

### Taxes & budget
`TaxSystem`, `BudgetSystem`, `CityServiceBudgetSystem`, `BudgetApplySystem`, `CityServiceUpkeepSystem`, `LoanUpdateSystem`, `Game.Tools.LoanSystem`.

### Trade & companies
`TradeSystem`, `ResourceExporterSystem`, `ResourceBuyerSystem`, `ResourceProducerSystem`, `ResourceAvailabilitySystem`, `StorageCompanySystem`, `BuyingCompanySystem`, `ProcessingCompanySystem`, `ServiceCompanySystem`, `ExtractorCompanySystem`, `CompanyBankruptcySystem`, `CompanyDividendSystem`, `CompanyStatisticsSystem`, `OutsideConnectionDelaySystem`, `Game.Prefabs.ResourceSystem` (prefab price table), `Game.Net.OutsideConnectionSystem`.

### Read-only inputs (never replace)
`CitySystem`, `CityStatisticsSystem`, `CountEmploymentSystem`, `CountCompanyDataSystem`, `CountConsumptionSystem`, `CountPopulationSystem`, `CountFreeWorkplacesSystem`, `CountStudyPositionsSystem`, `CitizenHappinessSystem`, `HappinessAdjustSystem`, `GroundPollutionSystem`/`AirPollutionSystem`/`NoisePollutionSystem`, `TelecomCoverageSystem`, `MilestoneSystem`, `SimulationSystem` (frame/tick), `WealthStatisticsSystem`, `HouseholdStatisticsSystem`, `WorkProviderStatisticsSystem`.

### Rendering / overlay / infoview
`Game.Rendering.OverlayRenderSystem` — the custom in-world drawing entry point.
`Game.Rendering.OverlayInfomodeSystem` — vanilla's infomode→overlay bridge; **decompile this first** to learn how infoview colors are painted.
`Game.Rendering.AreaColorSystem`, `NetColorSystem`, `MeshColorSystem`, `BuildingLotRenderSystem`, `AreaRenderSystem`, `GuideLinesSystem`.
`Game.Prefabs.InfoviewInitializeSystem`.

### UI (namespace badly under-covered by the dump)
`Game.UI.UISystemBase`, `Game.UI.Tooltip.TooltipSystemBase`, `Game.UI.UIUpdateSystem`, `Game.UI.NameSystem`, `Game.UI.InGame.UIHighlightSystem`.
Known to exist from shipped mods but NOT in dump: `LandValueTooltipSystem` (replaced by LandValueOverhaul mod), the `Game.UI.InGame.*` demand/budget/economy panel UI systems. Enumerate empirically in-game.

### Serialization
`Game.Serialization.*` family (65 systems): includes `LoadGameSystem`, `BeginPrefabSerializationSystem`/`EndPrefabSerializationSystem`, per-feature systems (`CitizenSystem`, `RenterSystem`, `ConnectedBuildingSystem`, …), and `Game.Serialization.DataMigration.*` (e.g. `ResidentPseudoRandomSystem`) — **versioned save-migration precedent exists in the game itself**.

### Key dependency edges (from the dump — these confirm the design doc's background claims)

* `ZoneSpawnSystem` reads components incl. `Game.Net.LandValue`, `SpawnableBuildingData`, `ZoneData`, `ValidArea` and **uses** `ResidentialDemandSystem` + `CommercialDemandSystem` + `IndustrialDemandSystem` + `GroundPollutionSystem` + `ZoneSystem` → building spawn is gated by the three global demand scalars and reads land value. Site selection replacement point.
* `HouseholdSpawnSystem` uses `ResidentialDemandSystem` (+ `CitySystem`) and reads `OutsideConnectionData` → the hidden *household demand* spawning households at outside connections.
* `ResourceExporterSystem` reads `ResourceExporter`, `Game.Pathfind.PathInformation` and uses `PathfindSetupSystem` + `TaxSystem` → seller-delivered freight going through the pathfind queue (ties into the routing project).
* `LandValueSystem` reads `Game.Net.Curve`, `BuildingData`, `LandValue`, `PropertyRenter`, `Abandoned`, `ConsumptionData`, `BuildingPropertyData`, `Household`, `Game.Areas.Geometry` → land value lives on net edges and is influenced by building/renter state (the circularity the design severs).
* `RentAdjustSystem` reads `PropertyRenter`, `PropertyOnMarket`, `Household`, `Worker`, `WorkProvider`, service-company components; uses pollution systems ×3, `TelecomCoverageSystem`, `TaxSystem`, `CountEmploymentSystem`, `CitySystem` → the rent-setting locus, with amenity/pollution inputs.
* `PropertyRenterSystem` reads `PropertyRenter`, `BuildingCondition`, `SpawnableBuildingData`, `ZoneData`, `BuildingPropertyData`, `PropertyOnMarket`, `Abandoned`, `SignatureBuildingData`; uses `CityStatisticsSystem`, `IconCommandSystem`, `TriggerSystem`, `ZoneBuiltRequirementSystem`, electricity/water road-connection systems → rent collection + condition coupling.
* `BuildingUpkeepSystem` reads `BuildingCondition`, `ConsumptionData`, `SpawnableBuildingData`, `ZoneData`, `ResourceData`; uses `PropertyRenterSystem`, `ResourceSystem`, `ClimateSystem` → condition/upkeep locus (and post-2.0, presumably leveling).
* `HouseholdFindPropertySystem` reads `BuildingPropertyData`, `ParkData` (homeless in parks), `Household`, `Abandoned`, `CrimeProducer`, `Locked`, pollution/coverage/tax inputs; uses `PathfindSetupSystem` → housing search goes through the pathfind queue (a top query producer per the routing doc; replacing it with bucket search serves both projects).
* `ResidentialDemandSystem` reads `PropertyRenter`, `BuildingPropertyData`, `Household`, `Population`, `SpawnableBuildingData`, `ZonePropertiesData`; uses `TaxSystem`, `CountEmploymentSystem`, `CountStudyPositionsSystem`, `TriggerSystem`, `CitySystem`.
* `TaxSystem` reads `TaxPayer`, `IndustrialProcessData`, `Worker`, `ResourceData`; uses `CityStatisticsSystem`, `SimulationSystem`, `ResourceSystem`.
* `TradeSystem` reads `StorageLimitData`, `StorageCompanyData`, `OutsideConnectionData`, `ResourceData`, `GarbageFacilityData`; uses `CityStatisticsSystem`, `ResourceSystem`.
* `BuildingConstructionSystem` reads `UnderConstruction`, `Crane`(!), `SpawnableObjectData`; uses `ZoneSpawnSystem`, `TerrainSystem` → vanilla already models construction lag with visuals; keep it, feed it from the new site-selection logic.

---

## 5. UI modding — everything found

### 5.1 Architecture **[verified via official wiki search results + template repo + shipped mod source]**

* Game UI = **React + TypeScript + SCSS on Coherent Gameface (cohtml)** — an HTML/CSS/JS UI middleware. Webpack bundles mod UI. A **single shared React instance** is injected into all mods (no version mismatch, don't bundle your own React).
* UI mod entry: export a `ModRegistrar` (from npm package `cs2/modding`); it receives a `moduleRegistry`:
  * `registry.append('Game', MyComponent)` / `append('Menu', ...)` — mount new components into the in-game / menu screens.
  * `registry.find(stringOrRegexp)` — search vanilla modules by path/file/component name.
  * `registry.extend(...)` / `override(...)` — wrap or replace existing vanilla components (how you graft onto demand bars, budget panel, tooltips without forking the UI).
  * The `types/` folder in the template carries game typings; UrbanDevKit alternatively provides them as a package dependency.
* C# → UI data flow **[verified concretely in Infixo/CS2-InfoLoom `Systems/ResidentialDemandUISystem.cs`]**:
  ```csharp
  public class MyUISystem : Game.UI.UISystemBase   // UISystemBase verified in systems dump
  {
      protected override void OnCreate()
      {
          base.OnCreate();
          AddBinding(m_uiResults = new Colossal.UI.Binding.RawValueBinding(
              "cityInfo",            // binding group
              "ilResidential",       // binding key
              (IJsonWriter w) => { w.ArrayBegin(n); ... w.Write(v); ... }));
      }
  }
  ```
  Binding family in `Colossal.UI.Binding`: `ValueBinding<T>`, `GetterValueBinding<T>`, `TriggerBinding` (UI→C# calls), `RawValueBinding` (hand-written JSON via `IJsonWriter`); `AddUpdateBinding` for per-frame refresh. TS side consumes via `bindValue` / `useValue` / `trigger` from the `cs2/api` package, keyed by (group, key).
* Localization: register locale sources with the game's localization manager; shipped mods carry `l10n/` folders.
* Options UI: subclass `Game.Settings.ModSetting` — attribute-driven settings screens (checkboxes, dropdowns, buttons, sliders). This is where per-tier feature flags and per-district policy toggles belong. **[verified via River-Mochi/CS2-Templates showing a working Options UI sample]**
* InfoLoom's UI history: earlier versions used the community **Gooee** framework (Cities2Modding/Gooee); Gooee is the pre-official-toolchain era — use the official module system for new work.

### 5.2 Overlay/visualization routes for the design's map overlays (ranked by risk)

1. **Pure-UI panels + selected-object detail** (lowest risk, fully verified pattern): new windows/panels via `registry.append`; per-parcel detail by extending the selected-info panel; tooltips via a `Game.UI.Tooltip.TooltipSystemBase` subclass. InfoLoom proves the entire demand-panels workload. LandValueOverhaul proves tooltip-system replacement (`LandValueTooltipSystem`).
2. **`Game.Rendering.OverlayRenderSystem` custom drawing** (system exists [verified-dump]; in-world overlay drawing is established practice — Traffic mod tool overlays, algernon/ImageOverlay): draw per-parcel colored quads over `Game.Zones.Block` geometry (`m_Position`, `m_Direction`, `m_Size`), color-ramped by the active scalar (expected rent, time-to-fill, ℓ* gap, escrow fill, net fiscal yield). Pair with a UI toggle row from route 1. Decompile `Game.Rendering.OverlayInfomodeSystem` first — it is vanilla's own infomode→overlay painter and the best template.
3. **Native infoview injection** (stretch, [unverified]): inject an Infoview prefab (+ infomode children) via `PrefabSystem.AddPrefab` (wiki page "PrefabSystem" exists). Constraint from §3: infomode renderer semantics are engine **enums** — no slot for novel scalars; either repurpose an enum whose renderer fits or stay on route 2. Verify feasibility in-game before promising the native infoview menu.

### 5.3 Specific view workloads → mechanisms

| Design view | Mechanism |
|---|---|
| Demand bars as residual aggregates | Replacement demand systems publish the same UI bindings the vanilla demand UI reads (**binding names unverified** — decompile `Game.UI.InGame.*`); fallback: own bar cluster via `registry.extend` on the toolbar |
| Expected rent / time-to-fill per zone type; parcel ℓ*, redevelopment pressure, escrow fill; net fiscal yield | Route 2 parcel painting + route 1 legend/toggles; escrow as world-space progress bars only at close zoom |
| Rent decomposition tooltip (S / tax / wedge) | `TooltipSystemBase` subclass; LandValueOverhaul precedent |
| Local price vs parity band per resource; marginal offers | New panel (route 1); no vanilla equivalent |
| LR revenue in budget panel | Own fiscal panel first; graft into vanilla budget panel via `registry.extend` after decompiling its bindings |

---

## 6. Precedent mods (what each one proves) — all open source

| Mod | URL | What it proves / provides |
|---|---|---|
| **Traffic** (krzychu124) | github.com/krzychu124/Traffic | Toolchain csproj pattern (CSII_TOOLPATH, Mod.props/targets); IMod entry shape; in-world tool overlay drawing; the routing project's verified reference |
| **LandValueOverhaul** (Jimmyokok) | github.com/Jimmyokok/LandValueOverhaul | **Replaces `BuildingUpkeepSystem`, `LandValueSystem`, `PropertyRenterSystem`, `RentAdjustSystem`, `LandValueTooltipSystem` in the live game** — the exact Tier C/C′ seam, proven post-Economy-2.0; also documents land-value propagation params (spread distance 2000→200, speed 0.01→0.001) |
| **RentControl** (Jimmyokok) | github.com/Jimmyokok/RentControl | Further rent-system modification precedent |
| **RealEco** (Infixo) | github.com/Infixo/CS2-RealEco | Replaces `HouseholdBehaviorSystem`, `ResourceBuyerSystem`, `CommercialDemandSystem`; deep economy-parameter modding (prefab configs) |
| **InfoLoom** (Infixo) | github.com/Infixo/CS2-InfoLoom | Economy/demand UI panels; `UISystemBase` + `RawValueBinding("cityInfo", ...)` verified in source; reads demand systems' internals; files: `Systems/ResidentialDemandUISystem.cs`, `CommercialDemandUISystem.cs`, `IndustrialDemandUISystem.cs`, `BuildingDemandUISystem.cs`, `PopulationStructureUISystem.cs`, `Workforce/WorkplacesInfoLoomUISystem.cs` |
| **LandValueTuning / LandValueRemake** (Noel-leoN) | github.com/Noel-leoN/LandValueTuning | More `LandValueSystem` modification precedent |
| **CSL2DemandControl** (johnytoxic) | github.com/johnytoxic/CSL2DemandControl | Demand-system override (infinite demand) precedent |
| **HallOfFame** (toverux) | github.com/toverux/HallOfFame | Modern full-stack mod (C# + UI); documents `--uiDeveloperMode` + `localhost:9444` dev loop |
| **ImageOverlay** (algernon) | github.com/algernon-A/ImageOverlay | Custom in-world overlay rendering precedent (image plane over map) |
| **PlopTheGrowables** (algernon) | github.com/algernon-A/PlopTheGrowables | Growable-building lifecycle interference (level locking, despawn prevention, historical flag) |
| **SceneExplorer** (krzychu124) | github.com/krzychu124/SceneExplorer | Live ECS inspection in-game — invaluable during adapter work |
| **UrbanDevKit** (CitiesSkylinesModding) | github.com/CitiesSkylinesModding/UrbanDevKit | .NET + TS utility libs: cross-assembly shared state, coroutine runner, game typings as package |
| **Gooee** (Cities2Modding) | github.com/Cities2Modding/Gooee | Legacy community UI framework — historical reference only |
| **StockModTemplatesDiffer** (CitiesSkylinesModding) | github.com/CitiesSkylinesModding/StockModTemplatesDiffer | The official templates' exact contents, tracked across toolchain versions (`dotnet` and `ui` dirs) |
| **CS2-Templates** (River-Mochi) | github.com/River-Mochi/CS2-Templates | VS2022 starter with working Options UI (ModSetting) sample |

---

## 7. Save/load — findings and the open question

* CS2 saves serialize ECS state through **`Colossal.Serialization.Entities`** (namespace confirmed by the `Game.Serialization.*` system family and by crash-log signatures like "Serialize→SerializerSystem"). `Game.Serialization.DataMigration.*` shows the game versions its own save schemas.
* **Community pattern [unverified — the #1 spike for the new repo]:** custom `IComponentData` structs implementing the serialization interface (`ISerializable` with `Serialize<TWriter>` / `Deserialize<TReader>`) round-trip through saves automatically with their entity. Write a throwaway spike mod first: attach one serializable per-entity component + one singleton blob, save, reload, verify — and check what happens when the save is loaded *without* the mod (expected: unknown component data skipped; verify no corruption).
* Architecture regardless of the spike's outcome:
  * Keep **vanilla components authoritative** for anything vanilla systems also touch — write results into `Game.Net.LandValue`, `BuildingCondition`, `PropertyRenter.m_Rent`, level-via-prefab-swap — so a save opened without the mod is coherent, and feature-flags-off degrades cleanly.
  * Mod-native state inventory (from the design): per-parcel escrow balance + target configuration; per-household assessment-anniversary phase (anti-synchronization); owner tag + moving-cost draw + tenure clock; tenant-protection phase-in state; per-(resource×exit) sustained-Q EMA + transient deviation; 3 citywide migration scalars per segment (reservation threshold, prominence, network memory); claims ledger / construction-pipeline commitments (reconcile against `UnderConstruction` entities on load); calibration correction factors (safe to reset, better to keep).
  * Schema-version every mod-native blob from day one (the game's own DataMigration precedent).
  * Fallback if in-save persistence fails: versioned singleton blob, or a sidecar file keyed to the save (worse for cloud saves/sharing — last resort).

---

## 8. Gotchas & environment notes

* `cs2.paradoxwikis.com` (the official modding wiki) was **unreachable from the sandboxed research environment** (proxy CONNECT 403) — the wiki content above came via search-result summaries. From a normal machine, read directly: `/UI_Modding`, `/Creating_UI_And_Code_Mods`, `/Modding_Toolchain`, `/ECS_-_Entity_Component_System`, `/PrefabSystem`, `/Debugging`, `/Developer_mode`, `/Community-Made_Guides`.
* GitHub raw fetches (`raw.githubusercontent.com`) work fine for pulling the ECS dumps and mod sources.
* `cs2-ecs-explorer` data lives on branch **`master`** (`main` 404s).
* The components dump already caught one real bug in the routing project (a field assumed on `Game.Prefabs.CarLaneData` didn't exist) — always check the dump before writing code against a component, then let the in-game compile be the final arbiter.
* Useful routing-side verified facts that the economy mod will also touch: `Game.Net.LaneFlow { float4 m_Duration; float4 m_Distance; }` is the game's own realized-travel measurement (live seconds = length / (Σdistance/Σduration)); `Game.Citizens.Worker.m_LastCommuteTime` is per-worker realized commute; `Game.Pathfind.PathInformation { m_Origin, m_Destination, m_Distance, m_Duration, m_TotalCost }` describes completed path queries. These are the telemetry hooks for access-cost calibration and congestion-inclusive parity bands.
* Node.js is required on the game/dev machine for the UI toolchain (`npm x create-csii-ui-mod`, webpack builds).

---

## 9. In-game verification checklist (ordered by architectural risk)

1. **Custom-component save round-trip spike** (§7) — before any tier writes persistent state; a negative result changes the persistence architecture.
2. **Current-version decompile diff**: does `BuildingUpkeepSystem` own leveling post-Economy-2.0? Where does `LandValueTooltipSystem` live? Enumerate `Game.UI.InGame.*` demand/budget/economy UI systems and their binding (group, key) names.
3. **Demand-bar binding contract**: can replacement demand systems feed the vanilla bars by publishing identical bindings, or ship a replacement bar cluster?
4. **Decompile `Game.Rendering.OverlayInfomodeSystem`** — how vanilla paints infomode colors; how much of overlay route 2 comes free.
5. **`PrefabSystem.AddPrefab` infoview injection** — route 3 feasibility.
6. **`Game.Net.LandValue` write semantics** with `LandValueSystem` disabled — write frequency, who else reads it, propagation behavior.
7. **Prefab-swap renovation**: change `SpawnableBuildingData` level with tenants in place — does vanilla level-up's swap path preserve renters when triggered externally?
8. **Post-2.0 price plumbing**: where companies read buy/sell prices (`ResourceSystem` prefab table vs `ResourceInfo` instances) — the Tier D injection point.
9. **Toolchain UI packaging end-to-end** on the dev box (template instantiation → webpack → Mods folder → binding visible in-game).

---

## 10. Source index

Official wiki (fetch from an unproxied machine): `cs2.paradoxwikis.com/UI_Modding` · `/Creating_UI_And_Code_Mods` · `/Modding_Toolchain` · `/Modding` · `/ECS_-_Entity_Component_System` · `/PrefabSystem` · `/Debugging` · `/Developer_mode`.
Data: github.com/captain-of-coit/cs2-ecs-explorer (`data/Components.json`, `data/Systems.json`, branch `master`; interactive: captain-of-coit.github.io/cs2-ecs-explorer).
Mods & templates: see the table in §6.
Community orgs: github.com/CitiesSkylinesModding (official-adjacent, Discord-maintained) · github.com/Cities2Modding (legacy era) · optimus-code/Cities2Modding (early info dump; BepInEx-era, mostly historical).
Guides: ps1ke.github.io/Cities-Skylines-2-Modding-Guide (beginner C# debugging setup).
