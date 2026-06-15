# HyperContent Provider Flow

How Providers are registered, selected, and execute actual IO for bundle loading.

For architecture overview, see [ARCHITECTURE.md](ARCHITECTURE.md).
For how Providers fit into the load flow, see [LOAD_RELEASE_FLOW.md](LOAD_RELEASE_FLOW.md).

---

## Scope

This document covers:
- IContentProvider interface and ProvideHandle bridge
- Provider registration order and conditional compilation
- BundleFileProvider local file resolution chain
- PlayAssetDeliveryBundleProvider (Android AAB)
- RemoteBundleProvider download + cache flow
- BundleAssetExtractor (zero IO asset extraction)
- Provider selection at runtime (complete picture)

---

## Key Classes

| Class | File | Owner | Responsibility |
|-------|------|-------|---------------|
| `IContentProvider` | `Runtime/Core/IContentProvider.cs` | Owner0 | Provider interface: ProviderId, Provide, Release |
| `ProvideHandle` | `Runtime/Providers/ProvideHandle.cs` | Owner2 | Bridge: Complete, Fail, UpdateProgress, GetDependencyResult |
| `ProviderRegistry` | `Runtime/Providers/ProviderRegistry.cs` | Owner2 | Provider registration by ProviderId |
| `BundleFileProvider` | `Runtime/Providers/BundleFileProvider.cs` | Owner2 | Load .bundle from local filesystem |
| `PlayAssetDeliveryBundleProvider` | `Runtime/Providers/PlayAssetDeliveryBundleProvider.cs` | Owner2 | Android AAB loading via PAD API |
| `BundleAssetExtractor` | `Runtime/Providers/BundleAssetExtractor.cs` | Owner2 | Extract asset from loaded AssetBundle (no IO) |
| `RemoteBundleProvider` | `Runtime/Providers/RemoteBundleProvider.cs` | Owner2 | HTTP download + cache, remote bundle loading |
| `LocalFileProvider` | `Runtime/Providers/LocalFileProvider.cs` | Owner2 | Editor mode direct load (no bundle) |
| `SceneProvider` | `Runtime/Providers/SceneProvider.cs` | Owner2 | Async scene loading |
| `UnityBundleLoader` | `Runtime/Bundle/UnityBundleLoader.cs` | Owner3 | AssetBundle.LoadFromFile with offset support |

---

## 1. Provider Registration

During `HyperContentImpl` construction, providers register into `ProviderRegistry` (a `Dictionary<string, IContentProvider>` keyed by ProviderId). Later registrations overwrite earlier ones with the same ProviderId.

### Registration Order

```
HyperContentImpl(catalog, bundleLoader, bundleStore, bundleTransport, ...)
  │
  ├─ [1] AssetDatabaseProvider (Editor simulation mode only)
  │      ProviderId = "AssetDatabaseProvider"
  │
  ├─ [2] Bundle File Provider (conditional — see §2 PAD)
  │      ProviderId = "BundleFileProvider"
  │      Android AAB runtime → PlayAssetDeliveryBundleProvider
  │      All other cases     → BundleFileProvider
  │
  ├─ [3] BundleAssetExtractor
  │      ProviderId = "BundleAssetExtractor"
  │
  ├─ [4] LocalFileProvider
  │      ProviderId = "LocalFileProvider"
  │
  ├─ [5] SceneProvider
  │      ProviderId = "SceneProvider"
  │
  └─ [6] RemoteBundleProvider (only if bundleTransport != null)
         ProviderId = "RemoteBundleProvider"
```

### Built-in Provider Summary

| Provider | ProviderId | Responsibility |
|----------|------------|----------------|
| `BundleFileProvider` | `"BundleFileProvider"` | Load `.bundle` from local filesystem → `AssetBundle` |
| `PlayAssetDeliveryBundleProvider` | `"BundleFileProvider"` | Android AAB variant — loads via PAD API with offset; falls back to BundleFileProvider |
| `BundleAssetExtractor` | `"BundleAssetExtractor"` | Extract asset from in-memory `AssetBundle` (no file IO) |
| `LocalFileProvider` | `"LocalFileProvider"` | Direct filesystem load (Editor mode) |
| `RemoteBundleProvider` | `"RemoteBundleProvider"` | HTTP download + local cache for remote bundles |
| `SceneProvider` | `"SceneProvider"` | Async scene loading via SceneManager |

