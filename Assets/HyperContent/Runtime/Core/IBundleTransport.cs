using System;
using System.Threading;
using UnityEngine;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Interface for bundle transport (download/upload).
    /// Handles network operations for remote bundles.
    /// </summary>
    public interface IBundleTransport
    {
        /// <summary>
        /// Initialize the transport.
        /// </summary>
        /// <param name="pBaseUrl">Base URL for remote content</param>
        /// <param name="pTimeoutSeconds">Timeout in seconds</param>
        /// <returns>True if initialization succeeded</returns>
        bool Initialize(string pBaseUrl, int pTimeoutSeconds = 30);

        /// <summary>
        /// Download bundle asynchronously with progress reporting (callback-based). This is
        /// the sole async download entry point — the previous <c>Task&lt;FetchResult&gt;</c> / awaitable
        /// overload was removed to keep the transport free of internal <c>async/await</c>
        /// (callers who need <c>await</c> should wrap with <c>TaskCompletionSource</c>).
        /// </summary>
        /// <param name="pUrl">Full URL to download from</param>
        /// <param name="pOnProgress">Progress callback (0.0 to 1.0)</param>
        /// <param name="pOnComplete">Completion callback with result</param>
        /// <param name="pCt">Linked into the transport attempt; cancel aborts the active request when supported.</param>
        void DownloadAsync(string pUrl, Action<float> pOnProgress, Action<FetchResult> pOnComplete,
            CancellationToken pCt = default);

        /// <summary>
        /// Download bundle synchronously (blocking).
        /// </summary>
        /// <param name="pUrl">Full URL to download from</param>
        /// <param name="pData">Output downloaded data</param>
        /// <returns>Fetch result</returns>
        [Obsolete("Blocks main thread. Use DownloadAsync(url, CancellationToken) instead. Will be removed after Owner2 migration.")]
        FetchResult Download(string pUrl, out byte[] pData);

        /// <summary>
        /// Cancel an ongoing download.
        /// </summary>
        /// <param name="pUrl">URL of the download to cancel</param>
        void CancelDownload(string pUrl);

        /// <summary>
        /// Check if a download is in progress.
        /// </summary>
        /// <param name="pUrl">URL to check</param>
        /// <returns>True if download is active</returns>
        bool IsDownloading(string pUrl);
    }
}
