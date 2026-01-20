using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Builds Unity AssetBundles and generates output directory structure
    /// </summary>
    public static class BundleBuilder
    {
        /// <summary>
        /// Build all bundles defined in the context
        /// </summary>
        public static bool BuildBundles(BuildContext context)
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
            
            // Collect bundle information
            CollectBundleInfo(context, outputDir, buildManifest);
            
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
        private static List<AssetBundleBuild> PrepareAssetBundleBuilds(BuildContext context)
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
        private static BuildAssetBundleOptions GetBuildAssetBundleOptions(BundleCompressionType compressionType)
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
        /// Collect bundle information after build
        /// </summary>
        private static void CollectBundleInfo(BuildContext context, string outputDir, AssetBundleManifest manifest)
        {
            var allBundles = manifest.GetAllAssetBundles();
            
            foreach (var bundleName in allBundles)
            {
                var bundlePath = Path.Combine(outputDir, bundleName);
                
                if (!File.Exists(bundlePath))
                {
                    context.Warnings.Add(new BuildWarning($"Bundle file not found: {bundlePath}", null, bundleName));
                    continue;
                }
                
                var fileInfo = new FileInfo(bundlePath);
                var size = fileInfo.Length;
                var hash = CalculateFileHash(bundlePath);
                
                // Get dependencies from manifest
                var dependencies = manifest.GetAllDependencies(bundleName);
                
                // Get asset keys for this bundle
                var assetKeys = new List<string>();
                if (context.BundleToAssets.TryGetValue(bundleName, out var assetGuids))
                {
                    foreach (var assetGuid in assetGuids)
                    {
                        if (context.AssetMarkers.TryGetValue(assetGuid, out var marker))
                        {
                            assetKeys.Add(marker.assetKey);
                        }
                    }
                }
                
                // Store bundle info (will be used by CatalogGenerator)
                // For now, we'll store it in a temporary structure
                // The actual BundleInfo will be created by CatalogGenerator
            }
        }
        
        /// <summary>
        /// Calculate SHA256 hash of a file
        /// </summary>
        private static string CalculateFileHash(string filePath)
        {
            using (var sha256 = SHA256.Create())
            {
                using (var stream = File.OpenRead(filePath))
                {
                    var hash = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }
        
        /// <summary>
        /// Clear old bundle files
        /// </summary>
        private static void ClearOldBundles(string outputDir)
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
