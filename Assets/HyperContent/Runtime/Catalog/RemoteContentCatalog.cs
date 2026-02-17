using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// Remote catalog implementation that fetches and caches catalog from remote URL
    /// Remote catalog always overrides local catalog (Owner0 v1 definition)
    /// </summary>
    public class RemoteContentCatalog : IContentCatalog
    {
        private IContentCatalog _localCatalog;
        private IContentCatalog _remoteCatalog;
        private string _remoteCatalogUrl;
        private string _localCachePath;
        private bool _isInitialized;
        
        // Catalog cache settings
        private const string CATALOG_CACHE_DIR = "Catalogs";
        private const long CATALOG_CACHE_MAX_AGE_SECONDS = 3600; // 1 hour
        
        public bool IsValid => _isInitialized && (_remoteCatalog?.IsValid ?? _localCatalog?.IsValid ?? false);
        public int Version => _remoteCatalog?.Version ?? _localCatalog?.Version ?? 0;
        
        public bool Initialize(string source)
        {
            _remoteCatalogUrl = source;
            _isInitialized = false;
            
            // Initialize local cache path
            _localCachePath = Path.Combine(Application.persistentDataPath, "HyperContent", CATALOG_CACHE_DIR);
            if (!Directory.Exists(_localCachePath))
            {
                Directory.CreateDirectory(_localCachePath);
            }
            
            // Try to load local catalog first (fallback)
            string localCatalogPath = GetLocalCatalogPath();
            if (File.Exists(localCatalogPath))
            {
                _localCatalog = new LocalContentCatalog();
                _localCatalog.Initialize(localCatalogPath);
            }
            
            // Try to fetch remote catalog
            if (!string.IsNullOrEmpty(_remoteCatalogUrl))
            {
                return FetchRemoteCatalog();
            }
            
            // If no remote URL, use local catalog
            _isInitialized = _localCatalog?.IsValid ?? false;
            return _isInitialized;
        }
        
        /// <summary>
        /// Fetch remote catalog and cache it locally.
        /// Uses catalogHash to decide whether catalog needs update: if remote catalogHash equals cached catalogHash, use cache and skip download.
        /// </summary>
        private bool FetchRemoteCatalog()
        {
            try
            {
                string cachedPath = GetCachedCatalogPath();

                // Fetch from remote (synchronous for now, can be made async)
                Debug.Log($"[HyperContent] Fetching remote catalog from: {_remoteCatalogUrl}");

                using (var request = UnityEngine.Networking.UnityWebRequest.Get(_remoteCatalogUrl))
                {
                    request.timeout = 30;
                    var asyncOp = request.SendWebRequest();
                    while (!asyncOp.isDone) { }

                    if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                    {
                        string catalogJson = request.downloadHandler.text;
                        byte[] remoteCatalogHash = LocalContentCatalogV2.GetCatalogHashFromJson(catalogJson);

                        // v2 catalog: use catalogHash to decide if catalog needs update
                        if (remoteCatalogHash != null)
                        {
                            byte[] cachedCatalogHash = null;
                            if (File.Exists(cachedPath))
                            {
                                try
                                {
                                    string cachedJson = File.ReadAllText(cachedPath);
                                    cachedCatalogHash = LocalContentCatalogV2.GetCatalogHashFromJson(cachedJson);
                                }
                                catch { }
                            }

                            if (LocalContentCatalogV2.CatalogHashEquals(cachedCatalogHash, remoteCatalogHash) && File.Exists(cachedPath))
                            {
                                Debug.Log($"[HyperContent] Remote catalog unchanged (catalogHash match), using cache");
                                var v2 = new LocalContentCatalogV2();
                                v2.SetBaseUrl(GetBaseUrlFromCatalogUrl(_remoteCatalogUrl));
                                if (v2.Initialize(cachedPath))
                                {
                                    _remoteCatalog = v2;
                                    _isInitialized = true;
                                    return true;
                                }
                            }

                            SaveCatalogToCache(catalogJson);
                            var v2New = new LocalContentCatalogV2();
                            v2New.SetBaseUrl(GetBaseUrlFromCatalogUrl(_remoteCatalogUrl));
                            if (v2New.Initialize(cachedPath))
                            {
                                _remoteCatalog = v2New;
                                _isInitialized = true;
                                Debug.Log($"[HyperContent] Remote catalog updated and cached (catalogHash changed or new)");
                                return true;
                            }
                        }
                        else
                        {
                            // v1 catalog: TTL-based behavior
                            if (File.Exists(cachedPath))
                            {
                                var fileInfo = new FileInfo(cachedPath);
                                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                long fileTime = ((DateTimeOffset)fileInfo.LastWriteTime.ToUniversalTime()).ToUnixTimeSeconds();
                                if (now - fileTime < CATALOG_CACHE_MAX_AGE_SECONDS)
                                {
                                    _remoteCatalog = new LocalContentCatalog();
                                    if (_remoteCatalog.Initialize(cachedPath))
                                    {
                                        _isInitialized = true;
                                        return true;
                                    }
                                }
                            }
                            var tempCatalog = new LocalContentCatalog();
                            string tempPath = Path.Combine(Application.temporaryCachePath, "temp_catalog.json");
                            File.WriteAllText(tempPath, catalogJson);
                            if (tempCatalog.Initialize(tempPath))
                            {
                                SaveCatalogToCache(catalogJson);
                                _remoteCatalog = tempCatalog;
                                _isInitialized = true;
                                try { File.Delete(tempPath); } catch { }
                                return true;
                            }
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[HyperContent] Failed to fetch remote catalog: {request.error}, using cached/local catalog");
                        if (File.Exists(cachedPath))
                        {
                            byte[] cachedHash = null;
                            try
                            {
                                cachedHash = LocalContentCatalogV2.GetCatalogHashFromJson(File.ReadAllText(cachedPath));
                            }
                            catch { }
                            if (cachedHash != null)
                            {
                                _remoteCatalog = new LocalContentCatalogV2();
                                if (_remoteCatalog.Initialize(cachedPath)) { _isInitialized = true; return true; }
                            }
                            _remoteCatalog = new LocalContentCatalog();
                            if (_remoteCatalog.Initialize(cachedPath)) { _isInitialized = true; return true; }
                        }
                    }
                }

                _isInitialized = _localCatalog?.IsValid ?? false;
                return _isInitialized;
            }
            catch (Exception e)
            {
                Debug.LogError($"[HyperContent] Error fetching remote catalog: {e.Message}");
                _isInitialized = _localCatalog?.IsValid ?? false;
                return _isInitialized;
            }
        }
        
        /// <summary>
        /// Save catalog JSON to local cache
        /// </summary>
        private void SaveCatalogToCache(string catalogJson)
        {
            try
            {
                string cachedPath = GetCachedCatalogPath();
                string dir = Path.GetDirectoryName(cachedPath);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                // Atomic write: write to temp file first
                string tempPath = cachedPath + ".tmp";
                File.WriteAllText(tempPath, catalogJson);
                
                // Atomic rename
                if (File.Exists(cachedPath))
                {
                    File.Delete(cachedPath);
                }
                File.Move(tempPath, cachedPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HyperContent] Failed to cache remote catalog: {e.Message}");
            }
        }
        
        /// <summary>
        /// Get path for cached remote catalog
        /// </summary>
        private string GetCachedCatalogPath()
        {
            // Use URL hash as filename to avoid path issues
            string urlHash = ComputeSimpleHash(_remoteCatalogUrl);
            return Path.Combine(_localCachePath, $"remote_{urlHash}.catalog.json");
        }
        
        /// <summary>
        /// Get path for local catalog (fallback)
        /// </summary>
        private string GetLocalCatalogPath()
        {
            // Try StreamingAssets first
            string streamingPath = Path.Combine(Application.streamingAssetsPath, "catalog.catalog.json");
            if (File.Exists(streamingPath))
            {
                return streamingPath;
            }
            
            // Try persistent data path
            return Path.Combine(Application.persistentDataPath, "catalog.catalog.json");
        }
        
        /// <summary>
        /// Derive bundle base URL from catalog URL (directory part) for v2 catalog RemoteUrl.
        /// </summary>
        private static string GetBaseUrlFromCatalogUrl(string catalogUrl)
        {
            if (string.IsNullOrEmpty(catalogUrl)) return null;
            int lastSlash = catalogUrl.LastIndexOf('/');
            if (lastSlash <= 0) return catalogUrl;
            return catalogUrl.Substring(0, lastSlash);
        }

        /// <summary>
        /// Simple hash function for URL
        /// </summary>
        private string ComputeSimpleHash(string input)
        {
            int hash = 0;
            foreach (char c in input)
            {
                hash = hash * 31 + c;
            }
            return Math.Abs(hash).ToString("x8");
        }
        
        public bool TryGetBundleName(string assetKey, out string bundleName)
        {
            // Remote catalog takes precedence (Owner0 v1 definition: remote overrides local)
            if (_remoteCatalog != null && _remoteCatalog.TryGetBundleName(assetKey, out bundleName))
            {
                return true;
            }
            
            if (_localCatalog != null && _localCatalog.TryGetBundleName(assetKey, out bundleName))
            {
                return true;
            }
            
            bundleName = null;
            return false;
        }
        
        public bool TryGetBundleInfo(string bundleName, out BundleInfo bundleInfo)
        {
            // Remote catalog takes precedence
            if (_remoteCatalog != null && _remoteCatalog.TryGetBundleInfo(bundleName, out bundleInfo))
            {
                return true;
            }
            
            if (_localCatalog != null && _localCatalog.TryGetBundleInfo(bundleName, out bundleInfo))
            {
                return true;
            }
            
            bundleInfo = null;
            return false;
        }
        
        public IEnumerable<string> GetAllAssetKeys()
        {
            // Merge keys from both catalogs, remote takes precedence
            var keys = new HashSet<string>();
            
            if (_localCatalog != null)
            {
                foreach (var key in _localCatalog.GetAllAssetKeys())
                {
                    keys.Add(key);
                }
            }
            
            if (_remoteCatalog != null)
            {
                foreach (var key in _remoteCatalog.GetAllAssetKeys())
                {
                    keys.Add(key);
                }
            }
            
            return keys;
        }
        
        public IEnumerable<string> GetAllBundleNames()
        {
            // Merge bundle names from both catalogs, remote takes precedence
            var bundles = new HashSet<string>();
            
            if (_localCatalog != null)
            {
                foreach (var bundle in _localCatalog.GetAllBundleNames())
                {
                    bundles.Add(bundle);
                }
            }
            
            if (_remoteCatalog != null)
            {
                foreach (var bundle in _remoteCatalog.GetAllBundleNames())
                {
                    bundles.Add(bundle);
                }
            }
            
            return bundles;
        }
        
        public void Release()
        {
            _remoteCatalog?.Release();
            _localCatalog?.Release();
            _remoteCatalog = null;
            _localCatalog = null;
            _isInitialized = false;
        }
    }
}
