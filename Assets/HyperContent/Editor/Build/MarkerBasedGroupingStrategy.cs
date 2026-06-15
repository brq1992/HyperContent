using System.Collections.Generic;
using com.igg.hypercontent.shared;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Grouping strategy that uses HyperContentAsset markers to determine bundle assignments
    /// This is the original grouping approach based on bundleGroup field in markers
    /// </summary>
    public class MarkerBasedGroupingStrategy : IBundleGroupingStrategy
    {
        public string StrategyName => "Marker-Based Groups";
        
        public bool AssignBundles(BuildContext context)
        {
            context.AssetToBundle.Clear();
            context.BundleToAssets.Clear();
            context.BundleCompression.Clear();
            
            foreach (var kvp in context.AssetMarkers)
            {
                var assetGuid = kvp.Key;
                var marker = kvp.Value;
                
                // Determine bundle name
                string bundleName;
                
                if (marker.forceSeparateBundle)
                {
                    // Force separate bundle: use asset key as bundle name
                    bundleName = SanitizeBundleName(marker.assetKey);
                }
                else if (!string.IsNullOrEmpty(marker.bundleGroup))
                {
                    // Use bundle group
                    bundleName = SanitizeBundleName(marker.bundleGroup);
                }
                else
                {
                    // Default: use asset key as bundle name
                    bundleName = SanitizeBundleName(marker.assetKey);
                }
                
                // Add asset to bundle
                if (!context.BundleToAssets.ContainsKey(bundleName))
                {
                    context.BundleToAssets[bundleName] = new HashSet<string>();
                }
                context.BundleToAssets[bundleName].Add(assetGuid);
                context.AssetToBundle[assetGuid] = bundleName;
                
                // If includeDependencies is true, add dependencies to the same bundle
                if (context.Config.includeDependencies && context.Dependencies.TryGetValue(assetGuid, out var deps))
                {
                    foreach (var depGuid in deps)
                    {
                        // Only add if dependency is not already in another bundle
                        if (!context.AssetToBundle.ContainsKey(depGuid))
                        {
                            context.BundleToAssets[bundleName].Add(depGuid);
                            context.AssetToBundle[depGuid] = bundleName;
                        }
                    }
                }
            }

            foreach (var bundleName in context.BundleToAssets.Keys)
                context.BundleCompression[bundleName] = context.Config.compressionType;

            return true;
        }
        
        public List<string> Validate(BuildContext context)
        {
            // Marker-based strategy is always valid if we have assets
            return new List<string>();
        }
        
        /// <summary>
        /// Sanitize bundle name to ensure it's valid
        /// </summary>
        private static string SanitizeBundleName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "default_bundle";
            }
            
            // Remove path separators and invalid characters
            name = name.Replace("/", "_").Replace("\\", "_");
            name = name.Replace(" ", "_");
            
            // Ensure it doesn't exceed max length
            if (name.Length > NamingRules.MAX_BUNDLE_NAME_LENGTH)
            {
                name = name.Substring(0, NamingRules.MAX_BUNDLE_NAME_LENGTH);
            }
            
            return name.ToLowerInvariant();
        }
    }
}
