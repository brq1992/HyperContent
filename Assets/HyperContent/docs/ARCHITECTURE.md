# HyperContent Architecture

System architecture overview: layered design, core abstractions, and class responsibilities. Read this first to understand how HyperContent works as a whole.

For conventions (naming, error codes, RefCount), see [CONVENTIONS.md](CONVENTIONS.md).
For Catalog schema details, see [CATALOG_SCHEMA.md](CATALOG_SCHEMA.md).
For Owner responsibilities, see [OWNERS.md](OWNERS.md).
For runtime flows: [INITIALIZATION_FLOW.md](INITIALIZATION_FLOW.md), [LOAD_RELEASE_FLOW.md](LOAD_RELEASE_FLOW.md), [CONTENT_UPDATE_FLOW.md](CONTENT_UPDATE_FLOW.md), [PROVIDER_FLOW.md](PROVIDER_FLOW.md).

**Spec pointers:** numeric **error codes** — [CONVENTIONS.md §4](CONVENTIONS.md) (source: `Shared/Constants.cs`). **Catalog disk hot-update** — `CatalogDiskUpdateKind` + 1006–1009 (`SkippedNoRemote` is **not** failure); detail — [CONTENT_UPDATE_FLOW.md §2.3](CONTENT_UPDATE_FLOW.md). **Global vs scoped download progress** — [CONVENTIONS.md §1.5](CONVENTIONS.md). **Catalog/hash HTTP vs bundle queue** — [CONVENTIONS.md §1.2](CONVENTIONS.md), §6.4.2 below. **Load network modes + minimal sample** — [LOAD_RELEASE_FLOW.md §9](LOAD_RELEASE_FLOW.md). **Reload vs in-flight loads** — [INITIALIZATION_FLOW.md §8](INITIALIZATION_FLOW.md).

> Design doc version: v0.6 | Runtime wiring aligned with `HyperContentImpl`, `CatalogLocator`, `BundleDownloadQueue` (2026-03)

---

## 1. Design Principles

Five core decisions that permeate the entire system:

| # | Decision | Constraint |
|---|----------|------------|
| 1 | **User must hold a Handle to Release** | Prevents wild releases; Handle is the closed-loop credential for load and unload |
| 2 | **Same resource loaded twice hits Operation cache + ref count** | No duplicate IO; automatically shares underlying Bundle/Asset |
| 3 | **RefCount reaching zero recursively unloads dependencies** | Guarantees correct Bundle unload timing -- no leaks, no premature unloads |
| 4 | **Address translated to Location via Catalog** | Caller is fully decoupled from physical asset location; hot-update only replaces Catalog |
| 5 | **Providers are registered by ProviderId** | Supports local, remote, editor, custom load sources -- transparent to upper layers |

---

## 2. Layered Architecture

```
+------------------------------------------------------+
|                    API Layer                          |
|         HyperContent (facade / sole entry point)     |
+------------------------------------------------------+
|                  Catalog Layer                        |
|     Address (string) -> ResourceLocation             |
+------------------------------------------------------+
|                 Operation Layer                       |
|    Async DAG / RefCount / Scheduling / Op Cache      |
+------------------------------------------------------+
|                  Provider Layer                       |
|      Actual IO (Bundle / Local / Remote / Editor)    |
+------------------------------------------------------+
```

Strict unidirectional dependency: each layer only calls the layer below it. No cross-layer direct access.

---

## 3. API Layer -- External Interface

### 3.1 Design Philosophy

Callers only perceive two things: **address (string)** and **handle (ContentHandle\<T\>)**. They are unaware of Bundle, Provider, Location, or Operation. This is the most important encapsulation boundary.

The entire system is accessed through a single **static class** `HyperContent`. There is no Manager, no singleton to create, no MonoBehaviour to attach. Callers just call static methods.

### 3.2 Initialization

HyperContent requires **explicit initialization** before any Load/Release API. Call `Initialize` or `InitializeAsync` at startup; calling `LoadAsync` without initializing will log an error and return a default handle.

| Approach | When to use |
|----------|-------------|
| **Callback** | `HyperContent.Initialize(pOnComplete: ok => { ... })` — non-blocking, callback when done |
| **Async/await** | `await HyperContent.InitializeAsync()` — use in async context (e.g. `async Start()`) |

```
HyperContent.LoadAsync("addr") called
  └─ IsInitialized ?
       ├─ No  → CheckInitialized() logs error, returns default(ContentHandle<T>)
       └─ Yes → proceed with load
```

Default catalog resolution (settings.json flow):

| Environment | Base Path | Catalog Resolution |
|-------------|-----------|-------------------|
| **Editor** | `HyperContentBuild/{Platform}/` | `CatalogLocator.ResolveLocalCatalogAsync()` loads `settings.json`, returns package or cached catalog path |
| **Runtime** | `StreamingAssets/hc/` | Same: `settings.json` baked in APK; CatalogLocator resolves Package / Cached |

