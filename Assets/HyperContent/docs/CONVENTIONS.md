# HyperContent Conventions

Rules, codes, and constraints that all Owners must follow. Look up this document when unsure about naming, error handling, logging, or lifecycle rules.

For architecture overview, see [ARCHITECTURE.md](ARCHITECTURE.md).
For runtime data flows, see [INITIALIZATION_FLOW.md](INITIALIZATION_FLOW.md), [LOAD_RELEASE_FLOW.md](LOAD_RELEASE_FLOW.md), [CONTENT_UPDATE_FLOW.md](CONTENT_UPDATE_FLOW.md), [PROVIDER_FLOW.md](PROVIDER_FLOW.md).
For Catalog schema details, see [CATALOG_SCHEMA.md](CATALOG_SCHEMA.md).
For Owner responsibilities, see [OWNERS.md](OWNERS.md).
For directory layout, see [DIRECTORY_STRUCTURE.md](DIRECTORY_STRUCTURE.md).

---

## 1. Design Principles

Five core decisions that permeate the entire system. All code must conform to these constraints:

| # | Decision | Constraint |
|---|----------|------------|
| 1 | **User must hold a Handle to Release** | Prevents wild releases; Handle is the closed-loop credential for load and unload |
| 2 | **Same resource loaded twice hits Operation cache + ref count** | No duplicate IO; automatically shares underlying Bundle/Asset |
| 3 | **RefCount reaching zero recursively unloads dependencies** | Guarantees correct Bundle unload timing -- no leaks, no premature unloads |
| 4 | **Address translated to Location via Catalog** | Caller is fully decoupled from physical asset location; hot-update only replaces Catalog |
| 5 | **Providers are registered by ProviderId** | Supports local, remote, editor, custom load sources -- transparent to upper layers |

### 1.1 Backward compatibility (runtime evolution)

During the **HyperContent Runtime refactor** (per the active project runtime plan), the module **does not** guarantee backward compatibility: catalog/schema versions, `settings.json` shape, and public facade APIs may change in breaking ways **without** compatibility branches, shims, or migration guides unless the team explicitly requires them. After that window closes, resume the normal change-control expectations in [OWNERS.md](OWNERS.md) and module overview docs.

### 1.2 Global queue vs catalog/hash HTTP

- **`IBundleDownloadQueue`** (see `Runtime/Core/IBundleDownloadQueue.cs`) is the single scheduler for **bundle** bytes: priorities (**High** / **Normal** / **Low**), same-URL merge, and fan-out to `IBundleTransport`. **`BundleDownloadManager` and load-triggered remote bundle fetches must enqueue** — they must not call `IBundleTransport` directly once the queue implementation exists (Owner3 integration).
- **Global progress:** `IDownloadQueueProgressListener` + `DownloadQueueProgressSnapshot`; the static facade registers/unregisters listeners on the active queue (**Owner2** wires `HyperContent` → queue instance).
- **Catalog / hash HTTP (current implementation):** Remote hash and catalog `.bin` use **direct `UnityWebRequest` in `CatalogLocator`** (`CheckAndDownloadCatalogUpdateAsync` / `TryUpdateCachedCatalogOnDiskAsync`). They **do not** use `HttpBundleTransport` and **do not** enter **`IBundleDownloadQueue`**. See [ARCHITECTURE.md](ARCHITECTURE.md) §6.4.2 and [CONTENT_UPDATE_FLOW.md](CONTENT_UPDATE_FLOW.md) §2.2.
- **If** a future change routes catalog/hash through the **same** transport as bundles, those requests would then need the **global queue** (e.g. at **High** priority) for consistent progress and starvation avoidance — until then, the split path above is intentional.

### 1.3 Load network mode: `Silent` as semantic default

When `LoadNetworkOptions` (or equivalent) is **omitted**, the effective network mode is **`Silent`**: missing remote bundles are downloaded via the queue without a user prompt. This is a **semantic** default only — it does **not** depend on whether legacy overloads without options are kept or removed during the refactor.

- **`QueryOnly` on load APIs:** Setting `LoadNetworkOptions.Mode` to **`QueryOnly`** on `LoadAsync` / `InstantiateAsync` / `LoadSceneAsync` is **unsupported** (invalid handle + `SYSTEM_INVALID_STATE` log). Use **`HyperContent.QueryLoadAvailability<T>`** for probe-only semantics ([LOAD_RELEASE_FLOW.md](LOAD_RELEASE_FLOW.md)).

### 1.4 `ReloadRuntimeCatalog` timing

