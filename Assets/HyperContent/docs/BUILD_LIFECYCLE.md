# HyperContent Build Lifecycle

How HyperContent assets are built, packaged into APK, and prepared for runtime consumption.

For architecture overview, see [ARCHITECTURE.md](ARCHITECTURE.md).
For build pipeline technical decisions, see [BUILD_PIPELINE_DECISION.md](BUILD_PIPELINE_DECISION.md).
For runtime initialization after build, see [INITIALIZATION_FLOW.md](INITIALIZATION_FLOW.md).

---

## Scope

This document covers:
- Build phase: asset collection → bundle building → catalog generation → settings.json
- Player build phase: auto-copy to StreamingAssets
- Build output files and their purposes
- Remote catalog generation for hot-update
- Troubleshooting build issues

---

## Key Classes

| Class | File | Owner | Responsibility |
|-------|------|-------|---------------|
| `HyperContentBuilder` | `Editor/Build/HyperContentBuilder.cs` | Owner1 | Build orchestrator: coordinates grouping tool → build executor |
| `IBundleGroupingTool` | `Editor/Build/IBundleGroupingTool.cs` | Owner1 | Grouping tool interface (Phase 1) |
| `DefaultGroupingTool` | `Editor/Build/DefaultGroupingTool.cs` | Owner1 | Default grouping tool: collect → analyze → assign → BuildPlan |
| `IBuildExecutor` | `Editor/Build/IBuildExecutor.cs` | Owner1 | Build executor interface (Phase 2) |
| `DefaultBuildExecutor` | `Editor/Build/DefaultBuildExecutor.cs` | Owner1 | Default executor: validate → build → catalog → settings |
| `BuildToolFactory` | `Editor/Build/BuildToolFactory.cs` | Owner1 | Registry for grouping tools and build executors |
| `BuildPlan` | `Editor/Build/BuildPlan.cs` | Owner1 | Data transfer between Phase 1 (grouping) and Phase 2 (building) |
| `BuildContext` | `Editor/Build/BuildContext.cs` | Owner1 | Build session state, config, and error collection |
| `AssetCollector` | `Editor/Build/AssetCollector.cs` | Owner1 | Scan project for HyperContentAsset markers and Content dirs |
| `DependencyAnalyzer` | `Editor/Build/DependencyAnalyzer.cs` | Owner1 | Analyze asset dependencies, assign bundles via strategy |
| `IBundleGroupingStrategy` | `Editor/Build/IBundleGroupingStrategy.cs` | Owner1 | Bundle grouping strategy interface (used by DefaultGroupingTool) |
| `MarkerBasedGroupingStrategy` | `Editor/Build/MarkerBasedGroupingStrategy.cs` | Owner1 | Group by HyperContentAsset.bundleGroup field |
| `AddressableGroupingStrategy` | `Editor/Build/AddressableGroupingStrategy.cs` | Owner1 | Group by Addressable Group membership |
| `BundleGroupingStrategyFactory` | `Editor/Build/BundleGroupingStrategyFactory.cs` | Owner1 | Create grouping strategy by enum type |
| `CatalogGenerator` | `Editor/Build/CatalogGenerator.cs` | Owner1 | Generate catalog; owns `Serialize`/`Deserialize` API |
| `BuildValidator` | `Editor/Build/BuildValidator.cs` | Owner1 | GUID/Name uniqueness, hash collision, round-trip validation |
| `BuildReportGenerator` | `Editor/Build/BuildReportGenerator.cs` | Owner1 | Generate build report (console log, no file output) |
| `BuildReport` | `Editor/Build/BuildReport.cs` | Owner1 | Report data: bundle sizes, duplicate deps, aggregation |
| `BundleBuilder` | `Editor/Build/BundleBuilder.cs` | Owner1 | Standalone bundle builder (legacy, inlined in DefaultBuildExecutor) |
| `HyperContentPlayerBuildProcessor` | `Editor/HyperContentPlayerBuildProcessor.cs` | Owner1 | Copy catalog + settings to StreamingAssets during Player Build |

---

## 1. Overview

