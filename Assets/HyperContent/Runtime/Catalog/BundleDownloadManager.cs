using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Manages bundle download after catalog update.
    /// Internal class — users access via HyperContent facade.
    /// Owner: Owner3. Uses <see cref="IBundleDownloadQueue"/> only (no direct <see cref="IBundleTransport"/>).
    /// </summary>
    internal class BundleDownloadManager
    {
        private readonly ICatalog _catalog;
        private readonly IBundleStore _bundleStore;
        private readonly IBundleDownloadQueue _downloadQueue;

        public BundleDownloadManager(
            ICatalog pCatalog,
            IBundleStore pBundleStore,
            IBundleDownloadQueue pDownloadQueue)
        {
            _catalog = pCatalog;
            _bundleStore = pBundleStore;
            _downloadQueue = pDownloadQueue;
        }

        // ── Check API ──────────────────────────────────────────────────────

        /// <summary>
        /// Check all bundles that need to be downloaded (in catalog but not local).
        /// For mixed catalog: only bundles with contentLocation == Remote (2) are considered;
        /// StreamingAssets (3) bundles ship with the APK and are skipped.
        /// </summary>
        public DownloadCheckResult CheckAllPendingDownloads()
        {
            return CheckAllPendingDownloads(PendingBundleQueryScope.All);
        }

        /// <summary>
        /// Check pending remote bundle downloads, optionally restricted to <see cref="BundleTagFlags.Blocking"/> only
        /// (same missing rules as <see cref="DownloadAllBlockingAsync"/>).
        /// </summary>
        public DownloadCheckResult CheckAllPendingDownloads(PendingBundleQueryScope pScope)
        {
            var result = new DownloadCheckResult { success = true };
            var bundleList = new List<BundleDownloadInfo>();

            foreach (var bundleName in _catalog.GetAllBundleNames())
            {
                if (!_catalog.TryGetBundleInfo(bundleName, out var info))
                    continue;

                if (pScope == PendingBundleQueryScope.BlockingOnly &&
                    (info.TagFlags & BundleTagFlags.Blocking) == 0)
                    continue;

                if (info.Location == ContentLocation.Remote && NeedsDownload(bundleName, info))
                {
                    bundleList.Add(new BundleDownloadInfo
                    {
                        bundleName = bundleName,
                        sizeBytes = info.Size,
                        remoteUrl = info.RemoteRelativePath
                    });
                }
            }

            result.bundleList = bundleList;
            result.totalCount = bundleList.Count;
            result.totalSizeBytes = bundleList.Sum(b => b.sizeBytes);
            return result;
        }

        /// <summary>
        /// Check bundles needed to load a specific asset.
        /// </summary>
        public DownloadCheckResult CheckDownloadsForAsset(string pAssetPath)
        {
            return CheckDownloadsForAssets(new[] { pAssetPath });
        }

        /// <summary>
        /// Check bundles needed to load multiple assets (deduplicated).
        /// </summary>
        public DownloadCheckResult CheckDownloadsForAssets(IEnumerable<string> pAssetPaths)
        {
            var result = new DownloadCheckResult { success = true };
            var bundleSet = new HashSet<string>();

            foreach (var assetPath in pAssetPaths)
            {
                if (_catalog.TryGetLocations(assetPath, typeof(UnityEngine.Object), out var locations))
                {
                    foreach (var location in locations)
                    {
                        CollectBundleNamesRecursive(location, bundleSet);
                    }
                }
            }

            var bundleList = new List<BundleDownloadInfo>();
            foreach (var bundleName in bundleSet)
            {
                if (!_catalog.TryGetBundleInfo(bundleName, out var info))
                    continue;

                if (info.Location == ContentLocation.Remote && NeedsDownload(bundleName, info))
                {
                    bundleList.Add(new BundleDownloadInfo
                    {
                        bundleName = bundleName,
                        sizeBytes = info.Size,
                        remoteUrl = info.RemoteRelativePath
                    });
                }
            }

            result.bundleList = bundleList;
            result.totalCount = bundleList.Count;
            result.totalSizeBytes = bundleList.Sum(b => b.sizeBytes);
            return result;
        }

        /// <summary>
        /// Check if a bundle needs downloading: not cached, or cached but hash mismatch.
        /// </summary>
        private bool NeedsDownload(string pBundleName, BundleInfo pInfo)
        {
            if (!_bundleStore.Exists(pBundleName))
                return true;

            if (!string.IsNullOrEmpty(pInfo.Hash) &&
                !_bundleStore.VerifyHash(pBundleName, pInfo.Hash))
            {
                HCLogger.LogVerbose($"Bundle hash mismatch, needs re-download: {pBundleName}");
                return true;
            }

            return false;
        }

        internal static void CollectBundleNamesRecursive(ResourceLocation pLocation, HashSet<string> pBundleSet)
        {
            if (pLocation == null || pBundleSet == null)
                return;

            if (IsBundleProvider(pLocation.ProviderId))
            {
                string bundleKey = !string.IsNullOrEmpty(pLocation.Address)
                    ? pLocation.Address
                    : pLocation.InternalId;
                if (!string.IsNullOrEmpty(bundleKey))
                    pBundleSet.Add(bundleKey);
            }

            if (pLocation.Dependencies != null)
            {
                foreach (var dep in pLocation.Dependencies)
                    CollectBundleNamesRecursive(dep, pBundleSet);
            }
        }

        private static bool IsBundleProvider(string pProviderId)
        {
            return pProviderId == "BundleFileProvider" ||
                   pProviderId == "RemoteBundleProvider" ||
                   pProviderId == "PlayAssetDeliveryBundleProvider";
        }

        // ── Download API ───────────────────────────────────────────────────

        /// <summary>
        /// Download all pending bundles (<see cref="BundleDownloadPriority.Normal"/>).
        /// </summary>
        public void DownloadAllAsync(
            Action<DownloadProgress> pOnProgress,
            Action<DownloadResult> pOnComplete,
            CancellationToken pCancellationToken = default)
        {
            if (pCancellationToken.IsCancellationRequested)
            {
                pOnComplete?.Invoke(new DownloadResult { success = false, cancelled = true });
                return;
            }

            var check = CheckAllPendingDownloads();
            if (!check.success || check.totalCount == 0)
            {
                pOnComplete?.Invoke(new DownloadResult { success = true, downloadedCount = 0 });
                return;
            }

            DownloadBundlesAsync(
                check.bundleList.Select(b => b.bundleName),
                pOnProgress,
                pOnComplete,
                BundleDownloadPriority.Normal,
                pCancellationToken);
        }

        /// <summary>
        /// Download all <see cref="ContentLocation.Remote"/> bundles tagged <see cref="BundleTagFlags.Blocking"/> that are not yet satisfied locally
        /// (<see cref="BundleDownloadPriority.High"/>).
        /// </summary>
        public void DownloadAllBlockingAsync(
            Action<DownloadProgress> pOnProgress,
            Action<DownloadResult> pOnComplete,
            CancellationToken pCancellationToken = default)
        {
            if (pCancellationToken.IsCancellationRequested)
            {
                pOnComplete?.Invoke(new DownloadResult { success = false, cancelled = true });
                return;
            }

            var check = CheckAllPendingDownloads(PendingBundleQueryScope.BlockingOnly);
            if (!check.success || check.totalCount == 0)
            {
                pOnComplete?.Invoke(new DownloadResult { success = true, downloadedCount = 0 });
                return;
            }

            DownloadBundlesAsync(
                check.bundleList.Select(b => b.bundleName),
                pOnProgress,
                pOnComplete,
                BundleDownloadPriority.High,
                pCancellationToken);
        }

        /// <summary>
        /// Download specified bundles.
        /// </summary>
        public void DownloadBundlesAsync(
            IEnumerable<string> pBundleNames,
            Action<DownloadProgress> pOnProgress,
            Action<DownloadResult> pOnComplete,
            BundleDownloadPriority pPriority = BundleDownloadPriority.Normal,
            CancellationToken pCancellationToken = default)
        {
            if (pCancellationToken.IsCancellationRequested)
            {
                pOnComplete?.Invoke(new DownloadResult { success = false, cancelled = true });
                return;
            }

            var bundlesToDownload = new List<BundleDownloadInfo>();
            long totalBytes = 0;

            foreach (var bundleName in pBundleNames)
            {
                if (_catalog.TryGetBundleInfo(bundleName, out var info))
                {
                    bundlesToDownload.Add(new BundleDownloadInfo
                    {
                        bundleName = bundleName,
                        sizeBytes = info.Size,
                        remoteUrl = info.RemoteRelativePath
                    });
                    totalBytes += info.Size;
                }
            }

            if (bundlesToDownload.Count == 0)
            {
                pOnComplete?.Invoke(new DownloadResult { success = true });
                return;
            }

            EnqueueParallelDownloads(bundlesToDownload, totalBytes, pOnProgress, pOnComplete, pPriority, pCancellationToken);
        }

        private void EnqueueParallelDownloads(
            List<BundleDownloadInfo> pBundles,
            long pTotalBytes,
            Action<DownloadProgress> pOnProgress,
            Action<DownloadResult> pOnComplete,
            BundleDownloadPriority pPriority,
            CancellationToken pCancellationToken = default)
        {
            var result = new DownloadResult
            {
                success = true,
                failedBundleList = new List<string>()
            };

            int totalCount = pBundles.Count;
            int remaining = totalCount;
            long completedBytesLocal = 0;

            foreach (var bundle in pBundles)
            {
                var captured = bundle;
                _catalog.TryGetBundleInfo(captured.bundleName, out var infoForHash);
                _downloadQueue.Enqueue(new BundleDownloadEnqueueOptions
                {
                    RemoteRelativePath = captured.remoteUrl,
                    BundleName = captured.bundleName,
                    Hash = infoForHash?.Hash,
                    SizeBytes = captured.sizeBytes,
                    Priority = pPriority,
                    CancellationToken = pCancellationToken,
                    OnProgress = pProgress =>
                    {
                        int doneCount;
                        long baseBytes;
                        lock (result)
                        {
                            doneCount = result.downloadedCount + result.failedCount;
                            baseBytes = completedBytesLocal;
                        }

                        pOnProgress?.Invoke(new DownloadProgress
                        {
                            completedCount = doneCount,
                            totalCount = totalCount,
                            completedBytes = baseBytes + (long)(captured.sizeBytes * pProgress),
                            totalBytes = pTotalBytes,
                            currentBundleName = captured.bundleName,
                            currentBundleProgress = pProgress
                        });
                    },
                    OnComplete = pFetch =>
                    {
                        lock (result)
                        {
                            if (pFetch.Success && pFetch.Data != null)
                            {
                                if (_catalog.TryGetBundleInfo(captured.bundleName, out var info) &&
                                    _bundleStore.Save(captured.bundleName, pFetch.Data, info.Hash))
                                {
                                    result.downloadedCount++;
                                    completedBytesLocal += captured.sizeBytes;
                                    HCLogger.LogInfo($"Bundle downloaded: {captured.bundleName}");
                                }
                                else
                                {
                                    result.failedCount++;
                                    result.failedBundleList.Add(captured.bundleName);
                                }
                            }
                            else
                            {
                                result.failedCount++;
                                result.failedBundleList.Add(captured.bundleName);
                                HCLogger.LogError(ErrorCode.BUNDLE_LOAD_FAILED,
                                    $"Failed to download: {captured.bundleName}, error: {pFetch.ErrorMessage}");
                            }

                            int finished = result.downloadedCount + result.failedCount;
                            pOnProgress?.Invoke(new DownloadProgress
                            {
                                completedCount = finished,
                                totalCount = totalCount,
                                completedBytes = completedBytesLocal,
                                totalBytes = pTotalBytes,
                                currentBundleName = captured.bundleName,
                                currentBundleProgress = 1f
                            });
                        }

                        if (Interlocked.Decrement(ref remaining) == 0)
                        {
                            lock (result)
                            {
                                result.cancelled = pCancellationToken.IsCancellationRequested;
                                result.success = !result.cancelled && result.failedCount == 0;
                            }

                            pOnComplete?.Invoke(result);
                        }
                    }
                });
            }
        }
    }

    // ── Data Structures ────────────────────────────────────────────────────

    /// <summary>
    /// Result of download check.
    /// </summary>
    public class DownloadCheckResult
    {
        public bool success;
        public string error;
        public List<BundleDownloadInfo> bundleList;
        public int totalCount;
        public long totalSizeBytes;
    }

    /// <summary>
    /// Info about a single bundle to download.
    /// </summary>
    public class BundleDownloadInfo
    {
        public string bundleName;
        public long sizeBytes;

        /// <summary>
        /// CDN-relative path for the bundle file (same semantics as <see cref="BundleInfo.RemoteRelativePath"/>).
        /// Full URL is built only inside <see cref="IBundleTransport"/> via <see cref="HyperContentPaths.CombineRemoteCdnRequestUrl"/> at HTTP time.
        /// </summary>
        public string remoteUrl;
    }

    /// <summary>
    /// Download progress.
    /// </summary>
    public class DownloadProgress
    {
        public int completedCount;
        public int totalCount;
        public long completedBytes;
        public long totalBytes;
        public string currentBundleName;
        public float currentBundleProgress;
    }

    /// <summary>
    /// Download result.
    /// </summary>
    public class DownloadResult
    {
        public bool success;
        public bool cancelled;
        public int downloadedCount;
        public int failedCount;
        public List<string> failedBundleList;
        public string error;
    }
}