`ReloadRuntimeCatalogAsync` reloads the **in-memory** catalog from disk (see facade in `HyperContent.cs`). Use it when **`HyperContent` is already initialized** and **`TryUpdateCachedCatalogOnDiskAsync`** has written a **new** catalog (**`Applied`**) — so the next **bundle download** work for that segment sees the new mapping. If **`TryUpdateCachedCatalogOnDiskAsync`** completed **before** the first **`Initialize`**, **reload is not needed** for that cold start; init’s **`ResolveLocalCatalogAsync`** already picks up the cached file ([INITIALIZATION_FLOW.md](INITIALIZATION_FLOW.md) §2–§3, §8).

**No** framework-level cancellation of in-flight bundle transfers, **no** extra global locks, and **no** queue draining are required solely for reload — ordering is guaranteed by this call sequence at the application layer.

### 1.5 Download progress: global queue vs operation-scoped

Runtime plan §5.2 distinguishes two progress channels; they **coexist** and serve different UI needs:

| Channel | API / mechanism | Denominator (typical) | Use case |
|---------|-----------------|------------------------|----------|
| **Global queue** | `HyperContent.RegisterDownloadQueueProgressListener` → `IDownloadQueueProgressListener` → `DownloadQueueProgressSnapshot` | Queue-wide estimated bytes / logical task counts (implementation-defined; see `BundleDownloadQueue`) | Settings “total download”, global HUD |
| **Operation-scoped** | `DownloadAllUpdatesAsync` / `DownloadBundlesAsync` / `DownloadAllBlockingBundlesAsync` **`pOnProgress`** | Only bundles in **that** batch | Update screen progress bar |
| **Per-load (optional)** | *Not in public API yet* | Only bundles needed for **one** `LoadAsync` / scene / instantiate | “Loading this screen” 100% when **that** load’s deps are ready — **must not** be confused with global % |

**Rules:**
- Do **not** use global queue % as a substitute for “this `LoadAsync` is done” unless the product explicitly wants queue-wide semantics.
- **Same-URL merge:** one physical download may satisfy multiple waiters; each **scoped** consumer (batch callback or future per-load hook) should still observe **completion** for its own wait set (see runtime plan §5.2).
- **Owner3 (full execution):** `BundleDownloadQueue` + `BundleDownloadManager` implement merge fan-out, per-waiter `OnProgress` / `OnComplete`, and cancellation behaviour described in §1.6. **Global** listener wiring stays on **`HyperContent`** (Owner2 facade → `IBundleDownloadQueue`).
- **Per-load scoped progress (optional, not batch):** a future Owner0-approved API may add an optional progress callback on `LoadNetworkOptions` or on `ContentHandle<T>`; until then, use batch `pOnProgress` or global listener only.

### 1.6 Batch download `CancellationToken`

Aligned with runtime plan §6.A “批下载结果与取消”:

| Rule | Detail |
|------|--------|
| **API** | `HyperContent.DownloadAllUpdatesAsync`, `DownloadAllBlockingBundlesAsync`, `DownloadBundlesAsync` take optional **`CancellationToken`** (after progress/complete callbacks). |
| **Propagation** | `BundleDownloadManager` sets `BundleDownloadEnqueueOptions.CancellationToken` for each logical bundle in the batch. |
| **Queue** | `BundleDownloadQueue` removes a waiter on token fire: invokes that waiter’s `OnComplete` with **`ErrorCode.OPERATION_CANCELLED` (5009)**; if **other** waiters share the same merged URL, the physical download **continues**; when the **last** waiter for that URL is gone, **`IBundleTransport.CancelDownload`** runs. Pending dequeuing skips already-cancelled items without starting HTTP. |
| **Result** | `DownloadResult.cancelled` is true when the batch token is cancelled by completion time; `success` is false if `cancelled` or any item failed. **No rollback** of bundles already written before cancel (same partial semantics as mid-batch failure). |
| **Transport** | `IBundleTransport.DownloadAsync(..., CancellationToken)` on the callback overload links the token into `HttpBundleTransport`’s per-request CTS (`UnityWebRequest.Abort` on cancel). |

---

## 2. Assembly & Namespace Structure

Assemblies:
- `HyperContent.Shared` -- No dependencies
- `HyperContent.Runtime` -- Depends on Shared
- `HyperContent.Editor` -- Depends on Runtime + Shared + UnityEditor

Dependency direction: `Editor -> Runtime -> Shared`

Namespaces follow the project-wide `com.igg.<root_folder_name>` convention (see `.cursor/rules/code-style.mdc`):

