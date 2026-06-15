using System.Collections.Generic;
using UnityEngine;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Tracks instantiated GameObject -> source Operation mapping.
    /// InstantiateAsync increments RefCount via Track; ReleaseInstance decrements via Release.
    /// See ARCHITECTURE.md section 7.
    /// </summary>
    internal sealed class InstanceRegistry
    {
        internal struct Entry
        {
            public string InstanceName;
            public AsyncOperationBase Operation;
        }

        // Stores instance name (snapshotted at Track time) alongside the source op so
        // diagnostics dumps can name the leaking instance even if the GameObject has
        // been Destroyed by an external code path. We deliberately do NOT keep a
        // GameObject reference here so a forgotten ReleaseInstance does not also
        // pin the destroyed Unity object's managed wrapper.
        private readonly Dictionary<int, Entry> _map = new Dictionary<int, Entry>();

        internal int Count => _map.Count;

        internal void Track(GameObject instance, AsyncOperationBase op)
        {
            int id = instance.GetInstanceID();
            _map[id] = new Entry { InstanceName = instance.name, Operation = op };
            op.RefCount++;
            HCLogger.LogVerbose($"[InstanceRegistry] Track name={instance.name} id={id} " +
                $"[{LogFields.LOCATION_HASH}={op.LocationHash}] [{LogFields.REF_COUNT}={op.RefCount}] " +
                $"tracked={_map.Count}");
        }

        internal void Release(GameObject instance, ResourceManager manager)
        {
            int id = instance.GetInstanceID();
            if (!_map.TryGetValue(id, out var entry))
            {
                HCLogger.LogWarn($"ReleaseInstance: instance {instance.name} (id={id}) not tracked.");
                return;
            }
            _map.Remove(id);
            HCLogger.LogVerbose($"[InstanceRegistry] Release name={instance.name} id={id} " +
                $"[{LogFields.LOCATION_HASH}={entry.Operation.LocationHash}] tracked={_map.Count}");
            Object.Destroy(instance);
            manager.Release(entry.Operation);
        }

        internal void Clear()
        {
            HCLogger.LogVerbose($"[InstanceRegistry] Clear — removing {_map.Count} tracked instances");
            _map.Clear();
        }

        /// <summary>
        /// Diagnostics-only snapshot. Appends (instanceId, instanceName, op) tuples without
        /// allocating beyond the caller-supplied lists. Used by HyperContentDiagnostics to
        /// identify which Instantiate calls are still holding RefCount.
        /// </summary>
        internal void Snapshot(List<int> pInstanceIds, List<string> pInstanceNames, List<AsyncOperationBase> pOps)
        {
            if (pInstanceIds == null || pInstanceNames == null || pOps == null) return;
            foreach (var kv in _map)
            {
                pInstanceIds.Add(kv.Key);
                pInstanceNames.Add(kv.Value.InstanceName);
                pOps.Add(kv.Value.Operation);
            }
        }
    }
}
