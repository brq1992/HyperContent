using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Default <see cref="IBundleDownloadQueue"/>: priority pending queues, same <see cref="BundleDownloadEnqueueOptions.RemoteRelativePath"/>
    /// coalesces into one <see cref="IBundleTransport"/> download and fans out completion to all waiters.
    /// Owner: Owner3. Per-waiter <see cref="BundleDownloadEnqueueOptions.CancellationToken"/> removes that waiter without aborting
    /// the HTTP transfer while other waiters for the same URL remain; the last waiter cancelled triggers <see cref="IBundleTransport.CancelDownload"/>.
    /// </summary>
    public sealed class BundleDownloadQueue : IBundleDownloadQueue
    {
        private readonly IBundleTransport _transport;
        private readonly int _maxConcurrentPhysical;
        private readonly object _gate = new object();

        private readonly Queue<BundleDownloadEnqueueOptions> _pendingHigh = new Queue<BundleDownloadEnqueueOptions>();
        private readonly Queue<BundleDownloadEnqueueOptions> _pendingNormal = new Queue<BundleDownloadEnqueueOptions>();
        private readonly Queue<BundleDownloadEnqueueOptions> _pendingLow = new Queue<BundleDownloadEnqueueOptions>();

        /// <summary>In-flight downloads keyed by normalized relative path.</summary>
        private readonly Dictionary<string, MergeGroup> _inFlight =
            new Dictionary<string, MergeGroup>(StringComparer.Ordinal);

        private int _activePhysicalCount;
        private long _pendingEstimatedBytes;
        private long _completedEstimatedBytes;

        // Running count of logical waiters across all in-flight groups, kept in sync as waiters are
        // added/removed. Lets NotifyProgressListenersLocked report ActiveLogicalCount in O(1) instead
        // of re-summing every group's Waiters on each progress notification.
        private int _activeLogicalCount;

        private readonly List<IDownloadQueueProgressListener> _listeners = new List<IDownloadQueueProgressListener>();

        private sealed class MergeGroup
        {
            public string RelativePath;
            public readonly List<BundleDownloadEnqueueOptions> Waiters = new List<BundleDownloadEnqueueOptions>();
            public readonly List<CancellationTokenRegistration> Registrations = new List<CancellationTokenRegistration>();
        }

        public BundleDownloadQueue(IBundleTransport pTransport, int pMaxConcurrentPhysical = 4)
        {
            _transport = pTransport ?? throw new ArgumentNullException(nameof(pTransport));
            _maxConcurrentPhysical = pMaxConcurrentPhysical > 0 ? pMaxConcurrentPhysical : 4;
        }

        /// <inheritdoc />
        public void Enqueue(BundleDownloadEnqueueOptions pOptions)
        {
            if (pOptions == null)
                throw new ArgumentNullException(nameof(pOptions));

            if (string.IsNullOrEmpty(pOptions.RemoteRelativePath))
            {
                var fail = FetchResult.CreateFailure(
                    ErrorCode.TRANSPORT_INVALID_URL, "RemoteRelativePath is null or empty");
                pOptions.OnComplete?.Invoke(fail);
                return;
            }

            NamingRules.RequireCatalogBundleRelativePath(
                pOptions.RemoteRelativePath, nameof(BundleDownloadEnqueueOptions.RemoteRelativePath));

            if (pOptions.CancellationToken.IsCancellationRequested)
            {
                pOptions.OnComplete?.Invoke(FetchResult.CreateFailure(ErrorCode.OPERATION_CANCELLED, "Cancelled"));
                return;
            }

            lock (_gate)
            {
                string key = NormalizeRelativeKey(pOptions.RemoteRelativePath);
                if (_inFlight.TryGetValue(key, out MergeGroup existing))
                {
                    AddWaiterLocked(key, existing, pOptions);
                    _pendingEstimatedBytes += Math.Max(0, pOptions.SizeBytes);
                    NotifyProgressListenersLocked();
                    return;
                }

                _pendingEstimatedBytes += Math.Max(0, pOptions.SizeBytes);
                switch (pOptions.Priority)
                {
                    case BundleDownloadPriority.High:
                        _pendingHigh.Enqueue(pOptions);
                        break;
                    case BundleDownloadPriority.Low:
                        _pendingLow.Enqueue(pOptions);
                        break;
                    default:
                        _pendingNormal.Enqueue(pOptions);
                        break;
                }

                NotifyProgressListenersLocked();
                PumpLocked();
            }
        }

        /// <inheritdoc />
        public void RegisterProgressListener(IDownloadQueueProgressListener pListener)
        {
            if (pListener == null) return;
            lock (_gate)
            {
                if (!_listeners.Contains(pListener))
                    _listeners.Add(pListener);
            }
        }

        /// <inheritdoc />
        public void UnregisterProgressListener(IDownloadQueueProgressListener pListener)
        {
            if (pListener == null) return;
            lock (_gate)
            {
                _listeners.Remove(pListener);
            }
        }

        private void AddWaiterLocked(string pKey, MergeGroup pGroup, BundleDownloadEnqueueOptions pOpt)
        {
            pGroup.Waiters.Add(pOpt);
            _activeLogicalCount++;
            if (pOpt.CancellationToken.CanBeCanceled)
            {
                var opt = pOpt;
                var reg = pOpt.CancellationToken.Register(() => WaiterCancelled(pKey, opt));
                pGroup.Registrations.Add(reg);
            }
            else
                pGroup.Registrations.Add(default);
        }

        private void WaiterCancelled(string pKey, BundleDownloadEnqueueOptions pOpt)
        {
            MergeGroup group;
            bool lastWaiterCancelled = false;
            string relativePath = null;

            lock (_gate)
            {
                if (!_inFlight.TryGetValue(pKey, out group))
                    return;

                int idx = group.Waiters.IndexOf(pOpt);
                if (idx < 0)
                    return;

                group.Waiters.RemoveAt(idx);
                _activeLogicalCount--;
                if (idx < group.Registrations.Count)
                {
                    try
                    {
                        group.Registrations[idx].Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        // ignored
                    }

                    group.Registrations.RemoveAt(idx);
                }

                _pendingEstimatedBytes = Math.Max(0, _pendingEstimatedBytes - Math.Max(0, pOpt.SizeBytes));
                NotifyProgressListenersLocked();

                if (group.Waiters.Count == 0)
                {
                    lastWaiterCancelled = true;
                    relativePath = group.RelativePath;
                    _inFlight.Remove(pKey);
                    _activePhysicalCount--;
                }
            }

            try
            {
                pOpt.OnComplete?.Invoke(FetchResult.CreateFailure(ErrorCode.OPERATION_CANCELLED, "Cancelled"));
            }
            catch (Exception e)
            {
                HCLogger.LogError($"BundleDownloadQueue OnComplete threw: {e.Message}");
            }

            if (lastWaiterCancelled && !string.IsNullOrEmpty(relativePath))
            {
                _transport.CancelDownload(relativePath);
                lock (_gate)
                {
                    NotifyProgressListenersLocked();
                    PumpLocked();
                }
            }
        }

        private void PumpLocked()
        {
            while (_activePhysicalCount < _maxConcurrentPhysical)
            {
                if (!TryDequeueNextPending(out BundleDownloadEnqueueOptions next))
                    break;

                string key = NormalizeRelativeKey(next.RemoteRelativePath);

                if (_inFlight.TryGetValue(key, out MergeGroup merged))
                {
                    AddWaiterLocked(key, merged, next);
                    continue;
                }

                var group = new MergeGroup { RelativePath = next.RemoteRelativePath };
                AddWaiterLocked(key, group, next);
                _inFlight[key] = group;
                _activePhysicalCount++;

                string relativePath = next.RemoteRelativePath;
                StartPhysicalDownload(key, group, relativePath);
            }
        }

        private void StartPhysicalDownload(string pKey, MergeGroup pGroup, string pRelativePath)
        {
            _transport.DownloadAsync(
                pRelativePath,
                pProgress =>
                {
                    // Progress fires many times per second; snapshot waiters into a pooled buffer
                    // (instead of a fresh List per tick) so OnProgress callbacks run outside the lock
                    // without piling up GC pressure during downloads.
                    int count;
                    BundleDownloadEnqueueOptions[] rented;
                    lock (_gate)
                    {
                        count = pGroup.Waiters.Count;
                        rented = ArrayPool<BundleDownloadEnqueueOptions>.Shared.Rent(count);
                        pGroup.Waiters.CopyTo(rented, 0);
                    }

                    try
                    {
                        for (int i = 0; i < count; i++)
                            rented[i].OnProgress?.Invoke(pProgress);
                    }
                    finally
                    {
                        ArrayPool<BundleDownloadEnqueueOptions>.Shared.Return(rented, clearArray: true);
                    }
                },
                pResult =>
                {
                    List<BundleDownloadEnqueueOptions> snapshot;
                    List<CancellationTokenRegistration> regsCopy;
                    lock (_gate)
                    {
                        if (!_inFlight.TryGetValue(pKey, out var g) || !ReferenceEquals(g, pGroup))
                            return;

                        snapshot = new List<BundleDownloadEnqueueOptions>(g.Waiters);
                        regsCopy = new List<CancellationTokenRegistration>(g.Registrations);
                        _inFlight.Remove(pKey);
                        _activeLogicalCount -= snapshot.Count;
                        _activePhysicalCount--;

                        long completedSlice = 0;
                        for (int i = 0; i < snapshot.Count; i++)
                            completedSlice += Math.Max(0, snapshot[i].SizeBytes);
                        if (completedSlice == 0 && pResult.Success && pResult.Data != null)
                            completedSlice = pResult.Data.Length;

                        _pendingEstimatedBytes = Math.Max(0, _pendingEstimatedBytes - completedSlice);
                        if (pResult.Success && pResult.Data != null)
                            _completedEstimatedBytes += pResult.Data.Length;
                        NotifyProgressListenersLocked();
                        PumpLocked();
                    }

                    for (int i = 0; i < regsCopy.Count; i++)
                    {
                        try
                        {
                            regsCopy[i].Dispose();
                        }
                        catch (ObjectDisposedException)
                        {
                            // ignored
                        }
                    }

                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        try
                        {
                            snapshot[i].OnComplete?.Invoke(pResult);
                        }
                        catch (Exception e)
                        {
                            HCLogger.LogError($"BundleDownloadQueue OnComplete threw: {e.Message}");
                        }
                    }
                },
                CancellationToken.None);
        }

        private bool TryDequeueNextPending(out BundleDownloadEnqueueOptions pOpt)
        {
            pOpt = DequeueNextNonCancelled(_pendingHigh);
            if (pOpt != null)
                return true;
            pOpt = DequeueNextNonCancelled(_pendingNormal);
            if (pOpt != null)
                return true;
            pOpt = DequeueNextNonCancelled(_pendingLow);
            return pOpt != null;
        }

        private BundleDownloadEnqueueOptions DequeueNextNonCancelled(Queue<BundleDownloadEnqueueOptions> pQueue)
        {
            while (pQueue.Count > 0)
            {
                BundleDownloadEnqueueOptions opt = pQueue.Dequeue();
                long slice = Math.Max(0, opt.SizeBytes);

                if (opt.CancellationToken.IsCancellationRequested)
                {
                    _pendingEstimatedBytes = Math.Max(0, _pendingEstimatedBytes - slice);
                    try
                    {
                        opt.OnComplete?.Invoke(FetchResult.CreateFailure(ErrorCode.OPERATION_CANCELLED, "Cancelled"));
                    }
                    catch (Exception e)
                    {
                        HCLogger.LogError($"BundleDownloadQueue OnComplete threw: {e.Message}");
                    }

                    NotifyProgressListenersLocked();
                    continue;
                }

                return opt;
            }

            return null;
        }

        private void NotifyProgressListenersLocked()
        {
            if (_listeners.Count == 0)
                return;

            int pendingLogical = _pendingHigh.Count + _pendingNormal.Count + _pendingLow.Count;

            var snapshot = new DownloadQueueProgressSnapshot
            {
                CompletedBytes = _completedEstimatedBytes,
                TotalEstimatedBytes = _completedEstimatedBytes + _pendingEstimatedBytes,
                ActiveLogicalCount = _activeLogicalCount,
                PendingLogicalCount = pendingLogical
            };

            for (int i = 0; i < _listeners.Count; i++)
            {
                try
                {
                    _listeners[i].OnDownloadQueueProgress(ref snapshot);
                }
                catch (Exception e)
                {
                    HCLogger.LogError($"DownloadQueueProgressListener threw: {e.Message}");
                }
            }
        }

        private static string NormalizeRelativeKey(string pPath)
        {
            return string.IsNullOrEmpty(pPath) ? string.Empty : pPath.Trim();
        }
    }
}
