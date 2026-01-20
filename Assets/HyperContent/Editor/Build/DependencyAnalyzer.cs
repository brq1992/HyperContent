using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Analyzes asset dependencies and determines bundle assignments
    /// </summary>
    public static class DependencyAnalyzer
    {
        /// <summary>
        /// Analyze all assets and build dependency graph
        /// </summary>
        public static void AnalyzeDependencies(BuildContext context)
        {
            context.Dependencies.Clear();
            context.BundleDependencies.Clear();
            
            // Collect all asset GUIDs that are marked
            var allAssetGuids = context.AssetMarkers.Keys.ToList();
            
            // Build dependency graph for each marked asset
            foreach (var assetGuid in allAssetGuids)
            {
                AnalyzeAssetDependencies(assetGuid, context);
            }
            
            // Build bundle dependencies based on asset dependencies
            BuildBundleDependencies(context);
        }
        
        /// <summary>
        /// Analyze dependencies for a single asset
        /// </summary>
        private static void AnalyzeAssetDependencies(string assetGuid, BuildContext context)
        {
            if (!context.GuidToPath.TryGetValue(assetGuid, out var assetPath))
            {
                return;
            }
            
            var dependencies = new HashSet<string>();
            
            // Get direct dependencies
            var directDeps = AssetDatabase.GetDependencies(assetPath, false);
            foreach (var depPath in directDeps)
            {
                // Skip self
                if (depPath == assetPath)
                {
                    continue;
                }
                
                // Skip scripts and other non-asset files
                if (depPath.EndsWith(".cs") || depPath.EndsWith(".js"))
                {
                    continue;
                }
                
                var depGuid = AssetDatabase.AssetPathToGUID(depPath);
                if (!string.IsNullOrEmpty(depGuid))
                {
                    dependencies.Add(depGuid);
                }
            }
            
            context.Dependencies[assetGuid] = dependencies;
        }
        
        /// <summary>
        /// Build bundle dependencies from asset dependencies
        /// </summary>
        private static void BuildBundleDependencies(BuildContext context)
        {
            foreach (var kvp in context.Dependencies)
            {
                var assetGuid = kvp.Key;
                var dependencies = kvp.Value;
                
                // Get bundle for this asset
                if (!context.AssetToBundle.TryGetValue(assetGuid, out var bundleName))
                {
                    continue;
                }
                
                // Find dependencies that are in different bundles
                foreach (var depGuid in dependencies)
                {
                    if (context.AssetToBundle.TryGetValue(depGuid, out var depBundleName))
                    {
                        if (depBundleName != bundleName)
                        {
                            // Add bundle dependency
                            if (!context.BundleDependencies.ContainsKey(bundleName))
                            {
                                context.BundleDependencies[bundleName] = new HashSet<string>();
                            }
                            context.BundleDependencies[bundleName].Add(depBundleName);
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// Assign assets to bundles based on markers and grouping rules
        /// </summary>
        public static void AssignBundles(BuildContext context)
        {
            context.AssetToBundle.Clear();
            context.BundleToAssets.Clear();
            
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
            if (name.Length > HyperContent.Shared.NamingRules.MAX_BUNDLE_NAME_LENGTH)
            {
                name = name.Substring(0, HyperContent.Shared.NamingRules.MAX_BUNDLE_NAME_LENGTH);
            }
            
            return name.ToLowerInvariant();
        }
    }
}
