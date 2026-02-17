using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HyperContent.Shared;
using UnityEditor;
using UnityEngine;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Validates build results and checks for errors.
    /// Includes strong uniqueness: GUID, Name, and nameHash collision check.
    /// </summary>
    public static class BuildValidator
    {
        /// <summary>
        /// Validate build context and report errors
        /// </summary>
        /// <param name="context">Build context</param>
        /// <param name="checkBundleFiles">Whether to check if bundle files exist (only after build)</param>
        public static bool Validate(BuildContext context, bool checkBundleFiles = true)
        {
            bool isValid = true;
            
            // Uniqueness: GUID and Name (assetKey)
            ValidateGuidUniqueness(context);
            ValidateDuplicateKeys(context);
            ValidateNameHashCollision(context);
            
            // Check for invalid keys
            ValidateInvalidKeys(context);
            
            // Check for missing resources (only check bundle files if requested)
            ValidateMissingResources(context, checkBundleFiles);
            
            // Generate bundle size report (only if bundle files should be checked)
            if (checkBundleFiles)
            {
                GenerateBundleSizeReport(context);
            }
            
            // Check if there are any errors
            if (context.Errors.Count > 0)
            {
                isValid = false;
            }
            
            return isValid;
        }
        
        /// <summary>
        /// Ensure every asset GUID is unique (each GUID appears at most once).
        /// </summary>
        private static void ValidateGuidUniqueness(BuildContext context)
        {
            var guids = context.KeyToGuid.Values.ToList();
            var distinctCount = guids.Distinct().Count();
            if (distinctCount != guids.Count)
            {
                var duplicates = guids.GroupBy(g => g).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                var sample = string.Join(", ", duplicates.Take(5));
                context.Errors.Add(new BuildError(
                    $"GUID uniqueness violated: {guids.Count - distinctCount} duplicate GUID(s). Sample: {sample}",
                    null,
                    null
                ));
            }
        }

        /// <summary>
        /// If multiple different names hash to same nameHash, build fails (nameHash collision).
        /// </summary>
        private static void ValidateNameHashCollision(BuildContext context)
        {
            var nameHashToNames = new Dictionary<string, List<string>>();
            foreach (var kvp in context.KeyToGuid)
            {
                var name = kvp.Key;
                var hash = NameHashUtil.Compute(name);
                if (string.IsNullOrEmpty(hash)) continue;
                if (!nameHashToNames.TryGetValue(hash, out var list))
                {
                    list = new List<string>();
                    nameHashToNames[hash] = list;
                }
                list.Add(name);
            }
            foreach (var kvp in nameHashToNames)
            {
                if (kvp.Value.Count <= 1) continue;
                var distinctNames = kvp.Value.Distinct().ToList();
                if (distinctNames.Count <= 1) continue;
                context.Errors.Add(new BuildError(
                    $"Name hash collision: nameHash '{kvp.Key}' maps to multiple names: [{string.Join(", ", distinctNames)}]. Rename assets to avoid collision.",
                    null,
                    distinctNames.First()
                ));
            }
        }

        /// <summary>
        /// Validate for duplicate asset keys (Name uniqueness)
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
        private static void ValidateMissingResources(BuildContext context, bool checkBundleFiles = true)
        {
            var outputDir = context.Config.outputDirectory;
            
            // Check that all bundle files exist (only if requested, i.e., after build)
            if (checkBundleFiles)
            {
                foreach (var bundleName in context.BundleToAssets.Keys)
                {
                    // Get actual bundle file name from mapping (if Unity modified it)
                    string bundleFileName;
                    if (context.ExpectedToActualBundleName != null && 
                        context.ExpectedToActualBundleName.TryGetValue(bundleName, out var actualName))
                    {
                        bundleFileName = actualName;
                    }
                    else
                    {
                        // Fallback: Unity BuildPipeline automatically adds .bundle extension
                        bundleFileName = bundleName;
                        if (!bundleFileName.EndsWith(".bundle"))
                        {
                            bundleFileName = bundleName + ".bundle";
                        }
                    }
                    
                    var bundlePath = Path.Combine(outputDir, bundleFileName);
                    bundlePath = bundlePath.Replace("\\", "/"); // Normalize path separators
                    
                    if (!File.Exists(bundlePath))
                    {
                        // Try alternative paths
                        var alternativePaths = new[]
                        {
                            Path.Combine(outputDir, bundleName).Replace("\\", "/"),
                            Path.Combine(outputDir, bundleName + ".bundle").Replace("\\", "/"),
                            Path.Combine(outputDir, bundleFileName).Replace("\\", "/")
                        };
                        
                        bool found = false;
                        foreach (var altPath in alternativePaths)
                        {
                            if (File.Exists(altPath))
                            {
                                found = true;
                                break;
                            }
                        }
                        
                        if (!found)
                        {
                            // Log available files for debugging
                            var availableFiles = new List<string>();
                            try
                            {
                                if (Directory.Exists(outputDir))
                                {
                                    var files = Directory.GetFiles(outputDir, "*", SearchOption.TopDirectoryOnly);
                                    foreach (var file in files)
                                    {
                                        var fileName = Path.GetFileName(file);
                                        if (!fileName.EndsWith(".manifest") && !fileName.EndsWith(".catalog.json"))
                                        {
                                            availableFiles.Add(fileName);
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Ignore errors
                            }
                            
                            var availableFilesStr = availableFiles.Count > 0 
                                ? $" Available files: {string.Join(", ", availableFiles)}" 
                                : " No files found in output directory.";
                            
                            context.Errors.Add(new BuildError(
                                $"Bundle file not found: {bundlePath}. " +
                                $"Expected bundle name: {bundleName}. " +
                                $"Actual bundle name (from mapping): {bundleFileName}. " +
                                $"Please check if the bundle was built successfully.{availableFilesStr}",
                                null,
                                bundleName
                            ));
                        }
                    }
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
                
                // Get actual bundle file name from mapping (if Unity modified it)
                string bundleFileName;
                if (context.ExpectedToActualBundleName != null && 
                    context.ExpectedToActualBundleName.TryGetValue(bundleName, out var actualName))
                {
                    bundleFileName = actualName;
                }
                else
                {
                    // Fallback: Unity BuildPipeline automatically adds .bundle extension
                    bundleFileName = bundleName;
                    if (!bundleFileName.EndsWith(".bundle"))
                    {
                        bundleFileName = bundleName + ".bundle";
                    }
                }
                
                var bundlePath = Path.Combine(outputDir, bundleFileName);
                bundlePath = bundlePath.Replace("\\", "/"); // Normalize path separators
                
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
