using System;
using System.Collections.Generic;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Build report containing statistics and analysis
    /// </summary>
    [Serializable]
    public class BuildReport
    {
        /// <summary>
        /// Total number of bundles built
        /// </summary>
        public int TotalBundles { get; set; }
        
        /// <summary>
        /// Total number of assets processed
        /// </summary>
        public int TotalAssets { get; set; }
        
        /// <summary>
        /// Total bundle size in bytes
        /// </summary>
        public long TotalBundleSize { get; set; }
        
        /// <summary>
        /// Largest bundle name and size
        /// </summary>
        public BundleSizeInfo LargestBundle { get; set; }
        
        /// <summary>
        /// Smallest bundle name and size
        /// </summary>
        public BundleSizeInfo SmallestBundle { get; set; }
        
        /// <summary>
        /// Average bundle size
        /// </summary>
        public long AverageBundleSize { get; set; }
        
        /// <summary>
        /// Bundle size information
        /// </summary>
        public List<BundleSizeInfo> BundleSizes { get; set; } = new List<BundleSizeInfo>();
        
        /// <summary>
        /// Duplicate dependencies (bundles that share dependencies)
        /// </summary>
        public List<DuplicateDependencyInfo> DuplicateDependencies { get; set; } = new List<DuplicateDependencyInfo>();
        
        /// <summary>
        /// Asset aggregation by bundle
        /// </summary>
        public Dictionary<string, List<string>> AssetAggregation { get; set; } = new Dictionary<string, List<string>>();
        
        /// <summary>
        /// Build duration in milliseconds
        /// </summary>
        public long BuildDurationMs { get; set; }
        
        /// <summary>
        /// Build timestamp
        /// </summary>
        public DateTime BuildTimestamp { get; set; }
    }
    
    /// <summary>
    /// Bundle size information
    /// </summary>
    [Serializable]
    public class BundleSizeInfo
    {
        public string BundleName { get; set; }
        public long SizeBytes { get; set; }
        public int AssetCount { get; set; }
    }
    
    /// <summary>
    /// Duplicate dependency information
    /// </summary>
    [Serializable]
    public class DuplicateDependencyInfo
    {
        public string DependencyBundle { get; set; }
        public List<string> DependentBundles { get; set; } = new List<string>();
    }
}
