using System;
using System.Threading;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Priority for <see cref="IBundleDownloadQueue"/> scheduling. Load-driven work uses <see cref="High"/>;
    /// batch update UI uses <see cref="Normal"/>; background prefetch uses <see cref="Low"/> (runtime plan §5).
    /// </summary>
    public enum BundleDownloadPriority
    {
        High = 0,
        Normal = 1,
        Low = 2
    }

    /// <summary>
    /// Request passed to <see cref="IBundleDownloadQueue.Enqueue"/>. The queue is the only bundle path that should call
    /// <see cref="IBundleTransport"/>; implementations may merge the same logical URL and fan out <see cref="OnComplete"/> to each waiter.
    /// </summary>
    public sealed class BundleDownloadEnqueueOptions
    {
        /// <summary>Extensionless path after CDN base / platform folder; <see cref="HttpBundleTransport"/> adds <c>.bundle</c>. Same as <see cref="BundleInfo.RemoteRelativePath"/>.</summary>
        public string RemoteRelativePath { get; set; }

        /// <summary>Logical bundle name for <see cref="IBundleStore"/> keys.</summary>
        public string BundleName { get; set; }

        public string Hash { get; set; }

        public long SizeBytes { get; set; }

        public BundleDownloadPriority Priority { get; set; } = BundleDownloadPriority.Normal;

        public CancellationToken CancellationToken { get; set; }

        /// <summary>Invoked when this logical request completes (success or failure), including after a merged physical download.</summary>
        public Action<FetchResult> OnComplete { get; set; }

        /// <summary>Optional per-request progress (0..1) for batch UIs; merged downloads broadcast the same segment progress to all waiters.</summary>
        public Action<float> OnProgress { get; set; }

        /// <summary>Optional correlation for operation-scoped progress (CONVENTIONS.md §5.2).</summary>
        public long ProducerOperationId { get; set; }
    }

    /// <summary>
    /// Coarse global queue progress (numerator/denominator definition is implementation-defined; document in ARCHITECTURE / LOAD_RELEASE_FLOW).
    /// </summary>
    public struct DownloadQueueProgressSnapshot
    {
        public long CompletedBytes;
        public long TotalEstimatedBytes;
        public int ActiveLogicalCount;
        public int PendingLogicalCount;
    }

    /// <summary>
    /// Receives aggregate queue progress. Register via <see cref="IBundleDownloadQueue.RegisterProgressListener"/> or the
    /// <c>HyperContent.RegisterDownloadQueueProgressListener</c> facade (Owner2).
    /// </summary>
    public interface IDownloadQueueProgressListener
    {
        void OnDownloadQueueProgress(ref DownloadQueueProgressSnapshot pSnapshot);
    }

    /// <summary>
    /// Global bundle download queue: priorities, same-URL coalescing, single consumer of <see cref="IBundleTransport"/>.
    /// Owner3 wires <see cref="BundleDownloadManager"/> and load providers to this API; per-waiter <see cref="BundleDownloadEnqueueOptions.CancellationToken"/>
    /// and batch cancel semantics — see <c>CONVENTIONS.md</c> §1.6. HTTP Range deferred (TODO.md).
    /// </summary>
    public interface IBundleDownloadQueue
    {
        void Enqueue(BundleDownloadEnqueueOptions pOptions);

        void RegisterProgressListener(IDownloadQueueProgressListener pListener);

        void UnregisterProgressListener(IDownloadQueueProgressListener pListener);
    }
}
