# HyperContent Initialization Flow

How the system starts up: from `settings.json` loading through catalog resolution to ready state.

For architecture overview, see [ARCHITECTURE.md](ARCHITECTURE.md).
For content update mechanism, see [CONTENT_UPDATE_FLOW.md](CONTENT_UPDATE_FLOW.md).
For load/release flows after initialization, see [LOAD_RELEASE_FLOW.md](LOAD_RELEASE_FLOW.md).

---

## Scope

This document covers:
- System initialization entry points (callback / awaitable)
- Bootstrapper integration pattern
- settings.json loading and catalog source resolution
- Editor vs Runtime path differences
- Android StreamingAssets handling

---

## Key Classes

| Class | File | Owner | Responsibility |
|-------|------|-------|---------------|
| `HyperContent` | `Runtime/Operations/HyperContent.cs` | Owner2 | Static facade, initialization entry point |
| `HyperContentImpl` | `Runtime/Operations/HyperContentImpl.cs` | Owner2 | Internal implementation, provider registration |
| `CatalogLocator` | `Runtime/Catalog/CatalogLocator.cs` | Owner3 | Catalog discovery: settings + **local** resolve; internal `CheckAndDownloadCatalogUpdateAsync` for disk-only remote update |
| `LocalContentCatalog` | `Runtime/Catalog/LocalContentCatalog.cs` | Owner3 | Catalog parsing, address query |
| `RuntimeSettings` | `Runtime/Core/RuntimeSettings.cs` | Owner0 | settings.json deserialization structure |
| `HyperContentPaths` | `Runtime/Core/HyperContentPaths.cs` | Owner0 | Path constants, Android StreamingAssets support |

---

## 1. Initialization Entry Points

HyperContent uses **explicit initialization** — callers must initialize before any Load/Release calls.

| Approach | API | When to use |
|----------|-----|-------------|
| **Callback** | `HyperContent.Initialize(pOnComplete?)` | Non-blocking, callback-driven startup |
| **Awaitable** | `await HyperContent.InitializeAsync()` | Unity 6 async callers |

Catalog path is resolved internally by `CatalogLocator` (via `settings.json`). Callers do not need to specify any path.

Calling `LoadAsync` before initialization completes logs an error and returns null. Calling `Initialize` after already initialized is a no-op (callback receives `true` immediately).

---

## 2. Bootstrapper Integration

HyperContent plugs into the game's `LoadingManager` sequential loading chain.

**Recommended (cold start):** run **remote catalog → disk** (`HyperContent.TryUpdateCachedCatalogOnDiskAsync`) **before** `HyperContent.Initialize`, in a dedicated loading step that **fully completes** (success or failure) before `InitLoadingHelper` runs. `TryUpdateCachedCatalogOnDiskAsync` does **not** require prior initialization; after an `Applied` write, the subsequent `Initialize` path’s `ResolveLocalCatalogAsync` will pick the **cached** catalog file, so you usually **do not** need `ReloadRuntimeCatalogAsync` on that path.

**Alternative:** initialize first (local catalog only), then call `TryUpdateCachedCatalogOnDiskAsync`; if the disk update applies a new catalog, call `ReloadRuntimeCatalogAsync` before bundle work that depends on the new mapping (see [CONTENT_UPDATE_FLOW.md](CONTENT_UPDATE_FLOW.md) §2.3).

```
Bootstrapper.Start() → LoadingManager
  ├─ [0] (Recommended) Catalog disk refresh — await TryUpdateCachedCatalogOnDiskAsync; then continue
  ├─ [1] AddressableManager.InitLoadingHelper
  │       └─ IResourceLoader.InitializeAsync(callback)
  │            └─ HyperContent.Initialize(pOnComplete: callback)
  ├─ [2] HybridCLRManager.LoadingHelper      ← safe: init is complete
  ├─ [3] GameLocalizeManager.LoadingHelper
  └─ ...
```

`AddressableManager` wraps HyperContent behind `IResourceLoader`, so game code never directly references HyperContent. The loading manager guarantees sequential execution — each step finishes before the next starts.

---

## 3. Initialization Flow