- Package catalog: `StreamingAssets/hc/HyperCatalog.bin` (fixed name)
- Cached catalog: `persistentDataPath/HyperContent/hc/HyperCatalog_{buildVersion}.bin` (versioned; paths come from `settings.json` `cachedCatalogPath` / `cachedCatalogHashPath`; `hc` is the cache catalog subfolder from `HyperContentPaths.CacheCatalogPath`)
- Remote catalog / hash: **relative paths** in `settings.json` (`remoteCatalogRelativePath`, `remoteCatalogHashRelativePath`) — typically **versioned filenames only** under `{platform}/` on the CDN (e.g. `HyperCatalog_{ver}.hash`), matching `ServerData/{Platform}/` publish output, **not** a mirror of `StreamingAssets/hc/`. Full request URLs are built at HTTP time via `HyperContentPaths.CombineRemoteCdnRequestUrl` using `RuntimeSettings.remoteBundleBaseUrl` and/or **`HyperContent.SetRemoteBundleBaseUrl`** (see [CONTENT_UPDATE_FLOW.md](CONTENT_UPDATE_FLOW.md) §2.2).
- **Init does not download** remote catalog or bundles: bundle-mode init runs **`CatalogLocator.ResolveLocalCatalogAsync`** only (disk). **`HyperContent.TryUpdateCachedCatalogOnDiskAsync`** performs hash/catalog HTTP **to disk only** and **does not** require prior init. **Recommended:** call it **before** the first **`Initialize`** so init loads the updated cache (usually **no** **`ReloadRuntimeCatalogAsync`**). **Alternatively:** call it **after** init; if **`Applied`**, use **`ReloadRuntimeCatalogAsync`** so in-memory `ICatalog` matches disk before dependent bundle work.

See [BUILD_LIFECYCLE.md](BUILD_LIFECYCLE.md) for build and catalog flow; [TODO.md](TODO.md) for planned CATALOG_LIFECYCLE.md.

### 3.3 HyperContent (Static Facade)

Current API (as implemented in `Runtime/Operations/HyperContent.cs`):

```csharp
// namespace: com.igg.hypercontent — see HyperContent.cs for full signatures.
// CatalogDiskUpdateResult: com.igg.hypercontent.runtime. CancellationToken: System.Threading.
public static class HyperContent
{
    // -- Initialization (required before Load/Release) ----------------
    static void Initialize(Action<bool> pOnComplete = null);
    static Awaitable<bool> InitializeAsync();

    // -- CDN base (O(1); may run before Initialize) -------------------
    static bool SetRemoteBundleBaseUrl(string pBaseUrl);
    static bool TryResolveKey(object pKey, out string pAddress);

    // -- Query-only availability (do not use QueryOnly on LoadAsync; see CONVENTIONS §1.3) --
    static LoadAvailabilityResult QueryLoadAvailability<T>(string pAddress) where T : Object;
    static LoadAvailabilityResult QueryLoadAvailability<T>(object pKey) where T : Object;

    // -- Asset Loading (each entry point also has overload with LoadNetworkOptions) --
    static ContentHandle<T> LoadAsync<T>(object pKey) where T : Object; // + LoadNetworkOptions overload
    static ContentHandle<T> LoadAsync<T>(string pAddress) where T : Object;
    static ContentHandle<T> LoadAsync<T>(AssetReference<T> pReference) where T : Object;
    static ContentHandle<T> LoadAsync<T>(AssetReference pReference) where T : Object;
    static ContentHandle<GameObject> InstantiateAsync(string pAddress, Transform pParent = null);
    static ContentHandle<GameObject> InstantiateAsync(AssetReference<GameObject> pReference, Transform pParent = null);
    // Additional InstantiateAsync overloads (object key, world space, position/rotation): see HyperContent.cs
    static ContentHandle<SceneInstance> LoadSceneAsync(string pAddress, LoadSceneMode pMode = LoadSceneMode.Single); // + LoadNetworkOptions overload

    // -- Release (both overloads available) --------------------------
    static void Release(ContentHandle handle);           // Non-generic: any handle
    static void Release<T>(ContentHandle<T> handle);    // Generic overload (implicit cast from ContentHandle<T>)
    static void ReleaseInstance(GameObject pInstance);

    // -- Resource Query ----------------------------------------------
    static bool HasResource(string address);
    static bool HasResource(object pKey);

    // -- Lifecycle ---------------------------------------------------
    static void Shutdown();

    // -- Disk catalog hot-update + in-memory reload (see INITIALIZATION_FLOW §3 / §8) --
    static Awaitable<CatalogDiskUpdateResult> TryUpdateCachedCatalogOnDiskAsync(CancellationToken pToken = default);
    static void TryUpdateCachedCatalogOnDiskAsync(Action<CatalogDiskUpdateResult> pOnComplete, CancellationToken pToken = default);
    static Awaitable<bool> ReloadRuntimeCatalogAsync(CancellationToken pToken = default);

    // -- Pending downloads (scope: All vs Blocking-only) ------------
    static bool HasPendingDownloads();
    static bool HasPendingDownloads(PendingBundleQueryScope pScope);
    static DownloadCheckResult GetPendingDownloads();
    static DownloadCheckResult GetPendingDownloads(PendingBundleQueryScope pScope);
    static DownloadCheckResult GetDownloadsForAsset(string pAssetPath);
    static DownloadCheckResult GetDownloadsForAssets(IEnumerable<string> pAssetPaths);

    // -- Download (batch CancellationToken: CONVENTIONS §1.6) --------
    static void DownloadAllUpdatesAsync(Action<DownloadProgress> pOnProgress = null, Action<DownloadResult> pOnComplete = null, CancellationToken pCancellationToken = default);
    static void DownloadAllBlockingBundlesAsync(Action<DownloadProgress> pOnProgress = null, Action<DownloadResult> pOnComplete = null, CancellationToken pCancellationToken = default);
    static void DownloadBundlesAsync(IEnumerable<string> pBundleNames, Action<DownloadProgress> pOnProgress = null, Action<DownloadResult> pOnComplete = null, CancellationToken pCancellationToken = default);

    // -- Global queue progress (aggregate) -------------------------
    static void RegisterDownloadQueueProgressListener(IDownloadQueueProgressListener pListener);
    static void UnregisterDownloadQueueProgressListener(IDownloadQueueProgressListener pListener);

    // -- State -------------------------------------------------------
    static bool IsInitialized { get; }

    // -- Events ------------------------------------------------------
    static event Action<string, Exception> OnLoadFailed;
    static event Action<string> OnLoadSucceeded;
}
```

