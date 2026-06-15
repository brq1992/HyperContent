# HyperContent Owner Assignment

This document is the single source of truth for all Owner responsibilities in HyperContent.

## Overview

| Owner | Responsibility | Key Files |
|-------|----------------|-----------|
| **Owner0** | Core interfaces, data structures, specs | `Runtime/Core/*`, `Runtime/Data/*`, `Shared/*`, `Runtime/Catalog/CatalogSchema.cs`, `Runtime/Catalog/ICatalog.cs`, `Runtime/Catalog/ResourceLocation.cs` |
| **Owner1** | Build Pipeline & Catalog generation | `Editor/Build/*`, `Editor/*.cs` |
| **Owner2** | Runtime: facade, Operation DAG, Providers, resource loading | `Runtime/Operations/HyperContent.cs`, `Runtime/Operations/HyperContentImpl.cs`, `Runtime/Operations/*`, `Runtime/Providers/*`, `Runtime/Lifecycle/*` |
| **Owner3** | Content update, caching & transfer | `Runtime/Bundle/*`, `Runtime/Catalog/LocalContentCatalog.cs`, `Runtime/Catalog/CatalogLocator.cs`, `Runtime/Catalog/BundleDownloadManager.cs` |

---

## Owner0: Core Interfaces & Specifications

### Responsibilities

1. **Define & maintain core interfaces** (`Runtime/Core/*.cs`)
   - ICatalog, IContentProvider (Provider interface), IBundleStore, IBundleTransport, **IBundleDownloadQueue**, IBundleLoader
   - **IDownloadQueueProgressListener** + enqueue/progress types (`BundleDownloadEnqueueOptions`, `BundleDownloadPriority`, `DownloadQueueProgressSnapshot`) in `IBundleDownloadQueue.cs`
   - ContentHandle\<T\>, SceneInstance, VoidResult
   - ALL interface changes require Owner0 Review

2. **Define & maintain data structures** (`Runtime/Data/*.cs`, `Shared/*.cs`)
   - ResourceLocation, BundleInfo, BundleTagFlags (in `CatalogSchema.cs`), FetchResult, OperationStatus
   - Load network & pending scope: `LoadAssetNetworkMode`, `PendingBundleQueryScope`, `MissingBundlePromptInfo`, `LoadAvailabilityResult`, `LoadNetworkOptions` (`Runtime/Data/LoadNetworkTypes.cs`)
   - ALL data structure changes require Owner0 Review

3. **Maintain specification documents**
   - ARCHITECTURE.md, CONVENTIONS.md, CATALOG_SCHEMA.md, INITIALIZATION_FLOW.md, LOAD_RELEASE_FLOW.md, CONTENT_UPDATE_FLOW.md, PROVIDER_FLOW.md
   - CatalogSchema.cs (stringTable, nameAliases, guidSortedIndex)

4. **Define error codes & log fields** (`Shared/Constants.cs`)
   - ErrorCode, LogFields, NamingRules

5. **Define design principles** (5 core decisions in CONVENTIONS.md)
   - Handle-based release, Operation cache + RefCount, recursive dependency unload, Catalog address translation, Provider registration

### Code Review Gate

Any change to these areas **requires Owner0 approval**:

- **Design principles** (CONVENTIONS.md section 1)
- **Core interfaces** (`Runtime/Core/*.cs`) -- ICatalog, IContentProvider, IBundleStore, IBundleTransport, **IBundleDownloadQueue**, IBundleLoader, ContentHandle\<T\>
- **Catalog Schema** (`CatalogSchema.cs` and CATALOG_SCHEMA.md)
- **ResourceLocation** structure
- **Shared data structures** (`Runtime/Data/*.cs`, `Shared/*.cs`)
- **Error codes** (`Shared/Constants.cs` - ErrorCode)
- **Log fields** (`Shared/Constants.cs` - LogFields)
- **Operation state machine & RefCount rules** (CONVENTIONS.md section 6)

