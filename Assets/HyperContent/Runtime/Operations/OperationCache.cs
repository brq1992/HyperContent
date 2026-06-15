using System;
using System.Collections.Generic;
using UnityEngine;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Global operation cache: GetOrCreate guarantees a single Operation per LocationHash.
    /// Release decrements RefCount and recursively releases dependencies at zero.
    /// See ARCHITECTURE.md section 5.3 and CONVENTIONS.md section 6.
    /// </summary>
    internal sealed class OperationCache
    {
        private readonly Dictionary<int, AsyncOperationBase> _cache = new Dictionary<int, AsyncOperationBase>();

        internal int Count => _cache.Count;

        internal AsyncOperationBase GetOrCreate(ResourceLocation location, Func<AsyncOperationBase> factory)
        {
            if (_cache.TryGetValue(location.LocationHash, out var existing))
            {
                AssertCacheHitIdentityMatch(existing, location);
                existing.RefCount++;
                HCLogger.LogVerbose($"[OperationCache] Hit [{LogFields.LOCATION_HASH}={location.LocationHash}] " +
                    $"[{LogFields.ADDRESS}={location.Address}] [{LogFields.REF_COUNT}={existing.RefCount}]");
                return existing;
            }
            var op = factory();
            op.RefCount = 1;
            op.LocationHash = location.LocationHash;
            _cache[location.LocationHash] = op;
            HCLogger.LogVerbose($"[OperationCache] Created [{LogFields.LOCATION_HASH}={location.LocationHash}] " +
                $"[{LogFields.ADDRESS}={location.Address}] cacheSize={_cache.Count}");
            return op;
        }

        /// <summary>
        /// Cache-hit fast path that avoids the closure + delegate alloc that <see cref="GetOrCreate"/>
        /// would otherwise pay (≈ 60–80 B per call when the lambda captures a generic type parameter).
        /// Returns true and bumps RefCount when the location is already cached; otherwise leaves the
        /// caller to fall through to <see cref="GetOrCreate"/>. Behavior on hit is identical to
        /// <see cref="GetOrCreate"/> — same identity assertion, same RefCount++, same verbose log —
        /// so callers can swap the two without observable side-effects beyond reduced GC pressure.
        /// </summary>
        internal bool TryGetExisting(ResourceLocation location, out AsyncOperationBase existing)
        {
            if (_cache.TryGetValue(location.LocationHash, out existing))
            {
                AssertCacheHitIdentityMatch(existing, location);
                existing.RefCount++;
                HCLogger.LogVerbose($"[OperationCache] Hit [{LogFields.LOCATION_HASH}={location.LocationHash}] " +
                    $"[{LogFields.ADDRESS}={location.Address}] [{LogFields.REF_COUNT}={existing.RefCount}]");
                return true;
            }
            return false;
        }

        internal void Release(AsyncOperationBase op)
        {
            op.RefCount--;
            HCLogger.LogVerbose($"[OperationCache] Release [{LogFields.LOCATION_HASH}={op.LocationHash}] " +
                $"[{LogFields.REF_COUNT}={op.RefCount}]");

            if (op.RefCount > 0) return;

            _cache.Remove(op.LocationHash);

            HCLogger.LogVerbose($"[OperationCache] Disposing [{LogFields.LOCATION_HASH}={op.LocationHash}] " +
                $"deps={op.DependencyCount} cacheSize={_cache.Count}");

            for (int i = 0; i < op.DependencyCount; i++)
                Release(op.Dependencies[i]);

            op.Dispose();
        }

        internal bool TryGet(int locationHash, out AsyncOperationBase op)
        {
            return _cache.TryGetValue(locationHash, out op);
        }

        /// <summary>
        /// Diagnostics-only snapshot of every cached operation, appended to <paramref name="pOut"/>
        /// without allocating. Iteration order matches the underlying dictionary (unspecified).
        /// Callers must not mutate the cache during iteration; for leak inspection this is fine
        /// because we only read from the main thread between frames.
        /// </summary>
        internal void Snapshot(List<AsyncOperationBase> pOut)
        {
            if (pOut == null) return;
            foreach (var kv in _cache)
                pOut.Add(kv.Value);
        }

        internal void Clear()
        {
            HCLogger.LogVerbose($"[OperationCache] Clear — disposing {_cache.Count} operations");
            foreach (var kvp in _cache)
                kvp.Value.Dispose();
            _cache.Clear();
        }

        /// <summary>
        /// Sanity check that runs only in dev builds: when two ResourceLocations land on the
        /// same LocationHash they MUST describe the same logical asset (address + internalId
        /// + providerId). Otherwise we'd silently return the cached op and the caller would
        /// receive the wrong asset (e.g. two prefabs named "Foo.prefab" living in different
        /// bundles). Catches both honest-to-god int-hash collisions and any future regression
        /// that drops a discriminator from ResourceLocation.ComputeHash.
        /// </summary>
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        private static void AssertCacheHitIdentityMatch(AsyncOperationBase existing, ResourceLocation incoming)
        {
            var cached = existing?.Location;
            if (cached == null) return;

            bool match =
                string.Equals(cached.Address, incoming.Address, StringComparison.Ordinal) &&
                string.Equals(cached.InternalId, incoming.InternalId, StringComparison.Ordinal) &&
                string.Equals(cached.ProviderId, incoming.ProviderId, StringComparison.Ordinal);

            Debug.Assert(match,
                $"[OperationCache] LocationHash collision! hash={incoming.LocationHash} " +
                $"cached=(address='{cached.Address}', internalId='{cached.InternalId}', provider='{cached.ProviderId}') " +
                $"incoming=(address='{incoming.Address}', internalId='{incoming.InternalId}', provider='{incoming.ProviderId}'). " +
                "Returning the cached op would deliver the wrong asset.");
        }
    }
}