**Load network behavior (runtime plan / Phase 6):** Types live in `Runtime/Data/LoadNetworkTypes.cs` — `LoadAssetNetworkMode` (`Silent` / `PromptBeforeDownload` / `QueryOnly`), `LoadNetworkOptions` (mode + optional prompt callback), `MissingBundlePromptInfo` (missing bundle count + total bytes from catalog only), `LoadAvailabilityResult`, and `PendingBundleQueryScope` (`All` / `BlockingOnly`). Overloads that take `LoadNetworkOptions` exist for load entry points; omitting options stays semantically `Silent` per [CONVENTIONS.md §1.3](CONVENTIONS.md). **`QueryOnly` is not used via `LoadAsync` / `InstantiateAsync` / `LoadSceneAsync`** — those APIs log `SYSTEM_INVALID_STATE` and return an invalid handle; use **`QueryLoadAvailability<T>`** instead ([CONVENTIONS.md §1.3](CONVENTIONS.md), [LOAD_RELEASE_FLOW.md](LOAD_RELEASE_FLOW.md)).

**Catalog disk update — `CatalogDiskUpdateResult` (aligned with code):**

| `CatalogDiskUpdateKind` | Typical `ErrorCode` | Meaning |
|-------------------------|---------------------|---------|
| `NoChange` | 1006 | Remote hash matches cache; nothing written. |
| `Applied` | 1007 | New `.bin` + hash written under cache root. |
| `SkippedNoRemote` | 1009 | `HasRemoteCatalog` false in settings; **no** network — **not** a failure. |
| `Failed` | 1008 (or transport/settings codes) | Hash/catalog HTTP, disk, cancel, or misconfiguration when an update was attempted. |

Internal locator: `CatalogLocator.CheckAndDownloadCatalogUpdateAsync`; does not touch `LocalContentCatalog` in memory.

Planned / next-version APIs: `PreloadDependenciesAsync`, `GetDownloadSizeAsync`, `LoadCatalogAsync`, and **Low-priority background prefetch** (runtime plan §5.0). See [TODO.md](TODO.md).

### 3.5 AssetReference (GUID-based reference)

Serializable asset references store a **32-char lowercase hex GUID** as the load key instead of a path string. This survives asset moves and renames. Two types exist:

- **`AssetReference`** — Non-generic; use with `HyperContent.LoadAsync<T>(pReference)` when the field type is the base class.
- **`AssetReference<T>`** — Generic; use with `HyperContent.LoadAsync(pReference)` so the type is inferred from the reference.

Both are **pure data classes** (no load/release logic). Loading is done only through the HyperContent facade so the user API stays unchanged. The Catalog resolves the GUID via its internal `_guidToAssetIndex` O(1) dictionary (see [CATALOG_SCHEMA.md §2.8](CATALOG_SCHEMA.md)). The Editor provides `AssetReferenceDrawer` (Owner1) so Inspector fields show an ObjectField and the selected asset's GUID is written to the serialized field.

Internal implementation (`HyperContentImpl`) is a pure C# class. No MonoBehaviour is required.

### 3.4 ContentHandle\<T\> -- Unified Handle

The handle is the **sole contract** between user and system, serving three roles:
1. Observation window for async operation (progress, completion event)
2. Result holder (Result property)
3. **Release credential** (system locates Operation via internal OperationId)

All operation types use the same generic handle -- no separate types for asset/scene/preload:

```csharp
public struct ContentHandle<T> : IEnumerator, IEquatable<ContentHandle<T>>
{
    // -- State Query -------------------------------------------------
    public bool   IsValid   { get; }  // Handle still valid (not released or uninitialized)
    public bool   IsDone    { get; }  // Operation completed (success or failure)
    public bool   IsSuccess { get; }  // Completed and succeeded
    public float  Progress  { get; }  // 0.0 ~ 1.0, weighted average including dependency bundles
    public string Error     { get; }  // Failure reason, null on success

    // -- Result ------------------------------------------------------
    public T Result { get; }          // Valid when IsDone && IsSuccess, throws otherwise

    // -- Async Collaboration -----------------------------------------
    public event Action<ContentHandle<T>> Completed;  // Fires on success or failure
    public ContentHandleAwaiter<T> GetAwaiter();      // Supports await

    // -- IEnumerator (coroutine yield return) ------------------------
    object IEnumerator.Current => null;
    bool IEnumerator.MoveNext() => !IsDone;
    void IEnumerator.Reset() { }

    // -- Equality ----------------------------------------------------
    public bool Equals(ContentHandle<T> other) => OperationId == other.OperationId;

    // -- Internal (user does not see) --------------------------------
    internal int OperationId { get; }
}
```

#### Special Type Parameters

```csharp
/// Scene load result wrapper.
public readonly struct SceneInstance
{
    public Scene  Scene      { get; }
    public bool   IsValid    => Scene.IsValid();
    internal AsyncOperation UnloadOperation { get; }
}

/// Void placeholder for operations that return no asset object (e.g. PreloadDependenciesAsync).
public readonly struct VoidResult { }
```

#### Usage Examples

```csharp
using com.igg.hypercontent;

await HyperContent.InitializeAsync();  // Required before any Load/Release

// Callback style
var handle = HyperContent.LoadAsync<Texture2D>("UI/Avatar");
handle.Completed += h =>
{
    if (h.IsSuccess) image.texture = h.Result;
    else Debug.LogError(h.Error);
};

// await style
var handle = HyperContent.LoadAsync<Texture2D>("UI/Avatar");
await handle;
image.texture = handle.Result;

// Coroutine style (IEnumerator)
IEnumerator LoadRoutine()
{
    var handle = HyperContent.LoadAsync<Texture2D>("UI/Avatar");
    yield return handle;
    image.texture = handle.Result;
}

// Scene loading (identical usage pattern)
var sceneHandle = HyperContent.LoadSceneAsync("Level/Stage01");
await sceneHandle;
Debug.Log(sceneHandle.Result.Scene.name);

// Release (unified entry for all types)
HyperContent.Release(handle);
HyperContent.Release(sceneHandle);   // internally calls UnloadSceneAsync

// AssetReference (GUID-based; survives asset moves) — [SerializeField] AssetReference<Texture2D> _iconRef;
var refHandle = HyperContent.LoadAsync(_iconRef);   // type inferred from _iconRef
await refHandle;
image.texture = refHandle.Result;
HyperContent.Release(refHandle);
```

---

## 4. Catalog Layer -- Address to Location Translation

### 4.1 ResourceLocation

Location is the core internal data structure that fully describes "where this asset is, how to load it, what it depends on":

```csharp
public sealed class ResourceLocation
{
    public string Address { get; }                              // User-side address, e.g. "ui/main_panel"
    public string InternalId { get; }                           // Bundle: StreamingAssets = bundle key; Remote = CDN-relative path (same as BundleInfo.RemoteRelativePath), not a full URL
    public string ProviderId { get; }                           // Routes to which Provider
    public Type ResourceType { get; }                           // Asset type
    public IReadOnlyList<ResourceLocation> Dependencies { get; } // Dependent Locations (e.g. bundle Locations)
    public object Data { get; }                                 // Extra data (CRC, Hash, file size, etc.)
    public int LocationHash { get; }                            // Key for Operation cache
}
```

### 4.2 ICatalog

```csharp
public interface ICatalog
{
    // Core: address (GUID or Name) -> ResourceLocation tree
    bool TryGetLocations(string address, Type type, out IList<ResourceLocation> locations);
    string Version { get; }
    bool IsValid { get; }

    // Lifecycle
    bool Initialize(string source);
    void Release();

    // Content management (Owner3 update pipeline)
    bool TryGetBundleInfo(string bundleName, out BundleInfo bundleInfo);
    IEnumerable<string> GetAllBundleNames();
}
```

The default implementation (`LocalContentCatalog`) deserializes JSON into CatalogSchema, **rejects** any catalog whose `schemaVersion` ≠ `CatalogSchema.CurrentSchemaVersion` (`CATALOG_VERSION_MISMATCH`), builds O(1) Dictionary lookup structures at Initialize time, and constructs flat ResourceLocation trees on `TryGetLocations`. Catalog must be successfully initialized before any load operation -- a null or invalid catalog prevents system startup.

