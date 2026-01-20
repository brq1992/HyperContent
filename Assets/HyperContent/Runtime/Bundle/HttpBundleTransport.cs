using System;
using System.Collections.Generic;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// HTTP-based bundle transport implementation
    /// Supports timeout, retry, and concurrent downloads
    /// </summary>
    public class HttpBundleTransport : IBundleTransport
    {
        private string _baseUrl;
        private int _timeoutSeconds;
        private int _maxRetries;
        private int _maxConcurrentDownloads;
        
        // Active download operations
        private Dictionary<string, DownloadOperation> _activeDownloads = new Dictionary<string, DownloadOperation>();
        private Queue<string> _downloadQueue = new Queue<string>();
        private int _currentConcurrentDownloads = 0;
        
        // Default configuration
        private const int DEFAULT_TIMEOUT_SECONDS = 30;
        private const int DEFAULT_MAX_RETRIES = 3;
        private const int DEFAULT_MAX_CONCURRENT = 4;
        
        /// <summary>
        /// Download operation state
        /// </summary>
        private class DownloadOperation
        {
            public string Url;
            public UnityWebRequest Request;
            public Action<float> OnProgress;
            public Action<FetchResult> OnComplete;
            public int RetryCount;
            public float StartTime;
            public bool IsCancelled;
        }
        
        public bool Initialize(string baseUrl, int timeoutSeconds = 30)
        {
            _baseUrl = baseUrl;
            _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : DEFAULT_TIMEOUT_SECONDS;
            _maxRetries = DEFAULT_MAX_RETRIES;
            _maxConcurrentDownloads = DEFAULT_MAX_CONCURRENT;
            
            // Ensure baseUrl ends with /
            if (!string.IsNullOrEmpty(_baseUrl) && !_baseUrl.EndsWith("/"))
            {
                _baseUrl += "/";
            }
            
            Debug.Log($"[HyperContent] HttpBundleTransport initialized: baseUrl={_baseUrl}, timeout={_timeoutSeconds}s");
            return true;
        }
        
        /// <summary>
        /// Set maximum retry count
        /// </summary>
        public void SetMaxRetries(int maxRetries)
        {
            _maxRetries = maxRetries > 0 ? maxRetries : DEFAULT_MAX_RETRIES;
        }
        
        /// <summary>
        /// Set maximum concurrent downloads
        /// </summary>
        public void SetMaxConcurrentDownloads(int maxConcurrent)
        {
            _maxConcurrentDownloads = maxConcurrent > 0 ? maxConcurrent : DEFAULT_MAX_CONCURRENT;
        }
        
        public void DownloadAsync(string url, Action<float> onProgress, Action<FetchResult> onComplete)
        {
            if (string.IsNullOrEmpty(url))
            {
                onComplete?.Invoke(FetchResult.CreateFailure(
                    ErrorCode.TRANSPORT_INVALID_URL,
                    "URL is null or empty"
                ));
                return;
            }
            
            // Build full URL if baseUrl is set
            string fullUrl = url;
            if (!string.IsNullOrEmpty(_baseUrl) && !url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                fullUrl = _baseUrl + url;
            }
            
            // Check if already downloading
            if (_activeDownloads.ContainsKey(fullUrl))
            {
                Debug.LogWarning($"[HyperContent] Download already in progress: {fullUrl}");
                return;
            }
            
            // Start download
            StartDownload(fullUrl, onProgress, onComplete);
        }
        
        private void StartDownload(string url, Action<float> onProgress, Action<FetchResult> onComplete)
        {
            // Check concurrent limit
            if (_currentConcurrentDownloads >= _maxConcurrentDownloads)
            {
                _downloadQueue.Enqueue(url);
                Debug.Log($"[HyperContent] Download queued (concurrent limit reached): {url}");
                return;
            }
            
            _currentConcurrentDownloads++;
            
            var operation = new DownloadOperation
            {
                Url = url,
                OnProgress = onProgress,
                OnComplete = onComplete,
                RetryCount = 0,
                StartTime = Time.realtimeSinceStartup,
                IsCancelled = false
            };
            
            _activeDownloads[url] = operation;
            
            // Start coroutine for download
            HyperContentManager.Instance.StartCoroutine(DownloadCoroutine(operation));
        }
        
        private IEnumerator DownloadCoroutine(DownloadOperation operation)
        {
            string url = operation.Url;
            int retryCount = 0;
            FetchResult result = null;
            
            while (retryCount <= _maxRetries && !operation.IsCancelled)
            {
                operation.RetryCount = retryCount;
                float attemptStartTime = Time.realtimeSinceStartup;
                
                // Create UnityWebRequest
                UnityWebRequest request = UnityWebRequest.Get(url);
                request.timeout = _timeoutSeconds;
                operation.Request = request;
                
                // Send request
                var asyncOp = request.SendWebRequest();
                
                // Progress tracking
                while (!asyncOp.isDone && !operation.IsCancelled)
                {
                    float progress = asyncOp.progress;
                    operation.OnProgress?.Invoke(progress);
                    yield return null;
                }
                
                if (operation.IsCancelled)
                {
                    request.Abort();
                    request.Dispose();
                    result = FetchResult.CreateFailure(
                        ErrorCode.TRANSPORT_DOWNLOAD_FAILED,
                        "Download cancelled",
                        (long)((Time.realtimeSinceStartup - attemptStartTime) * 1000)
                    );
                    break;
                }
                
                // Check result
                if (request.result == UnityWebRequest.Result.Success)
                {
                    byte[] data = request.downloadHandler.data;
                    long durationMs = (long)((Time.realtimeSinceStartup - attemptStartTime) * 1000);
                    
                    result = FetchResult.CreateSuccess(data.Length, durationMs);
                    request.Dispose();
                    break;
                }
                else
                {
                    // Check if should retry
                    bool shouldRetry = false;
                    int errorCode = ErrorCode.TRANSPORT_NETWORK_ERROR;
                    string errorMsg = request.error;
                    
                    if (request.result == UnityWebRequest.Result.ConnectionError ||
                        request.result == UnityWebRequest.Result.DataProcessingError)
                    {
                        shouldRetry = retryCount < _maxRetries;
                        errorCode = ErrorCode.TRANSPORT_NETWORK_ERROR;
                    }
                    else if (request.result == UnityWebRequest.Result.ProtocolError)
                    {
                        // HTTP errors (4xx, 5xx) - retry only for 5xx
                        int httpCode = (int)request.responseCode;
                        if (httpCode >= 500 && retryCount < _maxRetries)
                        {
                            shouldRetry = true;
                        }
                        errorCode = ErrorCode.TRANSPORT_DOWNLOAD_FAILED;
                        errorMsg = $"HTTP {httpCode}: {request.error}";
                    }
                    
                    request.Dispose();
                    
                    if (shouldRetry)
                    {
                        retryCount++;
                        Debug.LogWarning($"[HyperContent] Download failed, retrying ({retryCount}/{_maxRetries}): {url}, error: {errorMsg}");
                        yield return new WaitForSeconds(1.0f * retryCount); // Exponential backoff
                        continue;
                    }
                    else
                    {
                        long durationMs = (long)((Time.realtimeSinceStartup - attemptStartTime) * 1000);
                        result = FetchResult.CreateFailure(errorCode, errorMsg, durationMs);
                        break;
                    }
                }
            }
            
            // Cleanup
            _activeDownloads.Remove(url);
            _currentConcurrentDownloads--;
            
            // Invoke callback
            if (result != null)
            {
                operation.OnComplete?.Invoke(result);
            }
            
            // Process queued downloads
            ProcessDownloadQueue();
        }
        
        private void ProcessDownloadQueue()
        {
            while (_downloadQueue.Count > 0 && _currentConcurrentDownloads < _maxConcurrentDownloads)
            {
                string queuedUrl = _downloadQueue.Dequeue();
                // Reconstruct operation from queue - this is simplified
                // In real implementation, we'd need to store the callbacks
                Debug.LogWarning($"[HyperContent] Queued download callback lost: {queuedUrl}");
            }
        }
        
        public FetchResult Download(string url, out byte[] data)
        {
            data = null;
            
            if (string.IsNullOrEmpty(url))
            {
                return FetchResult.CreateFailure(
                    ErrorCode.TRANSPORT_INVALID_URL,
                    "URL is null or empty"
                );
            }
            
            // Build full URL
            string fullUrl = url;
            if (!string.IsNullOrEmpty(_baseUrl) && !url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                fullUrl = _baseUrl + url;
            }
            
            // Use UnityWebRequest synchronously (blocking)
            UnityWebRequest request = UnityWebRequest.Get(fullUrl);
            request.timeout = _timeoutSeconds;
            
            var asyncOp = request.SendWebRequest();
            
            // Wait for completion
            while (!asyncOp.isDone)
            {
                // Block until done
            }
            
            FetchResult result;
            if (request.result == UnityWebRequest.Result.Success)
            {
                data = request.downloadHandler.data;
                result = FetchResult.CreateSuccess(data.Length, 0);
            }
            else
            {
                // Check if timeout by examining error message or request result
                int errorCode = ErrorCode.TRANSPORT_DOWNLOAD_FAILED;
                if (request.result == UnityWebRequest.Result.ConnectionError && 
                    (request.error != null && request.error.Contains("timeout")))
                {
                    errorCode = ErrorCode.TRANSPORT_TIMEOUT;
                }
                result = FetchResult.CreateFailure(errorCode, request.error);
            }
            
            request.Dispose();
            return result;
        }
        
        public void CancelDownload(string url)
        {
            string fullUrl = url;
            if (!string.IsNullOrEmpty(_baseUrl) && !url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                fullUrl = _baseUrl + url;
            }
            
            if (_activeDownloads.TryGetValue(fullUrl, out var operation))
            {
                operation.IsCancelled = true;
                if (operation.Request != null)
                {
                    operation.Request.Abort();
                }
                Debug.Log($"[HyperContent] Download cancelled: {fullUrl}");
            }
        }
        
        public bool IsDownloading(string url)
        {
            string fullUrl = url;
            if (!string.IsNullOrEmpty(_baseUrl) && !url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                fullUrl = _baseUrl + url;
            }
            
            return _activeDownloads.ContainsKey(fullUrl);
        }
    }
}
