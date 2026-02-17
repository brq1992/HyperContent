using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using HyperContent.Editor;
using HyperContent.Editor.Build;

namespace AddressableGrouping
{
    /// <summary>
    /// Grouping tool that uses Addressable groups to organize assets
    /// This tool directly reads Addressable group data and creates bundles based on Addressable groups
    /// </summary>
    public class AddressableGroupingTool : IBundleGroupingTool
    {
        public string ToolName => "Addressable Grouping Tool";
        
        public string Description => "Uses Addressable Groups to organize assets into bundles. " +
                                    "Assets are grouped based on their Addressable group membership. " +
                                    "Only assets marked as Addressable will be included in the build plan.";
        
        public BuildPlan GeneratePlan(BuildConfig config)
        {
            var plan = new BuildPlan();
            
            try
            {
                // Step 1: Get Addressable settings
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    plan.Errors.Add(new BuildError(
                        "AddressableAssetSettings not found. Please ensure Addressables package is installed and initialized."
                    ));
                    return plan;
                }
                
                // Step 2: Collect Addressable assets
                CollectAddressableAssets(plan, settings);
                
                if (plan.Errors.Count > 0)
                {
                    return plan;
                }
                
                // Step 3: Analyze dependencies
                AnalyzeDependencies(plan);
                
                // Step 4: Assign bundles based on Addressable groups
                AssignBundlesFromAddressableGroups(plan, settings, config);
                
                // Step 5: Build bundle dependencies
                BuildBundleDependencies(plan);
                
                return plan;
            }
            catch (Exception e)
            {
                plan.Errors.Add(new BuildError(
                    $"Addressable grouping tool failed: {e.Message}\n{e.StackTrace}"
                ));
                return plan;
            }
        }
        
        public List<string> Validate(BuildConfig config)
        {
            var errors = new List<string>();
            
            // Check if Addressable settings exist
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                errors.Add("AddressableAssetSettings not found. Please ensure Addressables package is installed and initialized.");
                return errors;
            }
            
            // Check if there are any Addressable groups
            if (settings.groups == null || settings.groups.Count == 0)
            {
                errors.Add("No Addressable groups found. Please create at least one group in the Addressables Groups window.");
                return errors;
            }
            
            // Check if any assets are marked as Addressable
            var allEntries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(allEntries, false);
            
            if (allEntries.Count == 0)
            {
                errors.Add("No Addressable assets found. Please mark some assets as Addressable.");
            }
            
