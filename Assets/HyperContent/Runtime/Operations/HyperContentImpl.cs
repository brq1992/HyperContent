using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using com.igg.hypercontent.runtime;
using com.igg.hypercontent.shared;
using com.igg.core;

namespace com.igg.hypercontent
{
    /// <summary>
    /// Internal implementation behind the HyperContent static facade.
    /// Owns all runtime subsystems (OperationCache, ProviderRegistry, ResourceManager, InstanceRegistry).
    /// Pure C# class — created by HyperContent.Initialize or HyperContent.InitializeAsync.
    /// See ARCHITECTURE.md section 3 and DIRECTORY_STRUCTURE.md Operations/HyperContentImpl.cs.
    /// </summary>
    internal sealed class HyperContentImpl
    {
        internal static HyperContentImpl Current { get; set; }

        private readonly OperationCache _operationCache;
        private readonly ProviderRegistry _providerRegistry;
        private readonly ResourceManager _resourceManager;
        private readonly InstanceRegistry _instanceRegistry;
        private ICatalog _catalog;
        private readonly IBundleLoader _bundleLoader;
        private readonly IBundleStore _bundleStore;
        private readonly IBundleTransport _bundleTransport;
        private readonly IBundleDownloadQueue _bundleDownloadQueue;

        private int _nextHandleId;

        // handleId -> the operation that handle owns a refcount on. Used both for
        // double-release detection (Remove returning false) and for diagnostics reverse
        // mapping (op -> handles holding it). The dictionary cost vs the previous HashSet
        // is negligible (one extra reference per active handle), and storing the op here
        // means leak diagnostics work even when HYPERCONTENT_TRACK_HANDLES is off.
        private readonly Dictionary<int, AsyncOperationBase> _activeHandles =
            new Dictionary<int, AsyncOperationBase>();

#if HYPERCONTENT_TRACK_HANDLES
        // Per-handle acquisition trace, populated only when HYPERCONTENT_TRACK_HANDLES is defined.
        // Used by HyperContentDiagnostics to identify the call site of unreleased handles
        // (i.e. business code that loaded an asset without a paired Release).
        private readonly Dictionary<int, HandleAcquisition> _handleAcquisitions =
            new Dictionary<int, HandleAcquisition>();

        internal struct HandleAcquisition
        {
            public int LocationHash;
            public string Address;
            public int Frame;
            public float Time;
            public string StackTrace;
        }
#endif

        internal ICatalog Catalog => _catalog;
        internal IBundleLoader BundleLoader => _bundleLoader;
        internal IBundleStore BundleStore => _bundleStore;
        internal IBundleTransport BundleTransport => _bundleTransport;
        internal IBundleDownloadQueue BundleDownloadQueue => _bundleDownloadQueue;
        internal OperationCache OperationCache => _operationCache;
        internal InstanceRegistry InstanceRegistry => _instanceRegistry;
        internal IReadOnlyDictionary<int, AsyncOperationBase> ActiveHandles => _activeHandles;
        internal int ActiveHandleCount => _activeHandles.Count;

        internal event Action<string, Exception> LoadFailed;
        internal event Action<string> LoadSucceeded;

        internal bool IsInitialized { get; private set; }

