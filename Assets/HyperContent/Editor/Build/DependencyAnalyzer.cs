using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace com.igg.hypercontent.editor
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
            
            // Collect all asset GUIDs that are marked
            var allAssetGuids = context.AssetMarkers.Keys.ToList();
            
            // Build dependency graph for each marked asset
            foreach (var assetGuid in allAssetGuids)
            {
                AnalyzeAssetDependencies(assetGuid, context);
            }
            
            // Note: Bundle dependencies are calculated after AssignBundles() is called
            // because we need AssetToBundle mapping first
        }
        
        /// <summary>
        /// Pre-build estimate of bundle dependencies using <c>AssetDatabase.GetDependencies(path, true)</c>.
        /// Produces a reasonable approximation for validation and editor catalog generation.
        /// <para/>
        /// For actual builds, <see cref="DefaultBuildExecutor.RebuildBundleDependenciesFromSbpResults"/>
        /// overwrites <see cref="BuildContext.BundleDependencies"/> with SBP's object-level dependency
        /// data which is the ground truth (same approach as Addressables).
        /// </summary>
        public static void BuildBundleDependencies(BuildContext context)
        {
            context.BundleDependencies.Clear();
            if (context.AssetToBundle == null || context.GuidToPath == null)
                return;

            foreach (var kvp in context.AssetToBundle)
            {
                var assetGuid = kvp.Key;
                var bundleName = kvp.Value;
                if (!context.GuidToPath.TryGetValue(assetGuid, out var assetPath) || string.IsNullOrEmpty(assetPath))
                    continue;

                var depPaths = AssetDatabase.GetDependencies(assetPath, true);
                foreach (var depPath in depPaths)
                {
                    if (depPath == assetPath)
                        continue;
                    if (depPath.EndsWith(".cs") || depPath.EndsWith(".js"))
                        continue;

                    var depGuid = AssetDatabase.AssetPathToGUID(depPath);
                    if (string.IsNullOrEmpty(depGuid))
                        continue;
                    if (!context.AssetToBundle.TryGetValue(depGuid, out var depBundleName))
                        continue;
                    if (depBundleName == bundleName)
                        continue;

                    if (!context.BundleDependencies.ContainsKey(bundleName))
                        context.BundleDependencies[bundleName] = new HashSet<string>();
                    context.BundleDependencies[bundleName].Add(depBundleName);
                }
            }
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
        /// Assign assets to bundles using the configured grouping strategy
        /// </summary>
        public static void AssignBundles(BuildContext context)
        {
            // Create grouping strategy based on config
            var strategy = BundleGroupingStrategyFactory.CreateStrategy(context.Config.groupingStrategy);
            
            // Validate strategy
            var validationErrors = strategy.Validate(context);
            if (validationErrors.Count > 0)
            {
                foreach (var error in validationErrors)
                {
                    context.Errors.Add(new BuildError(error));
                }
                return;
            }
            
            // Assign bundles using strategy
            if (!strategy.AssignBundles(context))
            {
                context.Errors.Add(new BuildError(
                    $"Bundle grouping failed using strategy: {strategy.StrategyName}"
                ));
            }
        }
    }
}
