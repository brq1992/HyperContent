using System.Collections.Generic;
using com.igg.hypercontent.runtime;
using com.igg.hypercontent.shared;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// ScriptableObject for marking assets with HyperContent metadata
    /// This is the minimal marking approach for Key/Label/Group
    /// </summary>
    [CreateAssetMenu(fileName = "HyperContentAsset", menuName = "HyperContent/Asset Marker", order = 1)]
    public class HyperContentAsset : ScriptableObject
    {
        [Tooltip("Asset key used to load this asset at runtime")]
        public string assetKey;
        
        [Tooltip("Optional labels for filtering and grouping")]
        public List<string> labels = new List<string>();
        
        [Tooltip("Bundle group name. Assets with same group will be packed into same bundle")]
        public string bundleGroup;
        
        [Tooltip("Force this asset to be in its own bundle")]
        public bool forceSeparateBundle;

        /// <summary>
        /// If true, the bundle containing this asset is tagged <see cref="BundleTagFlags.Blocking"/> in the catalog
        /// (batch prefetch before gameplay). OR semantics: any marked asset in the bundle sets the flag.
        /// You may also add label <c>blocking</c> (case-insensitive) instead of using this field.
        /// </summary>
        [Tooltip("Tag this asset's bundle as Blocking in catalog (prefetch before gameplay). Any asset in the bundle can set this.")]
        public bool markBundleBlocking;
        
        /// <summary>
        /// Validate asset key format
        /// </summary>
        public bool ValidateKey(out string error)
        {
            error = null;
            
            if (string.IsNullOrEmpty(assetKey))
            {
                error = "Asset key cannot be empty";
                return false;
            }
            
            if (assetKey.Length > NamingRules.MAX_KEY_LENGTH)
            {
                error = $"Asset key exceeds maximum length of {NamingRules.MAX_KEY_LENGTH}";
                return false;
            }
            
            // Check for invalid characters (bundle name cannot contain path separators, but key can)
            // Key can contain '/' for hierarchical organization
            if (assetKey.Contains("\\"))
            {
                error = "Asset key cannot contain backslash, use forward slash '/' instead";
                return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// Validate bundle group name format
        /// </summary>
        public bool ValidateGroup(out string error)
        {
            error = null;
            
            if (string.IsNullOrEmpty(bundleGroup))
            {
                // Empty group is allowed (will use default grouping)
                return true;
            }
            
            if (bundleGroup.Length > NamingRules.MAX_BUNDLE_NAME_LENGTH)
            {
                error = $"Bundle group name exceeds maximum length of {NamingRules.MAX_BUNDLE_NAME_LENGTH}";
                return false;
            }
            
            // Bundle name cannot contain path separators
            if (bundleGroup.Contains("/") || bundleGroup.Contains("\\"))
            {
                error = "Bundle group name cannot contain path separators";
                return false;
            }
            
            return true;
        }
    }
}
