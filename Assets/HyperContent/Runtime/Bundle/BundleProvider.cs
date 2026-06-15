using System;
using UnityEngine;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// [LEGACY] Pre-refactor bundle provider facade.
    /// Superseded by the Provider Layer: BundleFileProvider, RemoteBundleProvider, etc.
    /// in Runtime/Providers/. The new architecture routes through IContentProvider.Provide()
    /// rather than this monolithic facade.
    ///
    /// TODO [Owner3]: Remove once confirmed no external callers remain.
    /// </summary>
    public class BundleProvider
    {
        private IBundleStore _store;
        private IBundleTransport _transport;
        private IBundleLoader _loader;
        private ICatalog _catalog;
        
        /// <summary>
        /// Bundle fetch result with data
        /// </summary>
        public class BundleFetchResult
        {
            public bool Success { get; set; }
            public byte[] Data { get; set; }
            public string LocalPath { get; set; }
            public AssetBundle AssetBundle { get; set; }
            public int ErrorCode { get; set; }
            public string ErrorMessage { get; set; }
        }
        
        public BundleProvider(IBundleStore store, IBundleTransport transport, IBundleLoader loader, ICatalog catalog)
        {
            _store = store;
            _transport = transport;
            _loader = loader;
            _catalog = catalog;
        }
        
        /// <summary>
        /// Get bundle data synchronously
        /// </summary>
        public BundleFetchResult GetBundle(string bundleName)
        {
            var result = new BundleFetchResult();
            
            if (!_catalog.TryGetBundleInfo(bundleName, out var bundleInfo))
            {
                result.ErrorCode = ErrorCode.BUNDLE_NOT_FOUND;
                result.ErrorMessage = $"Bundle not found in catalog: {bundleName}";
                return result;
            }

            // Local/Remote routing: StreamingAssets → local file (no download); Remote → download/cache then load
            switch (bundleInfo.Location)
            {
                case ContentLocation.Local:
                case ContentLocation.StreamingAssets:
                    return GetLocalBundle(bundleName, bundleInfo);
                    
                case ContentLocation.Remote:
                    return GetRemoteBundle(bundleName, bundleInfo);
                    
                case ContentLocation.Resources:
                    result.ErrorCode = ErrorCode.BUNDLE_NOT_FOUND;
                    result.ErrorMessage = "Resources location not supported by BundleProvider";
                    return result;
                    
                default:
                    result.ErrorCode = ErrorCode.BUNDLE_NOT_FOUND;
                    result.ErrorMessage = $"Unknown location: {bundleInfo.Location}";
                    return result;
            }
        }
        
        /// <summary>
        /// Get bundle data asynchronously
        /// </summary>
        public void GetBundleAsync(string bundleName, Action<BundleFetchResult> onComplete, Action<float> onProgress = null)
        {
            if (!_catalog.TryGetBundleInfo(bundleName, out var bundleInfo))
            {
                var result = new BundleFetchResult
                {
                    ErrorCode = ErrorCode.BUNDLE_NOT_FOUND,
                    ErrorMessage = $"Bundle not found in catalog: {bundleName}"
                };
                onComplete?.Invoke(result);
                return;
            }

            // Local/Remote routing: StreamingAssets → local file; Remote → download/cache then load
            switch (bundleInfo.Location)
            {
                case ContentLocation.Local:
                case ContentLocation.StreamingAssets:
                    GetLocalBundleAsync(bundleName, bundleInfo, onComplete);
                    break;
                    
                case ContentLocation.Remote:
                    GetRemoteBundleAsync(bundleName, bundleInfo, onComplete, onProgress);
                    break;
                    
                default:
                    var errorResult = new BundleFetchResult
                    {
                        ErrorCode = ErrorCode.BUNDLE_NOT_FOUND,
                        ErrorMessage = $"Unsupported location: {bundleInfo.Location}"
                    };
                    onComplete?.Invoke(errorResult);
                    break;
            }
        }
        
        /// <summary>
        /// Get local bundle
        /// </summary>
        private BundleFetchResult GetLocalBundle(string bundleName, BundleInfo bundleInfo)
        {
            var result = new BundleFetchResult();
            
            // Try to get from store first
            string localPath = _store.GetLocalPath(bundleName);
            if (string.IsNullOrEmpty(localPath))
            {
                // Try bundleInfo local path
                if (!string.IsNullOrEmpty(bundleInfo.LocalPath))
                {
                    localPath = bundleInfo.LocalPath;
                }
                else if (bundleInfo.Location == ContentLocation.StreamingAssets)
                {
                    localPath = System.IO.Path.Combine(Application.streamingAssetsPath, bundleName);
                }
            }
            
            if (string.IsNullOrEmpty(localPath) || !System.IO.File.Exists(localPath))
            {
                result.ErrorCode = ErrorCode.BUNDLE_NOT_FOUND;
                result.ErrorMessage = $"Local bundle file not found: {bundleName}";
                return result;
            }
            
            // Verify hash if provided
            if (!string.IsNullOrEmpty(bundleInfo.Hash))
            {
                if (!_store.VerifyHash(bundleName, bundleInfo.Hash))
                {
                    result.ErrorCode = ErrorCode.BUNDLE_INVALID_HASH;
                    result.ErrorMessage = $"Bundle hash verification failed: {bundleName}";
                    return result;
                }
            }
            
            // Load data
            if (_store.Load(bundleName, out byte[] data))
            {
                result.Success = true;
                result.Data = data;
                result.LocalPath = localPath;
                return result;
            }
            else
            {
                // Try to load from file path directly
                try
                {
                    data = System.IO.File.ReadAllBytes(localPath);
                    result.Success = true;
                    result.Data = data;
                    result.LocalPath = localPath;
                    return result;
                }
                catch (Exception e)
                {
                    result.ErrorCode = ErrorCode.BUNDLE_LOAD_FAILED;
                    result.ErrorMessage = $"Failed to load bundle: {e.Message}";
                    return result;
                }
            }
        }
        
        /// <summary>
        /// Get local bundle asynchronously
        /// </summary>
        private void GetLocalBundleAsync(string bundleName, BundleInfo bundleInfo, Action<BundleFetchResult> onComplete)
        {
            // Local path is synchronous; avoid requiring HyperContentManager / coroutine runner (legacy API).
            var result = GetLocalBundle(bundleName, bundleInfo);
            onComplete?.Invoke(result);
        }
        
        /// <summary>
        /// Get remote bundle
        /// </summary>
        private BundleFetchResult GetRemoteBundle(string bundleName, BundleInfo bundleInfo)
        {
            var result = new BundleFetchResult();
            
            // Check if already cached
            if (_store.Exists(bundleName))
            {
                // Verify hash
                if (!string.IsNullOrEmpty(bundleInfo.Hash))
                {
                    if (!_store.VerifyHash(bundleName, bundleInfo.Hash))
                    {
                        // Hash mismatch, delete and re-download
                        _store.Delete(bundleName);
                    }
                    else
                    {
                        // Use cached version
                        return GetLocalBundle(bundleName, bundleInfo);
                    }
                }
                else
                {
                    // Use cached version
                    return GetLocalBundle(bundleName, bundleInfo);
                }
            }
            
            // Download from remote
            if (string.IsNullOrEmpty(bundleInfo.RemoteRelativePath))
            {
                result.ErrorCode = ErrorCode.BUNDLE_NOT_FOUND;
                result.ErrorMessage = $"Remote relative path not specified for bundle: {bundleName}";
                return result;
            }

            var fetchResult = _transport.Download(bundleInfo.RemoteRelativePath, out byte[] data);
            if (!fetchResult.Success || data == null)
            {
                result.ErrorCode = fetchResult.ErrorCode;
                result.ErrorMessage = fetchResult.ErrorMessage;
                return result;
            }
            
            // Verify hash
            if (!string.IsNullOrEmpty(bundleInfo.Hash))
            {
                string actualHash = ComputeHash(data);
                if (actualHash != bundleInfo.Hash)
                {
                    result.ErrorCode = ErrorCode.BUNDLE_INVALID_HASH;
                    result.ErrorMessage = $"Hash mismatch for downloaded bundle: {bundleName}";
                    return result;
                }
            }
            
            // Save to store
            if (!_store.Save(bundleName, data, bundleInfo.Hash))
            {
                result.ErrorCode = ErrorCode.BUNDLE_LOAD_FAILED;
                result.ErrorMessage = $"Failed to save bundle to store: {bundleName}";
                return result;
            }
            
            result.Success = true;
            result.Data = data;
            result.LocalPath = _store.GetLocalPath(bundleName);
            return result;
        }
        
        /// <summary>
        /// Get remote bundle asynchronously
        /// </summary>
        private void GetRemoteBundleAsync(string bundleName, BundleInfo bundleInfo, 
            Action<BundleFetchResult> onComplete, Action<float> onProgress)
        {
            // Check if already cached
            if (_store.Exists(bundleName))
            {
                if (!string.IsNullOrEmpty(bundleInfo.Hash))
                {
                    if (!_store.VerifyHash(bundleName, bundleInfo.Hash))
                    {
                        _store.Delete(bundleName);
                    }
                    else
                    {
                        // Use cached version
                        GetLocalBundleAsync(bundleName, bundleInfo, onComplete);
                        return;
                    }
                }
                else
                {
                    // Use cached version
                    GetLocalBundleAsync(bundleName, bundleInfo, onComplete);
                    return;
                }
            }
            
            // Download from remote
            if (string.IsNullOrEmpty(bundleInfo.RemoteRelativePath))
            {
                var result = new BundleFetchResult
                {
                    ErrorCode = ErrorCode.BUNDLE_NOT_FOUND,
                    ErrorMessage = $"Remote relative path not specified for bundle: {bundleName}"
                };
                onComplete?.Invoke(result);
                return;
            }

            _transport.DownloadAsync(
                bundleInfo.RemoteRelativePath,
                onProgress,
                (fetchResult) =>
                {
                    var result = new BundleFetchResult();
                    
                    if (!fetchResult.Success)
                    {
                        result.ErrorCode = fetchResult.ErrorCode;
                        result.ErrorMessage = fetchResult.ErrorMessage;
                        onComplete?.Invoke(result);
                        return;
                    }
                    
                    // Note: FetchResult doesn't include data in current interface
                    // We need to download synchronously as fallback or modify interface
                    // For now, download synchronously
                    var syncResult = _transport.Download(bundleInfo.RemoteRelativePath, out byte[] data);
                    if (!syncResult.Success || data == null)
                    {
                        result.ErrorCode = syncResult.ErrorCode;
                        result.ErrorMessage = syncResult.ErrorMessage;
                        onComplete?.Invoke(result);
                        return;
                    }
                    
                    // Verify hash
                    if (!string.IsNullOrEmpty(bundleInfo.Hash))
                    {
                        string actualHash = ComputeHash(data);
                        if (actualHash != bundleInfo.Hash)
                        {
                            result.ErrorCode = ErrorCode.BUNDLE_INVALID_HASH;
                            result.ErrorMessage = $"Hash mismatch for downloaded bundle: {bundleName}";
                            onComplete?.Invoke(result);
                            return;
                        }
                    }
                    
                    // Save to store
                    if (!_store.Save(bundleName, data, bundleInfo.Hash))
                    {
                        result.ErrorCode = ErrorCode.BUNDLE_LOAD_FAILED;
                        result.ErrorMessage = $"Failed to save bundle to store: {bundleName}";
                        onComplete?.Invoke(result);
                        return;
                    }
                    
                    result.Success = true;
                    result.Data = data;
                    result.LocalPath = _store.GetLocalPath(bundleName);
                    onComplete?.Invoke(result);
                }
            );
        }
        
        /// <summary>
        /// Load bundle as Unity AssetBundle
        /// </summary>
        public void LoadAssetBundle(string bundleName, Action<AssetBundle> onComplete, Action<string> onError = null)
        {
            GetBundleAsync(
                bundleName,
                (result) =>
                {
                    if (!result.Success)
                    {
                        onError?.Invoke(result.ErrorMessage);
                        return;
                    }
                    
                    // Load as AssetBundle
                    if (_loader != null)
                    {
                        // Use loader to load from path or memory
                        if (!string.IsNullOrEmpty(result.LocalPath))
                        {
                            _loader.LoadFromFileAsync(bundleName, result.LocalPath, (assetBundle) =>
                            {
                                result.AssetBundle = assetBundle;
                                onComplete?.Invoke(assetBundle);
                            });
                        }
                        else if (result.Data != null)
                        {
                            _loader.LoadFromMemoryAsync(bundleName, result.Data, (assetBundle) =>
                            {
                                result.AssetBundle = assetBundle;
                                onComplete?.Invoke(assetBundle);
                            });
                        }
                        else
                        {
                            onError?.Invoke("No bundle data available");
                        }
                    }
                    else
                    {
                        onError?.Invoke("Bundle loader not available");
                    }
                }
            );
        }
        
        /// <summary>
        /// Compute hash for data
        /// </summary>
        private string ComputeHash(byte[] data)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(data);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}