```
HyperContent.Initialize(pOnComplete: callback) or await InitializeAsync()
  │
  ├─ Editor AssetDatabase mode?
  │    └─ Yes → Create EditorAssetDatabaseCatalog + AssetDatabaseProvider → Ready
  │
  └─ Bundle mode (Editor with bundles / Runtime):
       │
       ├─ 1. await CatalogLocator.ResolveLocalCatalogAsync()  (disk only, **no HTTP**)
       │      ├─ Load settings.json from RuntimePath
       │      ├─ If cached catalog file exists → CatalogSource.Cached
       │      └─ Else → package catalog at CatalogBasePath + localCatalogPath → CatalogResolution
       │
       ├─ 2. LocalContentCatalog.Initialize(resolution.catalogPath)
       │      └─ Parse JSON → build O(1) Dictionary lookups → pre-cache BundleInfo
       │
       ├─ 3. Create HyperContentImpl(catalog, bundleLoader, ...)
       │      └─ Register all Providers (see PROVIDER_FLOW.md §1)
       │
       ├─ 4. HttpBundleTransport initialized with effective CDN base
       │      (module `SetRemoteBundleBaseUrl` if set, else `RuntimeSettings.remoteBundleBaseUrl`)
       │
       └─ 5. callback(true) — system ready
```

**Remote catalog to disk (optional, explicit):** Bundle-mode **init** still does **not** perform catalog/hash HTTP; it only runs `ResolveLocalCatalogAsync` (disk).

- **Recommended ordering:** Call `await HyperContent.TryUpdateCachedCatalogOnDiskAsync` **before** `Initialize` (wraps `CatalogLocator.CheckAndDownloadCatalogUpdateAsync`). It writes cache files only and **does not** touch the in-memory catalog (there is none yet). On **`Applied`**, the first `Initialize` will load the updated file via `ResolveLocalCatalogAsync` → **Cached**; **no** `ReloadRuntimeCatalogAsync` is required for that cold-start path.
- **If** you run `TryUpdateCachedCatalogOnDiskAsync` **after** `Initialize` and get **`Applied`**, the process memory still holds the old catalog — call `await HyperContent.ReloadRuntimeCatalogAsync()` before bundle work that depends on the new mapping.

`SetRemoteBundleBaseUrl` may be called **before** `Initialize`; the value is stored O(1) and applied when the transport is created or updated if already initialized.

**After init — batch bundle download:** `DownloadAllUpdatesAsync` / `DownloadAllBlockingBundlesAsync` / `DownloadBundlesAsync` support optional **`CancellationToken`** and populate **`DownloadResult.cancelled`** when cancelled; merge and queue semantics are documented in [CONVENTIONS.md](CONVENTIONS.md) §1.6 and [LOAD_RELEASE_FLOW.md](LOAD_RELEASE_FLOW.md) §0 (runtime plan §6.A).

---

## 4. Catalog Source Priority (local resolve only)

`ResolveLocalCatalogAsync` chooses the catalog **file path** without network I/O:

```
1. Cached  ← If settings.cachedCatalogPath is set and that file exists under the cache root
2. Package ← Else StreamingAssets (or editor bundle output) + localCatalogPath
```

Remote hash compare and catalog download are **only** in `CheckAndDownloadCatalogUpdateAsync` / facade `TryUpdateCachedCatalogOnDiskAsync`. See [CONTENT_UPDATE_FLOW.md](CONTENT_UPDATE_FLOW.md) for hash/catalog download details.

---

## 5. settings.json

`settings.json` is **immutable after APK install** — baked into StreamingAssets at build time, only changes with a new APK release.

### Key Fields (RuntimeSettings)

| Field | Description |
|-------|-------------|
| `buildVersion` | Build version string (format: [CONVENTIONS.md §2](CONVENTIONS.md)), e.g. `"2026.02.24.03.02.10"` |
| `localCatalogPath` | Package catalog relative path, always `"HyperCatalog.bin"` |
| `remoteBundleBaseUrl` | Unified CDN root **without** platform segment; used for bundles and catalog/hash HTTP |
| `remoteCatalogRelativePath` | Catalog `.bin` path after `{platform}/` on CDN — versioned filename only, e.g. `HyperCatalog_2026.03.25.17.57.05.bin` (not under `hc/` on CDN; local package still uses `StreamingAssets/hc/`) |
| `remoteCatalogHashRelativePath` | Hash file after `{platform}/`, e.g. `HyperCatalog_2026.03.25.17.57.05.hash` |
| `cachedCatalogPath` | Local cache catalog path (versioned filename) |
| `catalogRequestTimeout` | Download timeout in seconds |
| `HasRemoteCatalog` | Computed: true if `remoteCatalogHashRelativePath` is set |

