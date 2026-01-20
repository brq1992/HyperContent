using System;
using System.Collections.Generic;
using UnityEngine;

namespace HyperContent
{
    /// <summary>
    /// Environment profile for CDN configuration (dev/staging/prod)
    /// </summary>
    [Serializable]
    public class EnvironmentProfile
    {
        /// <summary>
        /// Environment type
        /// </summary>
        public enum EnvironmentType
        {
            Development,
            Staging,
            Production
        }
        
        public EnvironmentType Type { get; set; }
        public string BaseUrl { get; set; }
        public string CatalogUrl { get; set; }
        public int TimeoutSeconds { get; set; } = 30;
        public int MaxRetries { get; set; } = 3;
        public int MaxConcurrentDownloads { get; set; } = 4;
        
        /// <summary>
        /// Create default profiles
        /// </summary>
        public static Dictionary<EnvironmentType, EnvironmentProfile> CreateDefaultProfiles()
        {
            var profiles = new Dictionary<EnvironmentType, EnvironmentProfile>();
            
            profiles[EnvironmentType.Development] = new EnvironmentProfile
            {
                Type = EnvironmentType.Development,
                BaseUrl = "http://localhost:8080/content/",
                CatalogUrl = "http://localhost:8080/content/catalog.catalog.json",
                TimeoutSeconds = 30,
                MaxRetries = 2,
                MaxConcurrentDownloads = 2
            };
            
            profiles[EnvironmentType.Staging] = new EnvironmentProfile
            {
                Type = EnvironmentType.Staging,
                BaseUrl = "https://staging-cdn.example.com/content/",
                CatalogUrl = "https://staging-cdn.example.com/content/catalog.catalog.json",
                TimeoutSeconds = 30,
                MaxRetries = 3,
                MaxConcurrentDownloads = 4
            };
            
            profiles[EnvironmentType.Production] = new EnvironmentProfile
            {
                Type = EnvironmentType.Production,
                BaseUrl = "https://cdn.example.com/content/",
                CatalogUrl = "https://cdn.example.com/content/catalog.catalog.json",
                TimeoutSeconds = 30,
                MaxRetries = 3,
                MaxConcurrentDownloads = 6
            };
            
            return profiles;
        }
    }
    
    /// <summary>
    /// Environment profile manager
    /// </summary>
    public class EnvironmentProfileManager
    {
        private static EnvironmentProfileManager _instance;
        public static EnvironmentProfileManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new EnvironmentProfileManager();
                }
                return _instance;
            }
        }
        
        private Dictionary<EnvironmentProfile.EnvironmentType, EnvironmentProfile> _profiles;
        private EnvironmentProfile.EnvironmentType _currentEnvironment;
        
        private EnvironmentProfileManager()
        {
            _profiles = EnvironmentProfile.CreateDefaultProfiles();
            _currentEnvironment = EnvironmentProfile.EnvironmentType.Production; // Default to production
        }
        
        /// <summary>
        /// Set current environment
        /// </summary>
        public void SetEnvironment(EnvironmentProfile.EnvironmentType environment)
        {
            if (_profiles.ContainsKey(environment))
            {
                _currentEnvironment = environment;
                Debug.Log($"[HyperContent] Environment set to: {environment}");
            }
            else
            {
                Debug.LogError($"[HyperContent] Environment profile not found: {environment}");
            }
        }
        
        /// <summary>
        /// Get current environment profile
        /// </summary>
        public EnvironmentProfile GetCurrentProfile()
        {
            return _profiles.TryGetValue(_currentEnvironment, out var profile) ? profile : null;
        }
        
        /// <summary>
        /// Get profile for specific environment
        /// </summary>
        public EnvironmentProfile GetProfile(EnvironmentProfile.EnvironmentType environment)
        {
            return _profiles.TryGetValue(environment, out var profile) ? profile : null;
        }
        
        /// <summary>
        /// Register or update a profile
        /// </summary>
        public void RegisterProfile(EnvironmentProfile profile)
        {
            if (profile != null)
            {
                _profiles[profile.Type] = profile;
                Debug.Log($"[HyperContent] Profile registered: {profile.Type}");
            }
        }
        
        /// <summary>
        /// Load profiles from JSON (for runtime configuration)
        /// </summary>
        public void LoadProfilesFromJson(string json)
        {
            try
            {
                // Simple JSON parsing - in production, use proper JSON library
                // This is a simplified version
                Debug.LogWarning("[HyperContent] LoadProfilesFromJson not fully implemented, using defaults");
            }
            catch (Exception e)
            {
                Debug.LogError($"[HyperContent] Failed to load profiles from JSON: {e.Message}");
            }
        }
        
        /// <summary>
        /// Get base URL for current environment
        /// </summary>
        public string GetBaseUrl()
        {
            var profile = GetCurrentProfile();
            return profile?.BaseUrl ?? "";
        }
        
        /// <summary>
        /// Get catalog URL for current environment
        /// </summary>
        public string GetCatalogUrl()
        {
            var profile = GetCurrentProfile();
            return profile?.CatalogUrl ?? "";
        }
    }
}