            return errors;
        }
        
        /// <summary>
        /// Collect all Addressable assets and create markers for them
        /// </summary>
        private void CollectAddressableAssets(BuildPlan plan, AddressableAssetSettings settings)
        {
            // Get all Addressable entries
            var allEntries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(allEntries, false);
            
            foreach (var entry in allEntries)
            {
                if (string.IsNullOrEmpty(entry.guid))
                {
                    continue;
                }
                
                var assetPath = AssetDatabase.GUIDToAssetPath(entry.guid);
                if (string.IsNullOrEmpty(assetPath))
                {
                    plan.Warnings.Add(new BuildWarning(
                        $"Addressable entry has invalid GUID: {entry.address}",
                        null,
                        entry.address
                    ));
                    continue;
                }
                
                // Create a marker for this Addressable asset
                // Use Addressable address as asset key, or fallback to asset path
                var assetKey = !string.IsNullOrEmpty(entry.address) 
                    ? entry.address 
                    : GenerateKeyFromPath(assetPath);
                
                // Check for duplicate keys
                if (plan.KeyToGuid.ContainsKey(assetKey))
                {
                    var existingPath = plan.GuidToPath[plan.KeyToGuid[assetKey]];
                    plan.Errors.Add(new BuildError(
                        $"Duplicate asset key '{assetKey}' found. Existing: {existingPath}, New: {assetPath}",
                        assetPath,
                        assetKey
                    ));
                    continue;
                }
                
                // Create a marker (we'll use a lightweight representation)
                var marker = ScriptableObject.CreateInstance<HyperContentAsset>();
                marker.assetKey = assetKey;
                marker.bundleGroup = entry.parentGroup != null ? entry.parentGroup.Name : "Default";
                
                // Register asset
                plan.AssetMarkers[entry.guid] = marker;
                plan.KeyToGuid[assetKey] = entry.guid;
                plan.GuidToPath[entry.guid] = assetPath;
            }
        }
        
        /// <summary>
        /// Generate asset key from asset path
        /// </summary>
        private string GenerateKeyFromPath(string assetPath)
        {
            // Remove Assets/ prefix and extension
            var key = assetPath.Replace("\\", "/");
            if (key.StartsWith("Assets/"))
            {
                key = key.Substring("Assets/".Length);
            }
            
            var ext = System.IO.Path.GetExtension(key);
            if (!string.IsNullOrEmpty(ext))
            {
                key = key.Substring(0, key.Length - ext.Length);
            }
            
            return key;
        }
        
        /// <summary>
        /// Analyze dependencies for collected assets
        /// </summary>
        private void AnalyzeDependencies(BuildPlan plan)
        {
            foreach (var assetGuid in plan.AssetMarkers.Keys.ToList())
            {
                if (!plan.GuidToPath.TryGetValue(assetGuid, out var assetPath))
                {
                    continue;
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
                
                plan.Dependencies[assetGuid] = dependencies;
            }
        }
        
        /// <summary>
        /// Assign bundles based on Addressable groups
        /// </summary>
        private void AssignBundlesFromAddressableGroups(BuildPlan plan, AddressableAssetSettings settings, BuildConfig config)
        {
            plan.AssetToBundle.Clear();
            plan.BundleToAssets.Clear();
            
            // Build mapping from asset GUID to Addressable group name
            var guidToGroupName = new Dictionary<string, string>();
            
            var allEntries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(allEntries, false);
            
            foreach (var entry in allEntries)
            {
                if (!string.IsNullOrEmpty(entry.guid))
                {
                    var groupName = entry.parentGroup != null ? entry.parentGroup.Name : "Default";
                    guidToGroupName[entry.guid] = groupName;
                }
            }
            
            // Assign bundles based on Addressable groups
            foreach (var kvp in plan.AssetMarkers)
            {
                var assetGuid = kvp.Key;
                
                // Get group name from Addressable
                if (!guidToGroupName.TryGetValue(assetGuid, out var groupName))
                {
                    // Fallback: use marker's bundleGroup or asset key
                    var marker = kvp.Value;
                    groupName = !string.IsNullOrEmpty(marker.bundleGroup) 
                        ? marker.bundleGroup 
                        : marker.assetKey;
                }
                
                // Sanitize group name for bundle name
                var bundleName = SanitizeBundleName(groupName);
                
                // Add to bundle mapping
                if (!plan.BundleToAssets.ContainsKey(bundleName))
                {
                    plan.BundleToAssets[bundleName] = new HashSet<string>();
                }
                plan.BundleToAssets[bundleName].Add(assetGuid);
                plan.AssetToBundle[assetGuid] = bundleName;
            }
            
            // Handle dependencies if enabled
            if (config.includeDependencies)
            {
                AddDependenciesToBundles(plan);
            }
        }
        
        /// <summary>
        /// Add dependencies to bundles if includeDependencies is enabled
        /// </summary>
        private void AddDependenciesToBundles(BuildPlan plan)
        {
            foreach (var kvp in plan.Dependencies)
            {
                var assetGuid = kvp.Key;
                var dependencies = kvp.Value;
                
                if (!plan.AssetToBundle.TryGetValue(assetGuid, out var bundleName))
                {
                    continue;
                }
                
                foreach (var depGuid in dependencies)
                {
                    if (!plan.AssetToBundle.ContainsKey(depGuid))
                    {
                        if (!plan.BundleToAssets.ContainsKey(bundleName))
                        {
                            plan.BundleToAssets[bundleName] = new HashSet<string>();
                        }
                        plan.BundleToAssets[bundleName].Add(depGuid);
                        plan.AssetToBundle[depGuid] = bundleName;
                    }
                }
            }
        }
        
        /// <summary>
        /// Build bundle dependencies from asset dependencies
        /// </summary>
        private void BuildBundleDependencies(BuildPlan plan)
        {
            plan.BundleDependencies.Clear();
            
            foreach (var kvp in plan.Dependencies)
            {
                var assetGuid = kvp.Key;
                var dependencies = kvp.Value;
                
                if (!plan.AssetToBundle.TryGetValue(assetGuid, out var bundleName))
                {
                    continue;
                }
                
                foreach (var depGuid in dependencies)
                {
                    if (plan.AssetToBundle.TryGetValue(depGuid, out var depBundleName))
                    {
                        if (depBundleName != bundleName)
                        {
                            if (!plan.BundleDependencies.ContainsKey(bundleName))
                            {
                                plan.BundleDependencies[bundleName] = new HashSet<string>();
                            }
                            plan.BundleDependencies[bundleName].Add(depBundleName);
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
