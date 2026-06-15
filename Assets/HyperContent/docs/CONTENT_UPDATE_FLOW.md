# HyperContent Content Update Flow

How catalog updates are detected and bundles are downloaded at runtime.

For initialization (which triggers catalog resolution), see [INITIALIZATION_FLOW.md](INITIALIZATION_FLOW.md).
For how build generates remote catalog, see [BUILD_LIFECYCLE.md](BUILD_LIFECYCLE.md).

---

## Scope

This document covers:
- Catalog freshness check (hash comparison)
- Catalog download and caching
- Bundle download management (check / selective / batch download)
- Cache directory structure and strategy
- Public API for update checking and downloading

---

## Key Classes

| Class | File | Owner | Responsibility |
|-------|------|-------|---------------|
| `CatalogLocator` | `Runtime/Catalog/CatalogLocator.cs` | Owner3 | settings.json load; **local** path resolve (`ResolveLocalCatalogAsync`, no HTTP); remote hash/catalog **to disk** via **direct `UnityWebRequest` in this class** — **not** `IBundleDownloadQueue` / `HttpBundleTransport` (see [ARCHITECTURE.md](ARCHITECTURE.md) §6.4.2) |
| `BundleDownloadManager` | `Runtime/Catalog/BundleDownloadManager.cs` | Owner3 | Bundle download: check pending, check by asset, selective download |
| `LocalBundleStore` | `Runtime/Bundle/LocalBundleStore.cs` | Owner3 | Local bundle cache: atomic write, LRU prune, hash verify |
| `HttpBundleTransport` | `Runtime/Bundle/HttpBundleTransport.cs` | Owner3 | HTTP download: retry, concurrency, timeout, progress (used **only** by `BundleDownloadQueue`) |
| `BundleDownloadQueue` | `Runtime/Bundle/BundleDownloadQueue.cs` | Owner3 | `IBundleDownloadQueue`: priorities, merge by `RemoteRelativePath`, fan-out `OnComplete` |
| `RuntimeSettings` | `Runtime/Core/RuntimeSettings.cs` | Owner0 | settings.json configuration |
| `HyperContentPaths` | `Runtime/Core/HyperContentPaths.cs` | Owner0 | Path constants and utilities |

---

## 1. Two-Level Update Strategy

HyperContent uses a two-level update strategy modeled after Unity Addressables. **Catalog/hash HTTP is never part of `Initialize`** — init only runs `ResolveLocalCatalogAsync` (disk). The app chooses **when** to call the disk hot-update API (see [INITIALIZATION_FLOW.md](INITIALIZATION_FLOW.md) §2–§3).

| Level | What updates | How | When |
|-------|--------------|-----|------|
| **Catalog** | Asset-to-bundle mapping | **Init:** `CatalogLocator.ResolveLocalCatalogAsync()` picks **cached vs package** catalog on disk (**no HTTP**). **Optional disk refresh:** `HyperContent.TryUpdateCachedCatalogOnDiskAsync` → remote hash compare + catalog **to disk only**. **Recommended:** run that **before** `Initialize` so the first init loads the new cached file (**no** `ReloadRuntimeCatalogAsync`). **Alternative:** run after init → call `ReloadRuntimeCatalogAsync` after **`Applied`** before bundle work that needs the new mapping. | Explicit app call; ordering relative to init is a product choice |
| **Bundles** | Bundle file bytes | `BundleDownloadManager` + `IBundleStore` / catalog per-bundle hash | After init, before or during gameplay |

```
┌─────────────────────────────┐     ┌─────────────────────────────┐
│   Level 1: Catalog          │     │   Level 2: Bundles          │
│                             │     │                             │
│  Local path at init         │ ──→ │  Which remote bundles are   │
│  (ResolveLocalCatalogAsync) │     │  missing or stale?          │
│                             │     │                             │
│  Optional: disk hot-update   │     │  BundleDownloadManager      │
│  (TryUpdate… before or       │     │  + RemoteBundleProvider     │
│   after Init — see §2.3)     │     │                             │
└─────────────────────────────┘     └─────────────────────────────┘
```

---

## 2. Catalog paths and remote refresh

### 2.1 Initialization — local resolve only (`ResolveLocalCatalogAsync`)

Used by `HyperContent` bundle-mode init. **No UnityWebRequest** for catalog/hash.

