using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Loads EditorCatalog.json once and caches the registered GUID set so that
    /// AssetReferenceDrawer can validate drag-and-drop assignments without rebuilding.
    ///
    /// The cache is invalidated automatically when the catalog file on disk changes
    /// (detected via file-write timestamp). A manual refresh is also available via
    /// HyperContent/Refresh Asset Reference Validator menu item.
    /// </summary>
    [InitializeOnLoad]
    public static class AssetReferenceCatalogValidator
    {
        private static HashSet<string> _registeredGuidSet;
        private static long _catalogLastWriteUtc = -1;

        static AssetReferenceCatalogValidator()
        {
            // Reload cache when the catalog asset changes inside the project.
            AssetDatabase.importPackageCompleted += _ => Invalidate();
        }

        [MenuItem("HyperContent/Refresh Asset Reference Validator")]
        public static void MenuRefresh()
        {
            Invalidate();
            EnsureLoaded();
            int count = _registeredGuidSet?.Count ?? 0;
            Debug.Log($"[HyperContent] AssetReference validator refreshed — {count} registered assets.");
        }

        /// <summary>
        /// Returns true when pGuid is registered in EditorCatalog.json.
        /// Automatically reloads the catalog if the file has been updated on disk.
        /// </summary>
        public static bool IsRegistered(string pGuid)
        {
            if (string.IsNullOrEmpty(pGuid))
                return false;

            EnsureLoaded();
            return _registeredGuidSet != null && _registeredGuidSet.Contains(pGuid.ToLowerInvariant());
        }

        /// <summary>
        /// Returns true when the EditorCatalog file exists on disk.
        /// </summary>
        public static bool CatalogExists()
        {
            return File.Exists(CatalogFilePath);
        }

        private static string CatalogFilePath =>
            Path.Combine(simulation.EditorCatalogGenerator.CATALOG_DIR,
                         simulation.EditorCatalogGenerator.CATALOG_FILENAME);

        // ── Internal helpers ──────────────────────────────────────────────

        private static void Invalidate()
        {
            _registeredGuidSet = null;
            _catalogLastWriteUtc = -1;
        }

        private static void EnsureLoaded()
        {
            string path = CatalogFilePath;
            if (!File.Exists(path))
            {
                _registeredGuidSet = null;
                return;
            }

            long writeTime = File.GetLastWriteTimeUtc(path).Ticks;
            if (_registeredGuidSet != null && writeTime == _catalogLastWriteUtc)
                return; // cache is still valid

            try
            {
                string json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<EditorCatalogData>(json);

                _registeredGuidSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (data?.entries != null)
                {
                    foreach (var entry in data.entries)
                    {
                        if (!string.IsNullOrEmpty(entry.guid))
                            _registeredGuidSet.Add(entry.guid);
                    }
                }

                _catalogLastWriteUtc = writeTime;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HyperContent] Failed to load EditorCatalog for validation: {e.Message}");
                _registeredGuidSet = null;
            }
        }

        // Minimal deserialization types mirroring EditorCatalogGenerator's internal schema.
        [Serializable]
        private class EditorCatalogData
        {
            public long timestamp;
            public List<EditorCatalogEntry> entries;
        }

        [Serializable]
        private class EditorCatalogEntry
        {
            public string key;
            public string guid;
            public string assetPath;
        }
    }
}
