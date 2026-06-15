using System;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// HTTP download + local cache for remote bundles.
    /// Enqueues through <see cref="IBundleDownloadQueue"/> (single consumer of <see cref="IBundleTransport"/>; supports merge-by-relative-path).
    /// See ARCHITECTURE.md section 6.4.
    /// </summary>
    internal sealed class RemoteBundleProvider : IContentProvider
    {
        public const string ID = "RemoteBundleProvider";

        private readonly IBundleLoader _bundleLoader;
        private readonly IBundleDownloadQueue _downloadQueue;
        private readonly IBundleStore _bundleStore;

        internal RemoteBundleProvider(
            IBundleLoader pBundleLoader,
            IBundleDownloadQueue pDownloadQueue,
            IBundleStore pBundleStore = null)
        {
            _bundleLoader = pBundleLoader ?? throw new ArgumentNullException(nameof(pBundleLoader));
            _downloadQueue = pDownloadQueue ?? throw new ArgumentNullException(nameof(pDownloadQueue));
            _bundleStore = pBundleStore;
        }

        public string ProviderId => ID;

        public void Provide(ProvideHandle handle)
        {
            string internalId = handle.Location.InternalId;
            if (!string.IsNullOrEmpty(internalId) && internalId.IndexOf("://", StringComparison.Ordinal) < 0)
                NamingRules.RequireCatalogBundleRelativePath(internalId, "RemoteBundleProvider.InternalId");

            string bundleName = !string.IsNullOrEmpty(handle.Location.Address)
                ? handle.Location.Address
                : ExtractBundleName(internalId);
            HCLogger.LogVerbose($"[RemoteBundleProvider] Provide [{LogFields.BUNDLE_NAME}={bundleName}] relative={internalId}");

            if (_bundleLoader.IsLoaded(bundleName))
            {
                if (_bundleLoader.TryGetBundle(bundleName, out var existing))
                {
                    HCLogger.LogVerbose($"[RemoteBundleProvider] Already loaded [{LogFields.BUNDLE_NAME}={bundleName}]");
                    handle.Complete(existing);
                    return;
                }
            }

            if (_bundleStore != null && _bundleStore.Exists(bundleName))
            {
                string localPath = _bundleStore.GetLocalPath(bundleName);
                HCLogger.LogVerbose($"[RemoteBundleProvider] Found in local store [{LogFields.BUNDLE_NAME}={bundleName}] " +
                    $"path={localPath}");
                _bundleLoader.LoadFromFileAsync(bundleName, localPath, bundle =>
                {
                    if (bundle != null)
                    {
                        HCLogger.LogInfo($"[RemoteBundleProvider] Loaded from cache [{LogFields.BUNDLE_NAME}={bundleName}]");
                        handle.Complete(bundle);
                    }
                    else
                    {
                        HCLogger.LogWarn($"[RemoteBundleProvider] Cache load failed, downloading [{LogFields.BUNDLE_NAME}={bundleName}]");
                        DownloadAndLoad(internalId, bundleName, handle);
                    }
                });
                return;
            }

            HCLogger.LogVerbose($"[RemoteBundleProvider] Not cached, downloading [{LogFields.BUNDLE_NAME}={bundleName}]");
            DownloadAndLoad(internalId, bundleName, handle);
        }

        public void Release(ProvideHandle handle)
        {
            string bundleName = !string.IsNullOrEmpty(handle.Location.Address)
                ? handle.Location.Address
                : ExtractBundleName(handle.Location.InternalId);
            HCLogger.LogVerbose($"[RemoteBundleProvider] Release [{LogFields.BUNDLE_NAME}={bundleName}]");
            if (_bundleLoader.IsLoaded(bundleName))
                _bundleLoader.Unload(bundleName, false);
        }

        private void DownloadAndLoad(string pRelativePath, string pBundleName, ProvideHandle pHandle)
        {
            string hash = pHandle.Location.Data as string;
            HCLogger.LogInfo($"[RemoteBundleProvider] Enqueue download [{LogFields.BUNDLE_NAME}={pBundleName}] relative={pRelativePath}");

            _downloadQueue.Enqueue(new BundleDownloadEnqueueOptions
            {
                RemoteRelativePath = pRelativePath,
                BundleName = pBundleName,
                Hash = hash,
                SizeBytes = 0,
                Priority = BundleDownloadPriority.High,
                OnComplete = pResult => OnQueuedDownloadCompleted(pResult, pBundleName, pRelativePath, hash, pHandle)
            });
        }

        private void OnQueuedDownloadCompleted(
            FetchResult pResult,
            string pBundleName,
            string pRelativePath,
            string pHash,
            ProvideHandle pHandle)
        {
            try
            {
                byte[] data = pResult.Data;

                if (!pResult.Success || data == null)
                {
                    HCLogger.LogWarn($"[RemoteBundleProvider] Download failed [{LogFields.BUNDLE_NAME}={pBundleName}] " +
                        $"code={pResult.ErrorCode} msg={pResult.ErrorMessage}");
                    pHandle.Fail(new Exception(
                        $"Download failed: {pRelativePath} (code={pResult.ErrorCode}, msg={pResult.ErrorMessage})"));
                    return;
                }

                HCLogger.LogVerbose($"[RemoteBundleProvider] Downloaded [{LogFields.BUNDLE_NAME}={pBundleName}] " +
                    $"[{LogFields.SIZE_BYTES}={data.Length}]");

                if (_bundleStore != null)
                {
                    HCLogger.LogVerbose($"[RemoteBundleProvider] Saving to store [{LogFields.BUNDLE_NAME}={pBundleName}]");
                    _bundleStore.Save(pBundleName, data, pHash);
                    string localPath = _bundleStore.GetLocalPath(pBundleName);
                    _bundleLoader.LoadFromFileAsync(pBundleName, localPath, pBundle =>
                    {
                        if (pBundle != null)
                        {
                            HCLogger.LogInfo($"[RemoteBundleProvider] Downloaded & loaded [{LogFields.BUNDLE_NAME}={pBundleName}]");
                            pHandle.Complete(pBundle);
                        }
                        else
                        {
                            HCLogger.LogWarn($"[RemoteBundleProvider] File load failed after save, falling back to memory " +
                                $"[{LogFields.BUNDLE_NAME}={pBundleName}]");
                            LoadFromMemoryFallback(pBundleName, data, pHandle);
                        }
                    });
                }
                else
                {
                    HCLogger.LogVerbose($"[RemoteBundleProvider] No store, loading from memory [{LogFields.BUNDLE_NAME}={pBundleName}]");
                    LoadFromMemoryFallback(pBundleName, data, pHandle);
                }
            }
            catch (Exception e)
            {
                HCLogger.LogError($"[RemoteBundleProvider] Unexpected error after download " +
                    $"[{LogFields.BUNDLE_NAME}={pBundleName}]: {e.Message}");
                pHandle.Fail(e);
            }
        }

        private void LoadFromMemoryFallback(string pBundleName, byte[] pData, ProvideHandle pHandle)
        {
            HCLogger.LogVerbose($"[RemoteBundleProvider] LoadFromMemory [{LogFields.BUNDLE_NAME}={pBundleName}] " +
                $"[{LogFields.SIZE_BYTES}={pData.Length}]");
            _bundleLoader.LoadFromMemoryAsync(pBundleName, pData, pBundle =>
            {
                if (pBundle != null)
                {
                    HCLogger.LogInfo($"[RemoteBundleProvider] Loaded from memory [{LogFields.BUNDLE_NAME}={pBundleName}]");
                    pHandle.Complete(pBundle);
                }
                else
                {
                    pHandle.Fail(new Exception($"Failed to load bundle from memory: {pBundleName}"));
                }
            });
        }

        private static string ExtractBundleName(string pUrlOrPath)
        {
            if (string.IsNullOrEmpty(pUrlOrPath)) return pUrlOrPath;
            int lastSlash = pUrlOrPath.LastIndexOf('/');
            return lastSlash >= 0 ? pUrlOrPath.Substring(lastSlash + 1) : pUrlOrPath;
        }
    }
}