```
Load settings.json (CatalogLocator / HyperContentPaths.SettingsPath)
  │
  ├─ Cached catalog file exists? (cache root + settings.cachedCatalogPath)
  │    └─ Yes → CatalogSource.Cached, path = that file
  │
  └─ No → CatalogSource.Package, path = CatalogBasePath + settings.localCatalogPath
```

`HasRemoteCatalog` in settings does **not** trigger downloads during this step.

### 2.2 Optional — remote catalog to disk (`CheckAndDownloadCatalogUpdateAsync`)

Internal locator used by the facade **`HyperContent.TryUpdateCachedCatalogOnDiskAsync`**. Each HTTP GET uses **`HyperContentPaths.CombineRemoteCdnRequestUrl(effectiveBase, relativePath)`** (CDN base + platform segment + relative path from JSON).

**Path shape:** `remoteCatalogHashRelativePath` and `remoteCatalogRelativePath` are relative to **`{platform}/`** on the CDN. The build writes remote files to `ServerData/{Platform}/HyperCatalog_{ver}.hash` / `.bin`; `settings.json` stores those **filenames only** (no `hc/` segment on the CDN — unlike local `StreamingAssets/hc/`).

**Transport split:** Hash and catalog downloads use **`UnityWebRequest` inside `CatalogLocator` only**. They are **not** enqueued on the global **`IBundleDownloadQueue`** (bundles use `HttpBundleTransport` → queue). This keeps metadata fetches independent of bundle backlog and priorities.

### 2.3 Catalog disk update — outcomes (`CatalogDiskUpdateResult`)

Facade: `HyperContent.TryUpdateCachedCatalogOnDiskAsync`. Structured result: `CatalogDiskUpdateKind` + `ErrorCode` + `Message`.

| `CatalogDiskUpdateKind` | `ErrorCode` | Meaning |
|-------------------------|-------------|---------|
| `NoChange` | **1006** | Remote hash matches local; no catalog file written. |
| `Applied` | **1007** | New catalog (and hash) written under cache. |
| `SkippedNoRemote` | **1009** | No remote catalog in settings (`HasRemoteCatalog` false). **Not a failure** — no network, no disk writes for this API. |
| `Failed` | **1008** (or other) | Network, non-2xx HTTP, disk write, cancel, or misconfiguration when remote update was attempted. |
| — | **6001** etc. | Settings missing or invalid when loading `settings.json` for this path. |

Full table: [CONVENTIONS.md §4](CONVENTIONS.md).

**After `Applied` (1007):**

- **Recommended:** If `TryUpdateCachedCatalogOnDiskAsync` finished **before** the first `HyperContent.Initialize`, the subsequent init loads the updated cached catalog — **no** `ReloadRuntimeCatalogAsync` needed for cold start ([INITIALIZATION_FLOW.md](INITIALIZATION_FLOW.md) §3, §8).
- **If** the disk update ran **after** `Initialize`, call **`ReloadRuntimeCatalogAsync`** before bundle work that depends on the new catalog ([INITIALIZATION_FLOW.md §8](INITIALIZATION_FLOW.md)).

```
Effective CDN base = module SetRemoteBundleBaseUrl if non-empty, else RuntimeSettings.remoteBundleBaseUrl
  │
  ├─ No remote catalog configured (settings) → facade SkippedNoRemote + 1009
  │
  ├─ GET remote hash URL → empty / failure → Failed
  │
  ├─ Hash matches disk + cached .bin exists → NoChange
  │
  └─ Else GET remote catalog .bin → write .tmp → rename; write hash file → Applied
```

This path **does not** load JSON into `LocalContentCatalog` and does **not** call `Initialize`. If memory already holds a catalog from a prior **`Initialize`**, call **`ReloadRuntimeCatalogAsync`** after **`Applied`** before relying on new bundle entries; if **`Initialize` has not run yet**, skip reload and proceed to init.

### 2.4 Memory reload (`ReloadRuntimeCatalogAsync`)

Re-runs **`ResolveLocalCatalogAsync`** and replaces the runtime `ICatalog` implementation inside `HyperContentImpl` from the resolved path. Clears the lazy `BundleDownloadManager` cache. **No network.**

### CatalogSource (local resolve)

| Source | Meaning |
|--------|---------|
| `Package` | Package catalog: `CatalogBasePath` + `localCatalogPath` (e.g. StreamingAssets/hc/HyperCatalog.bin) |
| `Cached` | Versioned file under `persistentDataPath/.../hc/` when `cachedCatalogPath` exists on disk |