### HyperContent Static Facade (Key API defined by Owner0, implemented by Owner2)

- **File**: `Runtime/Operations/HyperContent.cs` (static class, sole public entry point)
- **Key methods**: `Initialize`, `InitializeAsync`, `LoadAsync<T>`, `InstantiateAsync`, `LoadSceneAsync`, `Release`, `ReleaseInstance`, `Shutdown`
- **Download queue (Phase 5):** facade `RegisterDownloadQueueProgressListener` / `UnregisterDownloadQueueProgressListener` (names per runtime plan) delegate to `IBundleDownloadQueue` — **specified by Owner0, implemented by Owner2** once an implementation is wired on `HyperContentImpl`
- **Handle**: `ContentHandle<T>` (`Runtime/Core/ContentHandle.cs`) -- unified generic handle for all operation types

### Owned Files

- `Runtime/Core/*.cs` -- All core interface definitions
- `Runtime/Data/*.cs` -- Shared data structures
- `Shared/*.cs` -- Shared constants and definitions
- `Runtime/Catalog/CatalogSchema.cs` -- Catalog Schema definition
- `Runtime/Catalog/ICatalog.cs` -- Catalog interface
- `Runtime/Catalog/ResourceLocation.cs` -- Location descriptor

### POC Verification

1. **Prepare**: Place test Bundle in `StreamingAssets/`; create Catalog JSON.
2. **Run**: Add HyperContent to Scene; use test script; check Console.

---

## Owner1: Build Pipeline

### Responsibilities

Build, validate, and output runtime-consumable bundles + catalog from Unity project assets.

### Key Files

| File | Purpose |
|------|---------|
| `Editor/Build/HyperContentBuilder.cs` | Build orchestrator and entry point |
| `Editor/Build/AssetCollector.cs` | Collect assets (markers + config filtering) |
| `Editor/Build/DependencyAnalyzer.cs` | Analyze asset-to-bundle dependencies |
| `Editor/Build/BundleBuilder.cs` | Bundle building implementation |
| `Editor/Build/CatalogGenerator.cs` | Catalog generation (catalogHash/contentHash) |
| `Editor/Build/BuildValidator.cs` | Validation (key uniqueness, GUID, nameHash collision) |
| `Editor/Build/DefaultBuildExecutor.cs` | Default build executor (Bundle + Catalog) |
| `Editor/Build/BuildContext.cs` | Build session state |
| `Editor/Build/BuildPlan.cs` | Grouping tool output |
| `Editor/Build/BuildReport.cs` / `BuildReportGenerator.cs` | Build report |
| `Editor/Build/IBuildExecutor.cs` / `IBundleGroupingTool.cs` | Plugin contracts |
| `Editor/Build/DefaultGroupingTool.cs` | Default grouping tool |
| `Editor/Build/BuildToolFactory.cs` / `BundleGroupingStrategyFactory.cs` | Strategy factories |
| `Editor/Build/MarkerBasedGroupingStrategy.cs` | Group by HyperContentAsset marker |
| `Editor/Build/AddressableGroupingStrategy.cs` | Group by Addressables group membership |
| `Editor/HyperContentAsset.cs` | Asset marker ScriptableObject |
| `Editor/AssetReferenceDrawer.cs` | PropertyDrawer for AssetReference / AssetReference\<T\> (Inspector ObjectField, GUID serialization) |
| `Editor/HyperContentBuildMenu.cs` | Menu items |
| `Editor/HyperContentBuildWindow.cs` | Build window |
| `Editor/Build/BuildManifest.cs` | Build Manifest data structure ([CONTENT_UPDATE_BUILD_FLOW.md](CONTENT_UPDATE_BUILD_FLOW.md)) |
| `Editor/Build/BuildManifestManager.cs` | Manifest save/load for Full / Update Build |
| `Editor/Build/ContentChangeDetector.cs` | Change detection: GUID-based diff + dependency expansion |
| `Editor/Build/IUpdateBundleGroupingStrategy.cs` | Interface for update-bundle grouping strategy |
| `Editor/Build/UpdateBundleAssigner.cs` | Resolve strategy + assign changed assets to update bundles |
| `Editor/Build/UpdateBuildExecutor.cs` | Update Build executor: full-context build + revert + mixed catalog |

