using System.Collections.Generic;
using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// Interface for content catalog management
    /// Catalog is the index that maps asset keys to bundles
    /// </summary>
    public interface IContentCatalog
    {
        /// <summary>
        /// Initialize catalog from a source (file path, URL, etc.)
        /// </summary>
        /// <param name="source">Source identifier (file path, URL, etc.)</param>
        /// <returns>True if initialization succeeded</returns>
        bool Initialize(string source);
        
        /// <summary>
        /// Check if catalog is initialized and valid
        /// </summary>
        bool IsValid { get; }
        
        /// <summary>
        /// Get bundle name for a given asset key
        /// </summary>
        /// <param name="assetKey">Asset key</param>
        /// <param name="bundleName">Output bundle name</param>
        /// <returns>True if key exists in catalog</returns>
        bool TryGetBundleName(string assetKey, out string bundleName);
        
        /// <summary>
        /// Get bundle info for a given bundle name
        /// </summary>
        /// <param name="bundleName">Bundle name</param>
        /// <param name="bundleInfo">Output bundle info</param>
        /// <returns>True if bundle exists in catalog</returns>
        bool TryGetBundleInfo(string bundleName, out BundleInfo bundleInfo);
        
        /// <summary>
        /// Get all asset keys in the catalog
        /// </summary>
        /// <returns>Collection of asset keys</returns>
        IEnumerable<string> GetAllAssetKeys();
        
        /// <summary>
        /// Get all bundle names in the catalog
        /// </summary>
        /// <returns>Collection of bundle names</returns>
        IEnumerable<string> GetAllBundleNames();
        
        /// <summary>
        /// Get catalog version
        /// </summary>
        int Version { get; }
        
        /// <summary>
        /// Release catalog resources
        /// </summary>
        void Release();
    }
}
