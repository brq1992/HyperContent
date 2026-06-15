using System;
using System.Threading;
using UnityEngine;
using com.igg.hypercontent.shared;
using com.igg.hypercontent.runtime;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Stub implementation of IBundleTransport for POC.
    /// This is a placeholder — full implementation will be done by Owner1/2/3.
    /// </summary>
    public class StubTransport : IBundleTransport
    {
        public bool Initialize(string pBaseUrl, int pTimeoutSeconds = 30)
        {
            return false;
        }

        public void DownloadAsync(string pUrl, Action<float> pOnProgress, Action<FetchResult> pOnComplete,
            CancellationToken pCt = default)
        {
            if (pCt.IsCancellationRequested)
            {
                pOnComplete?.Invoke(FetchResult.CreateFailure(ErrorCode.OPERATION_CANCELLED, "Cancelled"));
                return;
            }

            pOnComplete?.Invoke(FetchResult.CreateFailure(
                ErrorCode.TRANSPORT_NETWORK_ERROR,
                "Transport not implemented in POC"));
        }

#pragma warning disable CS0618 // Obsolete member usage — required by interface until removal
        public FetchResult Download(string pUrl, out byte[] pData)
        {
            pData = null;
            return FetchResult.CreateFailure(
                ErrorCode.TRANSPORT_NETWORK_ERROR,
                "Transport not implemented in POC");
        }
#pragma warning restore CS0618

        public void CancelDownload(string pUrl)
        {
        }

        public bool IsDownloading(string pUrl)
        {
            return false;
        }
    }
}