        internal HyperContentImpl(
            ICatalog catalog,
            IBundleLoader bundleLoader,
            IBundleStore bundleStore,
            IBundleTransport bundleTransport,
            string bundleBasePath = null,
            IContentProvider editorProvider = null,
            IPlayAssetPackRouter pPlayAssetPackRouter = null)
        {
            HCLogger.LogVerbose("HyperContentImpl ctor — begin");
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _bundleLoader = bundleLoader;
            _bundleStore = bundleStore;
            _bundleTransport = bundleTransport;
            _bundleDownloadQueue = bundleTransport != null ? new BundleDownloadQueue(bundleTransport) : null;

            _operationCache = new OperationCache();
            _providerRegistry = new ProviderRegistry();
            _instanceRegistry = new InstanceRegistry();

            if (editorProvider != null)
            {
                _providerRegistry.Register(editorProvider);
                HCLogger.LogVerbose($"Editor provider registered [{LogFields.PROVIDER_ID}={editorProvider.ProviderId}]");
            }

            var bundleFileProvider = new BundleFileProvider(bundleLoader, bundleStore, bundleBasePath);
#if UNITY_ANDROID && !UNITY_EDITOR && GOOGLE_PLAY_ASSET_DELIVERY
            if (pPlayAssetPackRouter != null)
            {
                _providerRegistry.Register(new PlayAssetDeliveryBundleProvider(
                    bundleLoader, bundleFileProvider, pPlayAssetPackRouter, bundleStore));
                HCLogger.LogInfo("PlayAssetDeliveryBundleProvider registered (Android PAD)");
            }
            else
            {
                _providerRegistry.Register(bundleFileProvider);
                HCLogger.LogInfo("BundleFileProvider registered (PAD disabled — no IPlayAssetPackRouter configured)");
            }
#else
            _providerRegistry.Register(bundleFileProvider);
#endif
            _providerRegistry.Register(new BundleAssetExtractor(bundleLoader));
            _providerRegistry.Register(new LocalFileProvider());
            _providerRegistry.Register(new SceneProvider());

            if (_bundleDownloadQueue != null)
            {
                _providerRegistry.Register(new RemoteBundleProvider(bundleLoader, _bundleDownloadQueue, bundleStore));
                HCLogger.LogVerbose("RemoteBundleProvider registered (bundle download queue)");
            }
            else
            {
                HCLogger.LogVerbose("RemoteBundleProvider skipped (no bundleTransport)");
            }

            _resourceManager = new ResourceManager(_operationCache, _providerRegistry);
            IsInitialized = true;
            HCLogger.LogInfo("HyperContentImpl initialized — all subsystems ready");
        }

        internal ContentHandle<T> LoadAsync<T>(string pAddress) where T : UnityEngine.Object
        {
            return LoadAsync<T>(pAddress, null);
        }

        internal ContentHandle<T> LoadAsync<T>(string pAddress, LoadNetworkOptions pNetworkOptions) where T : UnityEngine.Object
        {
            HCLogger.LogVerbose($"LoadAsync<{typeof(T).Name}> [{LogFields.ADDRESS}={pAddress}]");

            var op = StartLoadAssetOp<T>(pAddress, pNetworkOptions);
            if (op == null) return default;

            int handleId = AllocateHandleId(op);
            return new ContentHandle<T>(op, handleId, () => op.Result);
        }

        /// <summary>
        /// Shared entry shaping for LoadAsync &amp; InstantiateAsync: input validation, catalog
        /// resolution, IGGProfiler <c>HC.Load_{addr}</c> sample wrap, and the actual ResourceManager
        /// kick-off. Returns a refcounted AssetOperation&lt;T&gt; (RefCount = 1 from the cache create
        /// / +1 from a cache hit) the caller hands directly to a ContentHandle via
        /// <see cref="AllocateHandleId"/>. Returns null on validation / catalog miss; errors are
        /// already logged with the right ErrorCode before returning.
        /// </summary>
        private AssetOperation<T> StartLoadAssetOp<T>(string pAddress, LoadNetworkOptions pNetworkOptions)
            where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(pAddress))
            {
                return CreateFailedAssetOp<T>(pAddress, ErrorCode.RESOURCE_KEY_INVALID, "Invalid address");
            }

            if (pNetworkOptions != null && pNetworkOptions.Mode == LoadAssetNetworkMode.QueryOnly)
            {
                return CreateFailedAssetOp<T>(pAddress, ErrorCode.SYSTEM_INVALID_STATE,
                    "LoadAsync does not support LoadAssetNetworkMode.QueryOnly; use HyperContent.QueryLoadAvailability<T>.");
            }

            // D2 § Catalog 段：业务每次 LoadAsync/InstantiateAsync 都会调一次 TryGetLocations，
            // 是 hot path。BeginSample/EndSample 是 [Conditional("ENABLE_PROFILERLOG")]，关宏时
            // 调用点 + 字符串拼接整段从 IL 擦除，零成本（A2 落地结论一致）。
            // 注意：必须先把 TryGetLocations 拆成独立的一行 bool 赋值，否则字符串拼接表达式
            // 与 if 条件耦合在一起，[Conditional] 擦不到 if 内部分支。
            IGGProfiler.BeginSample($"HC.Load.Stage.Catalog_{pAddress}");
            bool catalogResolved = _catalog.TryGetLocations(pAddress, typeof(T), out var locations);
            IGGProfiler.EndSample($"HC.Load.Stage.Catalog_{pAddress}");