The enum value **`Downloaded`** is not returned by **`ResolveLocalCatalogAsync`**; it remains for diagnostics compatibility. After a successful remote write in §2.2, the next local resolve typically reports **`Cached`** (file now present).

### Cache Atomic Write

Remote catalog downloads use write-to-tmp-then-rename to prevent corruption if the process is interrupted:
1. Write data to `HyperCatalog_{ver}.bin.tmp`
2. Delete existing `HyperCatalog_{ver}.bin` (if any)
3. Rename `.tmp` → `.bin`

---

## 3. Bundle Freshness Check

After catalog initialization, `BundleDownloadManager` checks which bundles need downloading.

### Check Modes

| Mode | API | Use Case |
|------|-----|----------|
| **Check all** | `CheckAllPendingDownloads()` | Full update check at startup |
| **Check by asset** | `CheckDownloadsForAsset(path)` | Pre-level download check |
| **Check multiple assets** | `CheckDownloadsForAssets(paths)` | Batch asset check (deduplicated) |

### Check Logic

For each bundle in the catalog:
1. Is it a remote bundle (`ContentLocation.Remote`)? If not, skip.
2. Does it exist in `IBundleStore`? If yes, skip (already downloaded).
3. → Add to pending downloads list with size and **CDN-relative** path (`RemoteRelativePath`; full URL only inside `HttpBundleTransport` at request time).

For asset-based checks, the system also includes dependency bundles (via `ResourceLocation.Dependencies`), deduplicated across multiple assets.

---

## 4. Bundle Download

### Download Modes

| Mode | API | Description |
|------|-----|-------------|
| **Download all (updates)** | `HyperContent.DownloadAllUpdatesAsync(onProgress, onComplete, cancellationToken)` | All pending remote bundles (**Normal** priority) |
| **Download all blocking** | `HyperContent.DownloadAllBlockingBundlesAsync(...)` | Remote + `BundleTagFlags.Blocking` only (**High**) |
| **Selective download** | `HyperContent.DownloadBundlesAsync(bundleNames, onProgress, onComplete, cancellationToken)` | User-selected bundles (**Normal**) |

Optional **`CancellationToken`** (last parameter): batch-level cancel — no new HTTP for cancelled pending items; merged-URL abort when last waiter cancels. See [CONVENTIONS.md §1.6](CONVENTIONS.md).

### Download Progress

| Field | Description |
|-------|-------------|
| `completedCount` / `totalCount` | Bundle count progress |
| `completedBytes` / `totalBytes` | Byte-level progress |
| `currentBundleName` | Currently downloading bundle |
| `currentBundleProgress` | 0-1 progress of current bundle |

### Download Result

| Field | Description |
|-------|-------------|
| `success` | True only if **not** cancelled and all downloads succeeded |
| `cancelled` | True if the batch `CancellationToken` was cancelled by completion time |
| `downloadedCount` | Number of successfully downloaded bundles |
| `failedCount` | Number of failed bundles |
| `failedBundleList` | Names of failed bundles |

---

## 5. Public API (via HyperContent Facade)

Catalog-on-disk and bundle download are exposed through the static `HyperContent` facade (Owner2 surface; Owner3 implements underlying types).

| API | Description |
|-----|-------------|
| `SetRemoteBundleBaseUrl(url)` | Module-level CDN base (O(1)); may be called **before** `Initialize`; applied to `HttpBundleTransport` when created or updated. |
| `TryUpdateCachedCatalogOnDiskAsync(...)` | Remote hash + optional catalog download **to disk only** (wraps `CatalogLocator.CheckAndDownloadCatalogUpdateAsync`). Does not reload memory catalog. |
| `ReloadRuntimeCatalogAsync(...)` | Reload in-memory catalog from current local resolve path — needed when disk hot-update ran **after** init (**`Applied`**); not needed if disk update finished **before** first `Initialize`. |
| `HasPendingDownloads()` / `HasPendingDownloads(scope)` | Whether any remote bundles need download; `PendingBundleQueryScope.BlockingOnly` limits to `BundleTagFlags.Blocking`. |
| `GetPendingDownloads()` / `GetPendingDownloads(scope)` | Pending list + sizes (same scope as above). |
| `GetDownloadsForAsset(path)` | Downloads needed for one asset (deps included). |
| `GetDownloadsForAssets(paths)` | Same for multiple assets (deduplicated). |
| `DownloadAllUpdatesAsync(onProgress, onComplete, cancellationToken)` | Download all pending remote bundles (manager batch path). |
| `DownloadAllBlockingBundlesAsync(..., cancellationToken)` | Subset: remote + **Blocking** tag only, not yet local. |
| `DownloadBundlesAsync(names, onProgress, onComplete, cancellationToken)` | Download selected bundles |
| `RegisterDownloadQueueProgressListener` / `Unregister…` | Global aggregate queue progress (Owner2 facade → `IBundleDownloadQueue`). |

