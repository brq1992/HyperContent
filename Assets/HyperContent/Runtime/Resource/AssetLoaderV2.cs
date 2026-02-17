using System;
using System.Collections.Generic;
using UnityEngine;
using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// IAssetLoader implementation using CatalogViewV2: O(log n) lookup by GUID/Name,
    /// dependency expansion, stringTable on-demand to control GC.
    /// Owner2: runtime resource management.
    /// </summary>
    public class AssetLoaderV2 : IAssetLoader
    {
        private readonly CatalogViewV2 _catalogView;
        private readonly IBundleLoader _bundleLoader;
        private readonly Func<string, BundleInfo> _getBundleInfo;

        /// <summary>
        /// Create loader with catalog view, bundle loader, and bundle path resolver.
        /// getBundleInfo(bundleName) should return BundleInfo with LocalPath set for local bundles.
        /// </summary>
        public AssetLoaderV2(CatalogViewV2 catalogView, IBundleLoader bundleLoader, Func<string, BundleInfo> getBundleInfo)
        {
            _catalogView = catalogView ?? throw new ArgumentNullException(nameof(catalogView));
            _bundleLoader = bundleLoader ?? throw new ArgumentNullException(nameof(bundleLoader));
            _getBundleInfo = getBundleInfo ?? throw new ArgumentNullException(nameof(getBundleInfo));
        }

        /// <summary>
        /// Create loader with a base path for local bundles (e.g. Application.streamingAssetsPath).
        /// Bundle file is assumed at Path.Combine(basePath, bundleName).
        /// </summary>
        public static AssetLoaderV2 CreateWithBasePath(CatalogViewV2 catalogView, IBundleLoader bundleLoader, string bundlesBasePath)
        {
            if (string.IsNullOrEmpty(bundlesBasePath))
                throw new ArgumentException("Bundles base path is required.", nameof(bundlesBasePath));

            Func<string, BundleInfo> getBundleInfo = bundleName =>
            {
                string localPath = System.IO.Path.Combine(bundlesBasePath, bundleName);
                return new BundleInfo
                {
                    Name = bundleName,
                    LocalPath = localPath,
                    Location = ContentLocation.StreamingAssets
                };
            };
            return new AssetLoaderV2(catalogView, bundleLoader, getBundleInfo);
        }

        public AssetHandle<T> Load<T>(string key) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(key))
            {
                var err = new AssetHandle<T>(key);
                err.Fail(ErrorCode.RESOURCE_KEY_INVALID, "Invalid asset key");
                return err;
            }

            var (bundleIndex, assetPathIndex) = _catalogView.FindAsset(key);
            if (bundleIndex < 0 || assetPathIndex < 0)
            {
                var err = new AssetHandle<T>(key);
                err.Fail(ErrorCode.RESOURCE_NOT_FOUND, $"Asset not found in catalog: {key}");
                return err;
            }

            var handle = new AssetHandle<T>(key);
            List<int> loadOrder = _catalogView.GetBundleLoadOrder(bundleIndex);
            handle.SetRequiredBundles(ResolveBundleNames(loadOrder));
            handle.BeginOperation(loadOrder.Count);

            LoadBundlesThenAsset(loadOrder, bundleIndex, assetPathIndex, handle);
            return handle;
        }

        /// <summary>Resolve bundle names on-demand from indices (only when needed for loading).</summary>
        private List<string> ResolveBundleNames(List<int> indices)
        {
            var names = new List<string>(indices.Count);
            for (int i = 0; i < indices.Count; i++)
            {
                string name = _catalogView.GetBundleName(indices[i]);
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }
            return names;
        }

        private void LoadBundlesThenAsset<T>(List<int> bundleIndices, int assetBundleIndex, int assetPathIndex, AssetHandle<T> handle) where T : UnityEngine.Object
        {
            if (bundleIndices == null || bundleIndices.Count == 0)
            {
                LoadAssetFromBundle(assetBundleIndex, assetPathIndex, handle);
                return;
            }

            LoadNextBundle(0, bundleIndices, assetBundleIndex, assetPathIndex, handle);
        }

        private void LoadNextBundle<T>(int index, List<int> bundleIndices, int assetBundleIndex, int assetPathIndex, AssetHandle<T> handle) where T : UnityEngine.Object
        {
            if (index >= bundleIndices.Count)
            {
                LoadAssetFromBundle(assetBundleIndex, assetPathIndex, handle);
                return;
            }

            string bundleName = _catalogView.GetBundleName(bundleIndices[index]);
            if (string.IsNullOrEmpty(bundleName))
            {
                handle.Fail(ErrorCode.BUNDLE_NOT_FOUND, $"Bundle name missing at index {bundleIndices[index]}");
                return;
            }

            if (_bundleLoader.IsLoaded(bundleName))
            {
                handle.CompleteOperation();
                LoadNextBundle(index + 1, bundleIndices, assetBundleIndex, assetPathIndex, handle);
                return;
            }

            BundleInfo info = _getBundleInfo(bundleName);
            if (info == null || string.IsNullOrEmpty(info.LocalPath))
            {
                handle.Fail(ErrorCode.BUNDLE_NOT_FOUND, $"Bundle info or path not found: {bundleName}");
                return;
            }

            string path = info.LocalPath;
            if (!System.IO.Path.IsPathRooted(path) && !string.IsNullOrEmpty(Application.streamingAssetsPath))
                path = System.IO.Path.Combine(Application.streamingAssetsPath, path);

            _bundleLoader.LoadFromFileAsync(bundleName, path, assetBundle =>
            {
                if (assetBundle == null)
                {
                    handle.Fail(ErrorCode.BUNDLE_LOAD_FAILED, $"Failed to load bundle: {bundleName}");
                    return;
                }
                handle.CompleteOperation();
                LoadNextBundle(index + 1, bundleIndices, assetBundleIndex, assetPathIndex, handle);
            });
        }

        private void LoadAssetFromBundle<T>(int bundleIndex, int assetPathIndex, AssetHandle<T> handle) where T : UnityEngine.Object
        {
            string bundleName = _catalogView.GetBundleName(bundleIndex);
            if (string.IsNullOrEmpty(bundleName))
            {
                handle.Fail(ErrorCode.BUNDLE_NOT_FOUND, "Asset bundle name missing");
                return;
            }

            if (!_bundleLoader.TryGetBundle(bundleName, out AssetBundle assetBundle))
            {
                handle.Fail(ErrorCode.BUNDLE_LOAD_FAILED, $"Bundle not loaded: {bundleName}");
                return;
            }

            // On-demand: resolve asset path from stringTable only when loading to control GC.
            string assetPath = _catalogView.GetString(assetPathIndex);
            if (string.IsNullOrEmpty(assetPath))
            {
                handle.Fail(ErrorCode.RESOURCE_NOT_FOUND, "Asset path missing in catalog");
                return;
            }

            T asset = assetBundle.LoadAsset<T>(assetPath);
            if (asset == null)
            {
                // Fallback: try by file name only
                string fileName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
                asset = assetBundle.LoadAsset<T>(fileName);
            }
            if (asset == null)
            {
                handle.Fail(ErrorCode.RESOURCE_LOAD_FAILED, $"Asset not found in bundle: {assetPath}");
                return;
            }

            handle.Complete(asset);
        }
    }
}
