using System;
using System.Collections.Generic;
using UnityEngine;

namespace com.igg.hypercontent.runtime
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
        /// Load AssetBundle from file path with byte offset.
        /// Required for Play Asset Delivery where bundles are packed with a non-zero offset inside the AAB.
        /// </summary>
        /// <param name="bundleName">Bundle name</param>
        /// <param name="filePath">Local file path</param>
        /// <param name="offset">Byte offset within the file</param>
        /// <param name="onComplete">Completion callback with loaded bundle</param>
        void LoadFromFileAsync(string bundleName, string filePath, ulong offset, Action<AssetBundle> onComplete);

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

        /// <summary>
        /// Diagnostics-only snapshot of bundleNames currently held by the loader (i.e. backing
        /// AssetBundle objects still alive). Implementations should append to <paramref name="pOutNames"/>
        /// without allocating internally so this can be called from leak-check inner loops.
        /// Order is unspecified; callers must not mutate the loader during iteration.
        /// </summary>
        void GetLoadedBundleNames(List<string> pOutNames);
    }
}