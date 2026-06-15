using UnityEngine;

namespace com.igg.hypercontent.shared
{
    /// <summary>
    /// Lightweight reader for the editorPlayMode field stored in HyperContentBuildConfig.json.
    /// Lives in Shared assembly so both Runtime and Editor can access it.
    /// 0 = UseAssetDatabase, 1 = UseExistingAssetBundle.
    /// </summary>
    public static class PlayModeSettings
    {
        public const int MODE_USE_ASSET_DATABASE = 0;
        public const int MODE_USE_EXISTING_BUNDLE = 1;

        private const string CONFIG_PATH = "ProjectSettings/HyperContentBuildConfig.json";

        private static int? _cachedMode;

        /// <summary>
        /// Returns the current editor play mode value (0 or 1).
        /// Result is cached for the lifetime of the domain.
        /// </summary>
        public static int GetEditorPlayMode()
        {
            if (_cachedMode.HasValue)
                return _cachedMode.Value;

            _cachedMode = ReadFromConfig();
            return _cachedMode.Value;
        }

        /// <summary>
        /// Force re-read from disk (call after the user changes the setting).
        /// </summary>
        public static void InvalidateCache()
        {
            _cachedMode = null;
        }

        public static bool IsAssetDatabaseMode()
        {
#if UNITY_EDITOR
            return GetEditorPlayMode() == MODE_USE_ASSET_DATABASE;
#else
            return false;
#endif
        }

        private static int ReadFromConfig()
        {
#if UNITY_EDITOR
            try
            {
                if (System.IO.File.Exists(CONFIG_PATH))
                {
                    string json = System.IO.File.ReadAllText(CONFIG_PATH);
                    var wrapper = JsonUtility.FromJson<PlayModeWrapper>(json);
                    if (wrapper != null)
                        return wrapper.editorPlayMode;
                }
            }
            catch (System.Exception e)
            {
                HCLogger.LogWarn($"Failed to read play mode from config: {e.Message}");
            }
#endif
            return MODE_USE_ASSET_DATABASE;
        }

        [System.Serializable]
        private class PlayModeWrapper
        {
            public int editorPlayMode = MODE_USE_ASSET_DATABASE;
        }
    }
}