---

## 2. Conditional Compilation: PAD Support

PlayAssetDeliveryBundleProvider is compiled only when both conditions are met:

| Layer | Symbol | Source |
|-------|--------|--------|
| asmdef `versionDefines` | `GOOGLE_PLAY_ASSET_DELIVERY` | Auto-defined when `com.google.play.assetdelivery` package is installed |
| C# preprocessor | `UNITY_ANDROID && !UNITY_EDITOR` | Unity built-in platform defines |

Result:
- No PAD package → PAD code stripped at compile time
- PAD installed + non-Android → PAD code stripped
- PAD installed + Android runtime → PAD provider compiled and registered
- Editor mode → always BundleFileProvider (predictable debugging)

PAD provider reuses ProviderId `"BundleFileProvider"`, so catalog entries don't need separate routing — the switch is transparent.

---

## 3. BundleFileProvider — Local File Resolution

BundleFileProvider resolves bundle files using a priority chain:

```
BundleFileProvider.Provide(handle)
  │
  ├─ Already loaded? (IBundleLoader.IsLoaded)
  │    └─ Yes → handle.Complete(existingBundle)
  │
  └─ ResolveFilePath(internalId):
       │
       ├─ [Priority 1] IBundleStore (download cache)
       │    └─ bundleStore.Exists(internalId)? → store path
       │
       ├─ [Priority 2] bundleBasePath (custom base directory)
       │    └─ FileExistsOrIsStreamingAssets(basePath/internalId)? → basePath/internalId
       │
       ├─ [Priority 3] StreamingAssets
       │    └─ FileExistsOrIsStreamingAssets(streamingAssetsPath/internalId)? → streaming path
       │
       └─ [Priority 4] Absolute path
            └─ File.Exists(internalId)? → internalId

  Resolved? → IBundleLoader.LoadFromFileAsync(name, path, callback)
  Not found → handle.Fail(FileNotFoundException)
```

**Priority rationale**:
- Hot-updated bundles (store) override ship-time bundles
- Custom content directories for DLC
- StreamingAssets for ship-time bundles
- Absolute paths for debugging

---

## 4. PlayAssetDeliveryBundleProvider — Android AAB

On Android with AAB packaging, bundles inside StreamingAssets are packed into Asset Packs. Normal file paths don't work — the PAD API provides the real path and byte offset.

```
PlayAssetDeliveryBundleProvider.Provide(handle)
  │
  ├─ Already loaded? → handle.Complete(existingBundle)
  │
  └─ PlayAssetDelivery.RetrieveAssetPackAsync("Bundles")
       │
       ├─ Exception / Error / Not Available
       │    └─ Fallback → BundleFileProvider.Provide(handle)
       │
       └─ GetAssetLocation(assetName)
            │
            ├─ null (not found in pack)
            │    └─ Fallback → BundleFileProvider.Provide(handle)
            │
            └─ AssetLocation { Path, Offset }
                 └─ IBundleLoader.LoadFromFileAsync(name, path, offset, callback)
                      ├─ Success → handle.Complete(bundle)
                      └─ Failure → Fallback → BundleFileProvider.Provide(handle)
```

Key points:
- The `offset` parameter is critical: AAB packs multiple files into a single archive at different byte offsets
- Every failure path falls back to BundleFileProvider (handles non-AAB scenarios)
- Asset name is extracted from internalId by stripping the platform prefix

---

## 5. RemoteBundleProvider — Download + Cache

