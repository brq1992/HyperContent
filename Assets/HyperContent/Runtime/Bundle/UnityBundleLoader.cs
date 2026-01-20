using System;
using System.Collections.Generic;
using UnityEngine;

namespace HyperContent
{
    /// <summary>
    /// Unity AssetBundle loader implementation (POC)
    /// Wraps Unity's AssetBundle API
    /// </summary>
    public class UnityBundleLoader : IBundleLoader
    {
        private Dictionary<string, AssetBundle> _loadedBundles = new Dictionary<string, AssetBundle>();
        
        public void LoadFromFileAsync(string bundleName, string filePath, Action<AssetBundle> onComplete)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            {
                onComplete?.Invoke(null);
                return;
            }
            
            // For POC, use synchronous loading
            // In production, this should use AssetBundle.LoadFromFileAsync
            try
            {
                var bundle = AssetBundle.LoadFromFile(filePath);
                if (bundle != null)
                {
                    _loadedBundles[bundleName] = bundle;
                }
                onComplete?.Invoke(bundle);
            }
            catch (Exception e)
            {
                Debug.LogError($"[HyperContent] Failed to load bundle {bundleName}: {e.Message}");
                onComplete?.Invoke(null);
            }
        }
        
        public void LoadFromMemoryAsync(string bundleName, byte[] data, Action<AssetBundle> onComplete)
        {
            if (data == null || data.Length == 0)
            {
                onComplete?.Invoke(null);
                return;
            }
            
            // For POC, use synchronous loading
            // In production, this should use AssetBundle.LoadFromMemoryAsync
            try
            {
                var bundle = AssetBundle.LoadFromMemory(data);
                if (bundle != null)
                {
                    _loadedBundles[bundleName] = bundle;
                }
                onComplete?.Invoke(bundle);
            }
            catch (Exception e)
            {
                Debug.LogError($"[HyperContent] Failed to load bundle from memory {bundleName}: {e.Message}");
                onComplete?.Invoke(null);
            }
        }
        
        public void Unload(string bundleName, bool unloadAllLoadedObjects = false)
        {
            if (_loadedBundles.TryGetValue(bundleName, out var bundle))
            {
                bundle.Unload(unloadAllLoadedObjects);
                _loadedBundles.Remove(bundleName);
            }
        }
        
        public bool IsLoaded(string bundleName)
        {
            return _loadedBundles.ContainsKey(bundleName);
        }
        
        public bool TryGetBundle(string bundleName, out AssetBundle bundle)
        {
            return _loadedBundles.TryGetValue(bundleName, out bundle);
        }
    }
}
