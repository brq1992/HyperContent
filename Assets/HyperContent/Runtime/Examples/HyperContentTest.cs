using UnityEngine;
using HyperContent;

namespace HyperContent.Examples
{
    /// <summary>
    /// POC测试脚本 - 演示最小可用链路
    /// </summary>
    public class HyperContentTest : MonoBehaviour
    {
        [Header("Catalog Settings")]
        [Tooltip("Catalog文件名（放在StreamingAssets目录）")]
        public string catalogFileName = "test.catalog.json";
        
        [Header("Test Asset")]
        [Tooltip("要加载的资产键")]
        public string testAssetKey = "test_sprite";
        
        private void Start()
        {
            // 确保HyperContentManager存在
            if (HyperContentManager.Instance == null)
            {
                var managerObj = new GameObject("HyperContentManager");
                managerObj.AddComponent<HyperContentManager>();
            }
            
            // 初始化HyperContent系统
            if (HyperContentManager.Instance.Initialize(catalogFileName))
            {
                Debug.Log($"[HyperContentTest] System initialized with catalog: {catalogFileName}");
                
                // 测试加载资源
                TestLoadAsset();
            }
            else
            {
                Debug.LogError($"[HyperContentTest] Failed to initialize with catalog: {catalogFileName}");
            }
        }
        
        private void TestLoadAsset()
        {
            if (string.IsNullOrEmpty(testAssetKey))
            {
                Debug.LogWarning("[HyperContentTest] Test asset key is empty");
                return;
            }
            
            var provider = HyperContentManager.ResourceProvider;
            if (provider == null)
            {
                Debug.LogError("[HyperContentTest] ResourceProvider is null");
                return;
            }
            
            // 尝试加载Sprite
            var sprite = provider.LoadAsset<Sprite>(testAssetKey);
            if (sprite != null)
            {
                Debug.Log($"[HyperContentTest] Successfully loaded asset: {testAssetKey}");
            }
            else
            {
                // 尝试加载其他类型
                var texture = provider.LoadAsset<Texture2D>(testAssetKey);
                if (texture != null)
                {
                    Debug.Log($"[HyperContentTest] Successfully loaded texture: {testAssetKey}");
                }
                else
                {
                    Debug.LogWarning($"[HyperContentTest] Failed to load asset: {testAssetKey}");
                }
            }
        }
        
        private void OnDestroy()
        {
            // 清理资源
            HyperContentManager.ResourceProvider?.ReleaseAll();
        }
    }
}
