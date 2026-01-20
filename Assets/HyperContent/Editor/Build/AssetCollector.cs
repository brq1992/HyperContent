using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Collects all assets marked with HyperContentAsset markers
    /// </summary>
    public static class AssetCollector
    {
        /// <summary>
        /// Collect all marked assets from the project
        /// Supports two approaches:
        /// 1. Assets with HyperContentAsset markers (ScriptableObject)
        /// 2. Assets in Assets/HyperContent/Content/ directory (auto-collected)
        /// </summary>
        public static void CollectAssets(BuildContext context)
        {
            context.AssetMarkers.Clear();
            context.KeyToGuid.Clear();
            context.GuidToPath.Clear();
            
            // Approach 1: Collect assets with explicit HyperContentAsset markers
            CollectMarkedAssets(context);
            
            // Approach 2: Collect assets from content directories (with optional markers)
            CollectAssetsFromContentDirectories(context);
        }
        
        /// <summary>
        /// Collect assets that have explicit HyperContentAsset markers
        /// Marker naming convention: AssetName_HyperContent.asset or HyperContent_AssetName.asset
        /// </summary>
        private static void CollectMarkedAssets(BuildContext context)
        {
            var markerGuids = AssetDatabase.FindAssets($"t:{nameof(HyperContentAsset)}");
            
            foreach (var markerGuid in markerGuids)
            {
                var markerPath = AssetDatabase.GUIDToAssetPath(markerGuid);
                var marker = AssetDatabase.LoadAssetAtPath<HyperContentAsset>(markerPath);
                
                if (marker == null || string.IsNullOrEmpty(marker.assetKey))
                {
                    continue;
                }
                
                // Try to find the associated asset file
                var assetPath = FindAssetForMarker(markerPath);
                
                if (string.IsNullOrEmpty(assetPath))
                {
                    context.Warnings.Add(new BuildWarning(
                        $"Could not find asset for marker: {markerPath}. Marker will be skipped.",
                        markerPath,
                        marker.assetKey
                    ));
                    continue;
                }
                
                var assetGuid = AssetDatabase.AssetPathToGUID(assetPath);
                if (string.IsNullOrEmpty(assetGuid))
                {
                    continue;
                }
                
                RegisterAsset(context, assetGuid, assetPath, marker);
            }
        }
        
        /// <summary>
        /// Find the asset file associated with a marker file
        /// </summary>
        private static string FindAssetForMarker(string markerPath)
        {
            var markerDir = Path.GetDirectoryName(markerPath);
            var markerName = Path.GetFileNameWithoutExtension(markerPath);
            
            // Pattern 1: Marker is named "AssetName_HyperContent", asset is "AssetName"
            if (markerName.EndsWith("_HyperContent"))
            {
                var baseName = markerName.Substring(0, markerName.Length - "_HyperContent".Length);
                var dirFiles = Directory.GetFiles(markerDir, $"{baseName}.*");
                foreach (var file in dirFiles)
                {
                    if (!file.EndsWith(".meta") && !file.EndsWith(".asset") && file != markerPath)
                    {
                        return file.Replace("\\", "/");
                    }
                }
            }
            
            // Pattern 2: Marker is named "HyperContent_AssetName", asset is "AssetName"
            if (markerName.StartsWith("HyperContent_"))
            {
                var baseName = markerName.Substring("HyperContent_".Length);
                var dirFiles = Directory.GetFiles(markerDir, $"{baseName}.*");
                foreach (var file in dirFiles)
                {
                    if (!file.EndsWith(".meta") && !file.EndsWith(".asset") && file != markerPath)
                    {
                        return file.Replace("\\", "/");
                    }
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Collect assets from content directories
        /// Assets in Assets/HyperContent/Content/ are automatically included
        /// </summary>
        private static void CollectAssetsFromContentDirectories(BuildContext context)
        {
            // Directory-based collection: Assets in Assets/HyperContent/Content/ are automatically included
            var contentDirs = new[] { "Assets/HyperContent/Content" };
            
            foreach (var contentDir in contentDirs)
            {
                if (!Directory.Exists(contentDir))
                {
                    continue;
                }
                
                var assetGuids = AssetDatabase.FindAssets("", new[] { contentDir });
                foreach (var assetGuid in assetGuids)
                {
                    // Skip if already registered (from explicit markers)
                    if (context.AssetMarkers.ContainsKey(assetGuid))
                    {
                        continue;
                    }
                    
                    var assetPath = AssetDatabase.GUIDToAssetPath(assetGuid);
                    
                    // Skip meta files, scripts, and marker files
                    if (assetPath.EndsWith(".meta") || 
                        assetPath.EndsWith(".cs") || 
                        assetPath.Contains("HyperContent") && assetPath.EndsWith(".asset"))
                    {
                        continue;
                    }
                    
                    // Try to find a marker for this asset
                    var marker = FindMarkerForAsset(assetPath);
                    
                    if (marker != null)
                    {
                        RegisterAsset(context, assetGuid, assetPath, marker);
                    }
                    else
                    {
                        // If no marker found, create a default key from the asset path
                        var defaultKey = GenerateDefaultKey(assetPath);
                        var defaultMarker = CreateDefaultMarker(defaultKey, assetPath);
                        RegisterAsset(context, assetGuid, assetPath, defaultMarker);
                    }
                }
            }
        }
        
        /// <summary>
        /// Find HyperContentAsset marker for a given asset path
        /// </summary>
        private static HyperContentAsset FindMarkerForAsset(string assetPath)
        {
            var assetDir = Path.GetDirectoryName(assetPath);
            var assetName = Path.GetFileNameWithoutExtension(assetPath);
            
            // Look for marker files in the same directory
            var markerPaths = new[]
            {
                Path.Combine(assetDir, $"{assetName}_HyperContent.asset"),
                Path.Combine(assetDir, $"HyperContent_{assetName}.asset"),
            };
            
            foreach (var markerPath in markerPaths)
            {
                if (File.Exists(markerPath))
                {
                    var marker = AssetDatabase.LoadAssetAtPath<HyperContentAsset>(markerPath);
                    if (marker != null)
                    {
                        return marker;
                    }
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Create a default marker for an asset without a marker
        /// </summary>
        private static HyperContentAsset CreateDefaultMarker(string assetKey, string assetPath)
        {
            var marker = ScriptableObject.CreateInstance<HyperContentAsset>();
            marker.assetKey = assetKey;
            marker.bundleGroup = Path.GetDirectoryName(assetPath).Replace("\\", "/").Replace("Assets/", "");
            return marker;
        }
        
        /// <summary>
        /// Generate default asset key from asset path
        /// </summary>
        private static string GenerateDefaultKey(string assetPath)
        {
            // Remove Assets/ prefix and extension
            var key = assetPath.Replace("\\", "/");
            if (key.StartsWith("Assets/"))
            {
                key = key.Substring("Assets/".Length);
            }
            
            var ext = Path.GetExtension(key);
            if (!string.IsNullOrEmpty(ext))
            {
                key = key.Substring(0, key.Length - ext.Length);
            }
            
            return key;
        }
        
        /// <summary>
        /// Register an asset in the build context
        /// </summary>
        private static void RegisterAsset(BuildContext context, string assetGuid, string assetPath, HyperContentAsset marker)
        {
            // Validate marker
            if (!marker.ValidateKey(out var keyError))
            {
                context.Errors.Add(new BuildError($"Invalid asset key: {keyError}", assetPath, marker.assetKey));
                return;
            }
            
            if (!marker.ValidateGroup(out var groupError))
            {
                context.Errors.Add(new BuildError($"Invalid bundle group: {groupError}", assetPath, marker.assetKey));
                return;
            }
            
            // Check for duplicate keys
            if (context.KeyToGuid.ContainsKey(marker.assetKey))
            {
                var existingPath = context.GuidToPath[context.KeyToGuid[marker.assetKey]];
                context.Errors.Add(new BuildError(
                    $"Duplicate asset key '{marker.assetKey}' found. Existing: {existingPath}, New: {assetPath}",
                    assetPath,
                    marker.assetKey
                ));
                return;
            }
            
            // Register asset
            context.AssetMarkers[assetGuid] = marker;
            context.KeyToGuid[marker.assetKey] = assetGuid;
            context.GuidToPath[assetGuid] = assetPath;
        }
    }
}
