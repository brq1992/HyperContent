using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Networking;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Unity AssetBundle loader implementation.
    /// Primary path uses <see cref="AssetBundle.LoadFromFileAsync"/>.
    /// On Android StreamingAssets, if that returns null, falls back to
    /// <see cref="UnityWebRequestAssetBundle.GetAssetBundle"/> so the bundle can still load.
    /// </summary>
    public class UnityBundleLoader : IBundleLoader
    {
        private readonly Dictionary<string, AssetBundle> _loadedBundles = new Dictionary<string, AssetBundle>();

        // Concurrent-load coalescing: keyed by bundleName.
        // Without this, two callers requesting the same bundle in the same frame can both
        // observe IsLoaded == false (the load is in-flight, not yet registered in _loadedBundles)
        // and both fire AssetBundle.LoadFromFileAsync against the same (path, offset). Unity
        // then trips its dedup check and prints
        //   "The AssetBundle '<filename>' can't be loaded because another AssetBundle with the
        //    same files is already loaded."
        // which is especially likely with PAD InstallTime where every bundle shares
        // path = base.apk and only differs by offset.
        private readonly Dictionary<string, InFlightLoad> _inFlight = new Dictionary<string, InFlightLoad>();

        private sealed class InFlightLoad
        {
            public string FilePath;
            public ulong Offset;
            public List<Action<AssetBundle>> Waiters;
        }

        public void LoadFromFileAsync(string bundleName, string filePath, Action<AssetBundle> onComplete)
        {
            LoadFromFileAsync(bundleName, filePath, 0, onComplete);
        }

        public void LoadFromFileAsync(string bundleName, string filePath, ulong offset, Action<AssetBundle> onComplete)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                onComplete?.Invoke(null);
                return;
            }

            if (_loadedBundles.TryGetValue(bundleName, out var alreadyLoaded))
            {
                onComplete?.Invoke(alreadyLoaded);
                return;
            }

            if (_inFlight.TryGetValue(bundleName, out var inflight))
            {
                if (!string.Equals(inflight.FilePath, filePath, StringComparison.Ordinal) || inflight.Offset != offset)
                {
                    HCLogger.LogWarn($"[UnityBundleLoader] Concurrent load for bundle={bundleName} with mismatched location: " +
                        $"in-flight=({inflight.FilePath}, {inflight.Offset}) new=({filePath}, {offset}). Coalescing onto in-flight request.");
                }
                inflight.Waiters.Add(onComplete);
                return;
            }

            if (offset == 0 && !HyperContentPaths.FileExistsOrIsStreamingAssets(filePath))
            {
                HCLogger.LogWarn($"[UnityBundleLoader] File not found: {filePath}");
                onComplete?.Invoke(null);
                return;
            }

            var entry = new InFlightLoad
            {
                FilePath = filePath,
                Offset = offset,
                Waiters = new List<Action<AssetBundle>> { onComplete },
            };
            _inFlight[bundleName] = entry;

            var sw = Stopwatch.StartNew();
            HCLogger.LogVerbose($"[HC.LoadFromFileAsync] bundle={bundleName} path={filePath} offset={offset}");
            AssetBundleCreateRequest request;
            try
            {
                request = offset == 0
                    ? AssetBundle.LoadFromFileAsync(filePath, 0)
                    : AssetBundle.LoadFromFileAsync(filePath, 0, offset);
            }
            catch (Exception e)
            {
                sw.Stop();
                HCLogger.LogError($"Failed to load bundle {bundleName}: {e.Message}");
                CompleteInFlight(bundleName, null);
                return;
            }

            request.completed += _ =>
            {
                sw.Stop();
                var bundle = request.assetBundle;

#if UNITY_ANDROID && !UNITY_EDITOR
                if (bundle == null && HyperContentPaths.IsAndroidStreamingAssetsPath(filePath) && offset == 0)
                {
                    HCLogger.LogError($"[UnityBundleLoader] LoadFromFileAsync FAILED for StreamingAssets bundle, falling back to WebRequest — bundle={bundleName} path={filePath} elapsed={sw.ElapsedMilliseconds}ms");
                    var swFallback = Stopwatch.StartNew();
                    LoadViaWebRequest(bundleName, filePath, fallbackBundle =>
                    {
                        swFallback.Stop();
                        HCLogger.LogVerbose($"[HC.LoadViaWebRequest.Done] bundle={bundleName} elapsed={swFallback.ElapsedMilliseconds}ms success={fallbackBundle != null}");
                        CompleteInFlight(bundleName, fallbackBundle);
                    });
                    return;
                }
#endif

                HCLogger.LogVerbose($"[HC.LoadFromFileAsync.Done] bundle={bundleName} elapsed={sw.ElapsedMilliseconds}ms success={bundle != null}");
                CompleteInFlight(bundleName, bundle);
            };
        }

        private void CompleteInFlight(string bundleName, AssetBundle bundle)
        {
            if (!_inFlight.TryGetValue(bundleName, out var entry))
            {
                if (bundle != null && !_loadedBundles.ContainsKey(bundleName))
                    _loadedBundles[bundleName] = bundle;
                return;
            }

            _inFlight.Remove(bundleName);
            if (bundle != null)
                _loadedBundles[bundleName] = bundle;

            var waiters = entry.Waiters;
            for (int i = 0; i < waiters.Count; i++)
            {
                try { waiters[i]?.Invoke(bundle); }
                catch (Exception e) { HCLogger.LogError($"[UnityBundleLoader] Waiter callback threw for bundle={bundleName}: {e}"); }
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // Caller is the LoadFromFileAsync completion handler, which still owns the in-flight
        // entry and will route the result through CompleteInFlight. Don't touch _loadedBundles
        // here — that would double-register and bypass the waiter fan-out.
        private void LoadViaWebRequest(string bundleName, string pPath, Action<AssetBundle> onComplete)
        {
            var request = UnityWebRequestAssetBundle.GetAssetBundle(pPath);
            var op = request.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        var bundle = DownloadHandlerAssetBundle.GetContent(request);
                        if (bundle == null)
                            HCLogger.LogWarn($"[UnityBundleLoader] GetContent returned null: {pPath}");
                        onComplete?.Invoke(bundle);
                    }
                    else
                    {
                        HCLogger.LogWarn($"[UnityBundleLoader] WebRequest failed: {request.error} path={pPath}");
                        onComplete?.Invoke(null);
                    }
                }
                finally
                {
                    request.Dispose();
                }
            };
        }
