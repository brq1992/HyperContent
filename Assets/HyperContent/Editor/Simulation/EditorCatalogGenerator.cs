using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.editor.simulation
{
    /// <summary>
    /// Generates an Editor-only catalog for UseAssetDatabase play mode.
    /// Reuses the real build pipeline (grouping tool → plan) to collect all assets
    /// and their addresses, then writes a lightweight JSON catalog that maps
    /// address/key → full asset path. No bundles are built.
    ///
    /// Output: Assets/HyperContent/Editor/Catalog/EditorCatalog.json
    /// </summary>
    public static class EditorCatalogGenerator
    {
        internal const string CATALOG_DIR = "Assets/HyperContent/Editor/Catalog";
        internal const string CATALOG_FILENAME = "EditorCatalog.json";

        public static string CatalogPath => Path.Combine(CATALOG_DIR, CATALOG_FILENAME);

        /// <summary>
        /// Generate the Editor catalog by running the grouping tool pipeline
        /// (collect assets, analyze deps, assign bundles) without actually building bundles.
        /// </summary>
        public static bool Generate(BuildConfig config)
        {
            try
            {
                Debug.Log("[HyperContent] Generating Editor catalog...");

                var groupingTool = BuildToolFactory.GetGroupingTool(config.groupingToolId);
                var plan = groupingTool.GeneratePlan(config);

                if (plan.Errors.Count > 0)
                {
                    foreach (var err in plan.Errors)
                        Debug.LogError($"[HyperContent] EditorCatalog error: {err.Message}");
                    return false;
                }

                var catalog = BuildEditorCatalog(plan);

                if (!Directory.Exists(CATALOG_DIR))
                    Directory.CreateDirectory(CATALOG_DIR);

                string json = JsonUtility.ToJson(catalog, true);
                File.WriteAllText(CatalogPath, json);

                AssetDatabase.Refresh();

                Debug.Log($"[HyperContent] Editor catalog generated: {CatalogPath} — " +
                    $"{catalog.entries.Count} entries");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[HyperContent] Editor catalog generation failed: {e}");
                return false;
            }
        }

        private static EditorCatalogData BuildEditorCatalog(BuildPlan plan)
        {
            var catalog = new EditorCatalogData
            {
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            foreach (var kvp in plan.KeyToGuid)
            {
                string key = kvp.Key;
                string guid = kvp.Value;

                if (!plan.GuidToPath.TryGetValue(guid, out string assetPath))
                    continue;

                catalog.entries.Add(new EditorCatalogEntry
                {
                    key = key,
                    guid = guid.ToLowerInvariant(),
                    assetPath = assetPath
                });
            }

            catalog.entries.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
            return catalog;
        }

        public static bool CatalogExists()
        {
            return File.Exists(CatalogPath);
        }
    }

    /// <summary>
    /// Lightweight Editor-only catalog schema.
    /// Each entry maps key + guid → full asset path for AssetDatabase loading.
    /// </summary>
    [Serializable]
    internal class EditorCatalogData
    {
        public long timestamp;
        public List<EditorCatalogEntry> entries = new List<EditorCatalogEntry>();
    }

    [Serializable]
    internal class EditorCatalogEntry
    {
        public string key;
        public string guid;
        public string assetPath;
    }
}
