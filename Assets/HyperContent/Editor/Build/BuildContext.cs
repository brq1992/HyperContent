using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Context object that holds build state and configuration
    /// </summary>
    public class BuildContext
    {
        /// <summary>
        /// Build configuration
        /// </summary>
        public BuildConfig Config { get; set; }
        
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
        /// Build errors
        /// </summary>
        public List<BuildError> Errors { get; set; } = new List<BuildError>();
        
        /// <summary>
        /// Build warnings
        /// </summary>
        public List<BuildWarning> Warnings { get; set; } = new List<BuildWarning>();
        
        /// <summary>
        /// Build report data
        /// </summary>
        public BuildReport Report { get; set; } = new BuildReport();
    }
    
    /// <summary>
    /// Build error information
    /// </summary>
    public class BuildError
    {
        public string Message { get; set; }
        public string AssetPath { get; set; }
        public string AssetKey { get; set; }
        
        public BuildError(string message, string assetPath = null, string assetKey = null)
        {
            Message = message;
            AssetPath = assetPath;
            AssetKey = assetKey;
        }
    }
    
    /// <summary>
    /// Build warning information
    /// </summary>
    public class BuildWarning
    {
        public string Message { get; set; }
        public string AssetPath { get; set; }
        public string AssetKey { get; set; }
        
        public BuildWarning(string message, string assetPath = null, string assetKey = null)
        {
            Message = message;
            AssetPath = assetPath;
            AssetKey = assetKey;
        }
    }
    
    /// <summary>
    /// Build configuration
    /// </summary>
    [Serializable]
    public class BuildConfig
    {
        [Tooltip("Output directory for bundles")]
        public string outputDirectory = "Assets/StreamingAssets";
        
        [Tooltip("Catalog name")]
        public string catalogName = "default_catalog";
        
        [Tooltip("Build target platform")]
        public BuildTarget buildTarget = BuildTarget.StandaloneWindows64;
        
        [Tooltip("Compression method for bundles")]
        public BundleCompressionType compressionType = BundleCompressionType.Lz4;
        
        [Tooltip("Include asset dependencies in bundles")]
        public bool includeDependencies = true;
        
        [Tooltip("Force rebuild all bundles")]
        public bool forceRebuild = false;
        
        [Tooltip("Generate build report")]
        public bool generateReport = true;
    }
    
    /// <summary>
    /// Compression type for AssetBundles
    /// </summary>
    public enum BundleCompressionType
    {
        None,
        Lz4,
        Lz4HC
    }
}
