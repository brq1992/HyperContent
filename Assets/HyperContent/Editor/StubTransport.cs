using System;
using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// Stub implementation of IBundleTransport for POC
    /// This is a placeholder - full implementation will be done by Owner1/2/3
    /// </summary>
    public class StubTransport : IBundleTransport
    {
        public bool Initialize(string baseUrl, int timeoutSeconds = 30)
        {
            // POC: Not implemented
            return false;
        }
        
        public void DownloadAsync(string url, Action<float> onProgress, Action<FetchResult> onComplete)
        {
            // POC: Not implemented
            onComplete?.Invoke(FetchResult.CreateFailure(
                ErrorCode.TRANSPORT_NETWORK_ERROR,
                "Transport not implemented in POC"
            ));
        }
        
        public FetchResult Download(string url, out byte[] data)
        {
            data = null;
            return FetchResult.CreateFailure(
                ErrorCode.TRANSPORT_NETWORK_ERROR,
                "Transport not implemented in POC"
            );
        }
        
        public void CancelDownload(string url)
        {
            // POC: Not implemented
        }
        
        public bool IsDownloading(string url)
        {
            return false;
        }
    }
}