| Namespace | Contents |
|-----------|----------|
| `com.igg.hypercontent` | Static facade `HyperContent`, `HyperContentImpl` (API layer, 2 files) |
| `com.igg.hypercontent.runtime` | All runtime implementation (flat, no sub-namespaces) |
| `com.igg.hypercontent.shared` | Error codes, log fields, naming rules |
| `com.igg.hypercontent.editor` | Build pipeline, editor tools |
| `com.igg.hypercontent.test` | Integration tests |

---

## 3. Naming Rules

### File Naming
- Catalog: `HyperCatalog.bin` (fixed name; see `HyperContentPaths.LOCAL_CATALOG_FILENAME`)
- Bundle: `{bundle_name}.bundle`
- Hash (optional): `{bundle_name}.hash`

**Catalog `bundleName` vs on-disk bundle files:** The catalog stores **extensionless** logical bundle names (`ResourceLocation.InternalId` for local bundles). On disk, shipped bundle files are **`{bundleName}.bundle`** (`NamingRules.BUNDLE_FILE_EXTENSION`). Runtime (`BundleFileProvider`, `LocalContentCatalog` local paths) appends `.bundle` when resolving filesystem paths. **Android:** keep that suffix so Gradle `aaptOptions.noCompress "bundle"` (or project equivalent) skips APK deflate and `AssetBundle.LoadFromFile*` stays mmap-friendly — do not ship extensionless bundle blobs for APK-local content.