```
RemoteBundleProvider.Provide(handle)
  │
  ├─ Already loaded? → handle.Complete(existingBundle)
  │
  ├─ In local store? (bundleStore.Exists)
  │    └─ Yes → IBundleLoader.LoadFromFileAsync from store path
  │
  └─ Enqueue `IBundleDownloadQueue` (priority **High**) → `BundleDownloadQueue` → `IBundleTransport` (merge by relative path)
       ├─ Success → bundleStore.Save(name, data, hash) → LoadFromFileAsync from store
       │              └─ File load failed → LoadFromMemory fallback
       └─ Failure → handle.Fail(exception)
```

**URL composition:** For remote bundles, `ResourceLocation.InternalId` is the **CDN-relative** path (after the per-platform segment in `CombineRemoteCdnRequestUrl`). The catalog / `LocalContentCatalog` does **not** store full URLs; `HttpBundleTransport` resolves the final URL when `DownloadAsync` runs.

**Progress & cancel semantics:** Global queue vs batch-scoped progress and batch **`CancellationToken`** are defined in [CONVENTIONS.md §1.5–§1.6](CONVENTIONS.md). `RemoteBundleProvider` participates in **enqueue + completion** only; it **must not** call `IBundleTransport` directly for bundle bytes (all bundle HTTP goes `IBundleDownloadQueue` → `BundleDownloadQueue` → transport). Load-driven enqueues use default `CancellationToken` unless a future API plumbs load-scoped tokens through `BundleDownloadEnqueueOptions`.

---

## 6. BundleAssetExtractor — Asset Extraction

BundleAssetExtractor does **zero file IO**. It only calls `LoadAssetAsync` on an already-loaded AssetBundle.

```
BundleAssetExtractor.Provide(handle)
  │
  ├─ Get primary bundle: handle.GetDependencyResult<AssetBundle>(0)
  │
  ├─ bundle == null → handle.Fail("Dependency bundle not loaded")
  │
  └─ bundle.LoadAssetAsync<T>(internalId)
       ├─ asset != null → handle.Complete(asset)
       └─ asset == null → handle.Fail("Asset not found in bundle")
```

All download/load work is exclusively BundleFileProvider's (or RemoteBundleProvider's) domain.

---

## 7. IBundleLoader — Offset-Aware Loading

`UnityBundleLoader` supports two loading modes:

| Mode | When Used | API |
|------|-----------|-----|
| Zero offset | BundleFileProvider, RemoteBundleProvider | `LoadFromFileAsync(name, path, callback)` |
| With offset | PlayAssetDeliveryBundleProvider (AAB) | `LoadFromFileAsync(name, path, offset, callback)` |

Both delegate to `AssetBundle.LoadFromFileAsync(path, crc=0, offset)`. Loaded bundles are cached in `_loadedBundles` dictionary.

---

## 8. Provider Selection — Complete Picture

```
                Catalog: ProviderId = "BundleFileProvider"
                              │
                   ProviderRegistry.Get("BundleFileProvider")
                              │
        ┌─────────────────────┼──────────────────────────┐
        │                     │                          │
  Android AAB Runtime    Android APK / iOS          Editor / Windows
  (PAD package)           Runtime
        │                     │                          │
  PlayAssetDelivery    BundleFileProvider          BundleFileProvider
  BundleProvider             │                          │
        │               ResolveFilePath            ResolveFilePath
   PAD API ───┐         (store → base →           (streaming or
        │     │          streaming → abs)            basePath)
   success  failure          │                          │
      │        │        LoadFromFileAsync          LoadFromFileAsync
      │        └─fallback─→ (offset=0)             (offset=0)
      │
  LoadFromFileAsync
   (with offset)
```

---

## Related Docs

- [ARCHITECTURE.md](ARCHITECTURE.md) — Provider Layer in the layered architecture
- [LOAD_RELEASE_FLOW.md](LOAD_RELEASE_FLOW.md) — How Providers are invoked from the Operation DAG
- [INITIALIZATION_FLOW.md](INITIALIZATION_FLOW.md) — Provider registration during init
- [CONTENT_UPDATE_FLOW.md](CONTENT_UPDATE_FLOW.md) — Bundle download via IBundleTransport
