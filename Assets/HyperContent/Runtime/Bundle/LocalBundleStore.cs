using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// Enhanced local file-based bundle store implementation
    /// Features: atomic writes, corruption prevention, hash verification, prune strategy
    /// </summary>
    public class LocalBundleStore : IBundleStore
    {
        private string _cacheRoot;
        private long _maxCacheSizeBytes;
        private Dictionary<string, CacheEntry> _cacheMetadata = new Dictionary<string, CacheEntry>();
        private const string METADATA_FILE = "cache_metadata.json";
        
        /// <summary>
        /// Cache entry metadata for prune strategy
        /// </summary>
        [Serializable]
        private class CacheEntry
        {
            public string bundleName;
            public long size;
            public string hash;
            public long lastAccessTime; // Unix timestamp
            public long createTime; // Unix timestamp
        }
        
        /// <summary>
        /// Cache metadata container
        /// </summary>
        [Serializable]
        private class CacheMetadata
        {
            public List<CacheEntry> entries = new List<CacheEntry>();
        }
        
        public bool Initialize(string cacheRoot)
        {
            _cacheRoot = cacheRoot;
            
            if (string.IsNullOrEmpty(_cacheRoot))
            {
                _cacheRoot = Path.Combine(Application.persistentDataPath, "HyperContent", "Cache");
            }
            
            // Default max cache size: 1GB
            _maxCacheSizeBytes = 1024L * 1024 * 1024;
            
            try
            {
                if (!Directory.Exists(_cacheRoot))
                {
                    Directory.CreateDirectory(_cacheRoot);
                }
                
                // Load cache metadata
                LoadCacheMetadata();
                
                // Verify existing files and remove corrupted ones
                VerifyAndCleanCache();
                
                Debug.Log($"[HyperContent] BundleStore initialized: {_cacheRoot}, cache size: {GetCacheSize() / (1024 * 1024)}MB");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HyperContent] BundleStore initialization failed: {e.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Set maximum cache size in bytes
        /// </summary>
        public void SetMaxCacheSize(long maxSizeBytes)
        {
            _maxCacheSizeBytes = maxSizeBytes;
        }
        
        public bool Exists(string bundleName)
        {
            string path = GetBundlePath(bundleName);
            if (!File.Exists(path))
            {
                return false;
            }
            
            // Verify file integrity if metadata exists
            if (_cacheMetadata.TryGetValue(bundleName, out var entry))
            {
                // Check if file size matches
                try
                {
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.Length != entry.size)
                    {
                        Debug.LogWarning($"[HyperContent] Bundle size mismatch, removing: {bundleName}");
                        Delete(bundleName);
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }
            
            return true;
        }
        
        public string GetLocalPath(string bundleName)
        {
            string path = GetBundlePath(bundleName);
            return File.Exists(path) ? path : null;
        }
        
        public bool Save(string bundleName, byte[] data, string hash)
        {
            try
            {
                // Verify hash before saving
                if (!string.IsNullOrEmpty(hash))
                {
                    string actualHash = ComputeHash(data);
                    if (actualHash != hash)
                    {
                        Debug.LogError($"[HyperContent] Hash mismatch for bundle {bundleName}, expected: {hash}, actual: {actualHash}");
                        return false;
                    }
                }
                
                string path = GetBundlePath(bundleName);
                string dir = Path.GetDirectoryName(path);
                
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                // Atomic write: write to temp file first, then rename
                string tempPath = path + ".tmp";
                
                // Delete temp file if exists
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
                
                // Write to temp file
                File.WriteAllBytes(tempPath, data);
                
                // Verify temp file integrity
                byte[] verifyData = File.ReadAllBytes(tempPath);
                if (verifyData.Length != data.Length)
                {
                    File.Delete(tempPath);
                    Debug.LogError($"[HyperContent] Write verification failed for bundle {bundleName}");
                    return false;
                }
                
                // Verify hash of written file
                if (!string.IsNullOrEmpty(hash))
                {
                    string writtenHash = ComputeHash(verifyData);
                    if (writtenHash != hash)
                    {
                        File.Delete(tempPath);
                        Debug.LogError($"[HyperContent] Hash verification failed after write for bundle {bundleName}");
                        return false;
                    }
                }
                
                // Atomic rename (replaces existing file atomically on most file systems)
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tempPath, path);
                
                // Update metadata
                long currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var entry = new CacheEntry
                {
                    bundleName = bundleName,
                    size = data.Length,
                    hash = hash ?? ComputeHash(data),
                    lastAccessTime = currentTime,
                    createTime = _cacheMetadata.TryGetValue(bundleName, out var oldEntry) ? oldEntry.createTime : currentTime
                };
                _cacheMetadata[bundleName] = entry;
                SaveCacheMetadata();
                
                // Prune cache if needed
                PruneCacheIfNeeded();
                
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HyperContent] Failed to save bundle {bundleName}: {e.Message}");
                // Clean up temp file
                string tempPath = GetBundlePath(bundleName) + ".tmp";
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                return false;
            }
        }
        
        public bool Load(string bundleName, out byte[] data)
        {
            data = null;
            
            try
            {
                string path = GetBundlePath(bundleName);
                if (!File.Exists(path))
                {
                    return false;
                }
                
                // Read file
                data = File.ReadAllBytes(path);
                
                // Verify integrity if metadata exists
                if (_cacheMetadata.TryGetValue(bundleName, out var entry))
                {
                    // Check size
                    if (data.Length != entry.size)
                    {
                        Debug.LogWarning($"[HyperContent] Bundle size mismatch during load: {bundleName}, expected: {entry.size}, actual: {data.Length}");
                        Delete(bundleName);
                        data = null;
                        return false;
                    }
                    
                    // Verify hash
                    string actualHash = ComputeHash(data);
                    if (!string.IsNullOrEmpty(entry.hash) && actualHash != entry.hash)
                    {
                        Debug.LogWarning($"[HyperContent] Bundle hash mismatch during load: {bundleName}");
                        Delete(bundleName);
                        data = null;
                        return false;
                    }
                }
                
                // Update last access time
                if (_cacheMetadata.TryGetValue(bundleName, out var cacheEntry))
                {
                    cacheEntry.lastAccessTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    SaveCacheMetadata();
                }
                
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HyperContent] Failed to load bundle {bundleName}: {e.Message}");
                // File might be corrupted, try to delete it
                try
                {
                    Delete(bundleName);
                }
                catch { }
                return false;
            }
        }
        
        public bool VerifyHash(string bundleName, string expectedHash)
        {
            if (string.IsNullOrEmpty(expectedHash))
            {
                return false;
            }
            
            if (!Load(bundleName, out byte[] data))
            {
                return false;
            }
            
            string actualHash = ComputeHash(data);
            bool isValid = actualHash == expectedHash;
            
            if (!isValid)
            {
                Debug.LogWarning($"[HyperContent] Hash verification failed for bundle {bundleName}, expected: {expectedHash}, actual: {actualHash}");
                // Remove corrupted bundle
                Delete(bundleName);
            }
            
            return isValid;
        }
        
        public bool Delete(string bundleName)
        {
            try
            {
                string path = GetBundlePath(bundleName);
                bool deleted = false;
                
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted = true;
                }
                
                // Delete temp file if exists
                string tempPath = path + ".tmp";
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                
                // Remove from metadata
                _cacheMetadata.Remove(bundleName);
                SaveCacheMetadata();
                
                return deleted;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HyperContent] Failed to delete bundle {bundleName}: {e.Message}");
                return false;
            }
        }
        
        public long GetCacheSize()
        {
            try
            {
                if (!Directory.Exists(_cacheRoot))
                {
                    return 0;
                }
                
                long totalSize = 0;
                foreach (var file in Directory.GetFiles(_cacheRoot, "*", SearchOption.AllDirectories))
                {
                    // Skip temp files and metadata
                    if (file.EndsWith(".tmp") || file.EndsWith(METADATA_FILE))
                    {
                        continue;
                    }
                    
                    var fileInfo = new FileInfo(file);
                    totalSize += fileInfo.Length;
                }
                return totalSize;
            }
            catch
            {
                return 0;
            }
        }
        
        public void ClearCache()
        {
            try
            {
                if (Directory.Exists(_cacheRoot))
                {
                    Directory.Delete(_cacheRoot, true);
                    Directory.CreateDirectory(_cacheRoot);
                }
                
                _cacheMetadata.Clear();
                SaveCacheMetadata();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HyperContent] Failed to clear cache: {e.Message}");
            }
        }
        
        /// <summary>
        /// Prune cache using LRU strategy when cache size exceeds limit
        /// </summary>
        public void PruneCache()
        {
            long currentSize = GetCacheSize();
            if (currentSize <= _maxCacheSizeBytes)
            {
                return;
            }
            
            // Sort by last access time (LRU)
            var sortedEntries = _cacheMetadata.Values
                .OrderBy(e => e.lastAccessTime)
                .ToList();
            
            long targetSize = _maxCacheSizeBytes * 8 / 10; // Keep 80% of max size
            
            foreach (var entry in sortedEntries)
            {
                if (currentSize <= targetSize)
                {
                    break;
                }
                
                if (Delete(entry.bundleName))
                {
                    currentSize -= entry.size;
                    Debug.Log($"[HyperContent] Pruned bundle: {entry.bundleName}, size: {entry.size}");
                }
            }
        }
        
        private void PruneCacheIfNeeded()
        {
            long currentSize = GetCacheSize();
            if (currentSize > _maxCacheSizeBytes)
            {
                PruneCache();
            }
        }
        
        private void LoadCacheMetadata()
        {
            try
            {
                string metadataPath = Path.Combine(_cacheRoot, METADATA_FILE);
                if (File.Exists(metadataPath))
                {
                    string json = File.ReadAllText(metadataPath);
                    var metadata = JsonUtility.FromJson<CacheMetadata>(json);
                    
                    if (metadata != null && metadata.entries != null)
                    {
                        _cacheMetadata.Clear();
                        foreach (var entry in metadata.entries)
                        {
                            _cacheMetadata[entry.bundleName] = entry;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HyperContent] Failed to load cache metadata: {e.Message}");
            }
        }
        
        private void SaveCacheMetadata()
        {
            try
            {
                string metadataPath = Path.Combine(_cacheRoot, METADATA_FILE);
                var metadata = new CacheMetadata
                {
                    entries = _cacheMetadata.Values.ToList()
                };
                
                string json = JsonUtility.ToJson(metadata, true);
                File.WriteAllText(metadataPath, json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HyperContent] Failed to save cache metadata: {e.Message}");
            }
        }
        
        private void VerifyAndCleanCache()
        {
            try
            {
                if (!Directory.Exists(_cacheRoot))
                {
                    return;
                }
                
                var filesToRemove = new List<string>();
                
                // Check all bundle files
                foreach (var file in Directory.GetFiles(_cacheRoot, "*" + NamingRules.BUNDLE_FILE_EXTENSION, SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    string bundleName = fileName.Replace('_', '/').Replace('_', '\\');
                    
                    // Check if file exists in metadata
                    if (!_cacheMetadata.ContainsKey(bundleName))
                    {
                        // Orphaned file, remove it
                        filesToRemove.Add(file);
                        continue;
                    }
                    
                    // Verify file integrity
                    var entry = _cacheMetadata[bundleName];
                    var fileInfo = new FileInfo(file);
                    
                    if (fileInfo.Length != entry.size)
                    {
                        Debug.LogWarning($"[HyperContent] Corrupted bundle detected: {bundleName}, removing");
                        filesToRemove.Add(file);
                        _cacheMetadata.Remove(bundleName);
                        continue;
                    }
                    
                    // Verify hash if possible
                    try
                    {
                        byte[] data = File.ReadAllBytes(file);
                        string actualHash = ComputeHash(data);
                        if (!string.IsNullOrEmpty(entry.hash) && actualHash != entry.hash)
                        {
                            Debug.LogWarning($"[HyperContent] Hash mismatch for bundle: {bundleName}, removing");
                            filesToRemove.Add(file);
                            _cacheMetadata.Remove(bundleName);
                        }
                    }
                    catch
                    {
                        // File read failed, consider corrupted
                        filesToRemove.Add(file);
                        _cacheMetadata.Remove(bundleName);
                    }
                }
                
                // Remove corrupted/orphaned files
                foreach (var file in filesToRemove)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch { }
                }
                
                // Remove temp files
                foreach (var file in Directory.GetFiles(_cacheRoot, "*.tmp", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch { }
                }
                
                // Save updated metadata
                if (filesToRemove.Count > 0)
                {
                    SaveCacheMetadata();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HyperContent] Cache verification failed: {e.Message}");
            }
        }
        
        private string GetBundlePath(string bundleName)
        {
            // Sanitize bundle name for file system
            string safeName = bundleName.Replace('/', '_').Replace('\\', '_');
            return Path.Combine(_cacheRoot, safeName + NamingRules.BUNDLE_FILE_EXTENSION);
        }
        
        private string ComputeHash(byte[] data)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(data);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}