#endif

        public void LoadFromMemoryAsync(string bundleName, byte[] data, Action<AssetBundle> onComplete)
        {
            if (data == null || data.Length == 0)
            {
                onComplete?.Invoke(null);
                return;
            }

            if (_loadedBundles.TryGetValue(bundleName, out var alreadyLoaded))
            {
                onComplete?.Invoke(alreadyLoaded);
                return;
            }

            try
            {
                var bundle = AssetBundle.LoadFromMemory(data);
                if (bundle != null)
                {
                    _loadedBundles[bundleName] = bundle;
                }
                onComplete?.Invoke(bundle);
            }
            catch (Exception e)
            {
                HCLogger.LogError($"Failed to load bundle from memory {bundleName}: {e.Message}");
                onComplete?.Invoke(null);
            }
        }

        public void Unload(string bundleName, bool unloadAllLoadedObjects = false)
        {
            if (_loadedBundles.TryGetValue(bundleName, out var bundle))
            {
                bundle.Unload(unloadAllLoadedObjects);
                _loadedBundles.Remove(bundleName);
                return;
            }

            // Unloading while a load is still in flight is a Provider-level lifecycle bug
            // (Release was called before the corresponding Provide finished). We can't
            // safely cancel the AssetBundleCreateRequest, so log and let the load complete
            // — CompleteInFlight will register it; the caller is responsible for calling
            // Unload again afterwards if they truly want it gone.
            if (_inFlight.ContainsKey(bundleName))
            {
                HCLogger.LogWarn($"[UnityBundleLoader] Unload called while load is in-flight, " +
                    $"bundle={bundleName}. The bundle will finish loading; caller should release it again.");
            }
        }

        public bool IsLoaded(string bundleName)
        {
            return _loadedBundles.ContainsKey(bundleName);
        }

        public bool TryGetBundle(string bundleName, out AssetBundle bundle)
        {
            return _loadedBundles.TryGetValue(bundleName, out bundle);
        }

        public void GetLoadedBundleNames(List<string> pOutNames)
        {
            if (pOutNames == null) return;
            foreach (var kv in _loadedBundles)
                pOutNames.Add(kv.Key);
        }
    }
}