**Dependency resolution mode:** the dependency bundles placed in each asset's flat Location tree follow the `DependencyLoadMode` frozen into `settings.json` at Full Build time. **AssetLevel** (default) uses the asset's own per-asset dependency set (`AssetRecordEntry.dependencyBundles`); a record without that data fails loudly with `CATALOG_ASSET_DEPS_MISSING` (1010) rather than falling back. **BundleLevel** (rollback/A-B) uses the owning bundle's full bundle-level closure. The asset-level set is built at build time from SBP data: one-hop bundles over the in-bundle entry frontier, transitive expansion via `BundleDependencies`, clamp to `AssetLevel ⊆ BundleLevel`, plus SpriteAtlas indirect-dependency recovery — [CATALOG_SCHEMA.md §2.4 / §2.4.1 / §2.4.2](CATALOG_SCHEMA.md); runtime contents detail — [LOAD_RELEASE_FLOW.md §1.1](LOAD_RELEASE_FLOW.md).

For hot-update, only Catalog data needs replacement -- no restart required.

---

## 5. Operation Layer -- Async DAG Execution Engine (Core)

This is the most critical layer, responsible for:
- Building the resource dependency DAG (Directed Acyclic Graph)
- Executing each node's load in topological order asynchronously
- Managing each Operation's lifecycle via reference counting
- Operation caching -- guarantees a single Operation instance per Location globally

### 5.1 Operation State Machine

```
             +----------+
             |   None   |  Initial state
             +----+-----+
                  | Created and submitted
             +----v-----+
             | Pending  |  Waiting for all dependency Operations to complete
             +----+-----+
                  | All dependencies Succeeded
             +----v------+
             | InProgress|  Provider executes actual IO
             +----+------+
           +------+-------+
      +----v----+    +----v----+
      |Succeeded|    | Failed  |
      +----+----+    +----+----+
           |              |
           +------+-------+
             +----v----+
             |  Alive  |  RefCount > 0, stays alive
             +----+----+
                  | RefCount == 0
             +----v-----+
             | Disposed |  Triggers Provider.Release, recursively releases dependencies
             +----------+
```

### 5.2 AsyncOperationBase

```csharp
public abstract class AsyncOperationBase
{
    internal int              RefCount;
    public OperationStatus    Status { get; internal set; }
    internal int              LocationHash;
    internal ResourceLocation Location;
    internal List<AsyncOperationBase> Dependencies = new();
    internal int              PendingDepCount;
    public Exception          Exception { get; internal set; }
    internal float            ProgressValue;

    public event Action<AsyncOperationBase> OnCompleted;

    internal abstract void Execute(IContentProvider provider, ProvideHandle handle);
    internal virtual void Dispose() { }
    public virtual float GetProgress();
}

public sealed class AssetOperation<T> : AsyncOperationBase where T : UnityEngine.Object
{
    public T Result { get; internal set; }
}
```

### 5.3 OperationCache

Guarantees a single Operation instance per LocationHash. Multiple loads only increment RefCount.

```csharp
internal sealed class OperationCache
{
    private readonly Dictionary<int, AsyncOperationBase> _cache = new();

    internal AsyncOperationBase GetOrCreate(ResourceLocation location,
                                            Func<AsyncOperationBase> factory)
    {
        if (_cache.TryGetValue(location.LocationHash, out var existing))
        {
            existing.RefCount++;
            return existing;
        }
        var op = factory();
        op.RefCount = 1;
        op.LocationHash = location.LocationHash;
        _cache[location.LocationHash] = op;
        return op;
    }

    internal void Release(AsyncOperationBase op)
    {
        op.RefCount--;
        if (op.RefCount > 0) return;
        _cache.Remove(op.LocationHash);
        foreach (var dep in op.Dependencies)
            Release(dep);
        op.Dispose();
    }
}
```

### 5.4 ResourceManager (DAG Scheduler)

```csharp
internal sealed class ResourceManager
{
    private readonly OperationCache   _cache;
    private readonly ProviderRegistry _providers;

    internal AssetOperation<T> LoadAsync<T>(ResourceLocation location) where T : UnityEngine.Object
    {
        var op = (AssetOperation<T>)_cache.GetOrCreate(location,
            () => new AssetOperation<T>(location));

        if (op.Status != OperationStatus.None) return op;

        StartOperation(op, location);
        return op;
    }

    internal AsyncOperationBase LoadDependency(ResourceLocation location)
    {
        var op = _cache.GetOrCreate(location, () => new AssetOperation<UnityEngine.Object>(location));
        if (op.Status != OperationStatus.None) return op;
        StartOperation(op, location);
        return op;
    }

    private void StartOperation(AsyncOperationBase op, ResourceLocation location)
    {
        op.PendingDepCount = location.Dependencies.Count;
        if (location.Dependencies.Count == 0)
            ScheduleExecute(op);
        else
        {
            foreach (var depLocation in location.Dependencies)
            {
                var depOp = LoadDependency(depLocation);
                op.Dependencies.Add(depOp);
                if (depOp.Status == OperationStatus.Succeeded)
                    op.PendingDepCount--;
                else if (depOp.Status == OperationStatus.Failed)
                    HandleDependencyFailure(op, depOp);
                else
                    depOp.OnCompleted += completedDep => OnDependencyCompleted(op, completedDep);
            }
            if (op.PendingDepCount == 0) ScheduleExecute(op);
        }
        if (op.Status == OperationStatus.None) op.Status = OperationStatus.Pending;
    }

    private void ScheduleExecute(AsyncOperationBase op)
    {
        op.Status = OperationStatus.InProgress;
        var provider = _providers.Get(op.Location.ProviderId);
        var handle = new ProvideHandle(op, this);
        op.Execute(provider, handle);
    }

    internal void Release(AsyncOperationBase op) => _cache.Release(op);
}
```

