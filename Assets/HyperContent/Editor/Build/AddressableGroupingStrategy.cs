using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Grouping strategy that uses Addressable groups to determine bundle assignments
    /// This strategy reads grouping data from Addressable Groups and applies it to collected assets
    /// </summary>
    public class AddressableGroupingStrategy : IBundleGroupingStrategy
    {
        public string StrategyName => "Addressable Groups";
        
        public bool AssignBundles(BuildContext context)
        {
            try
            {
                // Get Addressable settings
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    context.Errors.Add(new BuildError(
                        "AddressableAssetSettings not found. Please ensure Addressables package is installed and initialized."
                    ));
                    return false;
                }
                
                // Clear existing bundle assignments
                context.AssetToBundle.Clear();
                context.BundleToAssets.Clear();
                
                // Build mapping from asset GUID to Addressable group name
                // This is the "grouping data" we get from Addressable
                var guidToGroupName = BuildGuidToGroupNameMap(settings);
                
                // Apply grouping data to collected assets
                var groupToAssets = new Dictionary<string, HashSet<string>>();
                
                foreach (var kvp in context.AssetMarkers)
                {
                    var assetGuid = kvp.Key;
                    
                    // Get group name from Addressable grouping data
                    if (!guidToGroupName.TryGetValue(assetGuid, out var groupName))
                    {
                        // Asset is not in Addressable, use fallback: marker's bundleGroup or asset key
                        context.Warnings.Add(new BuildWarning(
                            $"Asset {context.GuidToPath[assetGuid]} is not found in Addressable Groups. Using fallback grouping.",
                            context.GuidToPath[assetGuid]
                        ));
                        
                        // Fallback to marker-based grouping
                        var marker = kvp.Value;
                        if (marker.forceSeparateBundle)
                        {
                            groupName = marker.assetKey;
                        }
                        else if (!string.IsNullOrEmpty(marker.bundleGroup))
                        {
                            groupName = marker.bundleGroup;
                        }
                        else
                        {
                            groupName = marker.assetKey;
                        }
                    }
                    
                    // Sanitize group name for bundle name
                    var bundleName = SanitizeBundleName(groupName);
                    
                    // Add to bundle mapping
                    if (!groupToAssets.ContainsKey(bundleName))
                    {
                        groupToAssets[bundleName] = new HashSet<string>();
                    }
                    groupToAssets[bundleName].Add(assetGuid);
                    context.AssetToBundle[assetGuid] = bundleName;
                }
                
                // Copy to context
                foreach (var kvp in groupToAssets)
                {
                    context.BundleToAssets[kvp.Key] = kvp.Value;
                }
                
                // Handle dependencies if enabled
                if (context.Config.includeDependencies)
                {
                    AddDependenciesToBundles(context);
                }
                
                return true;
            }
            catch (Exception e)
            {
                context.Errors.Add(new BuildError(
                    $"Addressable grouping strategy failed: {e.Message}\n{e.StackTrace}"
                ));
                return false;
            }
        }
        
        public List<string> Validate(BuildContext context)
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
        /// Build a mapping from asset GUID to Addressable group name
        /// This extracts the "grouping data" from Addressable Groups
        /// </summary>
        private Dictionary<string, string> BuildGuidToGroupNameMap(AddressableAssetSettings settings)
        {
            var guidToGroupName = new Dictionary<string, string>();
            
            // Get all Addressable entries
            var allEntries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(allEntries, false);
            
            // Extract grouping data: GUID -> Group Name
            foreach (var entry in allEntries)
            {
                if (!string.IsNullOrEmpty(entry.guid))
                {
                    var groupName = entry.parentGroup != null ? entry.parentGroup.Name : "Default";
                    guidToGroupName[entry.guid] = groupName;
                }
            }
            
            return guidToGroupName;
        }
        
        /// <summary>
        /// Add dependencies to bundles if includeDependencies is enabled
        /// </summary>
        private void AddDependenciesToBundles(BuildContext context)
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
                
                // Add dependencies to the same bundle if they're not already in another bundle
                foreach (var depGuid in dependencies)
                {
                    if (!context.AssetToBundle.ContainsKey(depGuid))
                    {
                        if (!context.BundleToAssets.ContainsKey(bundleName))
                        {
                            context.BundleToAssets[bundleName] = new HashSet<string>();
                        }
                        context.BundleToAssets[bundleName].Add(depGuid);
                        context.AssetToBundle[depGuid] = bundleName;
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
