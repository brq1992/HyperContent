using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// Information about a content bundle
    /// </summary>
    public class BundleInfo
    {
        /// <summary>
        /// Bundle name/identifier
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// Bundle file size in bytes
        /// </summary>
        public long Size { get; set; }
        
        /// <summary>
        /// Bundle hash for integrity verification (SHA256)
        /// </summary>
        public string Hash { get; set; }
        
        /// <summary>
        /// Bundle version for update checking
        /// </summary>
        public int Version { get; set; }
        
        /// <summary>
        /// Bundle location
        /// </summary>
        public ContentLocation Location { get; set; }
        
        /// <summary>
        /// Remote URL if location is Remote
        /// </summary>
        public string RemoteUrl { get; set; }
        
        /// <summary>
        /// Local file path if location is Local or StreamingAssets
        /// </summary>
        public string LocalPath { get; set; }
        
        /// <summary>
        /// List of bundle names this bundle depends on
        /// </summary>
        public string[] Dependencies { get; set; }
        
        /// <summary>
        /// List of asset keys contained in this bundle
        /// </summary>
        public string[] AssetKeys { get; set; }
    }
}
