#if UNITY_ANDROID
using System;
using System.Collections.Generic;
using UnityEngine;
using Google.Play.AssetDelivery;
using com.igg.hypercontent.shared;
using com.igg.core;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Google Play Asset Delivery (PAD) bundle provider — equivalent to Addressables'
    /// <c>PlayAssetDeliveryResource</c>, expressed in HyperContent's provider/handle protocol.
    ///
    /// Responsibilities:
    ///  1. Honor hot-update first: if <see cref="IBundleStore"/> already has the bundle (downloaded
    ///     post-publish from CDN), load straight from cache and skip PAD entirely.
    ///  2. Otherwise, route the bundle to its asset pack via <see cref="IPlayAssetPackRouter"/>,
    ///     await pack availability, then call <c>AssetBundle.LoadFromFileAsync(path, 0, offset)</c>
    ///     so Unity can mmap the bundle directly out of the (split-)APK without a copy.
    ///  3. On any PAD failure (pack missing, status != Available, AssetLocation null), fall back to
    ///     <see cref="BundleFileProvider"/> so non-Play installs (universal APK from <c>bundletool</c>)
    ///     still work via the legacy <c>jar:file://.../base.apk!/assets/...</c> path.
    ///
    /// v1 scope: <see cref="AssetPackDeliveryMode.InstallTime"/> only — covers <c>AABBuilder</c>'s
    /// current configuration, no progress UI or user prompts. Hooks for FastFollow / OnDemand are
    /// commented inline so we can layer those on without changing the public API.
    ///
    /// Registers with <see cref="BundleFileProvider.ID"/> so catalog routing stays transparent;
    /// <see cref="HyperContentImpl"/> picks PAD vs raw file via the registration block, the catalog
    /// itself doesn't change.
    /// </summary>
    internal sealed class PlayAssetDeliveryBundleProvider : IContentProvider
    {
        private readonly IBundleLoader _bundleLoader;
        private readonly BundleFileProvider _fallbackProvider;
        private readonly IPlayAssetPackRouter _packRouter;
        private readonly IBundleStore _bundleStore;

        // One pack request per asset pack name — multiple bundles in the same pack share a single
        // RetrieveAssetPackAsync call. Keyed by pack name; values are the in-flight or completed
        // PlayAssetPackRequest. Cleared lazily if the request is found to have failed.
        private readonly Dictionary<string, PlayAssetPackRequest> _packRequests =
            new Dictionary<string, PlayAssetPackRequest>(StringComparer.Ordinal);

        public string ProviderId => BundleFileProvider.ID;

        internal PlayAssetDeliveryBundleProvider(
            IBundleLoader pBundleLoader,
            BundleFileProvider pFallbackProvider,
            IPlayAssetPackRouter pPackRouter,
            IBundleStore pBundleStore = null)
        {
            _bundleLoader = pBundleLoader ?? throw new ArgumentNullException(nameof(pBundleLoader));
            _fallbackProvider = pFallbackProvider ?? throw new ArgumentNullException(nameof(pFallbackProvider));
            _packRouter = pPackRouter ?? throw new ArgumentNullException(nameof(pPackRouter));
            _bundleStore = pBundleStore;
        }

        public void Provide(ProvideHandle pHandle)
        {
            string internalId = pHandle.Location.InternalId;
            HCLogger.LogVerbose($"[PAD] Provide [{LogFields.BUNDLE_NAME}={internalId}]");

            if (_bundleLoader.IsLoaded(internalId))
            {
                if (_bundleLoader.TryGetBundle(internalId, out var existing))
                {
                    HCLogger.LogVerbose($"[PAD] Already loaded [{LogFields.BUNDLE_NAME}={internalId}]");
                    pHandle.Complete(existing);
                    return;
                }
            }

            // Hot-update path: if a CDN-fetched copy already exists in the local store, use it.
            // PAD only knows about the version baked into the AAB, so cache must win to honor patches.
            if (TryLoadFromBundleStore(internalId, pHandle))
                return;

            string packName = _packRouter.ResolvePackName(internalId);
            if (string.IsNullOrEmpty(packName))
            {
                HCLogger.LogVerbose($"[PAD] No pack mapping for [{LogFields.BUNDLE_NAME}={internalId}], delegating to fallback");
                _fallbackProvider.Provide(pHandle);
                return;
            }

            try
            {
                var packRequest = GetOrCreatePackRequest(packName);
                if (packRequest.IsDone)
                {
                    OnPackReady(packName, packRequest, internalId, pHandle);
                }
                else
                {
                    packRequest.Completed += pRequest => OnPackReady(packName, pRequest, internalId, pHandle);
                }
            }
            catch (Exception e)
            {
                HCLogger.LogWarn($"[PAD] RetrieveAssetPackAsync threw, falling back [{LogFields.BUNDLE_NAME}={internalId}] " +
                    $"pack={packName} error={e.Message}");
                _fallbackProvider.Provide(pHandle);
            }
        }

        public void Release(ProvideHandle pHandle)
        {
            string internalId = pHandle.Location.InternalId;
            HCLogger.LogVerbose($"[PAD] Release [{LogFields.BUNDLE_NAME}={internalId}]");
            if (_bundleLoader.IsLoaded(internalId))
                _bundleLoader.Unload(internalId, false);
        }

        // ── Pack request cache ──────────────────────────────────────────────

        private PlayAssetPackRequest GetOrCreatePackRequest(string pPackName)
        {
            if (_packRequests.TryGetValue(pPackName, out var existing))
            {
                if (!existing.IsDone || existing.Error == AssetDeliveryErrorCode.NoError)
                    return existing;

                // Previously-failed request — drop it and retry. PlayAssetDelivery dedupes internally
                // so this won't trigger a duplicate download if the pack actually exists now.
                HCLogger.LogVerbose($"[PAD] Discarding stale failed pack request pack={pPackName} error={existing.Error}");
                _packRequests.Remove(pPackName);
            }

            HCLogger.LogInfo($"[PAD] RetrieveAssetPackAsync pack={pPackName}");
#if ENABLE_PROFILERLOG
            // D2 § PAD pack 拉取段：AAB InstallTime 模式下首次访问 pack 经 Play Core JNI，部分中低端
            // 设备上 200ms+；后续同 pack 复用 _packRequests 不会再走这里，所以 sample 只反映 "首次拉取"
            // 的 wall clock。pPackName 作为 sample 名后缀，多 pack 时数据可分组查看。
            //
            // 关宏整段 #if 包：因为 EndSample 注册在 fresh.Completed lambda 里，lambda 关宏后 body 为空
            // 但实例 + closure（捕获 packSample）仍会进 IL，必须 #if 包含订阅本身才能彻底擦除。
            //
            // RetrieveAssetPackAsync 同步完成的概率低（首次必须等 Play Core），但仍兜底处理：
            // PlayAssetPackRequest.Completed 已完成时再 += 不会自动 invoke（与 AsyncOperationBase
            // 的 add-访问器不同），所以同步路径用 fresh.IsDone 主动 EndSample。
            string packSample = $"HC.PAD.Pack_{pPackName}";
            IGGProfiler.BeginSample(packSample);
#endif
            var fresh = PlayAssetDelivery.RetrieveAssetPackAsync(pPackName);
#if ENABLE_PROFILERLOG
            if (fresh.IsDone)
                IGGProfiler.EndSample(packSample);
            else
                fresh.Completed += _ => IGGProfiler.EndSample(packSample);
#endif
            _packRequests[pPackName] = fresh;
            return fresh;
        }

        // ── Pack ready → resolve asset location → mmap load ────────────────

        private void OnPackReady(
            string pPackName,
            PlayAssetPackRequest pRequest,
            string pInternalId,
            ProvideHandle pHandle)
        {
            if (pRequest.Error != AssetDeliveryErrorCode.NoError)
            {
                HCLogger.LogWarn($"[PAD] Pack error, falling back [{LogFields.BUNDLE_NAME}={pInternalId}] " +
                    $"pack={pPackName} error={pRequest.Error}");
                _fallbackProvider.Provide(pHandle);
                return;
            }

            // v1 (InstallTime): pack should already be Available immediately after install.
            // OnDemand/FastFollow hook: branch here on Status (Pending/Retrieving/RequiresUserConfirmation/
            // WaitingForWifi/...) and surface progress via pRequest.DownloadProgress + pHandle.UpdateProgress
            // before re-checking Status. Not implemented in v1 because AABBuilder only registers
            // AssetPackDeliveryMode.InstallTime today.
            if (pRequest.Status != AssetDeliveryStatus.Available)
            {
                HCLogger.LogWarn($"[PAD] Pack not available, falling back [{LogFields.BUNDLE_NAME}={pInternalId}] " +
                    $"pack={pPackName} status={pRequest.Status}");
                _fallbackProvider.Provide(pHandle);
                return;
            }

            string assetName = _packRouter.ResolveAssetName(pInternalId);
            AssetLocation assetLocation;
            try
            {
                assetLocation = pRequest.GetAssetLocation(assetName);
            }
            catch (Exception e)
            {
                HCLogger.LogWarn($"[PAD] GetAssetLocation threw, falling back [{LogFields.BUNDLE_NAME}={pInternalId}] " +
                    $"pack={pPackName} asset={assetName} error={e.Message}");
                _fallbackProvider.Provide(pHandle);
                return;
            }

            if (assetLocation == null)
            {
                HCLogger.LogWarn($"[PAD] AssetLocation null, falling back [{LogFields.BUNDLE_NAME}={pInternalId}] " +
                    $"pack={pPackName} asset={assetName}");
                _fallbackProvider.Provide(pHandle);
                return;
            }

            HCLogger.LogInfo($"[PAD] Loading via PAD [{LogFields.BUNDLE_NAME}={pInternalId}] " +
                $"pack={pPackName} path={assetLocation.Path} offset={assetLocation.Offset} size={assetLocation.Size}");

            // 元数据日志：PAD 主路径下 _bundleLoader.LoadFromFileAsync 收到的 filePath
            // 是 base.apk / split-apk 整包路径，不能用 FileInfo 取 size（会拿到整个 apk
            // 字节数）。但 PAD API 已经在 assetLocation.Size 把 bundle 真实字节数喂到嘴边，
            // 直接用 LogBundleSizeBytes 写出，无 IO。
            // 注：HCLogger.LogBundleSizeBytes 自带 [Conditional("ENABLE_PROFILERLOG")]
            // 守卫，关宏后调用点 + 参数表达式（assetLocation.Size 取值）整体擦除，所以
            // 不需要再用 #if 包；放在 #if 块外面让职责清晰：性能元数据 vs IGGProfiler sample
            // 是两套独立的诊断输出，都对 ENABLE_PROFILERLOG 响应。
            HCLogger.LogBundleSizeBytes(pInternalId, assetLocation.Size, "pad");

#if ENABLE_PROFILERLOG
            // D2 hotfix（2026-05-21）— PAD 主路径 BundleIO 段埋点：
            // D2 初版只在 BundleFileProvider 包了 HC.BundleIO_<bundle>，但 Android 真机开了
            // GOOGLE_PLAY_ASSET_DELIVERY 宏后 PAD provider 替代 BundleFileProvider 成为主路径，
            // BundleFileProvider 仅在 PAD 失败时作为 fallback 走——意味着主路径 bundle mmap + 头反序列化
            // wall clock 完全不可观测（实测 MainChatWindow 首次加载 Schedule 段 105ms 中大头都在这段）。
            // 与 BundleFileProvider 同名 sample HC.BundleIO_<bundle>，业务侧分析时无须区分两条路径。
            //
            // 跨 lambda 必须 #if 整段包：lambda body 还有非 [Conditional] 的业务逻辑（pHandle.Complete /
            // _fallbackProvider.Provide），不能用 #if 包整个 lambda；BeginSample/EndSample 只能单独 #if。
            // EndSample 必须放在 lambda 入口最早处，无论 pBundle 是否 null——失败 fallback 路径会进
            // _fallbackProvider.Provide → BundleFileProvider 又会 BeginSample 同名，必须先 End 防止冲突。
            string padBundleIOSample = $"HC.BundleIO_{pInternalId}";
            IGGProfiler.BeginSample(padBundleIOSample);
#endif
            _bundleLoader.LoadFromFileAsync(pInternalId, assetLocation.Path, assetLocation.Offset, pBundle =>
            {
#if ENABLE_PROFILERLOG
                IGGProfiler.EndSample(padBundleIOSample);
#endif
                if (pBundle != null)
                {
                    HCLogger.LogInfo($"[PAD] Loaded [{LogFields.BUNDLE_NAME}={pInternalId}] pack={pPackName}");
                    pHandle.Complete(pBundle);
                }
                else
                {
                    HCLogger.LogWarn($"[PAD] LoadFromFileAsync returned null, falling back [{LogFields.BUNDLE_NAME}={pInternalId}] " +
                        $"pack={pPackName} path={assetLocation.Path} offset={assetLocation.Offset}");
                    _fallbackProvider.Provide(pHandle);
                }
            });
        }

        // ── Hot-update cache shortcut ──────────────────────────────────────

        private bool TryLoadFromBundleStore(string pInternalId, ProvideHandle pHandle)
        {
            if (_bundleStore == null || !_bundleStore.Exists(pInternalId))
                return false;

            string localPath = _bundleStore.GetLocalPath(pInternalId);
            HCLogger.LogVerbose($"[PAD] Bundle in local store, bypassing PAD [{LogFields.BUNDLE_NAME}={pInternalId}] path={localPath}");

            // 元数据日志：hot-update 路径下 localPath 是持久目录真实磁盘路径，可直接 FileInfo 取 size。
            // source="store" 区分这是 CDN 热更过来的副本，分析时方便对照 PAD 主路径（apk 内 mmap）的 IO 特征。
            HCLogger.LogBundleSize(pInternalId, localPath, "store");

#if ENABLE_PROFILERLOG
            // D2 hotfix（2026-05-21）— PAD hot-update 路径 BundleIO 段埋点：
            // 与上面 OnPackReady 中的 BundleIO 埋点同名 sample，业务侧无须区分 PAD 主路径 vs hot-update 路径。
            // 失败 fallback 时，OnPackReady 会再次 BeginSample 同名 sample——所以 EndSample 必须放在
            // lambda 入口最早处，先于任何分支判断，确保 fallback 触发时上一次 sample 已正确 End。
            string padStoreBundleIOSample = $"HC.BundleIO_{pInternalId}";
            IGGProfiler.BeginSample(padStoreBundleIOSample);
#endif
            _bundleLoader.LoadFromFileAsync(pInternalId, localPath, pBundle =>
            {
#if ENABLE_PROFILERLOG
                IGGProfiler.EndSample(padStoreBundleIOSample);
#endif
                if (pBundle != null)
                {
                    HCLogger.LogInfo($"[PAD] Loaded from store [{LogFields.BUNDLE_NAME}={pInternalId}]");
                    pHandle.Complete(pBundle);
                }
                else
                {
                    HCLogger.LogWarn($"[PAD] Store load failed, retrying via PAD [{LogFields.BUNDLE_NAME}={pInternalId}]");
                    string packName = _packRouter.ResolvePackName(pInternalId);
                    if (string.IsNullOrEmpty(packName))
                    {
                        _fallbackProvider.Provide(pHandle);
                        return;
                    }
                    try
                    {
                        var packRequest = GetOrCreatePackRequest(packName);
                        if (packRequest.IsDone)
                            OnPackReady(packName, packRequest, pInternalId, pHandle);
                        else
                            packRequest.Completed += pRequest => OnPackReady(packName, pRequest, pInternalId, pHandle);
                    }
                    catch (Exception e)
                    {
                        HCLogger.LogWarn($"[PAD] Retry-via-PAD threw, falling back [{LogFields.BUNDLE_NAME}={pInternalId}] error={e.Message}");
                        _fallbackProvider.Provide(pHandle);
                    }
                }
            });
            return true;
        }
    }
}
#endif