```
┌──────────────┐      ┌──────────────┐      ┌──────────────────────────────┐
│  BUILD PHASE │  →   │ PLAYER BUILD │  →   │       RUNTIME PHASE          │
│   (Editor)   │      │   (Editor)   │      │   (Device / Play Mode)       │
└──────────────┘      └──────────────┘      └──────────────────────────────┘

• Bundle Build         • Auto-copy to        • Load settings.json
• Catalog Generation     StreamingAssets      • Resolve catalog (local/remote)
• settings.json        • Package into APK     • Initialize LocalContentCatalog
• Remote catalog                              • Load bundles & assets
```

---

## 2. Build Phase

Triggered via menu: **HyperContent / Build**.

### Build Steps

The build uses a **two-layer architecture**: a **grouping tool** (`IBundleGroupingTool`) produces a `BuildPlan`, then a **build executor** (`IBuildExecutor`) consumes it to produce the final output.

```
HyperContentBuilder.Build(config)
  │
  ╔═ Phase 1: Grouping ════════════════════════════════════════════════════
  ║
  ║  1.1 Get Grouping Tool
  ║    └─ BuildToolFactory.GetGroupingTool(config.groupingToolId)
  ║         ├─ "default"      → DefaultGroupingTool
  ║         └─ "addressable"  → HyperContentAddressableGroupingTool
  ║
  ║  1.2 Validate Grouping Tool
  ║    └─ IBundleGroupingTool.Validate(config)
  ║
  ║  1.3 Generate BuildPlan
  ║    └─ IBundleGroupingTool.GeneratePlan(config) → BuildPlan
  ║
  ║         ┌─────────────────────────────────────────────────────┐
  ║         │  DefaultGroupingTool.GeneratePlan() internals:      │
  ║         │                                                     │
  ║         │  ├─ Collect Assets                                  │
  ║         │  │    └─ AssetCollector                              │
  ║         │  │         ├─ Scan HyperContentAsset markers        │
  ║         │  │         └─ Scan Content directories               │
  ║         │  │                                                   │
  ║         │  ├─ Analyze Dependencies                            │
  ║         │  │    └─ DependencyAnalyzer                          │
  ║         │  │         └─ Build asset-to-asset dependency graph  │
  ║         │  │                                                   │
  ║         │  ├─ Assign Bundles                                  │
  ║         │  │    └─ IBundleGroupingStrategy.AssignBundles()     │
  ║         │  │         ├─ MarkerBasedGroupingStrategy (default)  │
  ║         │  │         │    └─ Group by marker.bundleGroup       │
  ║         │  │         └─ AddressableGroupingStrategy            │
  ║         │  │              └─ Group by Addressable Group name   │
  ║         │  │                                                   │
  ║         │  └─ Build Bundle Dependencies                       │
  ║         │       └─ Derive bundle-to-bundle deps from asset    │
  ║         │          deps                                        │
  ║         └─────────────────────────────────────────────────────┘
  ║
  ╚════════════════════════════════════════════════════════════════════════
  │
  ╔═ Phase 2: Building ═══════════════════════════════════════════════════
  ║
  ║  2.1 Get Build Executor
  ║    └─ BuildToolFactory.GetBuildExecutor(config.buildExecutorId)
  ║         └─ "default" → DefaultBuildExecutor
  ║
  ║  2.2 Validate Executor
  ║    └─ IBuildExecutor.Validate(plan, config)
  ║
  ║  2.3 Execute Build
  ║    └─ IBuildExecutor.Execute(plan, config) → BuildResult
  ║
  ║         ┌─────────────────────────────────────────────────────┐
  ║         │  DefaultBuildExecutor.Execute() internals:           │
  ║         │                                                     │
  ║         │  ├─ Pre-build Validation                            │
  ║         │  │    └─ BuildValidator                              │
  ║         │  │         ├─ GUID uniqueness                        │
  ║         │  │         ├─ Name uniqueness                        │
  ║         │  │         ├─ nameHash collision                     │
  ║         │  │         └─ Invalid key detection                  │
  ║         │  │                                                   │
  ║         │  ├─ Build Bundles                                   │
  ║         │  │    └─ SBP ContentPipeline.BuildAssetBundles       │
  ║         │  │       → Assets/StreamingAssets/{Platform}/Bundles/│
  ║         │  │                                                   │
  ║         │  ├─ Generate Catalog                                │
  ║         │  │    └─ CatalogGenerator.GenerateCatalog()          │
  ║         │  │       → HyperContentBuild/{Platform}/hc/         │
  ║         │  │         HyperCatalog.bin (fixed name)             │
  ║         │  │                                                   │
  ║         │  ├─ Post-build Validation                           │
   ║         │  │    └─ BuildValidator                              │
   ║         │  │         ├─ Bundle file completeness               │
   ║         │  │         ├─ Catalog round-trip validation          │
   ║         │  │         └─ Bundle size report                     │
   ║         │  │                                                   │
   ║         │  ├─ Generate Report (if generateReport = true)      │
   ║         │  │    └─ BuildReportGenerator                        │
   ║         │  │       → Console log only (no file output)         │
   ║         │  │                                                   │
   ║         │  └─ Generate Settings & Remote Catalog              │
   ║         │       ├─ Generate settings.json                      │
   ║         │       │    → HyperContentBuild/{Platform}/hc/        │
   ║         │       └─ If buildRemoteCatalog = true:               │
   ║         │            ├─ HyperCatalog_{ver}.bin → ServerData/Production/{Platform}/ │
   ║         │            └─ HyperCatalog_{ver}.hash → ServerData/Production/{Platform}/ │
   ║         │            (settings.json: remote paths = filenames only, no hc/ on CDN) │
   ║         └─────────────────────────────────────────────────────┘
  ║
  ╚════════════════════════════════════════════════════════════════════════
  │
  └─ See CONVENTIONS.md §3 for complete path definitions
```

