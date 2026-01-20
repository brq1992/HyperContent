using System;
using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// Interface for bundle transport (download/upload)
    /// Handles network operations for remote bundles
    /// </summary>
    public interface IBundleTransport
    {
        /// <summary>
        /// Initialize the transport
        /// </summary>
        /// <param name="baseUrl">Base URL for remote content</param>
        /// <param name="timeoutSeconds">Timeout in seconds</param>
        /// <returns>True if initialization succeeded</returns>
        bool Initialize(string baseUrl, int timeoutSeconds = 30);
        
        /// <summary>
        /// Download bundle from remote URL
        /// </summary>
        /// <param name="url">Full URL to download from</param>
        /// <param name="onProgress">Progress callback (0.0 to 1.0)</param>
        /// <param name="onComplete">Completion callback with result</param>
        void DownloadAsync(string url, Action<float> onProgress, Action<FetchResult> onComplete);
        
        /// <summary>
        /// Download bundle synchronously (blocking)
        /// </summary>
        /// <param name="url">Full URL to download from</param>
        /// <param name="data">Output downloaded data</param>
        /// <returns>Fetch result</returns>
        FetchResult Download(string url, out byte[] data);
        
        /// <summary>
        /// Cancel an ongoing download
        /// </summary>
        /// <param name="url">URL of the download to cancel</param>
        void CancelDownload(string url);
        
        /// <summary>
        /// Check if a download is in progress
        /// </summary>
        /// <param name="url">URL to check</param>
        /// <returns>True if download is active</returns>
        bool IsDownloading(string url);
    }
}
