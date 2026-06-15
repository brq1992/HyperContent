using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using com.igg.hypercontent.shared;
using com.igg.core;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Load .bundle file from local storage into memory, producing an AssetBundle object.
    /// Pure IO provider — downloads nothing, only loads from disk.
    /// See ARCHITECTURE.md section 6.4.
    /// </summary>
    internal sealed class BundleFileProvider : IContentProvider, IDisposable
    {
        public const string ID = "BundleFileProvider";

        private readonly IBundleLoader _bundleLoader;
        private readonly IBundleStore _bundleStore;
        private readonly string _bundleBasePath;

        // Cache of bundleName -> absolute physical path.
        // The resolution order (cache store -> bundleBasePath -> StreamingAssets -> absolute) is
        // idempotent for the lifetime of a resolved path, so we only need to invalidate when the
        // store writes/deletes a bundle (hot-update). Populated lazily on first Resolve.
        private readonly Dictionary<string, string> _resolvedPathCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal BundleFileProvider(IBundleLoader bundleLoader, IBundleStore bundleStore = null, string bundleBasePath = null)
        {
            _bundleLoader = bundleLoader ?? throw new ArgumentNullException(nameof(bundleLoader));
            _bundleStore = bundleStore;
            _bundleBasePath = bundleBasePath;

            if (_bundleStore != null)
                _bundleStore.BundleChanged += OnBundleStoreChanged;
        }

        public string ProviderId => ID;

        public void Dispose()
        {
            if (_bundleStore != null)
                _bundleStore.BundleChanged -= OnBundleStoreChanged;
            _resolvedPathCache.Clear();
        }

        private void OnBundleStoreChanged(string bundleName)
        {
            if (string.IsNullOrEmpty(bundleName))
                _resolvedPathCache.Clear();
            else
                _resolvedPathCache.Remove(bundleName);
        }

        public void Provide(ProvideHandle handle)
        {
            string internalId = handle.Location.InternalId;
            HCLogger.LogVerbose($"[Load] Step F: BundleFileProvider.Provide bundle={internalId}");

            if (_bundleLoader.IsLoaded(internalId))
            {
                if (_bundleLoader.TryGetBundle(internalId, out var existing))
                {
                    HCLogger.LogVerbose($"[BundleFileProvider] Already loaded [{LogFields.BUNDLE_NAME}={internalId}]");
                    handle.Complete(existing);
                    return;
                }
            }

            IGGProfiler.BeginSample($"HC.Resolve_{internalId}");
            string filePath = ResolveFilePath(internalId);
            IGGProfiler.EndSample($"HC.Resolve_{internalId}");
            if (string.IsNullOrEmpty(filePath))
            {
                HCLogger.LogWarn($"[BundleFileProvider] File not found [{LogFields.BUNDLE_NAME}={internalId}]");
                handle.Fail(new System.IO.FileNotFoundException($"Bundle file not found: {internalId}"));
                return;
            }

            HCLogger.LogVerbose($"[Load] Step G: BundleFileProvider.LoadFromFileAsync bundle={internalId} path={filePath}");
            // 元数据日志：filePath 这里是真实磁盘路径（store / bundleBasePath / 绝对路径），
            // jar URI 旁路在 LogBundleSize 内部静默跳过，[Conditional] 关宏整体擦除。
            HCLogger.LogBundleSize(internalId, filePath, "disk");
            IGGProfiler.BeginSample($"HC.BundleIO_{internalId}");
            _bundleLoader.LoadFromFileAsync(internalId, filePath, bundle =>
            {
                IGGProfiler.EndSample($"HC.BundleIO_{internalId}");
                HCLogger.LogVerbose($"[Load] Step H: BundleFileProvider callback bundle={internalId} success={bundle != null}");
                if (bundle != null)
                {
                    handle.Complete(bundle);
                }
                else
                {
                    handle.Fail(new Exception($"Failed to load bundle from file: {filePath}"));
                }
            });
        }

        public void Release(ProvideHandle handle)
        {
            string internalId = handle.Location.InternalId;
            HCLogger.LogVerbose($"[BundleFileProvider] Release [{LogFields.BUNDLE_NAME}={internalId}]");
            if (_bundleLoader.IsLoaded(internalId))
                _bundleLoader.Unload(internalId, false);
        }

        private string ResolveFilePath(string internalId)
        {
            if (_resolvedPathCache.TryGetValue(internalId, out var cached))
                return cached;

            string resolved = ResolveFilePathUncached(internalId);
            // Only cache positive resolutions; a negative result can later turn positive
            // (e.g. a bundle gets saved into the store via hot-update after the first miss).
            if (!string.IsNullOrEmpty(resolved))
                _resolvedPathCache[internalId] = resolved;
            return resolved;
        }

        private string ResolveFilePathUncached(string internalId)
        {
            if (_bundleStore != null && _bundleStore.Exists(internalId))
            {
                string storePath = _bundleStore.GetLocalPath(internalId);
                HCLogger.LogVerbose($"[BundleFileProvider] Resolved via store [{LogFields.BUNDLE_NAME}={internalId}] path={storePath}");
                return storePath;
            }

            // Catalog-driven keys are extensionless; absolute/jar paths may already include ".bundle".
            bool catalogStyleKey = !string.IsNullOrEmpty(internalId)
                && internalId.IndexOf("://", StringComparison.Ordinal) < 0
                && !Path.IsPathRooted(internalId);

            if (catalogStyleKey)
                NamingRules.RequireCatalogBundleRelativePath(internalId, "BundleFileProvider.ResolveFilePath");

            string fileName = catalogStyleKey
                ? internalId + NamingRules.BUNDLE_FILE_EXTENSION
                : internalId.EndsWith(NamingRules.BUNDLE_FILE_EXTENSION, StringComparison.Ordinal)
                    ? internalId
                    : internalId + NamingRules.BUNDLE_FILE_EXTENSION;

            if (!string.IsNullOrEmpty(_bundleBasePath))
            {
                string basePath = HyperContentPaths.CombinePath(_bundleBasePath, fileName);
                if (HyperContentPaths.FileExistsOrIsStreamingAssets(basePath))
                {
                    HCLogger.LogVerbose($"[BundleFileProvider] Resolved via basePath [{LogFields.BUNDLE_NAME}={internalId}] path={basePath}");
                    return basePath;
                }
            }

            string streamingPath = HyperContentPaths.CombinePath(Application.streamingAssetsPath, fileName);
            if (HyperContentPaths.FileExistsOrIsStreamingAssets(streamingPath))
            {
                HCLogger.LogVerbose($"[BundleFileProvider] Resolved via StreamingAssets [{LogFields.BUNDLE_NAME}={internalId}]");
                return streamingPath;
            }

            if (System.IO.File.Exists(internalId))
            {
                HCLogger.LogVerbose($"[BundleFileProvider] Resolved as absolute path [{LogFields.BUNDLE_NAME}={internalId}]");
                return internalId;
            }

            HCLogger.LogError($"[BundleFileProvider] Resolution failed [{LogFields.BUNDLE_NAME}={internalId}] " +
                           $"expected file name '{fileName}' under basePath='{_bundleBasePath}' or StreamingAssets. " +
                           "Ensure bundles were (re)built with the .bundle extension; legacy extensionless bundles are no longer supported.");
            return null;
        }
    }
}
