using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// HTTP-based bundle transport implementation.
    /// Fully callback-driven — never blocks the main thread and has <b>no</b> internal
    /// <c>async/await</c>. Uses <see cref="UnityWebRequestAsyncOperation.completed"/> for
    /// transition events, <see cref="HyperContentRunner"/> for per-frame progress ticks, and
    /// <see cref="HyperContentRunner.Schedule"/> for retry backoff. Matches Addressables'
    /// resource-manager style state-machine approach (no per-attempt async state machine).
    ///
    /// Relative URLs (no scheme) are resolved as <c>{base}{platform}/{path}</c>, where <c>platform</c> is
    /// <see cref="HyperContentPaths.RemoteBundlePlatformSegment"/> (same folder names as local package bundles).
    /// Catalog passes an extensionless segment; this transport appends <see cref="NamingRules.BUNDLE_FILE_EXTENSION"/>
    /// before composing the URL so CDN files stay <c>*.bundle</c>.
    ///
    /// HTTP Range / resume: deferred — see <c>Assets/HyperContent/docs/TODO.md</c> (marked item).
    /// </summary>
    public class HttpBundleTransport : IBundleTransport
    {
        private string _baseUrl;
        private int _timeoutSeconds;
        private int _maxRetries;
        private int _maxConcurrentDownloads;

        private readonly Dictionary<string, CancellationTokenSource> _activeDownloadTokenDict
            = new Dictionary<string, CancellationTokenSource>();
        private readonly Queue<QueuedDownload> _pendingQueue = new Queue<QueuedDownload>();
        private int _currentConcurrentDownloads;

        private const int DEFAULT_TIMEOUT_SECONDS = 30;
        private const int DEFAULT_MAX_RETRIES = 3;
        private const int DEFAULT_MAX_CONCURRENT = 4;

        private struct QueuedDownload
        {
            public string url;
            public Action<float> onProgress;
            public Action<FetchResult> onComplete;
            public CancellationToken cancellationToken;
        }

        public bool Initialize(string pBaseUrl, int pTimeoutSeconds = 30)
        {
            _baseUrl = pBaseUrl;
            _timeoutSeconds = pTimeoutSeconds > 0 ? pTimeoutSeconds : DEFAULT_TIMEOUT_SECONDS;
            _maxRetries = DEFAULT_MAX_RETRIES;
            _maxConcurrentDownloads = DEFAULT_MAX_CONCURRENT;

            if (!string.IsNullOrEmpty(_baseUrl) && !_baseUrl.EndsWith("/"))
            {
                _baseUrl += "/";
            }

            HCLogger.LogInfo($"HttpBundleTransport initialized: baseUrl={_baseUrl}, timeout={_timeoutSeconds}s");
            return true;
        }

        /// <summary>
        /// Set or update the base URL for remote bundle downloads at runtime.
        /// Call this when remoteBundleBaseUrl was empty in settings.json (e.g. Update Build left it blank).
        /// </summary>
        public void SetBaseUrl(string pBaseUrl)
        {
            _baseUrl = pBaseUrl ?? "";
            if (!string.IsNullOrEmpty(_baseUrl) && !_baseUrl.EndsWith("/"))
                _baseUrl += "/";
            HCLogger.LogInfo($"HttpBundleTransport base URL updated: {_baseUrl}");
        }

        public void SetMaxRetries(int pMaxRetries)
        {
            _maxRetries = pMaxRetries > 0 ? pMaxRetries : DEFAULT_MAX_RETRIES;
        }

        public void SetMaxConcurrentDownloads(int pMaxConcurrent)
        {
            _maxConcurrentDownloads = pMaxConcurrent > 0 ? pMaxConcurrent : DEFAULT_MAX_CONCURRENT;
        }

        // ── IBundleTransport: Callback-based async (primary) ────────────────

        public void DownloadAsync(string pUrl, Action<float> pOnProgress, Action<FetchResult> pOnComplete,
            CancellationToken pCt = default)
        {
            if (string.IsNullOrEmpty(pUrl))
            {
                pOnComplete?.Invoke(FetchResult.CreateFailure(
                    ErrorCode.TRANSPORT_INVALID_URL, "URL is null or empty"));
                return;
            }

            string fullUrl = ResolveUrl(pUrl);

            if (_activeDownloadTokenDict.ContainsKey(fullUrl))
            {
                HCLogger.LogWarn($"Download already in progress: {fullUrl}");
                return;
            }

            if (_currentConcurrentDownloads >= _maxConcurrentDownloads)
            {
                _pendingQueue.Enqueue(new QueuedDownload
                {
                    url = fullUrl,
                    onProgress = pOnProgress,
                    onComplete = pOnComplete,
                    cancellationToken = pCt
                });
                HCLogger.LogVerbose($"Download queued (concurrent limit reached): {fullUrl}");
                return;
            }

            StartDownload(fullUrl, pOnProgress, pOnComplete, pCt);
        }

        // ── IBundleTransport: Sync (deprecated) ─────────────────────────────

        [Obsolete("Blocks main thread. Use DownloadAsync(url, onProgress, onComplete, ct) instead.")]
        public FetchResult Download(string pUrl, out byte[] pData)
        {
            pData = null;

            if (string.IsNullOrEmpty(pUrl))
            {
                return FetchResult.CreateFailure(
                    ErrorCode.TRANSPORT_INVALID_URL, "URL is null or empty");
            }

            string fullUrl = ResolveUrl(pUrl);

            using (var request = UnityWebRequest.Get(fullUrl))
            {
                request.timeout = _timeoutSeconds;
                var asyncOp = request.SendWebRequest();

                while (!asyncOp.isDone) { }

                if (request.result == UnityWebRequest.Result.Success)
                {
                    pData = request.downloadHandler.data;
                    return FetchResult.CreateSuccess(pData.Length, 0);
                }

                int errorCode = ErrorCode.TRANSPORT_DOWNLOAD_FAILED;
                if (request.result == UnityWebRequest.Result.ConnectionError &&
                    request.error != null && request.error.Contains("timeout"))
                {
                    errorCode = ErrorCode.TRANSPORT_TIMEOUT;
                }
                return FetchResult.CreateFailure(errorCode, request.error);
            }
        }

        public void CancelDownload(string pUrl)
        {
            string fullUrl = ResolveUrl(pUrl);

            if (_activeDownloadTokenDict.TryGetValue(fullUrl, out var cts))
            {
                cts.Cancel();
                HCLogger.LogVerbose($"Download cancelled: {fullUrl}");
            }
        }

        public bool IsDownloading(string pUrl)
        {
            string fullUrl = ResolveUrl(pUrl);
            return _activeDownloadTokenDict.ContainsKey(fullUrl);
        }

        // ── Internal callback engine ────────────────────────────────────────

        /// <summary>
        /// Per-download state bag: holds the <see cref="UnityWebRequest"/>, cancellation plumbing,
        /// progress-tick subscription, and retry counter. One instance per URL, disposed in
        /// <see cref="Finish"/>. Intentionally a class so callbacks can capture by reference
        /// without closure allocations per call site.
        /// </summary>
        private sealed class HttpDownload
        {
            public HttpBundleTransport owner;
            public string url;
            public Action<float> onProgress;
            public Action<FetchResult> onComplete;
            public CancellationToken originalToken;
            public CancellationTokenSource cts;

            public int attempt;
            public float attemptStartTime;

            public UnityWebRequest request;
            public CancellationTokenRegistration abortRegistration;
            public Action progressTickCached;
            public bool progressTickRegistered;
            public int scheduledRetryToken;
            public bool finished;
        }

        private void StartDownload(string pUrl, Action<float> pOnProgress, Action<FetchResult> pOnComplete,
            CancellationToken pCt)
        {
            _currentConcurrentDownloads++;
            var cts = pCt.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(pCt)
                : new CancellationTokenSource();
            _activeDownloadTokenDict[pUrl] = cts;

            var state = new HttpDownload
            {
                owner = this,
                url = pUrl,
                onProgress = pOnProgress,
                onComplete = pOnComplete,
                originalToken = pCt,
                cts = cts,
                attempt = 0
            };
            state.progressTickCached = () => OnProgressTick(state);

            BeginAttempt(state);
        }

        private void BeginAttempt(HttpDownload pState)
        {
            if (pState.finished) return;

            if (pState.cts.IsCancellationRequested)
            {
                Finish(pState, FetchResult.CreateFailure(ErrorCode.OPERATION_CANCELLED, "Download cancelled"));
                return;
            }

            pState.attemptStartTime = Time.realtimeSinceStartup;

            try
            {
                pState.request = UnityWebRequest.Get(pState.url);
                pState.request.timeout = _timeoutSeconds;
            }
            catch (Exception e)
            {
                HCLogger.LogError($"Failed to construct UnityWebRequest for {pState.url}: {e.Message}");
                Finish(pState, FetchResult.CreateFailure(ErrorCode.TRANSPORT_DOWNLOAD_FAILED, e.Message));
                return;
            }

            if (pState.cts.Token.CanBeCanceled)
            {
                var localRequest = pState.request;
                pState.abortRegistration = pState.cts.Token.Register(() =>
                {
                    try { localRequest.Abort(); } catch { /* ignore */ }
                });
            }

            var asyncOp = pState.request.SendWebRequest();

            if (pState.onProgress != null)
            {
                HyperContentRunner.Instance.AddUpdate(pState.progressTickCached);
                pState.progressTickRegistered = true;
            }

            asyncOp.completed += _ => OnAttemptCompleted(pState);
        }

        private void OnProgressTick(HttpDownload pState)
        {
            if (pState.finished || pState.request == null) return;
            try { pState.onProgress?.Invoke(pState.request.downloadProgress); }
            catch (Exception e) { HCLogger.LogError($"[HttpBundleTransport] onProgress threw: {e.Message}"); }
        }

        private void OnAttemptCompleted(HttpDownload pState)
        {
            if (pState.finished) return;

            if (pState.progressTickRegistered)
            {
                HyperContentRunner.Instance.RemoveUpdate(pState.progressTickCached);
                pState.progressTickRegistered = false;
            }

            pState.abortRegistration.Dispose();
            pState.abortRegistration = default;

            long durationMs = (long)((Time.realtimeSinceStartup - pState.attemptStartTime) * 1000);
            var request = pState.request;

            if (pState.cts.IsCancellationRequested)
            {
                DisposeRequest(pState);
                Finish(pState, FetchResult.CreateFailure(ErrorCode.OPERATION_CANCELLED, "Download cancelled", durationMs));
                return;
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                byte[] data = request.downloadHandler.data;
                try { pState.onProgress?.Invoke(1f); }
                catch (Exception e) { HCLogger.LogError($"[HttpBundleTransport] final onProgress threw: {e.Message}"); }

                var result = FetchResult.CreateSuccess(data.Length, durationMs);
                result.Data = data;
                DisposeRequest(pState);
                Finish(pState, result);
                return;
            }

            bool canRetry = pState.attempt < _maxRetries && ShouldRetry(request);
            if (canRetry)
            {
                pState.attempt++;
                float backoffSeconds = Mathf.Pow(2, pState.attempt - 1);
                HCLogger.LogWarn($"Download failed, retrying ({pState.attempt}/{_maxRetries}): {pState.url}, " +
                    $"error: {request.error}");

                DisposeRequest(pState);

                pState.scheduledRetryToken = HyperContentRunner.Instance.Schedule(backoffSeconds,
                    () => BeginAttempt(pState));
                return;
            }

            int errorCode = request.result == UnityWebRequest.Result.ConnectionError
                ? ErrorCode.TRANSPORT_NETWORK_ERROR
                : ErrorCode.TRANSPORT_DOWNLOAD_FAILED;
            string errorMsg = request.error;
            if (request.result == UnityWebRequest.Result.ProtocolError)
            {
                errorMsg = $"HTTP {request.responseCode}: {request.error}";
            }

            DisposeRequest(pState);
            Finish(pState, FetchResult.CreateFailure(errorCode, errorMsg, durationMs));
        }

        private static void DisposeRequest(HttpDownload pState)
        {
            if (pState.request != null)
            {
                try { pState.request.Dispose(); } catch { /* ignore */ }
                pState.request = null;
            }
        }

        private void Finish(HttpDownload pState, FetchResult pResult)
        {
            if (pState.finished) return;
            pState.finished = true;

            if (pState.scheduledRetryToken != 0)
            {
                HyperContentRunner.Instance.CancelSchedule(pState.scheduledRetryToken);
                pState.scheduledRetryToken = 0;
            }

            if (pState.progressTickRegistered)
            {
                HyperContentRunner.Instance.RemoveUpdate(pState.progressTickCached);
                pState.progressTickRegistered = false;
            }

            pState.abortRegistration.Dispose();
            DisposeRequest(pState);

            _activeDownloadTokenDict.Remove(pState.url);
            try { pState.cts.Dispose(); } catch { /* ignore */ }
            _currentConcurrentDownloads--;

            try { pState.onComplete?.Invoke(pResult); }
            catch (Exception e) { HCLogger.LogError($"[HttpBundleTransport] onComplete threw: {e.Message}"); }

            DrainPendingQueue();
        }

        private static bool ShouldRetry(UnityWebRequest pRequest)
        {
            if (pRequest.result == UnityWebRequest.Result.ConnectionError ||
                pRequest.result == UnityWebRequest.Result.DataProcessingError)
            {
                return true;
            }

            if (pRequest.result == UnityWebRequest.Result.ProtocolError &&
                pRequest.responseCode >= 500)
            {
                return true;
            }

            return false;
        }

        private void DrainPendingQueue()
        {
            while (_pendingQueue.Count > 0 && _currentConcurrentDownloads < _maxConcurrentDownloads)
            {
                var queued = _pendingQueue.Dequeue();
                if (_activeDownloadTokenDict.ContainsKey(queued.url))
                {
                    HCLogger.LogWarn($"Queued download already active, skipping: {queued.url}");
                    continue;
                }
                StartDownload(queued.url, queued.onProgress, queued.onComplete, queued.cancellationToken);
            }
        }

        private string ResolveUrl(string pUrl)
        {
            if (string.IsNullOrEmpty(pUrl))
                return pUrl;

            string path = pUrl.Trim();
            if (!path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                path = NamingRules.ToTransportRelativeBundlePath(path);
            }

            string combined = HyperContentPaths.CombineRemoteCdnRequestUrl(_baseUrl, path);
            return combined ?? path;
        }
    }
}