### SBP task list (`CreateBuildTaskListForUpdate`)

Full Build and Update Build both call `ContentPipeline.BuildAssetBundles` with the same custom task list from `DefaultBuildExecutor.CreateBuildTaskListForUpdate()` (`Assets/HyperContent/Editor/Build/DefaultBuildExecutor.cs`). It follows the Addressables-style SBP preset (player scripts → dependency → packing → writing) and adds the same class of **shared-bundle extraction** tasks Addressables uses:

| Order (within dependency / packing phases) | Task | Role |
|--------------------------------------------|------|------|
| After `CalculateAssetDependencyData` | `StripUnusedSpriteSources` | Drops unused sprite source data before packing. |
| | `CreateBuiltInBundle` | Emits shared **`unitybuiltinassets`** (logical name `DefaultBuildExecutor.BUILTIN_ASSETS_BUNDLE_NAME`; on-disk file ends with `.bundle`). Built-in shaders/meshes etc. are not duplicated into every content bundle. |
| | `CreateMonoScriptBundle` | Emits shared **`monoscripts`** (`MONOSCRIPTS_BUNDLE_NAME`); MonoScript stubs are pulled out of content bundles so cross-bundle `MonoScript` references resolve from one bundle. |
| After `GenerateBundlePacking` | `UpdateBundleObjectLayout` | Re-adjusts bundle layouts **after** MonoScripts/built-ins are moved, so later tasks see consistent packing. |

`HyperContentBundleBuildParameters` passes `BuildConfig.stripUnityVersionFromBundleHeaders` into SBP as `ContentBuildFlags.StripUnityVersion` when enabled (default off, aligned with Addressables). The toggle lives under **Advanced / Experimental** in the HyperContent build window and is saved in `ProjectSettings/HyperContentBuildConfig.json`.

### Asset-level dependency computation (after Build Bundles, before Generate Catalog)

Once SBP returns its `WriteData`, `DefaultBuildExecutor` derives the per-asset dependency bundle sets that feed `AssetRecordEntry.dependencyBundles` in the catalog (consumed at runtime under `DependencyLoadMode.AssetLevel`):

| Step | Method (`DefaultBuildExecutor`) | Role |
|------|----------------------------------|------|
| 1 | `RebuildBundleDependenciesFromSbpResults` | Rebuilds the legacy **bundle-level** transitive closure (`BundleRecordEntry` deps) from SBP results — also used by `BundleLevel` mode and as the diagnostic baseline. |
| 2 | `BuildAssetDependencyBundlesFromSbp` | Computes each asset's **own** real dependency bundles (ordered, **owning bundle LAST** to preserve the post-order invariant). Pipeline: one-hop entry bundles from `WriteData.AssetToFiles`, taken over the asset's **in-bundle entry frontier** (root + same-bundle sibling entries it references, from `IDependencyData.referencedObjects` via `BuildEntryReferenceGraph`/`ComputeInBundleEntryFrontier`) → add the SBP-invisible **SpriteAtlas** edge (`AddPackedSpriteAtlasBundles`, the only `AssetDatabase.GetDependencies` use) → transitively expand through `BundleDependencies` (recovers multi-hop chains like prefab→material→shader) → clamp to the owning bundle's bundle-level closure (`AssetLevel ⊆ BundleLevel`). See [CATALOG_SCHEMA.md §2.4.1 / §2.4.2](CATALOG_SCHEMA.md). |
| 3 | `ValidateAssetDepsSubsetOfBundleClosure` | **Diagnostic-only**: logs an Error if an asset's non-owning deps are not all inside its owning bundle's bundle-level closure. Does **not** block the build (the SpriteAtlas edge legitimately adds a bundle outside the pure-SBP closure). |

