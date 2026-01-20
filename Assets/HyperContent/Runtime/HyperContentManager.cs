using UnityEngine;
using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// Main entry point for HyperContent system
    /// Singleton manager that initializes all subsystems
    /// </summary>
    public class HyperContentManager : MonoBehaviour
    {
        private static HyperContentManager _instance;
        public static HyperContentManager Instance => _instance;
        
        private IResourceProvider _resourceProvider;
        private IContentCatalog _catalog;
        private IBundleStore _bundleStore;
        private IBundleTransport _bundleTransport;
        private IBundleLoader _bundleLoader;
        
        private bool _initialized = false;
        
        public static IResourceProvider ResourceProvider => _instance?._resourceProvider;
        
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }
        
        /// <summary>
        /// Initialize HyperContent system
        /// </summary>
        /// <param name="catalogSource">Catalog file path or name</param>
        /// <param name="cacheRoot">Cache root directory (null for default)</param>
        /// <returns>True if initialization succeeded</returns>
        public bool Initialize(string catalogSource, string cacheRoot = null)
        {
            if (_initialized)
            {
                Debug.LogWarning("[HyperContent] Already initialized");
                return true;
            }
            
            // Initialize catalog
            _catalog = new LocalContentCatalog();
            if (!_catalog.Initialize(catalogSource))
            {
                Debug.LogError("[HyperContent] Failed to initialize catalog");
                return false;
            }
            
            // Initialize bundle store
            _bundleStore = new LocalBundleStore();
            if (!_bundleStore.Initialize(cacheRoot))
            {
                Debug.LogError("[HyperContent] Failed to initialize bundle store");
                _catalog.Release();
                return false;
            }
            
            // Initialize bundle transport (POC: null for local-only)
            _bundleTransport = null;
            
            // Initialize bundle loader
            _bundleLoader = new UnityBundleLoader();
            
            // Initialize resource provider
            _resourceProvider = new ResourceProvider();
            if (!_resourceProvider.Initialize(_catalog, _bundleStore, _bundleTransport, _bundleLoader))
            {
                Debug.LogError("[HyperContent] Failed to initialize resource provider");
                Cleanup();
                return false;
            }
            
            _initialized = true;
            Debug.Log("[HyperContent] System initialized successfully");
            return true;
        }
        
        private void Cleanup()
        {
            _resourceProvider?.ReleaseAll();
            _catalog?.Release();
            _resourceProvider = null;
            _catalog = null;
            _bundleStore = null;
            _bundleTransport = null;
            _bundleLoader = null;
        }
        
        private void OnDestroy()
        {
            if (_instance == this)
            {
                Cleanup();
                _instance = null;
            }
        }
    }
}
