using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HyperContent;
using UnityEditor;
using UnityEngine;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Generates catalog.json file following schemaVersion=1 strictly
    /// </summary>
    public static class CatalogGenerator
    {
        /// <summary>
        /// Generate catalog.json file from build context
        /// </summary>
        public static bool GenerateCatalog(BuildContext context)
        {
            try
            {
                // Create catalog schema following strict v1 format
                var catalog = new CatalogSchema
                {
                    version = 1, // Strictly schemaVersion=1
                    name = context.Config.catalogName,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    assetToBundle = new CatalogSchema.AssetBundleMapping[0],
                    bundles = new CatalogSchema.BundleInfoData[0]
                };
                
                // Build assetToBundle mapping
                var assetToBundleList = new List<CatalogSchema.AssetBundleMapping>();
                foreach (var kvp in context.KeyToGuid)
                {
                    var assetKey = kvp.Key;
                    var assetGuid = kvp.Value;
                    
                    if (context.AssetToBundle.TryGetValue(assetGuid, out var bundleName))
                    {
                        assetToBundleList.Add(new CatalogSchema.AssetBundleMapping
                        {
                            key = assetKey,
                            bundle = bundleName
                        });
                    }
                }
                catalog.assetToBundle = assetToBundleList.ToArray();
                
                // Build bundles array
                var bundlesList = new List<CatalogSchema.BundleInfoData>();
                var outputDir = context.Config.outputDirectory;
                
                foreach (var kvp in context.BundleToAssets)
                {
                    var bundleName = kvp.Key;
                    var assetGuids = kvp.Value;
                    
                    var bundlePath = Path.Combine(outputDir, bundleName);
                    
                    if (!File.Exists(bundlePath))
                    {
                        context.Warnings.Add(new BuildWarning($"Bundle file not found: {bundlePath}", null, bundleName));
                        continue;
                    }
                    
                    // Get bundle file info
                    var fileInfo = new FileInfo(bundlePath);
                    var size = fileInfo.Length;
                    var hash = CalculateFileHash(bundlePath);
                    
                    // Get dependencies
                    var dependencies = new HashSet<string>();
                    if (context.BundleDependencies.TryGetValue(bundleName, out var bundleDeps))
                    {
                        dependencies.UnionWith(bundleDeps);
                    }
                    
                    // Also check AssetBundle manifest for dependencies
                    var manifestPath = Path.Combine(outputDir, bundleName + ".manifest");
                    if (File.Exists(manifestPath))
                    {
                        var manifestDeps = GetDependenciesFromManifest(manifestPath);
                        dependencies.UnionWith(manifestDeps);
                    }
                    
                    // Get asset keys
                    var assetKeys = new List<string>();
                    foreach (var assetGuid in assetGuids)
                    {
                        if (context.AssetMarkers.TryGetValue(assetGuid, out var marker))
                        {
                            assetKeys.Add(marker.assetKey);
                        }
                    }
                    
                    // Determine location (for v1, we'll use StreamingAssets as default)
                    var location = "StreamingAssets";
                    var localPath = bundleName; // Relative to StreamingAssets
                    var remoteUrl = "";
                    
                    var bundleInfo = new CatalogSchema.BundleInfoData
                    {
                        name = bundleName,
                        size = size,
                        hash = hash,
                        version = 1,
                        location = location,
                        remoteUrl = remoteUrl,
                        localPath = localPath,
                        dependencies = dependencies.ToArray(),
                        assetKeys = assetKeys.ToArray()
                    };
                    
                    bundlesList.Add(bundleInfo);
                }
                
                catalog.bundles = bundlesList.ToArray();
                
                // Serialize to JSON
                var json = JsonUtility.ToJson(catalog, true);
                
                // Write to file
                var catalogPath = Path.Combine(outputDir, $"{context.Config.catalogName}.catalog.json");
                File.WriteAllText(catalogPath, json);
                
                Debug.Log($"[HyperContent] Catalog generated: {catalogPath}");
                
                return true;
            }
            catch (Exception e)
            {
                context.Errors.Add(new BuildError($"Failed to generate catalog: {e.Message}"));
                Debug.LogError($"[HyperContent] Catalog generation failed: {e}");
                return false;
            }
        }
        
        /// <summary>
        /// Calculate SHA256 hash of a file
        /// </summary>
        private static string CalculateFileHash(string filePath)
        {
            try
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
            catch (Exception e)
            {
                Debug.LogWarning($"[HyperContent] Failed to calculate hash for {filePath}: {e.Message}");
                return "";
            }
        }
        
        /// <summary>
        /// Parse dependencies from AssetBundle manifest file
        /// </summary>
        private static HashSet<string> GetDependenciesFromManifest(string manifestPath)
        {
            var dependencies = new HashSet<string>();
            
            try
            {
                var lines = File.ReadAllLines(manifestPath);
                bool inDependencies = false;
                
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    
                    if (trimmed.StartsWith("Dependencies:"))
                    {
                        inDependencies = true;
                        continue;
                    }
                    
                    if (inDependencies)
                    {
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("-"))
                        {
                            break;
                        }
                        
                        // Extract bundle name from dependency line
                        // Format: "- BundleName"
                        if (trimmed.StartsWith("-"))
                        {
                            var depName = trimmed.Substring(1).Trim();
                            if (!string.IsNullOrEmpty(depName))
                            {
                                dependencies.Add(depName);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HyperContent] Failed to parse manifest {manifestPath}: {e.Message}");
            }
            
            return dependencies;
        }
    }
}