### Build Pipeline Steps

**Full Build:**
1. **Collect assets** -- Scan markers/configs, filter assets
2. **Analyze dependencies** -- Asset dependencies, Bundle assignment
3. **Group bundles** -- Group assets via grouping strategy
4. **Validate** -- GUID uniqueness, Name uniqueness, nameHash collision, empty keys, missing assets
5. **Build Bundles** -- Full SBP `ContentPipeline.BuildAssetBundles` (custom task list)
6. **Extract SBP Dependencies** -- Read `IBundleBuildResults.BundleInfos` to overwrite `BundleDependencies` with object-level accurate data
7. **Generate Catalog** -- Output `HyperCatalog.bin` (fixed name) with ResourceLocation tree data (all `contentLocation = StreamingAssets`)
8. **Post-build validation** -- Verify bundle file existence
9. **Generate report** (optional) -- Bundle stats and dependency report
10. **Save Build Manifest** -- Record per-asset hash + bundle assignment for future Update Builds

**Update Build** (see [CONTENT_UPDATE_BUILD_FLOW.md](../docs/CONTENT_UPDATE_BUILD_FLOW.md) for Phase A–D details):
1. **Load Build Manifest** -- From original Full Build (immutable baseline, never regenerated)
2. **Detect changes (Phase A)** -- Compare current assets vs manifest by GUID; detect modified, new, removed; expand via dependency rules
3. **Assign update bundles (Phase B1)** -- Group dependency-expanded changed assets into update bundles via IUpdateBundleGroupingStrategy
4. **Full-context build + revert (Phase B2–B4)** -- Create update layout, build with `ContentPipeline.BuildAssetBundles` (full SBP, custom task list) using the **full current bundle graph** (so Unity correctly resolves dependency ownership), extract accurate bundle dependencies from `IBundleBuildResults`, then revert unchanged assets' catalog/bundle references back to original Full Build state; update bundles are deleted from StreamingAssets after copying to ServerData
5. **Generate mixed catalog (Phase C)** -- From original Full Build baseline: unchanged → StreamingAssets (3), changed/new → Remote (2); catalog rebuilt from scratch each Update Build (no patching previous catalog)
6. **Output (Phase D)** -- Copy new update bundles to the **remote catalog output folder** (same directory as `HyperCatalog_*.bin` / `.hash` when `buildRemoteCatalog` is enabled) or legacy `ServerData/{Platform}/Bundles/` when off; then write `settings.json` + remote catalog files; Build Manifest is NOT regenerated

### Naming Rules (from Shared/NamingRules)

