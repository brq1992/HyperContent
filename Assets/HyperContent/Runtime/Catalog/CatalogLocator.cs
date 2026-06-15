using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Indicates where the resolved catalog came from.
    /// </summary>
    public enum CatalogSource
    {
        /// <summary>Package catalog from StreamingAssets/hc/HyperCatalog.bin</summary>
        Package = 0,
        /// <summary>Previously cached catalog from persistentDataPath</summary>
        Cached = 1,
        /// <summary>Freshly downloaded from remote in this session</summary>
        Downloaded = 2
    }

    /// <summary>
    /// Result of catalog resolution.
    /// </summary>
    public struct CatalogResolution
    {
        /// <summary>Absolute path to the resolved catalog file</summary>
        public string catalogPath;

        /// <summary>Where the catalog came from (for diagnostics/logging)</summary>
        public CatalogSource source;

        /// <summary>Parsed RuntimeSettings (may be needed by other systems)</summary>
        public RuntimeSettings settings;

        /// <summary>True if resolution succeeded</summary>
        public bool success;

        /// <summary>Error message if resolution failed</summary>
        public string error;

        public static CatalogResolution Success(string pPath, CatalogSource pSource, RuntimeSettings pSettings)
        {
            return new CatalogResolution
            {
                catalogPath = pPath,
                source = pSource,
                settings = pSettings,
                success = true,
                error = null
            };
        }

        public static CatalogResolution Failure(string pError)
        {
            return new CatalogResolution
            {
                catalogPath = null,
                source = CatalogSource.Package,
                settings = null,
                success = false,
                error = pError
            };
        }
    }

    /// <summary>
    /// Outcome of <see cref="CatalogLocator.CheckAndDownloadCatalogUpdateAsync"/> (internal locator; disk facade wraps this).
    /// Owner: Owner3.
    /// </summary>
    public enum CatalogLocatorUpdateStatus
    {
        /// <summary>No remote catalog configured in settings — no network work.</summary>
        SkippedNoRemote = 0,

        /// <summary>Remote hash matches disk cache and cached catalog file is present.</summary>
        Unchanged,

        /// <summary>New catalog and hash written under cache root.</summary>
        Downloaded,

        /// <summary>Network, HTTP, or disk failure.</summary>
        Failed
    }

    /// <summary>
    /// Structured result for remote catalog hash check + optional download (disk only; does not reload memory catalog).
    /// </summary>
    public struct CatalogLocatorUpdateOutcome
    {
        public CatalogLocatorUpdateStatus status;
        public string error;
    }

    /// <summary>
    /// Catalog discovery: load settings.json; <see cref="ResolveLocalCatalogAsync"/> (disk only, no HTTP);
    /// <see cref="CheckAndDownloadCatalogUpdateAsync"/> for optional remote hash/catalog to disk (async, non-blocking).
    /// Owner: Owner3.
    ///
    /// INTERNAL STRUCTURE:
    /// All network/disk work lives in callback-form methods (<c>ResolveLocalCatalog</c>, <c>LoadSettings</c>,
    /// <c>CheckAndDownloadCatalogUpdate</c>) — no internal <c>async/await</c> state machines. The
    /// <c>*Async</c> suffix overloads are thin <see cref="TaskCompletionSource{T}"/> bridges for
    /// callers that want <c>await</c> ergonomics. This mirrors Addressables' pure-callback
    /// InitializationOperation chain.
    /// </summary>
    public static class CatalogLocator
    {
        // ── ResolveLocalCatalog ──────────────────────────────────────────────

        /// <summary>
        /// Callback form of <see cref="ResolveLocalCatalogAsync"/>.
        /// <paramref name="onComplete"/> is always invoked (success or failure). On cancellation it is
        /// invoked with a failed <see cref="CatalogResolution"/> carrying a cancellation message.
        /// </summary>
        public static void ResolveLocalCatalog(
            CancellationToken pToken,
            Action<CatalogResolution> onComplete)
        {
            if (onComplete == null) return;

            string settingsPath = HyperContentPaths.SettingsPath;

            LoadSettings(settingsPath, pToken, (settings, loadEx) =>
            {
                if (loadEx is OperationCanceledException)
                {
                    onComplete(CatalogResolution.Failure("settings load cancelled"));
                    return;
                }

                if (settings == null)
                {
                    onComplete(CatalogResolution.Failure($"Failed to load settings.json from: {settingsPath}"));
                    return;
                }

                HCLogger.LogInfo($"[CatalogLocator] Local resolve — buildVersion={settings.buildVersion}, " +
                    $"hasRemoteCatalog={settings.HasRemoteCatalog}");

                string packageCatalogPath = Path.Combine(
                    HyperContentPaths.CatalogBasePath,
                    settings.localCatalogPath ?? string.Empty);

                string catalogCacheRoot = HyperContentPaths.CacheCatalogPath;
                bool hasCachedFileName = !string.IsNullOrEmpty(settings.cachedCatalogPath);
                string cachedCatalogPath = hasCachedFileName
                    ? Path.Combine(catalogCacheRoot, settings.cachedCatalogPath)
                    : null;

                if (hasCachedFileName && File.Exists(cachedCatalogPath))
                {
                    HCLogger.LogInfo($"[CatalogLocator] Using cached catalog (local resolve): {cachedCatalogPath}");
                    onComplete(CatalogResolution.Success(cachedCatalogPath, CatalogSource.Cached, settings));
                    return;
                }

                HCLogger.LogInfo($"[CatalogLocator] Using package catalog (local resolve): {packageCatalogPath}");
                onComplete(CatalogResolution.Success(packageCatalogPath, CatalogSource.Package, settings));
            });
        }

        /// <summary>
        /// Resolve which catalog file to load **from disk only** (no HTTP).
        /// If a cached catalog file exists under the cache root, use <see cref="CatalogSource.Cached"/>; otherwise package
        /// catalog at <see cref="HyperContentPaths.CatalogBasePath"/> + <see cref="RuntimeSettings.localCatalogPath"/>.
        /// </summary>
        public static Task<CatalogResolution> ResolveLocalCatalogAsync(CancellationToken pToken = default)
        {
            var tcs = new TaskCompletionSource<CatalogResolution>();
            ResolveLocalCatalog(pToken, r => tcs.TrySetResult(r));
            return tcs.Task;
        }

        /// <summary>
        /// Legacy entry: same as <see cref="ResolveLocalCatalogAsync"/> (init must not perform remote catalog I/O here).
        /// </summary>
        public static Task<CatalogResolution> ResolveAsync(CancellationToken pToken = default)
        {
            return ResolveLocalCatalogAsync(pToken);
        }

        // ── CheckAndDownloadCatalogUpdate ────────────────────────────────────

        /// <summary>
        /// Callback form of <see cref="CheckAndDownloadCatalogUpdateAsync"/>. No internal
        /// <c>async/await</c>; uses <see cref="UnityWebRequestAsyncOperation.completed"/> events
        /// to sequence hash fetch → compare → catalog download → atomic disk write.
        /// </summary>
        public static void CheckAndDownloadCatalogUpdate(
            RuntimeSettings pSettings,
            string pCdnBaseUrl,
            CancellationToken pToken,
            Action<CatalogLocatorUpdateOutcome> onComplete)
        {
            if (onComplete == null) return;

            if (pSettings == null)
            {
                onComplete(new CatalogLocatorUpdateOutcome
                {
                    status = CatalogLocatorUpdateStatus.Failed,
                    error = "settings is null"
                });
                return;
            }

            if (!pSettings.HasRemoteCatalog)
            {
                HCLogger.LogVerbose("[CatalogLocator] CheckAndDownloadCatalogUpdate skipped — no remote catalog in settings");
                onComplete(new CatalogLocatorUpdateOutcome { status = CatalogLocatorUpdateStatus.SkippedNoRemote });
                return;
            }

            string catalogCachePath = HyperContentPaths.CacheCatalogPath;
            string cachedCatalogPath = Path.Combine(catalogCachePath, pSettings.cachedCatalogPath ?? string.Empty);
            string cachedHashPath = Path.Combine(catalogCachePath, pSettings.cachedCatalogHashPath ?? string.Empty);

            string hashUrl = HyperContentPaths.CombineRemoteCdnRequestUrl(
                pCdnBaseUrl, pSettings.remoteCatalogHashRelativePath);
            if (string.IsNullOrEmpty(hashUrl))
            {
                string msg = "Remote catalog hash URL could not be built (missing CDN base or relative hash path)";
                HCLogger.LogWarn($"[CatalogLocator] {msg}");
                onComplete(new CatalogLocatorUpdateOutcome { status = CatalogLocatorUpdateStatus.Failed, error = msg });
                return;
            }

            DownloadText(hashUrl, pSettings.catalogRequestTimeout, pToken, (remoteHash, hashEx) =>
            {
                if (hashEx is OperationCanceledException)
                {
                    onComplete(new CatalogLocatorUpdateOutcome
                    {
                        status = CatalogLocatorUpdateStatus.Failed,
                        error = "cancelled"
                    });
                    return;
                }

                if (string.IsNullOrEmpty(remoteHash))
                {
                    string msg = hashEx != null
                        ? $"Failed to download remote catalog hash: {hashEx.Message}"
                        : $"Failed to download remote catalog hash (empty response) | url={hashUrl}";
                    HCLogger.LogError($"[CatalogLocator] {msg}");
                    onComplete(new CatalogLocatorUpdateOutcome
                    {
                        status = CatalogLocatorUpdateStatus.Failed,
                        error = msg
                    });
                    return;
                }

                remoteHash = remoteHash.Trim();
                HCLogger.LogInfo($"[CatalogLocator] Remote catalog hash: {remoteHash}");

                string cachedHash = ReadCachedHash(cachedHashPath);
                if (string.Equals(remoteHash, cachedHash, StringComparison.Ordinal)
                    && File.Exists(cachedCatalogPath))
                {
                    HCLogger.LogInfo("[CatalogLocator] Catalog cache up to date (hash match)");
                    onComplete(new CatalogLocatorUpdateOutcome { status = CatalogLocatorUpdateStatus.Unchanged });
                    return;
                }

                HCLogger.LogInfo($"[CatalogLocator] Catalog update required (remote={remoteHash}, cached={cachedHash ?? "null"})");

                string catalogUrl = HyperContentPaths.CombineRemoteCdnRequestUrl(
                    pCdnBaseUrl, pSettings.remoteCatalogRelativePath);
                if (string.IsNullOrEmpty(catalogUrl))
                {
                    string msg = "Remote catalog URL could not be built (missing CDN base or relative catalog path)";
                    HCLogger.LogWarn($"[CatalogLocator] {msg}");
                    onComplete(new CatalogLocatorUpdateOutcome { status = CatalogLocatorUpdateStatus.Failed, error = msg });
                    return;
                }

                DownloadAndCacheCatalog(
                    pSettings, cachedCatalogPath, cachedHashPath, remoteHash, catalogUrl, pToken,
                    (wrote, writeEx) =>
                    {
                        if (writeEx is OperationCanceledException)
                        {
                            onComplete(new CatalogLocatorUpdateOutcome
                            {
                                status = CatalogLocatorUpdateStatus.Failed,
                                error = "cancelled"
                            });
                            return;
                        }

                        if (wrote)
                        {
                            onComplete(new CatalogLocatorUpdateOutcome { status = CatalogLocatorUpdateStatus.Downloaded });
                        }
                        else
                        {
                            onComplete(new CatalogLocatorUpdateOutcome
                            {
                                status = CatalogLocatorUpdateStatus.Failed,
                                error = "Catalog download or disk write failed"
                            });
                        }
                    });
            });
        }

        /// <summary>
        /// Internal locator: fetch remote hash (with <see cref="HyperContentPaths.CombineRemoteCdnRequestUrl"/>), compare to disk,
        /// download catalog when mismatch, write hash + catalog atomically. **No** in-memory catalog reload.
        /// Call after <see cref="ResolveLocalCatalogAsync"/> when the app is ready to touch the network.
        /// Uses direct <see cref="UnityWebRequest"/> here — catalog/hash HTTP is intentionally **not** routed through
        /// <c>IBundleDownloadQueue</c> / <c>HttpBundleTransport</c> (bundle-only pipeline).
        /// </summary>
        /// <param name="pSettings">Parsed runtime settings (must not be null).</param>
        /// <param name="pCdnBaseUrl">Module CDN base (may differ from JSON if set at runtime).</param>
        public static Task<CatalogLocatorUpdateOutcome> CheckAndDownloadCatalogUpdateAsync(
            RuntimeSettings pSettings,
            string pCdnBaseUrl,
            CancellationToken pToken = default)
        {
            var tcs = new TaskCompletionSource<CatalogLocatorUpdateOutcome>();
            CheckAndDownloadCatalogUpdate(pSettings, pCdnBaseUrl, pToken, r => tcs.TrySetResult(r));
            return tcs.Task;
        }

        // ── LoadSettings ─────────────────────────────────────────────────────

        /// <summary>
        /// Callback form of <see cref="LoadSettingsAsync"/>. On failure, returns
        /// <c>(null, ex?)</c>; on cancellation returns <c>(null, OperationCanceledException)</c>.
        /// </summary>
        public static void LoadSettings(
            string pSettingsPath, CancellationToken pToken,
            Action<RuntimeSettings, Exception> onComplete)
        {
            if (onComplete == null) return;

            if (!HyperContentPaths.ShouldUseWebRequest(pSettingsPath))
            {
                if (!File.Exists(pSettingsPath))
                {
                    HCLogger.LogError(ErrorCode.SETTINGS_NOT_FOUND,
                        $"settings.json not found: {pSettingsPath}");
                    onComplete(null, null);
                    return;
                }

                try
                {
                    string json = File.ReadAllText(pSettingsPath);
                    onComplete(ParseSettings(json, pSettingsPath), null);
                }
                catch (Exception e)
                {
                    HCLogger.LogError(ErrorCode.SETTINGS_LOAD_FAILED,
                        $"Failed to load settings.json: {e.Message}");
                    onComplete(null, e);
                }
                return;
            }

            HyperContentPaths.LoadText(pSettingsPath, 10, pToken, (json, ex) =>
            {
                if (ex is OperationCanceledException)
                {
                    onComplete(null, ex);
                    return;
                }

                if (string.IsNullOrEmpty(json))
                {
                    HCLogger.LogError(ErrorCode.SETTINGS_NOT_FOUND,
                        $"settings.json not found or empty: {pSettingsPath}");
                    onComplete(null, null);
                    return;
                }

                try
                {
                    onComplete(ParseSettings(json, pSettingsPath), null);
                }
                catch (Exception e2)
                {
                    HCLogger.LogError(ErrorCode.SETTINGS_LOAD_FAILED,
                        $"Failed to parse settings.json: {e2.Message}");
                    onComplete(null, e2);
                }
            });
        }

        /// <summary>
        /// Load only settings.json without catalog resolution.
        /// Useful for checking update availability before full init.
        /// </summary>
        public static Task<RuntimeSettings> LoadSettingsAsync(
            string pSettingsPath, CancellationToken pToken = default)
        {
            var tcs = new TaskCompletionSource<RuntimeSettings>();
            LoadSettings(pSettingsPath, pToken, (settings, ex) =>
            {
                if (ex is OperationCanceledException)
                    tcs.TrySetException(ex);
                else
                    tcs.TrySetResult(settings);
            });
            return tcs.Task;
        }

        private static RuntimeSettings ParseSettings(string pJson, string pPathForLog)
        {
            var settings = JsonUtility.FromJson<RuntimeSettings>(pJson);
            if (settings == null)
            {
                HCLogger.LogError(ErrorCode.SETTINGS_INVALID_FORMAT,
                    $"Failed to parse settings.json: {pPathForLog}");
            }
            return settings;
        }

        // ── DownloadText / DownloadAndCacheCatalog (private callback ops) ────

        /// <summary>
        /// Build a human-readable failure description for a finished UnityWebRequest,
        /// classifying HTTP errors (e.g. 404) vs connection errors vs timeout so logs
        /// can tell "file missing on CDN" apart from "network down / request timed out".
        /// </summary>
        private static string DescribeWebRequestFailure(UnityWebRequest pRequest, string pUrl)
        {
            long httpCode = pRequest.responseCode;
            string error = pRequest.error;
            string category;
            switch (pRequest.result)
            {
                case UnityWebRequest.Result.ProtocolError:
                    category = httpCode == 404 ? "HTTP 404 Not Found" : $"HTTP error {httpCode}";
                    break;
                case UnityWebRequest.Result.ConnectionError:
                    category = !string.IsNullOrEmpty(error)
                        && error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                            ? "Timeout"
                            : "Connection error";
                    break;
                case UnityWebRequest.Result.DataProcessingError:
                    category = "Data processing error";
                    break;
                default:
                    category = "Unknown error";
                    break;
            }
            return $"{category} | url={pUrl} | httpCode={httpCode} | result={pRequest.result} | error={error}";
        }

        private static void DownloadText(
            string pUrl, int pTimeout, CancellationToken pToken,
            Action<string, Exception> onComplete)
        {
            if (onComplete == null) return;

            if (pToken.IsCancellationRequested)
            {
                onComplete(null, new OperationCanceledException(pToken));
                return;
            }

            HCLogger.LogVerbose($"[CatalogLocator] Downloading: {pUrl}");

            UnityWebRequest request;
            try
            {
                request = UnityWebRequest.Get(pUrl);
                request.timeout = pTimeout > 0 ? pTimeout : 30;
            }
            catch (Exception e)
            {
                onComplete(null, e);
                return;
            }

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
                    {
                        onComplete(request.downloadHandler.text, null);
                    }
                    else
                    {
                        string detail = DescribeWebRequestFailure(request, pUrl);
                        HCLogger.LogError($"[CatalogLocator] Download failed: {detail}");
                        onComplete(null, new Exception(detail));
                    }
                }
                catch (Exception e)
                {
                    HCLogger.LogWarn($"[CatalogLocator] Download exception ({pUrl}): {e.Message}");
                    onComplete(null, e);
                }
                finally
                {
                    ctr.Dispose();
                    request.Dispose();
                }
            };
        }

        private static void DownloadAndCacheCatalog(
            RuntimeSettings pSettings,
            string pCachedCatalogPath,
            string pCachedHashPath,
            string pRemoteHash,
            string pCatalogAbsoluteUrl,
            CancellationToken pToken,
            Action<bool, Exception> onComplete)
        {
            if (onComplete == null) return;

            if (string.IsNullOrEmpty(pCatalogAbsoluteUrl))
            {
                HCLogger.LogWarn("[CatalogLocator] Remote catalog URL is empty");
                onComplete(false, null);
                return;
            }

            if (pToken.IsCancellationRequested)
            {
                onComplete(false, new OperationCanceledException(pToken));
                return;
            }

            UnityWebRequest request;
            try
            {
                request = UnityWebRequest.Get(pCatalogAbsoluteUrl);
                request.timeout = pSettings.catalogRequestTimeout > 0 ? pSettings.catalogRequestTimeout : 30;
            }
            catch (Exception e)
            {
                HCLogger.LogWarn($"[CatalogLocator] Remote catalog request construction failed: {e.Message}");
                onComplete(false, e);
                return;
            }

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
                        onComplete(false, new OperationCanceledException(pToken));
                        return;
                    }

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        string detail = DescribeWebRequestFailure(request, pCatalogAbsoluteUrl);
                        HCLogger.LogError($"[CatalogLocator] Remote catalog download failed: {detail}");
                        onComplete(false, null);
                        return;
                    }

                    string cacheDir = Path.GetDirectoryName(pCachedCatalogPath);
                    if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
                        Directory.CreateDirectory(cacheDir);

                    string tempCatalog = pCachedCatalogPath + ".tmp";
                    File.WriteAllBytes(tempCatalog, request.downloadHandler.data);
                    if (File.Exists(pCachedCatalogPath))
                        File.Delete(pCachedCatalogPath);
                    File.Move(tempCatalog, pCachedCatalogPath);

                    File.WriteAllText(pCachedHashPath, pRemoteHash);

                    HCLogger.LogInfo($"[CatalogLocator] Remote catalog downloaded and cached: {pCachedCatalogPath}");
                    onComplete(true, null);
                }
                catch (Exception e)
                {
                    HCLogger.LogWarn($"[CatalogLocator] Remote catalog cache failed: {e.Message}");
                    onComplete(false, e);
                }
                finally
                {
                    ctr.Dispose();
                    request.Dispose();
                }
            };
        }

        /// <summary>
        /// Read cached hash from disk (pure local file IO, no async needed).
        /// </summary>
        private static string ReadCachedHash(string pCachedHashPath)
        {
            if (!File.Exists(pCachedHashPath))
            {
                HCLogger.LogVerbose("[CatalogLocator] No cached hash found (first launch or new version)");
                return null;
            }

            try
            {
                string hash = File.ReadAllText(pCachedHashPath).Trim();
                HCLogger.LogVerbose($"[CatalogLocator] Cached hash: {hash}");
                return hash;
            }
            catch (Exception e)
            {
                HCLogger.LogWarn($"[CatalogLocator] Failed to read cached hash: {e.Message}");
                return null;
            }
        }
    }
}
