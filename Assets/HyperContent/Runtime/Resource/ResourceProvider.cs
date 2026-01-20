using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// Main resource provider implementation
    /// Provides LoadAsset<T> interface similar to Addressables
    /// Handles dependency resolution, in-flight merging, refcount-driven bundle lifecycle
    /// </summary>
    public class ResourceProvider : IResourceProvider
    {
        private IContentCatalog _catalog;
        private IBundleStore _bundleStore;
        private IBundleTransport _bundleTransport;
        private IBundleLoader _bundleLoader;
        
        // Asset tracking
        private Dictionary<string, UnityEngine.Object> _loadedAssets = new Dictionary<string, UnityEngine.Object>();
        private Dictionary<string, int> _assetRefCounts = new Dictionary<string, int>();
        
        // Handle tracking (in-flight merging)
        private Dictionary<string, Handle> _activeHandles = new Dictionary<string, Handle>();
        
        // Bundle reference counting (bundle-level lifecycle)
        private Dictionary<string, int> _bundleRefCounts = new Dictionary<string, int>();
        private Dictionary<string, HashSet<string>> _bundleToAssets = new Dictionary<string, HashSet<string>>();
        
        // Instance tracking
        private Dictionary<GameObject, string> _instanceToAssetKey = new Dictionary<GameObject, string>();
        
        // Concurrent bundle loading control
        private HashSet<string> _loadingBundles = new HashSet<string>();
        private const int MAX_CONCURRENT_BUNDLE_LOADS = 4;
        private int _currentConcurrentLoads = 0;
        
        public bool Initialize(IContentCatalog catalog, IBundleStore bundleStore, IBundleTransport bundleTransport, IBundleLoader bundleLoader)
        {
            _catalog = catalog;
            _bundleStore = bundleStore;
            _bundleTransport = bundleTransport;
            _bundleLoader = bundleLoader;
            
            if (_catalog == null || !_catalog.IsValid)
            {
                Debug.LogError("[HyperContent] Invalid catalog provided");
                return false;
            }
            
            Debug.Log("[HyperContent] ResourceProvider initialized");
            return true;
        }
        
        public T LoadAsset<T>(string key) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(key))
            {
                Debug.LogError("[HyperContent] Invalid asset key");
                return null;
            }
            
            // Check if already loaded
            if (_loadedAssets.TryGetValue(key, out var existing))
            {
                if (existing is T)
                {
                    IncrementAssetRefCount(key);
                    return existing as T;
                }
            }
            
            // Use async method synchronously (blocking)
            var handle = LoadAssetAsync<T>(key);
            if (handle.Status == HandleStatus.InProgress)
            {
                // Wait for completion (not ideal, but for backward compatibility)
                float timeout = Time.time + 30f; // 30 second timeout
                while (handle.Status == HandleStatus.InProgress && Time.time < timeout)
                {
                    System.Threading.Thread.Sleep(10);
                }
            }
            
            if (handle.IsValid)
            {
                return handle.Result;
            }
            
            return null;
        }
        
        public void LoadAssetAsync<T>(string key, Action<T> onComplete) where T : UnityEngine.Object
        {
            var handle = LoadAssetAsync<T>(key);
            if (handle != null)
            {
                handle.RegisterCallback(onComplete);
            }
        }
        
        public AssetHandle<T> LoadAssetAsync<T>(string key) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(key))
            {
                var errorHandle = new AssetHandle<T>(key);
                errorHandle.Fail(ErrorCode.RESOURCE_KEY_INVALID, "Invalid asset key");
                return errorHandle;
            }
            
            // Check if asset is already loaded
            if (_loadedAssets.TryGetValue(key, out var existing) && existing is T)
            {
                var handle = new AssetHandle<T>(key);
                handle.Complete(existing as T);
                IncrementAssetRefCount(key);
                return handle;
            }
            
            // In-flight merging: check if there's already a handle for this key
            string handleKey = $"{typeof(T).Name}:{key}";
            if (_activeHandles.TryGetValue(handleKey, out var existingHandle) && existingHandle is AssetHandle<T>)
            {
                var assetHandle = existingHandle as AssetHandle<T>;
                assetHandle.AddRef(); // Increment ref count for this request
                return assetHandle;
            }
            
            // Create new handle
            var newHandle = new AssetHandle<T>(key);
            _activeHandles[handleKey] = newHandle;
            newHandle.AddRef();
            
            // Start async loading
            StartLoadAssetAsync(key, newHandle);
            
            return newHandle;
        }
        
        public InstanceHandle InstantiateAsync(string key, Transform parent = null, bool instantiateInWorldSpace = false)
        {
            if (string.IsNullOrEmpty(key))
            {
                var errorHandle = new InstanceHandle(key, null);
                errorHandle.Fail(ErrorCode.RESOURCE_KEY_INVALID, "Invalid asset key");
                return errorHandle;
            }
            
            // Load GameObject asset first
            var assetHandle = LoadAssetAsync<GameObject>(key);
            var instanceHandle = new InstanceHandle(key, assetHandle);
            
            // Link progress
            assetHandle.RegisterProgressCallback(progress => instanceHandle.UpdateProgress(progress * 0.9f)); // 90% for loading
            
            // When asset is loaded, instantiate
            assetHandle.RegisterCallback(asset =>
            {
                if (asset == null)
                {
                    instanceHandle.Fail(ErrorCode.RESOURCE_LOAD_FAILED, "Failed to load GameObject asset");
                    return;
                }
                
                try
                {
                    GameObject instance = UnityEngine.Object.Instantiate(asset, parent, instantiateInWorldSpace);
                    _instanceToAssetKey[instance] = key;
                    IncrementAssetRefCount(key); // Increment ref count for instance
                    instanceHandle.Complete(instance);
                }
                catch (Exception e)
                {
                    instanceHandle.Fail(ErrorCode.RESOURCE_LOAD_FAILED, $"Failed to instantiate: {e.Message}");
                }
            });
            
            return instanceHandle;
        }
        
        public void ReleaseAsset(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;
            
            if (_assetRefCounts.TryGetValue(key, out int refCount))
            {
                refCount--;
                if (refCount <= 0)
                {
                    _assetRefCounts.Remove(key);
                    _loadedAssets.Remove(key);
                    
                    // Decrement bundle ref count
                    if (_catalog.TryGetBundleName(key, out string bundleName))
                    {
                        DecrementBundleRefCount(bundleName, key);
                    }
                }
                else
                {
                    _assetRefCounts[key] = refCount;
                }
            }
        }
        
        public void ReleaseInstance(GameObject instance)
        {
            if (instance == null)
                return;
            
            if (_instanceToAssetKey.TryGetValue(instance, out string key))
            {
                _instanceToAssetKey.Remove(instance);
                UnityEngine.Object.Destroy(instance);
                ReleaseAsset(key);
            }
            else
            {
                // Instance not tracked, just destroy it
                UnityEngine.Object.Destroy(instance);
            }
        }
        
        public bool IsAssetLoaded(string key)
        {
            return _loadedAssets.ContainsKey(key);
        }
        
        public void ReleaseAll()
        {
            // Release all instances
            foreach (var instance in _instanceToAssetKey.Keys.ToList())
            {
                if (instance != null)
                {
                    UnityEngine.Object.Destroy(instance);
                }
            }
            _instanceToAssetKey.Clear();
            
            // Clear all assets
            _loadedAssets.Clear();
            _assetRefCounts.Clear();
            
            // Unload all bundles
            foreach (var bundleName in _bundleRefCounts.Keys.ToList())
            {
                UnloadBundleIfNeeded(bundleName);
            }
            
            _bundleRefCounts.Clear();
            _bundleToAssets.Clear();
            _activeHandles.Clear();
            _loadingBundles.Clear();
        }
        
        // Internal methods
        
        private void StartLoadAssetAsync<T>(string key, AssetHandle<T> handle) where T : UnityEngine.Object
        {
            // Get bundle name
            if (!_catalog.TryGetBundleName(key, out string bundleName))
            {
                handle.Fail(ErrorCode.RESOURCE_NOT_FOUND, $"Asset key not found in catalog: {key}");
                RemoveActiveHandle(key, handle);
                return;
            }
            
            // Resolve dependencies using topological sort
            var requiredBundles = DependencyResolver.ResolveBundlesForAsset(_catalog, key);
            handle.SetRequiredBundles(requiredBundles);
            handle.BeginOperation(requiredBundles.Count);
            
            // Start loading bundles in dependency order
            LoadBundlesInOrder(requiredBundles, () =>
            {
                // All bundles loaded, now load the asset
                LoadAssetFromBundle<T>(key, bundleName, handle);
            }, (errorCode, errorMessage) =>
            {
                handle.Fail(errorCode, errorMessage);
                RemoveActiveHandle(key, handle);
            });
        }
        
        private void LoadBundlesInOrder(List<string> bundles, Action onComplete, Action<int, string> onError)
        {
            if (bundles == null || bundles.Count == 0)
            {
                onComplete?.Invoke();
                return;
            }
            
            LoadBundlesInOrderRecursive(bundles, 0, onComplete, onError);
        }
        
        private void LoadBundlesInOrderRecursive(List<string> bundles, int index, Action onComplete, Action<int, string> onError)
        {
            if (index >= bundles.Count)
            {
                onComplete?.Invoke();
                return;
            }
            
            string bundleName = bundles[index];
            
            // Check if bundle is already loaded
            if (_bundleLoader.IsLoaded(bundleName))
            {
                LoadBundlesInOrderRecursive(bundles, index + 1, onComplete, onError);
                return;
            }
            
            // Check if bundle is already loading
            if (_loadingBundles.Contains(bundleName))
            {
                // Wait for existing load (simplified: poll)
                WaitForBundleLoad(bundleName, () =>
                {
                    LoadBundlesInOrderRecursive(bundles, index + 1, onComplete, onError);
                }, onError);
                return;
            }
            
            // Check concurrent load limit
            if (_currentConcurrentLoads >= MAX_CONCURRENT_BUNDLE_LOADS)
            {
                // Wait and retry
                WaitAndRetry(() =>
                {
                    LoadBundlesInOrderRecursive(bundles, index, onComplete, onError);
                });
                return;
            }
            
            // Load bundle
            _loadingBundles.Add(bundleName);
            _currentConcurrentLoads++;
            
            LoadSingleBundle(bundleName, () =>
            {
                _loadingBundles.Remove(bundleName);
                _currentConcurrentLoads--;
                IncrementBundleRefCount(bundleName);
                LoadBundlesInOrderRecursive(bundles, index + 1, onComplete, onError);
            }, (errorCode, errorMessage) =>
            {
                _loadingBundles.Remove(bundleName);
                _currentConcurrentLoads--;
                onError?.Invoke(errorCode, errorMessage);
            });
        }
        
        private void LoadSingleBundle(string bundleName, Action onComplete, Action<int, string> onError)
        {
            if (!_catalog.TryGetBundleInfo(bundleName, out BundleInfo bundleInfo))
            {
                onError?.Invoke(ErrorCode.BUNDLE_NOT_FOUND, $"Bundle info not found: {bundleName}");
                return;
            }
            
            switch (bundleInfo.Location)
            {
                case ContentLocation.Local:
                case ContentLocation.StreamingAssets:
                    LoadBundleFromLocal(bundleName, bundleInfo, onComplete, onError);
                    break;
                    
                case ContentLocation.Remote:
                    LoadBundleFromRemote(bundleName, bundleInfo, onComplete, onError);
                    break;
                    
                default:
                    onError?.Invoke(ErrorCode.BUNDLE_NOT_FOUND, $"Unsupported bundle location: {bundleInfo.Location}");
                    break;
            }
        }
        
        private void LoadBundleFromLocal(string bundleName, BundleInfo bundleInfo, Action onComplete, Action<int, string> onError)
        {
            string bundlePath = bundleInfo.LocalPath;
            
            // Try StreamingAssets if localPath is relative
            if (!string.IsNullOrEmpty(bundlePath) && !System.IO.Path.IsPathRooted(bundlePath))
            {
                bundlePath = System.IO.Path.Combine(Application.streamingAssetsPath, bundlePath);
            }
            
            // Fallback to bundle store
            if ((string.IsNullOrEmpty(bundlePath) || !System.IO.File.Exists(bundlePath)) && _bundleStore != null && _bundleStore.Exists(bundleName))
            {
                bundlePath = _bundleStore.GetLocalPath(bundleName);
            }
            
            if (string.IsNullOrEmpty(bundlePath) || !System.IO.File.Exists(bundlePath))
            {
                onError?.Invoke(ErrorCode.BUNDLE_NOT_FOUND, $"Bundle file not found: {bundleName}");
                return;
            }
            
            _bundleLoader.LoadFromFileAsync(bundleName, bundlePath, (assetBundle) =>
            {
                if (assetBundle == null)
                {
                    onError?.Invoke(ErrorCode.BUNDLE_LOAD_FAILED, $"Failed to load bundle: {bundleName}");
                }
                else
                {
                    onComplete?.Invoke();
                }
            });
        }
        
        private void LoadBundleFromRemote(string bundleName, BundleInfo bundleInfo, Action onComplete, Action<int, string> onError)
        {
            if (_bundleTransport == null)
            {
                onError?.Invoke(ErrorCode.TRANSPORT_INVALID_URL, "BundleTransport is not available");
                return;
            }
            
            string url = bundleInfo.RemoteUrl;
            if (string.IsNullOrEmpty(url))
            {
                onError?.Invoke(ErrorCode.TRANSPORT_INVALID_URL, $"Remote URL not specified for bundle: {bundleName}");
                return;
            }
            
            // Check if already in cache
            if (_bundleStore != null && _bundleStore.Exists(bundleName))
            {
                string localPath = _bundleStore.GetLocalPath(bundleName);
                _bundleLoader.LoadFromFileAsync(bundleName, localPath, (assetBundle) =>
                {
                    if (assetBundle != null)
                    {
                        onComplete?.Invoke();
                    }
                    else
                    {
                        // Cache is invalid, download again
                        DownloadAndLoadBundle(bundleName, bundleInfo, url, onComplete, onError);
                    }
                });
            }
            else
            {
                DownloadAndLoadBundle(bundleName, bundleInfo, url, onComplete, onError);
            }
        }
        
        private void DownloadAndLoadBundle(string bundleName, BundleInfo bundleInfo, string url, Action onComplete, Action<int, string> onError)
        {
            // Use synchronous Download to get data
            // Note: This blocks the thread. In production, use async download with proper data callback
            FetchResult fetchResult = _bundleTransport.Download(url, out byte[] data);
            
            if (!fetchResult.Success || data == null)
            {
                onError?.Invoke(fetchResult.ErrorCode, fetchResult.ErrorMessage);
                return;
            }
            
            // Save to bundle store if available
            if (_bundleStore != null)
            {
                if (_bundleStore.Save(bundleName, data, bundleInfo.Hash))
                {
                    // Load from cache after save
                    string localPath = _bundleStore.GetLocalPath(bundleName);
                    _bundleLoader.LoadFromFileAsync(bundleName, localPath, (assetBundle) =>
                    {
                        if (assetBundle == null)
                        {
                            onError?.Invoke(ErrorCode.BUNDLE_LOAD_FAILED, $"Failed to load cached bundle: {bundleName}");
                        }
                        else
                        {
                            onComplete?.Invoke();
                        }
                    });
                }
                else
                {
                    // Save failed, try loading from memory
                    _bundleLoader.LoadFromMemoryAsync(bundleName, data, (assetBundle) =>
                    {
                        if (assetBundle == null)
                        {
                            onError?.Invoke(ErrorCode.BUNDLE_LOAD_FAILED, $"Failed to load bundle from memory: {bundleName}");
                        }
                        else
                        {
                            onComplete?.Invoke();
                        }
                    });
                }
            }
            else
            {
                // No bundle store, load from memory directly
                _bundleLoader.LoadFromMemoryAsync(bundleName, data, (assetBundle) =>
                {
                    if (assetBundle == null)
                    {
                        onError?.Invoke(ErrorCode.BUNDLE_LOAD_FAILED, $"Failed to load bundle from memory: {bundleName}");
                    }
                    else
                    {
                        onComplete?.Invoke();
                    }
                });
            }
        }
        
        private void LoadAssetFromBundle<T>(string key, string bundleName, AssetHandle<T> handle) where T : UnityEngine.Object
        {
            if (!_bundleLoader.TryGetBundle(bundleName, out AssetBundle assetBundle))
            {
                handle.Fail(ErrorCode.BUNDLE_LOAD_FAILED, $"Bundle not loaded: {bundleName}");
                RemoveActiveHandle(key, handle);
                return;
            }
            
            // Try loading by asset name first
            string assetName = System.IO.Path.GetFileNameWithoutExtension(key);
            T asset = assetBundle.LoadAsset<T>(assetName);
            
            // Fallback to full key
            if (asset == null)
            {
                asset = assetBundle.LoadAsset<T>(key);
            }
            
            if (asset == null)
            {
                handle.Fail(ErrorCode.RESOURCE_NOT_FOUND, $"Asset not found in bundle: {key} (bundle: {bundleName})");
                RemoveActiveHandle(key, handle);
                return;
            }
            
            // Success
            _loadedAssets[key] = asset;
            IncrementAssetRefCount(key);
            handle.Complete(asset);
            
            // Remove handle if ref count is 0 (no other waiting requests)
            if (handle.RefCount == 0)
            {
                RemoveActiveHandle(key, handle);
            }
        }
        
        private void RemoveActiveHandle<T>(string key, AssetHandle<T> handle) where T : UnityEngine.Object
        {
            string handleKey = $"{typeof(T).Name}:{key}";
            if (_activeHandles.TryGetValue(handleKey, out var existingHandle) && existingHandle == handle)
            {
                _activeHandles.Remove(handleKey);
            }
        }
        
        private void IncrementAssetRefCount(string key)
        {
            if (_assetRefCounts.ContainsKey(key))
            {
                _assetRefCounts[key]++;
            }
            else
            {
                _assetRefCounts[key] = 1;
            }
        }
        
        private void IncrementBundleRefCount(string bundleName)
        {
            if (_bundleRefCounts.ContainsKey(bundleName))
            {
                _bundleRefCounts[bundleName]++;
            }
            else
            {
                _bundleRefCounts[bundleName] = 1;
                _bundleToAssets[bundleName] = new HashSet<string>();
            }
            
            // Track which assets use this bundle
            if (_catalog.TryGetBundleInfo(bundleName, out BundleInfo bundleInfo) && bundleInfo.AssetKeys != null)
            {
                foreach (var assetKey in bundleInfo.AssetKeys)
                {
                    if (_assetRefCounts.ContainsKey(assetKey))
                    {
                        _bundleToAssets[bundleName].Add(assetKey);
                    }
                }
            }
        }
        
        private void DecrementBundleRefCount(string bundleName, string assetKey)
        {
            if (_bundleToAssets.ContainsKey(bundleName))
            {
                _bundleToAssets[bundleName].Remove(assetKey);
            }
            
            // Check if bundle has any remaining references
            if (_bundleToAssets.ContainsKey(bundleName) && _bundleToAssets[bundleName].Count == 0)
            {
                UnloadBundleIfNeeded(bundleName);
            }
        }
        
        private void UnloadBundleIfNeeded(string bundleName)
        {
            if (_bundleRefCounts.TryGetValue(bundleName, out int refCount))
            {
                refCount--;
                if (refCount <= 0)
                {
                    _bundleRefCounts.Remove(bundleName);
                    _bundleToAssets.Remove(bundleName);
                    _bundleLoader.Unload(bundleName, false);
                }
                else
                {
                    _bundleRefCounts[bundleName] = refCount;
                }
            }
        }
        
        private void WaitForBundleLoad(string bundleName, Action onComplete, Action<int, string> onError)
        {
            // Simplified: poll until loaded or timeout
            // In production, use proper event system or coroutines via HyperContentManager
            float timeout = Time.time + 30f;
            
            // For now, immediately check and retry later if needed
            if (!_loadingBundles.Contains(bundleName) && _bundleLoader.IsLoaded(bundleName))
            {
                onComplete?.Invoke();
                return;
            }
            
            // If still loading, schedule retry
            // Note: In production, this should use coroutines via HyperContentManager
            // For now, we'll retry in the next recursive call
            if (Time.time < timeout)
            {
                // Wait a bit and check again (simplified: immediate retry with limit check)
                WaitAndRetry(() =>
                {
                    if (_bundleLoader.IsLoaded(bundleName))
                    {
                        onComplete?.Invoke();
                    }
                    else if (_loadingBundles.Contains(bundleName))
                    {
                        // Still loading, retry later
                        WaitForBundleLoad(bundleName, onComplete, onError);
                    }
                    else
                    {
                        // Not loading and not loaded, failed
                        onError?.Invoke(ErrorCode.BUNDLE_LOAD_FAILED, $"Bundle load failed: {bundleName}");
                    }
                });
            }
            else
            {
                onError?.Invoke(ErrorCode.BUNDLE_LOAD_FAILED, $"Timeout waiting for bundle: {bundleName}");
            }
        }
        
        private void WaitAndRetry(Action retry)
        {
            // Simplified: immediate retry (for now)
            // In production, use coroutines via HyperContentManager for proper async waiting
            retry?.Invoke();
        }
    }
}
