using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// Local file-based catalog implementation (POC)
    /// Reads catalog from JSON file
    /// </summary>
    public class LocalContentCatalog : IContentCatalog
    {
        private CatalogSchema _schema;
        private Dictionary<string, string> _assetToBundle = new Dictionary<string, string>();
        private Dictionary<string, BundleInfo> _bundleInfos = new Dictionary<string, BundleInfo>();
        
        public bool IsValid => _schema != null;
        public int Version => _schema?.version ?? 0;
        
        public bool Initialize(string source)
        {
            try
            {
                string jsonContent;
                
                // Try to load from file path
                if (File.Exists(source))
                {
                    jsonContent = File.ReadAllText(source);
                }
                // Try to load from StreamingAssets
                else
                {
                    string streamingPath = Path.Combine(Application.streamingAssetsPath, source);
                    if (File.Exists(streamingPath))
                    {
                        jsonContent = File.ReadAllText(streamingPath);
                    }
                    else
                    {
                        // Try as relative path in StreamingAssets
                        if (!source.Contains("/") && !source.Contains("\\"))
                        {
                            streamingPath = Path.Combine(Application.streamingAssetsPath, source);
                            if (File.Exists(streamingPath))
                            {
                                jsonContent = File.ReadAllText(streamingPath);
                            }
                            else
                            {
                                Debug.LogError($"[HyperContent] Catalog not found: {source} (checked: {streamingPath})");
                                return false;
                            }
                        }
                        else
                        {
                            Debug.LogError($"[HyperContent] Catalog not found: {source} (checked: {streamingPath})");
                            return false;
                        }
                    }
                }
                
                _schema = JsonUtility.FromJson<CatalogSchema>(jsonContent);
                
                if (_schema == null)
                {
                    Debug.LogError("[HyperContent] Failed to parse catalog JSON");
                    return false;
                }
                
                // Build lookup dictionaries from arrays
                _assetToBundle.Clear();
                if (_schema.assetToBundle != null)
                {
                    foreach (var mapping in _schema.assetToBundle)
                    {
                        if (!string.IsNullOrEmpty(mapping.key) && !string.IsNullOrEmpty(mapping.bundle))
                        {
                            _assetToBundle[mapping.key] = mapping.bundle;
                        }
                    }
                }
                
                _bundleInfos.Clear();
                if (_schema.bundles != null)
                {
                    foreach (var bundleData in _schema.bundles)
                    {
                        if (string.IsNullOrEmpty(bundleData.name))
                        {
                            Debug.LogWarning("[HyperContent] Bundle with empty name found, skipping");
                            continue;
                        }
                        
                        var bundleInfo = new BundleInfo
                        {
                            Name = bundleData.name,
                            Size = bundleData.size,
                            Hash = bundleData.hash,
                            Version = bundleData.version,
                            Location = ParseLocation(bundleData.location),
                            RemoteUrl = bundleData.remoteUrl,
                            LocalPath = bundleData.localPath,
                            Dependencies = bundleData.dependencies ?? new string[0],
                            AssetKeys = bundleData.assetKeys ?? new string[0]
                        };
                        _bundleInfos[bundleData.name] = bundleInfo;
                    }
                }
                
                Debug.Log($"[HyperContent] Catalog loaded: {_assetToBundle.Count} assets, {_bundleInfos.Count} bundles");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[HyperContent] Catalog initialization failed: {e.Message}");
                return false;
            }
        }
        
        private ContentLocation ParseLocation(string location)
        {
            if (string.IsNullOrEmpty(location))
                return ContentLocation.None;
            
            if (Enum.TryParse<ContentLocation>(location, true, out var result))
                return result;
            
            return ContentLocation.None;
        }
        
        public bool TryGetBundleName(string assetKey, out string bundleName)
        {
            return _assetToBundle.TryGetValue(assetKey, out bundleName);
        }
        
        public bool TryGetBundleInfo(string bundleName, out BundleInfo bundleInfo)
        {
            return _bundleInfos.TryGetValue(bundleName, out bundleInfo);
        }
        
        public IEnumerable<string> GetAllAssetKeys()
        {
            return _assetToBundle.Keys;
        }
        
        public IEnumerable<string> GetAllBundleNames()
        {
            return _bundleInfos.Keys;
        }
        
        public void Release()
        {
            _schema = null;
            _assetToBundle.Clear();
            _bundleInfos.Clear();
        }
    }
}
