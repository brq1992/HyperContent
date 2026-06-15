using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
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
        /// Catalog tags (e.g. Blocking), originating from build-time grouping data serialized into <see cref="CatalogSchema.BundleRecordEntry.bundleTagFlags"/>.
        /// Flag definition: <see cref="BundleTagFlags"/>.
        /// </summary>
        public BundleTagFlags TagFlags { get; internal set; }

        /// <summary>
        /// If <see cref="Location"/> is <see cref="ContentLocation.Remote"/>, path **after** the per-platform CDN folder,
        /// **without** <see cref="NamingRules.BUNDLE_FILE_EXTENSION"/> (catalog contract). Example: <c>my_patch_1.0</c> maps
        /// to the CDN object <c>Android/my_patch_1.0.bundle</c> when combined with base URL and platform segment.
        /// <see cref="HttpBundleTransport"/> appends the file extension when resolving URLs. Empty or null if not remote.
        /// </summary>
        public string RemoteRelativePath { get; set; }
        
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
