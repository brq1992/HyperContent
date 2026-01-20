using System;
using System.Collections.Generic;

namespace HyperContent
{
    /// <summary>
    /// Schema definition for catalog JSON structure
    /// This is the contract that all catalog implementations must follow
    /// Unity JsonUtility doesn't support Dictionary, so we use arrays
    /// </summary>
    [Serializable]
    public class CatalogSchema
    {
        /// <summary>
        /// Catalog format version
        /// </summary>
        public int version = 1;
        
        /// <summary>
        /// Catalog name/identifier
        /// </summary>
        public string name;
        
        /// <summary>
        /// Catalog creation timestamp (Unix timestamp)
        /// </summary>
        public long timestamp;
        
        /// <summary>
        /// Mapping from asset key to bundle name (serialized as array)
        /// </summary>
        public AssetBundleMapping[] assetToBundle = new AssetBundleMapping[0];
        
        /// <summary>
        /// Bundle information list (serialized as array)
        /// </summary>
        public BundleInfoData[] bundles = new BundleInfoData[0];
        
        /// <summary>
        /// Key-value pair for asset to bundle mapping
        /// </summary>
        [Serializable]
        public class AssetBundleMapping
        {
            public string key;
            public string bundle;
        }
        
        /// <summary>
        /// Serialized bundle info data (for JSON serialization)
        /// </summary>
        [Serializable]
        public class BundleInfoData
        {
            public string name;
            public long size;
            public string hash;
            public int version;
            public string location; // "Local", "Remote", "StreamingAssets", "Resources"
            public string remoteUrl;
            public string localPath;
            public string[] dependencies;
            public string[] assetKeys;
        }
    }
}
