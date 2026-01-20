using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Validates build results and checks for errors
    /// </summary>
    public static class BuildValidator
    {
        /// <summary>
        /// Validate build context and report errors
        /// </summary>
        public static bool Validate(BuildContext context)
        {
            bool isValid = true;
            
            // Check for duplicate keys
            ValidateDuplicateKeys(context);
            
            // Check for invalid keys
            ValidateInvalidKeys(context);
            
            // Check for missing resources
            ValidateMissingResources(context);
            
            // Generate bundle size report
            GenerateBundleSizeReport(context);
            
            // Check if there are any errors
            if (context.Errors.Count > 0)
            {
                isValid = false;
            }
            
            return isValid;
        }
        
        /// <summary>
        /// Validate for duplicate asset keys
        /// </summary>
        private static void ValidateDuplicateKeys(BuildContext context)
        {
            var keyCounts = new Dictionary<string, List<string>>();
            
            foreach (var kvp in context.KeyToGuid)
            {
                var key = kvp.Key;
                var guid = kvp.Value;
                
                if (!keyCounts.ContainsKey(key))
                {
                    keyCounts[key] = new List<string>();
                }
                
                if (context.GuidToPath.TryGetValue(guid, out var path))
                {
                    keyCounts[key].Add(path);
                }
            }
            
            foreach (var kvp in keyCounts)
            {
                if (kvp.Value.Count > 1)
                {
                    var paths = string.Join(", ", kvp.Value);
                    context.Errors.Add(new BuildError(
                        $"Duplicate asset key '{kvp.Key}' found in multiple assets: {paths}",
                        null,
                        kvp.Key
                    ));
                }
            }
        }
        
        /// <summary>
        /// Validate asset keys for invalid characters or formats
        /// </summary>
        private static void ValidateInvalidKeys(BuildContext context)
        {
            foreach (var kvp in context.AssetMarkers)
            {
                var guid = kvp.Key;
                var marker = kvp.Value;
                
                if (!marker.ValidateKey(out var error))
                {
                    if (context.GuidToPath.TryGetValue(guid, out var path))
                    {
                        context.Errors.Add(new BuildError(error, path, marker.assetKey));
                    }
                    else
                    {
                        context.Errors.Add(new BuildError(error, null, marker.assetKey));
                    }
                }
                
                // Additional validation: check for reserved characters
                if (!string.IsNullOrEmpty(marker.assetKey))
                {
                    // Check for null characters
                    if (marker.assetKey.Contains('\0'))
                    {
                        context.Errors.Add(new BuildError(
                            "Asset key contains null character",
                            context.GuidToPath.TryGetValue(guid, out var p) ? p : null,
                            marker.assetKey
                        ));
                    }
                }
            }
        }
        
        /// <summary>
        /// Validate that all referenced resources exist
        /// </summary>
        private static void ValidateMissingResources(BuildContext context)
        {
            var outputDir = context.Config.outputDirectory;
            
            // Check that all bundle files exist
            foreach (var bundleName in context.BundleToAssets.Keys)
            {
                var bundlePath = Path.Combine(outputDir, bundleName);
                if (!File.Exists(bundlePath))
                {
                    context.Errors.Add(new BuildError(
                        $"Bundle file not found: {bundlePath}",
                        null,
                        bundleName
                    ));
                }
            }
            
            // Check that all asset files exist
            foreach (var kvp in context.GuidToPath)
            {
                var path = kvp.Value;
                if (!File.Exists(path))
                {
                    context.Errors.Add(new BuildError(
                        $"Asset file not found: {path}",
                        path,
                        null
                    ));
                }
            }
            
            // Check that all asset keys have corresponding assets
            foreach (var kvp in context.KeyToGuid)
            {
                var key = kvp.Key;
                var guid = kvp.Value;
                
                if (!context.GuidToPath.ContainsKey(guid))
                {
                    context.Errors.Add(new BuildError(
                        $"Asset key '{key}' references non-existent asset GUID: {guid}",
                        null,
                        key
                    ));
                }
            }
        }
        
        /// <summary>
        /// Generate bundle size report
        /// </summary>
        private static void GenerateBundleSizeReport(BuildContext context)
        {
            if (context.Report == null)
            {
                return;
            }
            
            var outputDir = context.Config.outputDirectory;
            var bundleSizes = new List<BundleSizeInfo>();
            long totalSize = 0;
            
            foreach (var kvp in context.BundleToAssets)
            {
                var bundleName = kvp.Key;
                var assetGuids = kvp.Value;
                var bundlePath = Path.Combine(outputDir, bundleName);
                
                if (File.Exists(bundlePath))
                {
                    var fileInfo = new FileInfo(bundlePath);
                    var size = fileInfo.Length;
                    totalSize += size;
                    
                    bundleSizes.Add(new BundleSizeInfo
                    {
                        BundleName = bundleName,
                        SizeBytes = size,
                        AssetCount = assetGuids.Count
                    });
                }
            }
            
            if (bundleSizes.Count > 0)
            {
                context.Report.BundleSizes = bundleSizes.OrderByDescending(b => b.SizeBytes).ToList();
                context.Report.TotalBundleSize = totalSize;
                context.Report.TotalBundles = bundleSizes.Count;
                context.Report.AverageBundleSize = totalSize / bundleSizes.Count;
                
                if (bundleSizes.Count > 0)
                {
                    context.Report.LargestBundle = bundleSizes[0];
                    context.Report.SmallestBundle = bundleSizes[bundleSizes.Count - 1];
                }
            }
            
            // Log bundle size report
            Debug.Log($"[HyperContent] Bundle Size Report:");
            Debug.Log($"  Total Bundles: {context.Report.TotalBundles}");
            Debug.Log($"  Total Size: {FormatBytes(context.Report.TotalBundleSize)}");
            Debug.Log($"  Average Size: {FormatBytes(context.Report.AverageBundleSize)}");
            
            if (context.Report.LargestBundle != null)
            {
                Debug.Log($"  Largest Bundle: {context.Report.LargestBundle.BundleName} ({FormatBytes(context.Report.LargestBundle.SizeBytes)})");
            }
            
            if (context.Report.SmallestBundle != null)
            {
                Debug.Log($"  Smallest Bundle: {context.Report.SmallestBundle.BundleName} ({FormatBytes(context.Report.SmallestBundle.SizeBytes)})");
            }
        }
        
        /// <summary>
        /// Format bytes to human-readable string
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            
            return $"{len:0.##} {sizes[order]}";
        }
    }
}
