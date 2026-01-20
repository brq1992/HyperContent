using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// Interface for bundle storage management
    /// Handles local cache, persistence, and retrieval of bundles
    /// </summary>
    public interface IBundleStore
    {
        /// <summary>
        /// Initialize the bundle store
        /// </summary>
        /// <param name="cacheRoot">Root directory for cache</param>
        /// <returns>True if initialization succeeded</returns>
        bool Initialize(string cacheRoot);
        
        /// <summary>
        /// Check if bundle exists in local cache
        /// </summary>
        /// <param name="bundleName">Bundle name</param>
        /// <returns>True if bundle exists locally</returns>
        bool Exists(string bundleName);
        
        /// <summary>
        /// Get local path for a bundle
        /// </summary>
        /// <param name="bundleName">Bundle name</param>
        /// <returns>Local file path, or null if not found</returns>
        string GetLocalPath(string bundleName);
        
        /// <summary>
        /// Save bundle data to local cache
        /// </summary>
        /// <param name="bundleName">Bundle name</param>
        /// <param name="data">Bundle data</param>
        /// <param name="hash">Expected hash for verification</param>
        /// <returns>True if save succeeded</returns>
        bool Save(string bundleName, byte[] data, string hash);
        
        /// <summary>
        /// Load bundle data from local cache
        /// </summary>
        /// <param name="bundleName">Bundle name</param>
        /// <param name="data">Output bundle data</param>
        /// <returns>True if load succeeded</returns>
        bool Load(string bundleName, out byte[] data);
        
        /// <summary>
        /// Verify bundle integrity using hash
        /// </summary>
        /// <param name="bundleName">Bundle name</param>
        /// <param name="expectedHash">Expected hash value</param>
        /// <returns>True if hash matches</returns>
        bool VerifyHash(string bundleName, string expectedHash);
        
        /// <summary>
        /// Delete bundle from local cache
        /// </summary>
        /// <param name="bundleName">Bundle name</param>
        /// <returns>True if deletion succeeded</returns>
        bool Delete(string bundleName);
        
        /// <summary>
        /// Get cache size in bytes
        /// </summary>
        long GetCacheSize();
        
        /// <summary>
        /// Clear all cached bundles
        /// </summary>
        void ClearCache();
    }
}
