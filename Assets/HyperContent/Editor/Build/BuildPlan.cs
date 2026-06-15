using System;
using System.Collections.Generic;
using com.igg.hypercontent.runtime;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Build plan produced by grouping tools
    /// This is the output of grouping tools and input to build executors
    /// </summary>
    public class BuildPlan
    {
        /// <summary>
        /// Mapping from asset GUID to HyperContentAsset marker
        /// </summary>
        public Dictionary<string, HyperContentAsset> AssetMarkers { get; set; } = new Dictionary<string, HyperContentAsset>();
        
        /// <summary>
        /// Mapping from asset key to asset GUID
        /// </summary>
        public Dictionary<string, string> KeyToGuid { get; set; } = new Dictionary<string, string>();
        
        /// <summary>
        /// Mapping from asset GUID to asset path
        /// </summary>
        public Dictionary<string, string> GuidToPath { get; set; } = new Dictionary<string, string>();
        
        /// <summary>
        /// Mapping from bundle name to asset GUIDs
        /// </summary>
        public Dictionary<string, HashSet<string>> BundleToAssets { get; set; } = new Dictionary<string, HashSet<string>>();
        
        /// <summary>
        /// Mapping from asset GUID to bundle name
        /// </summary>
        public Dictionary<string, string> AssetToBundle { get; set; } = new Dictionary<string, string>();
        
        /// <summary>
        /// Asset dependencies (GUID to GUIDs)
        /// </summary>
        public Dictionary<string, HashSet<string>> Dependencies { get; set; } = new Dictionary<string, HashSet<string>>();
        
        /// <summary>
        /// Bundle dependencies (bundle name to bundle names)
        /// </summary>
        public Dictionary<string, HashSet<string>> BundleDependencies { get; set; } = new Dictionary<string, HashSet<string>>();
        
        /// <summary>
        /// Per-bundle compression from the grouping tool. Must contain one entry per key in <see cref="BundleToAssets"/> (case-insensitive keys).
        /// </summary>
        public Dictionary<string, BundleCompressionType> BundleCompression { get; set; } =
            new Dictionary<string, BundleCompressionType>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Per-bundle flags from the grouping tool (build plan / "manual table" equivalent), e.g. Addressable entry labels → <see cref="BundleTagFlags"/>.
        /// Merged at catalog generation with per-asset <see cref="HyperContentAsset"/> flags (OR). Keys use the same bundle names as <see cref="BundleToAssets"/>.
        /// </summary>
        public Dictionary<string, BundleTagFlags> BundleTagFlagsFromPlan { get; set; } =
            new Dictionary<string, BundleTagFlags>(StringComparer.OrdinalIgnoreCase);
        
        /// <summary>
        /// Build errors found during grouping
        /// </summary>
        public List<BuildError> Errors { get; set; } = new List<BuildError>();
        
        /// <summary>
        /// Build warnings found during grouping
        /// </summary>
        public List<BuildWarning> Warnings { get; set; } = new List<BuildWarning>();
    }
}
