using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Centralized path constants for HyperContent.
    /// Path definitions: see CONVENTIONS.md §3 (canonical source).
    ///
    /// Build output:
    ///   Bundles  → Assets/StreamingAssets/{Platform}/Bundles/
    ///   Catalog  → HyperContentBuild/{Platform}/hc/
    /// Runtime (device):
    ///   Bundles  → streamingAssetsPath/{Platform}/Bundles/
    ///   Catalog  → streamingAssetsPath/hc/
    /// Cache:
    ///   Bundles  → persistentDataPath/HyperContent/Bundles/
    ///   Catalog  → persistentDataPath/HyperContent/hc/
    /// </summary>
    public static class HyperContentPaths
    {
        public const string STREAMING_ASSETS_SUBFOLDER = "hc";
        public const string BUNDLES_SUBFOLDER = "Bundles";
        public const string SETTINGS_FILENAME = "settings.json";
        /// <summary>Catalog filename for package/runtime. Must match LOCAL_CATALOG_NAME + ".bin".</summary>
        public const string LOCAL_CATALOG_FILENAME = "HyperCatalog.bin";
        /// <summary>Catalog identity name (no extension). Used for catalogNameIndex in CatalogSchema.</summary>
        public const string LOCAL_CATALOG_NAME = "HyperCatalog";
        public const string CACHE_SUBFOLDER = "HyperContent";
        public const string BUILD_OUTPUT_ROOT = "HyperContentBuild";

        // ── Catalog / Settings paths ─────────────────────────────────────

        /// <summary>
        /// Catalog + settings base path.
        /// Runtime: streamingAssetsPath/hc/
        /// Editor (UseExistingAssetBundle): HyperContentBuild/{Platform}/hc/
        /// </summary>
        public static string CatalogBasePath
        {
            get
            {
#if UNITY_EDITOR
                return BuildCatalogPath;
#else
                return System.IO.Path.Combine(Application.streamingAssetsPath, STREAMING_ASSETS_SUBFOLDER);
#endif
            }
        }

        /// <summary>CatalogBasePath + settings.json</summary>
        public static string SettingsPath =>
            System.IO.Path.Combine(CatalogBasePath, SETTINGS_FILENAME);

        // ── Bundle paths ─────────────────────────────────────────────────

        /// <summary>
        /// Package bundle base path.
        /// Runtime: streamingAssetsPath/{Platform}/Bundles/
        /// Editor: Assets/StreamingAssets/{Platform}/Bundles/
        /// </summary>
        public static string BundleBasePath
        {
            get
            {
#if UNITY_EDITOR
                return BuildBundlePath;
#else
                return System.IO.Path.Combine(
                    Application.streamingAssetsPath, GetRuntimePlatformFolder(), BUNDLES_SUBFOLDER);
#endif
            }
        }

        // ── Cache paths ──────────────────────────────────────────────────

        /// <summary>persistentDataPath/HyperContent/</summary>
        public static string CachePath =>
            System.IO.Path.Combine(Application.persistentDataPath, CACHE_SUBFOLDER);

        /// <summary>persistentDataPath/HyperContent/Bundles/</summary>
        public static string CacheBundlePath =>
            System.IO.Path.Combine(CachePath, BUNDLES_SUBFOLDER);

        /// <summary>persistentDataPath/HyperContent/hc/</summary>
        public static string CacheCatalogPath =>
            System.IO.Path.Combine(CachePath, STREAMING_ASSETS_SUBFOLDER);

        // ── Editor-only paths ────────────────────────────────────────────

#if UNITY_EDITOR
        /// <summary>HyperContentBuild/{Platform}/hc/</summary>
        public static string BuildCatalogPath =>
            System.IO.Path.Combine(BUILD_OUTPUT_ROOT, GetEditorPlatformFolder(), STREAMING_ASSETS_SUBFOLDER);

        /// <summary>HyperContentBuild/{Platform}/ (root, for PlayerBuildProcessor)</summary>
        public static string BuildPath =>
            System.IO.Path.Combine(BUILD_OUTPUT_ROOT, GetEditorPlatformFolder());

        /// <summary>Assets/StreamingAssets/{Platform}/Bundles/</summary>
        public static string BuildBundlePath =>
            System.IO.Path.Combine("Assets", "StreamingAssets", GetEditorPlatformFolder(), BUNDLES_SUBFOLDER);

        private static string GetEditorPlatformFolder()
        {
            var target = UnityEditor.EditorUserBuildSettings.activeBuildTarget;
            switch (target)
            {
                case UnityEditor.BuildTarget.Android: return "Android";
                case UnityEditor.BuildTarget.iOS: return "iOS";
                case UnityEditor.BuildTarget.StandaloneWindows:
                case UnityEditor.BuildTarget.StandaloneWindows64: return "Windows";
                case UnityEditor.BuildTarget.StandaloneOSX: return "macOS";
                default: return target.ToString();
            }
        }
#endif

        private static string GetRuntimePlatformFolder()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.Android: return "Android";
                case RuntimePlatform.IPhonePlayer: return "iOS";
                case RuntimePlatform.WindowsPlayer: return "Windows";
                case RuntimePlatform.OSXPlayer: return "macOS";
                default: return Application.platform.ToString();
            }
        }

        /// <summary>
        /// First URL path segment for remote bundle downloads after <see cref="RuntimeSettings.remoteBundleBaseUrl"/>.
        /// <see cref="HttpBundleTransport"/> resolves relative catalog paths as <c>{base}{segment}/{pathFromCatalog}</c>
        /// (e.g. <c>.../bundles/Android/mybundle.bundle</c>). Names match local streaming layout
        /// (<c>streamingAssets/{segment}/Bundles/</c>). In Editor, uses <c>EditorUserBuildSettings.activeBuildTarget</c>.
        /// </summary>
        public static string RemoteBundlePlatformSegment
        {
            get
            {
#if UNITY_EDITOR
                return GetEditorPlatformFolder();
#else
                return GetRuntimePlatformFolder();
#endif
            }
        }

        /// <summary>
        /// Composes an absolute CDN URL for HTTP: <c>{base}/{platform}/{relative}</c>.
        /// Same rule for remote bundles, catalog <c>.bin</c>, and catalog <c>.hash</c> (single CDN root in
        /// <see cref="RuntimeSettings.remoteBundleBaseUrl"/>; JSON stores only <paramref name="pRelativePath"/>).
        /// If <paramref name="pRelativePath"/> already starts with <c>http://</c> or <c>https://</c>, returns it unchanged.
        /// Returns null if <paramref name="pRelativePath"/> is null/empty, or if <paramref name="pBaseUrl"/> is null/empty
        /// while <paramref name="pRelativePath"/> is not an absolute URL.
        /// </summary>
        public static string CombineRemoteCdnRequestUrl(string pBaseUrl, string pRelativePath)
        {
            if (string.IsNullOrEmpty(pRelativePath))
                return null;
            if (pRelativePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                pRelativePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return pRelativePath;

            string baseUrl = (pBaseUrl ?? "").TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl))
                return null;

            baseUrl += "/";

            string relative = pRelativePath.TrimStart('/');
            string platformSegment = RemoteBundlePlatformSegment?.Trim('/') ?? "";
            if (string.IsNullOrEmpty(platformSegment))
                return baseUrl + relative;

            if (string.IsNullOrEmpty(relative))
                return baseUrl + platformSegment;

            return baseUrl + platformSegment + "/" + relative;
        }

        // ── Android StreamingAssets Support ────────────────────────────────
        // Mirrors Addressables' ResourceManagerConfig.ShouldPathUseWebRequest

        /// <summary>
        /// Check if path should use UnityWebRequest instead of File.ReadAllText.
        /// Android StreamingAssets returns a "jar:file://..." URL that requires WebRequest.
        /// </summary>
        public static bool ShouldUseWebRequest(string pPath)
        {
            if (string.IsNullOrEmpty(pPath))
                return false;

            // On Android, persistentDataPath files can still use File.Exists/ReadAllText
            if (Application.platform == RuntimePlatform.Android)
            {
                if (System.IO.File.Exists(pPath))
                    return false;
            }

            // If path contains "://", it's a URL format (http://, jar:file://, etc.)
            return pPath.Contains("://");
        }

        /// <summary>
        /// Load text file, automatically handling Android StreamingAssets.
        /// Synchronous blocking call.
        /// </summary>
        public static string LoadText(string pPath)
        {
            if (ShouldUseWebRequest(pPath))
            {
                return LoadTextViaWebRequest(pPath);
            }
            else if (System.IO.File.Exists(pPath))
            {
                return System.IO.File.ReadAllText(pPath);
            }
            return null;
        }

        private static string LoadTextViaWebRequest(string pPath)
        {
            using (var request = UnityWebRequest.Get(pPath))
            {
                request.timeout = 10;
                var asyncOp = request.SendWebRequest();
                while (!asyncOp.isDone) { }

                if (request.result == UnityWebRequest.Result.Success)
                    return request.downloadHandler.text;
                return null;
            }
        }

        /// <summary>
        /// Load raw bytes from file, automatically handling Android StreamingAssets (jar:file://).
        /// Synchronous blocking call. Mirrors <see cref="LoadText(string)"/> for binary catalog reads.
        /// Returns null if the file is not found or read fails.
        /// </summary>
        public static byte[] LoadBytes(string pPath)
        {
            if (ShouldUseWebRequest(pPath))
            {
                return LoadBytesViaWebRequest(pPath);
            }
            else if (System.IO.File.Exists(pPath))
            {
                return System.IO.File.ReadAllBytes(pPath);
            }
            return null;
        }

        private static byte[] LoadBytesViaWebRequest(string pPath)
        {
            using (var request = UnityWebRequest.Get(pPath))
            {
                request.timeout = 10;
                var asyncOp = request.SendWebRequest();
                while (!asyncOp.isDone) { }

                if (request.result == UnityWebRequest.Result.Success)
                    return request.downloadHandler.data;
                return null;
            }
        }

        /// <summary>
        /// Load raw bytes from <paramref name="pSource"/>, falling back to
        /// <c>streamingAssetsPath/{pSource}</c> when the primary read yields nothing.
        /// Centralizes the duplicated catalog load+fallback used by both binary and JSON paths.
        /// </summary>
        public static byte[] LoadBytesWithStreamingFallback(string pSource)
        {
            byte[] bytes = LoadBytes(pSource);
            if (bytes == null || bytes.Length == 0)
                bytes = LoadBytes(System.IO.Path.Combine(Application.streamingAssetsPath, pSource));
            return bytes;
        }

        /// <summary>
        /// Load text from <paramref name="pSource"/>, falling back to
        /// <c>streamingAssetsPath/{pSource}</c> when the primary read yields nothing.
        /// Centralizes the duplicated catalog load+fallback used by both binary and JSON paths.
        /// </summary>
        public static string LoadTextWithStreamingFallback(string pSource)
        {
            string text = LoadText(pSource);
            if (string.IsNullOrEmpty(text))
                text = LoadText(System.IO.Path.Combine(Application.streamingAssetsPath, pSource));
            return text;
        }

        /// <summary>
        /// Callback-based async load. Non-blocking; mirrors Addressables' pure-callback pattern
        /// (no internal <c>async/await</c> state machine). On success <paramref name="onComplete"/>
        /// is invoked with the text and a null exception. On failure (HTTP error / exception) it is
        /// invoked with <c>(null, ex?)</c>. On cancellation it is invoked with
        /// <c>(null, OperationCanceledException)</c>.
        /// </summary>
        public static void LoadText(
            string pPath, int pTimeout, CancellationToken pToken,
            Action<string, Exception> onComplete)
        {
            if (onComplete == null) return;

            if (pToken.IsCancellationRequested)
            {
                onComplete(null, new OperationCanceledException(pToken));
                return;
            }

            if (ShouldUseWebRequest(pPath))
            {
                LoadTextViaWebRequest(pPath, pTimeout, pToken, onComplete);
                return;
            }

            if (System.IO.File.Exists(pPath))
            {
                try
                {
                    var text = System.IO.File.ReadAllText(pPath);
                    onComplete(text, null);
                }
                catch (Exception e) { onComplete(null, e); }
                return;
            }

            onComplete(null, null);
        }

        private static void LoadTextViaWebRequest(
            string pPath, int pTimeout, CancellationToken pToken,
            Action<string, Exception> onComplete)
        {
            UnityWebRequest request;
            try
            {
                request = UnityWebRequest.Get(pPath);
                request.timeout = pTimeout;
            }
            catch (Exception e) { onComplete(null, e); return; }

            CancellationTokenRegistration ctr = default;
            if (pToken.CanBeCanceled)
                ctr = pToken.Register(() => { try { request.Abort(); } catch { /* ignore */ } });

            var asyncOp = request.SendWebRequest();
            asyncOp.completed += _ =>
            {
                try
                {
                    if (pToken.IsCancellationRequested)
                    {
                        onComplete(null, new OperationCanceledException(pToken));
                        return;
                    }

                    if (request.result == UnityWebRequest.Result.Success)
                        onComplete(request.downloadHandler.text, null);
                    else
                        onComplete(null, null);
                }
                catch (Exception e) { onComplete(null, e); }
                finally
                {
                    ctr.Dispose();
                    request.Dispose();
                }
            };
        }

        /// <summary>
        /// Async (<see cref="Task{T}"/>) bridge over <see cref="LoadText"/> for call sites
        /// that need <c>await</c> semantics. Returns <c>null</c> on failure (matches previous
        /// contract); throws <see cref="OperationCanceledException"/> on cancellation.
        /// </summary>
        public static Task<string> LoadTextAsync(
            string pPath, int pTimeout = 10, CancellationToken pToken = default)
        {
            var tcs = new TaskCompletionSource<string>();
            LoadText(pPath, pTimeout, pToken, (text, ex) =>
            {
                if (ex != null) tcs.TrySetException(ex);
                else tcs.TrySetResult(text);
            });
            return tcs.Task;
        }

        // ── Android StreamingAssets Bundle Loading Support ─────────────────
        // For AssetBundle.LoadFromFile, File.Exists() doesn't work on Android StreamingAssets,
        // but LoadFromFile itself handles it internally. We need to skip the File.Exists check.

        /// <summary>
        /// Check if path is in Android StreamingAssets (where File.Exists doesn't work).
        /// </summary>
        public static bool IsAndroidStreamingAssetsPath(string pPath)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (string.IsNullOrEmpty(pPath))
                return false;
            // Android StreamingAssets path starts with "jar:" or matches streamingAssetsPath
            return pPath.StartsWith("jar:", StringComparison.Ordinal) ||
                   pPath.StartsWith(Application.streamingAssetsPath, StringComparison.Ordinal);