### Identifier Limits
- Asset Key (Address): max 256 chars, allows `/`, disallows `\`
- Bundle Name: max 128 chars, no path separators

### Code Naming
- Interfaces: `I{Name}` (e.g. `ICatalog`, `IContentProvider`, `IBundleStore`)
- Implementations: descriptive name (e.g. `JsonCatalog`, `BundleFileProvider`)
- Enums: PascalCase (e.g. `OperationStatus`, `ContentLocation`)
- Constants: UPPER_SNAKE_CASE (e.g. `BUNDLE_NOT_FOUND`)

### Build Output Files
- Local catalog: `HyperCatalog.bin` (saved in `{buildOutputRoot}/{Platform}/hc/`)
- Build Manifest: `build_manifest.json` (saved in `{buildOutputRoot}/{Platform}/`)
- **Remote catalog folder** (when `buildRemoteCatalog` is enabled): resolved by `BuildConfig.GetResolvedRemoteCatalogBuildFolder(remoteCatalogBuildFolder, buildTarget)` — e.g. default `remoteCatalogBuildFolder = ServerData` → `{project}/ServerData/Production/{Platform}/`. Full Build and Update Build both write versioned `HyperCatalog_{buildVersion}.bin` + `.hash` here for CDN upload.
- **Update bundles** (Update Build Phase D): `{originalBundleName}_update_{version}.bundle`. When `buildRemoteCatalog` is **true**, copied to the **same folder** as the remote catalog files above (one directory with `.bin`/`.hash` for upload). When `buildRemoteCatalog` is **false**, legacy layout: `ServerData/{Platform}/Bundles/`.
- `settings.json` (when remote catalog is enabled): `remoteCatalogRelativePath` / `remoteCatalogHashRelativePath` are those **filenames only**, resolved as `{remoteBundleBaseUrl}/{platform}/{filename}` — **no** `hc/` prefix on the CDN (local catalog + settings still live under `{buildOutputRoot}/{Platform}/hc/` → `StreamingAssets/hc/` in the player)

### Build Manifest Rules
- Build Manifest is created **only during Full Build**; it is never regenerated during Update Builds.
- It must be preserved for all future Update Builds of the same release line. Losing it requires a new Full Build + new APK release.
- Every Update Build diffs against the **same original Full Build manifest**, not against the previous Update Build output.
- Each Update Build regenerates the catalog from scratch (Full Build baseline + current changes); it does not patch the previous update catalog.

### Update Bundle Version Uniqueness
- The `{version}` segment in update bundle names must be unique per Update Build (e.g. `ResolvedBuildVersion` or UTC timestamp).
- Example: `ui_common_update_2026.03.09.14.00.00.bundle`

---

## 4. Error Codes

Canonical definitions live in `Shared/Constants.cs` (`ErrorCode`). Use `HCLogger.LogError(code, message)` so logs stay aligned with this table. Typical mappings: **catalog disk hot-update** → 1006–1009 (1009 = skipped, no remote catalog in settings); **schema mismatch** → 1004; **missing CDN base when remote work is required** → 3005; **load prompt declined** → 5007; **query-only incomplete** → 5008; **batch cancel** → 5009.

### Catalog (1000-1999)
| Code | Meaning |
|------|---------|
| 1001 | Catalog not found |
| 1002 | Catalog invalid format |
| 1003 | Catalog load failed |
| 1004 | Catalog version mismatch |
| 1005 | Catalog entry not found |
| 1006 | Catalog disk update: **no change** (remote matches local; no file written) — `CatalogDiskUpdateKind.NoChange` |
| 1007 | Catalog disk update: **applied** (new catalog written to disk) — `CatalogDiskUpdateKind.Applied` |
| 1008 | Catalog disk update: **failed** — network/HTTP/disk/cancel/misconfiguration after an update was attempted (see `Message`; network layer may also surface Transport codes) |
| 1009 | Catalog disk update: **skipped** — no remote catalog configured in settings (`HasRemoteCatalog` false); maps to `CatalogDiskUpdateKind.SkippedNoRemote` — **not** a failure |
| 1010 | Asset-level dependency loading: asset record carries **no** asset-level dependency bundle list (`AssetRecordEntry.dependencyBundles`) under `DependencyLoadMode.AssetLevel` — load **fails loudly** instead of falling back to the owning bundle's full closure. Catalog was built without per-asset deps, or the pipeline dropped them. See [CATALOG_SCHEMA.md §2.4](CATALOG_SCHEMA.md), [LOAD_RELEASE_FLOW.md §1.1](LOAD_RELEASE_FLOW.md) |

`CatalogDiskUpdateResult` exposes **`CatalogDiskUpdateKind`**: **1006 / 1007** for successful disk outcomes, **1009** for skip-when-not-configured, **1008** (and optionally transport-related codes) for real failures.

### Bundle (2000-2999)
| Code | Meaning |
|------|---------|
| 2001 | Bundle not found |
| 2002 | Bundle load failed |
| 2003 | Bundle invalid hash |
| 2004 | Bundle size mismatch |
| 2005 | Bundle dependency missing |

### Transport (3000-3999)
| Code | Meaning |
|------|---------|
| 3001 | Network error |
| 3002 | Timeout |
| 3003 | Invalid URL |
| 3004 | Download failed |
| 3005 | Remote base not configured (CDN/base missing when remote HTTP is required) |

### Resource (4000-4999)
| Code | Meaning |
|------|---------|
| 4001 | Resource not found |
| 4002 | Resource type mismatch |
| 4003 | Resource load failed |
| 4004 | Resource key invalid |

### Operation (5000-5999)
| Code | Meaning |
|------|---------|
| 5001 | System not initialized |
| 5002 | System already initialized |
| 5003 | Operation invalid state |
| 5004 | System out of memory |
| 5005 | Operation timed out |
| 5006 | Operation dependency failed |
| 5007 | User declined remote bundle download (prompt before download) |
| 5008 | Query-only load: remote bundles missing locally — cannot complete without download |
| 5009 | Operation cancelled (e.g. `CancellationToken` on batch download) — distinct from transport failure |

### Settings (6000-6999)
| Code | Meaning |
|------|---------|
| 6001 | Settings not found |
| 6002 | Settings invalid format |
| 6003 | Settings load failed |

### Content Update Build (7000-7999)
| Code | Meaning |
|------|---------|
| 7001 | Build manifest not found |
| 7002 | Build manifest invalid format |
| 7003 | Build manifest load failed |
| 7004 | Change detection failed |
| 7005 | Update bundle build failed |
| 7006 | Mixed catalog generation failed |

Canonical source: `Shared/Constants.cs` (ErrorCode class).

---

## 5. Log Fields

Structured logging field names (canonical source: `Shared/Constants.cs` LogFields class). **Full set** — order matches the `LogFields` class in source; use these string values (or the `LogFields.*` constants) in messages and any future structured log sinks.

| Field (wire key) | `LogFields` member | Description |
|------------------|-------------------|-------------|
| `operation` | `OPERATION` | Operation name / type |
| `key` | `KEY` | Load key / identifier when logging something broader than `address` alone |
| `bundle_name` | `BUNDLE_NAME` | Bundle name |
| `error_code` | `ERROR_CODE` | Numeric error code |
| `error_message` | `ERROR_MESSAGE` | Error or failure detail text |
| `duration_ms` | `DURATION_MS` | Elapsed time in milliseconds |
| `size_bytes` | `SIZE_BYTES` | Byte size (e.g. download or file) |
| `ref_count` | `REF_COUNT` | Current operation reference count |
| `status` | `STATUS` | Operation / task status |
| `location` | `LOCATION` | Location-related detail (e.g. path or serialized hint; not a substitute for `location_hash`) |
| `address` | `ADDRESS` | User-side catalog address string |
| `location_hash` | `LOCATION_HASH` | Stable hash used as Operation cache key |
| `provider_id` | `PROVIDER_ID` | ProviderId of the executing provider |

---

## 6. RefCount Rules (Operation-Level)

RefCount operates at the **Operation** level, not the Asset level. Each Operation in the DAG has its own RefCount.

### Lifecycle
1. **First load**: `OperationCache.GetOrCreate` sets `RefCount = 1`, creates Operation
2. **Duplicate load (cache hit)**: `RefCount++`, returns existing Operation
3. **Release (user calls `HyperContent.Release`)**: `RefCount--`
4. **RefCount reaches 0**:
   - Remove Operation from OperationCache
   - **Recursively** Release all dependency Operations (each dep `RefCount--`)
   - Call `op.Dispose()` -> triggers `Provider.Release` for actual resource cleanup

### Rules
- Each `LoadAsync` call increments the target Operation's RefCount by exactly 1
- Each `Release` must be paired with a prior load; double-release on same Handle is a no-op with warning
- Dependency Operations are ref-counted separately; shared bundle Operations accumulate refs from multiple parent assets
- Bundle unload only happens when the bundle Operation's RefCount reaches 0 (all parent asset Operations released)
- `InstantiateAsync` adds an extra RefCount via `InstanceRegistry.Track`; `ReleaseInstance` decrements it

### Example: Shared Bundle

```
LoadAsync("A") -> Op-A RefCount=1, Op-Bundle RefCount=1 (dep of Op-A)
LoadAsync("B") -> Op-B RefCount=1, Op-Bundle RefCount=2 (dep of Op-B too)
Release(handleA) -> Op-A RefCount=0, Op-Bundle RefCount=1 (bundle stays)
Release(handleB) -> Op-B RefCount=0, Op-Bundle RefCount=0 (bundle unloads)
```

---

## 7. Logging (HCLogger)

All HyperContent code **must** use `HCLogger` (`Shared/HCLog.cs`) instead of `UnityEngine.Debug.Log*` directly.

Class name ends with "Logger" and methods start with "Log" — this is an undocumented Unity convention
that makes Console double-click skip these methods and navigate directly to the actual caller.

### Scripting Symbols

| Symbol | Levels Emitted | Recommended For |
|--------|---------------|-----------------|
| `HYPERCONTENT_LOG_VERBOSE` | Verbose + Info + Warn + Error | Development builds, deep debugging |
| `HYPERCONTENT_LOG` | Info + Warn + Error | Internal testing, QA |
| *(neither)* | Error only | Release / production builds |

Add symbols via **Project Settings > Player > Scripting Define Symbols** or `csc.rsp`.

### API

| Method | Level | Conditional |
|--------|-------|-------------|
| `HCLogger.LogVerbose(msg)` | Trace detail | `HYPERCONTENT_LOG_VERBOSE` |
| `HCLogger.LogInfo(msg)` | Key event | `HYPERCONTENT_LOG` or `VERBOSE` |
| `HCLogger.LogWarn(msg)` | Recoverable issue | `HYPERCONTENT_LOG` or `VERBOSE` |
| `HCLogger.LogError(msg)` | Hard failure | Always emitted |
| `HCLogger.LogError(errorCode, msg)` | Hard failure with error code | Always emitted |

### Level Guidelines

| Level | When to Use | Examples |
|-------|-------------|---------|
| **Verbose** | Fine-grained tracing that is noisy in normal use | Operation DAG step, cache hit/miss, progress tick, hash comparison |
| **Info** | Significant lifecycle milestone (few per session) | System initialized, catalog loaded, bundle downloaded, environment switched |
| **Warn** | Something unexpected but recoverable | Duplicate init ignored, retry, fallback path, missing optional data |
| **Error** | Unrecoverable failure requiring attention | Load failed, hash mismatch, missing catalog, null reference |

### Rules

1. **Never use `Debug.Log` / `Debug.LogWarning` / `Debug.LogError` directly** — always go through `HCLogger`.
2. Error messages that include an `ErrorCode` should use `HCLogger.LogError(errorCode, msg)` to ensure structured formatting.
3. Keep log messages concise; include relevant `LogFields` names when appropriate (e.g., `bundle_name`, `address`).
4. Avoid logging inside hot loops; prefer `LogVerbose` level if unavoidable.

---

## 8. Change Control

Any change to the following requires **Owner0 Code Review**:
- Design principles (section 1)
- Core interfaces (`ICatalog`, `IContentProvider`, `IBundleStore`, `IBundleTransport`, `IBundleDownloadQueue`, `IBundleLoader`, `ContentHandle<T>`)
- Operation state machine and RefCount rules
- Error codes and log fields
- Naming rules

See [OWNERS.md](OWNERS.md) for the full review process.