---

## 6. Provider Layer -- Pluggable IO

### 6.1 IContentProvider

```csharp
public interface IContentProvider
{
    string ProviderId { get; }
    void Provide(ProvideHandle handle);
    void Release(ProvideHandle handle);
}
```

### 6.2 ProvideHandle (Bridge)

```csharp
public sealed class ProvideHandle
{
    public ResourceLocation Location { get; }

    public void Complete<T>(T result) where T : UnityEngine.Object;
    public void Complete();                                    // Void completion (no result)
    public void CompleteAsBundle(AssetBundle bundle);           // Bundle completion (Provider convenience)
    public void Fail(Exception exception);
    public void UpdateProgress(float progress);
    public TDep GetDependencyResult<TDep>(int depIndex) where TDep : class;
}
```

### 6.3 ProviderRegistry

```csharp
internal sealed class ProviderRegistry
{
    private readonly Dictionary<string, IContentProvider> _map = new();
    public void Register(IContentProvider provider) => _map[provider.ProviderId] = provider;
    public IContentProvider Get(string providerId)
        => _map.TryGetValue(providerId, out var p)
            ? p
            : throw new InvalidOperationException($"Provider not found: {providerId}");
}
```

### 6.4 Built-in Providers

| Provider | ProviderId | Responsibility |
|----------|------------|----------------|
| `BundleFileProvider` | `"BundleFileProvider"` | Load `.bundle` file into memory, produces `AssetBundle` object (pure IO) |
| `PlayAssetDeliveryBundleProvider` | `"BundleFileProvider"` | Android PAD variant — loads bundles from AAB via PAD API with offset; falls back to `BundleFileProvider`. Shares the same ProviderId for transparent catalog routing. Conditionally compiled: `#if UNITY_ANDROID && GOOGLE_PLAY_ASSET_DELIVERY` |
| `BundleAssetExtractor` | `"BundleAssetExtractor"` | Extract concrete asset from in-memory `AssetBundle`, produces `T` (no IO) |
| `LocalFileProvider` | `"LocalFileProvider"` | Load directly from local filesystem (Editor mode, no Bundle) |
| `RemoteBundleProvider` | `"RemoteBundleProvider"` | Enqueues **`IBundleDownloadQueue`** at **`BundleDownloadPriority.High`** (merge-safe with load path); on completion writes via **`IBundleStore`** and loads the bundle — **does not** call `IBundleTransport` or `UnityWebRequest` directly |
| `SceneProvider` | `"SceneProvider"` | Async scene loading |

**`BundleFileProvider` — local disk path resolution:** For bundle `ResourceLocation`s, `InternalId` is the **catalog bundle name** (no file extension). When resolving a file on disk (`IBundleStore`, optional bundle base path, or `StreamingAssets`), the provider uses `internalId + NamingRules.BUNDLE_FILE_EXTENSION` (`.bundle`) unless `InternalId` already ends with that suffix. There is **no** legacy path that tries extensionless filenames: a miss is reported via `HCLogger.LogError` so build/APK/pack mismatches surface clearly in QA. This matches the build pipeline (logical name in catalog, `.bundle` on disk) and Android `aaptOptions.noCompress` so `AssetBundle.LoadFromFile*` can use mmap-friendly I/O.

### 6.4.1 Global bundle download queue (contract + implementation)

| Piece | Location / behavior |
|-------|---------------------|
| **Contract** | **`IBundleDownloadQueue`** — `Runtime/Core/IBundleDownloadQueue.cs` (Owner0): priorities **High / Normal / Low**, merge by normalized **`RemoteRelativePath`**, **`IDownloadQueueProgressListener`**, **`BundleDownloadEnqueueOptions`** (per-item progress/complete, optional **`CancellationToken`**). |
| **Implementation** | **`BundleDownloadQueue`** — `Runtime/Bundle/BundleDownloadQueue.cs` (Owner3): single caller of **`IBundleTransport.DownloadAsync`** for bundle bytes; fan-out **`OnProgress` / `OnComplete`** to all waiters sharing one physical download. |
| **Transport** | **`HttpBundleTransport`** — `Runtime/Bundle/HttpBundleTransport.cs`; constructed in bundle-mode init with effective CDN base (**`HyperContent.SetRemoteBundleBaseUrl`** overrides `RuntimeSettings.remoteBundleBaseUrl`). |
| **Wiring** | **`HyperContentImpl` ctor** (`Runtime/Operations/HyperContentImpl.cs`): if `bundleTransport != null`, `_bundleDownloadQueue = new BundleDownloadQueue(bundleTransport)` and **`RemoteBundleProvider`** is registered with that queue; if no transport (e.g. AssetDatabase play mode), queue and remote provider are omitted. |
| **Batch downloads** | **`BundleDownloadManager`** — `Runtime/Catalog/BundleDownloadManager.cs` (Owner3): **only** calls **`Enqueue`** — never **`IBundleTransport`** directly. **`DownloadAllUpdatesAsync`** → Normal; **`DownloadAllBlockingBundlesAsync`** → High; **`DownloadBundlesAsync`** → Normal (default). |
| **Facade progress** | **`HyperContent.RegisterDownloadQueueProgressListener`** forwards to **`HyperContentImpl.Current.BundleDownloadQueue`** when initialized (Owner2 facade → Owner3 queue). |

