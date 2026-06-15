using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using com.igg.hypercontent.runtime;
using com.igg.hypercontent.shared;
using UnityEditor;
using UnityEngine;

namespace com.igg.hypercontent.editor
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
            
            // Round-trip validation and bundle size report (only after catalog has been generated)
            if (checkBundleFiles)
            {
                ValidateCatalogRoundTrip(context);
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
        /// After <c>settings.json</c> is written: fail if the file contains absolute URLs (<c>://</c>).
        /// Matches <see cref="RuntimeSettings"/> policy — remote catalog/hash paths are relative; CDN base is set at runtime.
        /// </summary>
        public static bool ValidateExportedSettingsJson(BuildContext pContext)
        {
            if (pContext?.Config == null)
                return true;

            string catalogDir = pContext.Config.CatalogOutputDirectory;
            string settingsPath = Path.Combine(catalogDir, HyperContentPaths.SETTINGS_FILENAME);
            if (!File.Exists(settingsPath))
            {
                pContext.Errors.Add(new BuildError($"settings.json not found for validation: {settingsPath}", settingsPath));
                return false;
            }

            try
            {
                string json = File.ReadAllText(settingsPath);
                if (json.IndexOf("://", StringComparison.Ordinal) >= 0)
                {
                    pContext.Errors.Add(new BuildError(
                        "settings.json must not contain absolute URLs (substring '://'). " +
                        "Use relative remote paths only; set CDN base at runtime via SetRemoteBundleBaseUrl.",
                        settingsPath));
                    return false;
                }
            }
            catch (Exception e)
            {
                pContext.Errors.Add(new BuildError($"Failed to validate settings.json: {e.Message}", settingsPath));
                return false;
            }

            return true;
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
            var bundleDir = context.Config.BundleOutputDirectory;
            
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
                    
                    var bundlePath = Path.Combine(bundleDir, bundleFileName);
                    bundlePath = bundlePath.Replace("\\", "/"); // Normalize path separators
                    
                    if (!File.Exists(bundlePath))
                    {
                        // Try alternative paths
                        var alternativePaths = new[]
                        {
                            Path.Combine(bundleDir, bundleName).Replace("\\", "/"),
                            Path.Combine(bundleDir, bundleName + ".bundle").Replace("\\", "/"),
                            Path.Combine(bundleDir, bundleFileName).Replace("\\", "/")
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
                                if (Directory.Exists(bundleDir))
                                {
                                    var files = Directory.GetFiles(bundleDir, "*", SearchOption.TopDirectoryOnly);
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
        /// Validate catalog round-trip: serialize → deserialize → re-serialize must produce identical output.
        /// Delegates to CatalogGenerator.Serialize/Deserialize so format changes (JSON → binary) are transparent.
        /// </summary>
        private static void ValidateCatalogRoundTrip(BuildContext context)
        {
            var catalogDir = context.Config.CatalogOutputDirectory;
            var catalogPath = Path.Combine(catalogDir, HyperContentPaths.LOCAL_CATALOG_FILENAME);

            if (!File.Exists(catalogPath))
            {
                context.Warnings.Add(new BuildWarning(
                    $"Round-trip validation skipped: catalog file not found at {catalogPath}",
                    catalogPath
                ));
                return;
            }

            try
            {
                // round-trip 必须用与构建时一致的 format（来自 BuildConfig），否则字节比较毫无意义。
                var format = context.Config.catalogFormat;
                var originalBytes = File.ReadAllBytes(catalogPath);
                var deserialized = CatalogGenerator.Deserialize(originalBytes, format);

                if (deserialized == null)
                {
                    context.Errors.Add(new BuildError(
                        "Round-trip validation failed: catalog deserialized to null",
                        catalogPath,
                        null
                    ));
                    return;
                }

                int errorsBeforeCatalogIntegrity = context.Errors.Count;

                if (deserialized.schemaVersion != CatalogSchema.CurrentSchemaVersion)
                {
                    context.Errors.Add(new BuildError(
                        $"Catalog schemaVersion={deserialized.schemaVersion}, expected {CatalogSchema.CurrentSchemaVersion}. Rebuild catalog.",
                        catalogPath,
                        null));
                    return;
                }

                if (deserialized.bundleRecords != null)
                {
                    for (int i = 0; i < deserialized.bundleRecords.Count; i++)
                    {
                        var br = deserialized.bundleRecords[i];
                        if (br.contentLocation != (int)ContentLocation.Remote)
                            continue;

                        bool invalidPath = br.remoteRelativePathIndex < 0
                            || br.remoteRelativePathIndex >= deserialized.stringTable.Length
                            || string.IsNullOrEmpty(deserialized.stringTable[br.remoteRelativePathIndex]);

                        if (invalidPath)
                        {
                            string bundleName = (br.bundleNameIndex >= 0 && br.bundleNameIndex < deserialized.stringTable.Length)
                                ? deserialized.stringTable[br.bundleNameIndex]
                                : $"bundleRecords[{i}]";
                            context.Errors.Add(new BuildError(
                                $"Remote bundle '{bundleName}' must have a valid remoteRelativePathIndex into stringTable. " +
                                $"Got index {br.remoteRelativePathIndex}.",
                                catalogPath,
                                bundleName));
                        }
                    }
                }

                if (context.Errors.Count > errorsBeforeCatalogIntegrity)
                    return;

                var reserializedBytes = CatalogGenerator.Serialize(deserialized, format);

                if (!ByteArrayEquals(originalBytes, reserializedBytes))
                {
                    var diffPosition = FindFirstByteDifference(originalBytes, reserializedBytes);

                    context.Errors.Add(new BuildError(
                        $"Round-trip validation failed: re-serialized catalog differs from original. " +
                        $"First difference at byte {diffPosition}. " +
                        $"Original size={originalBytes.Length}, Re-serialized size={reserializedBytes.Length}. " +
                        GetByteDiffSnippet(originalBytes, reserializedBytes, diffPosition),
                        catalogPath,
                        null
                    ));
                }
                else
                {
                    Debug.Log($"[HyperContent] Round-trip validation passed ({originalBytes.Length} bytes)");
                }
            }
            catch (Exception e)
            {
                context.Errors.Add(new BuildError(
                    $"Round-trip validation exception: {e.Message}",
                    catalogPath,
                    null
                ));
            }
        }

        private static bool ByteArrayEquals(byte[] pA, byte[] pB)
        {
            if (pA.Length != pB.Length) return false;
            for (int i = 0; i < pA.Length; i++)
            {
                if (pA[i] != pB[i]) return false;
            }
            return true;
        }

        private static int FindFirstByteDifference(byte[] pA, byte[] pB)
        {
            int minLen = Math.Min(pA.Length, pB.Length);
            for (int i = 0; i < minLen; i++)
            {
                if (pA[i] != pB[i])
                    return i;
            }
            return minLen;
        }

        /// <summary>
        /// Try to produce a human-readable diff snippet. Falls back to hex if UTF-8 decoding fails.
        /// </summary>
        private static string GetByteDiffSnippet(byte[] pOriginal, byte[] pReserialized, int pDiffPos)
        {
            const int CONTEXT_BYTES = 40;
            try
            {
                int start = Math.Max(0, pDiffPos - CONTEXT_BYTES);
                int originalLen = Math.Min(pOriginal.Length - start, CONTEXT_BYTES * 2);
                int reserializedLen = Math.Min(pReserialized.Length - start, CONTEXT_BYTES * 2);

                var originalSnippet = System.Text.Encoding.UTF8.GetString(pOriginal, start, originalLen);
                var reserializedSnippet = System.Text.Encoding.UTF8.GetString(pReserialized, start, reserializedLen);

                return $"Original: ...{originalSnippet}... | Re-serialized: ...{reserializedSnippet}...";
            }
            catch
            {
                return "(binary content, text snippet unavailable)";
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
            
            var bundleDir = context.Config.BundleOutputDirectory;
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
                
                var bundlePath = Path.Combine(bundleDir, bundleFileName);
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