`BundleDownloadManager` is created lazily from `HyperContentImpl.Current` catalog, store, and **`IBundleDownloadQueue`**. Load-driven remote bundles use the same queue via `RemoteBundleProvider` (**High** priority). Batch `DownloadAllUpdatesAsync` uses **Normal**; `DownloadAllBlockingBundlesAsync` uses **High**. Only `BundleDownloadQueue` calls `IBundleTransport` for bundle bytes. **Operation-scoped batch progress** + **per-waiter cancel / merge** — **Owner3** (`BundleDownloadQueue`, `BundleDownloadManager`); see [ARCHITECTURE.md](ARCHITECTURE.md) §6.4.3 and [CONVENTIONS.md §1.5–§1.6](CONVENTIONS.md).

---

## 6. Cache Strategy

### LocalBundleStore

| Feature | Description |
|---------|-------------|
| Atomic write | Write-to-tmp-then-rename prevents partial files |
| Hash verify | SHA256 verification on read (optional, per-bundle) |
| LRU prune | Auto-cleanup when exceeding max size (default 1GB) |
| Startup integrity | Verify cached bundles on startup |

### HttpBundleTransport

| Feature | Value |
|---------|-------|
| Async model | Awaitable-based (never blocks main thread) |
| Auto retry | 3 attempts with exponential backoff |
| Concurrent downloads | Up to 4 simultaneous |
| Progress reporting | Per-bundle progress callback |
| Download resume (HTTP Range) | **Deferred** — tracked in [TODO.md](TODO.md) (*HTTP Range 断点续传*); requires queue/coordination per runtime plan §5 |

### Cache Directory

```
persistentDataPath/
└── HyperContent/
    ├── hc/                                  ← cached catalog + hash
    │   ├── HyperCatalog_{buildVersion}.bin
    │   └── HyperCatalog_{buildVersion}.hash
    └── Bundles/                             ← downloaded bundles
         ├── ui_common.bundle
         ├── textures_hero.bundle
         └── ...
```

New APK (different `buildVersion`) auto-invalidates catalog cache because the versioned path no longer matches.

---

## 7. Hot-Update Detection Flow (End to End)

```
Build Server                    CDN                         Client Device
     │                           │                              │
     │ Build new version         │                              │
     │ → bundles + catalog       │                              │
     │ → HyperCatalog_{v2}.bin   │                              │
     │ → HyperCatalog_{v2}.hash  │                              │
     │                           │                              │
     │ Upload ─────────────────→ │                              │
     │                           │                              │
     │                           │    App start                 │
     │                           │    (Recommended)             │
     │                           │    TryUpdateCachedCatalog…   │
     │                           │    ← GET hash / GET catalog │
     │                           │    then Init (local resolve  │
     │                           │    loads cache; no reload)   │
     │                           │    — or — init first, then   │
     │                           │    TryUpdate… + Reload if    │
     │                           │    Applied                   │
     │                           │                              │
     │                           │    Compare bundle hashes     │
     │                           │    ← Download changed ───────│
     │                           │      bundles                 │
     │                           │                              │
     │                           │    Ready                     │
```

---

## Related Docs

- [INITIALIZATION_FLOW.md](INITIALIZATION_FLOW.md) — `ResolveLocalCatalogAsync` + module CDN base during init; explicit disk catalog API
- [BUILD_LIFECYCLE.md](BUILD_LIFECYCLE.md) — How remote catalog files are generated
- [PROVIDER_FLOW.md](PROVIDER_FLOW.md) — RemoteBundleProvider uses `IBundleTransport` (relative path → URL inside transport)
- [CONVENTIONS.md](CONVENTIONS.md) — Error codes for transport and catalog disk update (§4)
- [TODO.md](TODO.md) — Deferred transport/queue items (Owner3)