See [CONVENTIONS.md §1.2](CONVENTIONS.md) (catalog/hash split), §1.5–§1.6 (progress + batch cancel).

### 6.4.2 Catalog / hash HTTP (outside the global queue)

**Remote catalog hash and catalog `.bin` downloads are intentionally separate** from the bundle pipeline. **`CatalogLocator.CheckAndDownloadCatalogUpdateAsync`** (`Runtime/Catalog/CatalogLocator.cs`) is invoked by the facade **`HyperContent.TryUpdateCachedCatalogOnDiskAsync`**; it uses **direct `UnityWebRequest`** inside **`CatalogLocator`** — **not** **`IBundleDownloadQueue`** and **not** **`HttpBundleTransport`**. Rationale: small, infrequent, control-flow-critical metadata I/O should not compete with bulk bundle traffic or share the same priority/merge rules. Bundle bytes remain the **only** payload that goes through **`BundleDownloadQueue`** + **`HttpBundleTransport`**.

**Facade outcome mapping** (see §3.3 table): locator **`CatalogLocatorUpdateStatus.SkippedNoRemote`** → **`CatalogDiskUpdateKind.SkippedNoRemote`** + **`ErrorCode.CATALOG_DISK_UPDATE_SKIPPED_NO_REMOTE` (1009)** — distinct from **`Failed`/1008**.

### 6.4.3 Operation-scoped progress + batch cancellation (**Owner3**)

Runtime plan **§5.2** (global vs operation-scoped progress) and **§6.A** (batch cancel) are implemented in **`BundleDownloadQueue`** + **`BundleDownloadManager`**:

| Topic | Responsibility |
|-------|----------------|
| **Global progress** | `DownloadQueueProgressSnapshot` + `IDownloadQueueProgressListener`; facade **`HyperContent.RegisterDownloadQueueProgressListener`** (Owner2) forwards to the active queue. |
| **Batch-scoped progress** | `DownloadAllUpdatesAsync` / `DownloadAllBlockingBundlesAsync` / `DownloadBundlesAsync` **`pOnProgress`** — numerator/denominator = bundles in **that** call only (`BundleDownloadManager`). |
| **Same-URL merge** | One physical `IBundleTransport` download; `OnProgress` / `OnComplete` fan-out to every `BundleDownloadEnqueueOptions` waiter; per-waiter **`CancellationToken`** can drop a waiter without aborting HTTP until the **last** waiter for that URL is gone (`BundleDownloadQueue`). |
| **Batch `CancellationToken`** | Passed from facade → manager → each enqueue option; `DownloadResult.cancelled` + **`ErrorCode.OPERATION_CANCELLED` (5009)** on cancelled fetches. See [CONVENTIONS.md §1.5–§1.6](CONVENTIONS.md). |
| **Per-load progress** | Optional future hook (e.g. `LoadNetworkOptions` / `ContentHandle`) — **not** required for batch; Owner0/Owner2 when introduced. |

> **Note**: `PlayAssetDeliveryBundleProvider` replaces `BundleFileProvider` at registration time on Android runtime builds when the `com.google.play.assetdelivery` package is present.

---

## 7. Instance Lifecycle Tracking

`InstantiateAsync` creates GameObjects that need separate tracking, because their lifecycle differs from the source asset:

```csharp
internal sealed class InstanceRegistry
{
    private readonly Dictionary<int, AsyncOperationBase> _map = new();

    internal void Track(GameObject instance, AsyncOperationBase op)
    {
        _map[instance.GetInstanceID()] = op;
        op.RefCount++;
    }

    internal void Release(GameObject instance, ResourceManager manager)
    {
        var id = instance.GetInstanceID();
        if (!_map.TryGetValue(id, out var op))
        {
            Debug.LogWarning($"[HyperContent] ReleaseInstance: instance {instance.name} not tracked.");
            return;
        }
        _map.Remove(id);
        Object.Destroy(instance);
        manager.Release(op);
    }
}
```

---

## 8. Extension Points

| Extension | Interface / API | Use Case |
|-----------|-----------------|----------|
| Custom Provider | Implement **`IContentProvider`**, register on **`ProviderRegistry`** inside a custom bootstrap (fork **`HyperContentImpl`** wiring) or future registration API | Custom decryption, alternate IO |
| Custom Catalog | Implement **`ICatalog`** | Custom catalog format, editor simulation |

