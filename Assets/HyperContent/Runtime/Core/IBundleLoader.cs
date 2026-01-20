using System;
using UnityEngine;

namespace HyperContent
{
    /// <summary>
    /// Interface for loading bundles into Unity's AssetBundle system
    /// Handles AssetBundle.LoadFromFile, LoadFromMemory, etc.
    /// </summary>
    public interface IBundleLoader
    {
        /// <summary>
        /// Load AssetBundle from file path
        /// </summary>
        /// <param name="bundleName">Bundle name</param>
        /// <param name="filePath">Local file path</param>
        /// <param name="onComplete">Completion callback with loaded bundle</param>
        void LoadFromFileAsync(string bundleName, string filePath, Action<AssetBundle> onComplete);
        
        /// <summary>
        /// Load AssetBundle from memory
        /// </summary>
        /// <param name="bundleName">Bundle name</param>
        /// <param name="data">Bundle data in memory</param>
        /// <param name="onComplete">Completion callback with loaded bundle</param>
        void LoadFromMemoryAsync(string bundleName, byte[] data, Action<AssetBundle> onComplete);
        
        /// <summary>
        /// Unload an AssetBundle
        /// </summary>
        /// <param name="bundleName">Bundle name</param>
        /// <param name="unloadAllLoadedObjects">Whether to unload all loaded objects</param>
        void Unload(string bundleName, bool unloadAllLoadedObjects = false);
        
        /// <summary>
        /// Check if a bundle is currently loaded
        /// </summary>
        /// <param name="bundleName">Bundle name</param>
        /// <returns>True if bundle is loaded</returns>
        bool IsLoaded(string bundleName);
        
        /// <summary>
        /// Get loaded AssetBundle instance
        /// </summary>
        /// <param name="bundleName">Bundle name</param>
        /// <param name="bundle">Output AssetBundle instance</param>
        /// <returns>True if bundle is loaded</returns>
        bool TryGetBundle(string bundleName, out AssetBundle bundle);
    }
}
