using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// Manages content updates by comparing catalogs and downloading missing bundles
    /// </summary>
    public class ContentUpdateManager
    {
        private IContentCatalog _currentCatalog;
        private IContentCatalog _remoteCatalog;
        private IBundleStore _bundleStore;
        private IBundleTransport _bundleTransport;
        
        /// <summary>
        /// Update result
        /// </summary>
        public class UpdateResult
        {
            public bool Success { get; set; }
            public int BundlesToDownload { get; set; }
            public long TotalSizeBytes { get; set; }
            public int BundlesDownloaded { get; set; }
            public int BundlesFailed { get; set; }
            public List<string> FailedBundles { get; set; } = new List<string>();
            public string ErrorMessage { get; set; }
        }
        
        public ContentUpdateManager(IContentCatalog currentCatalog, IContentCatalog remoteCatalog, 
            IBundleStore bundleStore, IBundleTransport bundleTransport)
        {
            _currentCatalog = currentCatalog;
            _remoteCatalog = remoteCatalog;
            _bundleStore = bundleStore;
            _bundleTransport = bundleTransport;
        }
        
        /// <summary>
        /// Check for updates and return bundles that need to be downloaded
        /// </summary>
        public UpdateResult CheckForUpdates()
        {
            var result = new UpdateResult();
            
            if (_remoteCatalog == null || !_remoteCatalog.IsValid)
            {
                result.ErrorMessage = "Remote catalog is not available";
                return result;
            }
            
            if (_currentCatalog == null || !_currentCatalog.IsValid)
            {
                result.ErrorMessage = "Current catalog is not available";
                return result;
            }
            
            // Compare catalogs and find missing/updated bundles
            var bundlesToDownload = new List<BundleInfo>();
            long totalSize = 0;
            
            foreach (var bundleName in _remoteCatalog.GetAllBundleNames())
            {
                if (!_remoteCatalog.TryGetBundleInfo(bundleName, out var remoteBundleInfo))
                {
                    continue;
                }
                
                // Check if bundle needs to be downloaded
                bool needsDownload = false;
                
                if (remoteBundleInfo.Location == ContentLocation.Remote)
                {
                    // Check if bundle exists locally
                    if (!_bundleStore.Exists(bundleName))
                    {
                        needsDownload = true;
                    }
                    else
                    {
                        // Check if version or hash changed
                        if (_currentCatalog.TryGetBundleInfo(bundleName, out var currentBundleInfo))
                        {
                            if (currentBundleInfo.Version < remoteBundleInfo.Version ||
                                currentBundleInfo.Hash != remoteBundleInfo.Hash)
                            {
                                needsDownload = true;
                            }
                        }
                        else
                        {
                            // Bundle not in current catalog, but exists locally - verify hash
                            if (!_bundleStore.VerifyHash(bundleName, remoteBundleInfo.Hash))
                            {
                                needsDownload = true;
                            }
                        }
                    }
                }
                
                if (needsDownload)
                {
                    bundlesToDownload.Add(remoteBundleInfo);
                    totalSize += remoteBundleInfo.Size;
                }
            }
            
            result.BundlesToDownload = bundlesToDownload.Count;
            result.TotalSizeBytes = totalSize;
            result.Success = true;
            
            return result;
        }
        
        /// <summary>
        /// Download missing bundles asynchronously
        /// </summary>
        public void UpdateContentAsync(Action<UpdateResult> onComplete, Action<float> onProgress = null)
        {
            var checkResult = CheckForUpdates();
            
            if (!checkResult.Success || checkResult.BundlesToDownload == 0)
            {
                onComplete?.Invoke(checkResult);
                return;
            }
            
            // Get bundles to download
            var bundlesToDownload = new List<BundleInfo>();
            foreach (var bundleName in _remoteCatalog.GetAllBundleNames())
            {
                if (_remoteCatalog.TryGetBundleInfo(bundleName, out var bundleInfo) &&
                    bundleInfo.Location == ContentLocation.Remote)
                {
                    bool needsDownload = !_bundleStore.Exists(bundleName);
                    
                    if (!needsDownload && _currentCatalog.TryGetBundleInfo(bundleName, out var currentInfo))
                    {
                        needsDownload = currentInfo.Version < bundleInfo.Version ||
                                       currentInfo.Hash != bundleInfo.Hash;
                    }
                    
                    if (needsDownload)
                    {
                        bundlesToDownload.Add(bundleInfo);
                    }
                }
            }
            
            // Start downloading
            var result = new UpdateResult
            {
                Success = true,
                BundlesToDownload = bundlesToDownload.Count,
                TotalSizeBytes = bundlesToDownload.Sum(b => b.Size)
            };
            
            if (bundlesToDownload.Count == 0)
            {
                onComplete?.Invoke(result);
                return;
            }
            
            // Download bundles sequentially (can be parallelized)
            DownloadBundlesSequentially(bundlesToDownload, result, onProgress, onComplete);
        }
        
        private void DownloadBundlesSequentially(List<BundleInfo> bundles, UpdateResult result, 
            Action<float> onProgress, Action<UpdateResult> onComplete)
        {
            int currentIndex = 0;
            
            Action downloadNext = null;
            downloadNext = () =>
            {
                if (currentIndex >= bundles.Count)
                {
                    result.Success = result.BundlesFailed == 0;
                    onComplete?.Invoke(result);
                    return;
                }
                
                var bundle = bundles[currentIndex];
                currentIndex++;
                
                // Update progress
                float progress = (float)(currentIndex - 1) / bundles.Count;
                onProgress?.Invoke(progress);
                
                // Download bundle
                if (string.IsNullOrEmpty(bundle.RemoteUrl))
                {
                    Debug.LogError($"[HyperContent] Bundle {bundle.Name} has no remote URL");
                    result.BundlesFailed++;
                    result.FailedBundles.Add(bundle.Name);
                    downloadNext();
                    return;
                }
                
                _bundleTransport.DownloadAsync(
                    bundle.RemoteUrl,
                    (downloadProgress) =>
                    {
                        // Update progress for current bundle
                        float overallProgress = ((currentIndex - 1) + downloadProgress) / bundles.Count;
                        onProgress?.Invoke(overallProgress);
                    },
                    (fetchResult) =>
                    {
                        if (fetchResult.Success)
                        {
                            // Get downloaded data (need to modify FetchResult to include data)
                            // For now, we'll need to download synchronously or modify interface
                            // This is a limitation - we need FetchResult to include data
                            Debug.LogWarning($"[HyperContent] Async download completed but data not available in FetchResult");
                            
                            // Fallback: download synchronously
                            var syncResult = _bundleTransport.Download(bundle.RemoteUrl, out byte[] data);
                            if (syncResult.Success && data != null)
                            {
                                // Save to store
                                if (_bundleStore.Save(bundle.Name, data, bundle.Hash))
                                {
                                    result.BundlesDownloaded++;
                                    Debug.Log($"[HyperContent] Bundle downloaded: {bundle.Name}");
                                }
                                else
                                {
                                    result.BundlesFailed++;
                                    result.FailedBundles.Add(bundle.Name);
                                    Debug.LogError($"[HyperContent] Failed to save bundle: {bundle.Name}");
                                }
                            }
                            else
                            {
                                result.BundlesFailed++;
                                result.FailedBundles.Add(bundle.Name);
                                Debug.LogError($"[HyperContent] Failed to download bundle: {bundle.Name}, error: {syncResult.ErrorMessage}");
                            }
                        }
                        else
                        {
                            result.BundlesFailed++;
                            result.FailedBundles.Add(bundle.Name);
                            Debug.LogError($"[HyperContent] Failed to download bundle: {bundle.Name}, error: {fetchResult.ErrorMessage}");
                        }
                        
                        // Download next bundle
                        downloadNext();
                    }
                );
            };
            
            // Start downloading
            downloadNext();
        }
        
        /// <summary>
        /// Download missing bundles synchronously (blocking)
        /// </summary>
        public UpdateResult UpdateContent()
        {
            var checkResult = CheckForUpdates();
            
            if (!checkResult.Success || checkResult.BundlesToDownload == 0)
            {
                return checkResult;
            }
            
            var result = new UpdateResult
            {
                Success = true,
                BundlesToDownload = checkResult.BundlesToDownload,
                TotalSizeBytes = checkResult.TotalSizeBytes
            };
            
            // Get bundles to download
            var bundlesToDownload = new List<BundleInfo>();
            foreach (var bundleName in _remoteCatalog.GetAllBundleNames())
            {
                if (_remoteCatalog.TryGetBundleInfo(bundleName, out var bundleInfo) &&
                    bundleInfo.Location == ContentLocation.Remote)
                {
                    bool needsDownload = !_bundleStore.Exists(bundleName);
                    
                    if (!needsDownload && _currentCatalog.TryGetBundleInfo(bundleName, out var currentInfo))
                    {
                        needsDownload = currentInfo.Version < bundleInfo.Version ||
                                       currentInfo.Hash != bundleInfo.Hash;
                    }
                    
                    if (needsDownload)
                    {
                        bundlesToDownload.Add(bundleInfo);
                    }
                }
            }
            
            // Download bundles
            foreach (var bundle in bundlesToDownload)
            {
                if (string.IsNullOrEmpty(bundle.RemoteUrl))
                {
                    Debug.LogError($"[HyperContent] Bundle {bundle.Name} has no remote URL");
                    result.BundlesFailed++;
                    result.FailedBundles.Add(bundle.Name);
                    continue;
                }
                
                var fetchResult = _bundleTransport.Download(bundle.RemoteUrl, out byte[] data);
                if (fetchResult.Success && data != null)
                {
                    if (_bundleStore.Save(bundle.Name, data, bundle.Hash))
                    {
                        result.BundlesDownloaded++;
                        Debug.Log($"[HyperContent] Bundle downloaded: {bundle.Name}");
                    }
                    else
                    {
                        result.BundlesFailed++;
                        result.FailedBundles.Add(bundle.Name);
                        Debug.LogError($"[HyperContent] Failed to save bundle: {bundle.Name}");
                    }
                }
                else
                {
                    result.BundlesFailed++;
                    result.FailedBundles.Add(bundle.Name);
                    Debug.LogError($"[HyperContent] Failed to download bundle: {bundle.Name}, error: {fetchResult.ErrorMessage}");
                }
            }
            
            result.Success = result.BundlesFailed == 0;
            return result;
        }
    }
}
