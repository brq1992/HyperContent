using UnityEngine;
using com.igg.hypercontent;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// POC test script — demonstrates the static facade API with ContentHandle.
    /// Usage: add to a scene; on Start calls <see cref="HyperContent.InitializeAsync"/> (same as production).
    /// Editor: use HyperContent build output + bundle play mode, or Asset Database play mode (HyperContent window, Overview tab).
    /// </summary>
    public class HyperContentTest : MonoBehaviour
    {
        [Header("Test Asset")]
        [Tooltip("Asset address to load")]
        public string testAssetAddress = "test_sprite";

        [Tooltip("When enabled, logs the wall-clock time between LoadAsync invocation and Completed callback " +
                 "(useful for before/after comparison of catalog/provider optimizations).")]
        public bool logLoadLatency = true;

        private ContentHandle<Texture2D> _activeHandle;
        private float _loadStartTime;

        private void Start()
        {
            HyperContent.OnLoadSucceeded += addr =>
                HCLogger.LogInfo($"[Test] Load succeeded: {addr}");

            HyperContent.OnLoadFailed += (addr, ex) =>
                HCLogger.LogError($"[Test] Load failed: {addr}, {ex?.Message}");

            if (HyperContent.IsInitialized)
            {
                HCLogger.LogInfo("[Test] HyperContent already initialized");
                TestLoadAsset();
                return;
            }

            HyperContent.Initialize(ok =>
            {
                if (!ok)
                {
                    HCLogger.LogError(
                        "[Test] HyperContent.Initialize failed. " +
                        "Use Editor play mode + HyperContent build (hc/settings.json + catalog) or Asset Database mode.");
                    return;
                }

                HCLogger.LogInfo("[Test] HyperContent initialized");
                TestLoadAsset();
            });
        }

        private void TestLoadAsset()
        {
            if (string.IsNullOrEmpty(testAssetAddress))
            {
                HCLogger.LogWarn("[Test] Test asset address is empty");
                return;
            }

            _loadStartTime = Time.realtimeSinceStartup;
            _activeHandle = HyperContent.LoadAsync<Texture2D>(testAssetAddress);
            if (!_activeHandle.IsValid)
            {
                HCLogger.LogError("[Test] LoadAsync returned invalid handle — check catalog");
                return;
            }

            _activeHandle.Completed += h =>
            {
                if (logLoadLatency)
                {
                    float elapsedMs = (Time.realtimeSinceStartup - _loadStartTime) * 1000f;
                    HCLogger.LogInfo($"[Test] LoadAsync → Completed elapsed={elapsedMs:F2} ms ({testAssetAddress})");
                }

                if (h.IsSuccess)
                {
                    var tex = h.Result;
                    HCLogger.LogInfo($"[Test] Loaded texture: {tex?.name} ({tex?.width}x{tex?.height})");
                }
                else
                {
                    HCLogger.LogError($"[Test] Load failed: {h.Error}");
                }
            };
        }

        private void OnDestroy()
        {
            if (_activeHandle.IsValid)
            {
                HyperContent.Release(_activeHandle);
                _activeHandle = default;
            }
        }
    }
}
