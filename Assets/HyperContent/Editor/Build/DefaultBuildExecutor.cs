using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Default build executor that uses Unity's AssetBundle system
    /// This executor combines BundleBuilder and CatalogGenerator functionality
    /// </summary>
    public class DefaultBuildExecutor : IBuildExecutor
    {
        public string ExecutorName => "Default Build Executor";
        
        public string Description => "Builds Unity AssetBundles and generates single catalog (v2 format per RESOURCE_LOADING_SYSTEM_SPEC).";
        
        public BuildResult Execute(BuildPlan plan, BuildConfig config)
        {
            // Determine compression type from plan or use default from config
            var compressionType = DetermineCompressionType(plan, config);
            
            var context = new BuildContext
            {
                Config = config,
                AssetMarkers = plan.AssetMarkers,
                KeyToGuid = plan.KeyToGuid,
                GuidToPath = plan.GuidToPath,
                BundleToAssets = plan.BundleToAssets,
                AssetToBundle = plan.AssetToBundle,
                Dependencies = plan.Dependencies,
                BundleDependencies = plan.BundleDependencies,
                Errors = plan.Errors,
                Warnings = plan.Warnings,
                Report = config.generateReport ? new BuildReport() : null
            };
            
            // Override compression type in config for this build
            context.Config.compressionType = compressionType;
            
            try
            {
                // Step 1: Validate before build (don't check bundle files as they don't exist yet)
                if (!BuildValidator.Validate(context, checkBundleFiles: false))
                {
                    return BuildResult.Failure(context, "Build validation failed");
                }
                
                // Step 2: Build bundles
                if (!BuildBundles(context))
                {
                    return BuildResult.Failure(context, "Bundle build failed");
                }
                
                // Step 3: Generate catalog (single v2 format per RESOURCE_LOADING_SYSTEM_SPEC)
                if (!CatalogGenerator.GenerateCatalogV2(context))
                {
                    return BuildResult.Failure(context, "Catalog generation failed");
                }
                
                // Step 4: Final validation
                BuildValidator.Validate(context);
                
                // Step 5: Generate report
                if (config.generateReport)
                {
                    BuildReportGenerator.GenerateReport(context);
                }
                
                if (context.Errors.Count > 0)
                {
                    return BuildResult.Failure(context, "Build completed with errors");
                }
                
                return BuildResult.Success(context);
            }
            catch (Exception e)
            {
                context.Errors.Add(new BuildError($"Build exception: {e.Message}"));
                return BuildResult.Failure(context, $"Build exception: {e.Message}");
            }
        }
        
        public List<string> Validate(BuildPlan plan, BuildConfig config)
        {
            var errors = new List<string>();
            
            // Validate output directory
            if (string.IsNullOrEmpty(config.outputDirectory))
            {
                errors.Add("Output directory cannot be empty");
            }
            
            // Validate catalog name
            if (string.IsNullOrEmpty(config.catalogName))
            {
                errors.Add("Catalog name cannot be empty");
            }
            
            // Validate plan has bundles
            if (plan.BundleToAssets.Count == 0)
            {
                errors.Add("Build plan has no bundles to build");
            }
            
            return errors;
        }
        
        /// <summary>
        /// Determine compression type from BuildPlan or use default from config
        /// If plan specifies compression for bundles, use the most common one, or default if all are different
        /// </summary>
        private BundleCompressionType DetermineCompressionType(BuildPlan plan, BuildConfig config)
        {
            // If plan has compression settings for bundles, use them
            if (plan.BundleCompression != null && plan.BundleCompression.Count > 0)
            {
                // Count compression types
                var compressionCounts = new Dictionary<BundleCompressionType, int>();
                foreach (var compression in plan.BundleCompression.Values)
                {
                    compressionCounts.TryGetValue(compression, out var count);
                    compressionCounts[compression] = count + 1;
                }
                
                // Find the most common compression type
                var mostCommon = compressionCounts.OrderByDescending(kvp => kvp.Value).FirstOrDefault();
                
                // If all bundles use the same compression, use it
                if (mostCommon.Value == plan.BundleCompression.Count)
                {
                    return mostCommon.Key;
                }
                
                // If different compressions are used, log a warning and use the most common
                Debug.LogWarning($"[HyperContent] BuildPlan contains bundles with different compression types. " +
                               $"Using most common: {mostCommon.Key} ({mostCommon.Value}/{plan.BundleCompression.Count} bundles). " +
                               $"Note: Unity BuildPipeline only supports one compression type per build.");
                return mostCommon.Key;
            }
            
            // No compression specified in plan, use default from config
            return config.compressionType;
        }
        
        /// <summary>
        /// Build Unity AssetBundles (reuses BundleBuilder logic)
        /// </summary>
        private bool BuildBundles(BuildContext context)
        {
            var startTime = DateTime.Now;
            
            // Ensure output directory exists
            var outputDir = context.Config.outputDirectory;
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }
            
            // Clear old bundles if force rebuild
            if (context.Config.forceRebuild)
            {
                ClearOldBundles(outputDir);
            }
            
            // Prepare AssetBundle build map
            var assetBundleBuilds = PrepareAssetBundleBuilds(context);
            
            if (assetBundleBuilds.Count == 0)
            {
                context.Errors.Add(new BuildError("No bundles to build"));
                return false;
            }
            
            // Build AssetBundles
            var buildManifest = BuildPipeline.BuildAssetBundles(
                outputDir,
                assetBundleBuilds.ToArray(),
                GetBuildAssetBundleOptions(context.Config.compressionType),
                context.Config.buildTarget
            );
            
            if (buildManifest == null)
            {
                context.Errors.Add(new BuildError("AssetBundle build failed"));
                return false;
            }
            
            // Store the actual bundle names from manifest for later use
            // Unity might modify bundle names, so we need to map our expected names to actual names
            StoreActualBundleNames(context, buildManifest);
            
            // Calculate build duration
            var duration = DateTime.Now - startTime;
            if (context.Report != null)
            {
                context.Report.BuildDurationMs = (long)duration.TotalMilliseconds;
                context.Report.BuildTimestamp = DateTime.Now;
            }
            
            return true;
        }
        
        /// <summary>
        /// Prepare AssetBundle build configurations
        /// </summary>
        private List<AssetBundleBuild> PrepareAssetBundleBuilds(BuildContext context)
        {
            var builds = new List<AssetBundleBuild>();
            
            foreach (var kvp in context.BundleToAssets)
            {
                var bundleName = kvp.Key;
                var assetGuids = kvp.Value;
                
                var assetPaths = new List<string>();
                var addressableNames = new List<string>();
                
                foreach (var assetGuid in assetGuids)
                {
                    if (!context.GuidToPath.TryGetValue(assetGuid, out var assetPath))
                    {
                        continue;
                    }
                    
                    assetPaths.Add(assetPath);
                    
                    // Get asset key for addressable name
                    if (context.AssetMarkers.TryGetValue(assetGuid, out var marker))
                    {
                        addressableNames.Add(marker.assetKey);
                    }
                    else
                    {
                        // Use path as fallback
                        addressableNames.Add(assetPath);
                    }
                }
                
                if (assetPaths.Count > 0)
                {
                    builds.Add(new AssetBundleBuild
                    {
                        assetBundleName = bundleName,
                        assetNames = assetPaths.ToArray(),
                        addressableNames = addressableNames.ToArray()
                    });
                }
            }
            
            return builds;
        }
        
        /// <summary>
        /// Get BuildAssetBundleOptions based on compression type
        /// </summary>
        private BuildAssetBundleOptions GetBuildAssetBundleOptions(BundleCompressionType compressionType)
        {
            var options = BuildAssetBundleOptions.None;
            
            switch (compressionType)
            {
                case BundleCompressionType.None:
                    options |= BuildAssetBundleOptions.UncompressedAssetBundle;
                    break;
                case BundleCompressionType.Lz4:
                    options |= BuildAssetBundleOptions.ChunkBasedCompression;
                    break;
                case BundleCompressionType.Lz4HC:
                    options |= BuildAssetBundleOptions.ChunkBasedCompression;
                    break;
            }
            
            return options;
        }
        
        /// <summary>
        /// Store the mapping from expected bundle names to actual bundle names from manifest
        /// </summary>
        private void StoreActualBundleNames(BuildContext context, AssetBundleManifest manifest)
        {
            var allBundles = manifest.GetAllAssetBundles();
            
            Debug.Log($"[HyperContent] BuildManifest contains {allBundles.Length} bundles:");
            foreach (var bundle in allBundles)
            {
                Debug.Log($"  - {bundle}");
            }
            
            Debug.Log($"[HyperContent] Expected {context.BundleToAssets.Count} bundles:");
            foreach (var expectedName in context.BundleToAssets.Keys)
            {
                Debug.Log($"  - {expectedName}");
            }
            
            // Create mapping from expected names to actual names
            foreach (var expectedName in context.BundleToAssets.Keys)
            {
                // Try exact match
                if (allBundles.Contains(expectedName))
                {
                    context.ExpectedToActualBundleName[expectedName] = expectedName;
                    Debug.Log($"[HyperContent] Mapped '{expectedName}' -> '{expectedName}' (exact match)");
                    continue;
                }
                
                // Try with .bundle extension
                var withExtension = expectedName + ".bundle";
                if (allBundles.Contains(withExtension))
                {
                    context.ExpectedToActualBundleName[expectedName] = withExtension;
                    Debug.Log($"[HyperContent] Mapped '{expectedName}' -> '{withExtension}' (with extension)");
                    continue;
                }
                
                // Try to find by matching (case-insensitive, ignoring extension)
                var expectedNameLower = expectedName.ToLowerInvariant();
                bool found = false;
                foreach (var actualName in allBundles)
                {
                    var actualNameWithoutExt = actualName;
                    if (actualNameWithoutExt.EndsWith(".bundle"))
                    {
                        actualNameWithoutExt = actualNameWithoutExt.Substring(0, actualNameWithoutExt.Length - ".bundle".Length);
                    }
                    
                    if (actualNameWithoutExt.ToLowerInvariant() == expectedNameLower)
                    {
                        context.ExpectedToActualBundleName[expectedName] = actualName;
                        Debug.Log($"[HyperContent] Mapped '{expectedName}' -> '{actualName}' (case-insensitive match)");
                        found = true;
                        break;
                    }
                }
                
                if (!found)
                {
                    Debug.LogWarning($"[HyperContent] Could not find actual bundle name for expected name: '{expectedName}'");
                }
            }
        }
        
        /// <summary>
        /// Clear old bundle files
        /// </summary>
        private void ClearOldBundles(string outputDir)
        {
            if (!Directory.Exists(outputDir))
            {
                return;
            }
            
            var bundleFiles = Directory.GetFiles(outputDir, "*.bundle", SearchOption.TopDirectoryOnly);
            foreach (var file in bundleFiles)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to delete old bundle: {file}, Error: {e.Message}");
                }
            }
            
            // Also delete manifest files
            var manifestFiles = Directory.GetFiles(outputDir, "*.manifest", SearchOption.TopDirectoryOnly);
            foreach (var file in manifestFiles)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to delete manifest: {file}, Error: {e.Message}");
                }
            }
        }
    }
}
