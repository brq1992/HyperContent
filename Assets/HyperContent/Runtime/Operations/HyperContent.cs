using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using com.igg.hypercontent.runtime;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent
{
    /// <summary>
    /// Static facade — sole public entry point for the HyperContent system.
    /// Requires explicit initialization before any Load/Release API calls.
    /// Call Initialize(pOnComplete: callback) or await InitializeAsync() at startup.
    ///
    /// Usage:
    ///   using com.igg.hypercontent;
    ///   await HyperContent.InitializeAsync();
    ///   var op = HyperContent.LoadAsync&lt;Texture2D&gt;("UI/Avatar");
    ///   HyperContent.Release(op);
    ///
    /// See ARCHITECTURE.md section 3 (API Layer).
    /// </summary>
    public static class HyperContent
    {
        private static HyperContentImpl Impl => HyperContentImpl.Current;
        private static BundleDownloadManager _downloadManager;
        // volatile to remove a theoretical reordering/caching hazard around the init guard. Unity runs
        // on the main thread so there is no real race today, but the cost is negligible.
        private static volatile bool _isInitializing;

        // Subscriptions made before Impl exists (e.g. business code in Awake) are buffered here and
        // forwarded to Impl once initialization completes, so early listeners aren't silently dropped.
        private static event Action<string, Exception> _pendingLoadFailed;
        private static event Action<string> _pendingLoadSucceeded;

        /// <summary>
        /// Module-level CDN base for bundle and catalog HTTP (O(1)). Filled by <see cref="SetRemoteBundleBaseUrl"/> at any time;
        /// applied to <see cref="HttpBundleTransport"/> on init and on each set when already initialized.
        /// </summary>
        private static string _moduleRemoteBundleBaseUrl = "";

        // ── State Query ─────────────────────────────────────────────────

        public static bool IsInitialized => Impl?.IsInitialized ?? false;

        // ── Initialization ──────────────────────────────────────────────

        /// <summary>
        /// Initialize HyperContent. Non-blocking — all network IO runs async internally via
        /// pure callback chains (no <c>async void</c>). Must be called before any Load/Release APIs.
        /// Use pOnComplete callback to know when initialization is done.
        ///
        /// Usage:
        ///   HyperContent.Initialize(pOnComplete: ok => { StartGame(); });
        ///
        /// For async/await callers, prefer InitializeAsync().
        /// </summary>
        public static void Initialize(Action<bool> pOnComplete = null)
        {
            HCLogger.LogVerbose("HyperContent.Initialize called");
            if (Impl != null && Impl.IsInitialized)
            {
                HCLogger.LogWarn("Already initialized, skipping.");
                pOnComplete?.Invoke(true);
                return;
            }
            if (_isInitializing)
            {
                HCLogger.LogWarn("Initialization already in progress, skipping duplicate call.");
                return;
            }
            _isInitializing = true;
            HCLogger.LogInfo("[Init] Step 1: Initialize chain started");
            InitializeInternal(result =>
            {
                _isInitializing = false;
                HCLogger.LogInfo($"[Init] Step 2: Initialize chain completed result={result}");
                if (!result)
                {
                    HCLogger.LogError(ErrorCode.SYSTEM_NOT_INITIALIZED,
                        "Initialization failed. Check previous errors for details.");
                }
                try { pOnComplete?.Invoke(result); }
                catch (Exception e) { HCLogger.LogError($"Initialize onComplete threw: {e.Message}"); }
            });
        }

        /// <summary>
        /// <see cref="Task{T}"/> bridge over <see cref="Initialize"/> for callers in an async context
        /// (e.g. <c>async Start()</c>). The internal pipeline is pure-callback — this overload
        /// only adds a single <see cref="TaskCompletionSource{T}"/> adapter at the boundary.
        /// </summary>
        public static Task<bool> InitializeAsync()
        {
            HCLogger.LogVerbose("HyperContent.InitializeAsync called");
            var tcs = new TaskCompletionSource<bool>();

            if (Impl != null && Impl.IsInitialized)
            {
                HCLogger.LogWarn("Already initialized, skipping.");
                tcs.TrySetResult(true);
                return tcs.Task;
            }
            if (_isInitializing)
            {
                HCLogger.LogWarn("Initialization already in progress.");
                tcs.TrySetResult(false);
                return tcs.Task;
            }

            Initialize(r => tcs.TrySetResult(r));
            return tcs.Task;
        }

        // ── Runtime configuration ──────────────────────────────────────

        /// <summary>
        /// Set or change the CDN base URL for remote bundle and catalog requests.
        /// Safe before <see cref="Initialize"/> (stored O(1)); on bundle-mode init the transport reads this value,
        /// and if already initialized the active <see cref="HttpBundleTransport"/> is updated immediately.
        /// When empty, falls back to <see cref="RuntimeSettings.remoteBundleBaseUrl"/> from settings.json for init and disk catalog update.
        /// </summary>
        /// <param name="pBaseUrl">CDN base URL **without** platform folder, e.g. "https://cdn.example.com/bundles/"</param>
        /// <returns>Always true after the value is recorded.</returns>
        public static bool SetRemoteBundleBaseUrl(string pBaseUrl)
        {
            _moduleRemoteBundleBaseUrl = pBaseUrl ?? string.Empty;
            if (Impl != null && Impl.IsInitialized && Impl.BundleTransport is HttpBundleTransport httpTransport)
                httpTransport.SetBaseUrl(_moduleRemoteBundleBaseUrl);
            HCLogger.LogInfo($"[Config] Remote bundle base URL set (module): {(_moduleRemoteBundleBaseUrl.Length > 0 ? _moduleRemoteBundleBaseUrl : "(empty — JSON fallback when applicable)")}");
            return true;
        }

        /// <summary>
        /// Effective CDN base: module override if non-empty, otherwise settings JSON value.
        /// </summary>
        private static string GetEffectiveRemoteBundleBaseUrl(RuntimeSettings pSettings)
        {
            if (!string.IsNullOrEmpty(_moduleRemoteBundleBaseUrl))
                return _moduleRemoteBundleBaseUrl;
            return pSettings != null ? (pSettings.remoteBundleBaseUrl ?? string.Empty) : string.Empty;
        }

        private static CatalogDiskUpdateResult MapCatalogLocatorOutcome(CatalogLocatorUpdateOutcome pOutcome)
        {
            switch (pOutcome.status)
            {
                case CatalogLocatorUpdateStatus.SkippedNoRemote:
                    return new CatalogDiskUpdateResult(
                        CatalogDiskUpdateKind.SkippedNoRemote,
                        ErrorCode.CATALOG_DISK_UPDATE_SKIPPED_NO_REMOTE,
                        null);
                case CatalogLocatorUpdateStatus.Unchanged:
                    return new CatalogDiskUpdateResult(
                        CatalogDiskUpdateKind.NoChange,
                        ErrorCode.CATALOG_DISK_UPDATE_NO_CHANGE,
                        null);
                case CatalogLocatorUpdateStatus.Downloaded:
                    return new CatalogDiskUpdateResult(
                        CatalogDiskUpdateKind.Applied,
                        ErrorCode.CATALOG_DISK_UPDATE_APPLIED,
                        null);
                default:
                    return new CatalogDiskUpdateResult(
                        CatalogDiskUpdateKind.Failed,
                        ErrorCode.CATALOG_DISK_UPDATE_FAILED,
                        string.IsNullOrEmpty(pOutcome.error) ? "Catalog update failed" : pOutcome.error);
            }
        }

        // ── Asset Loading ───────────────────────────────────────────────

        /// <summary>
        /// Resolves an object key to a loadable address (GUID or address string).
        /// Supports: string, HyperContent <see cref="AssetReference"/> / <see cref="AssetReference{T}"/>,
        /// and Unity Addressables <see cref="UnityEngine.AddressableAssets.AssetReference"/> (resolved via <c>AssetGUID</c>).
        /// Sub-object Addressable references still resolve to the main asset GUID only; Hyper catalog must match that keying.
        /// Used by LoadAsync(object), InstantiateAsync(object), HasResource(object) and by adapters.
        /// </summary>
        public static bool TryResolveKey(object pKey, out string pAddress)
        {
            pAddress = null;
            if (pKey == null) return false;
            if (pKey is string s && !string.IsNullOrEmpty(s))
            {
                pAddress = s;
                return true;
            }
            if (pKey is AssetReference refKey && refKey.RuntimeKeyIsValid)
            {
                pAddress = refKey.AssetGuid;
                return true;
            }
            if (pKey is UnityEngine.AddressableAssets.AssetReference addrRef && addrRef.RuntimeKeyIsValid())
            {
                pAddress = addrRef.AssetGUID;
                return !string.IsNullOrEmpty(pAddress);
            }
            return false;
        }

        /// <summary>
        /// Load asset by object key. Dispatches to string or AssetReference overloads.
        /// Use this when the key comes from AddressableManager (object) or generic APIs.
        /// </summary>
        public static ContentHandle<T> LoadAsync<T>(object pKey) where T : UnityEngine.Object
        {
            return LoadAsync<T>(pKey, null);
        }

        /// <summary>
        /// Load by object key with optional network behavior (e.g. prompt before first remote bundle download).
        /// </summary>
        public static ContentHandle<T> LoadAsync<T>(object pKey, LoadNetworkOptions pNetworkOptions) where T : UnityEngine.Object
        {
            if (!TryResolveKey(pKey, out string address))
            {
                HCLogger.LogError(ErrorCode.RESOURCE_KEY_INVALID, $"Invalid key type or value: {pKey?.GetType().Name ?? "null"}");
                return default;
            }
            return LoadAsync<T>(address, pNetworkOptions);
        }

        /// <summary>
        /// Load asset by address. Returns a ContentHandle&lt;T&gt; for observation, awaiting, and release.
        /// </summary>
        public static ContentHandle<T> LoadAsync<T>(string pAddress) where T : UnityEngine.Object
        {
            return LoadAsync<T>(pAddress, null);
        }

        /// <summary>
        /// Load asset by address with optional <see cref="LoadNetworkOptions"/> (default / omitted = <see cref="LoadAssetNetworkMode.Silent"/>).
        /// For <see cref="LoadAssetNetworkMode.QueryOnly"/> use <see cref="QueryLoadAvailability{T}"/> instead.
        /// </summary>
        public static ContentHandle<T> LoadAsync<T>(string pAddress, LoadNetworkOptions pNetworkOptions) where T : UnityEngine.Object
        {
            HCLogger.LogVerbose($"[Facade] LoadAsync<{typeof(T).Name}> [{LogFields.ADDRESS}={pAddress}]");
            if (!CheckInitialized()) return default;
            return Impl.LoadAsync<T>(pAddress, pNetworkOptions);
        }

        /// <summary>
        /// Whether the asset can be satisfied from local cache / package without downloading remote bundles (catalog snapshot only; no HTTP).
        /// </summary>
        public static LoadAvailabilityResult QueryLoadAvailability<T>(string pAddress) where T : UnityEngine.Object
        {
            if (!CheckInitialized())
            {
                return new LoadAvailabilityResult(
                    false,
                    new MissingBundlePromptInfo(0, 0, null),
                    ErrorCode.SYSTEM_NOT_INITIALIZED,
                    "HyperContent not initialized.");
            }
            return Impl.QueryLoadAvailability<T>(pAddress);
        }

        /// <summary>
        /// <see cref="QueryLoadAvailability{T}(string)"/> using string or <see cref="AssetReference"/> key.
        /// </summary>
        public static LoadAvailabilityResult QueryLoadAvailability<T>(object pKey) where T : UnityEngine.Object
        {
            if (!TryResolveKey(pKey, out string address))
            {
                return new LoadAvailabilityResult(
                    false,
                    new MissingBundlePromptInfo(0, 0, null),
                    ErrorCode.RESOURCE_KEY_INVALID,
                    "Invalid key type or value.");
            }
            return QueryLoadAvailability<T>(address);
        }

        /// <summary>
        /// Load asset by typed AssetReference. The GUID stored in the reference is used as
        /// the load key; type T is inferred from the reference so no explicit type argument
        /// is needed at the call site.
        /// Returns a ContentHandle&lt;T&gt; for observation, awaiting, and release.
        /// </summary>
        public static ContentHandle<T> LoadAsync<T>(AssetReference<T> pReference) where T : UnityEngine.Object
        {
            return LoadAsync<T>(pReference, null);
        }

        public static ContentHandle<T> LoadAsync<T>(AssetReference<T> pReference, LoadNetworkOptions pNetworkOptions) where T : UnityEngine.Object
        {
            if (pReference == null || !pReference.RuntimeKeyIsValid)
            {
                HCLogger.LogError(ErrorCode.RESOURCE_KEY_INVALID, "AssetReference is null or has no valid GUID.");
                return default;
            }
            return LoadAsync<T>(pReference.AssetGuid, pNetworkOptions);
        }

        /// <summary>
        /// Load asset by non-generic AssetReference with an explicit type argument.
        /// Use this when the reference field is declared as the base AssetReference type.
        /// Returns a ContentHandle&lt;T&gt; for observation, awaiting, and release.
        /// </summary>
        public static ContentHandle<T> LoadAsync<T>(AssetReference pReference) where T : UnityEngine.Object
        {
            return LoadAsync<T>(pReference, null);
        }

        public static ContentHandle<T> LoadAsync<T>(AssetReference pReference, LoadNetworkOptions pNetworkOptions) where T : UnityEngine.Object
        {
            if (pReference == null || !pReference.RuntimeKeyIsValid)
            {
                HCLogger.LogError(ErrorCode.RESOURCE_KEY_INVALID, "AssetReference is null or has no valid GUID.");
                return default;
            }
            return LoadAsync<T>(pReference.AssetGuid, pNetworkOptions);
        }

        /// <summary>
        /// Load + Instantiate a GameObject prefab. Returns ContentHandle&lt;GameObject&gt;.
        /// </summary>
        public static ContentHandle<GameObject> InstantiateAsync(string pAddress, Transform pParent = null)
        {
            return InstantiateAsync(pAddress, pParent, null);
        }

        public static ContentHandle<GameObject> InstantiateAsync(
            string pAddress,
            Transform pParent,
            LoadNetworkOptions pNetworkOptions)
        {
            HCLogger.LogVerbose($"[Facade] InstantiateAsync [{LogFields.ADDRESS}={pAddress}]");
            if (!CheckInitialized()) return default;
            return Impl.InstantiateAsync(pAddress, pParent, pNetworkOptions);
        }

        /// <summary>
        /// Load + Instantiate a prefab by typed AssetReference&lt;GameObject&gt;.
        /// Returns ContentHandle&lt;GameObject&gt;.
        /// </summary>
        public static ContentHandle<GameObject> InstantiateAsync(AssetReference<GameObject> pReference, Transform pParent = null)
        {
            return InstantiateAsync(pReference, pParent, null);
        }

        public static ContentHandle<GameObject> InstantiateAsync(
            AssetReference<GameObject> pReference,
            Transform pParent,
            LoadNetworkOptions pNetworkOptions)
        {
            if (pReference == null || !pReference.RuntimeKeyIsValid)
            {
                HCLogger.LogError(ErrorCode.RESOURCE_KEY_INVALID, "AssetReference is null or has no valid GUID.");
                return default;
            }
            return InstantiateAsync(pReference.AssetGuid, pParent, pNetworkOptions);
        }

        /// <summary>
        /// Load + Instantiate a prefab by object key. Dispatches to string or AssetReference.
        /// </summary>
        public static ContentHandle<GameObject> InstantiateAsync(object pKey, Transform pParent = null)
        {
            return InstantiateAsync(pKey, pParent, null);
        }

        public static ContentHandle<GameObject> InstantiateAsync(
            object pKey,
            Transform pParent,
            LoadNetworkOptions pNetworkOptions)
        {
            if (!TryResolveKey(pKey, out string address))
            {
                HCLogger.LogError(ErrorCode.RESOURCE_KEY_INVALID, $"Invalid key type or value: {pKey?.GetType().Name ?? "null"}");
                return default;
            }
            return InstantiateAsync(address, pParent, pNetworkOptions);
        }

        /// <summary>
        /// Load + Instantiate a prefab by object key, with optional world-space placement.
        /// </summary>
        public static ContentHandle<GameObject> InstantiateAsync(object pKey, Transform pParent, bool pInstantiateInWorldSpace)
        {
            return InstantiateAsync(pKey, pParent, pInstantiateInWorldSpace, null);
        }

        public static ContentHandle<GameObject> InstantiateAsync(
            object pKey,
            Transform pParent,
            bool pInstantiateInWorldSpace,
            LoadNetworkOptions pNetworkOptions)
        {
            if (!TryResolveKey(pKey, out string address))
            {
                HCLogger.LogError(ErrorCode.RESOURCE_KEY_INVALID, $"Invalid key type or value: {pKey?.GetType().Name ?? "null"}");
                return default;
            }
            if (!CheckInitialized()) return default;
            return Impl.InstantiateAsync(address, pParent, pInstantiateInWorldSpace, pNetworkOptions);
        }

        /// <summary>
        /// Load + Instantiate a prefab by object key at the given position and rotation.
        /// </summary>
        public static ContentHandle<GameObject> InstantiateAsync(object pKey, Vector3 pPosition, Quaternion pRotation, Transform pParent)
        {
            return InstantiateAsync(pKey, pPosition, pRotation, pParent, null);
        }

        public static ContentHandle<GameObject> InstantiateAsync(
            object pKey,
            Vector3 pPosition,
            Quaternion pRotation,
            Transform pParent,
            LoadNetworkOptions pNetworkOptions)
        {
            if (!TryResolveKey(pKey, out string address))
            {
                HCLogger.LogError(ErrorCode.RESOURCE_KEY_INVALID, $"Invalid key type or value: {pKey?.GetType().Name ?? "null"} {pKey?.ToString() ?? "null"}");
                return default;
            }
            if (!CheckInitialized()) return default;
            return Impl.InstantiateAsync(address, pPosition, pRotation, pParent, pNetworkOptions);
        }

        /// <summary>
        /// Load a scene by address. Returns ContentHandle&lt;SceneInstance&gt;.
        /// The scene is unloaded when the handle is released (RefCount reaches 0).
        /// </summary>
        public static ContentHandle<SceneInstance> LoadSceneAsync(string pAddress, LoadSceneMode pMode = LoadSceneMode.Single)
        {
            return LoadSceneAsync(pAddress, pMode, null);
        }

        public static ContentHandle<SceneInstance> LoadSceneAsync(
            string pAddress,
            LoadSceneMode pMode,
            LoadNetworkOptions pNetworkOptions)
        {
            HCLogger.LogVerbose($"[Facade] LoadSceneAsync [{LogFields.ADDRESS}={pAddress}] mode={pMode}");
            if (!CheckInitialized()) return default;
            return Impl.LoadSceneAsync(pAddress, pMode, pNetworkOptions);
        }

        // ── Release ─────────────────────────────────────────────────────

        /// <summary>
        /// Release a content handle (RefCount--). When RefCount reaches 0, the resource
        /// and its dependencies are recursively unloaded.
        /// </summary>
        public static void Release(ContentHandle pHandle)
        {
            HCLogger.LogVerbose($"[Facade] Release handleId={pHandle.HandleId} [{LogFields.LOCATION_HASH}={pHandle.OperationId}]");
            if (!pHandle.IsValid) return;
            if (!CheckInitialized()) return;
            Impl.Release(pHandle.HandleId, pHandle.Operation);
        }

        /// <summary>
        /// Typed overload: release a generic ContentHandle&lt;T&gt;.
        /// </summary>
        public static void Release<T>(ContentHandle<T> pHandle)
        {
            Release((ContentHandle)pHandle);
        }

        public static void ReleaseInstance(GameObject pInstance)
        {
            HCLogger.LogVerbose($"[Facade] ReleaseInstance name={pInstance?.name ?? "null"}");
            if (!CheckInitialized()) return;
            Impl.ReleaseInstance(pInstance);
        }

        // ── Resource Query ────────────────────────────────────────────────

        public static bool HasResource(string address)
        {
            if (!IsInitialized) return false;
            return Impl.HasResource(address);
        }

        /// <summary>
        /// Check if a resource exists for the given object key (string or AssetReference).
        /// </summary>
        public static bool HasResource(object pKey)
        {
            if (!IsInitialized) return false;
            if (!TryResolveKey(pKey, out string address)) return false;
            return Impl.HasResource(address);
        }

        // ── Lifecycle ──────────────────────────────────────────────────

        /// <summary>
        /// Shut down the HyperContent system: dispose all cached operations (triggering
        /// Provider.Release for each), clear instance tracking, and release the catalog.
        /// After calling this, Initialize must be called again before using Load/Release APIs.
        /// </summary>
        public static void Shutdown()
        {
            HCLogger.LogInfo("[Facade] Shutdown requested");
            if (Impl == null)
            {
                HCLogger.LogVerbose("[Facade] Shutdown — nothing to shut down (not initialized)");
                return;
            }
            Impl.Shutdown();
            HyperContentImpl.Current = null;
            _downloadManager = null;
            _isInitializing = false;
        }

        // ── Events ──────────────────────────────────────────────────────

        public static event Action<string, Exception> OnLoadFailed
        {
            add
            {
                if (Impl != null) Impl.LoadFailed += value;
                else              _pendingLoadFailed += value;
            }
            remove
            {
                if (Impl != null) Impl.LoadFailed -= value;
                _pendingLoadFailed -= value;
            }
        }

        public static event Action<string> OnLoadSucceeded
        {
            add
            {
                if (Impl != null) Impl.LoadSucceeded += value;
                else              _pendingLoadSucceeded += value;
            }
            remove
            {
                if (Impl != null) Impl.LoadSucceeded -= value;
                _pendingLoadSucceeded -= value;
            }
        }

        // Drains buffered pre-init subscriptions onto the freshly created Impl. Called right after
        // HyperContentImpl.Current is assigned in every init path.
        private static void ForwardPendingEvents(HyperContentImpl impl)
        {
            if (_pendingLoadFailed != null)
            {
                impl.LoadFailed += _pendingLoadFailed;
                _pendingLoadFailed = null;
            }
            if (_pendingLoadSucceeded != null)
            {
                impl.LoadSucceeded += _pendingLoadSucceeded;
                _pendingLoadSucceeded = null;
            }
        }

        // ── Diagnostics ─────────────────────────────────────────────────

        /// <summary>
        /// Log a detailed runtime diagnostics report to <see cref="UnityEngine.Debug.Log"/>.
        /// Call after a scene transition to detect leaks: any operation with unexpected
        /// <c>RefCount &gt; 0</c> means business code forgot to <see cref="Release"/>; any
        /// loaded bundle flagged <c>ORPHAN</c> means HyperContent itself failed to unload.
        /// Define <c>HYPERCONTENT_TRACK_HANDLES</c> to additionally capture per-handle
        /// acquisition stack traces (zero overhead when undefined).
        /// </summary>
        public static void LogDiagnosticsReport()
        {
            HyperContentDiagnostics.LogReport();
        }

        /// <summary>
        /// Programmatic access to the diagnostics snapshot — same data as <see cref="LogDiagnosticsReport"/>
        /// but as a structured object for assertions / dashboards.
        /// </summary>
        public static DiagnosticsSnapshot GetDiagnosticsSnapshot(DiagnosticsSnapshot pReuse = null)
        {
            return HyperContentDiagnostics.GetSnapshot(pReuse);
        }

        // ── Internal ────────────────────────────────────────────────────

        private static bool CheckInitialized()
        {
            if (Impl != null && Impl.IsInitialized) return true;

            HCLogger.LogError(ErrorCode.SYSTEM_NOT_INITIALIZED,
                "HyperContent not initialized. Call 'await HyperContent.InitializeAsync()' or " +
                "'HyperContent.Initialize(pOnComplete: ...)' before using Load/Release APIs.");
            return false;
        }

        private static void InitializeInternal(Action<bool> onComplete)
        {
#if UNITY_EDITOR
            if (PlayModeSettings.IsAssetDatabaseMode())
            {
                bool ok = InitializeAssetDatabaseMode();
                onComplete?.Invoke(ok);
                return;
            }
#endif
            InitializeBundleMode(onComplete);
        }

        private static void InitializeBundleMode(Action<bool> onComplete)
        {
            CatalogLocator.ResolveLocalCatalog(default, resolution =>
            {
                if (!resolution.success)
                {
                    HCLogger.LogError(ErrorCode.CATALOG_LOAD_FAILED,
                        $"Catalog resolution failed: {resolution.error}");
                    onComplete?.Invoke(false);
                    return;
                }

                var catalogFormat = (CatalogSerializationFormat)(resolution.settings?.catalogFormat ?? 0);
                HCLogger.LogInfo($"[Init] Catalog resolved: path={resolution.catalogPath}, " +
                    $"source={resolution.source}, format={catalogFormat}");

                var catalog = new LocalContentCatalog
                {
                    LoadMode = (DependencyLoadMode)(resolution.settings?.dependencyLoadMode ?? 0)
                };
                HCLogger.LogInfo($"[Init] Dependency load mode: {catalog.LoadMode}");
                if (!catalog.Initialize(resolution.catalogPath, catalogFormat))
                {
                    HCLogger.LogError(ErrorCode.CATALOG_LOAD_FAILED,
                        $"Catalog initialization failed: {resolution.catalogPath} (format={catalogFormat})");
                    onComplete?.Invoke(false);
                    return;
                }

                var settings = resolution.settings;
                string initialRemoteUrl = GetEffectiveRemoteBundleBaseUrl(settings);
                if (!string.IsNullOrEmpty(initialRemoteUrl))
                    HCLogger.LogInfo($"[Init] Remote bundle base URL (effective): {initialRemoteUrl}");
                else
                    HCLogger.LogInfo("[Init] Remote bundle base URL empty — call SetRemoteBundleBaseUrl or set remoteBundleBaseUrl in settings to enable remote downloads.");

                string bundleBasePath = HyperContentPaths.BundleBasePath;
                var bundleLoader = new UnityBundleLoader();

                var store = new LocalBundleStore();
                store.Initialize(HyperContentPaths.CacheBundlePath);
                var transport = new HttpBundleTransport();
                transport.Initialize(initialRemoteUrl,
                    settings?.catalogRequestTimeout > 0 ? settings.catalogRequestTimeout : 30);
                HCLogger.LogInfo("[Init] BundleStore and HttpBundleTransport created (remote URL can be set at runtime via SetRemoteBundleBaseUrl).");

                IPlayAssetPackRouter padRouter = null;
                if (settings != null && settings.HasPlayAssetDelivery)
                {
                    padRouter = new DefaultPlayAssetPackRouter(settings.playAssetDeliveryPackName);
                    HCLogger.LogInfo($"[Init] PAD enabled — pack='{settings.playAssetDeliveryPackName}'");
                }
                else
                {
                    HCLogger.LogInfo("[Init] PAD disabled — set RuntimeSettings.playAssetDeliveryPackName to enable Google Play Asset Delivery for local bundles.");
                }

                var impl = new HyperContentImpl(
                    catalog: catalog,
                    bundleLoader: bundleLoader,
                    bundleStore: store,
                    bundleTransport: transport,
                    bundleBasePath: bundleBasePath,
                    pPlayAssetPackRouter: padRouter);
                HyperContentImpl.Current = impl;
                ForwardPendingEvents(impl);
                HCLogger.LogInfo("[Init] Step 3: HyperContentImpl.Current assigned");

                HCLogger.LogInfo($"[Init] Initialized (Bundle mode) — catalog={resolution.catalogPath}, " +
                    $"source={resolution.source}, bundleBasePath={bundleBasePath}, remoteBundle=ready");
                onComplete?.Invoke(true);
            });
        }

#if UNITY_EDITOR
        private static bool InitializeAssetDatabaseMode()
        {
            HCLogger.LogInfo("InitializeAssetDatabaseMode — using AssetDatabase, no bundles required");

            var catalogType = System.Type.GetType(
                "com.igg.hypercontent.editor.simulation.EditorAssetDatabaseCatalog, HyperContent.Editor");
            if (catalogType == null)
            {
                HCLogger.LogError(ErrorCode.SYSTEM_INVALID_STATE,
                    "EditorAssetDatabaseCatalog type not found. Is HyperContent.Editor assembly loaded?");
                return false;
            }

            var catalog = (ICatalog)System.Activator.CreateInstance(catalogType);
            if (!catalog.Initialize(null))
            {
                HCLogger.LogWarn("EditorAssetDatabaseCatalog initialization failed");
                return false;
            }

            var providerType = System.Type.GetType(
                "com.igg.hypercontent.editor.simulation.AssetDatabaseProvider, HyperContent.Editor");

            var impl = new HyperContentImpl(
                catalog: catalog,
                bundleLoader: new UnityBundleLoader(),
                bundleStore: null,
                bundleTransport: null,
                editorProvider: providerType != null
                    ? (IContentProvider)System.Activator.CreateInstance(providerType)
                    : null);
            HyperContentImpl.Current = impl;
            ForwardPendingEvents(impl);

            HCLogger.LogInfo("Initialized (AssetDatabase mode) — direct asset loading, no AB needed");
            return true;
        }
#endif

        // ── Catalog disk update & reload (facade) ─────────────────────────────────

        /// <summary>
        /// Callback form — primary external shape for catalog-on-disk hot update. Pure-callback
        /// pipeline under the hood (no <c>async void</c>, no awaited state machine).
        /// Check remote catalog hash, download catalog to disk cache when needed. **Disk only** —
        /// does not reload memory catalog or call <see cref="Initialize"/>.
        /// </summary>
        public static void TryUpdateCachedCatalogOnDiskAsync(
            Action<CatalogDiskUpdateResult> pOnComplete,
            CancellationToken pToken = default)
        {
            string settingsPath = HyperContentPaths.SettingsPath;

            CatalogLocator.LoadSettings(settingsPath, pToken, (settings, loadEx) =>
            {
                if (loadEx is OperationCanceledException)
                {
                    pOnComplete?.Invoke(new CatalogDiskUpdateResult(
                        CatalogDiskUpdateKind.Failed,
                        ErrorCode.CATALOG_DISK_UPDATE_FAILED,
                        "Canceled"));
                    return;
                }

                if (settings == null)
                {
                    pOnComplete?.Invoke(new CatalogDiskUpdateResult(
                        CatalogDiskUpdateKind.Failed,
                        ErrorCode.SETTINGS_NOT_FOUND,
                        "Failed to load settings.json"));
                    return;
                }

                string cdnBase = GetEffectiveRemoteBundleBaseUrl(settings);
                CatalogLocator.CheckAndDownloadCatalogUpdate(settings, cdnBase, pToken, outcome =>
                {
                    try { pOnComplete?.Invoke(MapCatalogLocatorOutcome(outcome)); }
                    catch (Exception e)
                    {
                        HCLogger.LogError(ErrorCode.CATALOG_DISK_UPDATE_FAILED,
                            $"TryUpdateCachedCatalogOnDiskAsync onComplete threw: {e.Message}");
                    }
                });
            });
        }

        /// <summary>
        /// <see cref="Task{T}"/> bridge over the callback <see cref="TryUpdateCachedCatalogOnDiskAsync(Action{CatalogDiskUpdateResult}, CancellationToken)"/>.
        /// When settings have no remote catalog (<c>HasRemoteCatalog</c> false), returns <see cref="CatalogDiskUpdateKind.SkippedNoRemote"/>
        /// + <see cref="ErrorCode.CATALOG_DISK_UPDATE_SKIPPED_NO_REMOTE"/> (not a failure).
        /// **Recommended:** call before the first <see cref="Initialize"/> so init resolves the updated cached file (usually no <see cref="ReloadRuntimeCatalogAsync"/>).
        /// If <see cref="Initialize"/> already ran and this returns <see cref="CatalogDiskUpdateKind.Applied"/>, call <see cref="ReloadRuntimeCatalogAsync"/>
        /// before bundle work that depends on the new catalog.
        /// </summary>
        public static Task<CatalogDiskUpdateResult> TryUpdateCachedCatalogOnDiskAsync(CancellationToken pToken = default)
        {
            var tcs = new TaskCompletionSource<CatalogDiskUpdateResult>();
            TryUpdateCachedCatalogOnDiskAsync(r => tcs.TrySetResult(r), pToken);
            return tcs.Task;
        }

        /// <summary>
        /// Callback form of <see cref="ReloadRuntimeCatalogAsync"/>.
        /// </summary>
        public static void ReloadRuntimeCatalog(
            CancellationToken pToken,
            Action<bool> pOnComplete)
        {
#if UNITY_EDITOR
            if (PlayModeSettings.IsAssetDatabaseMode())
            {
                HCLogger.LogWarn("[Catalog] ReloadRuntimeCatalog skipped in AssetDatabase play mode.");
                pOnComplete?.Invoke(false);
                return;
            }
#endif
            if (!CheckInitialized())
            {
                pOnComplete?.Invoke(false);
                return;
            }

            CatalogLocator.ResolveLocalCatalog(pToken, resolution =>
            {
                if (!resolution.success)
                {
                    HCLogger.LogError(ErrorCode.CATALOG_LOAD_FAILED, resolution.error);
                    pOnComplete?.Invoke(false);
                    return;
                }

                if (!Impl.TryReplaceRuntimeCatalog(resolution.catalogPath))
                {
                    HCLogger.LogError(ErrorCode.CATALOG_LOAD_FAILED,
                        $"Failed to load catalog from path: {resolution.catalogPath}");
                    pOnComplete?.Invoke(false);
                    return;
                }

                _downloadManager = null;
                pOnComplete?.Invoke(true);
            });
        }

        /// <summary>
        /// Reload in-memory <see cref="LocalContentCatalog"/> from the current local resolve path (cached vs package).
        /// Does not perform network I/O. Use when <see cref="Initialize"/> has already run and <see cref="TryUpdateCachedCatalogOnDiskAsync(CancellationToken)"/> returned <see cref="CatalogDiskUpdateKind.Applied"/>.
        /// Thin <see cref="TaskCompletionSource{T}"/> wrapper around the callback <see cref="ReloadRuntimeCatalog"/>.
        /// </summary>
        public static Task<bool> ReloadRuntimeCatalogAsync(CancellationToken pToken = default)
        {
            var tcs = new TaskCompletionSource<bool>();
            ReloadRuntimeCatalog(pToken, r => tcs.TrySetResult(r));
            return tcs.Task;
        }

        // ── Update Check API ──────────────────────────────────────────────────────

        /// <summary>
        /// Check if there are pending bundle downloads.
        /// Call after Initialize() to check if content update is available.
        /// </summary>
        public static bool HasPendingDownloads()
        {
            return HasPendingDownloads(PendingBundleQueryScope.All);
        }

        /// <summary>
        /// Check pending downloads for the given scope (<see cref="PendingBundleQueryScope.All"/> vs blocking-only).
        /// </summary>
        public static bool HasPendingDownloads(PendingBundleQueryScope pScope)
        {
            EnsureDownloadManager();
            if (_downloadManager == null) return false;

            var check = _downloadManager.CheckAllPendingDownloads(pScope);
            return check.success && check.totalCount > 0;
        }

        /// <summary>
        /// Get all pending bundle downloads.
        /// </summary>
        public static DownloadCheckResult GetPendingDownloads()
        {
            return GetPendingDownloads(PendingBundleQueryScope.All);
        }

        /// <summary>
        /// Get pending bundle downloads for the given scope (aligned with <see cref="DownloadAllBlockingBundlesAsync"/> for <see cref="PendingBundleQueryScope.BlockingOnly"/>).
        /// </summary>
        public static DownloadCheckResult GetPendingDownloads(PendingBundleQueryScope pScope)
        {
            EnsureDownloadManager();
            if (_downloadManager == null)
                return new DownloadCheckResult { success = false, error = "Not initialized" };

            return _downloadManager.CheckAllPendingDownloads(pScope);
        }

        /// <summary>
        /// Get bundles needed to load a specific asset.
        /// </summary>
        public static DownloadCheckResult GetDownloadsForAsset(string pAssetPath)
        {
            EnsureDownloadManager();
            if (_downloadManager == null)
                return new DownloadCheckResult { success = false, error = "Not initialized" };

            return _downloadManager.CheckDownloadsForAsset(pAssetPath);
        }

        /// <summary>
        /// Get bundles needed to load multiple assets.
        /// </summary>
        public static DownloadCheckResult GetDownloadsForAssets(System.Collections.Generic.IEnumerable<string> pAssetPaths)
        {
            EnsureDownloadManager();
            if (_downloadManager == null)
                return new DownloadCheckResult { success = false, error = "Not initialized" };

            return _downloadManager.CheckDownloadsForAssets(pAssetPaths);
        }

        // ── Download API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Download all pending bundles.
        /// </summary>
        /// <param name="pCancellationToken">Batch cancellation: waiters linked in <see cref="IBundleDownloadQueue"/>; in-flight HTTP aborted when the last waiter for a URL is cancelled (see <c>BundleDownloadQueue</c>).</param>
        public static void DownloadAllUpdatesAsync(
            Action<DownloadProgress> pOnProgress = null,
            Action<DownloadResult> pOnComplete = null,
            CancellationToken pCancellationToken = default)
        {
            EnsureDownloadManager();
            if (_downloadManager == null)
            {
                pOnComplete?.Invoke(new DownloadResult { success = false, error = "Not initialized" });
                return;
            }

            _downloadManager.DownloadAllAsync(pOnProgress, pOnComplete, pCancellationToken);
        }

        /// <summary>
        /// Download every remote bundle tagged <see cref="BundleTagFlags.Blocking"/> that is not yet satisfied locally.
        /// Same completion/progress shape as <see cref="DownloadAllUpdatesAsync"/>, but enqueues at <see cref="BundleDownloadPriority.High"/>
        /// (same lane as <see cref="RemoteBundleProvider"/> load-driven downloads) so blocking work preempts Normal/Low queue traffic.
        /// </summary>
        public static void DownloadAllBlockingBundlesAsync(
            Action<DownloadProgress> pOnProgress = null,
            Action<DownloadResult> pOnComplete = null,
            CancellationToken pCancellationToken = default)
        {
            EnsureDownloadManager();
            if (_downloadManager == null)
            {
                pOnComplete?.Invoke(new DownloadResult { success = false, error = "Not initialized" });
                return;
            }

            _downloadManager.DownloadAllBlockingAsync(pOnProgress, pOnComplete, pCancellationToken);
        }

        /// <summary>
        /// Download specified bundles (after user selection).
        /// </summary>
        public static void DownloadBundlesAsync(
            System.Collections.Generic.IEnumerable<string> pBundleNames,
            Action<DownloadProgress> pOnProgress = null,
            Action<DownloadResult> pOnComplete = null,
            CancellationToken pCancellationToken = default)
        {
            EnsureDownloadManager();
            if (_downloadManager == null)
            {
                pOnComplete?.Invoke(new DownloadResult { success = false, error = "Not initialized" });
                return;
            }

            _downloadManager.DownloadBundlesAsync(pBundleNames, pOnProgress, pOnComplete,
                BundleDownloadPriority.Normal, pCancellationToken);
        }

        // ── Global download queue progress ────────────────────────────────────────

        /// <summary>
        /// Subscribe to coarse aggregate progress for all work going through <see cref="IBundleDownloadQueue"/> (loads, batch downloads, etc.).
        /// Call after <see cref="Initialize"/>; safe to register multiple listeners. Unregister with <see cref="UnregisterDownloadQueueProgressListener"/> to avoid leaks.
        /// Snapshot semantics: see <see cref="DownloadQueueProgressSnapshot"/> and ARCHITECTURE / LOAD_RELEASE_FLOW.
        /// </summary>
        public static void RegisterDownloadQueueProgressListener(IDownloadQueueProgressListener pListener)
        {
            if (pListener == null) return;
            if (!CheckInitialized()) return;
            Impl.BundleDownloadQueue?.RegisterProgressListener(pListener);
        }

        /// <summary>
        /// Remove a listener previously passed to <see cref="RegisterDownloadQueueProgressListener"/>.
        /// </summary>
        public static void UnregisterDownloadQueueProgressListener(IDownloadQueueProgressListener pListener)
        {
            if (pListener == null) return;
            if (Impl?.BundleDownloadQueue == null) return;
            Impl.BundleDownloadQueue.UnregisterProgressListener(pListener);
        }

        // ── Private Helper ────────────────────────────────────────────────────────

        private static void EnsureDownloadManager()
        {
            if (_downloadManager != null) return;
            if (HyperContentImpl.Current == null) return;

            var impl = HyperContentImpl.Current;
            if (impl.BundleStore == null || impl.BundleDownloadQueue == null)
            {
                HCLogger.LogVerbose("[HyperContent] BundleDownloadManager skipped — no BundleStore or BundleDownloadQueue configured");
                return;
            }

            _downloadManager = new BundleDownloadManager(
                impl.Catalog,
                impl.BundleStore,
                impl.BundleDownloadQueue);
        }
    }
}
