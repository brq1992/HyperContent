using UnityEngine;

namespace com.igg.hypercontent.test
{
    /// <summary>
    /// Raw AssetBundle load test: compare full-path vs short-name loading.
    /// Attach to any GameObject in scene, enter Play mode, check Console.
    /// </summary>
    public class AssetBundlePathTest : MonoBehaviour
    {
        [Header("Bundle Settings")]
        [Tooltip("Absolute path to the .bundle file on disk")]
        public string bundlePath = @"C:\Users\ruqing.b\Downloads\misc_assets_all.bundle";

        [Header("Test Keys")]
        public string fullPath = "Assets/Addressables/Misc/Assembly_Hotfix.dll.bytes";
        public string shortName = "Assembly_Hotfix.dll";

        private AssetBundle _bundle;

        private void Start()
        {
            LoadBundleAndTest();
        }

        private void LoadBundleAndTest()
        {
            Debug.Log($"[ABPathTest] Loading bundle from: {bundlePath}");
            _bundle = AssetBundle.LoadFromFile(bundlePath);

            if (_bundle == null)
            {
                Debug.LogError($"[ABPathTest] Failed to load bundle from: {bundlePath}");
                return;
            }

            Debug.Log($"[ABPathTest] Bundle loaded: {_bundle.name}");

            string[] allAssets = _bundle.GetAllAssetNames();
            Debug.Log($"[ABPathTest] === All asset names in bundle ({allAssets.Length}) ===");
            for (int i = 0; i < allAssets.Length; i++)
            {
                Debug.Log($"[ABPathTest]   [{i}] {allAssets[i]}");
            }

            Debug.Log("[ABPathTest] ========================================");

            TestLoad("FullPath", fullPath);
            TestLoad("ShortName", shortName);

            string fileNameNoExt = System.IO.Path.GetFileNameWithoutExtension(fullPath);
            TestLoad("GetFileNameWithoutExtension", fileNameNoExt);

            string fileName = System.IO.Path.GetFileName(fullPath);
            TestLoad("GetFileName", fileName);

            string lowerFullPath = fullPath.ToLower();
            TestLoad("FullPath(lowercase)", lowerFullPath);
        }

        private void TestLoad(string label, string key)
        {
            var asset = _bundle.LoadAsset<TextAsset>(key);
            if (asset != null)
            {
                Debug.Log($"[ABPathTest] <color=green>SUCCESS</color> [{label}] key=\"{key}\" → asset.name={asset.name} bytes={asset.bytes.Length}");
            }
            else
            {
                Debug.LogWarning($"[ABPathTest] <color=red>FAILED</color>  [{label}] key=\"{key}\" → null");
            }
        }

        private void OnDestroy()
        {
            if (_bundle != null)
            {
                _bundle.Unload(true);
                Debug.Log("[ABPathTest] Bundle unloaded.");
            }
        }
    }
}