**Update Build** runs the same step 2/3 for changed/new assets; unchanged assets restore their dependency bundle list from `BuildManifest.CachedAssetState.dependencyBundleNames` — see [CONTENT_UPDATE_BUILD_FLOW.md](CONTENT_UPDATE_BUILD_FLOW.md). The chosen `DependencyLoadMode` is frozen into `settings.json` during *Generate Settings* and only changes on a full rebuild.

### Build Output

For complete path definitions, see **[CONVENTIONS.md §3 Build Output & Runtime Paths](CONVENTIONS.md)** (canonical source).

Summary: Bundles → `Assets/StreamingAssets/{Platform}/Bundles/`, Catalog + Settings → `HyperContentBuild/{Platform}/hc/`, Remote Catalog → resolved folder (default `ServerData/Production/{Platform}/`, optional). **Update Build** also copies new `*_update_*.bundle` files into that same remote folder when `buildRemoteCatalog` is enabled (see [CONTENT_UPDATE_BUILD_FLOW.md](CONTENT_UPDATE_BUILD_FLOW.md) Phase D).

---

## 3. Player Build Phase

Triggered automatically by Unity's Player Build (Build Settings → Build).

`HyperContentPlayerBuildProcessor` (callbackOrder=2) maps `HyperContentBuild/{Platform}/hc/` → `StreamingAssets/hc/`. Bundles are already in `StreamingAssets/` from the Build phase and need no mapping.

For full APK layout, see **[CONVENTIONS.md §3.2](CONVENTIONS.md)**.

**Key points**:
- **Bundles already in StreamingAssets** — no copy needed
- **Only catalog + settings are copied** by HyperContentPlayerBuildProcessor
- **Order matters** — Runs after Addressables (order=2) to avoid conflicts
- **Platform-aware** — Uses `EditorUserBuildSettings.activeBuildTarget` to select correct folder

---

## 4. File Naming Rules

For file naming and path rules, see **[CONVENTIONS.md §2 Naming Rules](CONVENTIONS.md)** and **[CONVENTIONS.md §3 Build Output & Runtime Paths](CONVENTIONS.md)**.

**Why no package hash?** The runtime only compares remote hash vs cached hash. The package catalog is a pure fallback for offline/first-launch.

---

## 5. Build Config (BuildContext)

Key configuration fields in `BuildConfig` (defined in `BuildContext.cs`):

**Basic Settings**:

| Field | Default | Description |
|-------|---------|-------------|
| `buildTarget` | `StandaloneWindows64` | Target platform for bundle build |
| `compressionType` | `Lz4` | Bundle compression: `None`, `Lz4`, `Lz4HC` |
| `includeDependencies` | `true` | Include asset dependencies in bundles |
| `forceRebuild` | `false` | Force rebuild all bundles (clear old output first) |
| `generateReport` | `true` | Generate build report (console log) |

**Tool Selection**:

| Field | Default | Description |
|-------|---------|-------------|
| `groupingToolId` | `"default"` | Grouping tool ID (`"default"` or `"addressable"`) |
| `buildExecutorId` | `"default"` | Build executor ID (`"default"`) |
| `groupingStrategy` | `MarkerBased` | Strategy for DefaultGroupingTool: `MarkerBased` or `Addressable` |
| `editorPlayMode` | `UseAssetDatabase` | Editor play mode: `UseAssetDatabase` or `UseExistingAssetBundle` |

**Output & Remote Catalog**:

| Field | Default | Description |
|-------|---------|-------------|
| `buildOutputRoot` | `"HyperContentBuild"` | Build output root directory (project-level, not inside Assets) |
| `buildRemoteCatalog` | `false` | Whether to generate versioned remote catalog |
| `remoteCatalogBuildFolder` | `"ServerData"` | Output folder for remote catalog (for CDN upload) |
| `remoteCatalogLoadUrl` | `""` | Fallback CDN root if `remoteBundleLoadUrl` empty (no platform segment) |
| `remoteBundleLoadUrl` | `""` | Primary unified CDN root for settings.json `remoteBundleBaseUrl` |
| `overridePlayerVersion` | `""` | Override build version (empty = auto UTC timestamp `yyyy.MM.dd.HH.mm.ss`) |
| `catalogRequestTimeout` | `30` | Catalog download timeout in seconds |
| `stripUnityVersionFromBundleHeaders` | `false` | SBP `ContentBuildFlags.StripUnityVersion`; default off to match Addressables. Exposed in HyperContent window **Advanced / Experimental**. |

**Computed Properties** (read-only):

| Property | Value | Description |
|----------|-------|-------------|
| `BundleOutputDirectory` | `Assets/StreamingAssets/{Platform}/Bundles/` | Bundle output path |
| `CatalogOutputDirectory` | `{buildOutputRoot}/{Platform}/hc/` | Catalog + settings output path |
| `PlatformOutputDirectory` | `{buildOutputRoot}/{Platform}/` | General platform output root |
| `ResolvedBuildVersion` | `overridePlayerVersion` or UTC timestamp | Final build version string |

---

## 6. Build Validation

CatalogGenerator and BuildValidator perform these checks:

| Check | Phase | Description |
|-------|-------|-------------|
| GUID uniqueness | Pre & Post | Every asset GUID in catalog must be unique |
| Name uniqueness | Pre & Post | Every Name alias must be unique |
| nameHash collision | Pre & Post | SHA256 collision detection between different Names |
| Bundle completeness | Post | Every bundle referenced by assets must have a corresponding file |
| Round-trip validation | Post | Read catalog bytes → `CatalogGenerator.Deserialize` → `CatalogGenerator.Serialize` → byte-level comparison. Reports first differing byte position and context snippet on failure |

---

## 7. Troubleshooting

### Player Build: No HyperContent logs visible

Check Unity Console for:
```
[HyperContent] ========== PlayerBuildProcessor.PrepareForBuild START ==========
[HyperContent] BuildTarget: Android
[HyperContent] Expected BuildPath: HyperContentBuild/Android
[HyperContent] Directory exists: True/False
[HyperContent] ========== PlayerBuildProcessor.PrepareForBuild END ==========
```

If **no logs at all**: BuildPlayerProcessor may not be invoked (check asmdef setup).

### Common issues

| Symptom | Possible Cause | Action |
|---------|----------------|--------|
| No `[HyperContent]` logs during build | Build output dir missing | Run **HyperContent Build** first (Menu) |
| `Directory exists: False` | Platform mismatch | BuildTarget must match build output |
| `settings.json NOT FOUND` | Settings generation failed | Check DefaultBuildExecutor.GenerateSettingsAndRemoteCatalog logs |
| `HyperCatalog.bin NOT FOUND` | Catalog generation failed | Check CatalogGenerator / DefaultBuildExecutor logs |
| Files exist but runtime fails | Copy not executed | Verify AddAdditionalPathToStreamingAssets |

### Build order checklist

1. **HyperContent Build** (Menu: HyperContent/Build) → produces:
   - Bundles → `Assets/StreamingAssets/{Platform}/Bundles/`
   - Catalog + Settings → `HyperContentBuild/{Platform}/hc/`
2. **Player Build** (Build Settings → Build) → copies `HyperContentBuild/{Platform}/hc/` to `StreamingAssets/hc/` in APK (bundles already in StreamingAssets)

---

## Related Docs

- [ARCHITECTURE.md](ARCHITECTURE.md) — System design overview
- [BUILD_PIPELINE_DECISION.md](BUILD_PIPELINE_DECISION.md) — SBP vs traditional BuildPipeline decision
- [INITIALIZATION_FLOW.md](INITIALIZATION_FLOW.md) — How runtime consumes build output
- [CONTENT_UPDATE_FLOW.md](CONTENT_UPDATE_FLOW.md) — Remote catalog update mechanism
- [CATALOG_SCHEMA.md](CATALOG_SCHEMA.md) — Catalog data format