- Asset Key: max 256 chars, allows `/`, disallows `\`
- Bundle Name: max 128 chars, no path separators
- Catalog file name is fixed: `HyperCatalog.bin` (see `HyperContentPaths.LOCAL_CATALOG_FILENAME`)

### Collaboration with Owner0

- **Schema**: Strictly follow Owner0's catalog schema. Any schema modification must be proposed to Owner0 first.
- **Naming & Error codes**: Follow `Shared/NamingRules` and `Constants`; do not modify independently.
- **ResourceLocation generation**: Build output must produce valid ResourceLocation trees (with ProviderId, Dependencies) that the Catalog Layer can deserialize.

---

## Owner2: Runtime Resource Management

### Responsibilities

Implement the Operation Layer and Provider Layer: DAG-based async loading, operation caching, ref-counted lifecycle, and pluggable IO providers.

### Key Files

**Operation Layer** [NEW]:

| File | Purpose |
|------|---------|
| `Runtime/Operations/AsyncOperationBase.cs` | Operation base: RefCount, status, DAG dependencies |
| `Runtime/Operations/AssetOperation.cs` | Typed asset operation |
| `Runtime/Operations/SceneOperation.cs` | Scene load operation |
| `Runtime/Operations/OperationCache.cs` | Global cache: GetOrCreate + recursive Release |
| `Runtime/Operations/ResourceManager.cs` | DAG scheduler: dependency tree construction and execution |
| `Runtime/Operations/OperationStatus.cs` | Status enum |

**Provider Layer** [NEW]:

| File | Purpose |
|------|---------|
| `Runtime/Core/IContentProvider.cs` | Provider interface: ProviderId, Provide, Release (Owner0 owned) |
| `Runtime/Providers/ProvideHandle.cs` | Bridge: Complete, Fail, UpdateProgress, GetDependencyResult |
| `Runtime/Providers/ProviderRegistry.cs` | Provider registration by ProviderId |
| `Runtime/Providers/BundleFileProvider.cs` | Load .bundle file from local storage |
| `Runtime/Providers/BundleAssetExtractor.cs` | Extract asset from loaded AssetBundle (zero IO) |
| `Runtime/Providers/RemoteBundleProvider.cs` | HTTP download + cache for remote bundles |
| `Runtime/Providers/LocalFileProvider.cs` | Editor mode direct load |
| `Runtime/Providers/SceneProvider.cs` | Async scene loading |

**Lifecycle** [NEW]:

| File | Purpose |
|------|---------|
| `Runtime/Lifecycle/InstanceRegistry.cs` | Track instance -> Operation mapping for ReleaseInstance |

**Asset Reference** (pure data, GUID key; load via HyperContent.LoadAsync(reference)):

| File | Purpose |
|------|---------|
| `Runtime/Operations/AssetReference.cs` | Non-generic serializable reference (GUID key) |
| `Runtime/Operations/AssetReference_T.cs` | Generic AssetReference\<T\> (typed) |

**Facade**:

| File | Purpose |
|------|---------|
| `Runtime/Operations/HyperContent.cs` | Static facade (sole public entry point, explicit init) |
| `Runtime/Operations/HyperContentImpl.cs` | Internal implementation (pure C#) |

### New Architecture Loading Flow

See [INITIALIZATION_FLOW.md](INITIALIZATION_FLOW.md), [LOAD_RELEASE_FLOW.md](LOAD_RELEASE_FLOW.md), [CONTENT_UPDATE_FLOW.md](CONTENT_UPDATE_FLOW.md), [PROVIDER_FLOW.md](PROVIDER_FLOW.md) for runtime flows (init, load/release DAG, content update, provider execution):
1. User calls `await HyperContent.InitializeAsync()` then `HyperContent.LoadAsync<T>(address)` or `HyperContent.LoadAsync(reference)` (AssetReference)
2. ICatalog resolves address -> ResourceLocation tree
3. ResourceManager builds Operation DAG from Location tree
4. OperationCache deduplicates Operations, manages RefCount
5. Providers execute actual IO in parallel
6. ContentHandle\<T\> receives result

### Collaboration

- **Implement**: `HyperContent` static facade, `HyperContentImpl`, all built-in Providers
- **Follow specs**: Use Owner0's interfaces, error codes, log fields, data structures
- **Interface changes**: Must submit to Owner0 for Review first
- **Owner3 dependency**: Use Owner3's bundle infrastructure (BundleProvider, LocalBundleStore, HttpBundleTransport) via Provider Layer adapters

### Warnings

- Interface changes require Owner0 Review
- Operation RefCount must be strictly maintained: increment on load, decrement on release, recursive unload at zero
- ContentHandle must be invalidated after Release to prevent double-release
- BundleAssetExtractor does zero file IO -- it only calls `LoadAssetAsync` on already-loaded AssetBundle objects

---

## Owner3: Content Update & Transfer

### Responsibilities

Implement content update, caching, and transfer so content can be "updated online, rollback-able, cacheable, and diagnosable".

**Runtime plan alignment** (HyperContent Runtime 实施规划 — 阶段 5–6 传输/队列/批下载章节；与 [ARCHITECTURE.md](ARCHITECTURE.md) §6.4、 [CONVENTIONS.md](CONVENTIONS.md) §1.5–1.6 同颗粒度):**

| Area | Owner3 scope |
|------|----------------|
| **Catalog locator** | `ResolveLocalCatalogAsync` (no HTTP), `CheckAndDownloadCatalogUpdateAsync` (catalog/hash **independent HTTP**, not the bundle queue). |
| **Global download queue** | `BundleDownloadQueue`: High / Normal / Low, merge by `RemoteRelativePath`, **only** caller of `IBundleTransport` for bundle bytes. |
| **Transport** | `HttpBundleTransport`: retries, concurrency, callback + awaitable `DownloadAsync` with **`CancellationToken`**; HTTP Range deferred ([TODO.md](TODO.md)). |
| **Batch downloads** | `BundleDownloadManager`: enqueue-only; **`CancellationToken`** on `DownloadAll*` / `DownloadBundlesAsync`; `DownloadResult.cancelled`. |
| **Operation-scoped progress (§5.2)** | **Full execution:** per-batch `pOnProgress` semantics, same-URL merge fan-out (`OnProgress` / `OnComplete` per waiter), per-waiter cancel without killing shared HTTP until last waiter leaves. Global `RegisterDownloadQueueProgressListener` is **facade-only (Owner2)**; queue **implementation** is Owner3. |
| **Per-load progress** | Optional future API (Owner0/Owner2); Owner3 extends queue when contract requires. |

### Key Files

`RuntimeSettings`, `HyperContentPaths`, and other `Runtime/Core/*` types are **Owner0** (see **File Boundary** below). Owner3 **consumes** them but does not own those files.

| File | Purpose |
|------|---------|
| `Runtime/Bundle/HttpBundleTransport.cs` | HTTP/HTTPS download (retry, concurrency, timeout) |
| `Runtime/Bundle/BundleProvider.cs` | Bundle provider facade (store/transport/loader orchestration) |
| `Runtime/Bundle/LocalBundleStore.cs` | Local bundle cache (atomic write, LRU prune, hash verify) |
| `Runtime/Bundle/UnityBundleLoader.cs` | Unity AssetBundle loading |
| `Runtime/Catalog/CatalogLocator.cs` | settings.json load; **local** catalog path resolve (`ResolveLocalCatalogAsync`); optional **remote hash/catalog to disk** (`CheckAndDownloadCatalogUpdateAsync`) |
| `Runtime/Catalog/LocalContentCatalog.cs` | Local catalog loading (JSON parsing, address query, HyperContentPaths.LoadText for Android) |
| `Runtime/Catalog/BundleDownloadManager.cs` | Bundle download: check pending, check by asset, selective download |

### Core Features

1. **Downloader** (HttpBundleTransport): HTTP/HTTPS, resume on disconnect (30s), auto retry (3x), concurrent downloads (4), progress reporting.
2. **Cache** (LocalBundleStore): Atomic writes, startup integrity verification, SHA256 hash verification, LRU prune when exceeding max size (default 1GB).
3. **Catalog paths** (CatalogLocator): **Init** uses `ResolveLocalCatalogAsync` only (cached vs package catalog on disk, no HTTP). Optional **disk hot-update** uses `CheckAndDownloadCatalogUpdateAsync` (hash + catalog via UnityWebRequest inside CatalogLocator), wrapped by facade `HyperContent.TryUpdateCachedCatalogOnDiskAsync` (**no prior init required**). **Recommended:** run disk hot-update **before** first `Initialize` so init loads the new cache without `ReloadRuntimeCatalogAsync`. **If** hot-update runs **after** init and applies a new file, call `HyperContent.ReloadRuntimeCatalogAsync`.

### Incremental Update Mechanism (settings.json + hash)

**Catalog — which file to load at init (disk only)** (`CatalogLocator.ResolveLocalCatalogAsync`):
1. Load `settings.json` from StreamingAssets/hc/ (or HyperContentBuild/{Platform}/ in Editor).
2. If a cached catalog file exists (`CacheCatalogPath` + `cachedCatalogPath`) → use it.
3. Else → package catalog at `CatalogBasePath` + `localCatalogPath`.

**Catalog — optional remote refresh to disk** (internal `CatalogLocator.CheckAndDownloadCatalogUpdateAsync`; facade `HyperContent.TryUpdateCachedCatalogOnDiskAsync`):
1. If `HasRemoteCatalog` is false → `SkippedNoRemote` outcome (facade: `CatalogDiskUpdateKind.SkippedNoRemote`, ErrorCode 1009) — no network.
2. Else: GET remote hash using `HyperContentPaths.CombineRemoteCdnRequestUrl(effectiveBase, remoteCatalogHashRelativePath)`; compare to cached hash on disk; on mismatch, GET catalog `.bin` and write atomically under cache. **Does not** load into memory. **If** `HyperContent` was already initialized with an older catalog, call `ReloadRuntimeCatalogAsync` after **`Applied`**; **if** init has not run yet, the next `Initialize` loads the updated cache (no reload).

**Bundle freshness check** (`BundleDownloadManager`):
1. For each remote bundle in catalog, get `BundleInfo.Hash` (contentHash)
2. If local store has no such bundle -> needs download
3. If local store has bundle -> call `IBundleStore.VerifyHash(bundleName, remoteContentHash)`, false -> needs download
4. VerifyHash true -> skip download

### Collaboration

- **Implement interfaces**: `IBundleStore`, `IBundleTransport`, `IBundleLoader`
- **Follow specs**: Use Owner0's Schema, error codes, log fields
- **Interface changes**: Must submit to Owner0 for Review
- **Owner2 integration**: Owner2's Providers (`BundleFileProvider`, `RemoteBundleProvider`) enqueue on `IBundleDownloadQueue` only; `HyperContent` facade exposes `BundleDownloadManager` entry points (`HasPendingDownloads`, `DownloadAllUpdatesAsync`, `CancellationToken` overloads, etc.) and registers global progress listeners on the active queue.

### Warnings

- HttpBundleTransport: uses UnityWebRequest — ensure all download paths are async
- CatalogLocator: fully async (Awaitable), supports CancellationToken for timeout/abort
- JSON parsing failures should be handled gracefully

---

## Collaboration Rules (All Owners)

### Interface Change Flow

```
Owner1/2/3 proposes change
       |
Owner0 Review (approve / reject / request changes)
       |
Owner0 updates specification
       |
Proposing Owner implements
```

### File Boundary

- **Owner0**: `Runtime/Core/*`, `Runtime/Data/*`, `Shared/*`, `Runtime/Catalog/CatalogSchema.cs`, `Runtime/Catalog/ICatalog.cs`, `Runtime/Catalog/ResourceLocation.cs`
- **Owner1**: `Editor/` (all editor code and build pipeline)
- **Owner2**: `Runtime/Operations/HyperContent.cs`, `Runtime/Operations/HyperContentImpl.cs`, `Runtime/Operations/*`, `Runtime/Providers/*`, `Runtime/Lifecycle/*`
- **Owner3**: `Runtime/Bundle/*`, `Runtime/Catalog/` (except CatalogSchema, ICatalog, ResourceLocation)

### Cross-Owner Rules

- Do not modify another Owner's files without coordination
- All interface/schema changes require Owner0 Review
- Follow error codes and log fields from `Shared/Constants.cs`
- New modules (Operation DAG, Provider Layer) should be developed incrementally alongside existing code