**Not exposed today:** There is no public **`ResourceManager.AddInterceptor`**, no **`HyperContent.Update()`** tick API, and **`ContentHandle<T>`** is a **struct** — not designed for inheritance. Use **`HCLogger`**, global queue listeners, or wrap the **`HyperContent`** facade at the game layer for metrics/A/B.

---

## 9. Content Update Architecture

HyperContent uses a **two-layer incremental architecture** for content updates, inspired by Unity Addressables but simplified:

```
┌─────────────────────────────────────────────────────────────────┐
│             Layer 1: SBP Build Cache                            │
│             (Build-level incremental)                           │
│                                                                 │
│  Engine: ContentPipeline.BuildAssetBundles (full SBP)            │
│  Scope: Per-asset and per-bundle build artifacts                │
│  Effect: Skips dependency calc, serialization, and archiving    │
│          for unchanged content; provides object-level accurate  │
│          bundle dependencies via IBundleBuildResults.BundleInfos │
│  Result: Build speed + dependency accuracy                      │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│             Layer 2: Build Manifest + Mixed Catalog             │
│             (Download-level incremental)                        │
│                                                                 │
│  Engine: ContentChangeDetector + UpdateBuildExecutor             │
│  Scope: Per-asset change detection, per-bundle catalog routing  │
│  Effect: Changed assets → new small Remote bundles              │
│          Unchanged assets → original Local APK bundles          │
│  Result: Minimal download size — only changed content           │
└─────────────────────────────────────────────────────────────────┘
```

### Build Approach vs Addressables

HyperContent follows the same **full-context build + revert** principle as Addressables: create the update layout, build with the full current bundle graph (so Unity correctly resolves dependency ownership), then revert unchanged assets' catalog/bundle references back to the original Full Build state. APK bundles remain immutable in StreamingAssets throughout.

The simplification is in **state management and developer experience**, not in skipping full-context dependency resolution:

| Aspect | Addressables | HyperContent |
|--------|-------------|--------------|
| **State format** | `addressables_content_state.bin` (BinaryFormatter, opaque binary) | `build_manifest.json` (JSON, human-readable, easy to inspect and debug) |
| **State baseline** | `.bin` may be regenerated or patched across updates | Single immutable manifest from Full Build; all Update Builds diff against the same baseline |
| **Catalog update strategy** | Patches previous catalog state | Each Update Build regenerates catalog from scratch (Full Build baseline + current changes); no accumulated state drift |
| **Group configuration** | Requires per-group "Prevent Updates" (StaticContent) flag | All assets in the build plan are candidates for change detection; no extra group-level configuration |
| **Publish output** | All rebuilt bundles in output (cache reduces actual disk write) | Same full-context build; only update bundles and mixed catalog are published to CDN |

For the detailed build flow (Phase A–D), see [CONTENT_UPDATE_BUILD_FLOW.md](CONTENT_UPDATE_BUILD_FLOW.md).

### Catalog `contentLocation` Routing

Each `BundleRecordEntry` in the catalog carries a `contentLocation` field:
- `3` (StreamingAssets) — Load from APK, no download needed
- `2` (Remote) — Download from CDN, cache locally

Full Build: all bundles = StreamingAssets. Update Build: unchanged = StreamingAssets, changed = Remote.

At runtime, `LocalContentCatalog` reads `contentLocation` and routes to `BundleFileProvider` (local) or `RemoteBundleProvider` (remote). Bundle records carry **CDN-relative** paths only; the CDN base is the module-level **`HyperContent.SetRemoteBundleBaseUrl`** (and/or `RuntimeSettings.remoteBundleBaseUrl` from `settings.json`), applied to `HttpBundleTransport` and used when composing request URLs (`HyperContentPaths.CombineRemoteCdnRequestUrl`) — the catalog itself stores no absolute URLs.

For the full build-side update flow, see [CONTENT_UPDATE_BUILD_FLOW.md](CONTENT_UPDATE_BUILD_FLOW.md).
For the runtime update flow, see [CONTENT_UPDATE_FLOW.md](CONTENT_UPDATE_FLOW.md).

---

## 10. Open Topics

The following topics are deferred and will be addressed in subsequent versions:

1. ~~**Catalog hot-update full flow**~~ — Addressed: see §9 and [CONTENT_UPDATE_BUILD_FLOW.md](CONTENT_UPDATE_BUILD_FLOW.md)
2. **Download cache — advanced policy** -- **`LocalBundleStore`** already implements atomic writes, hash verify, metadata, and **LRU prune** against a max size (default 1GB); further work may include configurable quotas per platform, tiering, or background scrub policies
3. **Bundle encryption/decryption** -- insertion point design in Provider layer
4. **Operation timeout & retry** -- automatic retry mechanism under network jitter
5. **Memory budget management** -- LRU unload strategy when exceeding budget
6. **Multi-Catalog merge** -- DLC, hot-update pack and main pack Catalog merge and priority