#else
            return false;
#endif
        }

        /// <summary>
        /// Check if file exists, handling Android StreamingAssets special case.
        /// For Android StreamingAssets, returns true without actual check 
        /// (let AssetBundle.LoadFromFile verify internally).
        /// </summary>
        public static bool FileExistsOrIsStreamingAssets(string pPath)
        {
            if (IsAndroidStreamingAssetsPath(pPath))
                return true;  // Assume exists, let AssetBundle.LoadFromFile verify
            return System.IO.File.Exists(pPath);
        }

        // ── URL-safe Path Operations ─────────────────────────────────────────
        // Path.GetDirectoryName() breaks jar:file:// URLs by handling :// as separators.
        // Use string operations instead.

        /// <summary>
        /// Get directory path from a file path, handling URL formats like jar:file://
        /// Path.GetDirectoryName() breaks URLs, so we use string operations.
        /// </summary>
        public static string GetDirectoryPath(string pFilePath)
        {
            if (string.IsNullOrEmpty(pFilePath))
                return string.Empty;

            int lastSlash = pFilePath.LastIndexOf('/');
            int lastBackslash = pFilePath.LastIndexOf('\\');
            int lastSeparator = Math.Max(lastSlash, lastBackslash);

            if (lastSeparator <= 0)
                return string.Empty;

            return pFilePath.Substring(0, lastSeparator);
        }

        /// <summary>
        /// Combine a base path with a relative path, handling URL formats.
        /// </summary>
        public static string CombinePath(string pBasePath, string pRelativePath)
        {
            if (string.IsNullOrEmpty(pBasePath))
                return pRelativePath;
            if (string.IsNullOrEmpty(pRelativePath))
                return pBasePath;

            char lastChar = pBasePath[pBasePath.Length - 1];
            if (lastChar == '/' || lastChar == '\\')
                return pBasePath + pRelativePath;

            return pBasePath + "/" + pRelativePath;
        }
    }
}