            if (!catalogResolved || locations.Count == 0)
            {
                return CreateFailedAssetOp<T>(pAddress, ErrorCode.CATALOG_ENTRY_NOT_FOUND,
                    "Address not found in catalog");
            }

            HCLogger.LogVerbose($"[Load] Step C: Catalog resolved address={pAddress} locations={locations.Count}");
#if ENABLE_PROFILERLOG
            // lambda + closure 不是 [Conditional]，无法靠 IGGProfiler 的标记自动剥离；
            // 整段用 #if 包起来，关宏时 sampleName 声明、字符串插值、OnCompleted 注册全部不进编译产物。
            string sampleName = $"HC.Load_{pAddress}";
            IGGProfiler.BeginSample(sampleName);
#endif
            var op = StartLoad<T>(pAddress, locations[0], pNetworkOptions);
            HCLogger.LogVerbose($"[Load] Step D: StartLoad returned op={op != null}");
            if (op == null)
            {
                // StartLoad returns null only when the user declined a PromptBeforeDownload remote
                // fetch (TryConfirmRemoteDownloadsForLocation already logged a Warn). Surface it as a
                // failed handle so coroutine / await callers don't hang on a default (never-completing)
                // handle. Skip the duplicate error log; still raise LoadFailed for consistency.
                op = CreateFailedAssetOp<T>(pAddress, ErrorCode.OPERATION_USER_DECLINED_REMOTE_DOWNLOAD,
                    "User declined remote bundle download", pLogError: false);
            }
#if ENABLE_PROFILERLOG
            // op is now never null (synthetic failure op covers every early-out), so the EndSample
            // always pairs with the BeginSample above — including the declined-download path that
            // previously left the sample unbalanced. A synthetic failed op is already IsDone, so the
            // subscription fires immediately and the sample closes synchronously.
            op.OnCompleted += _ => IGGProfiler.EndSample(sampleName);
#endif
            return op;
        }

        /// <summary>
        /// Build a synthetic, already-failed <see cref="AssetOperation{T}"/> for an entry-level load
        /// failure (invalid address, catalog miss, declined remote download). Returning a failed op —
        /// rather than null, which the public API turned into a default (never-completing) handle — gives
        /// the caller a real terminal handle: its Completed callback fires, IsDone is true (so coroutines
        /// and await don't hang), and await surfaces the error. The op is synthetic so it never enters
        /// <see cref="OperationCache"/> nor the refcount / release path. The global <see cref="LoadFailed"/>
        /// event is raised to match the runtime-failure path in <see cref="StartLoad{T}"/>.
        /// </summary>
        private AssetOperation<T> CreateFailedAssetOp<T>(string pAddress, int pErrorCode, string pMessage, bool pLogError = true)
            where T : UnityEngine.Object
        {
            if (pLogError)
                HCLogger.LogError(pErrorCode, $"[{LogFields.ADDRESS}={pAddress}] {pMessage}");
            var failure = MakeLoadFailure(pAddress, pErrorCode, pMessage);
            var op = new AssetOperation<T>(failure);
            RaiseLoadFailed(pAddress, failure);
            return op;
        }

        /// <summary>
        /// Scene counterpart of <see cref="CreateFailedAssetOp{T}"/>: a synthetic, already-failed
        /// <see cref="SceneOperation"/> for entry-level scene load failures.
        /// </summary>
        private SceneOperation CreateFailedSceneOp(string pAddress, int pErrorCode, string pMessage, bool pLogError = true)
        {
            if (pLogError)
                HCLogger.LogError(pErrorCode, $"[{LogFields.ADDRESS}={pAddress}] {pMessage}");
            var failure = MakeLoadFailure(pAddress, pErrorCode, pMessage);
            var op = new SceneOperation(failure);
            RaiseLoadFailed(pAddress, failure);
            return op;
        }

        private static Exception MakeLoadFailure(string pAddress, int pErrorCode, string pMessage)
            => new Exception($"[{LogFields.ERROR_CODE}={pErrorCode}] [{LogFields.ADDRESS}={pAddress}] {pMessage}");

        private void RaiseLoadFailed(string pAddress, Exception pFailure)
        {
            try { LoadFailed?.Invoke(pAddress, pFailure); }
            catch (Exception e) { HCLogger.LogError($"LoadFailed handler threw: {e.Message}"); }
        }

        internal LoadAvailabilityResult QueryLoadAvailability<T>(string pAddress) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(pAddress))
            {
                return new LoadAvailabilityResult(
                    false,
                    new MissingBundlePromptInfo(0, 0, null),
                    ErrorCode.RESOURCE_KEY_INVALID,
                    "Invalid address");
            }

            if (!_catalog.TryGetLocations(pAddress, typeof(T), out var locations) || locations.Count == 0)
            {
                return new LoadAvailabilityResult(
                    false,
                    new MissingBundlePromptInfo(0, 0, null),
                    ErrorCode.CATALOG_ENTRY_NOT_FOUND,
                    "Address not found in catalog");
            }

            if (_bundleStore == null)
            {
                return new LoadAvailabilityResult(
                    true,
                    new MissingBundlePromptInfo(0, 0, null),
                    0,
                    null);
            }

            var missing = LoadNetworkPrecheck.CollectMissingRemoteForLocation(_catalog, _bundleStore, locations[0]);
            var summary = LoadNetworkPrecheck.ToMissingSummary(missing);
            return new LoadAvailabilityResult(missing.Count == 0, summary, 0, null);
        }

        internal ContentHandle<GameObject> InstantiateAsync(string pAddress, Transform pParent = null)
        {
            return InstantiateAsync(pAddress, pParent, null);
        }

        internal ContentHandle<GameObject> InstantiateAsync(string pAddress, Transform pParent, LoadNetworkOptions pNetworkOptions)
        {
            HCLogger.LogVerbose($"InstantiateAsync [{LogFields.ADDRESS}={pAddress}] parent={pParent?.name ?? "null"}");
            return InstantiateAsyncCore(pAddress, pNetworkOptions, pAsset => pParent != null
                ? UnityEngine.Object.Instantiate(pAsset, pParent)
                : UnityEngine.Object.Instantiate(pAsset));
        }

        internal ContentHandle<GameObject> InstantiateAsync(string pAddress, Transform pParent, bool pInstantiateInWorldSpace)
        {
            return InstantiateAsync(pAddress, pParent, pInstantiateInWorldSpace, null);
        }

        internal ContentHandle<GameObject> InstantiateAsync(
            string pAddress,
            Transform pParent,
            bool pInstantiateInWorldSpace,
            LoadNetworkOptions pNetworkOptions)
        {
            HCLogger.LogVerbose($"InstantiateAsync [{LogFields.ADDRESS}={pAddress}] parent={pParent?.name ?? "null"} worldSpace={pInstantiateInWorldSpace}");
            return InstantiateAsyncCore(pAddress, pNetworkOptions, pAsset => pParent != null
                ? UnityEngine.Object.Instantiate(pAsset, pParent, pInstantiateInWorldSpace)
                : UnityEngine.Object.Instantiate(pAsset));
        }

        internal ContentHandle<GameObject> InstantiateAsync(string pAddress, Vector3 pPosition, Quaternion pRotation, Transform pParent)
        {
            return InstantiateAsync(pAddress, pPosition, pRotation, pParent, null);
        }

        internal ContentHandle<GameObject> InstantiateAsync(
            string pAddress,
            Vector3 pPosition,
            Quaternion pRotation,
            Transform pParent,
            LoadNetworkOptions pNetworkOptions)
        {
            HCLogger.LogVerbose($"InstantiateAsync [{LogFields.ADDRESS}={pAddress}] position={pPosition} parent={pParent?.name ?? "null"}");
            return InstantiateAsyncCore(pAddress, pNetworkOptions, pAsset => pParent != null
                ? UnityEngine.Object.Instantiate(pAsset, pPosition, pRotation, pParent)
                : UnityEngine.Object.Instantiate(pAsset, pPosition, pRotation));
        }

        /// <summary>
        /// Shared core for all InstantiateAsync overloads. The only thing that varies between overloads
        /// is the concrete Object.Instantiate variant, captured by <paramref name="pInstantiator"/>.
        /// The Func adds a small closure allocation, acceptable here because InstantiateAsync is a
        /// low-frequency operation (vs LoadAsync) and it removes ~80% duplicated setup/teardown.
        /// </summary>
        private ContentHandle<GameObject> InstantiateAsyncCore(
            string pAddress,
            LoadNetworkOptions pNetworkOptions,
            Func<GameObject, GameObject> pInstantiator)
        {
            var assetOp = StartLoadAssetOp<GameObject>(pAddress, pNetworkOptions);
            if (assetOp == null) return default;

            GameObject instanceResult = null;
            assetOp.OnCompleted += pOp =>
            {
                if (pOp.Status != OperationStatus.Succeeded || assetOp.Result == null) return;
                // D2 Instantiate 拆分（2026-05-22）：
                //   外层 HC.Instantiate_<addr>          ：Object.Instantiate + _instanceRegistry.Track 总段。
                //   内层 HC.Instantiate.UnityInst_<addr>：纯 Unity Object.Instantiate（prefab 反序列化 + Awake 链 + 业务异常）黑盒段。
                //
                // 用途：
                //   * UnityInst ≈ 总段（差 < 1ms）→ 框架开销可忽略，耗时全在 Unity API 内部，
                //                                  要么 prefab 自身重（节点数 / 组件数 / 子图引用），
                //                                  要么业务 Awake 链有重活/异常。
                //   * 总段 - UnityInst > 5ms      → _instanceRegistry.Track 出现意外开销
                //                                  （dictionary rehash / weakref 注册），需要内部审计。
                //
                // 关 ENABLE_PROFILERLOG 时的零成本保证：inline 字符串 IGGProfiler.BeginSample/EndSample($"...")，
                // [Conditional] 关宏后调用点 + inline 字符串拼接整体擦除，无需 #if 包裹。
                IGGProfiler.BeginSample($"HC.Instantiate_{pAddress}");
                IGGProfiler.BeginSample($"HC.Instantiate.UnityInst_{pAddress}");
                instanceResult = pInstantiator(assetOp.Result);
                IGGProfiler.EndSample($"HC.Instantiate.UnityInst_{pAddress}");
                // Track bumps assetOp.RefCount once for the live GameObject; paired with
                // InstanceRegistry.Release (called by HyperContent.ReleaseInstance) which
                // Destroys + decrements. Combined with the handle's own RefCount that
                // Release(handle) decrements, this is the "Release(handle) + ReleaseInstance(go)
                // each drops one" double-count contract.
                _instanceRegistry.Track(instanceResult, assetOp);
                IGGProfiler.EndSample($"HC.Instantiate_{pAddress}");
            };

            int handleId = AllocateHandleId(assetOp);
            return new ContentHandle<GameObject>(assetOp, handleId, () => instanceResult);
        }

        internal ContentHandle<SceneInstance> LoadSceneAsync(string pAddress, LoadSceneMode pMode = LoadSceneMode.Single)
        {
            return LoadSceneAsync(pAddress, pMode, null);
        }

        internal ContentHandle<SceneInstance> LoadSceneAsync(string pAddress, LoadSceneMode pMode, LoadNetworkOptions pNetworkOptions)
        {
            HCLogger.LogVerbose($"LoadSceneAsync [{LogFields.ADDRESS}={pAddress}] mode={pMode}");

            if (string.IsNullOrEmpty(pAddress))
            {
                return CreateFailedSceneHandle(pAddress, ErrorCode.RESOURCE_KEY_INVALID, "Invalid scene address");
            }

            if (pNetworkOptions != null && pNetworkOptions.Mode == LoadAssetNetworkMode.QueryOnly)
            {
                return CreateFailedSceneHandle(pAddress, ErrorCode.SYSTEM_INVALID_STATE,
                    "LoadSceneAsync does not support LoadAssetNetworkMode.QueryOnly; use HyperContent.QueryLoadAvailability<SceneInstance>.");
            }

            // D2 § Catalog 段（scene 路径）：与 LoadAsync 共用同名前缀 HC.Load.Stage.Catalog_，
            // 关宏整段从 IL 擦除（[Conditional]）。
            IGGProfiler.BeginSample($"HC.Load.Stage.Catalog_{pAddress}");
            bool sceneCatalogResolved = _catalog.TryGetLocations(pAddress, typeof(SceneInstance), out var locations);
            IGGProfiler.EndSample($"HC.Load.Stage.Catalog_{pAddress}");

            if (sceneCatalogResolved && locations.Count > 0)
            {
                HCLogger.LogInfo($"[Load] Scene catalog resolved address={pAddress} locations={locations.Count}");
                var location = locations[0];
                LogLocationTree(pAddress, "SceneInstance", location);

                if (!TryConfirmRemoteDownloadsForLocation(location, pAddress, pNetworkOptions))
                {
                    // User declined a PromptBeforeDownload remote fetch (already logged a Warn).
                    // Surface as a failed handle so coroutine / await callers don't hang.
                    return CreateFailedSceneHandle(pAddress, ErrorCode.OPERATION_USER_DECLINED_REMOTE_DOWNLOAD,
                        "User declined remote bundle download", pLogError: false);
                }

                var sceneOp = _resourceManager.LoadSceneAsync(location, pMode);
                sceneOp.OnCompleted += completedOp =>
                {
                    if (completedOp.Status == OperationStatus.Succeeded)
                    {
                        HCLogger.LogInfo($"Scene load succeeded [{LogFields.ADDRESS}={pAddress}]");
                        LoadSucceeded?.Invoke(pAddress);
                    }
                    else
                    {
                        HCLogger.LogWarn($"Scene load failed [{LogFields.ADDRESS}={pAddress}] error={completedOp.Exception?.Message}");
                        LoadFailed?.Invoke(pAddress, completedOp.Exception);
                    }
                };

                int sceneHandleId = AllocateHandleId(sceneOp);
                return new ContentHandle<SceneInstance>(sceneOp, sceneHandleId,
                    () => new SceneInstance { Scene = sceneOp.ResultScene });
            }

            return CreateFailedSceneHandle(pAddress, ErrorCode.CATALOG_ENTRY_NOT_FOUND,
                "Scene address not found in catalog");
        }

        /// <summary>
        /// Wrap a synthetic failed <see cref="SceneOperation"/> in a terminal <see cref="ContentHandle{T}"/>
        /// for scene-load entry failures. AllocateHandleId returns 0 for synthetic ops (not tracked); the
        /// handle is already IsDone so its Completed callback fires immediately and await surfaces the error.
        /// </summary>
        private ContentHandle<SceneInstance> CreateFailedSceneHandle(string pAddress, int pErrorCode, string pMessage, bool pLogError = true)
        {
            var op = CreateFailedSceneOp(pAddress, pErrorCode, pMessage, pLogError);
            int handleId = AllocateHandleId(op);
            return new ContentHandle<SceneInstance>(op, handleId, () => new SceneInstance { Scene = op.ResultScene });
        }

        internal void Release(int pHandleId, AsyncOperationBase pOp)
        {
            if (pOp == null)
            {
                HCLogger.LogWarn("Attempting to release a null operation, ignored.");
                return;
            }
            if (pOp.IsSynthetic)
            {
                // Synthetic failure handles are never tracked in _activeHandles and hold no resource,
                // so releasing them is an idempotent no-op. This lets business code call Release on a
                // failed handle unconditionally (e.g. via IsValid checks) without a spurious
                // "double-release" warning or touching the cache.
                return;
            }
            if (!_activeHandles.Remove(pHandleId))
            {
                HCLogger.LogWarn($"Double-release detected for handleId={pHandleId} " +
                    $"[{LogFields.ADDRESS}={pOp.Location?.Address}], ignored.");
                return;
            }
#if HYPERCONTENT_TRACK_HANDLES
            _handleAcquisitions.Remove(pHandleId);
#endif
            HCLogger.LogVerbose($"Release handleId={pHandleId} [{LogFields.LOCATION_HASH}={pOp.LocationHash}] " +
                $"[{LogFields.ADDRESS}={pOp.Location?.Address}] [{LogFields.REF_COUNT}={pOp.RefCount}]");
            _resourceManager.Release(pOp);
        }

        internal void ReleaseInstance(GameObject instance)
        {
            if (instance == null)
            {
                HCLogger.LogWarn("Attempting to release a null instance, ignored.");
                return;
            }
            HCLogger.LogVerbose($"ReleaseInstance name={instance.name} id={instance.GetInstanceID()}");
            _instanceRegistry.Release(instance, _resourceManager);
        }

        internal bool HasResource(string address)
        {
            if (_catalog == null || string.IsNullOrEmpty(address)) return false;
            return _catalog.TryGetLocations(address, typeof(UnityEngine.Object), out _);
        }

        /// <summary>
        /// Replace in-memory catalog from a resolved on-disk path (e.g. after <see cref="CatalogLocator.ResolveLocalCatalogAsync"/>).
        /// Caller must ensure no conflicting loads; see CONVENTIONS §1.4 and INITIALIZATION_FLOW.md §8.
        /// </summary>
        internal bool TryReplaceRuntimeCatalog(string pCatalogPath)
        {
            if (string.IsNullOrEmpty(pCatalogPath))
                return false;

            var newCatalog = new LocalContentCatalog();
            // Carry the dependency load mode forward — settings.json (hence the mode) is fixed for the app
            // lifetime, so a reloaded catalog must keep the same mode as the one resolved at init.
            if (_catalog is LocalContentCatalog currentLocal)
                newCatalog.LoadMode = currentLocal.LoadMode;
            if (!newCatalog.Initialize(pCatalogPath))
                return false;

            _catalog?.Release();
            _catalog = newCatalog;
            HCLogger.LogInfo($"[Catalog] Runtime catalog replaced from disk: {pCatalogPath}");
            return true;
        }

        internal void Shutdown()
        {
            HCLogger.LogInfo("HyperContentImpl shutting down...");
            HCLogger.LogVerbose($"Shutdown — cache={_operationCache.Count}, instances={_instanceRegistry.Count}, " +
                $"activeHandles={_activeHandles.Count}");
            _operationCache.Clear();
            _instanceRegistry.Clear();
            _providerRegistry.Clear();
            _activeHandles.Clear();
#if HYPERCONTENT_TRACK_HANDLES
            _handleAcquisitions.Clear();
#endif
            _catalog?.Release();
            IsInitialized = false;
            HCLogger.LogInfo("HyperContentImpl shutdown complete");
        }

        private int AllocateHandleId(AsyncOperationBase pOp)
        {
            // Synthetic failure ops are not real cache-managed loads: they are never tracked in
            // _activeHandles (so they don't pollute leak diagnostics) and their Release is a no-op
            // (see Release below). Handle id 0 is never produced by the ++_nextHandleId counter, so it
            // is a safe "untracked" sentinel for the ContentHandle.
            if (pOp != null && pOp.IsSynthetic)
                return 0;

            int id = ++_nextHandleId;
            _activeHandles[id] = pOp;
#if HYPERCONTENT_TRACK_HANDLES
            // Skip = 2 hides AllocateHandleId itself and the LoadAsync/LoadSceneAsync wrapper;
            // the first user-meaningful frame will be the business call site (e.g. SomeManager.Foo()).
            var trace = new System.Diagnostics.StackTrace(2, true);
            _handleAcquisitions[id] = new HandleAcquisition
            {
                LocationHash = pOp?.LocationHash ?? 0,
                Address = pOp?.Location?.Address,
                Frame = UnityEngine.Time.frameCount,
                Time = UnityEngine.Time.realtimeSinceStartup,
                StackTrace = trace.ToString(),
            };
#endif
            return id;
        }

        /// <summary>
        /// Diagnostics-only: snapshot of every active handle with the op it owns a refcount on.
        /// Always populated from <see cref="_activeHandles"/> (source of truth). When
        /// HYPERCONTENT_TRACK_HANDLES is defined, frame / time / stack trace entries are also
        /// filled in from <see cref="_handleAcquisitions"/>; otherwise they receive default values.
        /// Output lists are appended to without allocation.
        /// </summary>
        internal void GetActiveHandlesSnapshot(
            List<int> pHandleIds,
            List<AsyncOperationBase> pOps,
            List<int> pFrames,
            List<float> pTimes,
            List<string> pStackTraces)
        {
            if (pHandleIds == null) return;
            foreach (var kv in _activeHandles)
            {
                pHandleIds.Add(kv.Key);
                pOps?.Add(kv.Value);
#if HYPERCONTENT_TRACK_HANDLES
                if (_handleAcquisitions.TryGetValue(kv.Key, out var acq))
                {
                    pFrames?.Add(acq.Frame);
                    pTimes?.Add(acq.Time);
                    pStackTraces?.Add(acq.StackTrace);
                }
                else
                {
                    pFrames?.Add(0);
                    pTimes?.Add(0f);
                    pStackTraces?.Add(null);
                }
#else
                pFrames?.Add(0);
                pTimes?.Add(0f);
                pStackTraces?.Add(null);
#endif
            }
        }

        private bool TryConfirmRemoteDownloadsForLocation(
            ResourceLocation pLocation,
            string pLogAddress,
            LoadNetworkOptions pNetworkOptions)
        {
            if (pNetworkOptions == null || pNetworkOptions.Mode != LoadAssetNetworkMode.PromptBeforeDownload)
                return true;
            if (_bundleStore == null)
                return true;

            if (pNetworkOptions.UserConfirmMissingBundleDownload == null)
            {
                HCLogger.LogError(ErrorCode.SYSTEM_INVALID_STATE,
                    "UserConfirmMissingBundleDownload is required when Mode is PromptBeforeDownload.");
                return false;
            }

            var missing = LoadNetworkPrecheck.CollectMissingRemoteForLocation(_catalog, _bundleStore, pLocation);
            if (missing.Count == 0)
                return true;

            var prompt = LoadNetworkPrecheck.ToMissingSummary(missing);
            if (pNetworkOptions.UserConfirmMissingBundleDownload(prompt))
                return true;

            HCLogger.LogWarn(
                $"[{LogFields.ERROR_CODE}={ErrorCode.OPERATION_USER_DECLINED_REMOTE_DOWNLOAD}] " +
                $"User declined remote bundle download for [{LogFields.ADDRESS}={pLogAddress}]");
            return false;
        }

        private AssetOperation<T> StartLoad<T>(string address, ResourceLocation location, LoadNetworkOptions pNetworkOptions = null)
            where T : UnityEngine.Object
        {
            if (!TryConfirmRemoteDownloadsForLocation(location, address, pNetworkOptions))
                return null;

            LogLocationTree(address, typeof(T).Name, location);

            var op = _resourceManager.LoadAsync<T>(location);
            op.OnCompleted += completedOp =>
            {
                if (completedOp.Status == OperationStatus.Succeeded)
                {
                    HCLogger.LogInfo($"Load succeeded [{LogFields.ADDRESS}={address}]");
                    LoadSucceeded?.Invoke(address);
                }
                else
                {
                    HCLogger.LogWarn($"Load failed [{LogFields.ADDRESS}={address}] error={completedOp.Exception?.Message}");
                    LoadFailed?.Invoke(address, completedOp.Exception);
                }
            };
            return op;
        }

        // HCLogger.LogVerbose is [Conditional("HYPERCONTENT_LOG_VERBOSE")], so only the final call is
        // erased when the macro is off — the StringBuilder allocation and recursive tree walk would
        // otherwise still run on every LoadAsync/LoadSceneAsync. Gate the whole body (and the helper)
        // behind the same macro, matching LocalContentCatalog.TryGetLocations.
        private static void LogLocationTree(string address, string typeName, ResourceLocation root)
        {
#if HYPERCONTENT_LOG_VERBOSE
            var sb = new System.Text.StringBuilder(512);
            sb.AppendLine($"StartLoad<{typeName}> [{LogFields.ADDRESS}={address}]");
            AppendLocation(sb, root, depth: 0);
            HCLogger.LogVerbose(sb.ToString());
#endif
        }

#if HYPERCONTENT_LOG_VERBOSE
        private static void AppendLocation(System.Text.StringBuilder sb, ResourceLocation loc, int depth)
        {
            string indent = depth == 0 ? "" : new string(' ', depth * 2) + "└─ ";
            sb.AppendLine($"{indent}[{loc.ProviderId}] " +
                $"internalId={loc.InternalId}  hash={loc.LocationHash}");

            for (int i = 0; i < loc.Dependencies.Count; i++)
                AppendLocation(sb, loc.Dependencies[i], depth + 1);
        }
#endif
    }
}
