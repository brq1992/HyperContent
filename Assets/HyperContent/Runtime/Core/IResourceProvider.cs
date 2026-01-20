using System;
using UnityEngine;

namespace HyperContent
{
    /// <summary>
    /// Interface for resource loading (the main public API)
    /// Provides LoadAsset<T> interface similar to Addressables
    /// </summary>
    public interface IResourceProvider
    {
        /// <summary>
        /// Initialize the resource provider
        /// </summary>
        /// <param name="catalog">Content catalog instance</param>
        /// <param name="bundleStore">Bundle store instance</param>
        /// <param name="bundleTransport">Bundle transport instance</param>
        /// <param name="bundleLoader">Bundle loader instance</param>
        /// <returns>True if initialization succeeded</returns>
        bool Initialize(IContentCatalog catalog, IBundleStore bundleStore, IBundleTransport bundleTransport, IBundleLoader bundleLoader);
        
        /// <summary>
        /// Load asset by key (synchronous)
        /// </summary>
        /// <typeparam name="T">Asset type</typeparam>
        /// <param name="key">Asset key</param>
        /// <returns>Loaded asset, or null if failed</returns>
        T LoadAsset<T>(string key) where T : UnityEngine.Object;
        
        /// <summary>
        /// Load asset by key (asynchronous)
        /// </summary>
        /// <typeparam name="T">Asset type</typeparam>
        /// <param name="key">Asset key</param>
        /// <param name="onComplete">Completion callback</param>
        void LoadAssetAsync<T>(string key, Action<T> onComplete) where T : UnityEngine.Object;
        
        /// <summary>
        /// Load asset by key (asynchronous, returns Handle)
        /// </summary>
        /// <typeparam name="T">Asset type</typeparam>
        /// <param name="key">Asset key</param>
        /// <returns>Handle for tracking the operation</returns>
        AssetHandle<T> LoadAssetAsync<T>(string key) where T : UnityEngine.Object;
        
        /// <summary>
        /// Instantiate asset as GameObject (asynchronous, returns Handle)
        /// </summary>
        /// <param name="key">Asset key</param>
        /// <param name="parent">Parent transform (optional)</param>
        /// <param name="instantiateInWorldSpace">Whether to instantiate in world space</param>
        /// <returns>Handle for tracking the operation</returns>
        InstanceHandle InstantiateAsync(string key, Transform parent = null, bool instantiateInWorldSpace = false);
        
        /// <summary>
        /// Release asset and decrement reference count
        /// </summary>
        /// <param name="key">Asset key</param>
        void ReleaseAsset(string key);
        
        /// <summary>
        /// Release an instantiated GameObject instance
        /// </summary>
        /// <param name="instance">GameObject instance to release</param>
        void ReleaseInstance(GameObject instance);
        
        /// <summary>
        /// Check if asset is loaded
        /// </summary>
        /// <param name="key">Asset key</param>
        /// <returns>True if asset is loaded</returns>
        bool IsAssetLoaded(string key);
        
        /// <summary>
        /// Release all resources
        /// </summary>
        void ReleaseAll();
    }
}
