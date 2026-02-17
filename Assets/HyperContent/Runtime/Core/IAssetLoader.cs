using UnityEngine;

namespace HyperContent
{
    /// <summary>
    /// Unified interface for loading assets by GUID or Name.
    /// Key semantics (see RESOURCE_LOADING_SYSTEM_SPEC.md):
    /// - GUID: 32-character string (no hyphens). Lookup by GUID in catalog, then load from bundle.
    /// - Name: Any other string. Resolved via nameHash to GUID in name alias table, then same as GUID lookup.
    /// </summary>
    public interface IAssetLoader
    {
        /// <summary>
        /// Load asset by key (GUID or Name). Returns immediately with a handle; actual load is asynchronous.
        /// </summary>
        /// <typeparam name="T">Asset type (must be UnityEngine.Object)</typeparam>
        /// <param name="key">Either a 32-char GUID (no hyphens) or a resource Name (resolved via nameHash to GUID internally)</param>
        /// <returns>Handle to track progress and get result when done</returns>
        AssetHandle<T> Load<T>(string key) where T : Object;
    }
}