---

## 6. Path Resolution

### Runtime Paths

| Environment | Settings Path | Catalog Base Path |
|-------------|---------------|-------------------|
| **Editor** (Bundle mode) | `HyperContentBuild/{Platform}/settings.json` | `HyperContentBuild/{Platform}/` |
| **Runtime** | `StreamingAssets/hc/settings.json` | `StreamingAssets/hc/` |

- `{Platform}` = `Android` / `iOS` / `Windows` (from `EditorUserBuildSettings.activeBuildTarget`)

### File Locations

| Source | Path | Description |
|--------|------|-------------|
| Package catalog | `StreamingAssets/hc/HyperCatalog.bin` | Fixed name, ships with APK |
| Package settings | `StreamingAssets/hc/settings.json` | Fixed name, ships with APK |
| Cached catalog | `persistentDataPath/HyperContent/hc/HyperCatalog_{ver}.bin` | Versioned, version change auto-invalidates |
| Cached hash | `persistentDataPath/HyperContent/hc/HyperCatalog_{ver}.hash` | SHA256, compared with remote |
| Remote catalog | `{remoteBundleBaseUrl}/{platform}/HyperCatalog_{ver}.bin` | Same layout as `ServerData/{Platform}/` publish; paths in JSON are filename-only after `{platform}/` |

---

## 7. Platform Notes

### Android StreamingAssets

`File.Exists` / `File.ReadAllText` do not work for Android StreamingAssets (`jar:file://` path). HyperContent handles this transparently:

| Operation | Non-Android | Android StreamingAssets |
|-----------|-------------|----------------------|
| Read text file | `File.ReadAllText` | `UnityWebRequest` |
| Check file exists | `File.Exists` | Assume exists (let `AssetBundle.LoadFromFile` verify) |
| Load AssetBundle | `AssetBundle.LoadFromFileAsync` | Same (Unity handles `jar:file://` internally) |

Key utility methods:
- `HyperContentPaths.LoadText(path)` — auto-selects File.ReadAllText or UnityWebRequest
- `HyperContentPaths.FileExistsOrIsStreamingAssets(path)` — returns true for Android StreamingAssets paths

---

## 8. `ReloadRuntimeCatalogAsync` and in-flight loads

`HyperContent.ReloadRuntimeCatalogAsync()` replaces the **in-memory** `ICatalog` on `HyperContentImpl` (see implementation). It does **not**:

- Cancel or drain **`IBundleDownloadQueue`**
- Invalidate **`OperationCache`** entries that were created **before** reload

**Application contract:** If **`TryUpdateCachedCatalogOnDiskAsync`** ran **after** `Initialize` and returned **`Applied`**, call **`ReloadRuntimeCatalogAsync`** before relying on new bundle entries or starting **new** bundle download work that depends on the new mapping ([CONVENTIONS.md §1.4](CONVENTIONS.md)). If the disk hot-update **completed before** the first `Initialize`, **reload is not required** for that session — `Initialize` already resolves the cached file.

If a **long-running `LoadAsync`** was started against the **old** catalog, behavior is **undefined** if reload swaps the catalog mid-flight; avoid overlapping reload with active loads of addresses that may change across catalogs.

**Concurrent loads** of the same address still follow [CONVENTIONS.md §6](CONVENTIONS.md) (Operation cache + RefCount).

---

## Related Docs

- [ARCHITECTURE.md](ARCHITECTURE.md) — System design overview
- [CONTENT_UPDATE_FLOW.md](CONTENT_UPDATE_FLOW.md) — Catalog update hash comparison and bundle download details
- [LOAD_RELEASE_FLOW.md](LOAD_RELEASE_FLOW.md) — What happens after initialization (loading assets)
- [PROVIDER_FLOW.md](PROVIDER_FLOW.md) — Provider registration during init
- [BUILD_LIFECYCLE.md](BUILD_LIFECYCLE.md) — How settings.json and catalog are generated at build time
